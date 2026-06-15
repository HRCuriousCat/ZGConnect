#!/usr/bin/env python3
"""
ZG Connect — Per-Tile Basemap Downloader
========================================
For each terrain tile, produces one basemap PNG aligned to the 1 km grid.

Two source modes:

  slippy (default legacy)
      Downloads web map tiles (OSM/Carto/etc.), stitches, crops, resizes.
      Zoom 17 ≈ 1.2 m/px   Zoom 18 ≈ 0.6 m/px   Zoom 19 ≈ 0.3 m/px

  cache
      Renders vector OSM from a regional Overpass JSON cache
      (same cache as osm_vegetation_masks.py / download_osm_overview.py).
      Offline, no tile-server rate limits. Style matches the overview renderer.
      Use --sample-resolution higher than --output-resolution for supersample
      antialiasing (render large, downscale with LANCZOS).

Requirements
------------
    pip install pyproj Pillow
    cache mode also needs: pip install shapely

Output
------
    _dataset/
        hightmaps_raw_1km_1025/    existing
        osm_light/                   example output folder
            zg_osm_*_1km_1024.png
            metadata.json            ready for ZG Connect Dataset Import Manager

Examples
--------
    python download_basemap.py --source slippy
    python download_basemap.py --source cache
    python download_basemap.py --source cache --cache "_dataset/osm_overpass_cache.json"
    python download_basemap.py --source cache --sample-resolution 2048 --output-resolution 1024
"""

import argparse
import io
import json
import math
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

from PIL import Image

# ══════════════════════════════════════════════════════════════════════════════
# CONFIGURATION
# ══════════════════════════════════════════════════════════════════════════════

# Path to the heightmap metadata.json — tile grid source of truth
HEIGHTMAP_METADATA = r"D:\_unity projects\ZG Connect\_dataset\hightmaps_raw_1km_1025\metadata.json"

# Dataset root — output folder is created directly inside here
DATASET_ROOT = r"D:\_unity projects\ZG Connect\_dataset"

# slippy | cache — cache renders from osm_overpass_cache.json (offline)
SOURCE = "slippy"

# Path to Overpass cache (cache mode). None = auto-discover like download_osm_overview.py
OSM_CACHE_FILE = None

# Output subfolder name (appears as the basemap display name in the editor)
OUTPUT_FOLDER_NAME = "zg_osm_voyager_nolabels_512"

# Slippy tile URL template — {s}, {z}, {x}, {y} are substituted automatically
#TILE_URL = "https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png"

# Clean map, no labels — light background
#TILE_URL = "https://{s}.basemaps.cartocdn.com/light_nolabels/{z}/{x}/{y}.png"

# Clean map, no labels — dark background
#TILE_URL = "https://{s}.basemaps.cartocdn.com/dark_nolabels/{z}/{x}/{y}.png"

# Carto Voyager style, no labels
TILE_URL = "https://{s}.basemaps.cartocdn.com/rastertiles/voyager_nolabels/{z}/{x}/{y}.png"

# Subdomains to rotate across (distributes load, speeds up parallel CDN fetches)
#TILE_SUBDOMAINS = ["a", "b", "c"]
TILE_SUBDOMAINS = ["a", "b", "c", "d"]

# Zoom level (17 = fast/coarser, 18 = recommended, 19 = slowest/sharpest)
ZOOM = 20

# Final PNG size in pixels (square)
OUTPUT_RESOLUTION = 1024

# Internal render size for cache mode before LANCZOS downscale (0 = same as output)
SAMPLE_RESOLUTION = 2048

# Filename prefix for output PNGs (cache mode uses zg_osm_cache automatically)
FILE_PREFIX = "zg_osm_voyager_nolabels"

# Disk cache for raw slippy tiles — tiles shared between adjacent terrain tiles
# are downloaded once and reused.  Delete this folder to force a fresh download.
# Set to None to disable caching (tiles are fetched fresh every run).
TILE_CACHE_DIR = r"D:\_unity projects\ZG Connect\_dataset\_tile_cache"

# Skip terrain tiles whose output PNG already exists (resume interrupted runs)
SKIP_EXISTING = True

# Delay between HTTP requests in seconds — keep this at ≥ 0.1 s to be polite
REQUEST_DELAY_SECONDS = 0.15

# Retries per slippy tile on network error
MAX_RETRIES = 3

# Road width multiplier for cache mode (1.0 = real-world scale, >1 = thicker lines)
ROAD_WIDTH_SCALE = 1.35

# ══════════════════════════════════════════════════════════════════════════════
# END CONFIGURATION
# ══════════════════════════════════════════════════════════════════════════════


def resolve_sample_resolution(output_resolution: int, sample_arg: int | None) -> int:
    if sample_arg is not None:
        sample = sample_arg
    elif SAMPLE_RESOLUTION > 0:
        sample = SAMPLE_RESOLUTION
    else:
        sample = output_resolution

    if sample < output_resolution:
        print(
            f"WARNING: sample-resolution ({sample}) < output-resolution "
            f"({output_resolution}); using output size only."
        )
        return output_resolution

    return sample


def render_cache_tile(
    osm_index,
    left: float,
    bottom: float,
    right: float,
    top: float,
    sample_resolution: int,
    output_resolution: int,
    road_width_scale: float,
) -> Image.Image:
    img = osm_index.render_tile(
        left,
        bottom,
        right,
        top,
        sample_resolution,
        road_width_scale=road_width_scale,
    )
    if sample_resolution != output_resolution:
        img = img.resize((output_resolution, output_resolution), Image.LANCZOS)
    return img


def parse_args() -> argparse.Namespace:
    ap = argparse.ArgumentParser(description="ZG Connect per-tile basemap generator.")
    ap.add_argument(
        "--source",
        choices=["slippy", "cache"],
        default=SOURCE,
        help="slippy = web tiles, cache = regional Overpass JSON cache",
    )
    ap.add_argument(
        "--cache",
        dest="cache_file",
        default=OSM_CACHE_FILE,
        help="Path to osm_overpass_cache.json (cache mode; auto-discovered if omitted)",
    )
    ap.add_argument(
        "--metadata",
        default=HEIGHTMAP_METADATA,
        help="Heightmap metadata.json defining the tile grid",
    )
    ap.add_argument(
        "--out",
        default=OUTPUT_FOLDER_NAME,
        help="Output folder name under dataset root",
    )
    ap.add_argument(
        "--output-resolution",
        "--resolution",
        dest="output_resolution",
        type=int,
        default=OUTPUT_RESOLUTION,
        help="Final PNG size in pixels (square)",
    )
    ap.add_argument(
        "--sample-resolution",
        type=int,
        default=None,
        help=(
            "Cache mode: internal render size before LANCZOS downscale "
            f"(default: config SAMPLE_RESOLUTION={SAMPLE_RESOLUTION} or output size)"
        ),
    )
    ap.add_argument(
        "--tile",
        help="Process only one tile id, e.g. 455000_5061000",
    )
    ap.add_argument(
        "--road-scale",
        type=float,
        default=ROAD_WIDTH_SCALE,
        help="Cache mode only: road/rail width multiplier (default: %(default)s)",
    )
    return ap.parse_args()


def main():
    args = parse_args()

    try:
        from pyproj import Transformer
    except ImportError:
        print("ERROR: pyproj not installed.  Run:  pip install pyproj Pillow")
        sys.exit(1)

    transformer = Transformer.from_crs("EPSG:3765", "EPSG:4326", always_xy=True)

    meta_path = Path(args.metadata)
    if not meta_path.exists():
        print(f"ERROR: File not found:\n  {meta_path}")
        sys.exit(1)

    with open(meta_path, encoding="utf-8") as f:
        hm = json.load(f)

    tiles = hm["tiles"]
    if args.tile:
        tiles = [t for t in tiles if f"{t['left']}_{t['bottom']}" == args.tile]
        if not tiles:
            print(f"ERROR: Tile not found in metadata: {args.tile}")
            sys.exit(1)

    tile_size = float(hm["settings"]["tile_size_meters"])
    total = len(tiles)
    output_resolution = args.output_resolution
    sample_resolution = resolve_sample_resolution(output_resolution, args.sample_resolution)

    res_label = f"{output_resolution} px output"
    if args.source == "cache" and sample_resolution != output_resolution:
        res_label = (
            f"{sample_resolution} px render -> {output_resolution} px output "
            f"(supersample {sample_resolution / output_resolution:.1f}x)"
        )

    print(f"Loaded {total} tiles  |  source={args.source}  |  {res_label}")

    out_dir = Path(DATASET_ROOT) / args.out
    out_dir.mkdir(parents=True, exist_ok=True)

    file_prefix = FILE_PREFIX if args.source == "slippy" else "zg_osm_cache"

    osm_index = None
    cache_path = None
    if args.source == "cache":
        from download_osm_overview import OsmRenderIndex, load_overpass_cache, resolve_cache_path

        cache_path = resolve_cache_path(args.cache_file)
        if cache_path is None:
            print("ERROR: No OSM cache file found for cache mode.")
            print("Run osm_vegetation_masks.py first or pass --cache <path>.")
            sys.exit(1)

        print(f"OSM cache: {cache_path.resolve()}")
        print("Building spatial index...")
        osm_data = load_overpass_cache(cache_path)
        osm_index = OsmRenderIndex(osm_data)
        print(f"Indexed {len(osm_index.items):,} drawable features")
    elif TILE_CACHE_DIR:
        Path(TILE_CACHE_DIR).mkdir(parents=True, exist_ok=True)
        print(f"Tile cache: {TILE_CACHE_DIR}")

    print()

    tile_entries = []
    failed_ids = []
    processed = skipped = failed = 0
    subdomain_idx = 0

    for idx, tile in enumerate(tiles, 1):
        left, bottom = tile["left"], tile["bottom"]
        right, top = tile["right"], tile["top"]
        tile_id = f"{left}_{bottom}"
        filename = f"{file_prefix}_{tile_id}_1km_{output_resolution}.png"
        out_path = out_dir / filename

        label = f"[{idx:>4}/{total}]  {tile_id}"

        if SKIP_EXISTING and out_path.exists():
            print(f"{label}  skip")
            skipped += 1
            tile_entries.append(make_entry(filename, left, bottom, right, top, output_resolution))
            continue

        try:
            if args.source == "cache":
                result = render_cache_tile(
                    osm_index,
                    left,
                    bottom,
                    right,
                    top,
                    sample_resolution,
                    output_resolution,
                    args.road_scale,
                )
                result.save(out_path, "PNG", optimize=False)
                aa_note = (
                    f"  ({sample_resolution}->{output_resolution})"
                    if sample_resolution != output_resolution
                    else ""
                )
                print(f"{label}  ok{aa_note}")
            else:
                corners = [
                    transformer.transform(left, bottom),
                    transformer.transform(right, bottom),
                    transformer.transform(right, top),
                    transformer.transform(left, top),
                ]

                lons = [c[0] for c in corners]
                lats = [c[1] for c in corners]
                min_lon, max_lon = min(lons), max(lons)
                min_lat, max_lat = min(lats), max(lats)

                tx_min, ty_min = latlon_to_tile(max_lat, min_lon, ZOOM)
                tx_max, ty_max = latlon_to_tile(min_lat, max_lon, ZOOM)
                n_x = tx_max - tx_min + 1
                n_y = ty_max - ty_min + 1

                canvas = Image.new("RGB", (n_x * 256, n_y * 256), color=(210, 210, 210))

                for tx in range(tx_min, tx_max + 1):
                    for ty in range(ty_min, ty_max + 1):
                        sub = TILE_SUBDOMAINS[subdomain_idx % len(TILE_SUBDOMAINS)]
                        subdomain_idx += 1

                        tile_img = fetch_tile(ZOOM, tx, ty, sub)
                        if tile_img:
                            canvas.paste(
                                tile_img,
                                ((tx - tx_min) * 256, (ty - ty_min) * 256),
                            )
                        time.sleep(REQUEST_DELAY_SECONDS)

                ox = tx_min * 256.0
                oy = ty_min * 256.0

                crop_left = latlon_to_world_px_x(min_lon, ZOOM) - ox
                crop_right = latlon_to_world_px_x(max_lon, ZOOM) - ox
                crop_top = latlon_to_world_px_y(max_lat, ZOOM) - oy
                crop_bottom = latlon_to_world_px_y(min_lat, ZOOM) - oy

                crop_box = (
                    max(0, int(crop_left)),
                    max(0, int(crop_top)),
                    min(canvas.width, math.ceil(crop_right)),
                    min(canvas.height, math.ceil(crop_bottom)),
                )

                cropped = canvas.crop(crop_box)
                result = cropped.resize(
                    (output_resolution, output_resolution), Image.LANCZOS
                )
                result.save(out_path, "PNG", optimize=False)

                print(f"{label}  ok  ({n_x}×{n_y} source tiles)")

            processed += 1
            tile_entries.append(
                make_entry(filename, left, bottom, right, top, output_resolution)
            )

        except Exception as exc:
            import traceback
            print(f"{label}  FAILED: {exc}")
            traceback.print_exc()
            failed += 1
            failed_ids.append(tile_id)

    settings = {
        "source": args.source,
        "crs": "EPSG:3765" if args.source == "cache" else "EPSG:4326",
        "texture_resolution": output_resolution,
        "file_extension": "png",
        "tile_size_meters": tile_size,
        "max_invalid_ratio": 0.95,
    }
    if args.source == "cache":
        settings["cache_file"] = str(cache_path)
        settings["renderer"] = "download_osm_overview"
        settings["sample_resolution"] = sample_resolution
        settings["downsample_filter"] = (
            "LANCZOS" if sample_resolution != output_resolution else None
        )
    else:
        settings["tile_url"] = TILE_URL
        settings["zoom"] = ZOOM

    meta_out = {
        "settings": settings,
        "tiles": tile_entries,
    }

    meta_out_path = out_dir / "metadata.json"
    with open(meta_out_path, "w", encoding="utf-8") as f:
        json.dump(meta_out, f, indent=2)

    print()
    print("-" * 60)
    print(f"  Processed  : {processed}")
    print(f"  Skipped    : {skipped}")
    print(f"  Failed     : {failed}")
    print(f"  metadata   : {meta_out_path}")
    print("-" * 60)

    if failed_ids:
        print(f"\nFailed tiles ({len(failed_ids)}):")
        for t in failed_ids:
            print(f"  {t}")
        print("\nRe-run with SKIP_EXISTING = True to retry only failed tiles.")

    if tile_entries:
        print(f"\nDone. Place '{args.out}' in your dataset root and "
              "scan in ZG Connect to use this basemap.")


# ── Slippy tile math ───────────────────────────────────────────────────────────

def latlon_to_tile(lat_deg, lon_deg, zoom):
    """Returns the (tx, ty) slippy tile index containing (lat, lon) at zoom."""
    n = 2 ** zoom
    tx = int((lon_deg + 180.0) / 360.0 * n)
    lat_r = math.radians(lat_deg)
    ty = int((1.0 - math.log(math.tan(lat_r) + 1.0 / math.cos(lat_r)) / math.pi)
             / 2.0 * n)
    return (max(0, min(n - 1, tx)),
            max(0, min(n - 1, ty)))


def latlon_to_world_px_x(lon_deg, zoom):
    """Global pixel X for a longitude (256 px per tile, x increases eastward)."""
    n = 2 ** zoom
    return (lon_deg + 180.0) / 360.0 * n * 256.0


def latlon_to_world_px_y(lat_deg, zoom):
    """Global pixel Y for a latitude (256 px per tile, y increases southward)."""
    n = 2 ** zoom
    lat_r = math.radians(lat_deg)
    return ((1.0 - math.log(math.tan(lat_r) + 1.0 / math.cos(lat_r)) / math.pi)
            / 2.0 * n * 256.0)


# ── Tile fetching ──────────────────────────────────────────────────────────────

def fetch_tile(z, x, y, subdomain):
    """
    Returns a 256×256 RGB PIL image for one slippy tile.
    Reads from disk cache if available, otherwise downloads and caches.
    Returns None if all retries fail.
    """
    cache_path = None
    if TILE_CACHE_DIR:
        cache_path = Path(TILE_CACHE_DIR) / str(z) / str(x) / f"{y}.png"
        if cache_path.exists():
            try:
                return Image.open(cache_path).convert("RGB")
            except Exception:
                pass  # corrupted cache entry — re-download below

    url = (TILE_URL
           .replace("{s}", subdomain)
           .replace("{z}", str(z))
           .replace("{x}", str(x))
           .replace("{y}", str(y)))

    for attempt in range(1, MAX_RETRIES + 1):
        try:
            data = http_get(url)
            img  = Image.open(io.BytesIO(data)).convert("RGB")

            if cache_path:
                cache_path.parent.mkdir(parents=True, exist_ok=True)
                cache_path.write_bytes(data)

            return img

        except Exception as exc:
            if attempt < MAX_RETRIES:
                time.sleep(0.5 * attempt)
            else:
                print(f"\n  [warn] tile {z}/{x}/{y} failed after {MAX_RETRIES} attempts: {exc}")
                return None

    return None


def http_get(url, timeout=30):
    req = urllib.request.Request(
        url,
        headers={
            "User-Agent": "ZGConnect-MapDownloader/1.0",
            "Accept":     "image/png,image/*",
        }
    )
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return resp.read()


# ── Metadata entry ─────────────────────────────────────────────────────────────

def make_entry(filename, left, bottom, right, top, texture_resolution):
    return {
        "texture_file":       filename,
        "left":               left,
        "bottom":             bottom,
        "right":              right,
        "top":                top,
        "texture_resolution": texture_resolution,
        "invalid_ratio":      0.0,
        "status":             "ok",
    }


if __name__ == "__main__":
    main()
