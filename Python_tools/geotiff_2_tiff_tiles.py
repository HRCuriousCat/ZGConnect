import os
import json
import math
import numpy as np
import rasterio
from rasterio.windows import from_bounds
from rasterio.enums import Resampling
from rasterio.transform import from_bounds as transform_from_bounds


# ============================================================
# CONFIG
# ============================================================

INPUT_TIF = r"D:\_unity projects\ZG Connect\_resources\dmr height\test_1.tif"

OUTPUT_DIR = r"D:\_unity projects\ZG Connect\_resources\dmr height\tiles_tif_1km_1025"

TILE_SIZE_METERS = 1000
OUTPUT_RESOLUTION = 1025

# Ako je više od 95% tilea NoData, preskoči ga
EMPTY_INVALID_RATIO_THRESHOLD = 0.95

# Tvoj NoData je 3.4e+38, zato ovo hvata sulude vrijednosti
NODATA_THRESHOLD = 1e20

os.makedirs(OUTPUT_DIR, exist_ok=True)


# ============================================================
# HELPERS
# ============================================================

def align_down(value, step):
    return math.floor(value / step) * step


def align_up(value, step):
    return math.ceil(value / step) * step


def is_tile_empty(data, nodata):
    invalid = ~np.isfinite(data)

    if nodata is not None:
        invalid |= data == nodata

    invalid |= data > NODATA_THRESHOLD

    invalid_ratio = np.count_nonzero(invalid) / data.size

    return invalid_ratio >= EMPTY_INVALID_RATIO_THRESHOLD, invalid_ratio


# ============================================================
# MAIN
# ============================================================

metadata = []

with rasterio.open(INPUT_TIF) as src:
    print("Input:", INPUT_TIF)
    print("CRS:", src.crs)
    print("Bounds:", src.bounds)
    print("Resolution:", src.res)
    print("NoData:", src.nodata)
    print()

    bounds = src.bounds

    x_min = bounds.left
    x_max = bounds.right
    y_min = bounds.bottom
    y_max = bounds.top

    x_start = align_down(x_min, TILE_SIZE_METERS)
    x_end = align_up(x_max, TILE_SIZE_METERS)

    y_start = align_up(y_max, TILE_SIZE_METERS)
    y_end = align_down(y_min, TILE_SIZE_METERS)

    print(f"Aligned grid:")
    print(f"X: {x_start} → {x_end}")
    print(f"Y: {y_start} → {y_end}")
    print()

    total_candidates = 0
    exported_count = 0
    skipped_empty_count = 0

    for x_left in range(int(x_start), int(x_end), TILE_SIZE_METERS):
        for y_top in range(int(y_start), int(y_end), -TILE_SIZE_METERS):

            total_candidates += 1

            x_right = x_left + TILE_SIZE_METERS
            y_bottom = y_top - TILE_SIZE_METERS

            tile_name = f"zg_dmr_{x_left}_{y_bottom}_1km_{OUTPUT_RESOLUTION}.tif"
            tile_path = os.path.join(OUTPUT_DIR, tile_name)

            print(f"Processing {tile_name}")

            # Window po stvarnim koordinatama
            window = from_bounds(
                x_left,
                y_bottom,
                x_right,
                y_top,
                transform=src.transform
            )

            # Čitanje + resampling na 1025x1025
            data = src.read(
                1,
                window=window,
                out_shape=(OUTPUT_RESOLUTION, OUTPUT_RESOLUTION),
                resampling=Resampling.bilinear,
                boundless=True,
                fill_value=src.nodata
            ).astype(np.float32)

            empty, invalid_ratio = is_tile_empty(data, src.nodata)

            if empty:
                print(f"  SKIP empty / mostly empty | invalid: {invalid_ratio:.2%}")
                skipped_empty_count += 1
                continue

            # Novi transform za točan 1x1km tile
            tile_transform = transform_from_bounds(
                x_left,
                y_bottom,
                x_right,
                y_top,
                OUTPUT_RESOLUTION,
                OUTPUT_RESOLUTION
            )

            profile = src.profile.copy()
            profile.update({
                "driver": "GTiff",
                "height": OUTPUT_RESOLUTION,
                "width": OUTPUT_RESOLUTION,
                "transform": tile_transform,
                "compress": "lzw",
                "tiled": True,
                "blockxsize": 256,
                "blockysize": 256,
                "nodata": src.nodata,
                "dtype": "float32"
            })

            with rasterio.open(tile_path, "w", **profile) as dst:
                dst.write(data, 1)

            valid = data[
                np.isfinite(data) &
                (data < NODATA_THRESHOLD)
            ]

            if src.nodata is not None:
                valid = valid[valid != src.nodata]

            tile_min = float(np.min(valid)) if valid.size > 0 else None
            tile_max = float(np.max(valid)) if valid.size > 0 else None
            tile_mean = float(np.mean(valid)) if valid.size > 0 else None

            metadata.append({
                "file": tile_name,
                "left": x_left,
                "right": x_right,
                "bottom": y_bottom,
                "top": y_top,
                "size_meters": TILE_SIZE_METERS,
                "resolution": OUTPUT_RESOLUTION,
                "invalid_ratio": invalid_ratio,
                "tile_min": tile_min,
                "tile_max": tile_max,
                "tile_mean": tile_mean
            })

            print(
                f"  OK | invalid: {invalid_ratio:.2%} | "
                f"min: {tile_min:.2f}, max: {tile_max:.2f}, mean: {tile_mean:.2f}"
            )

            exported_count += 1

metadata_path = os.path.join(OUTPUT_DIR, "tiles_metadata.json")

with open(metadata_path, "w", encoding="utf-8") as f:
    json.dump(metadata, f, indent=2, ensure_ascii=False)

print()
print("DONE")
print(f"Candidate tiles: {total_candidates}")
print(f"Exported tiles:  {exported_count}")
print(f"Skipped empty:   {skipped_empty_count}")
print(f"Metadata:        {metadata_path}")