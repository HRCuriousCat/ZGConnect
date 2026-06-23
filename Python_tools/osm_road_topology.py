#!/usr/bin/env python3
"""Hybrid road topology exporter for ZG Connect.

This module derives inspectable per-tile road topology JSON from the existing
{tile_id}_roads.json centerline files. It intentionally stops before final mesh
creation; Unity owns runtime height sampling and mesh realization.
"""

from __future__ import annotations

import argparse
import json
import math
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from shapely import make_valid
from shapely.geometry import GeometryCollection, LineString, MultiPolygon, Polygon, box
from shapely.ops import unary_union

COORD_TOLERANCE_M = 0.05
MIN_POLYGON_AREA_M2 = 1.0
TILE_EDGE_TOLERANCE_M = 0.08
INTERSECTION_AREA_TOLERANCE_M2 = 0.01


@dataclass(frozen=True)
class RawSegment:
    osm_way_id: int
    highway: str
    width_m: float
    coords: list[tuple[float, float]]


@dataclass(frozen=True)
class RoadLine:
    osm_way_id: int
    highway: str
    width_m: float
    style_id: str
    line: LineString


def points_close(a: tuple[float, float], b: tuple[float, float], tol: float = COORD_TOLERANCE_M) -> bool:
    return abs(a[0] - b[0]) <= tol and abs(a[1] - b[1]) <= tol


def style_id_for_highway(highway: str, width_m: float) -> str:
    h = (highway or "unclassified").strip().lower()
    if h in {"motorway", "trunk"}:
        return "arterial"
    if h in {"primary", "secondary", "tertiary"}:
        return "main"
    if h in {"footway", "path", "cycleway", "track", "bridleway", "steps"}:
        return "path"
    if width_m <= 4.0:
        return "service"
    return "street"


def load_all_road_segments(roads_dir: Path) -> list[RawSegment]:
    segments: list[RawSegment] = []
    for path in sorted(roads_dir.glob("*_roads.json")):
        doc = json.loads(path.read_text(encoding="utf-8-sig"))
        for seg in doc.get("segments") or []:
            pts = seg.get("points_epsg") or []
            if len(pts) < 2:
                continue
            segments.append(
                RawSegment(
                    osm_way_id=int(seg["osm_way_id"]),
                    highway=str(seg.get("highway", "unclassified")),
                    width_m=float(seg.get("width_m", 5.0)),
                    coords=[(float(p[0]), float(p[1])) for p in pts],
                )
            )
    return segments


def chain_segments_for_way(segments: list[RawSegment]) -> list[LineString]:
    remaining = list(segments)
    lines: list[LineString] = []
    while remaining:
        seed = remaining.pop(0)
        chain = list(seed.coords)
        changed = True
        while changed:
            changed = False
            for i, seg in enumerate(remaining):
                coords = seg.coords
                if points_close(chain[-1], coords[0]):
                    chain.extend(coords[1:])
                elif points_close(chain[-1], coords[-1]):
                    chain.extend(reversed(coords[:-1]))
                elif points_close(chain[0], coords[-1]):
                    chain = coords[:-1] + chain
                elif points_close(chain[0], coords[0]):
                    chain = list(reversed(coords[1:])) + chain
                else:
                    continue
                remaining.pop(i)
                changed = True
                break
        if len(chain) >= 2:
            lines.append(LineString(chain))
    return lines


def stitch_regional_lines(segments: list[RawSegment]) -> list[RoadLine]:
    by_way: dict[int, list[RawSegment]] = {}
    for seg in segments:
        by_way.setdefault(seg.osm_way_id, []).append(seg)

    roads: list[RoadLine] = []
    for way_id, way_segments in by_way.items():
        highway = way_segments[0].highway
        width_m = way_segments[0].width_m
        style_id = style_id_for_highway(highway, width_m)
        for line in chain_segments_for_way(way_segments):
            if not line.is_empty and line.length >= 0.5:
                roads.append(RoadLine(way_id, highway, width_m, style_id, line))
    return roads


def iter_polygons(geom: Polygon | MultiPolygon | GeometryCollection | None) -> list[Polygon]:
    if geom is None or geom.is_empty:
        return []
    if isinstance(geom, Polygon):
        return [geom] if geom.area >= MIN_POLYGON_AREA_M2 else []
    if isinstance(geom, MultiPolygon):
        return [p for p in geom.geoms if p.area >= MIN_POLYGON_AREA_M2]
    if isinstance(geom, GeometryCollection):
        out: list[Polygon] = []
        for part in geom.geoms:
            out.extend(iter_polygons(part))
        return out
    return []


def polygon_to_rings(poly: Polygon) -> dict[str, Any]:
    return {
        "outer": [[round(float(x), 3), round(float(y), 3)] for x, y in list(poly.exterior.coords)[:-1]],
        "holes": [
            [[round(float(x), 3), round(float(y), 3)] for x, y in list(ring.coords)[:-1]]
            for ring in poly.interiors
        ],
        "area_m2": round(float(poly.area), 3),
    }


def is_tile_clip_edge(
    p0: tuple[float, float],
    p1: tuple[float, float],
    bounds: tuple[float, float, float, float],
    tol: float = TILE_EDGE_TOLERANCE_M,
) -> bool:
    left, bottom, right, top = bounds
    on_left = abs(p0[0] - left) < tol and abs(p1[0] - left) < tol
    on_right = abs(p0[0] - right) < tol and abs(p1[0] - right) < tol
    on_bottom = abs(p0[1] - bottom) < tol and abs(p1[1] - bottom) < tol
    on_top = abs(p0[1] - top) < tol and abs(p1[1] - top) < tol
    return on_left or on_right or on_bottom or on_top


def build_road_surface(road: RoadLine) -> Polygon | MultiPolygon | GeometryCollection:
    half = max(0.5, road.width_m * 0.5)
    return make_valid(road.line.buffer(half, cap_style=2, join_style=3).buffer(0))


def build_asphalt_surface(roads: list[RoadLine], fillet_m: float) -> Polygon | MultiPolygon | None:
    strips = []
    for road in roads:
        geom = build_road_surface(road)
        strips.extend(iter_polygons(geom))
    if not strips:
        return None
    merged = make_valid(unary_union(strips).buffer(0))
    if fillet_m > 0:
        merged = make_valid(merged.buffer(fillet_m, join_style=1).buffer(-fillet_m, join_style=1).buffer(0))
    return merged


def extract_curb_chains(polys: list[Polygon], bounds: tuple[float, float, float, float]) -> list[dict[str, Any]]:
    chains: list[dict[str, Any]] = []
    for poly_index, poly in enumerate(polys):
        coords = list(poly.exterior.coords)[:-1]
        for i, p0_raw in enumerate(coords):
            p1_raw = coords[(i + 1) % len(coords)]
            p0 = (float(p0_raw[0]), float(p0_raw[1]))
            p1 = (float(p1_raw[0]), float(p1_raw[1]))
            if is_tile_clip_edge(p0, p1, bounds):
                continue
            chains.append(
                {
                    "id": f"curb_{poly_index}_{i}",
                    "is_tile_cut": False,
                    "points": [[round(p0[0], 3), round(p0[1], 3)], [round(p1[0], 3), round(p1[1], 3)]],
                }
            )
    return chains


def build_marking_guides(roads: list[RoadLine], bounds: tuple[float, float, float, float]) -> list[dict[str, Any]]:
    tile_box = box(*bounds)
    guides = []
    for road in roads:
        if road.style_id == "path" or road.width_m < 5.5:
            continue
        clipped = road.line.intersection(tile_box)
        parts = [clipped] if isinstance(clipped, LineString) else list(getattr(clipped, "geoms", []))
        for index, part in enumerate(parts):
            if isinstance(part, LineString) and len(part.coords) >= 2:
                guides.append(
                    {
                        "id": f"mark_{road.osm_way_id}_{index}",
                        "is_tile_cut": False,
                        "points": [[round(float(x), 3), round(float(y), 3)] for x, y in part.coords],
                    }
                )
    return guides


def polygon_has_multiple_road_contributors(
    poly: Polygon,
    road_surfaces: list[tuple[int, Polygon | MultiPolygon | GeometryCollection]],
) -> bool:
    contributors = 0
    for _, surface in road_surfaces:
        if surface.is_empty or not surface.intersects(poly):
            continue
        overlap = surface.intersection(poly)
        if not overlap.is_empty and float(overlap.area) > INTERSECTION_AREA_TOLERANCE_M2:
            contributors += 1
            if contributors >= 2:
                return True
    return False


def build_tile_topology(
    tile: dict[str, Any],
    roads: list[RoadLine],
    simplify_m: float,
    fillet_m: float,
) -> dict[str, Any]:
    bounds = (
        float(tile["left"]),
        float(tile["bottom"]),
        float(tile["right"]),
        float(tile["top"]),
    )
    tile_box = box(*bounds)
    road_surfaces = [(int(road.osm_way_id), build_road_surface(road)) for road in roads]
    surface = build_asphalt_surface(roads, fillet_m)
    clipped = [] if surface is None else iter_polygons(make_valid(surface.intersection(tile_box)))
    prepared = []
    for poly in clipped:
        geom = poly.simplify(simplify_m, preserve_topology=True) if simplify_m > 0 else poly
        prepared.extend(iter_polygons(make_valid(geom.buffer(0))))

    sidewalk_width_m = 1.5
    sidewalk_polys = []
    for poly in prepared:
        zone = make_valid(poly.buffer(sidewalk_width_m, join_style=1).difference(poly).intersection(tile_box))
        sidewalk_polys.extend(iter_polygons(zone))

    source_way_ids = sorted(
        {
            way_id
            for way_id, surface_geom in road_surfaces
            if not surface_geom.is_empty
            and surface_geom.intersects(tile_box)
            and float(surface_geom.intersection(tile_box).area) > INTERSECTION_AREA_TOLERANCE_M2
        }
    )
    warnings = []
    if source_way_ids and not prepared:
        warnings.append("source ways intersect tile but produced no asphalt polygons")
    curb_chains = extract_curb_chains(prepared, bounds)
    return {
        "version": 1,
        "tile_id": str(tile["tile_id"]),
        "left": int(tile["left"]),
        "bottom": int(tile["bottom"]),
        "right": int(tile["right"]),
        "top": int(tile["top"]),
        "asphalt_polygons": [polygon_to_rings(poly) for poly in prepared],
        "intersection_polygons": [
            polygon_to_rings(poly) for poly in prepared if polygon_has_multiple_road_contributors(poly, road_surfaces)
        ],
        "sidewalk_polygons": [polygon_to_rings(poly) for poly in sidewalk_polys],
        "curb_chains": curb_chains,
        "marking_guides": build_marking_guides(roads, bounds),
        "source_way_ids": source_way_ids,
        "warnings": warnings,
        "stats": {
            "source_way_count": len(source_way_ids),
            "asphalt_polygon_count": len(prepared),
            "asphalt_area_m2": round(sum(float(p.area) for p in prepared), 3),
            "curb_chain_count": len(curb_chains),
        },
    }


def load_tiles(path: Path | None, roads: list[RoadLine]) -> list[dict[str, Any]]:
    if path is not None:
        doc = json.loads(path.read_text(encoding="utf-8"))
        if isinstance(doc, list):
            tiles = doc
        elif isinstance(doc, dict) and "tiles" in doc:
            tiles = doc["tiles"]
        else:
            raise ValueError("--tiles JSON must be a list or an object with a 'tiles' list")
        if not isinstance(tiles, list) or not all(isinstance(tile, dict) for tile in tiles):
            raise ValueError("--tiles JSON 'tiles' value must be a list of tile objects")
        return list(tiles)
    bounds = unary_union([r.line for r in roads]).bounds if roads else (0, 0, 1000, 1000)
    left = math.floor(bounds[0] / 1000) * 1000
    bottom = math.floor(bounds[1] / 1000) * 1000
    right = math.ceil(bounds[2] / 1000) * 1000
    top = math.ceil(bounds[3] / 1000) * 1000
    tiles = []
    for y in range(int(bottom), int(top), 1000):
        for x in range(int(left), int(right), 1000):
            tiles.append({"tile_id": f"{x}_{y}", "left": x, "bottom": y, "right": x + 1000, "top": y + 1000})
    return tiles


def build_arg_parser() -> argparse.ArgumentParser:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--roads-dir", type=Path, required=True)
    ap.add_argument("--out", type=Path, required=True)
    ap.add_argument("--tiles", type=Path, help="Optional JSON tile list with left/bottom/right/top/tile_id entries")
    ap.add_argument("--simplify-m", type=float, default=0.15)
    ap.add_argument("--fillet-m", type=float, default=1.5)
    return ap


def main(argv: list[str] | None = None) -> int:
    args = build_arg_parser().parse_args(argv)
    roads = stitch_regional_lines(load_all_road_segments(args.roads_dir))
    args.out.mkdir(parents=True, exist_ok=True)
    tiles = load_tiles(args.tiles, roads)
    tile_dir = args.out / "road_tiles"
    tile_dir.mkdir(parents=True, exist_ok=True)
    manifest_tiles = []
    for tile in tiles:
        doc = build_tile_topology(tile, roads, simplify_m=args.simplify_m, fillet_m=args.fillet_m)
        rel = f"road_tiles/{tile['tile_id']}_road_topology.json"
        (args.out / rel).write_text(json.dumps(doc, indent=2), encoding="utf-8")
        manifest_tiles.append(
            {
                "tile_id": tile["tile_id"],
                "left": int(tile["left"]),
                "bottom": int(tile["bottom"]),
                "right": int(tile["right"]),
                "top": int(tile["top"]),
                "topology_path": rel,
            }
        )
    manifest = {
        "version": 1,
        "tile_size_meters": 1000,
        "road_count": len(roads),
        "tile_count": len(manifest_tiles),
        "tiles": manifest_tiles,
    }
    (args.out / "road_topology_manifest.json").write_text(
        json.dumps(manifest, indent=2),
        encoding="utf-8",
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
