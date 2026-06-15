"""
osm_ortho_splatmap_generator.py

Hybrid splatmap generator: blends OSM semantic polygon classification
with per-pixel ortho photo colour analysis for more natural terrain blending.

Two signals are combined at every pixel:

  OSM signal   — polygon-based semantic classification (forest, asphalt, etc.)
                 Reliable class identity. Hard edges.

  Ortho signal — per-pixel soft classification derived from RGB colour features
                 (normalised green excess, brightness, HSV saturation).
                 Natural variation. Reflects actual ground cover.

The blend is spatially adaptive:
  • Deep inside an OSM polygon → OSM dominates (ORTHO_INFLUENCE weight)
  • Near a polygon edge        → ortho influence rises, feathering the hard cut
  • Outside all OSM polygons   → ortho takes over (ORTHO_INFLUENCE_UNCLASSIFIED)
                                  instead of the blind "everything is grass" fallback

ORTHO_INFLUENCE controls how much ortho modulates the result inside polygons:
  0.0 = pure OSM  (same result as osm_splatmap_generator.py)
  0.2 = 80 % OSM + 20 % ortho  ← recommended starting point
  1.0 = pure ortho colour classification

Output format is identical to osm_splatmap_generator.py — the generated folder
can be imported via ZG Connect Dataset Manager.

Dependencies (add to existing environment):
    pip install osmium shapely Pillow scipy
    (numpy, pyproj, rasterio already present)

Data sources:
    OSM:   https://download.geofabrik.de/europe/croatia-latest.osm.pbf
    Ortho: DGU WMS tiles already downloaded to ORTHO_DIR
"""

import os
import json
import math
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
from scipy.ndimage import distance_transform_edt
from PIL import Image


# ============================================================
# CONFIG
# ============================================================

# Existing tile grid (output of the heightmap pipeline)
TILES_METADATA_JSON = r"D:\_unity projects\ZG Connect\_dataset\hightmaps_raw_1km_1025\metadata.json"

# OSM PBF source
OSM_PBF = r"D:\_unity projects\ZG Connect\_resources\osm\croatia-latest.osm.pbf"

# Ortho metadata + image folder
ORTHO_METADATA_JSON = r"D:\_unity projects\ZG Connect\_dataset\orthophoto_5000_2048\metadata.json"
ORTHO_DIR           = r"D:\_unity projects\ZG Connect\_dataset\orthophoto_5000_2048"

# Output directory (separate from the pure-OSM output)
OUTPUT_DIR = r"D:\_unity projects\ZG Connect\_dataset\osm_ortho_tiled"

# Splatmap resolution per tile in pixels
SPLAT_RESOLUTION = 512

# CRS of the tile coordinates
TILE_CRS = "EPSG:3765"
OSM_CRS  = "EPSG:4326"

# ── Blend weights ─────────────────────────────────────────────────────────────

# Ortho influence deep inside OSM polygons  (0 = pure OSM, 1 = pure ortho)
ORTHO_INFLUENCE = 0.20

# Ortho influence for pixels not covered by any OSM polygon.
# Higher than ORTHO_INFLUENCE because the blind "everything is grass" fallback
# is replaced here by a colour-based classification.
ORTHO_INFLUENCE_UNCLASSIFIED = 1.00

# ── Edge feathering ───────────────────────────────────────────────────────────

# Width of the soft transition zone at OSM polygon edges, in splatmap pixels.
# At SPLAT_RESOLUTION=512 over 1 km: 1 px ≈ 2 m, so 10 px ≈ 20 m transition.
FEATHER_RADIUS_PX = 10

# Road line buffer in WGS84 degrees (~9 m at Croatia's latitude)
ROAD_BUFFER_DEG    = 0.00008

# Pedestrian pathway buffer in WGS84 degrees (~3 m at Croatia's latitude)
PATHWAY_BUFFER_DEG = 0.000028


# ============================================================
# LAYER DEFINITIONS
#
#   splat0.png   R = grass (0)
#                G = forest (1)
#                B = urban_light (2)
#                A = urban_dark (3)
#
#   splat1.png   R = sand (4)
#                G = rock (5)
#                B = asphalt (6)
#                A = dirt (7)  — pedestrian pathways
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
NUM_SPLATMAPS = (NUM_LAYERS + 3) // 4   # 2

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


# ============================================================
# COLOUR PROFILES FOR ORTHO CLASSIFICATION
#
# Each layer is described by Gaussian targets for three features:
#
#   norm_ge      — normalised green excess: (2G−R−B)/(R+G+B)
#                  Range ≈ [−0.5, 0.5].  Vegetation → positive.
#
#   brightness_n — normalised brightness: (R+G+B)/(3·255)
#                  Range [0, 1].  Dark=0, bright=1.
#
#   sat          — HSV saturation: delta(RGB) / max(RGB)
#                  Range [0, 1].  Grey=0, saturated=1.
#
# Format per layer:
#   (ge_center, ge_sigma,  br_center, br_sigma,  sat_center, sat_sigma)
#
# Calibrated for Croatian DGU orthophoto 2022–2023 (EPSG:3765).
# Adjust if results look off on your specific imagery.
# ============================================================

LAYER_COLOR_PROFILES = {
    # layer         ge_c   ge_s   br_c   br_s   sat_c  sat_s
    "grass":       (0.12,  0.10,  0.52,  0.18,  0.28,  0.18),
    "forest":      (0.20,  0.10,  0.35,  0.15,  0.38,  0.18),
    "urban_light": (-0.02, 0.08,  0.68,  0.18,  0.08,  0.09),
    "urban_dark":  (-0.03, 0.08,  0.38,  0.16,  0.10,  0.10),
    "sand":        (0.02,  0.07,  0.72,  0.16,  0.16,  0.12),
    "rock":        (-0.02, 0.07,  0.55,  0.20,  0.07,  0.09),
    "asphalt":     (-0.04, 0.06,  0.40,  0.14,  0.05,  0.07),
    # Dirt paths: earthy/sandy tone — neutral green excess, medium brightness,
    # low-to-moderate saturation.  Footways, tracks, steps.
    "dirt":        (0.00,  0.08,  0.52,  0.20,  0.12,  0.12),
    # water omitted — handled by a separate GameObject
}


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

    # ── Asphalt — polygon areas (parking lots, railway yards) ─
    if landuse in ("railway",):
        candidates.append("asphalt")
    if highway in ("motorway", "trunk", "primary", "secondary",
                   "tertiary", "residential", "service", "unclassified"):
        candidates.append("asphalt")

    # ── Dirt — pedestrian pathways ─────────────────────────────
    if highway in ("footway", "path", "pedestrian", "steps",
                   "track", "bridleway"):
        candidates.append("dirt")

    if not candidates:
        return None

    return max(candidates, key=lambda l: LAYER_PRIORITY.get(l, 0))


# ============================================================
# OSM FEATURE EXTRACTION
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
        except Exception:
            self._error_count += 1

    # ── Ways ──────────────────────────────────────────────────
    def way(self, w):
        tags     = {k: v for k, v in w.tags}
        highway  = tags.get("highway",  "")
        waterway = tags.get("waterway", "")

        # Closed way fallback (covers simple closed ways if area() assembler
        # isn't triggered; idempotent if area() also fires).
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
                except Exception:
                    self._error_count += 1
            return   # never also process as a line

        # Open ways → buffered road / pathway lines.
        # Rivers are separate GameObjects — not processed here.
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
        except Exception:
            self._error_count += 1


def extract_osm_features(pbf_path):
    print(f"Reading OSM PBF: {pbf_path}")
    print(f"  (typically 2–5 min for a country-level PBF, ~3–5 GB RAM peak)")

    extractor = OSMExtractor()
    extractor.apply_file(pbf_path, locations=True, idx="flex_mem")

    print(f"  Done.  polygons: {extractor._poly_count:,}  "
          f"lines: {extractor._line_count:,}  "
          f"total: {len(extractor.features):,}  "
          f"errors: {extractor._error_count:,}")

    if extractor._poly_count < 1000:
        print()
        print("  WARNING: low polygon count — multipolygon relations likely missed.")
        print("  Ensure osmium area assembler is active: pip install --upgrade osmium")

    return extractor.features


# ============================================================
# REPROJECTION + SPATIAL INDEX
# ============================================================

def build_spatial_index(features_wgs84):
    transformer = pyproj.Transformer.from_crs(OSM_CRS, TILE_CRS, always_xy=True)

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
# ORTHO LOADING
# ============================================================

def load_ortho_lookup(ortho_metadata_path):
    """
    Reads the ortho metadata.json and builds a lookup dict:
        (left, bottom) → absolute path to the tile PNG

    Both left and bottom are stored as integers for reliable dict keying.
    """
    with open(ortho_metadata_path, encoding="utf-8") as f:
        meta = json.load(f)

    ortho_root = os.path.dirname(ortho_metadata_path)
    lookup = {}

    for tile in meta.get("tiles", []):
        key = (int(tile["left"]), int(tile["bottom"]))
        fname = tile.get("texture_file", "")
        if fname:
            lookup[key] = os.path.join(ortho_root, fname)

    print(f"Ortho lookup built: {len(lookup)} tiles available")
    return lookup


def load_ortho_tile(tile_left, tile_bottom, ortho_lookup):
    """
    Loads the ortho PNG for the given tile and returns a float32 RGB array
    downsampled to (SPLAT_RESOLUTION, SPLAT_RESOLUTION, 3) with values [0, 255].

    Returns None if no ortho is available for this tile.
    """
    key  = (int(tile_left), int(tile_bottom))
    path = ortho_lookup.get(key)

    if path is None or not os.path.isfile(path):
        return None

    img = Image.open(path)

    # Ensure RGB (strip alpha if present)
    if img.mode == "RGBA":
        img = img.convert("RGB")
    elif img.mode != "RGB":
        img = img.convert("RGB")

    # Downsample: 2048 → SPLAT_RESOLUTION using Lanczos for quality
    if img.size != (SPLAT_RESOLUTION, SPLAT_RESOLUTION):
        img = img.resize((SPLAT_RESOLUTION, SPLAT_RESOLUTION), Image.Resampling.LANCZOS)

    arr = np.array(img, dtype=np.float32)   # (H, W, 3), values [0, 255]
    return arr


# ============================================================
# ORTHO COLOUR CLASSIFICATION
# ============================================================

def compute_ortho_affinity(rgb_arr):
    """
    Derives per-layer soft weights from ortho RGB data using Gaussian scoring
    over three perceptual colour features.

    Parameters
    ----------
    rgb_arr : float32 (H, W, 3), values in [0, 255]

    Returns
    -------
    float32 (H, W, NUM_LAYERS)  — weights summing to 1 per pixel.
    """
    eps = 1e-6

    R = rgb_arr[..., 0]
    G = rgb_arr[..., 1]
    B = rgb_arr[..., 2]

    # Feature 1: Normalised green excess [-1, 1]
    # Positive  → vegetation (grass, forest)
    # Negative  → non-vegetation (asphalt, water, urban)
    norm_ge = (2.0 * G - R - B) / (R + G + B + eps)

    # Feature 2: Normalised brightness [0, 1]
    brightness_n = (R + G + B) / (3.0 * 255.0)

    # Feature 3: HSV saturation [0, 1]
    # Grey surfaces (asphalt, rock, urban) → near 0
    # Vegetation, water with sky reflection → higher
    rgb_01 = rgb_arr / 255.0
    c_max  = rgb_01.max(axis=2)
    c_min  = rgb_01.min(axis=2)
    delta  = c_max - c_min
    sat    = np.where(c_max > eps, delta / (c_max + eps), 0.0).astype(np.float32)

    # Gaussian scorer: exp(−½·((x−μ)/σ)²)
    def gauss(x, center, sigma):
        return np.exp(-0.5 * ((x - center) / (sigma + eps)) ** 2)

    scores = np.zeros((rgb_arr.shape[0], rgb_arr.shape[1], NUM_LAYERS), dtype=np.float32)

    for i, layer in enumerate(LAYERS):
        ge_c, ge_s, br_c, br_s, sat_c, sat_s = LAYER_COLOR_PROFILES[layer]
        scores[..., i] = (
            gauss(norm_ge,      ge_c,  ge_s)  *
            gauss(brightness_n, br_c,  br_s)  *
            gauss(sat,          sat_c, sat_s)
        )

    # Normalise so weights sum to 1 per pixel
    total = scores.sum(axis=2, keepdims=True)
    total = np.maximum(total, eps)
    return scores / total


# ============================================================
# EDGE FEATHERING
# ============================================================

def feather_mask(binary_coverage, radius_px):
    """
    Converts a hard binary polygon mask into a soft field using a
    distance transform:

      0.0  — at the polygon edge and outside
      ~1.0 — at radius_px pixels inside the polygon

    Uses a smoothstep curve (3t²−2t³) for a perceptually even ramp.

    Parameters
    ----------
    binary_coverage : float32 (H, W), values ≈ 0 or 1
    radius_px       : int, feathering width in pixels

    Returns
    -------
    float32 (H, W), values in [0, 1]
    """
    binary = binary_coverage > 0.5

    if not binary.any():
        return np.zeros_like(binary_coverage)

    # Distance from the nearest background pixel, measured inside the mask
    dist = distance_transform_edt(binary).astype(np.float32)

    # Normalise to [0, 1] over the feather radius
    t = np.clip(dist / max(radius_px, 1), 0.0, 1.0)

    # Smoothstep: smooth S-curve, zero derivative at t=0 and t=1
    return t * t * (3.0 - 2.0 * t)


# ============================================================
# PER-TILE HYBRID SPLATMAP GENERATION
# ============================================================

def rasterize_tile_hybrid(tile_left, tile_bottom, tile_right, tile_top,
                           features_proj, strtree, ortho_rgb):
    """
    Generates hybrid splatmap arrays for one 1 km tile.

    Algorithm
    ---------
    1. Rasterise OSM polygon/line features into per-layer float masks.
    2. Resolve priority overlaps (highest-priority layer wins).
    3. Record OSM polygon coverage (which pixels have any OSM data).
    4. Compute edge-feathered coverage field via distance transform.
    5. Derive per-layer soft weights from the ortho photo colours.
    6. Blend OSM weights with ortho weights using a spatially adaptive
       blend factor:
         • deep inside polygon  → ORTHO_INFLUENCE (e.g. 20 %)
         • near polygon edge    → ortho weight ramps up toward
                                  ORTHO_INFLUENCE_UNCLASSIFIED
         • outside all polygons → ORTHO_INFLUENCE_UNCLASSIFIED (e.g. 60 %)
    7. Re-normalise and pack into RGBA PNGs.

    Parameters
    ----------
    ortho_rgb : float32 (SPLAT_RESOLUTION, SPLAT_RESOLUTION, 3) or None.
                If None, falls back to pure OSM output (same as
                osm_splatmap_generator.py).

    Returns
    -------
    list of 2 RGBA numpy arrays, each (SPLAT_RESOLUTION, SPLAT_RESOLUTION, 4),
    dtype uint8.
    """
    res      = SPLAT_RESOLUTION
    tile_box = box(tile_left, tile_bottom, tile_right, tile_top)
    tile_tf  = transform_from_bounds(
        tile_left, tile_bottom, tile_right, tile_top, res, res
    )

    # ── 1. OSM rasterisation ──────────────────────────────────
    candidate_idxs = strtree.query(tile_box)
    layer_arrays   = {}   # layer_name → float32[H, W]

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

    # ── 2. Priority resolution (highest first) ────────────────
    sorted_by_priority = sorted(
        LAYER_PRIORITY.keys(),
        key=lambda l: LAYER_PRIORITY[l],
        reverse=True
    )

    cumulative = np.zeros((res, res), dtype=np.float32)
    osm_raw    = {}

    for layer in sorted_by_priority:
        arr = layer_arrays.get(layer, np.zeros((res, res), dtype=np.float32))
        arr = arr * (1.0 - cumulative)    # only unclaimed pixels
        osm_raw[layer] = arr
        np.minimum(cumulative + arr, 1.0, out=cumulative)

    # ── 3. OSM polygon coverage ───────────────────────────────
    # 1 where at least one OSM polygon was rasterised, 0 elsewhere.
    osm_polygon_coverage = cumulative.copy()

    # Grass fallback for the OSM-only signal (fills unclaimed pixels)
    unclaimed           = 1.0 - osm_polygon_coverage
    osm_raw["grass"]    = osm_raw.get("grass", np.zeros((res, res), dtype=np.float32)) + unclaimed

    # Stack OSM weights → (H, W, NUM_LAYERS)
    osm_weights = np.stack(
        [osm_raw.get(l, np.zeros((res, res), dtype=np.float32)) for l in LAYERS],
        axis=-1
    )

    # ── 4. Edge feathering ────────────────────────────────────
    # feathered_coverage is 0 at polygon edges / outside, 1 deep inside.
    # Used to smoothly scale ortho influence: more at edges, less inside.
    feathered_coverage = feather_mask(osm_polygon_coverage, FEATHER_RADIUS_PX)

    # ── 5 & 6. Ortho blend ───────────────────────────────────
    if ortho_rgb is not None:
        ortho_weights = compute_ortho_affinity(ortho_rgb)   # (H, W, NUM_LAYERS)

        # Spatially adaptive ortho blend weight:
        #   deep inside polygon  (feathered_coverage = 1) → ORTHO_INFLUENCE
        #   at / outside edge    (feathered_coverage = 0) → ORTHO_INFLUENCE_UNCLASSIFIED
        ortho_w = (
            feathered_coverage       * ORTHO_INFLUENCE +
            (1.0 - feathered_coverage) * ORTHO_INFLUENCE_UNCLASSIFIED
        )
        ortho_w = ortho_w[..., np.newaxis]   # broadcast over layer axis

        final_weights = (1.0 - ortho_w) * osm_weights + ortho_w * ortho_weights
    else:
        # No ortho available → pure OSM output
        final_weights = osm_weights

    # ── 7. Re-normalise (guard against float drift) ───────────
    total = final_weights.sum(axis=-1, keepdims=True)
    total = np.maximum(total, 1e-6)
    final_weights = final_weights / total

    # ── 8. Pack into RGBA PNGs ────────────────────────────────
    splatmaps = []
    for si in range(NUM_SPLATMAPS):
        rgba = np.zeros((res, res, 4), dtype=np.uint8)
        for ci in range(4):
            layer_idx = si * 4 + ci
            if layer_idx < NUM_LAYERS:
                arr = final_weights[..., layer_idx]
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

    # ── Load tile grid ─────────────────────────────────────────
    print(f"Loading tile grid: {TILES_METADATA_JSON}")
    with open(TILES_METADATA_JSON, encoding="utf-8") as f:
        hm_meta = json.load(f)

    tiles     = hm_meta["tiles"]
    tile_size = hm_meta["settings"]["tile_size_meters"]
    print(f"  {len(tiles)} tiles  |  {tile_size} m  |  CRS: {TILE_CRS}\n")

    # ── Load ortho lookup ─────────────────────────────────────
    print(f"Loading ortho lookup: {ORTHO_METADATA_JSON}")
    ortho_lookup = load_ortho_lookup(ORTHO_METADATA_JSON)
    print()

    # ── Extract + index OSM features ──────────────────────────
    features_wgs84         = extract_osm_features(OSM_PBF)
    features_proj, strtree = build_spatial_index(features_wgs84)
    print()

    # ── Process tiles ──────────────────────────────────────────
    output_tiles   = []
    ortho_hit      = 0
    ortho_miss     = 0

    for i, tile in enumerate(tiles):
        left    = tile["left"]
        bottom  = tile["bottom"]
        right   = tile["right"]
        top     = tile["top"]
        tile_id = f"{left}_{bottom}"

        print(f"[{i + 1:>4}/{len(tiles)}]  {tile_id}", end="  ", flush=True)

        # Load ortho for this tile (returns None if not available)
        ortho_rgb = load_ortho_tile(left, bottom, ortho_lookup)
        if ortho_rgb is not None:
            ortho_hit += 1
            print("ortho ✓", end="  ", flush=True)
        else:
            ortho_miss += 1
            print("ortho ✗ (OSM only)", end="  ", flush=True)

        splatmaps = rasterize_tile_hybrid(
            left, bottom, right, top,
            features_proj, strtree,
            ortho_rgb
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

    # ── Write metadata.json ───────────────────────────────────
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
    print(f"  Tiles processed      : {len(output_tiles)}")
    print(f"  Ortho coverage       : {ortho_hit} / {len(output_tiles)} tiles")
    if ortho_miss:
        print(f"  Tiles without ortho  : {ortho_miss}  (pure OSM fallback)")
    print(f"  Splatmaps / tile     : {NUM_SPLATMAPS}  ({NUM_LAYERS} layers)")
    print(f"  Resolution           : {SPLAT_RESOLUTION}×{SPLAT_RESOLUTION} px")
    print(f"  Ortho influence (in) : {ORTHO_INFLUENCE:.0%}")
    print(f"  Ortho influence (out): {ORTHO_INFLUENCE_UNCLASSIFIED:.0%}")
    print(f"  Edge feather radius  : {FEATHER_RADIUS_PX} px "
          f"(~{FEATHER_RADIUS_PX * 1000 / SPLAT_RESOLUTION:.0f} m)")
    print(f"  Output folder        : {OUTPUT_DIR}")
    print()
    print("NEXT STEPS:")
    print(f"  1. Add terrain textures to: {OUTPUT_DIR}/textures/")
    print(f"     Required: {', '.join(l + '.png' for l in LAYERS)}")
    print( "  2. Tools → ZG Connect → Dataset Import Manager")
    print( "  3. Scan dataset root — osm_ortho_tiled appears as Tiled basemap")
    print()
    print("TUNING:")
    print("  ORTHO_INFLUENCE              — 0.2 is a conservative start;")
    print("                                 raise to 0.4–0.5 for more variation")
    print("  ORTHO_INFLUENCE_UNCLASSIFIED — raise to 0.8+ for better fallback")
    print("                                 in areas OSM doesn't cover")
    print("  FEATHER_RADIUS_PX            — increase for wider edge transitions")
    print("  LAYER_COLOR_PROFILES         — adjust Gaussian centres/sigmas if")
    print("                                 colour classification looks off")
    print("=" * 60)


if __name__ == "__main__":
    main()
