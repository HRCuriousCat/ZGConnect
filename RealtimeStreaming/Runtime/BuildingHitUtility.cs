using UnityEngine;

namespace ZGConnect.RealtimeStreaming
{
    public static class BuildingHitUtility
    {
        const string TileBuildingsPrefix = "TileBuildings_";

        public static bool TryResolveBuildingRootFromCollider(
            Collider collider,
            out Transform buildingRoot,
            out string tileId)
        {
            buildingRoot = null;
            tileId = null;
            if (collider == null)
                return false;

            Transform tileRoot = FindTileRoot(collider.transform, out tileId);
            if (tileRoot == null || string.IsNullOrEmpty(tileId))
                return false;

            string physicsRootName = $"{TileBuildingsPrefix}{tileId}_Physics";
            Transform current = collider.transform;
            Transform best = null;

            while (current != null && current != tileRoot)
            {
                if (IsMetadataExcludedNode(current, physicsRootName))
                {
                    current = current.parent;
                    continue;
                }

                Transform parent = current.parent;
                if (parent == tileRoot || (parent != null && parent.name == physicsRootName))
                    best = current;

                current = parent;
            }

            if (best == null)
                return false;

            buildingRoot = best;
            return true;
        }

        static Transform FindTileRoot(Transform start, out string tileId)
        {
            tileId = null;
            Transform current = start;
            while (current != null)
            {
                if (current.name.StartsWith(TileBuildingsPrefix, System.StringComparison.Ordinal))
                {
                    string suffix = current.name.Substring(TileBuildingsPrefix.Length);
                    if (!suffix.EndsWith("_Physics", System.StringComparison.Ordinal))
                    {
                        tileId = suffix;
                        return current;
                    }
                }

                current = current.parent;
            }

            return null;
        }

        static bool IsMetadataExcludedNode(Transform node, string physicsRootName)
        {
            if (node == null)
                return true;

            if (node.name == RuntimeBuildingTilePostProcessor.CombinedRenderRootName)
                return true;

            return node.name == physicsRootName;
        }
    }
}
