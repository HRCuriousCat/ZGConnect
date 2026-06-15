#!/usr/bin/env python3
"""
ZG Connect — per-tile vegetation masks and road segment JSON from one regional OSM download.

Downloads OpenStreetMap once for the union bbox of all tiles (same model as
_dataset/download_osm_overview.py), then rasterizes masks and exports road JSON per tile.

Tile list source (pick one):
  --metadata  Heightmap metadata.json from the dataset folder
  --tiles     Legacy vegetation_tiles.json from Unity Vegetation Tiles Exporter

Outputs per tile:
  {tile_id}_vegetation.png  — linear RGBA (R forest, G park, B grass, A exclusion)
  {tile_id}_roads.json      — highway centerlines for Unity road mesh import (unless --no-roads)

Requirements:
    pip install requests pyproj numpy pillow shapely rasterio scipy

Examples:
    python Tools/osm_vegetation_masks.py \\
        --metadata "_dataset/hightmaps_raw_1km_1025/metadata.json" \\
        --out "_dataset/vegetation_masks"

    python Tools/osm_vegetation_masks.py --metadata ... --out ... --tile 455000_5071000
    python Tools/osm_vegetation_masks.py --metadata ... --out ... \\
        --region-min-e 455000 --region-max-e 457000 --region-min-n 5071000 --region-max-n 5073000

Verbose progress is printed by default ([HH:MM:SS] timestamps). Use --quiet for summary only.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import time
from datetime import datetime
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import numpy as np
import requests
from PIL import Image
from pyproj import Transformer
from rasterio.features import rasterize
from rasterio.transform import from_bounds as transform_from_bounds
from scipy.ndimage import gaussian_filter
from shapely.geometry import LineString, MultiPolygon, Polygon, box, shape
from shapely.ops import unary_union

# Optional default when running without CLI (edit for your machine)
HEIGHTMAP_METADATA = r"D:\_unity projects\ZG Connect\_dataset\hightmaps_raw_1km_1025\metadata.json"
OUTPUT_DIR = r"D:\_unity projects\ZG Connect\_dataset\vegetation_masks"
DEFAULT_CRS = "EPSG:3765"
ROADS_JSON_FORMAT_VERSION = 1
REGIONAL_CACHE_NAME = "osm_overpass_cache.json"

FOREST_TAGS = [
    ("landuse", "forest"),
    ("natural", "wood"),
    ("natural", "scrub"),
]

PARK_TAGS = [
    ("leisure", "park"),
    ("landuse", "recreation_ground"),
    ("landuse", "village_green"),
    ("leisure", "garden"),
    ("leisure", "nature_reserve"),
]

GRASS_TAGS = [
    ("landuse", "grass"),
    ("landuse", "meadow"),
    ("landuse", "greenfield"),
    ("natural", "grassland"),
    ("landuse", "flowerbed"),
]

EXCLUSION_TAGS = [
    ("building", None),
    ("natural", "water"),
    ("waterway", None),
    ("landuse", "residential"),
    ("landuse", "commercial"),
    ("landuse", "industrial"),
    ("amenity", "parking"),
]

HIGHWAY_REGEX = (
    r"^(motorway|trunk|primary|secondary|tertiary|unclassified|residential|"
    r"service|living_street|pedestrian|footway|path|cycleway|track)$"
)

ROAD_BUFFER_M = {
    "motorway": 12.0,
    "trunk": 10.0,
    "primary": 8.0,
    "secondary": 7.0,
    "tertiary": 6.0,
    "residential": 5.0,
    "service": 4.0,
    "footway": 2.0,
    "path": 1.5,
    "default": 5.0,
}

USER_AGENT = "ZGConnect-VegetationMasks/1.0 (local dataset pipeline; +https://www.openstreetmap.org)"

OVERPASS_ENDPOINTS = [
    "https://overpass-api.de/api/interpreter",
    "https://overpass.kumi.systems/api/interpreter",
]

# Set False via --quiet
VERBOSE = True
PARSE_PROGRESS_EVERY = 10_000
TILE_HEARTBEAT_EVERY = 10


def _ts() -> str:
    return datetime.now().strftime("%H:%M:%S")


def log(msg: str, indent: int = 0) -> None:
    if not VERBOSE:
        return
    prefix = "  " * indent
    print(f"[{_ts()}] {prefix}{msg}", flush=True)


def log_step(title: str, detail: str = "") -> None:
    line = f"── {title}"
    if detail:
        line += f" — {detail}"
    log(line)


def format_bytes(n: int) -> str:
    if n < 1024:
        return f"{n} B"
    if n < 1024 * 1024:
        return f"{n / 1024:.1f} KB"
    return f"{n / (1024 * 1024):.2f} MB"


def format_duration(seconds: float) -> str:
    if seconds < 60:
        return f"{seconds:.1f}s"
    if seconds < 3600:
        return f"{seconds / 60:.1f}m"
    return f"{seconds / 3600:.1f}h"


@dataclass
class RegionalGeoms:
    forest: list = field(default_factory=list)
    park: list = field(default_factory=list)
    grass: list = field(default_factory=list)
    exclusion_poly: list = field(default_factory=list)
    exclusion_road: list = field(default_factory=list)


@dataclass
class RoadWay:
    osm_way_id: int
    highway: str
    width_m: float
    line: LineString


# ── Tile config loading ────────────────────────────────────────────────────────


def tile_id_from_bounds(left: int, bottom: int) -> str:
    return f"{left}_{bottom}"


def normalize_tile_entry(raw: dict) -> dict:
    left = int(raw["left"])
    bottom = int(raw["bottom"])
    right = int(raw.get("right", left + 1000))
    top = int(raw.get("top", bottom + 1000))
    return {
        "tile_id": raw.get("tile_id") or tile_id_from_bounds(left, bottom),
        "left": left,
        "bottom": bottom,
        "right": right,
        "top": top,
        "invalid_ratio": float(raw.get("invalid_ratio", 0.0)),
    }


def load_from_heightmap_metadata(path: Path, args: argparse.Namespace) -> dict[str, Any]:
    data = json.loads(path.read_text(encoding="utf-8"))
    settings = data.get("settings") or {}

    tile_size = int(settings.get("tile_size_meters", 1000))
    hm_res = int(settings.get("resolution", 1025))

    if args.resolution is not None:
        mask_res = args.resolution
    else:
        mask_res = max(64, hm_res - 1) if hm_res > 1 else 1024

    crs = args.crs or DEFAULT_CRS

    tiles_out = []
    for t in data.get("tiles") or []:
        norm = normalize_tile_entry(t)
        if not tile_intersects_region(norm, args):
            continue
        if args.max_invalid_ratio is not None:
            if norm["invalid_ratio"] > args.max_invalid_ratio:
                continue
        tiles_out.append(norm)

    tiles_out.sort(key=lambda t: (t["bottom"], t["left"]))
    return {
        "source": str(path),
        "crs": crs,
        "tile_size_meters": tile_size,
        "resolution": mask_res,
        "unity_origin_easting": int(settings.get("unity_origin_x", 0)),
        "unity_origin_northing": int(settings.get("unity_origin_y", 0)),
        "tiles": tiles_out,
    }


def load_from_vegetation_tiles_json(path: Path, args: argparse.Namespace) -> dict[str, Any]:
    data = json.loads(path.read_text(encoding="utf-8"))
    tiles_out = []
    for t in data.get("tiles") or []:
        norm = normalize_tile_entry(t)
        if not tile_intersects_region(norm, args):
            continue
        tiles_out.append(norm)

    return {
        "source": str(path),
        "crs": args.crs or data.get("crs", DEFAULT_CRS),
        "tile_size_meters": int(data.get("tile_size_meters", 1000)),
        "resolution": int(args.resolution or data.get("resolution", 1024)),
        "unity_origin_easting": int(data.get("unity_origin_easting", 0)),
        "unity_origin_northing": int(data.get("unity_origin_northing", 0)),
        "tiles": tiles_out,
    }


def tile_intersects_region(tile: dict, args: argparse.Namespace) -> bool:
    if not args.region_filter:
        return True
    return (
        tile["left"] < args.region_max_e
        and tile["right"] > args.region_min_e
        and tile["bottom"] < args.region_max_n
        and tile["top"] > args.region_min_n
    )


def resolve_tile_id_filter(tile: dict, filter_id: str | None) -> bool:
    if not filter_id:
        return True
    return tile["tile_id"] == filter_id or tile_id_from_bounds(tile["left"], tile["bottom"]) == filter_id


def tiles_union_bounds(tiles: list[dict], tile_size: int) -> tuple[float, float, float, float]:
    """EPSG (min_x, min_y, max_x, max_y) covering all tiles."""
    min_x = min(t["left"] for t in tiles)
    min_y = min(t["bottom"] for t in tiles)
    max_x = max(t.get("right", t["left"] + tile_size) for t in tiles)
    max_y = max(t.get("top", t["bottom"] + tile_size) for t in tiles)
    return float(min_x), float(min_y), float(max_x), float(max_y)


def tile_epsg_bounds(tile: dict, tile_size: int) -> tuple[float, float, float, float]:
    left = int(tile["left"])
    bottom = int(tile["bottom"])
    if "right" in tile and "top" in tile:
        return float(left), float(bottom), float(tile["right"]), float(tile["top"])
    return tile_bounds(left, bottom, tile_size)


# ── Geo / OSM ──────────────────────────────────────────────────────────────────


def tile_bounds(left: int, bottom: int, size: int) -> tuple[float, float, float, float]:
    return float(left), float(bottom), float(left + size), float(bottom + size)


def wgs84_bbox(min_x: float, min_y: float, max_x: float, max_y: float, crs: str):
    to_wgs = Transformer.from_crs(crs, "EPSG:4326", always_xy=True)
    xs = [min_x, max_x, min_x, max_x]
    ys = [min_y, min_y, max_y, max_y]
    lons, lats = to_wgs.transform(xs, ys)
    return min(lats), min(lons), max(lats), max(lons)


def project_geom(geom, crs: str):
    to_proj = Transformer.from_crs("EPSG:4326", crs, always_xy=True)

    def _coord(c):
        x, y = to_proj.transform(c[0], c[1])
        return (x, y)

    def _ring(ring):
        return [_coord(c) for c in ring]

    g = shape(geom)
    if g.geom_type == "Polygon":
        ext = _ring(geom["coordinates"][0])
        holes = [_ring(h) for h in geom["coordinates"][1:]]
        return Polygon(ext, holes)
    if g.geom_type == "LineString":
        return LineString([_coord(c) for c in geom["coordinates"]])
    if g.geom_type == "MultiPolygon":
        polys = []
        for part in geom["coordinates"]:
            ext = _ring(part[0])
            holes = [_ring(h) for h in part[1:]]
            polys.append(Polygon(ext, holes))
        return MultiPolygon(polys)
    return g


def _tag_selector(key: str, value: str | None) -> str:
    return f'["{key}"]' if value is None else f'["{key}"="{value}"]'


def build_overpass_query(south: float, west: float, north: float, east: float) -> str:
    bbox = f"{south},{west},{north},{east}"
    lines = ["[out:json][timeout:180];", "("]

    def add_tags(tag_list, obj: str):
        for key, val in tag_list:
            lines.append(f"  {obj}{_tag_selector(key, val)}({bbox});")

    for obj in ("way", "relation"):
        add_tags(FOREST_TAGS, obj)
        add_tags(PARK_TAGS, obj)
        add_tags(GRASS_TAGS, obj)
        add_tags(EXCLUSION_TAGS, obj)

    lines.append(f'  way["highway"~"{HIGHWAY_REGEX}"]({bbox});')
    lines.append(");")
    lines.append("out geom;")
    return "\n".join(lines)


def overpass_headers() -> dict[str, str]:
    return {
        "User-Agent": USER_AGENT,
        "Accept": "application/json",
        "Content-Type": "application/x-www-form-urlencoded",
        "Accept-Encoding": "gzip, deflate",
    }


def load_regional_overpass_cache(cache_file: Path) -> dict | None:
    if not cache_file.is_file():
        log(f"Cache miss: {cache_file}", 1)
        return None
    size = cache_file.stat().st_size
    log_step("Load Overpass cache", f"{cache_file.name} ({format_bytes(size)})")
    t0 = time.perf_counter()
    log("Reading JSON from disk …", 1)
    text = cache_file.read_text(encoding="utf-8")
    log(f"Read {format_bytes(len(text.encode('utf-8')))} in {format_duration(time.perf_counter() - t0)}", 1)
    t1 = time.perf_counter()
    data = json.loads(text)
    n = len(data.get("elements", []))
    log(f"Parsed JSON: {n:,} elements in {format_duration(time.perf_counter() - t1)}", 1)
    return data


def save_regional_overpass_cache(cache_file: Path, data: dict) -> None:
    cache_file.parent.mkdir(parents=True, exist_ok=True)
    n = len(data.get("elements", []))
    log_step("Save Overpass cache", f"{cache_file} ({n:,} elements)")
    t0 = time.perf_counter()
    log("Serializing JSON …", 1)
    text = json.dumps(data)
    log(f"Serialized {format_bytes(len(text.encode('utf-8')))} in {format_duration(time.perf_counter() - t0)}", 1)
    t1 = time.perf_counter()
    cache_file.write_text(text, encoding="utf-8")
    log(f"Wrote disk in {format_duration(time.perf_counter() - t1)}", 1)


def download_osm_regional(
    south: float,
    west: float,
    north: float,
    east: float,
    endpoints: list[str] | None = None,
) -> dict:
    q = build_overpass_query(south, west, north, east)
    urls = endpoints or OVERPASS_ENDPOINTS
    headers = overpass_headers()
    last_err: Exception | None = None

    log_step(
        "Overpass download",
        f"WGS84 south={south:.5f} west={west:.5f} north={north:.5f} east={east:.5f}",
    )
    log(f"Query size: {len(q)} chars, {len(urls)} endpoint(s)", 1)

    for url_idx, url in enumerate(urls):
        log(f"Endpoint [{url_idx + 1}/{len(urls)}]: {url}", 1)
        for attempt in range(3):
            try:
                log(
                    f"POST attempt {attempt + 1}/3 (timeout 300s — server may take minutes) …",
                    2,
                )
                t0 = time.perf_counter()
                r = requests.post(
                    url,
                    data={"data": q},
                    headers=headers,
                    timeout=300,
                )
                elapsed = time.perf_counter() - t0
                body_mb = format_bytes(len(r.content))
                log(f"Response HTTP {r.status_code}, {body_mb}, {format_duration(elapsed)}", 2)
                if r.status_code == 429:
                    wait = 30 * (attempt + 1)
                    log(f"Rate limited — sleeping {wait}s …", 2)
                    time.sleep(wait)
                    continue
                if r.status_code in (406, 502, 503, 504):
                    snippet = (r.text or "")[:200].replace("\n", " ")
                    last_err = RuntimeError(f"{r.status_code} from {url}: {snippet}")
                    log(f"Server error, trying next endpoint if any: {snippet[:80]}", 2)
                    break
                r.raise_for_status()
                log("Decoding JSON response …", 2)
                t1 = time.perf_counter()
                data = r.json()
                n = len(data.get("elements", []))
                log(f"Decoded {n:,} elements in {format_duration(time.perf_counter() - t1)}", 2)
                return data
            except requests.RequestException as e:
                last_err = e
                log(f"Request failed: {e}", 2)
                if attempt < 2:
                    wait = 5 * (attempt + 1)
                    log(f"Retrying in {wait}s …", 2)
                    time.sleep(wait)
                else:
                    break

    raise RuntimeError(
        f"Overpass request failed after trying {len(urls)} endpoint(s). "
        f"Set USER_AGENT at top of script if blocked. Last error: {last_err}"
    )


def element_matches(tags: dict, rules: list) -> bool:
    for key, val in rules:
        if key not in tags:
            continue
        if val is None or tags.get(key) == val:
            return True
    return False


def parse_regional_osm(osm_json: dict, crs: str) -> tuple[RegionalGeoms, list[RoadWay]]:
    geoms = RegionalGeoms()
    roads: list[RoadWay] = []
    elements = osm_json.get("elements", [])
    total = len(elements)

    log_step("Parse OSM geometries", f"{total:,} elements → {crs}")
    t0 = time.perf_counter()

    for idx, el in enumerate(elements):
        if VERBOSE and total > PARSE_PROGRESS_EVERY and idx > 0 and idx % PARSE_PROGRESS_EVERY == 0:
            pct = 100 * idx // total
            log(
                f"Parse progress {idx:,}/{total:,} ({pct}%) "
                f"— {format_duration(time.perf_counter() - t0)} elapsed",
                1,
            )
        tags = el.get("tags") or {}
        et = el.get("type")

        if et == "way" and "geometry" in el:
            coords = [[p["lon"], p["lat"]] for p in el["geometry"]]
            if len(coords) < 2:
                continue

            if "highway" in tags:
                highway = tags["highway"]
                if not _highway_matches_regex(highway):
                    continue
                buf = ROAD_BUFFER_M.get(highway, ROAD_BUFFER_M["default"])
                ls = project_geom({"type": "LineString", "coordinates": coords}, crs)
                if ls.is_empty:
                    continue
                geoms.exclusion_road.append((ls, buf))
                roads.append(
                    RoadWay(
                        osm_way_id=int(el["id"]),
                        highway=highway,
                        width_m=float(buf),
                        line=ls,
                    )
                )
                continue

            if coords[0] != coords[-1]:
                continue
            g = project_geom({"type": "Polygon", "coordinates": [coords]}, crs)
            if g.is_empty:
                continue

            if element_matches(tags, FOREST_TAGS):
                geoms.forest.append(g)
            elif element_matches(tags, PARK_TAGS):
                geoms.park.append(g)
            elif element_matches(tags, GRASS_TAGS):
                geoms.grass.append(g)
            if element_matches(tags, EXCLUSION_TAGS):
                geoms.exclusion_poly.append(g)

        elif et == "relation" and "members" in el:
            outer_rings = []
            for m in el["members"]:
                if m.get("role") != "outer" or "geometry" not in m:
                    continue
                coords = [[p["lon"], p["lat"]] for p in m["geometry"]]
                if len(coords) >= 4 and coords[0] == coords[-1]:
                    outer_rings.append(coords)
            if not outer_rings:
                continue
            try:
                g = project_geom({"type": "Polygon", "coordinates": outer_rings[:1]}, crs)
            except Exception:
                continue
            if g.is_empty:
                continue

            if element_matches(tags, FOREST_TAGS):
                geoms.forest.append(g)
            elif element_matches(tags, PARK_TAGS):
                geoms.park.append(g)
            elif element_matches(tags, GRASS_TAGS):
                geoms.grass.append(g)
            if element_matches(tags, EXCLUSION_TAGS):
                geoms.exclusion_poly.append(g)

    log(
        f"Parse done in {format_duration(time.perf_counter() - t0)}: "
        f"roads={len(roads):,}, forest={len(geoms.forest):,}, park={len(geoms.park):,}, "
        f"grass={len(geoms.grass):,}, exclusion_poly={len(geoms.exclusion_poly):,}, "
        f"exclusion_road_lines={len(geoms.exclusion_road):,}",
        1,
    )
    return geoms, roads


def _highway_matches_regex(highway: str) -> bool:
    return re.match(HIGHWAY_REGEX, highway) is not None


def _bbox_intersects(
    a_minx: float,
    a_miny: float,
    a_maxx: float,
    a_maxy: float,
    b_minx: float,
    b_miny: float,
    b_maxx: float,
    b_maxy: float,
) -> bool:
    return not (a_maxx < b_minx or a_minx > b_maxx or a_maxy < b_miny or a_miny > b_maxy)


def _clip_polygons_to_tile(polygons: list, tile_box: Polygon) -> list:
    out = []
    minx, miny, maxx, maxy = tile_box.bounds
    for g in polygons:
        if g is None or g.is_empty:
            continue
        gx0, gy0, gx1, gy1 = g.bounds
        if not _bbox_intersects(gx0, gy0, gx1, gy1, minx, miny, maxx, maxy):
            continue
        inter = g.intersection(tile_box)
        if inter.is_empty:
            continue
        if inter.geom_type == "Polygon":
            out.append(inter)
        elif inter.geom_type == "MultiPolygon":
            out.extend(inter.geoms)
        elif inter.geom_type == "GeometryCollection":
            for sub in inter.geoms:
                if sub.geom_type == "Polygon" and not sub.is_empty:
                    out.append(sub)
    return out


def _clip_roads_to_tile(roads: list, tile_box: Polygon) -> list:
    out = []
    minx, miny, maxx, maxy = tile_box.bounds
    for ls, buf in roads:
        if ls.is_empty:
            continue
        lx0, ly0, lx1, ly1 = ls.bounds
        if not _bbox_intersects(lx0, ly0, lx1, ly1, minx, miny, maxx, maxy):
            continue
        inter = ls.intersection(tile_box)
        if inter.is_empty:
            continue
        parts = []
        if inter.geom_type == "LineString":
            parts = [inter]
        elif inter.geom_type == "MultiLineString":
            parts = list(inter.geoms)
        elif inter.geom_type == "GeometryCollection":
            for sub in inter.geoms:
                if sub.geom_type == "LineString":
                    parts.append(sub)
        for part in parts:
            if part.is_empty or len(part.coords) < 2:
                continue
            out.append((part, buf))
    return out


def clip_geoms_for_tile(regional: RegionalGeoms, bounds: tuple[float, float, float, float]) -> dict:
    tile_box = box(*bounds)
    return {
        "forest": _clip_polygons_to_tile(regional.forest, tile_box),
        "park": _clip_polygons_to_tile(regional.park, tile_box),
        "grass": _clip_polygons_to_tile(regional.grass, tile_box),
        "exclusion_poly": _clip_polygons_to_tile(regional.exclusion_poly, tile_box),
        "exclusion_road": _clip_roads_to_tile(regional.exclusion_road, tile_box),
    }


def clip_roads_for_tile(roads: list[RoadWay], bounds: tuple[float, float, float, float]) -> list[dict]:
    tile_box = box(*bounds)
    minx, miny, maxx, maxy = tile_box.bounds
    segments: list[dict] = []

    for rw in roads:
        lx0, ly0, lx1, ly1 = rw.line.bounds
        if not _bbox_intersects(lx0, ly0, lx1, ly1, minx, miny, maxx, maxy):
            continue
        inter = rw.line.intersection(tile_box)
        if inter.is_empty:
            continue

        parts: list[LineString] = []
        if inter.geom_type == "LineString":
            parts = [inter]
        elif inter.geom_type == "MultiLineString":
            parts = list(inter.geoms)
        elif inter.geom_type == "GeometryCollection":
            for sub in inter.geoms:
                if sub.geom_type == "LineString":
                    parts.append(sub)

        for part in parts:
            if part.is_empty or len(part.coords) < 2:
                continue
            segments.append(
                {
                    "osm_way_id": rw.osm_way_id,
                    "highway": rw.highway,
                    "width_m": rw.width_m,
                    "points_epsg": [[float(x), float(y)] for x, y in part.coords],
                }
            )

    return segments


def epsg_to_unity_xy(easting: float, northing: float, origin_e: int, origin_n: int) -> list[float]:
    return [easting - origin_e, northing - origin_n]


def build_roads_tile_json(
    tile: dict,
    segments: list[dict],
    cfg: dict,
    bounds: tuple[float, float, float, float],
) -> dict:
    origin_e = int(cfg.get("unity_origin_easting", 0))
    origin_n = int(cfg.get("unity_origin_northing", 0))

    export_segments = []
    for seg in segments:
        epsg_pts = seg["points_epsg"]
        unity_pts = [epsg_to_unity_xy(p[0], p[1], origin_e, origin_n) for p in epsg_pts]
        export_segments.append(
            {
                "osm_way_id": seg["osm_way_id"],
                "highway": seg["highway"],
                "width_m": seg["width_m"],
                "points_epsg": epsg_pts,
                "points_unity": [[round(u[0], 3), round(u[1], 3)] for u in unity_pts],
            }
        )

    return {
        "format_version": ROADS_JSON_FORMAT_VERSION,
        "tile_id": tile["tile_id"],
        "crs": cfg.get("crs", DEFAULT_CRS),
        "left": int(bounds[0]),
        "bottom": int(bounds[1]),
        "right": int(bounds[2]),
        "top": int(bounds[3]),
        "unity_origin": {"easting": origin_e, "northing": origin_n},
        "segment_count": len(export_segments),
        "segments": export_segments,
    }


# ── Rasterization ──────────────────────────────────────────────────────────────


def make_raster_transform(bounds: tuple[float, float, float, float], w: int, h: int):
    min_x, min_y, max_x, max_y = bounds
    return transform_from_bounds(min_x, min_y, max_x, max_y, w, h)


def rasterize_shapes(geoms: list, transform, w: int, h: int) -> np.ndarray:
    shapes = [(g, 1) for g in geoms if g is not None and not g.is_empty]
    if not shapes:
        return np.zeros((h, w), dtype=np.float32)

    return rasterize(
        shapes,
        out_shape=(h, w),
        transform=transform,
        fill=0,
        dtype=np.float32,
        all_touched=True,
    )


def rasterize_roads(roads: list, transform, w: int, h: int) -> np.ndarray:
    if not roads:
        return np.zeros((h, w), dtype=np.float32)

    by_buf: dict[float, list] = defaultdict(list)
    for ls, buf in roads:
        if not ls.is_empty:
            by_buf[buf].append(ls)

    polys = []
    for buf, lines in by_buf.items():
        if len(lines) == 1:
            polys.append(lines[0].buffer(buf, cap_style=2, join_style=2))
        else:
            merged = unary_union(lines)
            if not merged.is_empty:
                polys.append(merged.buffer(buf, cap_style=2, join_style=2))

    return rasterize_shapes(polys, transform, w, h)


def soften(arr: np.ndarray, sigma_px: float = 1.5) -> np.ndarray:
    if sigma_px <= 0:
        return arr
    return np.clip(gaussian_filter(arr, sigma=sigma_px), 0.0, 1.0).astype(np.float32)


def build_mask(geoms: dict, bounds, res: int, soften_px: float) -> np.ndarray:
    w = h = res
    transform = make_raster_transform(bounds, w, h)

    def _raster_layer(name: str, shapes: list, do_soften: bool) -> np.ndarray:
        if VERBOSE:
            log(
                f"Raster {name}: {len(shapes)} shape(s) @ {w}×{h} …",
                3,
            )
        t = time.perf_counter()
        arr = rasterize_shapes(shapes, transform, w, h)
        if do_soften and soften_px > 0:
            arr = soften(arr, soften_px)
        if VERBOSE:
            log(f"  {name} done in {format_duration(time.perf_counter() - t)}", 3)
        return arr

    forest = _raster_layer("forest", geoms["forest"], True)
    park = _raster_layer("park", geoms["park"], True)
    grass = _raster_layer("grass", geoms["grass"], True)
    excl_poly = _raster_layer("exclusion_poly", geoms["exclusion_poly"], False)

    if VERBOSE:
        log(f"Raster roads: {len(geoms['exclusion_road'])} line(s) (buffer+union) …", 3)
    t_rd = time.perf_counter()
    excl_road = rasterize_roads(geoms["exclusion_road"], transform, w, h)
    if VERBOSE:
        log(f"  roads done in {format_duration(time.perf_counter() - t_rd)}", 3)

    exclusion = np.clip(np.maximum(excl_poly, excl_road), 0.0, 1.0)

    inv = 1.0 - exclusion
    forest *= inv
    park *= inv
    grass *= inv

    rgba = np.zeros((h, w, 4), dtype=np.uint8)
    rgba[..., 0] = (forest * 255).astype(np.uint8)
    rgba[..., 1] = (park * 255).astype(np.uint8)
    rgba[..., 2] = (grass * 255).astype(np.uint8)
    rgba[..., 3] = (exclusion * 255).astype(np.uint8)
    return rgba


def process_tile_outputs(
    tile: dict,
    cfg: dict,
    regional_geoms: RegionalGeoms,
    regional_roads: list[RoadWay],
    mask_dir: Path,
    roads_dir: Path | None,
    soften_px: float,
    write_mask: bool,
    write_roads: bool,
) -> None:
    tile_size = int(cfg.get("tile_size_meters", 1000))
    res = int(cfg.get("resolution", 1024))
    bounds = tile_epsg_bounds(tile, tile_size)
    tile_id = tile["tile_id"]

    t0 = time.perf_counter()
    log("Clip geometries to tile bounds …", 2)
    tile_geoms = clip_geoms_for_tile(regional_geoms, bounds)
    t_clip = time.perf_counter()
    log(
        f"Clipped in {format_duration(t_clip - t0)}: "
        f"forest={len(tile_geoms['forest'])}, park={len(tile_geoms['park'])}, "
        f"grass={len(tile_geoms['grass'])}, excl_poly={len(tile_geoms['exclusion_poly'])}, "
        f"road_lines={len(tile_geoms['exclusion_road'])}",
        2,
    )

    if write_mask:
        log(f"Build vegetation mask {res}×{res} …", 2)
        rgba = build_mask(tile_geoms, bounds, res, soften_px)
        mask_path = mask_dir / f"{tile_id}_vegetation.png"
        t_png = time.perf_counter()
        log(f"Write PNG {mask_path.name} …", 2)
        Image.fromarray(rgba, mode="RGBA").save(mask_path)
        t_mask = time.perf_counter()
        mask_msg = f"mask {format_duration(t_mask - t_clip)} (png {format_duration(t_mask - t_png)})"
    else:
        t_mask = time.perf_counter()
        mask_msg = "mask skip"

    if write_roads and roads_dir is not None:
        log("Clip road centerlines …", 2)
        t_r0 = time.perf_counter()
        segments = clip_roads_for_tile(regional_roads, bounds)
        log(f"  {len(segments)} segment(s) in {format_duration(time.perf_counter() - t_r0)}", 2)
        roads_doc = build_roads_tile_json(tile, segments, cfg, bounds)
        roads_path = roads_dir / f"{tile_id}_roads.json"
        log(f"Write JSON {roads_path.name} …", 2)
        roads_path.write_text(json.dumps(roads_doc, indent=2), encoding="utf-8")
        t_roads = time.perf_counter()
        roads_msg = f"roads {format_duration(t_roads - t_mask)}"
    else:
        t_roads = time.perf_counter()
        roads_msg = "roads skip"

    log(
        f"Tile finished in {format_duration(t_roads - t0)} "
        f"(clip {format_duration(t_clip - t0)}, {mask_msg}, {roads_msg})",
        1,
    )


def build_arg_parser() -> argparse.ArgumentParser:
    ap = argparse.ArgumentParser(
        description="ZG Connect vegetation masks + road JSON from one regional OSM download.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=(
            "Uses one Overpass request for the union of all tile bounds, cached to "
            f"{REGIONAL_CACHE_NAME} under --out (override with --cache-file)."
        ),
    )

    src = ap.add_mutually_exclusive_group()
    src.add_argument("--metadata", type=Path, help="Heightmap metadata.json")
    src.add_argument("--tiles", type=Path, help="Legacy vegetation_tiles.json")

    ap.add_argument("--out", type=Path, help="Output folder for vegetation PNGs")
    ap.add_argument(
        "--roads-out",
        type=Path,
        default=None,
        help="Folder for per-tile road JSON (default: <out>/../road_segments)",
    )
    ap.add_argument("--no-roads", action="store_true", help="Do not write road JSON files")
    ap.add_argument("--tile", help="Process only this tile_id")
    ap.add_argument("--resolution", type=int, help="Mask PNG size")
    ap.add_argument("--crs", default=None, help=f"CRS override (default: {DEFAULT_CRS})")
    ap.add_argument("--max-invalid-ratio", type=float, default=None)
    ap.add_argument("--soften", type=float, default=1.5, help="Edge soften radius in pixels (0=off)")
    ap.add_argument("--skip-existing", action="store_true", help="Skip tile if all requested outputs exist")
    ap.add_argument(
        "--refresh-overpass",
        action="store_true",
        help="Ignore regional Overpass cache and re-download",
    )
    ap.add_argument(
        "--cache-file",
        type=Path,
        default=None,
        help=f"Regional OSM JSON cache (default: <out>/{REGIONAL_CACHE_NAME})",
    )
    ap.add_argument("--user-agent", default=None)
    ap.add_argument("--overpass-url", action="append", dest="overpass_urls")
    ap.add_argument(
        "--quiet",
        action="store_true",
        help="Minimal output (only errors and final summary)",
    )

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


def tile_outputs_complete(
    tile_id: str,
    mask_dir: Path,
    roads_dir: Path | None,
    skip_roads: bool,
) -> bool:
    mask_ok = (mask_dir / f"{tile_id}_vegetation.png").is_file()
    if skip_roads or roads_dir is None:
        return mask_ok
    roads_ok = (roads_dir / f"{tile_id}_roads.json").is_file()
    return mask_ok and roads_ok


def main(argv: list[str] | None = None) -> int:
    ap = build_arg_parser()
    args = ap.parse_args(argv)
    apply_region_defaults(args)

    global USER_AGENT, VERBOSE
    VERBOSE = not args.quiet
    if args.user_agent:
        USER_AGENT = args.user_agent

    overpass_urls = args.overpass_urls if args.overpass_urls else None

    t_run = time.perf_counter()
    log_step("ZG Connect vegetation + roads pipeline", "starting")

    if args.metadata is None and args.tiles is None:
        if Path(HEIGHTMAP_METADATA).is_file():
            args.metadata = Path(HEIGHTMAP_METADATA)
            log(f"Using HEIGHTMAP_METADATA: {args.metadata}")
        else:
            ap.error("Provide --metadata or --tiles (or set HEIGHTMAP_METADATA at top of script)")

    log_step("Prepare output directories")
    out_dir = args.out or Path(OUTPUT_DIR)
    out_dir.mkdir(parents=True, exist_ok=True)
    log(f"Mask dir: {out_dir.resolve()}", 1)

    roads_dir = None
    if not args.no_roads:
        roads_dir = args.roads_out or (out_dir.parent / "road_segments")
        roads_dir.mkdir(parents=True, exist_ok=True)
        log(f"Road JSON dir: {roads_dir.resolve()}", 1)
    else:
        log("Road JSON export disabled (--no-roads)", 1)

    cache_file = args.cache_file or (out_dir / REGIONAL_CACHE_NAME)
    log(f"Overpass cache: {cache_file.resolve()}", 1)

    log_step("Load tile list")
    if args.metadata:
        cfg = load_from_heightmap_metadata(args.metadata, args)
        log(f"From heightmap metadata: {args.metadata}", 1)
    else:
        cfg = load_from_vegetation_tiles_json(args.tiles, args)
        log(f"From vegetation tiles JSON: {args.tiles}", 1)

    tiles = [t for t in cfg["tiles"] if resolve_tile_id_filter(t, args.tile)]
    if not tiles:
        sys.exit("No tiles to process." if not args.tile else f"Tile not found: {args.tile}")

    tile_size = int(cfg["tile_size_meters"])
    union = tiles_union_bounds(tiles, tile_size)
    south, west, north, east = wgs84_bbox(*union, cfg["crs"])
    width_km = (union[2] - union[0]) / 1000.0
    height_km = (union[3] - union[1]) / 1000.0

    log(
        f"Tiles to process: {len(tiles)}  |  CRS: {cfg['crs']}  |  "
        f"tile {tile_size} m  |  mask {cfg['resolution']} px",
        1,
    )
    log(
        f"Regional EPSG: E {union[0]:.0f}–{union[2]:.0f}, N {union[1]:.0f}–{union[3]:.0f} "
        f"({width_km:.1f} × {height_km:.1f} km)",
        1,
    )
    log(
        f"Unity origin: {cfg.get('unity_origin_easting')}, {cfg.get('unity_origin_northing')}",
        1,
    )
    if args.refresh_overpass:
        log("--refresh-overpass: will ignore cache", 1)

    log_step("Regional OSM data")
    t_dl = time.perf_counter()
    if args.refresh_overpass:
        osm = None
        log("Skipping cache read (--refresh-overpass)", 1)
    else:
        osm = load_regional_overpass_cache(cache_file)

    if osm is not None:
        log(f"Using cached OSM ({format_duration(time.perf_counter() - t_dl)})", 1)
    else:
        osm = download_osm_regional(south, west, north, east, endpoints=overpass_urls)
        save_regional_overpass_cache(cache_file, osm)
        log("Overpass polite pause 1s …", 1)
        time.sleep(1)
        log(f"Download + cache total: {format_duration(time.perf_counter() - t_dl)}", 1)

    regional_geoms, regional_roads = parse_regional_osm(osm, cfg["crs"])

    log_step("Per-tile export", f"{len(tiles)} tile(s)")
    done = skipped = failed = 0
    t_tile_loop = time.perf_counter()
    for i, t in enumerate(tiles, 1):
        tile_id = t["tile_id"]
        if args.skip_existing and tile_outputs_complete(tile_id, out_dir, roads_dir, args.no_roads):
            log(f"[{i}/{len(tiles)}] {tile_id} — skip (outputs exist)", 1)
            skipped += 1
            if VERBOSE and i % TILE_HEARTBEAT_EVERY == 0:
                log(
                    f"Heartbeat: {i}/{len(tiles)} tiles visited, "
                    f"{done} done, {skipped} skipped, {failed} failed",
                    1,
                )
            continue

        mask_path = out_dir / f"{tile_id}_vegetation.png"
        roads_path = roads_dir / f"{tile_id}_roads.json" if roads_dir else None
        write_mask = not (args.skip_existing and mask_path.is_file())
        write_roads = roads_dir is not None and not (
            args.skip_existing and roads_path is not None and roads_path.is_file()
        )

        log_step(f"Tile [{i}/{len(tiles)}]", tile_id)
        partial = []
        if args.skip_existing and mask_path.is_file() and not write_mask:
            partial.append("mask exists")
        if roads_path and args.skip_existing and roads_path.is_file() and not write_roads:
            partial.append("roads json exists")
        if partial:
            log(f"Partial write ({', '.join(partial)})", 2)

        try:
            process_tile_outputs(
                t,
                cfg,
                regional_geoms,
                regional_roads,
                out_dir,
                roads_dir,
                args.soften,
                write_mask,
                write_roads,
            )
            done += 1
        except Exception as e:
            log(f"FAILED: {e}", 1)
            print(f"ERROR {tile_id}: {e}", file=sys.stderr)
            failed += 1

        if VERBOSE and done > 0 and (i % TILE_HEARTBEAT_EVERY == 0 or i == len(tiles)):
            elapsed = time.perf_counter() - t_tile_loop
            rate = done / elapsed if elapsed > 0 else 0.0
            remaining = len(tiles) - i
            eta_s = remaining / rate if rate > 0 else 0.0
            log(
                f"Heartbeat: {i}/{len(tiles)} visited, {done} rasterized, "
                f"loop {format_duration(elapsed)}, "
                f"~{format_duration(eta_s)} ETA (processed tiles only)",
                1,
            )

    total = format_duration(time.perf_counter() - t_run)
    log_step(
        "Finished",
        f"done={done} skipped={skipped} failed={failed} total={total}",
    )
    if not VERBOSE:
        print(f"Done: {done}  skipped: {skipped}  failed: {failed}  ({total})")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
