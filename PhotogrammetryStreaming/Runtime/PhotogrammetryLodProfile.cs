using UnityEngine;

namespace ZGConnect.PhotogrammetryStreaming
{
    [CreateAssetMenu(fileName = "PhotogrammetryLodProfile", menuName = "ZG Connect/Photogrammetry/LOD Profile")]
    public class PhotogrammetryLodProfile : ScriptableObject
    {
        [Header("Screen-space error LOD")]
        [Range(4f, 64f)]
        public float maximumScreenSpaceError = 16f;

        [Range(16f, 128f)]
        public float culledScreenSpaceError = 64f;

        [Header("Loading")]
        [Range(2, 32)]
        public int maximumSimultaneousTileLoads = 8;

        [Range(4, 40)]
        public int loadingDescendantLimit = 20;

        [Range(0, 4)]
        public int skipLevels;

        public bool enableFrustumCulling = true;
        public bool enableFogCulling;
        public bool regionClipEnabled = true;

        [Header("Focus region (meters from camera, horizontal)")]
        [Tooltip("Ignore tiles whose bounds center is farther than this from the camera.")]
        public float maxFocusRadiusM = 6000f;

        [Tooltip("Max GLB meshes to request per traversal pass (nearest to camera).")]
        public int maxGlbTilesPerCollect = 48;

        [Tooltip("Max external .json tilesets to request per traversal pass.")]
        public int maxJsonExpansionPerCollect = 12;

        [Header("Unload")]
        public float unloadRadiusM = 2000f;

        [Header("Memory")]
        public long maxCachedBytes = 512L * 1024L * 1024L;

        [Header("Disk cache")]
        public bool enableDiskCache = true;
        public long maxDiskCacheBytes = 2L * 1024L * 1024L * 1024L;
        public int diskCacheTtlHours = 48;

        [Header("Frame budget")]
        public int maxTileFinalizesPerFrame = 2;
        public float tileFinalizeMsBudget = 8f;

        [Header("Bake / depth cap")]
        public int maxRefinementDepth = 20;
    }
}
