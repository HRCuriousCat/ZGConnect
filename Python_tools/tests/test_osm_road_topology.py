import json
import tempfile
import unittest
from pathlib import Path

from shapely.geometry import LineString

from Python_tools.osm_road_topology import (
    RawSegment,
    build_tile_topology,
    chain_segments_for_way,
    load_all_road_segments,
    load_tiles,
    stitch_regional_lines,
)


class RoadTopologyLoadingTests(unittest.TestCase):
    def test_load_all_road_segments_reads_existing_tile_json(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            (root / "473000_5080000_roads.json").write_text(
                json.dumps(
                    {
                        "segments": [
                            {
                                "osm_way_id": 10,
                                "highway": "residential",
                                "width_m": 5.0,
                                "points_epsg": [[0, 0], [10, 0]],
                            }
                        ]
                    }
                ),
                encoding="utf-8",
            )

            segments = load_all_road_segments(root)

        self.assertEqual(1, len(segments))
        self.assertEqual(10, segments[0].osm_way_id)
        self.assertEqual("residential", segments[0].highway)
        self.assertEqual(5.0, segments[0].width_m)
        self.assertEqual([(0.0, 0.0), (10.0, 0.0)], segments[0].coords)

    def test_load_all_road_segments_accepts_utf8_bom(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            (root / "0_0_roads.json").write_text(
                "\ufeff" + json.dumps(
                    {
                        "segments": [
                            {
                                "osm_way_id": 11,
                                "highway": "residential",
                                "width_m": 6.0,
                                "points_epsg": [[0, 0], [10, 0]],
                            }
                        ]
                    }
                ),
                encoding="utf-8",
            )

            segments = load_all_road_segments(root)

        self.assertEqual(1, len(segments))
        self.assertEqual(11, segments[0].osm_way_id)

    def test_load_tiles_accepts_top_level_list(self):
        with tempfile.TemporaryDirectory() as td:
            path = Path(td) / "tiles.json"
            tile = {"tile_id": "0_0", "left": 0, "bottom": 0, "right": 1000, "top": 1000}
            path.write_text(json.dumps([tile]), encoding="utf-8")

            tiles = load_tiles(path, [])

        self.assertEqual([tile], tiles)

    def test_load_tiles_accepts_empty_tiles_list(self):
        with tempfile.TemporaryDirectory() as td:
            path = Path(td) / "tiles.json"
            path.write_text(json.dumps({"tiles": []}), encoding="utf-8")

            tiles = load_tiles(path, [])

        self.assertEqual([], tiles)

    def test_load_tiles_rejects_invalid_json_shape(self):
        with tempfile.TemporaryDirectory() as td:
            path = Path(td) / "tiles.json"
            path.write_text(json.dumps({"tiles": {"tile_id": "0_0"}}), encoding="utf-8")

            with self.assertRaisesRegex(ValueError, "tiles"):
                load_tiles(path, [])

    def test_chain_segments_for_way_joins_reversed_segments(self):
        segments = [
            RawSegment(1, "residential", 5.0, [(0, 0), (10, 0)]),
            RawSegment(1, "residential", 5.0, [(20, 0), (10, 0)]),
        ]

        lines = chain_segments_for_way(segments)

        self.assertEqual(1, len(lines))
        self.assertEqual([(0.0, 0.0), (10.0, 0.0), (20.0, 0.0)], list(lines[0].coords))

    def test_stitch_regional_lines_preserves_width_and_highway(self):
        segments = [
            RawSegment(7, "primary", 8.0, [(0, 0), (20, 0)]),
            RawSegment(8, "footway", 2.0, [(0, 10), (20, 10)]),
        ]

        roads = stitch_regional_lines(segments)

        self.assertEqual(2, len(roads))
        self.assertEqual({7, 8}, {r.osm_way_id for r in roads})
        self.assertEqual({"primary", "footway"}, {r.highway for r in roads})
        self.assertEqual({8.0, 2.0}, {r.width_m for r in roads})
        self.assertTrue(all(isinstance(r.line, LineString) for r in roads))

    def test_build_tile_topology_merges_crossing_roads_into_asphalt_polygon(self):
        roads = stitch_regional_lines(
            [
                RawSegment(1, "residential", 6.0, [(-20, 0), (20, 0)]),
                RawSegment(2, "residential", 6.0, [(0, -20), (0, 20)]),
            ]
        )

        tile = {
            "tile_id": "0_0",
            "left": -50,
            "bottom": -50,
            "right": 50,
            "top": 50,
        }
        doc = build_tile_topology(tile, roads, simplify_m=0.05, fillet_m=1.5)

        self.assertEqual(1, doc["version"])
        self.assertEqual("0_0", doc["tile_id"])
        self.assertGreaterEqual(len(doc["asphalt_polygons"]), 1)
        self.assertGreater(doc["stats"]["asphalt_area_m2"], 0)
        self.assertGreaterEqual(doc["stats"]["source_way_count"], 2)

    def test_build_tile_topology_excludes_curb_edges_on_tile_cut(self):
        roads = stitch_regional_lines(
            [RawSegment(3, "residential", 6.0, [(-10, 0), (60, 0)])]
        )
        tile = {
            "tile_id": "0_0",
            "left": 0,
            "bottom": -50,
            "right": 50,
            "top": 50,
        }

        doc = build_tile_topology(tile, roads, simplify_m=0.05, fillet_m=0.0)

        self.assertGreater(len(doc["curb_chains"]), 0)
        self.assertTrue(all(not chain["is_tile_cut"] for chain in doc["curb_chains"]))

    def test_build_tile_topology_counts_road_surface_crossing_tile_boundary(self):
        roads = stitch_regional_lines(
            [RawSegment(4, "residential", 6.0, [(-2, 0), (-2, 20)])]
        )
        tile = {
            "tile_id": "0_0",
            "left": 0,
            "bottom": 0,
            "right": 50,
            "top": 50,
        }

        doc = build_tile_topology(tile, roads, simplify_m=0.05, fillet_m=0.0)

        self.assertEqual([4], doc["source_way_ids"])
        self.assertEqual(1, doc["stats"]["source_way_count"])
        self.assertGreater(doc["stats"]["asphalt_area_m2"], 0)

    def test_build_tile_topology_does_not_mark_separate_roads_as_intersections(self):
        roads = stitch_regional_lines(
            [
                RawSegment(5, "residential", 4.0, [(0, 0), (20, 0)]),
                RawSegment(6, "residential", 4.0, [(0, 20), (20, 20)]),
            ]
        )
        tile = {
            "tile_id": "0_0",
            "left": -10,
            "bottom": -10,
            "right": 30,
            "top": 30,
        }

        doc = build_tile_topology(tile, roads, simplify_m=0.05, fillet_m=0.0)

        self.assertGreaterEqual(len(doc["asphalt_polygons"]), 2)
        self.assertEqual([], doc["intersection_polygons"])

    def test_build_tile_topology_exports_sidewalk_ring_outside_asphalt(self):
        roads = stitch_regional_lines(
            [RawSegment(9, "residential", 6.0, [(0, 0), (20, 0)])]
        )
        tile = {
            "tile_id": "0_0",
            "left": -20,
            "bottom": -20,
            "right": 40,
            "top": 20,
        }

        doc = build_tile_topology(tile, roads, simplify_m=0.0, fillet_m=0.0)

        self.assertGreater(len(doc["sidewalk_polygons"]), 0)
        self.assertGreater(
            sum(poly["area_m2"] for poly in doc["sidewalk_polygons"]),
            0,
        )

    def test_build_tile_topology_exports_marking_guides_for_wide_non_path_roads(self):
        roads = stitch_regional_lines(
            [
                RawSegment(10, "residential", 6.0, [(-10, 0), (10, 0)]),
                RawSegment(11, "residential", 5.0, [(-10, 10), (10, 10)]),
                RawSegment(12, "footway", 8.0, [(-10, 20), (10, 20)]),
            ]
        )
        tile = {
            "tile_id": "0_0",
            "left": -5,
            "bottom": -5,
            "right": 5,
            "top": 25,
        }

        doc = build_tile_topology(tile, roads, simplify_m=0.0, fillet_m=0.0)

        self.assertEqual(1, len(doc["marking_guides"]))
        self.assertEqual("mark_10_0", doc["marking_guides"][0]["id"])
        self.assertEqual([[-5.0, 0.0], [5.0, 0.0]], doc["marking_guides"][0]["points"])

    def test_build_tile_topology_warns_when_source_roads_make_no_asphalt(self):
        roads = stitch_regional_lines(
            [RawSegment(13, "service", 1.0, [(0, 0), (0.5, 0)])]
        )
        tile = {
            "tile_id": "0_0",
            "left": -5,
            "bottom": -5,
            "right": 5,
            "top": 5,
        }

        doc = build_tile_topology(tile, roads, simplify_m=0.0, fillet_m=0.0)

        self.assertEqual([13], doc["source_way_ids"])
        self.assertEqual([], doc["asphalt_polygons"])
        self.assertEqual(
            ["source ways intersect tile but produced no asphalt polygons"],
            doc["warnings"],
        )


if __name__ == "__main__":
    unittest.main()
