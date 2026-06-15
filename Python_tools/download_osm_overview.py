import argparse
import json
import time
from pathlib import Path
from typing import Dict, List, Tuple, Optional

import requests
from PIL import Image, ImageDraw
from pyproj import Transformer


# ============================================================
# INPUT: EPSG:3765 projected bounds
# ============================================================

INPUT_XMIN = 442000
INPUT_YMIN = 5061000
INPUT_XMAX = 481000
INPUT_YMAX = 5079000

OUTPUT_SIZE = 2048
OUTPUT_FILE = "osm_selection_2048.png"
CACHE_FILENAME = "osm_overpass_cache.json"

# Checked in order when --cache is not passed (first existing file wins).
DEFAULT_CACHE_CANDIDATES = (
    "_dataset/vegetation_masks/osm_overpass_cache.json",
    "_dataset/osm_overpass_cache.json",
    CACHE_FILENAME,
)

# If True, expands the bbox to square so 2048x2048 is not distorted.
# For your input this becomes:
# xmin=442000, ymin=5050500, xmax=481000, ymax=5089500
MAKE_SQUARE = True

OVERPASS_URL = "https://overpass-api.de/api/interpreter"

USER_AGENT = "ZGConnectSelectionTexture/1.0"


# ============================================================
# CRS transformers
# ============================================================

# EPSG:3765 HTRS96 / Croatia TM -> WGS84 lon/lat
to_wgs84 = Transformer.from_crs("EPSG:3765", "EPSG:4326", always_xy=True)

# WGS84 lon/lat -> EPSG:3765 HTRS96 / Croatia TM
to_3765 = Transformer.from_crs("EPSG:4326", "EPSG:3765", always_xy=True)


# ============================================================
# Styling
# ============================================================

BACKGROUND = (224, 224, 216)

COLORS = {
    "water": (95, 165, 210),
    "forest": (72, 135, 76),
    "grass": (126, 178, 98),
    "farmland": (206, 190, 132),
    "residential": (205, 200, 190),
    "industrial": (184, 178, 170),
    "commercial": (198, 190, 176),
    "park": (112, 180, 100),
    "building": (120, 120, 120),
    "railway": (70, 70, 70),
    "road_motorway": (245, 170, 80),
    "road_primary": (245, 205, 90),
    "road_secondary": (245, 230, 130),
    "road_minor": (255, 255, 255),
    "road_path": (210, 210, 210),
    "outline": (80, 80, 80),
}


# ============================================================
# Utility
# ============================================================

def make_square_bbox(xmin: float, ymin: float, xmax: float, ymax: float):
    width = xmax - xmin
    height = ymax - ymin

    cx = (xmin + xmax) / 2
    cy = (ymin + ymax) / 2

    size = max(width, height)
    half = size / 2

    return cx - half, cy - half, cx + half, cy + half


def epsg3765_to_wgs84_bbox(xmin: float, ymin: float, xmax: float, ymax: float):
    """
    Converts projected bbox to WGS84 bbox.
    Returns south, west, north, east for Overpass.
    """

    corners = [
        to_wgs84.transform(xmin, ymin),
        to_wgs84.transform(xmin, ymax),
        to_wgs84.transform(xmax, ymin),
        to_wgs84.transform(xmax, ymax),
    ]

    lons = [c[0] for c in corners]
    lats = [c[1] for c in corners]

    west = min(lons)
    east = max(lons)
    south = min(lats)
    north = max(lats)

    return south, west, north, east


def lonlat_to_pixel(
    lon: float,
    lat: float,
    xmin: float,
    ymin: float,
    xmax: float,
    ymax: float,
    width_px: int,
    height_px: int
):
    x, y = to_3765.transform(lon, lat)

    px = int(round((x - xmin) / (xmax - xmin) * width_px))
    py = int(round((ymax - y) / (ymax - ymin) * height_px))

    return px, py


def is_closed_way(node_ids: List[int]) -> bool:
    return len(node_ids) >= 4 and node_ids[0] == node_ids[-1]


def is_closed_polygon(el: dict) -> bool:
    geom = el.get("geometry")
    if geom and len(geom) >= 4:
        return geom[0]["lon"] == geom[-1]["lon"] and geom[0]["lat"] == geom[-1]["lat"]

    node_ids = el.get("nodes", [])
    return is_closed_way(node_ids)


def project_root() -> Path:
    # Python_tools -> ZGConnect -> Assets -> <ZG Connect project>
    return Path(__file__).resolve().parents[3]


def resolve_output_path(output_arg: str) -> Path:
    path = Path(output_arg)

    if path.exists() and path.is_dir():
        return path / OUTPUT_FILE

    if path.suffix == "":
        return path.with_suffix(".png")

    return path


def resolve_cache_path(cache_arg: str | None) -> Path | None:
    if cache_arg:
        path = Path(cache_arg)
        return path if path.is_file() else None

    for rel in DEFAULT_CACHE_CANDIDATES:
        candidate = project_root() / rel
        if candidate.is_file():
            return candidate

    cwd_candidate = Path.cwd() / CACHE_FILENAME
    if cwd_candidate.is_file():
        return cwd_candidate

    return None


def classify_area(tags: Dict[str, str]) -> Optional[str]:
    natural = tags.get("natural")
    landuse = tags.get("landuse")
    leisure = tags.get("leisure")
    water = tags.get("water")
    building = tags.get("building")

    if building:
        return "building"

    if natural in ("water", "bay", "wetland") or water:
        return "water"

    if natural in ("wood", "tree_row") or landuse in ("forest",):
        return "forest"

    if landuse in ("grass", "meadow", "village_green") or leisure in ("park", "garden"):
        return "grass"

    if leisure in ("park", "garden", "recreation_ground", "pitch"):
        return "park"

    if landuse in ("farmland", "farmyard", "orchard", "vineyard"):
        return "farmland"

    if landuse in ("residential",):
        return "residential"

    if landuse in ("industrial", "construction"):
        return "industrial"

    if landuse in ("commercial", "retail"):
        return "commercial"

    return None


# Real-world road widths in meters â€” converted to pixels from the current m/px scale.
ROAD_WIDTH_METERS = {
    "motorway": 12.0,
    "primary": 9.0,
    "secondary": 7.0,
    "minor": 5.5,
    "service": 4.0,
    "path": 1.8,
}

RAILWAY_WIDTH_METERS = 3.5

# Multiplier applied on top of meter-based widths (1.0 = true scale, >1 = bolder).
DEFAULT_ROAD_WIDTH_SCALE = 1.35


def road_style(tags: Dict[str, str]) -> Optional[tuple[tuple[int, int, int], str]]:
    highway = tags.get("highway")

    if not highway:
        return None

    if highway in ("motorway", "motorway_link", "trunk", "trunk_link"):
        return COLORS["road_motorway"], "motorway"

    if highway in ("primary", "primary_link"):
        return COLORS["road_primary"], "primary"

    if highway in ("secondary", "secondary_link"):
        return COLORS["road_secondary"], "secondary"

    if highway in ("tertiary", "tertiary_link", "unclassified", "residential", "living_street"):
        return COLORS["road_minor"], "minor"

    if highway in ("service", "track"):
        return COLORS["road_minor"], "service"

    if highway in ("path", "footway", "cycleway", "pedestrian", "steps"):
        return COLORS["road_path"], "path"

    return COLORS["road_minor"], "service"


def line_width_px(
    width_m: float,
    meters_per_pixel: float,
    scale: float = DEFAULT_ROAD_WIDTH_SCALE,
) -> int:
    return max(1, int(round(width_m * scale / meters_per_pixel)))


AREA_ORDER = [
    "farmland",
    "grass",
    "forest",
    "park",
    "residential",
    "industrial",
    "commercial",
    "water",
    "building",
]

ROAD_PRIORITY = [
    ("motorway", "trunk"),
    ("primary",),
    ("secondary",),
    ("tertiary", "unclassified", "residential", "living_street"),
    ("service", "track"),
    ("path", "footway", "cycleway", "pedestrian", "steps"),
]


def element_lonlat_coords(el: dict, nodes: Dict[int, Tuple[float, float]]) -> List[Tuple[float, float]]:
    geom = el.get("geometry")
    if geom:
        return [(p["lon"], p["lat"]) for p in geom]

    coords = []
    for node_id in el.get("nodes", []):
        coord = nodes.get(node_id)
        if coord is not None:
            coords.append(coord)
    return coords


def relation_outer_lonlat_rings(relation: dict) -> List[List[Tuple[float, float]]]:
    rings = []
    for member in relation.get("members", []):
        if member.get("role") != "outer" or "geometry" not in member:
            continue
        coords = [(p["lon"], p["lat"]) for p in member["geometry"]]
        if len(coords) >= 4 and coords[0] == coords[-1]:
            rings.append(coords)
    return rings


def draw_osm_features(
    draw: ImageDraw.ImageDraw,
    nodes: Dict[int, Tuple[float, float]],
    ways: List[dict],
    relations: List[dict],
    xmin: float,
    ymin: float,
    xmax: float,
    ymax: float,
    output_size: int,
    road_width_scale: float = DEFAULT_ROAD_WIDTH_SCALE,
) -> None:
    meters_per_pixel = (xmax - xmin) / output_size
    def lonlat_ring_to_pixels(coords: List[Tuple[float, float]]) -> List[Tuple[int, int]]:
        pts = []
        for lon, lat in coords:
            px, py = lonlat_to_pixel(
                lon, lat,
                xmin, ymin, xmax, ymax,
                output_size, output_size,
            )
            pts.append((px, py))
        return pts

    def way_points(way: dict) -> List[Tuple[int, int]]:
        return lonlat_ring_to_pixels(element_lonlat_coords(way, nodes))

    def draw_area_polygon(tags: dict, pts: List[Tuple[int, int]]) -> None:
        if len(pts) < 3:
            return

        area_type = classify_area(tags)
        if area_type is None:
            return

        color = COLORS.get(area_type, (180, 180, 180))
        if area_type == "building":
            draw.polygon(pts, fill=color, outline=COLORS["outline"])
        else:
            draw.polygon(pts, fill=color)

    for category in AREA_ORDER:
        for way in ways:
            tags = way.get("tags", {})
            if not is_closed_polygon(way):
                continue
            if classify_area(tags) != category:
                continue
            draw_area_polygon(tags, way_points(way))

        for relation in relations:
            tags = relation.get("tags", {})
            if classify_area(tags) != category:
                continue
            for ring in relation_outer_lonlat_rings(relation):
                draw_area_polygon(tags, lonlat_ring_to_pixels(ring))

    railway_width = line_width_px(
        RAILWAY_WIDTH_METERS, meters_per_pixel, road_width_scale
    )
    for way in ways:
        if "railway" not in way.get("tags", {}):
            continue
        pts = way_points(way)
        if len(pts) >= 2:
            draw.line(pts, fill=COLORS["railway"], width=railway_width)

    for group in ROAD_PRIORITY:
        for way in ways:
            tags = way.get("tags", {})
            highway = tags.get("highway")
            if highway not in group and not any(
                highway == g or highway == f"{g}_link" for g in group
            ):
                continue

            style = road_style(tags)
            if style is None:
                continue

            color, width_key = style
            width = line_width_px(
                ROAD_WIDTH_METERS[width_key], meters_per_pixel, road_width_scale
            )
            pts = way_points(way)
            if len(pts) < 2:
                continue

            if width >= 3:
                outline = width + max(2, int(round(width * 0.35)))
                draw.line(pts, fill=(90, 90, 90), width=outline)
            draw.line(pts, fill=color, width=width)


class OsmRenderIndex:
    """Spatial index over cached OSM ways/relations for fast per-tile rendering."""

    def __init__(self, data: dict):
        from shapely.geometry import box as shapely_box
        from shapely.strtree import STRtree

        self.nodes: Dict[int, Tuple[float, float]] = {}
        self.items: List[dict] = []
        envelopes = []

        for el in data.get("elements", []):
            if el.get("type") == "node":
                self.nodes[el["id"]] = (el["lon"], el["lat"])

        for el in data.get("elements", []):
            if el.get("type") not in ("way", "relation"):
                continue
            env = self._envelope(el)
            if env is None:
                continue
            self.items.append(el)
            envelopes.append(env)

        self._tree = STRtree(envelopes) if envelopes else None

    def _envelope(self, el: dict):
        from shapely.geometry import box as shapely_box

        if el.get("type") == "relation":
            coords: List[Tuple[float, float]] = []
            for ring in relation_outer_lonlat_rings(el):
                coords.extend(ring)
        else:
            coords = element_lonlat_coords(el, self.nodes)

        if len(coords) < 2:
            return None

        xs = []
        ys = []
        for lon, lat in coords:
            x, y = to_3765.transform(lon, lat)
            xs.append(x)
            ys.append(y)

        return shapely_box(min(xs), min(ys), max(xs), max(ys))

    def query(self, left: float, bottom: float, right: float, top: float) -> tuple[list[dict], list[dict]]:
        from shapely.geometry import box as shapely_box

        if self._tree is None:
            return [], []

        tile_box = shapely_box(left, bottom, right, top)
        ways = []
        relations = []
        for idx in self._tree.query(tile_box):
            el = self.items[int(idx)]
            if el.get("type") == "relation":
                relations.append(el)
            else:
                ways.append(el)
        return ways, relations

    def render_tile(
        self,
        left: float,
        bottom: float,
        right: float,
        top: float,
        output_size: int,
        road_width_scale: float = DEFAULT_ROAD_WIDTH_SCALE,
    ) -> Image.Image:
        ways, relations = self.query(left, bottom, right, top)
        img = Image.new("RGB", (output_size, output_size), BACKGROUND)
        draw = ImageDraw.Draw(img)
        draw_osm_features(
            draw,
            self.nodes,
            ways,
            relations,
            left,
            bottom,
            right,
            top,
            output_size,
            road_width_scale=road_width_scale,
        )
        return img


# ============================================================
# Overpass
# ============================================================

def build_overpass_query(south: float, west: float, north: float, east: float) -> str:
    """
    Pulls only what is useful for a selection/reference map.
    Relations are intentionally skipped for simplicity and speed.
    """

    bbox = f"{south},{west},{north},{east}"

    return f"""
[out:json][timeout:180];
(
  way["highway"]({bbox});
  way["railway"]({bbox});

  way["building"]({bbox});

  way["natural"~"water|wood|wetland|scrub|grassland"]({bbox});
  way["water"]({bbox});
  way["waterway"]({bbox});

  way["landuse"~"forest|grass|meadow|residential|industrial|commercial|retail|farmland|farmyard|orchard|vineyard|cemetery|construction|recreation_ground"]({bbox});
  way["leisure"~"park|garden|recreation_ground|pitch|sports_centre"]({bbox});
);
out body;
>;
out skel qt;
"""


def load_overpass_cache(cache_file: Path) -> dict:
    print(f"Loading OSM cache: {cache_file.resolve()}")
    size_mb = cache_file.stat().st_size / (1024 * 1024)
    print(f"  size = {size_mb:.1f} MB")

    with cache_file.open("r", encoding="utf-8") as f:
        data = json.load(f)

    n = len(data.get("elements", []))
    print(f"  elements = {n:,}")
    return data


def download_overpass_data(query: str, cache_file: Path) -> dict:
    print("Downloading OSM data from Overpass...")
    print("This can take a while for a large bbox.")

    response = requests.post(
        OVERPASS_URL,
        data={"data": query},
        headers={"User-Agent": USER_AGENT},
        timeout=240,
    )

    if response.status_code != 200:
        raise RuntimeError(
            f"Overpass failed with HTTP {response.status_code}\n"
            f"{response.text[:2000]}"
        )

    data = response.json()

    cache_file.parent.mkdir(parents=True, exist_ok=True)
    with cache_file.open("w", encoding="utf-8") as f:
        json.dump(data, f)

    print(f"Saved cache: {cache_file}")

    # Be polite to Overpass.
    time.sleep(1)

    return data


def acquire_osm_data(
    query: str,
    cache_file: Path | None,
    refresh: bool,
) -> dict:
    if refresh:
        if cache_file is None:
            cache_file = project_root() / "_dataset" / CACHE_FILENAME
        return download_overpass_data(query, cache_file)

    if cache_file is not None:
        return load_overpass_cache(cache_file)

    searched = [str(project_root() / rel) for rel in DEFAULT_CACHE_CANDIDATES]
    searched.append(str(Path.cwd() / CACHE_FILENAME))
    raise SystemExit(
        "No OSM cache file found.\n"
        "Run osm_vegetation_masks.py first, pass --cache <path>, or use --refresh to download.\n"
        "Searched:\n  " + "\n  ".join(searched)
    )


# ============================================================
# Rendering
# ============================================================

def render_osm(
    data: dict,
    xmin: float,
    ymin: float,
    xmax: float,
    ymax: float,
    output_size: int,
    output_file: Path,
    road_width_scale: float = DEFAULT_ROAD_WIDTH_SCALE,
):
    print("Parsing OSM elements...")
    index = OsmRenderIndex(data)
    print(f"Indexed {len(index.items):,} drawable features")

    print("Rendering areas, railways, roads...")
    img = index.render_tile(
        xmin, ymin, xmax, ymax, output_size, road_width_scale=road_width_scale
    )

    output_file.parent.mkdir(parents=True, exist_ok=True)
    img.save(output_file, format="PNG")
    print(f"Saved: {output_file.resolve()}")


# ============================================================
# Main
# ============================================================

def parse_args() -> argparse.Namespace:
    ap = argparse.ArgumentParser(
        description=(
            "Render a 2048x2048 OSM overview PNG from a regional Overpass cache. "
            "By default uses an existing cache (e.g. from osm_vegetation_masks.py)."
        ),
    )
    ap.add_argument(
        "--cache",
        help=f"Path to osm_overpass_cache.json (default: first match in {', '.join(DEFAULT_CACHE_CANDIDATES)})",
    )
    ap.add_argument(
        "--refresh",
        action="store_true",
        help="Ignore existing cache and download fresh OSM data from Overpass",
    )
    ap.add_argument(
        "--output",
        default=OUTPUT_FILE,
        help=f"Output PNG path (default: {OUTPUT_FILE})",
    )
    ap.add_argument(
        "--road-scale",
        type=float,
        default=DEFAULT_ROAD_WIDTH_SCALE,
        help=(
            "Road/rail width multiplier on top of real-world meters "
            f"(default: {DEFAULT_ROAD_WIDTH_SCALE}; try 1.5-2.0 for bolder roads)"
        ),
    )
    return ap.parse_args()


def main():
    args = parse_args()
    cache_path = resolve_cache_path(args.cache)

    xmin = INPUT_XMIN
    ymin = INPUT_YMIN
    xmax = INPUT_XMAX
    ymax = INPUT_YMAX

    print("Input EPSG:3765 bbox:")
    print(f"  xmin = {xmin}")
    print(f"  ymin = {ymin}")
    print(f"  xmax = {xmax}")
    print(f"  ymax = {ymax}")
    print(f"  width  = {xmax - xmin:.2f} m")
    print(f"  height = {ymax - ymin:.2f} m")

    if MAKE_SQUARE:
        xmin, ymin, xmax, ymax = make_square_bbox(xmin, ymin, xmax, ymax)

    print()
    print("Render EPSG:3765 bbox:")
    print(f"  xmin = {xmin}")
    print(f"  ymin = {ymin}")
    print(f"  xmax = {xmax}")
    print(f"  ymax = {ymax}")
    print(f"  width  = {xmax - xmin:.2f} m")
    print(f"  height = {ymax - ymin:.2f} m")
    print(f"  resolution = {(xmax - xmin) / OUTPUT_SIZE:.2f} m/px")

    south, west, north, east = epsg3765_to_wgs84_bbox(xmin, ymin, xmax, ymax)

    print()
    print("Overpass WGS84 bbox:")
    print(f"  south = {south}")
    print(f"  west  = {west}")
    print(f"  north = {north}")
    print(f"  east  = {east}")

    query = build_overpass_query(south, west, north, east)

    if args.cache and cache_path is None:
        raise SystemExit(f"Cache file not found: {args.cache}")

    if cache_path is not None:
        print(f"Cache source: {cache_path.resolve()}")
    elif args.refresh:
        print(f"Cache target: {(project_root() / '_dataset' / CACHE_FILENAME).resolve()}")
    print()

    data = acquire_osm_data(query, cache_path, args.refresh)

    output_file = resolve_output_path(args.output)
    if output_file != Path(args.output):
        print(f"Output file: {output_file.resolve()}")

    render_osm(
        data=data,
        xmin=xmin,
        ymin=ymin,
        xmax=xmax,
        ymax=ymax,
        output_size=OUTPUT_SIZE,
        output_file=output_file,
        road_width_scale=args.road_scale,
    )


if __name__ == "__main__":
    main()
