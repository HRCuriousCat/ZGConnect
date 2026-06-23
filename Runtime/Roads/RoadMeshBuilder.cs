using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect.Roads
{
    public static class RoadMeshBuilder
    {
        public static Mesh BuildAsphaltMesh(RoadTopologyTile tile, RoadHeightSampler heightSampler)
        {
            var vertices = new List<Vector3>();
            var indices = new List<int>();

            if (tile?.AsphaltPolygons != null)
            {
                foreach (RoadPolygon polygon in tile.AsphaltPolygons)
                    AppendFanPolygon(polygon, heightSampler, vertices, indices);
            }

            return CreateMeshOrNull(tile != null ? $"Road_Asphalt_{tile.TileId}" : "Road_Asphalt", vertices, indices);
        }

        public static Mesh BuildPolygonMesh(string name, List<RoadPolygon> polygons, RoadHeightSampler heightSampler, float extraOffset)
        {
            var vertices = new List<Vector3>();
            var indices = new List<int>();
            if (polygons != null)
            {
                foreach (RoadPolygon polygon in polygons)
                    AppendFanPolygonWithOffset(polygon, heightSampler, extraOffset, vertices, indices);
            }

            return CreateMeshOrNull(name, vertices, indices);
        }

        static Mesh CreateMeshOrNull(string name, List<Vector3> vertices, List<int> indices)
        {
            if (vertices.Count == 0 || indices.Count == 0)
                return null;

            var mesh = new Mesh { name = name };
            if (vertices.Count > 65535)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            mesh.SetVertices(vertices);
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return mesh;
        }

        static void AppendFanPolygonWithOffset(
            RoadPolygon polygon,
            RoadHeightSampler heightSampler,
            float extraOffset,
            List<Vector3> vertices,
            List<int> indices)
        {
            int startBefore = vertices.Count;
            AppendFanPolygon(polygon, heightSampler, vertices, indices);
            for (int i = startBefore; i < vertices.Count; i++)
            {
                Vector3 p = vertices[i];
                p.y += extraOffset;
                vertices[i] = p;
            }
        }

        static void AppendFanPolygon(
            RoadPolygon polygon,
            RoadHeightSampler heightSampler,
            List<Vector3> vertices,
            List<int> indices)
        {
            if (polygon?.Outer == null || polygon.Outer.Count < 3)
                return;

            if (polygon.Holes != null && polygon.Holes.Count > 0)
            {
                // Hole-bearing polygons need a real triangulator before runtime rendering.
                return;
            }

            var ring = new List<Vector3>(polygon.Outer.Count);
            foreach (float[] xy in polygon.Outer)
            {
                if (!IsValidEpsgPoint(xy))
                    continue;

                Vector3 p = RoadTopologyGeometry.EpsgPointToWorld(xy);
                p.y = heightSampler != null ? heightSampler.Sample(p) : 0.05f;
                ring.Add(p);
            }

            if (ring.Count < 3)
                return;

            heightSampler?.SmoothRing(ring, iterations: 2, maxSlope: 0.35f);

            int start = vertices.Count;
            vertices.AddRange(ring);

            // V1 fan triangulation is acceptable for the prototype polygons.
            // Replace with ear clipping before shipping concave/high-hole junctions.
            for (int i = 1; i < ring.Count - 1; i++)
            {
                indices.Add(start);
                indices.Add(start + i);
                indices.Add(start + i + 1);
            }
        }

        static bool IsValidEpsgPoint(float[] xy)
        {
            return xy != null
                && xy.Length >= 2
                && !float.IsNaN(xy[0])
                && !float.IsNaN(xy[1])
                && !float.IsInfinity(xy[0])
                && !float.IsInfinity(xy[1]);
        }
    }
}
