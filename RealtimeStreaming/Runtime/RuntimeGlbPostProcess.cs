using System.Collections;
using System.Threading.Tasks;
using GLTFast;
using UnityEngine;

namespace ZGConnect.RealtimeStreaming
{
    /// <summary>Spreads legacy GLB post-instantiate setup (LOD + BuildingData) across frames. No mesh combine or colliders.</summary>
    public static class RuntimeGlbPostProcess
    {
        public static IEnumerator CompleteSetupSpread(
            RuntimeGlbInstantiateResult result,
            BuildingLodController lodController,
            int buildingsPerFrame,
            float msBudget)
        {
            if (result?.Root == null || result.Import == null)
                yield break;

            Transform root = result.Root.transform;
            GltfImport lod1Import = null;

            if (result.LodMode == BuildingLodStorageMode.DualFile)
            {
                Task<GltfImport> lod1Task = RuntimeGlbLoader.InstantiateLod1ForSpreadAsync(
                    result.Prepared,
                    result.TileId,
                    root);
                while (!lod1Task.IsCompleted)
                    yield return null;

                lod1Import = lod1Task.Result;
                yield return null;

                if (lod1Import != null)
                {
                    Transform lod1Root = root.Find($"TileBuildings_{result.TileId}_lod1");
                    if (lod1Root != null)
                        lodController.MergeDualGlb(root, lod1Root);
                }

                yield return null;
            }
            else
            {
                lodController.SetupLodGroupsFromHierarchy(root);
                yield return null;
            }

            RuntimeBuildingHierarchyUtility.FlattenRuntimeGltfWrappers(root);

            var holder = root.gameObject.GetComponent<RuntimeBuildingsGltfHolder>();
            if (holder == null)
                holder = root.gameObject.AddComponent<RuntimeBuildingsGltfHolder>();
            holder.SetImports(result.Import, lod1Import);

            yield return null;
            RuntimeGlbLoader.ApplyOriginCorrection(root, result.TileId, null);
        }
    }
}
