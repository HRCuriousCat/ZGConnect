import os
import json
import time
import requests
from io import BytesIO
from PIL import Image


# ============================================================
# CONFIG
# ============================================================

METADATA_JSON = r"D:\_unity projects\ZG Connect\_dataset\hightmaps_raw_1km_1025\metadata.json"

OUTPUT_DIR = r"D:\_unity projects\ZG Connect\_resources\_ortho\orthophoto_1000"

#WMS_URL = "https://geoportal.dgu.hr/services/inspire/orthophoto_lidar_2022_2023/wms"
#LAYER_NAME = "OI.OrthoimageCoverage"
#CRS = "EPSG:3765"


WMS_URL = "https://geoportal.dgu.hr/services/inspire/orthophoto_1000/wms"
LAYER_NAME = "OI.OrthoimageCoverage"
CRS = "EPSG:3765"




#https://geoportal.dgu.hr/services/inspire/orthophoto_1000/wms?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&LAYERS=OI.OrthoimageCoverage&STYLES=OI.OrthoimageCoverage.Default&CRS=EPSG:3765&BBOX=462000,5074000,462500,5074500&WIDTH=2000&HEIGHT=2000&FORMAT=image/png



TILE_SIZE_METERS = 1000.0
TEXTURE_RESOLUTION = 2048

IMAGE_FORMAT = "image/png"
FILE_EXTENSION = "png"

# Full run: 0.95 uključuje i dosta rubnih/djelomičnih tileova.
# Za čišći test koristi 0.10.
MAX_INVALID_RATIO = 0.95

# Ako je DOWNLOAD_ALL_TILES = False, skida samo prvih MAX_TILES_TO_DOWNLOAD tileova.
MAX_TILES_TO_DOWNLOAD = 5
DOWNLOAD_ALL_TILES = True

# Ako želiš skinuti točno određene tileove, upiši ih ovdje.
# Format: (left, bottom)
# Ako lista nije prazna, DOWNLOAD_ALL_TILES se ignorira.
SPECIFIC_TILES = [
    # (460000, 5074000),
]

SKIP_EXISTING = True
SAVE_DEBUG_SOURCE_IMAGES = False

SLEEP_BETWEEN_REQUESTS = 0.75
REQUEST_TIMEOUT = 120

DEBUG_DIR = os.path.join(OUTPUT_DIR, "_debug_sources")

os.makedirs(OUTPUT_DIR, exist_ok=True)

if SAVE_DEBUG_SOURCE_IMAGES:
    os.makedirs(DEBUG_DIR, exist_ok=True)


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

        found = {
            (int(tile["left"]), int(tile["bottom"]))
            for tile in selected
        }

        missing = wanted - found

        if missing:
            print("WARNING: requested tiles not found:")
            for item in sorted(missing):
                print("  missing:", item)

        return selected

    selected = []

    for tile in tiles:
        invalid_ratio = float(tile.get("invalid_ratio", 0))

        if invalid_ratio > MAX_INVALID_RATIO:
            continue

        selected.append(tile)

        if not DOWNLOAD_ALL_TILES and len(selected) >= MAX_TILES_TO_DOWNLOAD:
            break

    return selected


def final_texture_name(tile):
    left = int(tile["left"])
    bottom = int(tile["bottom"])
    return f"zg_ortho_{left}_{bottom}_1km_{TEXTURE_RESOLUTION}.{FILE_EXTENSION}"


def build_wms_params(left, bottom, right, top):
    return {
        "SERVICE": "WMS",
        "VERSION": "1.3.0",
        "REQUEST": "GetMap",
        "LAYERS": LAYER_NAME,
        "STYLES": "OI.OrthoimageCoverage.Default",
        "CRS": CRS,
        "BBOX": f"{left:.3f},{bottom:.3f},{right:.3f},{top:.3f}",
        "WIDTH": str(TEXTURE_RESOLUTION),
        "HEIGHT": str(TEXTURE_RESOLUTION),
        "FORMAT": IMAGE_FORMAT,
        "TRANSPARENT": "FALSE"
    }


def download_wms_image(session, left, bottom, right, top):
    params = build_wms_params(left, bottom, right, top)

    response = session.get(
        WMS_URL,
        params=params,
        timeout=REQUEST_TIMEOUT
    )

    print("    HTTP:", response.status_code)
    print("    Content-Type:", response.headers.get("Content-Type"))
    print("    URL:", response.url)

    response.raise_for_status()

    content_type = response.headers.get("Content-Type", "").lower()

    if "image" not in content_type:
        preview = response.text[:1000] if response.text else ""
        raise RuntimeError(
            "WMS response is not an image.\n"
            f"Content-Type: {content_type}\n"
            f"Preview:\n{preview}"
        )

    img = Image.open(BytesIO(response.content)).convert("RGB")

    if img.size != (TEXTURE_RESOLUTION, TEXTURE_RESOLUTION):
        print(
            f"    WARNING: image size is {img.size}, "
            f"expected {(TEXTURE_RESOLUTION, TEXTURE_RESOLUTION)}"
        )

        img = img.resize(
            (TEXTURE_RESOLUTION, TEXTURE_RESOLUTION),
            Image.Resampling.LANCZOS
        )

    return img, response.url


def make_tile_from_offset_thirds(session, tile):
    """
    Kreira jedan finalni 1x1 km ortofoto tile bez centralnog watermarka.

    Metoda:
    - napravi 3 WMS requesta
    - svaki request je širok 1 km
    - request 1 počinje na lijevom rubu finalnog tilea
    - request 2 počinje na početku druge trećine finalnog tilea
    - request 3 počinje na početku treće trećine finalnog tilea
    - iz svakog requesta uzima samo lijevi dio
    - spoji ta tri lijeva dijela u jedan finalni tile
    """

    final_left = float(tile["left"])
    final_bottom = float(tile["bottom"])
    final_right = float(tile["right"])
    final_top = float(tile["top"])

    tile_width_m = final_right - final_left

    # Pixel-perfect segment boundaries.
    # Za 2048 daje:
    # 0 -> 683
    # 683 -> 1365
    # 1365 -> 2048
    segment_pixel_bounds = [
        0,
        round(TEXTURE_RESOLUTION / 3.0),
        round(2.0 * TEXTURE_RESOLUTION / 3.0),
        TEXTURE_RESOLUTION
    ]

    requests_bboxes = []
    source_images = []
    source_urls = []
    slices = []

    for i in range(3):
        final_px_start = segment_pixel_bounds[i]
        final_px_end = segment_pixel_bounds[i + 1]
        segment_width_px = final_px_end - final_px_start

        # Pretvori pixel granicu finalnog tilea u metarsku X koordinatu.
        segment_geo_start_x = final_left + (final_px_start / TEXTURE_RESOLUTION) * tile_width_m
        segment_geo_end_x = final_left + (final_px_end / TEXTURE_RESOLUTION) * tile_width_m

        # Svaki request je punih 1 km širok, ali počinje na početku segmenta.
        req_left = segment_geo_start_x
        req_bottom = final_bottom
        req_right = req_left + tile_width_m
        req_top = final_top

        requests_bboxes.append((req_left, req_bottom, req_right, req_top))

        print(f"    Download source {i + 1}/3")
        print(f"    Final px: {final_px_start} -> {final_px_end} ({segment_width_px}px)")
        print(f"    Segment geo X: {segment_geo_start_x:.6f} -> {segment_geo_end_x:.6f}")
        print(f"    Request BBOX: {req_left:.3f},{req_bottom:.3f},{req_right:.3f},{req_top:.3f}")

        img, url = download_wms_image(
            session,
            req_left,
            req_bottom,
            req_right,
            req_top
        )

        source_images.append(img)
        source_urls.append(url)

        # Uzmi samo lijevi dio responsea koji odgovara širini segmenta.
        cropped = img.crop((
            0,
            0,
            segment_width_px,
            TEXTURE_RESOLUTION
        ))

        if cropped.size != (segment_width_px, TEXTURE_RESOLUTION):
            cropped = cropped.resize(
                (segment_width_px, TEXTURE_RESOLUTION),
                Image.Resampling.LANCZOS
            )

        slices.append(cropped)

    final_img = Image.new(
        "RGB",
        (TEXTURE_RESOLUTION, TEXTURE_RESOLUTION)
    )

    x = 0
    for cropped in slices:
        final_img.paste(cropped, (x, 0))
        x += cropped.size[0]

    if x != TEXTURE_RESOLUTION:
        raise RuntimeError(
            f"Final stitched width is wrong: {x}, expected {TEXTURE_RESOLUTION}"
        )

    return final_img, source_images, source_urls, requests_bboxes


def save_metadata(metadata_out_path, exported, failed):
    metadata_out = {
        "settings": {
            "wms_url": WMS_URL,
            "layer_name": LAYER_NAME,
            "crs": CRS,
            "image_format": IMAGE_FORMAT,
            "file_extension": FILE_EXTENSION,
            "tile_size_meters": TILE_SIZE_METERS,
            "texture_resolution": TEXTURE_RESOLUTION,
            "method": "three_offset_left_thirds_pixel_boundaries",
            "max_invalid_ratio": MAX_INVALID_RATIO
        },
        "tiles": exported,
        "failed": failed
    }

    with open(metadata_out_path, "w", encoding="utf-8") as f:
        json.dump(metadata_out, f, indent=2, ensure_ascii=False)


# ============================================================
# MAIN
# ============================================================

def main():
    print("=== DGU Ortho Offset Thirds Downloader ===")
    print("Metadata:", METADATA_JSON)
    print("Output:", OUTPUT_DIR)
    print("WMS:", WMS_URL)
    print("Layer:", LAYER_NAME)
    print("Texture resolution:", TEXTURE_RESOLUTION)
    print("Download all:", DOWNLOAD_ALL_TILES)
    print("Max invalid ratio:", MAX_INVALID_RATIO)
    print()

    metadata = load_metadata(METADATA_JSON)
    tiles = select_tiles(metadata)

    print("Selected tiles:", len(tiles))
    print()

    if not tiles:
        print("No tiles selected.")
        return

    session = requests.Session()

    exported = []
    failed = []

    metadata_out_path = os.path.join(OUTPUT_DIR, "ortho_tiles_metadata.json")

    for index, tile in enumerate(tiles, start=1):
        left = int(tile["left"])
        bottom = int(tile["bottom"])
        right = int(tile["right"])
        top = int(tile["top"])

        out_name = final_texture_name(tile)
        out_path = os.path.join(OUTPUT_DIR, out_name)

        print(f"[{index}/{len(tiles)}] Final tile {left},{bottom}")
        print(f"  Final BBOX: {left},{bottom},{right},{top}")

        if SKIP_EXISTING and os.path.exists(out_path):
            print(f"  SKIP existing: {out_name}")

            exported.append({
                "texture_file": out_name,
                "source_raw_file": tile.get("raw_file"),
                "left": left,
                "bottom": bottom,
                "right": right,
                "top": top,
                "bbox": [left, bottom, right, top],
                "texture_resolution": TEXTURE_RESOLUTION,
                "method": "three_offset_left_thirds_pixel_boundaries",
                "invalid_ratio": tile.get("invalid_ratio"),
                "unity_position": tile.get("unity_position"),
                "terrain_size": tile.get("terrain_size"),
                "status": "already_exists"
            })

            save_metadata(metadata_out_path, exported, failed)

            print()
            continue

        try:
            final_img, source_images, source_urls, source_bboxes = make_tile_from_offset_thirds(
                session,
                tile
            )

            final_img.save(out_path)

            print(f"  SAVED final texture: {out_name}")

            if SAVE_DEBUG_SOURCE_IMAGES:
                base = os.path.splitext(out_name)[0]

                for i, img in enumerate(source_images, start=1):
                    debug_name = f"{base}_source_{i}.png"
                    debug_path = os.path.join(DEBUG_DIR, debug_name)
                    img.save(debug_path)

            exported.append({
                "texture_file": out_name,
                "source_raw_file": tile.get("raw_file"),
                "left": left,
                "bottom": bottom,
                "right": right,
                "top": top,
                "bbox": [left, bottom, right, top],
                "texture_resolution": TEXTURE_RESOLUTION,
                "method": "three_offset_left_thirds_pixel_boundaries",
                "source_urls": source_urls,
                "source_bboxes": [
                    [bbox[0], bbox[1], bbox[2], bbox[3]]
                    for bbox in source_bboxes
                ],
                "invalid_ratio": tile.get("invalid_ratio"),
                "unity_position": tile.get("unity_position"),
                "terrain_size": tile.get("terrain_size"),
                "status": "exported"
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

        save_metadata(metadata_out_path, exported, failed)

        print()
        time.sleep(SLEEP_BETWEEN_REQUESTS)

    print("=== DONE ===")
    print("Exported/existing:", len(exported))
    print("Failed:", len(failed))
    print("Metadata:", metadata_out_path)


if __name__ == "__main__":
    main()