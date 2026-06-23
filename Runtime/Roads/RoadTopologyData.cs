using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace ZGConnect.Roads
{
    [Serializable]
    public sealed class RoadTopologyManifest
    {
        [JsonProperty("version")] public int Version;
        [JsonProperty("tile_size_meters")] public int TileSizeMeters = 1000;
        [JsonProperty("road_count")] public int RoadCount;
        [JsonProperty("tile_count")] public int TileCount;
        [JsonProperty("tiles")] public List<RoadTopologyManifestTile> Tiles = new();
    }

    [Serializable]
    public sealed class RoadTopologyManifestTile
    {
        [JsonProperty("tile_id")] public string TileId;
        [JsonProperty("left")] public int Left;
        [JsonProperty("bottom")] public int Bottom;
        [JsonProperty("right")] public int Right;
        [JsonProperty("top")] public int Top;
        [JsonProperty("topology_path")] public string TopologyPath;

        public Vector3 UnityOrigin => RoadTopologyGeometry.EpsgPointToWorld(Left, Bottom);
    }

    [Serializable]
    public sealed class RoadTopologyTile
    {
        [JsonProperty("version")] public int Version;
        [JsonProperty("tile_id")] public string TileId;
        [JsonProperty("left")] public int Left;
        [JsonProperty("bottom")] public int Bottom;
        [JsonProperty("right")] public int Right;
        [JsonProperty("top")] public int Top;
        [JsonProperty("asphalt_polygons")] public List<RoadPolygon> AsphaltPolygons = new();
        [JsonProperty("intersection_polygons")] public List<RoadPolygon> IntersectionPolygons = new();
        [JsonProperty("sidewalk_polygons")] public List<RoadPolygon> SidewalkPolygons = new();
        [JsonProperty("curb_chains")] public List<RoadPolyline> CurbChains = new();
        [JsonProperty("marking_guides")] public List<RoadPolyline> MarkingGuides = new();
        [JsonProperty("source_way_ids")] public List<long> SourceWayIds = new();
        [JsonProperty("stats")] public RoadTopologyStats Stats = new();
    }

    [Serializable]
    public sealed class RoadPolygon
    {
        /// <summary>Raw EPSG:3765 easting/northing pairs from topology JSON.</summary>
        [JsonProperty("outer")] public List<float[]> Outer = new();

        /// <summary>Raw EPSG:3765 easting/northing hole rings from topology JSON.</summary>
        [JsonProperty("holes")] public List<List<float[]>> Holes = new();
        [JsonProperty("area_m2")] public float AreaM2;
    }

    [Serializable]
    public sealed class RoadPolyline
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("is_tile_cut")] public bool IsTileCut;

        /// <summary>Raw EPSG:3765 easting/northing pairs from topology JSON.</summary>
        [JsonProperty("points")] public List<float[]> Points = new();
    }

    [Serializable]
    public sealed class RoadTopologyStats
    {
        [JsonProperty("source_way_count")] public int SourceWayCount;
        [JsonProperty("asphalt_polygon_count")] public int AsphaltPolygonCount;
        [JsonProperty("asphalt_area_m2")] public float AsphaltAreaM2;
        [JsonProperty("curb_chain_count")] public int CurbChainCount;
    }

    public static class RoadTopologyGeometry
    {
        public static Vector3 EpsgPointToWorld(float easting, float northing, float y = 0f)
        {
            return new Vector3(
                (float)(easting - ZGConnectCoordinates.OriginEasting),
                y,
                (float)(northing - ZGConnectCoordinates.OriginNorthing));
        }

        public static Vector3 EpsgPointToWorld(float[] xy, float y = 0f)
        {
            if (xy == null || xy.Length < 2)
                return Vector3.zero;

            return EpsgPointToWorld(xy[0], xy[1], y);
        }

        public static List<Vector3> EpsgPointsToWorld(IReadOnlyList<float[]> points, float y = 0f)
        {
            var worldPoints = new List<Vector3>(points?.Count ?? 0);
            if (points == null)
                return worldPoints;

            for (int i = 0; i < points.Count; i++)
                worldPoints.Add(EpsgPointToWorld(points[i], y));

            return worldPoints;
        }
    }
}
