using System;
using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// GLB hierarchy helpers for spatial bake (copied from RealtimeStreaming patterns).
    /// </summary>
    public static class SpatialBuildingHierarchyUtility
    {
        public static void FlattenRuntimeGltfWrappers(Transform root)
        {
            if (root == null)
                return;

            while (root.childCount == 1)
            {
                Transform wrapper = root.GetChild(0);
                if (wrapper.childCount == 0 || HasMeshComponents(wrapper))
                    break;

                while (wrapper.childCount > 0)
                    wrapper.GetChild(0).SetParent(root, worldPositionStays: true);

                UnityEngine.Object.DestroyImmediate(wrapper.gameObject);
            }

            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = root.childCount - 1; i >= 0; i--)
                {
                    Transform child = root.GetChild(i);
                    if (!IsTileBuildingsGroupNode(child))
                        continue;

                    while (child.childCount > 0)
                        child.GetChild(0).SetParent(root, worldPositionStays: true);

                    UnityEngine.Object.DestroyImmediate(child.gameObject);
                    changed = true;
                    break;
                }
            }
        }

        public static void ForEachBuildingTransform(Transform tileRoot, Action<Transform> action)
        {
            if (tileRoot == null || action == null)
                return;

            for (int i = 0; i < tileRoot.childCount; i++)
            {
                Transform child = tileRoot.GetChild(i);
                if (child.name == SpatialMeshCombineUtility.CombinedRenderRootName)
                    continue;

                if (IsNestedTileBuildingsGroup(child))
                    ForEachBuildingTransform(child, action);
                else
                    action(child);
            }
        }

        static bool IsTileBuildingsGroupNode(Transform node) =>
            IsNestedTileBuildingsGroup(node);

        static bool IsNestedTileBuildingsGroup(Transform node)
        {
            if (node == null || !node.name.StartsWith("TileBuildings_", StringComparison.Ordinal))
                return false;

            return !HasMeshComponents(node) && node.childCount > 0;
        }

        static bool HasMeshComponents(Transform node)
        {
            if (node == null)
                return false;

            return node.GetComponent<MeshFilter>() != null
                   || node.GetComponent<MeshRenderer>() != null
                   || node.GetComponent<SkinnedMeshRenderer>() != null;
        }
    }
}
