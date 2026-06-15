#!/usr/bin/env python3
"""
ZG Connect - match buildings_*.json to OSM cache and merge in-place (full record merge).

Preserves every field from the source JSON (gps, localPosition, materialSlots, etc.) and
adds/updates only the "osm" block. Matching uses GPS when present, otherwise Unity coords.

Spatial index is built from osm_overpass_cache.json.

Requirements:
    pip install pyproj shapely

Examples:
    python Tools/match_buildings_osm.py \\
        --cache "_dataset/vegetation_masks/osm_overpass_cache.json" \\
        --buildings-dir "_dataset/building_meshes"

    python Tools/match_buildings_osm.py --cache ... --buildings-dir ... --tile 455000_5070000
    python Tools/match_buildings_osm.py --cache ... --buildings-dir ... --dry-run
    python Tools/match_buildings_osm.py --cache ... --buildings-dir ... --out-dir "_dataset/building_meshes_merged"
"""

from __future__ import annotations

import argparse
import copy
import json
import math
import re
import shutil
import sys
import time
from collections import Counter
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Any

from pyproj import Transformer
from shapely.geometry import MultiPolygon, Point, Polygon, shape
from shapely.strtree import STRtree

# Reuse projection from vegetation pipeline when run from Tools/
_TOOLS = Path(__file__).resolve().parent
if str(_TOOLS) not in sys.path:
    sys.path.insert(0, str(_TOOLS))

try:
    from osm_vegetation_masks import project_geom
except ImportError:
    from shapely.geometry import LineString

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

DEFAULT_CRS = "EPSG:3765"
# Default Unity world origin used when GLB/buildings JSON was generated (patch_buildings_origin.py)
DEFAULT_UNITY_ORIGIN_X = 442000
DEFAULT_UNITY_ORIGIN_Y = 5051000
_TILE_ID_RE = re.compile(r"^(\d+)_(\d+)$")
_TILE_SIZE_DEFAULT = 1000
_JSON_ENCODING = "utf-8-sig"  # buildings JSON may include UTF-8 BOM (Excel/export tools)

# Preferred key order when writing compact JSON (all other keys appended after)
_BUILDING_KEY_ORDER = (
    "name",
    "localPosition",
    "gps",
    "localRotation",
    "localScale",
    "materialSlots",
    "meshMaterials",
    "osm",
)

_VERBOSE = True
OSM_INDEX_PROGRESS_EVERY = 25_000
TILE_PROGRESS_EVERY = 25


def _ts() -> str:
    return datetime.now().strftime("%H:%M:%S")


def log(msg: str, indent: int = 0) -> None:
    if not _VERBOSE:
        return
    prefix = "  " * indent
    print(f"[{_ts()}] {prefix}{msg}", flush=True)


def log_step(title: str, detail: str = "") -> None:
    line = f"-- {title}"
    if detail:
        line += f" - {detail}"
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


def read_json_text(path: Path) -> str:
    """Read JSON text; strip UTF-8 BOM if present (common in exported buildings_*.json)."""
    raw = path.read_bytes()
    if raw.startswith(b"\xef\xbb\xbf"):
        return raw[3:].decode("utf-8")
    return raw.decode("utf-8")


def load_json_file(path: Path) -> dict:
    return json.loads(read_json_text(path))


@dataclass
class OsmFeature:
    osm_type: str
    osm_id: int
    polygon: Any
    tags: dict[str, str]
    area: float

    @property
    def is_building(self) -> bool:
        return "building" in self.tags


def parse_tile_id(tile_id: str) -> tuple[int, int]:
    m = _TILE_ID_RE.match(tile_id.strip())
    if not m:
        raise ValueError(f"Invalid tileId: {tile_id}")
    return int(m.group(1)), int(m.group(2))


def tile_epsg_bounds(tile_id: str, tile_size: int) -> tuple[float, float, float, float]:
    left, bottom = parse_tile_id(tile_id)
    return float(left), float(bottom), float(left + tile_size), float(bottom + tile_size)


def point_in_tile(e: float, n: float, bounds: tuple[float, float, float, float], margin: float) -> bool:
    minx, miny, maxx, maxy = bounds
    return (minx - margin) <= e <= (maxx + margin) and (miny - margin) <= n <= (maxy + margin)


@dataclass
class OriginMode:
    name: str
    origin_x: float
    origin_y: float


def origin_modes_for_tile(
    tile_id: str,
    meta: dict,
    unity_origin_x: float,
    unity_origin_y: float,
) -> list[OriginMode]:
    """Candidate Unity-origin -> EPSG conversions for this tile."""
    left, bottom = parse_tile_id(tile_id)
    origin = meta.get("tileOriginUnity") or {}
    tile_ox = left - float(origin.get("x", 0.0))
    tile_oy = bottom - float(origin.get("z", 0.0))
    return [
        OriginMode("glb_default", unity_origin_x, unity_origin_y),
        OriginMode("tile_derived", tile_ox, tile_oy),
    ]


def epsg_for_building(
    entry: dict,
    origin_x: float,
    origin_y: float,
    crs: str,
) -> tuple[str, float, float]:
    """Returns (coord_source, easting, northing). GPS is preferred when present."""
    gps = entry.get("gps")
    if gps and gps.get("lat") is not None and gps.get("lon") is not None:
        lat = float(gps["lat"])
        lon = float(gps["lon"])
        to_proj = Transformer.from_crs("EPSG:4326", crs, always_xy=True)
        e, n = to_proj.transform(lon, lat)
        return "gps", float(e), float(n)

    pos = entry.get("localPosition") or {}
    return (
        "unity",
        origin_x + float(pos.get("x", 0.0)),
        origin_y + float(pos.get("z", 0.0)),
    )


def choose_origin_mode(
    tile_id: str,
    meta: dict,
    buildings: list[dict],
    tile_size: int,
    force: str,
    margin_m: float,
    unity_origin_x: float,
    unity_origin_y: float,
) -> OriginMode:
    """Pick how localPosition maps to EPSG for this tile (auto votes inside tile bbox)."""
    modes = origin_modes_for_tile(tile_id, meta, unity_origin_x, unity_origin_y)
    by_name = {m.name: m for m in modes}

    if force != "auto":
        if force not in by_name:
            raise ValueError(f"Unknown --origin-mode {force!r}")
        return by_name[force]

    bounds = tile_epsg_bounds(tile_id, tile_size)
    best: OriginMode | None = None
    best_score = -1

    for mode in modes:
        score = 0
        for entry in buildings:
            try:
                _, e, n = epsg_for_building(entry, mode.origin_x, mode.origin_y, DEFAULT_CRS)
            except Exception:
                continue
            if point_in_tile(e, n, bounds, margin_m):
                score += 1
        if score > best_score:
            best_score = score
            best = mode

    if best is None or best_score == 0:
        best = by_name["glb_default"]
        log(
            f"  Origin auto: no points inside tile bbox - fallback to {best.name} "
            f"({best.origin_x:.0f}, {best.origin_y:.0f})",
            2,
        )
    else:
        log(
            f"  Origin auto: {best.name} ({best.origin_x:.0f}, {best.origin_y:.0f}) "
            f"- {best_score}/{len(buildings)} sample(s) inside tile",
            2,
        )
    return best


def osm_way_to_polygon(el: dict, crs: str) -> Polygon | None:
    """Any closed way polygon in cache (skips open highways)."""
    tags = el.get("tags") or {}
    if "highway" in tags and "building" not in tags:
        return None
    geom = el.get("geometry")
    if not geom or len(geom) < 4:
        return None
    coords = [[p["lon"], p["lat"]] for p in geom]
    if coords[0] != coords[-1]:
        return None
    g = project_geom({"type": "Polygon", "coordinates": [coords]}, crs)
    if g.is_empty or g.geom_type != "Polygon":
        return None
    return g


def osm_relation_to_polygon(el: dict, crs: str) -> Polygon | None:
    outer_rings = []
    for m in el.get("members") or []:
        if m.get("role") != "outer" or "geometry" not in m:
            continue
        coords = [[p["lon"], p["lat"]] for p in m["geometry"]]
        if len(coords) >= 4 and coords[0] == coords[-1]:
            outer_rings.append(coords)
    if not outer_rings:
        return None
    try:
        g = project_geom({"type": "Polygon", "coordinates": outer_rings[:1]}, crs)
    except Exception:
        return None
    if g.is_empty:
        return None
    if g.geom_type == "Polygon":
        return g
    if g.geom_type == "MultiPolygon" and len(g.geoms) > 0:
        return max(g.geoms, key=lambda p: p.area)
    return None


def classify_osm_feature(tags: dict[str, str]) -> tuple[str, str]:
    """OSM-web-style role + short label for atPoint entries."""
    if tags.get("building"):
        label = tags.get("name") or tags.get("brand") or f"building={tags['building']}"
        return "building", label
    if tags.get("boundary") == "administrative":
        level = tags.get("admin_level", "?")
        label = tags.get("name") or f"admin_level_{level}"
        return "admin", label
    if tags.get("shop"):
        label = tags.get("name") or f"shop={tags['shop']}"
        return "shop", label
    if tags.get("landuse"):
        label = tags.get("name") or f"landuse={tags['landuse']}"
        return "landuse", label
    if tags.get("leisure"):
        label = tags.get("name") or f"leisure={tags['leisure']}"
        return "leisure", label
    if tags.get("natural"):
        label = tags.get("name") or f"natural={tags['natural']}"
        return "natural", label
    if tags.get("amenity"):
        label = tags.get("name") or f"amenity={tags['amenity']}"
        return "amenity", label
    if tags.get("waterway") or tags.get("water"):
        label = tags.get("name") or "water"
        return "water", label
    label = tags.get("name") or "area"
    return "area", label


def load_osm_polygons(cache_path: Path, crs: str) -> list[OsmFeature]:
    log_step("Load OSM cache", str(cache_path))
    t0 = time.perf_counter()
    size = cache_path.stat().st_size
    log(f"File size: {format_bytes(size)}", 1)
    log("Reading JSON ...", 1)
    text = read_json_text(cache_path)
    log(f"Read {format_bytes(len(text.encode('utf-8')))} in {format_duration(time.perf_counter() - t0)}", 1)

    t1 = time.perf_counter()
    data = json.loads(text)
    elements = data.get("elements") or []
    log(f"Parsed {len(elements):,} elements in {format_duration(time.perf_counter() - t1)}", 1)

    log_step("Index OSM area polygons (cache scope)", crs)
    t2 = time.perf_counter()
    out: list[OsmFeature] = []
    skipped = 0

    for idx, el in enumerate(elements):
        if _VERBOSE and idx > 0 and idx % OSM_INDEX_PROGRESS_EVERY == 0:
            pct = 100 * idx // max(len(elements), 1)
            log(
                f"Index progress {idx:,}/{len(elements):,} ({pct}%) "
                f"- {len(out):,} polygons - {format_duration(time.perf_counter() - t2)}",
                1,
            )

        et = el.get("type")
        poly = None
        if et == "way":
            poly = osm_way_to_polygon(el, crs)
        elif et == "relation":
            poly = osm_relation_to_polygon(el, crs)

        if poly is None:
            if et in ("way", "relation"):
                skipped += 1
            continue

        tags = {k: str(v) for k, v in (el.get("tags") or {}).items()}
        out.append(
            OsmFeature(
                osm_type=et,
                osm_id=int(el["id"]),
                polygon=poly,
                tags=tags,
                area=float(poly.area),
            )
        )

    n_bld = sum(1 for f in out if f.is_building)
    log(
        f"Indexed {len(out):,} polygons ({n_bld:,} with building=*) "
        f"in {format_duration(time.perf_counter() - t2)} (skipped {skipped:,})",
        1,
    )
    log(
        "Note: admin boundaries / landuse=retail only appear if present in the "
        "Overpass cache (see osm_vegetation_masks.py query).",
        1,
    )
    return out


def build_spatial_index(features: list[OsmFeature], label: str) -> tuple[STRtree, list[OsmFeature]]:
    log(f"STRtree [{label}] ...", 1)
    t0 = time.perf_counter()
    geoms = [f.polygon for f in features]
    tree = STRtree(geoms)
    log(f"  {len(geoms):,} geometries in {format_duration(time.perf_counter() - t0)}", 1)
    return tree, features


def match_building_point(
    pt: Point,
    tree: STRtree,
    items: list[OsmFeature],
    max_distance_m: float,
) -> tuple[OsmFeature | None, str, float, float]:
    """Prefer smallest containing building footprint (Kaufland, not whole retail zone)."""
    hits_idx = tree.query(pt, predicate="contains")
    if len(hits_idx) > 0:
        candidates = [items[int(i)] for i in hits_idx]
        best = min(candidates, key=lambda b: b.area)
        return best, "contains", 1.0, 0.0

    nearest_idx = tree.nearest(pt)
    if nearest_idx is None:
        return None, "none", 0.0, float("inf")

    best = items[int(nearest_idx)]
    dist = float(pt.distance(best.polygon))
    if dist <= max_distance_m:
        score = max(0.0, 1.0 - dist / max_distance_m)
        return best, "nearest", score, dist

    return None, "none", 0.0, dist


def features_at_point(
    pt: Point,
    tree: STRtree,
    items: list[OsmFeature],
    max_features: int,
) -> list[OsmFeature]:
    """All cache polygons containing the point, smallest area first (OSM Query Features order)."""
    hits_idx = tree.query(pt, predicate="contains")
    if len(hits_idx) == 0:
        return []
    hits = [items[int(i)] for i in hits_idx]
    hits.sort(key=lambda f: f.area)
    return hits[:max_features]


def feature_payload(f: OsmFeature) -> dict:
    role, label = classify_osm_feature(f.tags)
    return {
        "osmType": f.osm_type,
        "osmId": f.osm_id,
        "role": role,
        "label": label,
        "areaM2": round(f.area, 1),
        "tags": f.tags,
    }


def osm_match_payload(hit: OsmFeature, method: str, score: float, dist: float) -> dict:
    role, label = classify_osm_feature(hit.tags)
    return {
        "osmType": hit.osm_type,
        "osmId": hit.osm_id,
        "role": role,
        "label": label,
        "matchMethod": method,
        "matchScore": round(score, 4),
        "distanceM": round(dist, 3),
        "tags": hit.tags,
    }


def build_osm_block(
    coord_src: str,
    entry: dict,
    building_hit: OsmFeature | None,
    building_method: str,
    building_score: float,
    building_dist: float,
    at_point: list[OsmFeature],
) -> dict:
    block: dict[str, Any] = {"coordSource": coord_src}
    gps = entry.get("gps")
    if gps:
        block["queryLat"] = float(gps["lat"])
        block["queryLon"] = float(gps["lon"])

    if building_hit is not None:
        block["building"] = osm_match_payload(
            building_hit, building_method, building_score, building_dist
        )
    else:
        block["building"] = None

    block["atPoint"] = [feature_payload(f) for f in at_point]
    return block


def strip_existing_osm(entry: dict) -> bool:
    if "osm" in entry:
        del entry["osm"]
        return True
    return False


def order_building_keys(entry: dict) -> dict:
    """Stable key order: known fields first, then any extra source fields."""
    out: dict[str, Any] = {}
    for key in _BUILDING_KEY_ORDER:
        if key in entry:
            out[key] = entry[key]
    for key, val in entry.items():
        if key not in out:
            out[key] = val
    return out


def _fmt_component(v: float) -> str:
    return f"{v:.4f}"


def _fmt_vec3(obj: dict) -> str:
    return (
        "{ "
        f'"x": {_fmt_component(float(obj.get("x", 0)))}, '
        f'"y": {_fmt_component(float(obj.get("y", 0)))}, '
        f'"z": {_fmt_component(float(obj.get("z", 0)))}'
        " }"
    )


def format_building_compact(entry: dict) -> str:
    ordered = order_building_keys(entry)
    parts: list[str] = []
    if "name" in ordered:
        parts.append(f'"name": "{ordered["name"]}"')
    if "localPosition" in ordered:
        parts.append(f'"localPosition": {_fmt_vec3(ordered["localPosition"])}')
    if ordered.get("gps"):
        g = ordered["gps"]
        parts.append(
            f'"gps": {{"lat": {float(g["lat"]):.6f}, "lon": {float(g["lon"]):.6f}}}'
        )
    if "localRotation" in ordered:
        parts.append(f'"localRotation": {_fmt_vec3(ordered["localRotation"])}')
    if "localScale" in ordered:
        parts.append(f'"localScale": {_fmt_vec3(ordered["localScale"])}')
    if ordered.get("materialSlots"):
        parts.append(f'"materialSlots": {json.dumps(ordered["materialSlots"], ensure_ascii=False)}')
    if ordered.get("meshMaterials"):
        parts.append(f'"meshMaterials": {json.dumps(ordered["meshMaterials"], ensure_ascii=False)}')
    for key, val in ordered.items():
        if key in _BUILDING_KEY_ORDER:
            continue
        parts.append(f"{json.dumps(key)}: {json.dumps(val, ensure_ascii=False)}")
    if "osm" in ordered:
        parts.append(f'"osm": {json.dumps(ordered["osm"], ensure_ascii=False)}')
    return "{ " + ", ".join(parts) + " }"


def detect_json_style(text: str) -> str:
    """compact = one building object per line (typical dataset export)."""
    for line in text.splitlines():
        s = line.strip()
        if s.startswith('{ "name"') or (s.startswith("{") and '"name"' in s and '"localPosition"' in s):
            return "compact"
    return "pretty"


def write_tile_json(meta: dict, path: Path, style: str) -> None:
    tile_id = meta.get("tileId", "")
    tile_size = meta.get("tileSizeMeters", _TILE_SIZE_DEFAULT)
    origin = meta.get("tileOriginUnity") or {}
    buildings = meta.get("buildings") or []
    count = meta.get("buildingCount", len(buildings))

    if style == "compact":
        lines = [
            "{",
            f'  "tileId": "{tile_id}",',
            f'  "tileSizeMeters": {int(tile_size)},',
            (
                "  \"tileOriginUnity\": { "
                f'"x": {_fmt_component(float(origin.get("x", 0)))}, '
                f'"y": {_fmt_component(float(origin.get("y", 0)))}, '
                f'"z": {_fmt_component(float(origin.get("z", 0)))}'
                " },"
            ),
            f'  "buildingCount": {int(count)},',
            '  "buildings": [',
        ]
        for i, entry in enumerate(buildings):
            comma = "," if i < len(buildings) - 1 else ""
            lines.append(f"    {format_building_compact(entry)}{comma}")
        lines.append("  ]")
        lines.append("}")
        path.write_text("\n".join(lines) + "\n", encoding="utf-8")
        return

    path.write_text(json.dumps(meta, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def overlay_source_buildings(meta: dict, source_path: Path) -> tuple[int, int]:
    """
    Copy all fields from source buildings (except osm) into target entries by name.
    Returns (fields_copied, gps_restored).
    """
    if not source_path.is_file():
        return 0, 0

    src = load_json_file(source_path)
    by_name = {
        b["name"]: b
        for b in (src.get("buildings") or [])
        if b.get("name")
    }
    if not by_name:
        return 0, 0

    fields_copied = 0
    gps_restored = 0

    for entry in meta.get("buildings") or []:
        name = entry.get("name")
        if not name or name not in by_name:
            continue
        src_b = by_name[name]
        for key, val in src_b.items():
            if key == "osm":
                continue
            if key not in entry or entry[key] != val:
                entry[key] = copy.deepcopy(val)
                fields_copied += 1
                if key == "gps":
                    gps_restored += 1

    # Preserve tile-level fields from source when missing on target
    for key in ("tileId", "tileSizeMeters", "tileOriginUnity", "buildingCount"):
        if key in src and key not in meta:
            meta[key] = copy.deepcopy(src[key])

    return fields_copied, gps_restored


def prepare_buildings_from_source(meta: dict) -> tuple[list[dict], int]:
    """
    Deep-copy all building records from meta; strip only osm for re-run.
    Returns (buildings list, count of prior osm blocks removed).
    """
    cleared = 0
    buildings: list[dict] = []
    for raw in meta.get("buildings") or []:
        entry = copy.deepcopy(raw)
        if strip_existing_osm(entry):
            cleared += 1
        buildings.append(entry)
    meta["buildings"] = buildings
    return buildings, cleared


def merge_tile(
    path: Path,
    all_tree: STRtree,
    all_features: list[OsmFeature],
    building_tree: STRtree,
    building_features: list[OsmFeature],
    crs: str,
    max_distance_m: float,
    max_at_point: int,
    write_path: Path,
    backup: bool,
    dry_run: bool,
    tile_size: int,
    origin_mode_force: str,
    origin_margin_m: float,
    unity_origin_x: float,
    unity_origin_y: float,
    source_dir: Path | None,
    json_style: str | None,
) -> dict[str, Any]:
    """Full merge: preserve source fields + gps, add/update osm only."""
    t0 = time.perf_counter()
    raw_text = read_json_text(path)
    style = json_style or detect_json_style(raw_text)
    meta = json.loads(raw_text)
    tile_id = meta.get("tileId") or path.stem.replace("buildings_", "", 1)

    if source_dir is not None:
        src_path = source_dir / path.name
        copied, gps_rest = overlay_source_buildings(meta, src_path)
        if copied:
            log(
                f"  Source overlay {src_path.name}: {copied} field(s), "
                f"gps on {gps_rest} building(s)",
                2,
            )
        elif src_path.is_file():
            log(f"  Source overlay {src_path.name}: already in sync", 2)
        else:
            log(f"  Source overlay: no file {src_path.name}", 2)

    buildings, cleared_osm = prepare_buildings_from_source(meta)
    if cleared_osm:
        log(f"  Cleared previous osm on {cleared_osm} building(s) (re-run)", 2)

    gps_count = sum(1 for b in buildings if b.get("gps"))
    log(f"  Buildings: {len(buildings)}, with gps: {gps_count}", 2)
    if gps_count < len(buildings):
        log(
            f"  WARNING: {len(buildings) - gps_count} building(s) lack gps "
            f"(will use Unity coords for those)",
            2,
        )

    tile_size = int(meta.get("tileSizeMeters") or tile_size)
    meta["buildingCount"] = len(buildings)

    origin = choose_origin_mode(
        tile_id,
        meta,
        buildings,
        tile_size,
        origin_mode_force,
        origin_margin_m,
        unity_origin_x,
        unity_origin_y,
    )

    stats: dict[str, Any] = {
        "tileId": tile_id,
        "file": path.name,
        "jsonStyle": style,
        "originMode": origin.name,
        "originX": origin.origin_x,
        "originY": origin.origin_y,
        "buildingCount": len(buildings),
        "gpsCount": gps_count,
        "matchViaGps": 0,
        "matchViaUnity": 0,
        "matchedContains": 0,
        "matchedNearest": 0,
        "unmatched": 0,
        "errors": 0,
        "osmIdsUsed": Counter(),
    }

    for entry in buildings:
        name = entry.get("name") or "<unnamed>"
        try:
            coord_src, e, n = epsg_for_building(
                entry, origin.origin_x, origin.origin_y, crs
            )
        except Exception as ex:
            stats["errors"] += 1
            entry["osm"] = None
            log(f"  SKIP {name}: coordinate error - {ex}", 3)
            continue

        if coord_src == "gps":
            stats["matchViaGps"] += 1
        else:
            stats["matchViaUnity"] += 1

        pt = Point(e, n)
        at_point = features_at_point(pt, all_tree, all_features, max_at_point)
        hit, method, score, dist = match_building_point(
            pt, building_tree, building_features, max_distance_m
        )

        entry["osm"] = build_osm_block(
            coord_src, entry, hit, method, score, dist, at_point
        )

        if hit is None:
            stats["unmatched"] += 1
            if _VERBOSE and stats["unmatched"] <= 3:
                log(
                    f"  MISS {name} ({coord_src}): no building within {max_distance_m}m "
                    f"(nearest {dist:.1f}m, atPoint={len(at_point)})",
                    3,
                )
            continue

        if method == "contains":
            stats["matchedContains"] += 1
        else:
            stats["matchedNearest"] += 1

        stats["osmIdsUsed"][hit.osm_id] += 1
        if _VERBOSE and name.endswith("141838"):
            log(
                f"  DEBUG {name}: building osmId={hit.osm_id} label={classify_osm_feature(hit.tags)[1]} "
                f"atPoint={len(at_point)}",
                2,
            )

    matched = stats["matchedContains"] + stats["matchedNearest"]
    stats["matchedCount"] = matched
    dup_osm = sum(1 for c in stats["osmIdsUsed"].values() if c > 1)
    stats["duplicateOsmAssignments"] = dup_osm

    rate = 100.0 * matched / len(buildings) if buildings else 0.0
    log(
        f"  Match {matched}/{len(buildings)} ({rate:.1f}%) "
        f"via_gps={stats['matchViaGps']} via_unity={stats['matchViaUnity']} "
        f"contains={stats['matchedContains']} nearest={stats['matchedNearest']} "
        f"miss={stats['unmatched']} err={stats['errors']} dup_osm={dup_osm} "
        f"- {format_duration(time.perf_counter() - t0)}",
        2,
    )

    if dry_run:
        log(f"  Dry-run: would write {write_path} (style={style})", 2)
        return stats

    write_path.parent.mkdir(parents=True, exist_ok=True)
    if backup and write_path.is_file() and write_path.resolve() == path.resolve():
        bak = path.with_suffix(path.suffix + ".bak")
        if not bak.is_file():
            shutil.copy2(path, bak)
            log(f"  Backup -> {bak.name}", 2)

    t_w = time.perf_counter()
    write_tile_json(meta, write_path, style)
    log(
        f"  Merged -> {write_path.name} (style={style}, gps kept {gps_count}) "
        f"in {format_duration(time.perf_counter() - t_w)}",
        2,
    )

    return stats


def print_final_summary(all_stats: list[dict], elapsed: float) -> None:
    total_b = sum(s["buildingCount"] for s in all_stats)
    total_m = sum(s["matchedCount"] for s in all_stats)
    total_contains = sum(s["matchedContains"] for s in all_stats)
    total_nearest = sum(s["matchedNearest"] for s in all_stats)
    total_miss = sum(s["unmatched"] for s in all_stats)
    total_err = sum(s["errors"] for s in all_stats)
    total_dup = sum(s["duplicateOsmAssignments"] for s in all_stats)
    total_gps = sum(s.get("gpsCount", 0) for s in all_stats)
    total_via_gps = sum(s.get("matchViaGps", 0) for s in all_stats)

    log_step("Summary")
    log(f"Tiles processed: {len(all_stats):,}", 1)
    log(f"Buildings:       {total_b:,}", 1)
    log(f"With GPS kept:   {total_gps:,}", 1)
    log(f"Matched via GPS: {total_via_gps:,}", 1)
    log(f"Matched:         {total_m:,} ({100.0 * total_m / total_b:.1f}%)" if total_b else "Matched: 0", 1)
    log(f"  contains:      {total_contains:,}", 1)
    log(f"  nearest:       {total_nearest:,}", 1)
    log(f"Unmatched:       {total_miss:,}", 1)
    log(f"Errors:          {total_err:,}", 1)
    log(f"Tiles w/ dup OSM: {total_dup:,} (same OSM id matched to multiple meshes)", 1)
    log(f"Total time:      {format_duration(elapsed)}", 1)

    if total_miss > 0:
        worst = sorted(all_stats, key=lambda s: s["unmatched"], reverse=True)[:5]
        log("Worst tiles by unmatched count:", 1)
        for s in worst:
            if s["unmatched"] <= 0:
                continue
            log(
                f"  {s['tileId']}: {s['unmatched']}/{s['buildingCount']} miss "
                f"({100.0 * s['matchedCount'] / s['buildingCount']:.0f}% matched)",
                2,
            )


def main() -> None:
    global _VERBOSE

    ap = argparse.ArgumentParser(
        description="Match buildings_*.json to OSM cache and merge osm tags into each entry.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__.split("\n\n", 1)[-1] if "\n\n" in __doc__ else "",
    )
    ap.add_argument("--cache", required=True, help="Path to osm_overpass_cache.json")
    ap.add_argument(
        "--buildings-dir",
        required=True,
        help="Folder with buildings_*.json (merged in-place unless --out-dir is set)",
    )
    ap.add_argument(
        "--out-dir",
        default=None,
        help="Write merged JSON here instead of overwriting --buildings-dir",
    )
    ap.add_argument("--crs", default=DEFAULT_CRS, help=f"Projected CRS (default {DEFAULT_CRS})")
    ap.add_argument(
        "--max-distance-m",
        type=float,
        default=15.0,
        help="Max distance (m) for nearest building footprint fallback",
    )
    ap.add_argument(
        "--max-at-point",
        type=int,
        default=30,
        help="Max enclosing OSM features per building (atPoint, smallest first)",
    )
    ap.add_argument("--tile", default=None, help="Process only this tileId (e.g. 455000_5070000)")
    ap.add_argument("--dry-run", action="store_true", help="Match and report without writing files")
    ap.add_argument(
        "--no-backup",
        action="store_true",
        help="Do not create .json.bak before overwriting source files",
    )
    ap.add_argument("--quiet", action="store_true", help="Summary only, minimal per-tile lines")
    ap.add_argument(
        "--origin-mode",
        choices=("auto", "glb_default", "tile_derived"),
        default="auto",
        help="How localPosition maps to EPSG (auto votes per tile; glb_default=442000/5051000)",
    )
    ap.add_argument(
        "--unity-origin-x",
        type=int,
        default=DEFAULT_UNITY_ORIGIN_X,
        help=f"Unity origin easting for glb_default mode (default {DEFAULT_UNITY_ORIGIN_X})",
    )
    ap.add_argument(
        "--unity-origin-y",
        type=int,
        default=DEFAULT_UNITY_ORIGIN_Y,
        help=f"Unity origin northing for glb_default mode (default {DEFAULT_UNITY_ORIGIN_Y})",
    )
    ap.add_argument(
        "--tile-size",
        type=int,
        default=_TILE_SIZE_DEFAULT,
        help="Tile edge length in metres when not set in JSON",
    )
    ap.add_argument(
        "--origin-margin-m",
        type=float,
        default=50.0,
        help="Margin when auto-detecting origin (points inside tile bbox + margin)",
    )
    ap.add_argument(
        "--source-dir",
        default=None,
        help="Optional folder with authoritative buildings_*.json (e.g. _jsons) "
        "to copy all fields including gps before merge",
    )
    ap.add_argument(
        "--json-style",
        choices=("auto", "compact", "pretty"),
        default="auto",
        help="Output JSON layout (auto detects from input file)",
    )
    args = ap.parse_args()

    unity_origin_x = float(args.unity_origin_x)
    unity_origin_y = float(args.unity_origin_y)

    _VERBOSE = not args.quiet

    cache_path = Path(args.cache)
    buildings_dir = Path(args.buildings_dir)
    out_dir = Path(args.out_dir) if args.out_dir else None
    source_dir = Path(args.source_dir) if args.source_dir else None
    json_style = None if args.json_style == "auto" else args.json_style

    if not cache_path.is_file():
        raise SystemExit(f"Cache not found: {cache_path}")
    if not buildings_dir.is_dir():
        raise SystemExit(f"Buildings dir not found: {buildings_dir}")

    log_step("ZG Connect buildings OSM merge", "v2 BOM-safe JSON read")
    log(f"Cache:          {cache_path.resolve()}", 1)
    log(f"Buildings dir:  {buildings_dir.resolve()}", 1)
    if source_dir:
        log(f"Source dir:     {source_dir.resolve()} (full field overlay)", 1)
    if out_dir:
        log(f"Output dir:     {out_dir.resolve()} (copy mode)", 1)
    else:
        log("Output:         in-place merge (all source fields + osm)", 1)
    log("Merge policy:     preserve entire building record; only add/update osm", 1)
    log(f"Max nearest:    {args.max_distance_m} m", 1)
    if args.dry_run:
        log("Mode:           DRY RUN (no files written)", 1)

    t_all = time.perf_counter()

    all_features = load_osm_polygons(cache_path, args.crs)
    if not all_features:
        raise SystemExit("No OSM polygons indexed - check cache path and bbox.")

    building_features = [f for f in all_features if f.is_building]
    if not building_features:
        raise SystemExit("No building=* polygons in cache.")

    all_tree, all_features = build_spatial_index(all_features, "all")
    building_tree, building_features = build_spatial_index(
        building_features, "buildings"
    )

    json_files = sorted(
        p for p in buildings_dir.glob("buildings_*.json")
        if p.is_file() and not p.name.endswith(".bak")
    )
    if args.tile:
        want = f"buildings_{args.tile}.json"
        json_files = [p for p in json_files if p.name == want]
        if not json_files:
            raise SystemExit(f"No file {want} in {buildings_dir}")

    log_step("Process tiles", f"{len(json_files):,} file(s)")
    all_stats: list[dict] = []

    for i, path in enumerate(json_files):
        if _VERBOSE and (i == 0 or (i + 1) % TILE_PROGRESS_EVERY == 0 or i + 1 == len(json_files)):
            log(f"Tile [{i + 1}/{len(json_files)}] {path.name}", 1)

        write_path = (out_dir / path.name) if out_dir else path
        stats = merge_tile(
            path,
            all_tree,
            all_features,
            building_tree,
            building_features,
            args.crs,
            args.max_distance_m,
            args.max_at_point,
            write_path,
            backup=not args.no_backup,
            dry_run=args.dry_run,
            tile_size=args.tile_size,
            origin_mode_force=args.origin_mode,
            origin_margin_m=args.origin_margin_m,
            unity_origin_x=unity_origin_x,
            unity_origin_y=unity_origin_y,
            source_dir=source_dir,
            json_style=json_style,
        )
        # Counter is not JSON-serializable; drop before storing
        stats.pop("osmIdsUsed", None)
        all_stats.append(stats)

    print_final_summary(all_stats, time.perf_counter() - t_all)
    log("Done.", 0)


if __name__ == "__main__":
    main()
