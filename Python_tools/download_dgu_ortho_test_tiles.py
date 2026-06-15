import os
import json
import time
import requests
from PIL import Image


# ============================================================
# CONFIG
# ============================================================

METADATA_JSON = r"D:\_unity projects\ZG Connect\_resources\_height\tiles_raw_1km_1025\tiles_unity_metadata.json"

OUTPUT_DIR = r"D:\_unity projects\ZG Connect\_resources\_ortho\dgu_2022_test_2048"

WMS_URL = "https://geoportal.dgu.hr/services/inspire/orthophoto_lidar_2022_2023/wms"
# WMS_URL = "https://geoportal.dgu.hr/services/inspire/orthophoto_2022/wms"

LAYER_NAME = "OI.OrthoimageCoverage"

CRS = "EPSG:3765"

TEXTURE_RESOLUTION = 2048

IMAGE_FORMAT = "image/png"
FILE_EXTENSION = "png"

# Za test nemoj skidati rubne tileove pune NoData terena.
MAX_INVALID_RATIO = 0.10

# Koliko tileova da skine u testu.
MAX_TILES_TO_DOWNLOAD = 5

# Ako želiš ciljati konkretne tileove, upiši ih ovdje.
# Format: (left, bottom)
# Ako je lista prazna, skripta uzima prvih nekoliko dobrih tileova iz metadata.
SPECIFIC_TILES = [
    # (460000, 5074000),
    # (461000, 5074000),
    # (462000, 5074000),
]

REQUEST_TIMEOUT = 60
SLEEP_BETWEEN_REQUESTS = 0.5

os.makedirs(OUTPUT_DIR, exist_ok=True)


# ============================================================
# HELPERS
# ============================================================

def load_metadata(path):
    if not os.path.exists(path):
        raise FileNotFoundError(f"Metadata JSON not found: {path}")

    with open(path, "r", encoding="utf-8") as f:
        metadata = json.load(f)

    if "tiles" not in metadata:
        raise ValueError("Metadata JSON does not contain 'tiles' array.")

    return metadata


def select_tiles(metadata):
    tiles = metadata["tiles"]

    if SPECIFIC_TILES:
        wanted = set(SPECIFIC_TILES)
        selected = [
            tile for tile in tiles
            if (int(tile["left"]), int(tile["bottom"])) in wanted
        ]

        missing = wanted - {
            (int(tile["left"]), int(tile["bottom"]))
            for tile in selected
        }

        if missing:
            print("WARNING: Some requested tiles were not found in metadata:")
            for item in sorted(missing):
                print("  missing:", item)

        return selected

    selected = []

    for tile in tiles:
        invalid_ratio = float(tile.get("invalid_ratio", 0))

        if invalid_ratio > MAX_INVALID_RATIO:
            continue

        selected.append(tile)

        if len(selected) >= MAX_TILES_TO_DOWNLOAD:
            break

    return selected


def build_wms_params(tile):
    left = int(tile["left"])
    bottom = int(tile["bottom"])
    right = int(tile["right"])
    top = int(tile["top"])

    return {
        "SERVICE": "WMS",
        "VERSION": "1.1.1",
        "REQUEST": "GetMap",
        "LAYERS": LAYER_NAME,
        "STYLES": "",
        "SRS": CRS,
        "BBOX": f"{left},{bottom},{right},{top}",
        "WIDTH": str(TEXTURE_RESOLUTION),
        "HEIGHT": str(TEXTURE_RESOLUTION),
        "FORMAT": IMAGE_FORMAT,
        "TRANSPARENT": "FALSE"
    }


def output_filename(tile):
    left = int(tile["left"])
    bottom = int(tile["bottom"])
    return f"zg_ortho_{left}_{bottom}_1km_{TEXTURE_RESOLUTION}.{FILE_EXTENSION}"


def save_response_as_image(response, output_path):
    content_type = response.headers.get("Content-Type", "").lower()

    if "image" not in content_type:
        error_preview = response.text[:1000] if response.text else ""
        raise RuntimeError(
            "WMS response is not an image.\n"
            f"Content-Type: {content_type}\n"
            f"Response preview:\n{error_preview}"
        )

    with open(output_path, "wb") as f:
        f.write(response.content)

    # Validate image.
    try:
        with Image.open(output_path) as img:
            img.verify()
    except Exception as ex:
        raise RuntimeError(f"Saved file is not a valid image: {output_path}\n{ex}")

    # Reopen for readable info.
    with Image.open(output_path) as img:
        return img.size, img.mode


# ============================================================
# MAIN
# ============================================================

def main():
    metadata = load_metadata(METADATA_JSON)
    selected_tiles = select_tiles(metadata)

    print("Metadata:", METADATA_JSON)
    print("Output:", OUTPUT_DIR)
    print("Selected tiles:", len(selected_tiles))
    print()

    if not selected_tiles:
        print("No tiles selected. Lower MAX_INVALID_RATIO or set SPECIFIC_TILES.")
        return

    session = requests.Session()

    downloaded = []
    failed = []

    for index, tile in enumerate(selected_tiles, start=1):
        left = int(tile["left"])
        bottom = int(tile["bottom"])
        right = int(tile["right"])
        top = int(tile["top"])

        filename = output_filename(tile)
        output_path = os.path.join(OUTPUT_DIR, filename)

        print(f"[{index}/{len(selected_tiles)}] Tile {left}, {bottom}")
        print(f"  BBOX: {left},{bottom},{right},{top}")

        if os.path.exists(output_path):
            print(f"  SKIP exists: {filename}")
            downloaded.append({
                "texture_file": filename,
                "left": left,
                "bottom": bottom,
                "right": right,
                "top": top,
                "status": "already_exists"
            })
            continue

        params = build_wms_params(tile)

        try:
            response = session.get(
                WMS_URL,
                params=params,
                timeout=REQUEST_TIMEOUT
            )

            print("  URL:", response.url)
            print("  HTTP:", response.status_code)
            print("  Content-Type:", response.headers.get("Content-Type"))

            response.raise_for_status()

            image_size, image_mode = save_response_as_image(response, output_path)

            print(f"  SAVED: {filename}")
            print(f"  Image: {image_size}, mode={image_mode}")

            downloaded.append({
                "texture_file": filename,
                "source_raw_file": tile.get("raw_file"),
                "left": left,
                "bottom": bottom,
                "right": right,
                "top": top,
                "bbox": [left, bottom, right, top],
                "texture_resolution": TEXTURE_RESOLUTION,
                "wms_url": response.url,
                "image_format": IMAGE_FORMAT,
                "image_size": list(image_size),
                "image_mode": image_mode,
                "invalid_ratio": tile.get("invalid_ratio"),
                "status": "downloaded"
            })

        except Exception as ex:
            print("  FAILED:", ex)

            failed.append({
                "left": left,
                "bottom": bottom,
                "right": right,
                "top": top,
                "error": str(ex)
            })

        print()

        time.sleep(SLEEP_BETWEEN_REQUESTS)

    metadata_out = {
        "settings": {
            "wms_url": WMS_URL,
            "layer_name": LAYER_NAME,
            "crs": CRS,
            "texture_resolution": TEXTURE_RESOLUTION,
            "image_format": IMAGE_FORMAT,
            "max_invalid_ratio": MAX_INVALID_RATIO
        },
        "downloaded": downloaded,
        "failed": failed
    }

    metadata_out_path = os.path.join(OUTPUT_DIR, "ortho_test_metadata.json")

    with open(metadata_out_path, "w", encoding="utf-8") as f:
        json.dump(metadata_out, f, indent=2, ensure_ascii=False)

    print("DONE")
    print("Downloaded/Existing:", len(downloaded))
    print("Failed:", len(failed))
    print("Metadata:", metadata_out_path)


if __name__ == "__main__":
    main()