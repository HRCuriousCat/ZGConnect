import math
import time
from io import BytesIO
from pathlib import Path

import requests
from PIL import Image
from pyproj import Transformer


# ============================================================
# INPUT: WGS84 bounds
# ============================================================

WEST_LON = 15.818
EAST_LON = 16.109
SOUTH_LAT = 45.724
NORTH_LAT = 45.901

OUTPUT_SIZE = 2048
TILE_GRID = 4  # 4x4 tileova; svaki 512x512 px
OUTPUT_FILE = "dgu_ortho_selection_2048.png"

# DGU WMS endpoint
WMS_URL = "https://geoportal.dgu.hr/services/inspire/orthophoto_1000/wms"

LAYER = "OI.OrthoimageCoverage"
STYLE = "OI.OrthoimageCoverage.Default"


# ============================================================
# CRS transformers
# ============================================================

# EPSG:4326 lon/lat -> EPSG:3765 HTRS96 / Croatia TM
to_3765 = Transformer.from_crs("EPSG:4326", "EPSG:3765", always_xy=True)


def lonlat_to_3765(lon: float, lat: float):
    x, y = to_3765.transform(lon, lat)
    return x, y


def make_square_bbox(xmin, ymin, xmax, ymax):
    """
    Expand bbox to square in projected meters.
    This prevents stretching when exporting to 2048x2048.
    """
    width = xmax - xmin
    height = ymax - ymin

    cx = (xmin + xmax) / 2
    cy = (ymin + ymax) / 2

    size = max(width, height)

    half = size / 2

    return (
        cx - half,
        cy - half,
        cx + half,
        cy + half,
    )


def download_wms_tile(xmin, ymin, xmax, ymax, width, height, tile_index=None):
    """
    Downloads one WMS tile using WMS 1.1.1.
    For EPSG:3765, WMS 1.1.1 uses normal BBOX order:
    xmin,ymin,xmax,ymax
    """

    params = {
        "SERVICE": "WMS",
        "VERSION": "1.1.1",
        "REQUEST": "GetMap",
        "SRS": "EPSG:3765",
        "BBOX": f"{xmin},{ymin},{xmax},{ymax}",
        "LAYERS": LAYER,
        "STYLES": STYLE,
        "WIDTH": str(width),
        "HEIGHT": str(height),
        "FORMAT": "image/png",
        "TRANSPARENT": "FALSE",
    }

    label = f" tile {tile_index}" if tile_index is not None else ""
    print(f"Downloading{label}:")
    print(f"  BBOX={params['BBOX']}")

    response = requests.get(WMS_URL, params=params, timeout=60)

    if response.status_code != 200:
        raise RuntimeError(
            f"WMS request failed with HTTP {response.status_code}:\n"
            f"{response.text[:1000]}"
        )

    content_type = response.headers.get("Content-Type", "")

    if "image" not in content_type.lower():
        raise RuntimeError(
            "WMS did not return an image.\n"
            f"Content-Type: {content_type}\n"
            f"Response preview:\n{response.text[:1000]}"
        )

    img = Image.open(BytesIO(response.content)).convert("RGB")
    return img


def main():
    output_path = Path(OUTPUT_FILE)

    # ------------------------------------------------------------
    # Convert bbox corners from lon/lat to EPSG:3765
    # ------------------------------------------------------------

    x1, y1 = lonlat_to_3765(WEST_LON, SOUTH_LAT)
    x2, y2 = lonlat_to_3765(EAST_LON, NORTH_LAT)

    xmin = min(x1, x2)
    xmax = max(x1, x2)
    ymin = min(y1, y2)
    ymax = max(y1, y2)

    print("Original projected bbox EPSG:3765:")
    print(f"  xmin = {xmin}")
    print(f"  ymin = {ymin}")
    print(f"  xmax = {xmax}")
    print(f"  ymax = {ymax}")
    print(f"  width  = {xmax - xmin:.2f} m")
    print(f"  height = {ymax - ymin:.2f} m")

    # ------------------------------------------------------------
    # Expand to square
    # ------------------------------------------------------------

    sxmin, symin, sxmax, symax = make_square_bbox(xmin, ymin, xmax, ymax)

    square_size_m = sxmax - sxmin
    meters_per_pixel = square_size_m / OUTPUT_SIZE

    print()
    print("Square bbox EPSG:3765:")
    print(f"  xmin = {sxmin}")
    print(f"  ymin = {symin}")
    print(f"  xmax = {sxmax}")
    print(f"  ymax = {symax}")
    print(f"  size = {square_size_m:.2f} m x {square_size_m:.2f} m")
    print(f"  resolution = {meters_per_pixel:.2f} m/px")

    # ------------------------------------------------------------
    # Download tiled image
    # ------------------------------------------------------------

    tile_size_px = OUTPUT_SIZE // TILE_GRID

    if OUTPUT_SIZE % TILE_GRID != 0:
        raise ValueError("OUTPUT_SIZE must be divisible by TILE_GRID.")

    final_img = Image.new("RGB", (OUTPUT_SIZE, OUTPUT_SIZE))

    tile_width_m = square_size_m / TILE_GRID
    tile_height_m = square_size_m / TILE_GRID

    tile_counter = 1
    total_tiles = TILE_GRID * TILE_GRID

    for row in range(TILE_GRID):
        for col in range(TILE_GRID):
            txmin = sxmin + col * tile_width_m
            txmax = sxmin + (col + 1) * tile_width_m

            # Important:
            # Image rows go from top to bottom,
            # but projected Y goes from bottom to top.
            tymax = symax - row * tile_height_m
            tymin = symax - (row + 1) * tile_height_m

            tile = download_wms_tile(
                txmin,
                tymin,
                txmax,
                tymax,
                tile_size_px,
                tile_size_px,
                tile_index=f"{tile_counter}/{total_tiles}",
            )

            final_img.paste(tile, (col * tile_size_px, row * tile_size_px))

            tile_counter += 1

            # Be polite to the server
            time.sleep(0.2)

    # ------------------------------------------------------------
    # Save output
    # ------------------------------------------------------------

    final_img.save(output_path)

    print()
    print(f"Saved: {output_path.resolve()}")
    print(f"Image size: {final_img.size[0]}x{final_img.size[1]} px")


if __name__ == "__main__":
    main()