using UnityEditor;

namespace ZGConnect.SpatialStreaming.Editor
{
    public static class SpatialAssetBundleBuildUtility
    {
        public static BuildAssetBundleOptions GetBuildOptions(bool uncompressed) =>
            uncompressed
                ? BuildAssetBundleOptions.UncompressedAssetBundle
                : BuildAssetBundleOptions.None;
    }
}
