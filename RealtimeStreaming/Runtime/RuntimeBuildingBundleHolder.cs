using UnityEngine;

namespace ZGConnect.RealtimeStreaming
{
    /// <summary>Keeps a building tile AssetBundle alive while the instantiated tile exists.</summary>
    public sealed class RuntimeBuildingBundleHolder : MonoBehaviour
    {
        public AssetBundle Bundle;
        public string BundleFullPath;
        public bool HasSplitPhysicsPrefab;
        public string PhysicsPrefabAssetName;

        void OnDestroy()
        {
            if (!string.IsNullOrEmpty(BundleFullPath))
            {
                RuntimeAssetBundleCache.Release(BundleFullPath, unloadAllLoadedObjects: false);
                BundleFullPath = null;
            }

            Bundle = null;
        }
    }
}
