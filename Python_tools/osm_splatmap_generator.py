"""
osm_splatmap_generator.py

Generates per-tile terrain splatmap PNGs from an OSM PBF file,
using the same tile grid as the heightmap pipeline.

Output format matches the ZG Connect tiled basemap metadata schema.
The generated folder can be directly imported via ZG Connect Dataset Manager.

New dependencies (add to existing rasterio/numpy environment):
    pip install osmium shapely Pillow
    (pyproj already installed as rasterio dependency)

OSM data source:
    https://download.geofabrik.de/europe/croatia-latest.osm.pbf
"""

import os
import json
import numpy as np
from pathlib import Path

import osmium
import osmium.geom
import shapely.wkb
from shapely.geometry import box, mapping, Polygon
from shapely.strtree import STRtree
from shapely.ops import transform as shapely_transform
import pyproj
from rasterio.features import rasterize
from rasterio.transform import from_bounds as transform_from_bounds
from PIL import Image


# ============================================================
# CONFIG
# ============================================================

# Existing tile grid (output of the heightmap pipeline)
TILES_METADATA_JSON = r"D:\_unity projects\ZG Connect\_dataset\hightmaps_raw_1km_1025\metadata.json"

# OSM PBF source — download from https://download.geofabrik.de/europe/croatia-latest.osm.pbf
OSM_PBF = r"D:\_unity projects\ZG Connect\_resources\osm\croatia-latest.osm.pbf"

# Where to write the output (will be created if it doesn't exist)
OUTPUT_DIR = r"D:\_unity projects\ZG Connect\_dataset\osm_tiled"

# Splatmap resolution per tile in pixels.
# 512 → ~2 m/pixel at 1 km tile  (good balance of detail vs file size)
# 256 → ~4 m/pixel               (faster, smaller files)
# 1024 → ~1 m/pixel              (sharp, but slow and large)
SPLAT_RESOLUTION = 512

# CRS of the tile coordinates — must match heightmap pipeline
TILE_CRS = "EPSG:3765"

# OSM native CRS (always WGS84)
OSM_CRS = "EPSG:4326"


# ============================================================
# LAYER DEFINITIONS
#
# 8 semantic layers packed into 2 RGBA splatmap PNGs:
#
#   splat0.png   R = grass (0)
#                G = forest (1)
#                B = urban_light (2)
#                A = urban_dark (3)
#
#   splat1.png   R = water (4)
#                G = sand (5)
#                B = rock (6)
#                A = asphalt (7)
#
# Layer textures are NOT generated here — supply your own PNG files
# to OUTPUT_DIR/textures/ before running the Unity import.
# ============================================================

LAYERS = [
    "grass",        # 0  splat0.R
    "forest",       # 1  splat0.G
    "urban_light",  # 2  splat0.B
    "urban_dark",   # 3  splat0.A
    "sand",         # 4  splat1.R
    "rock",         # 5  splat1.G
    "asphalt",      # 6  splat1.B
    "dirt",         # 7  splat1.A  — pedestrian pathways (footways, tracks, steps)
]

NUM_LAYERS    = len(LAYERS)
NUM_SPLATMAPS = (NUM_LAYERS + 3) // 4  # 2

# Layer texture tiling sizes in metres (repeating tile pattern scale)
LAYER_TILE_SIZES = {
    "grass":       4.0,
    "forest":      8.0,
    "urban_light": 6.0,
    "urban_dark":  6.0,
    "sand":        4.0,
    "rock":        8.0,
    "asphalt":     3.0,
    "dirt":        2.0,
}

# Priority for overlap resolution — higher value wins.
# dirt < asphalt so roads overwrite paths at intersections.
LAYER_PRIORITY = {
    "grass":       1,
    "dirt":        2,
    "asphalt":     3,
    "sand":        4,
    "rock":        5,
    "urban_light": 6,
    "urban_dark":  7,
    "forest":      8,
}

# Road line buffer in WGS84 degrees (~9 m at Croatia's latitude)
ROAD_BUFFER_DEG    = 0.00008

# Pedestrian pathway buffer in WGS84 degrees (~3 m at Croatia's latitude)
PATHWAY_BUFFER_DEG = 0.000028


# ============================================================
# OSM TAG → LAYER MAPPING
# ============================================================

def classify_tags(tags):
    """
    Maps an OSM feature's tags to one of the 8 layer names, or None.
    Returns the highest-priority matching layer.
    """
    landuse  = tags.get("landuse",  "")
    natural_ = tags.get("natural",  "")
    waterway = tags.get("waterway", "")
    highway  = tags.get("highway",  "")
    leisure  = tags.get("leisure",  "")

    candidates = []

    # Water is handled by a separate GameObject — not a terrain layer.

    # ── Forest / vegetation ────────────────────────────────────
    if landuse in ("forest", "wood"):
        candidates.append("forest")
    if natural_ in ("wood", "forest", "scrub"):
        candidates.append("forest")

    # ── Urban dark (industrial / construction) ─────────────────
    if landuse in ("industrial", "construction", "quarry", "landfill"):
        candidates.append("urban_dark")

    # ── Urban light (residential / commercial) ─────────────────
    if landuse in ("residential", "commercial", "retail", "garages",
                   "religious", "education"):
        candidates.append("urban_light")
    if leisure in ("stadium", "sports_centre"):
        candidates.append("urban_light")

    # ── Rock ───────────────────────────────────────────────────
    if natural_ in ("rock", "bare_rock", "scree", "cliff", "shingle"):
        candidates.append("rock")

    # ── Sand / beach ───────────────────────────────────────────
    if natural_ in ("beach", "sand", "dune"):
        candidates.append("sand")
    if landuse == "beach":
        candidates.append("sand")

    # ── Grass / farmland (lowest priority) ────────────────────
    if landuse in ("grass", "meadow", "farmland", "farmyard",
                   "orchard", "vineyard", "allotments",
                   "recreation_ground", "village_green",
                   "plant_nursery", "cemetery"):
        candidates.append("grass")
    if natural_ in ("grassland", "heath", "fell"):
        candidates.append("grass")
    if leisure in ("park", "garden", "golf_course", "pitch"):
        candidates.append("grass")

    # ── Asphalt — polygon areas (parking lots, railway yards) ──
    if landuse in ("railway",):
        candidates.append("asphalt")

    # Road/pathway lines are handled separately in way() as buffered lines
    if highway in ("motorway", "trunk", "primary", "secondary",
                   "tertiary", "residential", "service", "unclassified"):
        candidates.append("asphalt")

    # ── Dirt / pedestrian pathways ─────────────────────────────
    if highway in ("footway", "path", "pedestrian", "steps", "track",
                   "bridleway"):
        candidates.append("dirt")

    if not candidates:
        return None

    return max(candidates, key=lambda l: LAYER_PRIORITY.get(l, 0))


# ============================================================
# OSM FEATURE EXTRACTION  (osmium streaming handler)
# ============================================================

class OSMExtractor(osmium.SimpleHandler):
    """
    Streams through a PBF file.

    area() handles polygon features — both simple closed ways AND multipolygon
    relations assembled by osmium's area assembler (auto-enabled in pyosmium
    when this method is defined). Uses create_multipolygon(), the correct
    WKBFactory method for area objects. This is critical: multipolygon
    relations cover the vast majority of forests, residential zones, parks,
    and water bodies in modern OSM data.

    way() provides a closed-way fallback (captures simple tagged ways in case
    the area assembler version differs) and handles open ways as buffered
    line features (roads, rivers).

    Stores results as list of (shapely_geom_WGS84, layer_name).
    """

    def __init__(self):
        super().__init__()
        self._wkb         = osmium.geom.WKBFactory()
        self.features     = []   # (shapely_geom_wgs84, layer_name)
        self._poly_count  = 0
        self._line_count  = 0
        self._error_count = 0

    # ── Polygon areas ─────────────────────────────────────────
    def area(self, a):
        tags  = {k: v for k, v in a.tags}
        layer = classify_tags(tags)
        if layer is None:
            return
        try:
            wkb  = self._wkb.create_multipolygon(a)
            geom = shapely.wkb.loads(wkb, hex=False)
            if not geom.is_valid:
                geom = geom.buffer(0)
            if geom.is_valid and not geom.is_empty:
                self.features.append((geom, layer))
                self._poly_count += 1
        except Exception as e:
            self._error_count += 1

    # ── Ways ──────────────────────────────────────────────────
    def way(self, w):
        tags     = {k: v for k, v in w.tags}
        highway  = tags.get("highway",  "")
        waterway = tags.get("waterway", "")

        # ── Closed way → polygon fallback ─────────────────────
        # Covers simple closed ways in case area() assembler isn't triggered
        # (idempotent if area() also fires — np.maximum makes duplicates safe).
        if w.is_closed() and len(w.nodes) >= 4:
            layer = classify_tags(tags)
            if layer is not None:
                try:
                    wkb  = self._wkb.create_linestring(w)
                    line = shapely.wkb.loads(wkb, hex=False)
                    geom = Polygon(list(line.coords))
                    if not geom.is_valid:
                        geom = geom.buffer(0)
                    if geom.is_valid and not geom.is_empty:
                        self.features.append((geom, layer))
                        self._poly_count += 1
                except Exception as e:
                    self._error_count += 1
            return  # never treat a closed way as a line

        # ── Open way → buffered line feature ──────────────────
        if highway in ("motorway", "trunk", "primary", "secondary",
                       "tertiary", "residential", "service", "unclassified"):
            layer  = "asphalt"
            buffer = ROAD_BUFFER_DEG
        elif highway in ("footway", "path", "pedestrian", "steps",
                         "track", "bridleway"):
            layer  = "dirt"
            buffer = PATHWAY_BUFFER_DEG
        else:
            return

        try:
            wkb  = self._wkb.create_linestring(w)
            geom = shapely.wkb.loads(wkb, hex=False)
            if geom.is_valid and not geom.is_empty:
                self.features.append((geom.buffer(buffer), layer))
                self._line_count += 1
        except Exception as e:
            self._error_count += 1


def extract_osm_features(pbf_path):
    """
    Reads the PBF file and returns all extracted features.
    locations=True resolves node coordinates (required for geometry).
    idx='flex_mem' keeps node locations in RAM (fastest, ~3-5 GB for Croatia).
    For memory-constrained machines use: idx='sparse_file_array,/tmp/osm_nodes'
    """
    print(f"Reading OSM PBF: {pbf_path}")
    print(f"  (typically 2–5 min for a country-level PBF, ~3-5 GB RAM peak)")

    extractor = OSMExtractor()
    extractor.apply_file(pbf_path, locations=True, idx="flex_mem")

    poly_src = "area()+way() fallback"
    print(f"  Done.  polygons: {extractor._poly_count:,}  [{poly_src}]  "
          f"lines: {extractor._line_count:,}  "
          f"total: {len(extractor.features):,}  "
          f"errors: {extractor._error_count:,}")

    if extractor._poly_count < 1000:
        print()
        print("  WARNING: low polygon count — multipolygon relations likely missed.")
        print("  Most forests/residential zones in OSM are relations, not simple ways.")
        print("  Ensure osmium area assembler is active:  pip install --upgrade osmium")
        print("  If count stays low, consider pre-converting via ogr2ogr:")
        print("    ogr2ogr -f GPKG output.gpkg croatia-latest.osm.pbf multipolygons")

    return extractor.features


# ============================================================
# REPROJECTION + SPATIAL INDEX
# ============================================================

def build_spatial_index(features_wgs84):
    """
    Reprojects all features from WGS84 to TILE_CRS (EPSG:3765).
    Builds an STRtree for fast per-tile bbox queries.
    Returns (features_proj, strtree).
    """
    transformer = pyproj.Transformer.from_crs(
        OSM_CRS, TILE_CRS, always_xy=True
    )

    def reproject(geom):
        return shapely_transform(transformer.transform, geom)

    print(f"Reprojecting {len(features_wgs84):,} features → {TILE_CRS} ...")
    features_proj = []
    errors = 0

    for geom_wgs, layer in features_wgs84:
        try:
            geom_proj = reproject(geom_wgs)
            if geom_proj.is_valid and not geom_proj.is_empty:
                features_proj.append((geom_proj, layer))
        except Exception:
            errors += 1

    print(f"  {len(features_proj):,} valid features  |  {errors} errors skipped")

    geoms = [f[0] for f in features_proj]
    tree  = STRtree(geoms)
    return features_proj, tree


# ============================================================
# PER-TILE SPLATMAP GENERATION
# ============================================================

def rasterize_tile(tile_left, tile_bottom, tile_right, tile_top,
                   features_proj, strtree):
    """
    Generates splatmap arrays for one 1 km tile.

    Returns list of 2 RGBA numpy arrays [splat0, splat1],
    each shape (SPLAT_RESOLUTION, SPLAT_RESOLUTION, 4), dtype uint8.
    """
    res        = SPLAT_RESOLUTION
    tile_box   = box(tile_left, tile_bottom, tile_right, tile_top)
    tile_tf    = transform_from_bounds(
        tile_left, tile_bottom, tile_right, tile_top, res, res
    )

    # Query spatial index — returns indices of candidate features
    candidate_idxs = strtree.query(tile_box)

    # Rasterize each layer independently
    layer_arrays = {}   # layer_name → float32[H, W]

    for idx in candidate_idxs:
        geom, layer = features_proj[idx]
        if not geom.intersects(tile_box):
            continue

        clipped = geom.intersection(tile_box)
        if clipped.is_empty:
            continue

        if layer not in layer_arrays:
            layer_arrays[layer] = np.zeros((res, res), dtype=np.float32)

        try:
            burned = rasterize(
                [(mapping(clipped), 1.0)],
                out_shape=(res, res),
                transform=tile_tf,
                fill=0.0,
                dtype=np.float32,
            )
            np.maximum(layer_arrays[layer], burned, out=layer_arrays[layer])
        except Exception:
            pass

    # ── Priority resolution ────────────────────────────────────
    # Process highest priority first. Each layer claims only pixels not yet
    # taken by something more important (arr * (1 - cumulative)).
    # Grass fallback fills whatever nothing else claimed.
    sorted_by_priority = sorted(
        LAYER_PRIORITY.keys(),
        key=lambda l: LAYER_PRIORITY[l],
        reverse=True
    )

    cumulative = np.zeros((res, res), dtype=np.float32)
    final = {}

    for layer in sorted_by_priority:
        arr = layer_arrays.get(layer, np.zeros((res, res), dtype=np.float32))
        arr = arr * (1.0 - cumulative)      # only unclaimed pixels
        final[layer] = arr
        np.minimum(cumulative + arr, 1.0, out=cumulative)

    # ── Fallback: unclaimed pixels → grass ────────────────────
    unclaimed = 1.0 - cumulative
    final["grass"] = final.get("grass", np.zeros((res, res), dtype=np.float32)) + unclaimed

    # ── Pack into RGBA PNGs ────────────────────────────────────
    splatmaps = []
    for si in range(NUM_SPLATMAPS):
        rgba = np.zeros((res, res, 4), dtype=np.uint8)
        for ci in range(4):
            layer_idx = si * 4 + ci
            if layer_idx < NUM_LAYERS:
                arr = final.get(LAYERS[layer_idx],
                                np.zeros((res, res), dtype=np.float32))
                rgba[:, :, ci] = (np.clip(arr, 0.0, 1.0) * 255).astype(np.uint8)
        splatmaps.append(rgba)

    return splatmaps


# ============================================================
# MAIN
# ============================================================

def main():
    os.makedirs(OUTPUT_DIR, exist_ok=True)
    os.makedirs(os.path.join(OUTPUT_DIR, "tiles"),    exist_ok=True)
    os.makedirs(os.path.join(OUTPUT_DIR, "textures"), exist_ok=True)

    # ── Load tile grid from existing heightmap metadata ────────
    print(f"Loading tile grid: {TILES_METADATA_JSON}")
    with open(TILES_METADATA_JSON, encoding="utf-8") as f:
        hm_meta = json.load(f)

    tiles     = hm_meta["tiles"]
    tile_size = hm_meta["settings"]["tile_size_meters"]
    print(f"  {len(tiles)} tiles  |  {tile_size} m  |  CRS: {TILE_CRS}\n")

    # ── Extract + index OSM features ──────────────────────────
    features_wgs84         = extract_osm_features(OSM_PBF)
    features_proj, strtree = build_spatial_index(features_wgs84)
    print()

    # ── Process tiles ──────────────────────────────────────────
    output_tiles = []

    for i, tile in enumerate(tiles):
        left   = tile["left"]
        bottom = tile["bottom"]
        right  = tile["right"]
        top    = tile["top"]
        tile_id = f"{left}_{bottom}"

        print(f"[{i + 1:>4}/{len(tiles)}]  {tile_id}", end="  ", flush=True)

        splatmaps = rasterize_tile(
            left, bottom, right, top,
            features_proj, strtree
        )

        splat_files = []
        for si, rgba in enumerate(splatmaps):
            fname = f"{tile_id}_splat{si}.png"
            fpath = os.path.join(OUTPUT_DIR, "tiles", fname)
            Image.fromarray(rgba, mode="RGBA").save(fpath, compress_level=6)
            splat_files.append(f"tiles/{fname}")
            print(f"splat{si} ✓", end="  ", flush=True)

        print()
        output_tiles.append({
            "left":      left,
            "bottom":    bottom,
            "right":     right,
            "top":       top,
            "splatmaps": splat_files,
        })

    # ── Write tiled metadata.json (ZG Connect import format) ──
    out_meta = {
        "type":     "tiled",
        "settings": {
            "tile_size_meters": tile_size,
            "crs":              TILE_CRS,
        },
        "layers": [
            {
                "id":               layer,
                "texture_file":     f"textures/{layer}.png",
                "tile_size_meters": LAYER_TILE_SIZES[layer],
            }
            for layer in LAYERS
        ],
        "tiles": output_tiles,
    }

    meta_path = os.path.join(OUTPUT_DIR, "metadata.json")
    with open(meta_path, "w", encoding="utf-8") as f:
        json.dump(out_meta, f, indent=2, ensure_ascii=False)

    # ── Summary ────────────────────────────────────────────────
    print()
    print("=" * 60)
    print("DONE")
    print(f"  Tiles processed : {len(output_tiles)}")
    print(f"  Splatmaps/tile  : {NUM_SPLATMAPS}  ({NUM_LAYERS} layers)")
    print(f"  Resolution      : {SPLAT_RESOLUTION}×{SPLAT_RESOLUTION} px")
    print(f"  Output folder   : {OUTPUT_DIR}")
    print()
    print("NEXT STEPS:")
    print(f"  1. Add terrain textures to: {OUTPUT_DIR}/textures/")
    print(f"     Required files: {', '.join(l + '.png' for l in LAYERS)}")
    print( "  2. Open Unity → Tools → ZG Connect → Dataset Import Manager")
    print( "  3. Scan dataset root folder — the osm_tiled subfolder")
    print( "     will appear as a Tiled basemap source")
    print("=" * 60)


if __name__ == "__main__":
    main()
