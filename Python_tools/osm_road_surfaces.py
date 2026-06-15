#!/usr/bin/env python3
"""
ZG Connect — prebaked road surface meshes from per-tile road segment JSON.

Pipeline (regional):
  1. Load all {tile}_roads.json centerlines
  2. Stitch segments by osm_way_id across tile boundaries
  3. Buffer each way → boolean union per material group → fillet
  4. Clip unified surfaces per 1 km tile
  5. Sample terrain height from UInt16 RAW heightmaps (Unity-compatible Y)
  6. Build vertically extruded slab meshes (top + thick bottom + side walls)
  7. Optional flat sidewalks along asphalt edges (--sidewalks)
  8. Export per-tile GLB + {tile}_roads_surface.json metadata

Requirements:
    pip install shapely numpy mapbox-earcut

Korištenje:

    # Prvi run (~15 min za regionalni union) + export svih pločica
    python osm_road_surfaces.py \\
        --metadata "_dataset/hightmaps_raw_1km_1025/metadata.json" \\
        --roads-dir "_dataset/road_segments" \\
        --heightmap-dir "_dataset/hightmaps_raw_1km_1025" \\
        --out "_dataset/road_surfaces"

    # Ponovni export (brzo, koristi .surface_cache u --out)
    python osm_road_surfaces.py --out "_dataset/road_surfaces" --skip-existing

    # Jedna pločica
    python osm_road_surfaces.py --tile 473000_5080000 --out "_dataset/road_surfaces"

    # Prilagodba ceste (slab, fillet, kvaliteta mesha)
    python osm_road_surfaces.py --tile 473000_5080000 \\
        --fillet-m 2.0 --surface-offset-m 0.05 --slab-thickness-m 0.20 \\
        --buffer-join 3 --simplify-m 0.15 --max-slope 0.35 --height-smooth-iters 2

    # Ceste + pločnici (asphalt + sidewalk materijali u GLB-u)
    python osm_road_surfaces.py --out "_dataset/road_surfaces" --sidewalks \\
        --sidewalk-width-m 1.5 --road-slab-thickness-m 0.35

    # Jedna pločica s pločnikom
    python osm_road_surfaces.py --tile 473000_5080000 --sidewalks \\
        --sidewalk-width-m 1.5 --out "_dataset/road_surfaces"

    # Forsiraj rebuild regionalnog union/fillet cachea
    # (obavezno nakon promjene --fillet-m ili --buffer-join)
    python osm_road_surfaces.py --refresh-surfaces \\
        --metadata "_dataset/hightmaps_raw_1km_1025/metadata.json" \\
        --roads-dir "_dataset/road_segments" \\
        --heightmap-dir "_dataset/hightmaps_raw_1km_1025" \\
        --out "_dataset/road_surfaces"

Izlaz po pločici:
  {tile_id}_roads.glb           — mesh (asphalt, dirt, opcionalno sidewalk)
  {tile_id}_roads_surface.json  — vertex/triangle counts, bake parametri

Regionalni union/fillet se cacheira u {out}/.surface_cache/
(prvi put ~15 min, zatim ~10 min za svih 858 pločica). Pločnici ne utjecu na cache.

Parametri kvalitete mesha (defaulti v2):
  --buffer-join 3           bevel joins (izbjegava mitre spikeove)
  --simplify-m 0.15         manje sliver trikuta prije earcut (asphalt/dirt)
  --max-slope 0.35          ogranicava twist visine izmedju susjeda (asphalt/dirt)
  --height-smooth-iters 2   glatko sampled Y po prstenu (asphalt/dirt)
  --min-triangle-area 0.02  odbacuje degenerirane trokute
  --road-slab-thickness-m 0.35  deblji vertikalni zidovi ceste (anti z-fight)
  --edge-segment-m 2.0          subdivizija rubova (gladke krivine, manje artefakata)

Parametri pločnika (defaulti):
  --sidewalks               ukljuci ravne pločnike uz asphalt (bez rampinga)
  --sidewalk-width-m 1.5    sirina pločnika prema van od ruba ceste
  --slab-thickness-m 0.20   debljina slab-a pločnika (i dirt traka)
  Unutarnji rub pločnika dijeli tocne verteks visine s asphalt rubom (nema razmaka).
  Pločnik se ne generira gdje asphalt graniči s dirtom niti na tile rubovima.
"""

from __future__ import annotations

import argparse
import json
import math
import struct
import sys
import time
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Any

import mapbox_earcut as earcut
import numpy as np
from shapely import make_valid, wkb
from shapely.geometry import GeometryCollection, LineString, MultiPolygon, Polygon, box
from shapely.ops import unary_union

# Optional defaults when running without CLI
HEIGHTMAP_METADATA = r"D:\_unity projects\ZG Connect\_dataset\hightmaps_raw_1km_1025\metadata.json"
ROADS_DIR = r"D:\_unity projects\ZG Connect\_dataset\road_segments"
HEIGHTMAP_DIR = r"D:\_unity projects\ZG Connect\_dataset\hightmaps_raw_1km_1025"
OUTPUT_DIR = r"D:\_unity projects\ZG Connect\_dataset\road_surfaces"

DEFAULT_CRS = "EPSG:3765"
SURFACE_JSON_VERSION = 2
COORD_TOLERANCE_M = 0.05
MIN_POLYGON_AREA_M2 = 1.0
DEFAULT_SIMPLIFY_M = 0.15
DEFAULT_MAX_SLOPE = 0.35
DEFAULT_HEIGHT_SMOOTH_ITERS = 2
DEFAULT_MIN_TRIANGLE_AREA_M2 = 0.02
TILE_EDGE_TOLERANCE_M = 0.08
DEFAULT_ROAD_SLAB_THICKNESS_M = 0.35
DEFAULT_SIDEWALK_WIDTH_M = 1.5
MIN_SIDEWALK_AREA_M2 = 0.5
DEFAULT_EDGE_SEGMENT_M = 2.0

# Shapely buffer join styles: 1=round, 2=mitre, 3=bevel
BUFFER_JOIN_BEVEL = 3

MATERIAL_COLORS = {
    "asphalt": [0.18, 0.18, 0.18, 1.0],
    "dirt": [0.42, 0.34, 0.24, 1.0],
    "sidewalk": [0.62, 0.60, 0.56, 1.0],
}

ASPHALT_HIGHWAYS = frozenset({
    "motorway", "trunk", "primary", "secondary", "tertiary",
    "unclassified", "residential", "service", "living_street",
    "pedestrian",
})

DIRT_HIGHWAYS = frozenset({
    "footway", "path", "steps", "track", "bridleway", "cycleway",
})

VERBOSE = True


def _ts() -> str:
    return datetime.now().strftime("%H:%M:%S")


def log(msg: str, indent: int = 0) -> None:
    if not VERBOSE:
        return
    print(f"[{_ts()}] {'  ' * indent}{msg}", flush=True)


def log_step(title: str, detail: str = "") -> None:
    line = f"-- {title}"
    if detail:
        line += f" -- {detail}"
    log(line)


def format_duration(seconds: float) -> str:
    if seconds < 60:
        return f"{seconds:.1f}s"
    if seconds < 3600:
        return f"{seconds / 60:.1f}m"
    return f"{seconds / 3600:.1f}h"


def classify_material(highway: str) -> str | None:
    if highway in ASPHALT_HIGHWAYS:
        return "asphalt"
    if highway in DIRT_HIGHWAYS:
        return "dirt"
    return "asphalt"


def epsg_to_unity_xy(easting: float, northing: float, origin_e: int, origin_n: int) -> tuple[float, float]:
    return easting - origin_e, northing - origin_n


def points_close(a: tuple[float, float], b: tuple[float, float], tol: float = COORD_TOLERANCE_M) -> bool:
    return abs(a[0] - b[0]) <= tol and abs(a[1] - b[1]) <= tol


def coord_key(easting: float, northing: float) -> tuple[float, float]:
    return round(float(easting), 3), round(float(northing), 3)


# ── Heightmap sampling (Unity-compatible) ───────────────────────────────────


@dataclass
class HeightmapTile:
    left: int
    bottom: int
    right: int
    top: int
    resolution: int
    normalized: np.ndarray  # GDAL row order: row 0 = north


class HeightmapIndex:
    """Lazy-loaded heightmap RAW cache indexed by (left, bottom)."""

    def __init__(self, metadata_path: Path, raw_dir: Path):
        meta = json.loads(metadata_path.read_text(encoding="utf-8"))
        settings = meta.get("settings") or {}
        self.min_height = float(settings.get("min_height", 95.0))
        self.max_height = float(settings.get("max_height", 1040.0))
        self.terrain_height = float(settings.get("terrain_height", self.max_height - self.min_height))
        self.origin_e = int(settings.get("unity_origin_x", 0))
        self.origin_n = int(settings.get("unity_origin_y", 0))
        self.tile_size = int(settings.get("tile_size_meters", 1000))
        self.default_res = int(settings.get("resolution", 1025))
        self.raw_dir = raw_dir

        self._entries: dict[tuple[int, int], dict] = {}
        self._cache: dict[tuple[int, int], HeightmapTile] = {}
        for t in meta.get("tiles") or []:
            key = (int(t["left"]), int(t["bottom"]))
            self._entries[key] = t

    def tile_key_for_point(self, easting: float, northing: float) -> tuple[int, int] | None:
        e, n = float(easting), float(northing)
        # Half-open intervals; points on max edge belong to the adjacent tile.
        for (left, bottom), entry in self._entries.items():
            right = int(entry.get("right", left + self.tile_size))
            top = int(entry.get("top", bottom + self.tile_size))
            if left <= e < right and bottom <= n < top:
                return left, bottom
        # Fallback: snap to 1 km grid (covers edge cases at dataset bounds).
        left = int(math.floor(e / self.tile_size) * self.tile_size)
        bottom = int(math.floor(n / self.tile_size) * self.tile_size)
        if (left, bottom) in self._entries:
            return left, bottom
        return None

    def _load_tile(self, key: tuple[int, int]) -> HeightmapTile | None:
        if key in self._cache:
            return self._cache[key]
        entry = self._entries.get(key)
        if entry is None:
            return None

        raw_name = entry.get("raw_file")
        if not raw_name:
            return None
        raw_path = self.raw_dir / raw_name
        if not raw_path.is_file():
            log(f"Missing RAW: {raw_path}", 2)
            return None

        res = int(entry.get("heightmap_resolution", self.default_res))
        expected = res * res * 2
        raw_bytes = raw_path.read_bytes()
        if len(raw_bytes) != expected:
            log(f"RAW size mismatch {raw_path.name}: expected {expected}, got {len(raw_bytes)}", 2)
            return None

        arr = np.frombuffer(raw_bytes, dtype="<u2").reshape(res, res).astype(np.float64) / 65535.0
        tile = HeightmapTile(
            left=int(entry["left"]),
            bottom=int(entry["bottom"]),
            right=int(entry.get("right", int(entry["left"]) + self.tile_size)),
            top=int(entry.get("top", int(entry["bottom"]) + self.tile_size)),
            resolution=res,
            normalized=arr,
        )
        self._cache[key] = tile
        return tile

    def sample_normalized(self, easting: float, northing: float) -> float:
        key = self.tile_key_for_point(easting, northing)
        if key is None:
            return 0.0
        tile = self._load_tile(key)
        if tile is None:
            return 0.0

        res = tile.resolution
        span = tile.right - tile.left
        u = (easting - tile.left) / span
        v = (northing - tile.bottom) / span
        u = min(max(u, 0.0), 1.0)
        v = min(max(v, 0.0), 1.0)

        px = u * (res - 1)
        py = (1.0 - v) * (res - 1)  # GDAL: row 0 = north

        x0 = int(math.floor(px))
        y0 = int(math.floor(py))
        x1 = min(x0 + 1, res - 1)
        y1 = min(y0 + 1, res - 1)
        fx = px - x0
        fy = py - y0

        v00 = tile.normalized[y0, x0]
        v10 = tile.normalized[y0, x1]
        v01 = tile.normalized[y1, x0]
        v11 = tile.normalized[y1, x1]
        return float(
            (1 - fx) * (1 - fy) * v00
            + fx * (1 - fy) * v10
            + (1 - fx) * fy * v01
            + fx * fy * v11
        )

    def sample_unity_y(self, easting: float, northing: float) -> float:
        norm = self.sample_normalized(easting, northing)
        height_m = self.min_height + norm * self.terrain_height
        return height_m - self.min_height


# ── Road JSON loading and stitching ──────────────────────────────────────────


@dataclass
class RawSegment:
    osm_way_id: int
    highway: str
    width_m: float
    coords: list[tuple[float, float]]


def load_all_road_segments(roads_dir: Path) -> list[RawSegment]:
    segments: list[RawSegment] = []
    files = sorted(roads_dir.glob("*_roads.json"))
    log(f"Loading road JSON from {roads_dir} ({len(files)} files) ...")
    t0 = time.perf_counter()
    for path in files:
        doc = json.loads(path.read_text(encoding="utf-8"))
        for seg in doc.get("segments") or []:
            pts = seg.get("points_epsg") or []
            if len(pts) < 2:
                continue
            coords = [(float(p[0]), float(p[1])) for p in pts]
            segments.append(
                RawSegment(
                    osm_way_id=int(seg["osm_way_id"]),
                    highway=str(seg.get("highway", "unclassified")),
                    width_m=float(seg.get("width_m", 5.0)),
                    coords=coords,
                )
            )
    log(f"  {len(segments):,} raw segment(s) in {format_duration(time.perf_counter() - t0)}")
    return segments


def chain_segments_for_way(segments: list[RawSegment]) -> list[LineString]:
    if not segments:
        return []

    remaining = list(segments)
    lines: list[LineString] = []

    while remaining:
        seed = remaining.pop(0)
        chain = list(seed.coords)
        changed = True
        while changed:
            changed = False
            for i, seg in enumerate(remaining):
                c = seg.coords
                if len(c) < 2:
                    continue
                if points_close(chain[-1], c[0]):
                    chain.extend(c[1:])
                    remaining.pop(i)
                    changed = True
                    break
                if points_close(chain[-1], c[-1]):
                    chain.extend(reversed(c[:-1]))
                    remaining.pop(i)
                    changed = True
                    break
                if points_close(chain[0], c[-1]):
                    chain = c[:-1] + chain
                    remaining.pop(i)
                    changed = True
                    break
                if points_close(chain[0], c[0]):
                    chain = list(reversed(c[1:])) + chain
                    remaining.pop(i)
                    changed = True
                    break

        if len(chain) >= 2:
            lines.append(LineString(chain))

    return lines


@dataclass
class RoadLine:
    osm_way_id: int
    highway: str
    width_m: float
    material: str
    line: LineString


def stitch_regional_lines(segments: list[RawSegment]) -> list[RoadLine]:
    by_way: dict[int, list[RawSegment]] = {}
    for seg in segments:
        by_way.setdefault(seg.osm_way_id, []).append(seg)

    log_step("Stitch centerlines", f"{len(by_way):,} OSM ways")
    t0 = time.perf_counter()
    roads: list[RoadLine] = []
    for way_id, segs in by_way.items():
        highway = segs[0].highway
        width_m = segs[0].width_m
        material = classify_material(highway)
        if material is None:
            continue
        for ls in chain_segments_for_way(segs):
            if ls.is_empty or ls.length < 0.5:
                continue
            roads.append(
                RoadLine(
                    osm_way_id=way_id,
                    highway=highway,
                    width_m=width_m,
                    material=material,
                    line=ls,
                )
            )
    log(f"  {len(roads):,} stitched line(s) in {format_duration(time.perf_counter() - t0)}")
    return roads


# ── Surface generation ─────────────────────────────────────────────────────────


SURFACE_CACHE_FILES = {
    "asphalt": "regional_asphalt.wkb",
    "dirt": "regional_dirt.wkb",
}


def save_surfaces_cache(out_dir: Path, surfaces: dict[str, Polygon | MultiPolygon | None]) -> None:
    cache_dir = out_dir / ".surface_cache"
    cache_dir.mkdir(parents=True, exist_ok=True)
    for material, geom in surfaces.items():
        path = cache_dir / SURFACE_CACHE_FILES[material]
        if geom is None or geom.is_empty:
            path.write_bytes(b"")
        else:
            path.write_bytes(wkb.dumps(geom))


def surface_cache_settings(args: argparse.Namespace) -> dict:
    return {
        "fillet_m": float(args.fillet_m),
        "buffer_join": int(args.buffer_join),
        "simplify_m": float(args.simplify_m),
    }


def load_surfaces_cache(out_dir: Path, settings: dict) -> dict[str, Polygon | MultiPolygon | None] | None:
    cache_dir = out_dir / ".surface_cache"
    meta_path = cache_dir / "cache_meta.json"
    if not meta_path.is_file():
        return None
    meta = json.loads(meta_path.read_text(encoding="utf-8"))
    cached = meta.get("settings") or meta
    for key, value in settings.items():
        if key not in cached:
            return None
        if isinstance(value, float):
            if abs(float(cached[key]) - value) > 1e-9:
                return None
        elif cached[key] != value:
            return None

    surfaces: dict[str, Polygon | MultiPolygon | None] = {}
    for material, filename in SURFACE_CACHE_FILES.items():
        path = cache_dir / filename
        if not path.is_file():
            return None
        blob = path.read_bytes()
        if not blob:
            surfaces[material] = None
        else:
            surfaces[material] = wkb.loads(blob)
    return surfaces


def write_surfaces_cache_meta(out_dir: Path, settings: dict) -> None:
    cache_dir = out_dir / ".surface_cache"
    cache_dir.mkdir(parents=True, exist_ok=True)
    (cache_dir / "cache_meta.json").write_text(
        json.dumps({"settings": settings}, indent=2),
        encoding="utf-8",
    )


def build_material_surfaces(
    roads: list[RoadLine],
    material: str,
    fillet_m: float,
    buffer_join: int = BUFFER_JOIN_BEVEL,
) -> Polygon | MultiPolygon | None:
    strips: list[Polygon] = []
    for rw in roads:
        if rw.material != material:
            continue
        half = rw.width_m * 0.5
        if half <= 0:
            continue
        # Bevel/round joins avoid self-intersecting mitre spikes on tight corners.
        geom = rw.line.buffer(half, cap_style=2, join_style=buffer_join)
        if geom.is_empty:
            continue
        if geom.geom_type == "Polygon":
            strips.append(geom)
        elif geom.geom_type == "MultiPolygon":
            strips.extend(geom.geoms)

    if not strips:
        return None

    log(f"  Union {len(strips):,} {material} strip(s) ...", 2)
    merged = unary_union(strips)
    if merged.is_empty:
        return None

    merged = make_valid(merged.buffer(0))
    if fillet_m > 0:
        merged = merged.buffer(fillet_m, join_style=1).buffer(-fillet_m, join_style=1)
        merged = make_valid(merged.buffer(0))

    if merged.geom_type == "Polygon" and merged.area < MIN_POLYGON_AREA_M2:
        return None
    if merged.geom_type == "GeometryCollection":
        polys = [g for g in merged.geoms if isinstance(g, Polygon) and g.area >= MIN_POLYGON_AREA_M2]
        if not polys:
            return None
        merged = unary_union(polys)
    return merged


def iter_polygons(geom: Polygon | MultiPolygon | GeometryCollection) -> list[Polygon]:
    if geom is None or geom.is_empty:
        return []
    if geom.geom_type == "Polygon":
        return [geom] if geom.area >= MIN_POLYGON_AREA_M2 else []
    if geom.geom_type == "MultiPolygon":
        return [g for g in geom.geoms if not g.is_empty and g.area >= MIN_POLYGON_AREA_M2]
    if isinstance(geom, GeometryCollection):
        out: list[Polygon] = []
        for g in geom.geoms:
            out.extend(iter_polygons(g))
        return out
    return []


def clip_surface_to_tile(surface: Polygon | MultiPolygon, bounds: tuple[float, float, float, float]) -> list[Polygon]:
    tile_box = box(*bounds)
    clipped = surface.intersection(tile_box)
    if clipped.is_empty:
        return []
    return iter_polygons(clipped)


def build_sidewalk_zones_2d(
    prepared_asphalt: Polygon,
    sidewalk_width_m: float,
    dirt_polys: list[Polygon],
) -> list[Polygon]:
    """Flat sidewalk ring: buffer(prepared asphalt) minus asphalt, minus dirt overlap."""
    if sidewalk_width_m <= 0 or prepared_asphalt.is_empty:
        return []

    outer = prepared_asphalt.buffer(sidewalk_width_m, join_style=1)
    zone = outer.difference(prepared_asphalt)
    if zone.is_empty:
        return []

    polys = iter_polygons(make_valid(zone.buffer(0)))
    polys = [p for p in polys if p.area >= MIN_SIDEWALK_AREA_M2]
    if not polys or not dirt_polys:
        return polys

    dirt_mask = unary_union(dirt_polys)
    if dirt_mask.is_empty:
        return polys

    cleaned: list[Polygon] = []
    for poly in polys:
        diff = poly.difference(dirt_mask)
        if diff.is_empty:
            continue
        cleaned.extend(
            p for p in iter_polygons(make_valid(diff.buffer(0))) if p.area >= MIN_SIDEWALK_AREA_M2
        )
    return cleaned


def subtract_asphalt_from_dirt(
    asphalt_polys: list[Polygon],
    dirt_polys: list[Polygon],
) -> list[Polygon]:
    if not dirt_polys:
        return []
    if not asphalt_polys:
        return dirt_polys
    asphalt_mask = unary_union(asphalt_polys)
    if asphalt_mask.is_empty:
        return dirt_polys
    cleaned: list[Polygon] = []
    for poly in dirt_polys:
        diff = poly.difference(asphalt_mask)
        if diff.is_empty:
            continue
        cleaned.extend(iter_polygons(make_valid(diff.buffer(0))))
    return cleaned


def ring_perimeter(coords: list[tuple[float, float]]) -> float:
    total = 0.0
    for i in range(len(coords)):
        x0, y0 = coords[i]
        x1, y1 = coords[(i + 1) % len(coords)]
        total += math.hypot(x1 - x0, y1 - y0)
    return total


def densify_ring(
    coords: list[tuple[float, float]],
    max_segment_m: float,
) -> list[tuple[float, float]]:
    if max_segment_m <= 0 or len(coords) < 2:
        return coords
    out: list[tuple[float, float]] = []
    for i in range(len(coords)):
        x0, y0 = coords[i]
        x1, y1 = coords[(i + 1) % len(coords)]
        out.append((x0, y0))
        seg_len = math.hypot(x1 - x0, y1 - y0)
        if seg_len <= max_segment_m:
            continue
        steps = int(math.ceil(seg_len / max_segment_m))
        for step in range(1, steps):
            t = step / steps
            out.append((x0 + (x1 - x0) * t, y0 + (y1 - y0) * t))
    return out


def densify_polygon(poly: Polygon, max_segment_m: float) -> Polygon:
    if max_segment_m <= 0:
        return poly
    exterior = densify_ring(list(poly.exterior.coords)[:-1], max_segment_m)
    holes = [densify_ring(list(ring.coords)[:-1], max_segment_m) for ring in poly.interiors]
    result = Polygon(exterior, holes)
    result = make_valid(result.buffer(0))
    if isinstance(result, Polygon):
        return result
    if isinstance(result, MultiPolygon) and len(result.geoms) > 0:
        return max(result.geoms, key=lambda g: g.area)
    return poly


def resample_closed_ring(
    coords: list[tuple[float, float]],
    target_count: int,
) -> list[tuple[float, float]]:
    if len(coords) < 2 or target_count < 3:
        return coords
    if len(coords) == target_count:
        return coords

    seg_lens: list[float] = []
    total = 0.0
    for i in range(len(coords)):
        x0, y0 = coords[i]
        x1, y1 = coords[(i + 1) % len(coords)]
        length = math.hypot(x1 - x0, y1 - y0)
        seg_lens.append(length)
        total += length
    if total < 1e-6:
        return coords

    out: list[tuple[float, float]] = []
    for k in range(target_count):
        dist = (k / target_count) * total
        acc = 0.0
        for i, length in enumerate(seg_lens):
            if acc + length >= dist or i == len(seg_lens) - 1:
                x0, y0 = coords[i]
                x1, y1 = coords[(i + 1) % len(coords)]
                local = 0.0 if length < 1e-9 else (dist - acc) / length
                local = min(max(local, 0.0), 1.0)
                out.append((x0 + (x1 - x0) * local, y0 + (y1 - y0) * local))
                break
            acc += length
    return out


def interpolate_y_on_ring(
    easting: float,
    northing: float,
    ring: list[tuple[float, float]],
    ring_y: np.ndarray,
) -> float:
    if len(ring) == 0:
        return 0.0
    if len(ring) == 1:
        return float(ring_y[0])

    best_dist_sq = float("inf")
    best_y = float(ring_y[0])
    px, py = float(easting), float(northing)
    for i in range(len(ring)):
        x0, y0 = ring[i]
        x1, y1 = ring[(i + 1) % len(ring)]
        dx = x1 - x0
        dy = y1 - y0
        seg_len_sq = dx * dx + dy * dy
        if seg_len_sq < 1e-12:
            t = 0.0
        else:
            t = ((px - x0) * dx + (py - y0) * dy) / seg_len_sq
            t = min(max(t, 0.0), 1.0)
        proj_x = x0 + dx * t
        proj_y = y0 + dy * t
        dist_sq = (px - proj_x) ** 2 + (py - proj_y) ** 2
        y0v = float(ring_y[i])
        y1v = float(ring_y[(i + 1) % len(ring)])
        y_val = y0v + (y1v - y0v) * t
        if dist_sq < best_dist_sq:
            best_dist_sq = dist_sq
            best_y = y_val
    return best_y


def compute_exterior_y_profile(
    prepared: Polygon,
    height_index: HeightmapIndex,
    surface_offset_m: float,
    max_slope: float,
    height_smooth_iters: int,
) -> tuple[list[tuple[float, float]], np.ndarray]:
    ring = list(prepared.exterior.coords)[:-1]
    if len(ring) < 3:
        return [], np.zeros(0, dtype=np.float32)

    verts = np.array(ring, dtype=np.float64)
    top_y = np.zeros(len(ring), dtype=np.float32)
    for i, (e, n) in enumerate(ring):
        top_y[i] = height_index.sample_unity_y(e, n) + surface_offset_m

    top_y = stabilize_surface_heights(
        top_y, verts, [len(ring)], max_slope, height_smooth_iters
    ).astype(np.float32)
    return ring, top_y


def prepare_polygon_for_mesh(poly: Polygon, simplify_m: float) -> Polygon | None:
    geom = poly
    if simplify_m > 0:
        geom = geom.simplify(simplify_m, preserve_topology=True)
    geom = make_valid(geom.buffer(0))
    if isinstance(geom, GeometryCollection):
        polys = [g for g in geom.geoms if isinstance(g, Polygon) and g.area >= MIN_POLYGON_AREA_M2]
        if not polys:
            return None
        geom = max(polys, key=lambda g: g.area)
    if not isinstance(geom, Polygon) or geom.is_empty or geom.area < MIN_POLYGON_AREA_M2:
        return None
    return geom


def is_tile_clip_edge(
    p0: tuple[float, float],
    p1: tuple[float, float],
    bounds: tuple[float, float, float, float],
    tol: float = TILE_EDGE_TOLERANCE_M,
) -> bool:
    """True when an exterior edge lies on the 1 km tile cut boundary (not a real road edge)."""
    left, bottom, right, top = bounds
    e0, n0 = p0
    e1, n1 = p1
    on_left = abs(e0 - left) < tol and abs(e1 - left) < tol
    on_right = abs(e0 - right) < tol and abs(e1 - right) < tol
    on_bottom = abs(n0 - bottom) < tol and abs(n1 - bottom) < tol
    on_top = abs(n0 - top) < tol and abs(n1 - top) < tol
    return on_left or on_right or on_bottom or on_top


def smooth_ring_values(values: np.ndarray, start: int, end: int, iterations: int) -> None:
    n = end - start
    if n < 3 or iterations <= 0:
        return
    segment = values[start:end].copy()
    for _ in range(iterations):
        prev = segment.copy()
        for i in range(n):
            segment[i] = 0.25 * prev[(i - 1) % n] + 0.5 * prev[i] + 0.25 * prev[(i + 1) % n]
    values[start:end] = segment


def clamp_ring_slope(
    values: np.ndarray,
    verts2d: np.ndarray,
    start: int,
    end: int,
    max_slope: float,
    passes: int = 4,
) -> None:
    n = end - start
    if n < 2 or max_slope <= 0:
        return
    for _ in range(passes):
        for i in range(n):
            j = (i + 1) % n
            idx_i = start + i
            idx_j = start + j
            de = float(verts2d[idx_j, 0] - verts2d[idx_i, 0])
            dn = float(verts2d[idx_j, 1] - verts2d[idx_i, 1])
            horiz = math.hypot(de, dn)
            if horiz < 1e-4:
                continue
            max_dy = horiz * max_slope
            dy = float(values[idx_j] - values[idx_i])
            if abs(dy) <= max_dy:
                continue
            mid = (float(values[idx_i]) + float(values[idx_j])) * 0.5
            sign = 1.0 if dy > 0 else -1.0
            values[idx_i] = mid - max_dy * 0.5 * sign
            values[idx_j] = mid + max_dy * 0.5 * sign


def stabilize_surface_heights(
    y: np.ndarray,
    verts2d: np.ndarray,
    ring_ends: list[int],
    max_slope: float,
    smooth_iters: int,
    exterior_only: bool = False,
) -> np.ndarray:
    out = y.copy()
    if exterior_only and ring_ends:
        end = ring_ends[0]
        if end >= 2:
            smooth_ring_values(out, 0, end, smooth_iters)
            clamp_ring_slope(out, verts2d, 0, end, max_slope)
        return out

    start = 0
    for end in ring_ends:
        if end - start >= 2:
            smooth_ring_values(out, start, end, smooth_iters)
            clamp_ring_slope(out, verts2d, start, end, max_slope)
        start = end
    return out


def compute_exterior_top_y_map(
    prepared: Polygon,
    height_index: HeightmapIndex,
    surface_offset_m: float,
    max_slope: float,
    height_smooth_iters: int,
) -> dict[tuple[float, float], float]:
    """Road-edge Y values keyed by EPSG coordinate (for sidewalk inner-ring locking)."""
    verts2d, _, ring_ends = triangulate_polygon_2d(prepared)
    if len(verts2d) == 0 or not ring_ends:
        return {}

    top_y = np.zeros(len(verts2d), dtype=np.float32)
    for i, (e, n) in enumerate(verts2d):
        top_y[i] = height_index.sample_unity_y(e, n) + surface_offset_m

    top_y = stabilize_surface_heights(
        top_y, verts2d, ring_ends, max_slope, height_smooth_iters
    ).astype(np.float32)

    ext_end = ring_ends[0]
    return {
        coord_key(float(verts2d[i, 0]), float(verts2d[i, 1])): float(top_y[i])
        for i in range(ext_end)
    }


# ── Mesh building ──────────────────────────────────────────────────────────────


@dataclass
class MeshData:
    name: str
    positions: np.ndarray  # (N, 3) float32
    normals: np.ndarray    # (N, 3) float32
    indices: np.ndarray    # (M, 3) uint32


def triangulate_polygon_2d(poly: Polygon) -> tuple[np.ndarray, np.ndarray, list[int]]:
    rings: list[list[tuple[float, float]]] = [list(poly.exterior.coords)[:-1]]
    for interior in poly.interiors:
        rings.append(list(interior.coords)[:-1])

    coords_2d: list[tuple[float, float]] = []
    ring_ends: list[int] = []
    for ring in rings:
        if len(ring) < 3:
            continue
        coords_2d.extend(ring)
        ring_ends.append(len(coords_2d))

    if len(coords_2d) < 3:
        return np.zeros((0, 2), dtype=np.float64), np.zeros(0, dtype=np.uint32), []

    verts = np.array(coords_2d, dtype=np.float64)
    ring_ends_arr = np.array(ring_ends, dtype=np.uint32)
    indices = earcut.triangulate_float64(verts, ring_ends_arr)
    return verts, indices.astype(np.uint32), ring_ends


def cull_degenerate_triangles(mesh: MeshData, min_area_m2: float) -> MeshData | None:
    if min_area_m2 <= 0 or len(mesh.indices) < 3:
        return mesh
    pos = mesh.positions
    kept: list[int] = []
    for tri in mesh.indices.reshape(-1, 3):
        a, b, c = pos[tri[0]], pos[tri[1]], pos[tri[2]]
        area = 0.5 * float(np.linalg.norm(np.cross(b - a, c - a)))
        if area >= min_area_m2:
            kept.extend(int(x) for x in tri)
    if not kept:
        return None
    return MeshData(
        name=mesh.name,
        positions=mesh.positions,
        normals=mesh.normals,
        indices=np.array(kept, dtype=np.uint32),
    )


def build_slab_mesh(
    poly: Polygon,
    height_index: HeightmapIndex,
    surface_offset_m: float,
    slab_thickness_m: float,
    material_name: str,
    tile_bounds: tuple[float, float, float, float] | None = None,
    simplify_m: float = DEFAULT_SIMPLIFY_M,
    max_slope: float = DEFAULT_MAX_SLOPE,
    height_smooth_iters: int = DEFAULT_HEIGHT_SMOOTH_ITERS,
    min_triangle_area_m2: float = DEFAULT_MIN_TRIANGLE_AREA_M2,
    prepared: Polygon | None = None,
    inner_y_lock: dict[tuple[float, float], float] | None = None,
    stabilize_exterior_only: bool = False,
) -> MeshData | None:
    if prepared is None:
        prepared = prepare_polygon_for_mesh(poly, simplify_m)
    if prepared is None:
        return None

    return _build_slab_mesh_from_prepared(
        prepared,
        height_index,
        surface_offset_m,
        slab_thickness_m,
        material_name,
        tile_bounds,
        max_slope,
        height_smooth_iters,
        min_triangle_area_m2,
        inner_y_lock,
        stabilize_exterior_only,
    )


def _build_slab_mesh_from_prepared(
    prepared: Polygon,
    height_index: HeightmapIndex,
    surface_offset_m: float,
    slab_thickness_m: float,
    material_name: str,
    tile_bounds: tuple[float, float, float, float] | None,
    max_slope: float,
    height_smooth_iters: int,
    min_triangle_area_m2: float,
    inner_y_lock: dict[tuple[float, float], float] | None,
    stabilize_exterior_only: bool,
) -> MeshData | None:
    verts2d, top_indices, ring_ends = triangulate_polygon_2d(prepared)
    if len(verts2d) == 0 or len(top_indices) == 0:
        return None

    n_verts = len(verts2d)
    inner_start = ring_ends[0] if ring_ends else n_verts

    top_y = np.zeros(n_verts, dtype=np.float32)
    for i, (e, n) in enumerate(verts2d):
        key = coord_key(e, n)
        if inner_y_lock is not None and i >= inner_start and key in inner_y_lock:
            top_y[i] = inner_y_lock[key]
        else:
            top_y[i] = height_index.sample_unity_y(e, n) + surface_offset_m

    if inner_y_lock is not None:
        top_y = stabilize_surface_heights(
            top_y,
            verts2d,
            ring_ends,
            max_slope,
            height_smooth_iters,
            exterior_only=True,
        ).astype(np.float32)
        for i in range(inner_start, n_verts):
            key = coord_key(float(verts2d[i, 0]), float(verts2d[i, 1]))
            if key in inner_y_lock:
                top_y[i] = inner_y_lock[key]
    else:
        top_y = stabilize_surface_heights(
            top_y, verts2d, ring_ends, max_slope, height_smooth_iters
        ).astype(np.float32)

    top_positions = np.zeros((n_verts, 3), dtype=np.float32)
    for i, (e, n) in enumerate(verts2d):
        ux, uz = epsg_to_unity_xy(e, n, height_index.origin_e, height_index.origin_n)
        top_positions[i] = (ux, top_y[i], uz)

    bottom_positions = top_positions.copy()
    bottom_positions[:, 1] -= slab_thickness_m

    # Vertical side walls on exterior ring; skip tile clip cuts.
    exterior = list(prepared.exterior.coords)[:-1]
    edge_quads: list[tuple[int, int, int, int]] = []
    ext_count = len(exterior)

    for i in range(ext_count):
        a = i
        b = (i + 1) % ext_count
        if a == b:
            continue
        p0 = (float(exterior[a][0]), float(exterior[a][1]))
        p1 = (float(exterior[b][0]), float(exterior[b][1]))
        if tile_bounds is not None and is_tile_clip_edge(p0, p1, tile_bounds):
            continue
        edge_quads.append((a, b, b + n_verts, a + n_verts))

    all_positions = np.vstack([top_positions, bottom_positions])

    side_indices: list[int] = []
    for a, b, c, d in edge_quads:
        side_indices.extend([a, b, c, a, c, d])

    top_tris = top_indices.reshape(-1, 3)
    bottom_tris_rev = (top_tris[:, ::-1] + n_verts).reshape(-1)
    all_indices = np.concatenate([top_indices, bottom_tris_rev, np.array(side_indices, dtype=np.uint32)])

    all_normals = assign_flat_face_normals(all_positions, all_indices)

    mesh = MeshData(
        name=material_name,
        positions=all_positions,
        normals=all_normals,
        indices=all_indices,
    )
    return cull_degenerate_triangles(mesh, min_triangle_area_m2)


def assign_flat_face_normals(positions: np.ndarray, indices: np.ndarray) -> np.ndarray:
    normals = np.zeros_like(positions)
    for tri in indices.reshape(-1, 3):
        i0, i1, i2 = int(tri[0]), int(tri[1]), int(tri[2])
        v0 = positions[i0]
        v1 = positions[i1]
        v2 = positions[i2]
        e1 = v1 - v0
        e2 = v2 - v0
        n = np.cross(e1, e2)
        length = float(np.linalg.norm(n))
        if length < 1e-12:
            n = np.array([0.0, 1.0, 0.0], dtype=np.float32)
        else:
            n = (n / length).astype(np.float32)
        normals[i0] = n
        normals[i1] = n
        normals[i2] = n
    return normals


def build_sidewalk_strip_mesh(
    sw_prepared: Polygon,
    road_ring: list[tuple[float, float]],
    road_ring_y: np.ndarray,
    height_index: HeightmapIndex,
    slab_thickness_m: float,
    tile_bounds: tuple[float, float, float, float] | None,
    edge_segment_m: float,
    min_triangle_area_m2: float,
) -> MeshData | None:
    """Flat sidewalk strip: inner ring locked to road edge Y, outer ring shares same Y."""
    if not sw_prepared.interiors or len(road_ring) < 3 or len(road_ring_y) < 3:
        return None

    inner_ring = list(sw_prepared.interiors[0].coords)[:-1]
    outer_ring = list(sw_prepared.exterior.coords)[:-1]
    if len(inner_ring) < 3 or len(outer_ring) < 3:
        return None

    perim = ring_perimeter(inner_ring)
    n = max(8, int(math.ceil(perim / max(edge_segment_m, 0.5))))
    inner_rs = resample_closed_ring(inner_ring, n)
    outer_rs = resample_closed_ring(outer_ring, n)

    inner_y = np.array(
        [interpolate_y_on_ring(e, nor, road_ring, road_ring_y) for e, nor in inner_rs],
        dtype=np.float32,
    )
    outer_y = inner_y.copy()

    top_inner = np.zeros((n, 3), dtype=np.float32)
    top_outer = np.zeros((n, 3), dtype=np.float32)
    for i, (e, nor) in enumerate(inner_rs):
        ux, uz = epsg_to_unity_xy(e, nor, height_index.origin_e, height_index.origin_n)
        top_inner[i] = (ux, inner_y[i], uz)
    for i, (e, nor) in enumerate(outer_rs):
        ux, uz = epsg_to_unity_xy(e, nor, height_index.origin_e, height_index.origin_n)
        top_outer[i] = (ux, outer_y[i], uz)

    bottom_inner = top_inner.copy()
    bottom_inner[:, 1] -= slab_thickness_m
    bottom_outer = top_outer.copy()
    bottom_outer[:, 1] -= slab_thickness_m

    all_positions = np.vstack([top_inner, top_outer, bottom_inner, bottom_outer])

    indices: list[int] = []
    for i in range(n):
        j = (i + 1) % n
        ti, tj = i, j
        to_i, to_j = n + i, n + j
        bi, bj = 2 * n + i, 2 * n + j
        bo_i, bo_j = 3 * n + i, 3 * n + j

        indices.extend([ti, tj, to_j, ti, to_j, to_i])
        indices.extend([bi, bo_i, bo_j, bi, bo_j, bj])

        p0 = (float(outer_rs[i][0]), float(outer_rs[i][1]))
        p1 = (float(outer_rs[j][0]), float(outer_rs[j][1]))
        if tile_bounds is None or not is_tile_clip_edge(p0, p1, tile_bounds):
            indices.extend([to_i, to_j, bo_j, to_i, bo_j, bo_i])

    indices_arr = np.array(indices, dtype=np.uint32)
    normals = assign_flat_face_normals(all_positions, indices_arr)
    mesh = MeshData(
        name="sidewalk",
        positions=all_positions,
        normals=normals,
        indices=indices_arr,
    )
    return cull_degenerate_triangles(mesh, min_triangle_area_m2)


def _accumulate_normal(normals: np.ndarray, positions: np.ndarray, tri: tuple[int, int, int], upward: bool | None) -> None:
    i0, i1, i2 = tri
    v0 = positions[i0]
    v1 = positions[i1]
    v2 = positions[i2]
    e1 = v1 - v0
    e2 = v2 - v0
    n = np.cross(e1, e2)
    length = np.linalg.norm(n)
    if length < 1e-12:
        return
    n = n / length
    if upward is True and n[1] < 0:
        n = -n
    if upward is False and n[1] > 0:
        n = -n
    normals[i0] += n
    normals[i1] += n
    normals[i2] += n


def _normalize_normals(normals: np.ndarray) -> None:
    lengths = np.linalg.norm(normals, axis=1, keepdims=True)
    lengths = np.maximum(lengths, 1e-8)
    normals[:] = normals / lengths


def merge_mesh_data(meshes: list[MeshData]) -> MeshData | None:
    if not meshes:
        return None
    if len(meshes) == 1:
        return meshes[0]

    positions = []
    normals = []
    indices = []
    offset = 0
    for mesh in meshes:
        positions.append(mesh.positions)
        normals.append(mesh.normals)
        indices.append(mesh.indices.reshape(-1, 3) + offset)
        offset += len(mesh.positions)

    return MeshData(
        name=meshes[0].name,
        positions=np.vstack(positions),
        normals=np.vstack(normals),
        indices=np.vstack(indices).astype(np.uint32).reshape(-1),
    )


# ── GLB export (minimal, no external GLTF library) ─────────────────────────────


def write_glb(path: Path, primitives: list[MeshData]) -> None:
    if not primitives:
        raise ValueError("No mesh primitives to export")

    buffer_parts: list[bytes] = []
    gltf_primitives: list[dict] = []
    accessors: list[dict] = []
    buffer_views: list[dict] = []
    byte_offset = 0

    def align4(n: int) -> int:
        return (n + 3) & ~3

    for prim in primitives:
        pos_bytes = prim.positions.astype("<f4").tobytes()
        norm_bytes = prim.normals.astype("<f4").tobytes()
        idx_bytes = prim.indices.astype("<u4").tobytes()

        pos_offset = byte_offset
        buffer_parts.append(pos_bytes)
        byte_offset += len(pos_bytes)
        byte_offset = align4(byte_offset)

        norm_offset = byte_offset
        buffer_parts.append(norm_bytes)
        byte_offset += len(norm_bytes)
        byte_offset = align4(byte_offset)

        idx_offset = byte_offset
        buffer_parts.append(idx_bytes)
        byte_offset += len(idx_bytes)
        byte_offset = align4(byte_offset)

        pos_min = prim.positions.min(axis=0).tolist()
        pos_max = prim.positions.max(axis=0).tolist()

        pos_accessor = len(accessors)
        accessors.append({
            "bufferView": len(buffer_views),
            "componentType": 5126,
            "count": len(prim.positions),
            "type": "VEC3",
            "min": pos_min,
            "max": pos_max,
        })
        buffer_views.append({"buffer": 0, "byteOffset": pos_offset, "byteLength": len(pos_bytes)})

        norm_accessor = len(accessors)
        accessors.append({
            "bufferView": len(buffer_views),
            "componentType": 5126,
            "count": len(prim.normals),
            "type": "VEC3",
        })
        buffer_views.append({"buffer": 0, "byteOffset": norm_offset, "byteLength": len(norm_bytes)})

        idx_accessor = len(accessors)
        accessors.append({
            "bufferView": len(buffer_views),
            "componentType": 5125,
            "count": len(prim.indices),
            "type": "SCALAR",
        })
        buffer_views.append({"buffer": 0, "byteOffset": idx_offset, "byteLength": len(idx_bytes)})

        gltf_primitives.append({
            "attributes": {"POSITION": pos_accessor, "NORMAL": norm_accessor},
            "indices": idx_accessor,
            "material": len(gltf_primitives),
            "mode": 4,
        })

    bin_blob = b""
    for part in buffer_parts:
        bin_blob += part
        pad = (align4(len(bin_blob)) - len(bin_blob)) % 4
        bin_blob += b"\x00" * pad

    def _mat_color(name: str) -> list[float]:
        return MATERIAL_COLORS.get(name, [0.2, 0.2, 0.2, 1.0])

    materials = [
        {
            "name": p.name,
            "pbrMetallicRoughness": {
                "baseColorFactor": _mat_color(p.name),
                "metallicFactor": 0.0,
                "roughnessFactor": 0.9,
            },
        }
        for p in primitives
    ]

    meshes_gltf = [{"name": "road_surface", "primitives": gltf_primitives}]
    nodes = [{"mesh": 0, "name": "road_surface"}]
    scenes = [{"nodes": [0]}]

    gltf = {
        "asset": {"version": "2.0", "generator": "ZGConnect osm_road_surfaces"},
        "scene": 0,
        "scenes": scenes,
        "nodes": nodes,
        "meshes": meshes_gltf,
        "materials": materials,
        "accessors": accessors,
        "bufferViews": buffer_views,
        "buffers": [{"byteLength": len(bin_blob)}],
    }

    json_chunk = json.dumps(gltf, separators=(",", ":")).encode("utf-8")
    json_pad = (4 - (len(json_chunk) % 4)) % 4
    json_chunk += b" " * json_pad

    bin_pad = (4 - (len(bin_blob) % 4)) % 4
    bin_blob += b"\x00" * bin_pad

    total_length = 12 + 8 + len(json_chunk) + 8 + len(bin_blob)
    header = struct.pack("<4sII", b"glTF", 2, total_length)
    json_header = struct.pack("<I4s", len(json_chunk), b"JSON")
    bin_header = struct.pack("<I4s", len(bin_blob), b"BIN\x00")

    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(header + json_header + json_chunk + bin_header + bin_blob)


# ── Tile metadata loading ──────────────────────────────────────────────────────


def load_tile_list(metadata_path: Path, args: argparse.Namespace) -> dict[str, Any]:
    meta = json.loads(metadata_path.read_text(encoding="utf-8"))
    settings = meta.get("settings") or {}
    tiles_out = []
    for t in meta.get("tiles") or []:
        norm = {
            "tile_id": f"{int(t['left'])}_{int(t['bottom'])}",
            "left": int(t["left"]),
            "bottom": int(t["bottom"]),
            "right": int(t.get("right", int(t["left"]) + 1000)),
            "top": int(t.get("top", int(t["bottom"]) + 1000)),
        }
        if args.region_filter:
            if not (
                norm["left"] < args.region_max_e
                and norm["right"] > args.region_min_e
                and norm["bottom"] < args.region_max_n
                and norm["top"] > args.region_min_n
            ):
                continue
        tiles_out.append(norm)

    if args.tile:
        tiles_out = [t for t in tiles_out if t["tile_id"] == args.tile]

    tiles_out.sort(key=lambda t: (t["bottom"], t["left"]))
    return {
        "crs": args.crs or DEFAULT_CRS,
        "tile_size_meters": int(settings.get("tile_size_meters", 1000)),
        "unity_origin_easting": int(settings.get("unity_origin_x", 0)),
        "unity_origin_northing": int(settings.get("unity_origin_y", 0)),
        "tiles": tiles_out,
    }


def tile_bounds(tile: dict) -> tuple[float, float, float, float]:
    return float(tile["left"]), float(tile["bottom"]), float(tile["right"]), float(tile["top"])


def _bake_meta(
    fillet_m: float,
    surface_offset_m: float,
    slab_thickness_m: float,
    simplify_m: float,
    max_slope: float,
    height_smooth_iters: int,
    min_triangle_area_m2: float,
    buffer_join: int = BUFFER_JOIN_BEVEL,
    road_slab_thickness_m: float = DEFAULT_ROAD_SLAB_THICKNESS_M,
    sidewalks: bool = False,
    sidewalk_width_m: float = DEFAULT_SIDEWALK_WIDTH_M,
    edge_segment_m: float = DEFAULT_EDGE_SEGMENT_M,
) -> dict:
    return {
        "fillet_m": fillet_m,
        "surface_offset_m": surface_offset_m,
        "slab_thickness_m": slab_thickness_m,
        "road_slab_thickness_m": road_slab_thickness_m,
        "simplify_m": simplify_m,
        "max_slope": max_slope,
        "height_smooth_iters": height_smooth_iters,
        "min_triangle_area_m2": min_triangle_area_m2,
        "buffer_join": buffer_join,
        "sidewalks": sidewalks,
        "sidewalk_width_m": sidewalk_width_m,
        "edge_segment_m": edge_segment_m,
    }


def process_tile(
    tile: dict,
    surfaces: dict[str, Polygon | MultiPolygon | None],
    height_index: HeightmapIndex,
    out_dir: Path,
    surface_offset_m: float,
    slab_thickness_m: float,
    road_slab_thickness_m: float,
    fillet_m: float,
    simplify_m: float = DEFAULT_SIMPLIFY_M,
    max_slope: float = DEFAULT_MAX_SLOPE,
    height_smooth_iters: int = DEFAULT_HEIGHT_SMOOTH_ITERS,
    min_triangle_area_m2: float = DEFAULT_MIN_TRIANGLE_AREA_M2,
    buffer_join: int = BUFFER_JOIN_BEVEL,
    sidewalks: bool = False,
    sidewalk_width_m: float = DEFAULT_SIDEWALK_WIDTH_M,
    edge_segment_m: float = DEFAULT_EDGE_SEGMENT_M,
) -> dict | None:
    tile_id = tile["tile_id"]
    bounds = tile_bounds(tile)
    primitives: list[MeshData] = []
    poly_count = 0
    sidewalk_poly_count = 0

    asphalt_polys: list[Polygon] = []
    asphalt_surface = surfaces.get("asphalt")
    if asphalt_surface is not None:
        asphalt_polys = clip_surface_to_tile(asphalt_surface, bounds)

    dirt_surface = surfaces.get("dirt")
    dirt_polys: list[Polygon] = []
    if dirt_surface is not None:
        dirt_polys = subtract_asphalt_from_dirt(
            asphalt_polys,
            clip_surface_to_tile(dirt_surface, bounds),
        )

    asphalt_meshes: list[MeshData] = []
    sidewalk_meshes: list[MeshData] = []

    for asphalt_poly in asphalt_polys:
        prepared = prepare_polygon_for_mesh(asphalt_poly, simplify_m)
        if prepared is None:
            continue
        prepared = densify_polygon(prepared, edge_segment_m)
        if prepared is None or prepared.is_empty:
            continue
        poly_count += 1

        road_ring, road_ring_y = compute_exterior_y_profile(
            prepared,
            height_index,
            surface_offset_m,
            max_slope,
            height_smooth_iters,
        )

        asphalt_mesh = build_slab_mesh(
            asphalt_poly,
            height_index,
            surface_offset_m,
            road_slab_thickness_m,
            "asphalt",
            tile_bounds=bounds,
            simplify_m=simplify_m,
            max_slope=max_slope,
            height_smooth_iters=height_smooth_iters,
            min_triangle_area_m2=min_triangle_area_m2,
            prepared=prepared,
        )
        if asphalt_mesh is not None:
            asphalt_meshes.append(asphalt_mesh)

        if sidewalks and sidewalk_width_m > 0 and len(road_ring) >= 3:
            for sw_poly in build_sidewalk_zones_2d(prepared, sidewalk_width_m, dirt_polys):
                sidewalk_poly_count += 1
                sw_prepared = prepare_polygon_for_mesh(sw_poly, 0.0)
                if sw_prepared is None:
                    continue
                sw_prepared = densify_polygon(sw_prepared, edge_segment_m)
                sw_mesh = build_sidewalk_strip_mesh(
                    sw_prepared,
                    road_ring,
                    road_ring_y,
                    height_index,
                    slab_thickness_m,
                    bounds,
                    edge_segment_m,
                    min_triangle_area_m2,
                )
                if sw_mesh is not None:
                    sidewalk_meshes.append(sw_mesh)

    merged_asphalt = merge_mesh_data(asphalt_meshes)
    if merged_asphalt is not None:
        primitives.append(merged_asphalt)

    poly_count += len(dirt_polys)
    dirt_meshes: list[MeshData] = []
    for poly in dirt_polys:
        dirt_prepared = prepare_polygon_for_mesh(poly, simplify_m)
        if dirt_prepared is None:
            continue
        dirt_prepared = densify_polygon(dirt_prepared, edge_segment_m)
        mesh = build_slab_mesh(
            poly,
            height_index,
            surface_offset_m,
            slab_thickness_m,
            "dirt",
            tile_bounds=bounds,
            simplify_m=simplify_m,
            max_slope=max_slope,
            height_smooth_iters=height_smooth_iters,
            min_triangle_area_m2=min_triangle_area_m2,
            prepared=dirt_prepared,
        )
        if mesh is not None:
            dirt_meshes.append(mesh)
    merged_dirt = merge_mesh_data(dirt_meshes)
    if merged_dirt is not None:
        primitives.append(merged_dirt)

    merged_sidewalk = merge_mesh_data(sidewalk_meshes)
    if merged_sidewalk is not None:
        primitives.append(merged_sidewalk)

    glb_path = out_dir / f"{tile_id}_roads.glb"
    meta_path = out_dir / f"{tile_id}_roads_surface.json"

    if not primitives:
        # Write empty metadata so skip-existing works; no GLB.
        meta = {
            "format_version": SURFACE_JSON_VERSION,
            "tile_id": tile_id,
            "left": tile["left"],
            "bottom": tile["bottom"],
            "right": tile["right"],
            "top": tile["top"],
            "has_mesh": False,
            "polygon_count": 0,
            "sidewalk_polygon_count": 0,
            "vertex_count": 0,
            "triangle_count": 0,
            "bake": _bake_meta(
                fillet_m, surface_offset_m, slab_thickness_m,
                simplify_m, max_slope, height_smooth_iters, min_triangle_area_m2, buffer_join,
                road_slab_thickness_m, sidewalks, sidewalk_width_m, edge_segment_m,
            ),
        }
        meta_path.write_text(json.dumps(meta, indent=2), encoding="utf-8")
        if glb_path.is_file():
            glb_path.unlink()
        return meta

    write_glb(glb_path, primitives)
    total_verts = sum(len(p.positions) for p in primitives)
    total_tris = sum(len(p.indices) // 3 for p in primitives)

    meta = {
        "format_version": SURFACE_JSON_VERSION,
        "tile_id": tile_id,
        "left": tile["left"],
        "bottom": tile["bottom"],
        "right": tile["right"],
        "top": tile["top"],
        "has_mesh": True,
        "glb_file": glb_path.name,
        "materials": [p.name for p in primitives],
        "polygon_count": poly_count,
        "sidewalk_polygon_count": sidewalk_poly_count,
        "vertex_count": total_verts,
        "triangle_count": total_tris,
        "bake": _bake_meta(
            fillet_m, surface_offset_m, slab_thickness_m,
            simplify_m, max_slope, height_smooth_iters, min_triangle_area_m2, buffer_join,
            road_slab_thickness_m, sidewalks, sidewalk_width_m, edge_segment_m,
        ),
    }
    meta_path.write_text(json.dumps(meta, indent=2), encoding="utf-8")
    return meta


# ── CLI ────────────────────────────────────────────────────────────────────────


def build_arg_parser() -> argparse.ArgumentParser:
    ap = argparse.ArgumentParser(
        description="ZG Connect — prebaked road surface GLB meshes from road segment JSON.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    ap.add_argument("--metadata", type=Path, help="Heightmap metadata.json")
    ap.add_argument("--roads-dir", type=Path, help="Folder with {tile}_roads.json files")
    ap.add_argument("--heightmap-dir", type=Path, help="Folder with zg_dmr_* RAW heightmaps")
    ap.add_argument("--out", type=Path, help="Output folder for GLB + surface JSON")
    ap.add_argument("--tile", help="Process only this tile_id")
    ap.add_argument("--crs", default=None, help=f"CRS label (default: {DEFAULT_CRS})")
    ap.add_argument("--fillet-m", type=float, default=2.0, help="Junction fillet radius in metres")
    ap.add_argument("--surface-offset-m", type=float, default=0.05, help="Lift above terrain (Unity Y)")
    ap.add_argument(
        "--slab-thickness-m",
        type=float,
        default=0.20,
        help="Vertical slab thickness for dirt/sidewalk (default: 0.20 m)",
    )
    ap.add_argument(
        "--road-slab-thickness-m",
        type=float,
        default=DEFAULT_ROAD_SLAB_THICKNESS_M,
        help="Vertical slab thickness for asphalt road sides (default: 0.35 m)",
    )
    ap.add_argument(
        "--buffer-join",
        type=int,
        default=BUFFER_JOIN_BEVEL,
        choices=[1, 2, 3],
        help="Buffer join style: 1=round, 2=mitre, 3=bevel (default: bevel)",
    )
    ap.add_argument(
        "--simplify-m",
        type=float,
        default=DEFAULT_SIMPLIFY_M,
        help="Douglas-Peucker simplify tolerance before triangulation (0=off)",
    )
    ap.add_argument(
        "--max-slope",
        type=float,
        default=DEFAULT_MAX_SLOPE,
        help="Max rise/run between adjacent ring vertices (prevents bow-tie twist)",
    )
    ap.add_argument(
        "--height-smooth-iters",
        type=int,
        default=DEFAULT_HEIGHT_SMOOTH_ITERS,
        help="Laplacian smoothing passes on ring heights",
    )
    ap.add_argument(
        "--min-triangle-area",
        type=float,
        default=DEFAULT_MIN_TRIANGLE_AREA_M2,
        help="Drop triangles smaller than this area (sq metres, 3D)",
    )
    ap.add_argument(
        "--edge-segment-m",
        type=float,
        default=DEFAULT_EDGE_SEGMENT_M,
        help="Max edge length before subdividing polygon rings (default: 2.0 m)",
    )
    ap.add_argument("--sidewalks", action="store_true", help="Generate flat sidewalks along asphalt edges")
    ap.add_argument(
        "--sidewalk-width-m",
        type=float,
        default=DEFAULT_SIDEWALK_WIDTH_M,
        help="Sidewalk width outward from asphalt edge (default: 1.5 m)",
    )
    ap.add_argument("--skip-existing", action="store_true", help="Skip tile if surface JSON exists")
    ap.add_argument(
        "--refresh-surfaces",
        action="store_true",
        help="Rebuild regional union/fillet cache (default: reuse .surface_cache in --out)",
    )
    ap.add_argument("--quiet", action="store_true", help="Minimal logging")

    reg = ap.add_argument_group("EPSG region filter (optional)")
    reg.add_argument("--region-min-e", type=int, default=None)
    reg.add_argument("--region-max-e", type=int, default=None)
    reg.add_argument("--region-min-n", type=int, default=None)
    reg.add_argument("--region-max-n", type=int, default=None)
    return ap


def apply_region_defaults(args: argparse.Namespace) -> None:
    bounds = [args.region_min_e, args.region_max_e, args.region_min_n, args.region_max_n]
    if any(b is not None for b in bounds):
        if not all(b is not None for b in bounds):
            sys.exit("Region filter requires all four: --region-min-e/max-e/min-n/max-n")
        if args.region_max_e <= args.region_min_e or args.region_max_n <= args.region_min_n:
            sys.exit("Invalid region: max must be greater than min")
        args.region_filter = True
    else:
        args.region_filter = False


def main(argv: list[str] | None = None) -> int:
    ap = build_arg_parser()
    args = ap.parse_args(argv)
    apply_region_defaults(args)

    global VERBOSE
    VERBOSE = not args.quiet

    t_run = time.perf_counter()
    log_step("ZG Connect road surface bake", "starting")

    metadata_path = args.metadata or Path(HEIGHTMAP_METADATA)
    roads_dir = args.roads_dir or Path(ROADS_DIR)
    heightmap_dir = args.heightmap_dir or Path(HEIGHTMAP_DIR)
    out_dir = args.out or Path(OUTPUT_DIR)

    if not metadata_path.is_file():
        sys.exit(f"Metadata not found: {metadata_path}")
    if not roads_dir.is_dir():
        sys.exit(f"Roads dir not found: {roads_dir}")
    if not heightmap_dir.is_dir():
        sys.exit(f"Heightmap dir not found: {heightmap_dir}")

    out_dir.mkdir(parents=True, exist_ok=True)
    log(f"Output: {out_dir.resolve()}", 1)

    cfg = load_tile_list(metadata_path, args)
    tiles = cfg["tiles"]
    if not tiles:
        sys.exit("No tiles to process.")

    log(f"Tiles to export: {len(tiles)}", 1)

    height_index = HeightmapIndex(metadata_path, heightmap_dir)

    cache_settings = surface_cache_settings(args)
    surfaces: dict[str, Polygon | MultiPolygon | None] | None = None
    if not args.refresh_surfaces:
        surfaces = load_surfaces_cache(out_dir, cache_settings)
        if surfaces is not None:
            log("Loaded regional surfaces from cache", 1)

    if surfaces is None:
        raw_segments = load_all_road_segments(roads_dir)
        regional_lines = stitch_regional_lines(raw_segments)

        log_step("Build regional surfaces")
        t_surf = time.perf_counter()
        surfaces = {}
        for material in ("asphalt", "dirt"):
            log(f"Material: {material}", 1)
            surfaces[material] = build_material_surfaces(
                regional_lines, material, args.fillet_m, args.buffer_join
            )
            geom = surfaces[material]
            if geom is None:
                log("  (empty)", 2)
            elif geom.geom_type == "Polygon":
                log(f"  1 polygon, area {geom.area:,.0f} m2", 2)
            else:
                log(f"  {len(geom.geoms)} polygon(s), total area {geom.area:,.0f} m2", 2)
        log(f"Surfaces built in {format_duration(time.perf_counter() - t_surf)}")
        save_surfaces_cache(out_dir, surfaces)
        write_surfaces_cache_meta(out_dir, cache_settings)
        log("Saved regional surfaces to cache", 1)

    exported = 0
    skipped = 0
    empty = 0

    log_step("Export per-tile meshes")
    for i, tile in enumerate(tiles):
        tile_id = tile["tile_id"]
        meta_path = out_dir / f"{tile_id}_roads_surface.json"
        if args.skip_existing and meta_path.is_file():
            skipped += 1
            continue

        if VERBOSE and (i == 0 or (i + 1) % 25 == 0 or i == len(tiles) - 1):
            log(f"Tile {i + 1}/{len(tiles)}: {tile_id}", 1)

        meta = process_tile(
            tile,
            surfaces,
            height_index,
            out_dir,
            args.surface_offset_m,
            args.slab_thickness_m,
            args.road_slab_thickness_m,
            args.fillet_m,
            args.simplify_m,
            args.max_slope,
            args.height_smooth_iters,
            args.min_triangle_area,
            args.buffer_join,
            args.sidewalks,
            args.sidewalk_width_m,
            args.edge_segment_m,
        )
        if meta and meta.get("has_mesh"):
            exported += 1
        else:
            empty += 1

    log_step(
        "Done",
        f"{exported} with mesh, {empty} empty, {skipped} skipped, "
        f"total {format_duration(time.perf_counter() - t_run)}",
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
