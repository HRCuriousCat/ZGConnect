using UnityEngine;

namespace ZGConnect.RealtimeStreaming
{
    /// <summary>Splits a baked building tile into visual-only and physics-only prefab roots.</summary>
    public static class RuntimeBuildingTilePhysicsSplitUtility
    {
        public static void PrepareVisualOnlyInstance(GameObject visualRoot)
        {
            if (visualRoot == null)
                return;

            RemoveAllColliders(visualRoot);
            RuntimeBuildingTilePostProcessor.StripEmptyBuildingShells(visualRoot.transform);
        }

        public static void PreparePhysicsOnlyInstance(GameObject physicsRoot)
        {
            if (physicsRoot == null)
                return;

            Transform combinedRoot = physicsRoot.transform.Find(RuntimeBuildingTilePostProcessor.CombinedRenderRootName);
            if (combinedRoot != null)
                RuntimeObjectUtility.Destroy(combinedRoot.gameObject);

            foreach (Renderer renderer in physicsRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer != null)
                    RuntimeObjectUtility.Destroy(renderer);
            }

            foreach (MeshFilter filter in physicsRoot.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter == null || filter.GetComponent<MeshCollider>() != null)
                    continue;

                RuntimeObjectUtility.Destroy(filter);
            }

            foreach (LODGroup lodGroup in physicsRoot.GetComponentsInChildren<LODGroup>(true))
            {
                if (lodGroup != null)
                    RuntimeObjectUtility.Destroy(lodGroup);
            }

            RuntimeBuildingTileRuntimeState state = physicsRoot.GetComponent<RuntimeBuildingTileRuntimeState>();
            if (state != null)
                RuntimeObjectUtility.Destroy(state);

            RuntimeBuildingTileBakedMarker bakedMarker = physicsRoot.GetComponent<RuntimeBuildingTileBakedMarker>();
            if (bakedMarker != null)
                RuntimeObjectUtility.Destroy(bakedMarker);
        }

        static void RemoveAllColliders(GameObject root)
        {
            foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
            {
                if (collider != null)
                    RuntimeObjectUtility.Destroy(collider);
            }
        }
    }
}
