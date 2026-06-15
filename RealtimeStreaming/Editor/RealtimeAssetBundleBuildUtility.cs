using UnityEditor;

namespace ZGConnect.RealtimeStreaming.Editor
{
    /// <summary>
    /// Shared AssetBundle build settings for pack-time baking.
    /// LZ4 (default) is much faster than ChunkBasedCompression (LZMA) for large mesh-heavy prefabs.
    /// </summary>
    public static class RealtimeAssetBundleBuildUtility
    {
        /// <summary>LZ4 — fast pack, good enough for local StreamingAssets delivery.</summary>
        public const BuildAssetBundleOptions PackBuildOptions = BuildAssetBundleOptions.None;

        public static BuildAssetBundleOptions GetPackBuildOptions(bool uncompressed) =>
            uncompressed
                ? BuildAssetBundleOptions.UncompressedAssetBundle
                : PackBuildOptions;
    }
}
