using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// Tracks whether mesh-detail bundles can be deserialized in the current player/editor session.
    /// </summary>
    public static class SpatialMeshDetailRuntime
    {
        static bool? _available;

        public static bool IsAvailable
        {
            get
            {
                if (_available.HasValue)
                    return _available.Value;

                _available = ResolveAvailability();
                return _available.Value;
            }
        }

        public static void MarkUnavailable()
        {
            if (_available == false)
                return;

            _available = false;
            Debug.LogWarning(
                "[ZGConnect.Spatial] Mesh-detail bundles are unavailable in this session " +
                "(missing SpatialMeshDetailAsset script mapping or stale bundle script refs). " +
                "Falling back to prefabs where present. Re-bake spatial bundles after script changes.");
        }

        public static void ResetForTests() => _available = null;

        static bool ResolveAvailability()
        {
#if UNITY_EDITOR
            string[] guids = UnityEditor.AssetDatabase.FindAssets("SpatialMeshDetailAsset t:MonoScript");
            if (guids == null || guids.Length == 0)
                return false;
#endif
            return typeof(SpatialMeshDetailAsset).IsSubclassOf(typeof(ScriptableObject));
        }
    }
}
