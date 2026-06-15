import os
import json
import numpy as np
import rasterio


# ============================================================
# CONFIG
# ============================================================

INPUT_TIF_DIR = r"D:\_unity projects\ZG Connect\_resources\dmr height\tiles_tif_1km_1025"
OUTPUT_RAW_DIR = r"D:\_unity projects\ZG Connect\_resources\dmr height\tiles_raw_1km_1025"

OUTPUT_METADATA = os.path.join(OUTPUT_RAW_DIR, "tiles_unity_metadata.json")

RESOLUTION = 1025
TILE_SIZE_METERS = 1000

# Globalni raspon visina za SVE tileove.
# Nemoj koristiti lokalni min/max po tileu.
MIN_HEIGHT = 95.0
MAX_HEIGHT = 1040.0

# Ako želiš preskočiti jako djelomične rubne tileove.
# Za prvi Unity test predlažem 0.10 ili 0.25.
# Za full export možeš staviti 0.95.
MAX_INVALID_RATIO_TO_EXPORT = 0.95

NODATA_THRESHOLD = 1e20

# Relativni Unity origin.
# Možeš ga kasnije promijeniti.
# Preporuka: koristi donji-lijevi kut cijelog seta ili neki centralni origin.
UNITY_ORIGIN_X = 442000
UNITY_ORIGIN_Y = 5051000

os.makedirs(OUTPUT_RAW_DIR, exist_ok=True)


# ============================================================
# HELPERS
# ============================================================

def parse_tile_position_from_filename(filename):
    """
    Očekuje naziv:
    zg_dmr_460000_5074000_1km_1025.tif

    Vraća:
    left_x = 460000
    bottom_y = 5074000
    """
    name = os.path.splitext(filename)[0]
    parts = name.split("_")

    # zg_dmr_460000_5074000_1km_1025
    left_x = int(parts[2])
    bottom_y = int(parts[3])

    return left_x, bottom_y


def get_invalid_mask(data, nodata):
    invalid = ~np.isfinite(data)

    if nodata is not None:
        invalid |= data == nodata

    invalid |= data > NODATA_THRESHOLD

    return invalid


def fill_invalid_with_nearest_valid(data, invalid_mask):
    """
    Jednostavna fallback varijanta:
    ako ima NoData rupe, puni ih minimalnom visinom.

    Ovo nije najljepša interpolacija, ali je stabilno za Unity.
    Za rubne tileove ionako ih vjerojatno nećeš koristiti u prvom testu.
    """
    data = data.copy()
    data[invalid_mask] = MIN_HEIGHT
    return data


# ============================================================
# MAIN
# ============================================================

unity_tiles = []

tif_files = sorted([
    f for f in os.listdir(INPUT_TIF_DIR)
    if f.lower().endswith(".tif")
])

print(f"Found {len(tif_files)} GeoTIFF tiles.")
print(f"Input:  {INPUT_TIF_DIR}")
print(f"Output: {OUTPUT_RAW_DIR}")
print()

exported = 0
skipped = 0

for filename in tif_files:
    tif_path = os.path.join(INPUT_TIF_DIR, filename)

    with rasterio.open(tif_path) as src:
        data = src.read(1).astype(np.float32)
        nodata = src.nodata
        bounds = src.bounds

    if data.shape != (RESOLUTION, RESOLUTION):
        print(f"SKIP wrong resolution: {filename} | shape={data.shape}")
        skipped += 1
        continue

    invalid = get_invalid_mask(data, nodata)
    invalid_ratio = np.count_nonzero(invalid) / data.size

    if invalid_ratio > MAX_INVALID_RATIO_TO_EXPORT:
        print(f"SKIP mostly empty: {filename} | invalid={invalid_ratio:.2%}")
        skipped += 1
        continue

    left_x, bottom_y = parse_tile_position_from_filename(filename)

    # NoData popunjavanje
    data = fill_invalid_with_nearest_valid(data, invalid)

    # Clamp na globalni raspon
    data = np.clip(data, MIN_HEIGHT, MAX_HEIGHT)

    # Normalizacija 0..1
    normalized = (data - MIN_HEIGHT) / (MAX_HEIGHT - MIN_HEIGHT)

    # Unity RAW: 16-bit unsigned little endian
    raw = np.round(normalized * 65535.0).astype("<u2")

    # Ako u Unityju teren ispadne okrenut sjever-jug, aktiviraj ovo:
    # raw = np.flipud(raw)

    raw_name = os.path.splitext(filename)[0] + ".raw"
    raw_path = os.path.join(OUTPUT_RAW_DIR, raw_name)

    raw.tofile(raw_path)

    unity_x = left_x - UNITY_ORIGIN_X
    unity_z = bottom_y - UNITY_ORIGIN_Y

    valid_data = data[~invalid] if np.any(~invalid) else data.flatten()

    tile_meta = {
        "raw_file": raw_name,
        "source_tif": filename,

        "left": left_x,
        "bottom": bottom_y,
        "right": left_x + TILE_SIZE_METERS,
        "top": bottom_y + TILE_SIZE_METERS,

        "unity_position": {
            "x": unity_x,
            "y": 0,
            "z": unity_z
        },

        "terrain_size": {
            "x": TILE_SIZE_METERS,
            "y": MAX_HEIGHT - MIN_HEIGHT,
            "z": TILE_SIZE_METERS
        },

        "heightmap_resolution": RESOLUTION,
        "raw_format": "UInt16 little-endian",
        "byte_order": "Windows",

        "global_min_height": MIN_HEIGHT,
        "global_max_height": MAX_HEIGHT,

        "invalid_ratio": float(invalid_ratio),
        "tile_min_after_clamp": float(np.min(valid_data)),
        "tile_max_after_clamp": float(np.max(valid_data)),
        "tile_mean_after_clamp": float(np.mean(valid_data))
    }

    unity_tiles.append(tile_meta)

    print(
        f"OK {raw_name} | "
        f"invalid={invalid_ratio:.2%} | "
        f"Unity pos=({unity_x}, 0, {unity_z})"
    )

    exported += 1


metadata = {
    "settings": {
        "resolution": RESOLUTION,
        "tile_size_meters": TILE_SIZE_METERS,
        "min_height": MIN_HEIGHT,
        "max_height": MAX_HEIGHT,
        "terrain_height": MAX_HEIGHT - MIN_HEIGHT,
        "unity_origin_x": UNITY_ORIGIN_X,
        "unity_origin_y": UNITY_ORIGIN_Y,
        "max_invalid_ratio_to_export": MAX_INVALID_RATIO_TO_EXPORT
    },
    "tiles": unity_tiles
}

with open(OUTPUT_METADATA, "w", encoding="utf-8") as f:
    json.dump(metadata, f, indent=2, ensure_ascii=False)

print()
print("DONE")
print(f"Exported RAW: {exported}")
print(f"Skipped:      {skipped}")
print(f"Metadata:     {OUTPUT_METADATA}")