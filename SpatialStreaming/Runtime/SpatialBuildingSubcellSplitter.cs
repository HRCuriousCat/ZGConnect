using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    public static class SpatialBuildingSubcellSplitter
    {
        public sealed class SplitResult
        {
            public bool UsedSubcells;
            public readonly Dictionary<string, List<Transform>> Subcells = new();
            public string CoarseSubcellId = "tile_coarse";
        }

        public static SplitResult SplitTileBuildings(
            Transform tileRoot,
            Vector3 tileOrigin,
            int tileSizeMeters,
            SpatialBakeProfile profile)
        {
            var result = new SplitResult();
            if (tileRoot == null || profile == null)
                return result;

            var buildings = CollectBuildingRoots(tileRoot);
            if (buildings.Count == 0)
                return result;

            if (buildings.Count < profile.minBuildingsForSubcellSplit)
            {
                result.UsedSubcells = false;
                result.Subcells[result.CoarseSubcellId] = buildings;
                return result;
            }

            int subcellSize = Mathf.Max(32, profile.subcellSizeMeters);
            int cellsPerEdge = Mathf.Clamp(tileSizeMeters / subcellSize, 1, Mathf.Max(1, profile.maxSubcellsPerEdge));
            int actualSubcellSize = tileSizeMeters / cellsPerEdge;

            result.UsedSubcells = cellsPerEdge > 1;
            foreach (Transform building in buildings)
            {
                if (building == null)
                    continue;

                Bounds bounds = ComputeRendererBounds(building);
                Vector3 center = bounds.size.sqrMagnitude > 0.0001f
                    ? bounds.center
                    : building.position;

                int gx = Mathf.Clamp(
                    Mathf.FloorToInt((center.x - tileOrigin.x) / actualSubcellSize),
                    0,
                    cellsPerEdge - 1);
                int gy = Mathf.Clamp(
                    Mathf.FloorToInt((center.z - tileOrigin.z) / actualSubcellSize),
                    0,
                    cellsPerEdge - 1);

                string subcellId = $"{gx}_{gy}";
                if (!result.Subcells.TryGetValue(subcellId, out List<Transform> list))
                {
                    list = new List<Transform>();
                    result.Subcells[subcellId] = list;
                }

                list.Add(building);
            }

            return result;
        }

        public static List<Transform> CollectBuildingRoots(Transform tileRoot)
        {
            var buildings = new List<Transform>();
            if (tileRoot == null)
                return buildings;

            SpatialBuildingHierarchyUtility.ForEachBuildingTransform(tileRoot, building =>
            {
                if (building != null && HasMeshRendererInHierarchy(building))
                    buildings.Add(building);
            });

            return buildings;
        }

        public static List<MeshRenderer> CollectRenderers(IEnumerable<Transform> buildings)
        {
            var renderers = new List<MeshRenderer>();
            if (buildings == null)
                return renderers;

            foreach (Transform building in buildings)
            {
                if (building == null)
                    continue;

                renderers.AddRange(building.GetComponentsInChildren<MeshRenderer>(true));
            }

            return renderers;
        }

        static bool HasMeshRendererInHierarchy(Transform node)
        {
            if (node == null)
                return false;

            return node.GetComponentInChildren<MeshRenderer>(true) != null;
        }

        static Bounds ComputeRendererBounds(Transform root)
        {
            bool hasBounds = false;
            var bounds = new Bounds(root.position, Vector3.zero);
            foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return bounds;
        }
    }
}
