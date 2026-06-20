using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// Paths for the Spatial Streaming workflow. Source assets live under StreamingAssets/ZGConnect
    /// (outside <c>bundles/</c> and <c>bundles_spatial/</c>).
    /// </summary>
    public static class SpatialStreamingPaths
    {
        public const string ZGConnectFolderName = "ZGConnect";
        public const string LegacyBundlesFolderName = "bundles";
        public const string SpatialBundlesFolderName = "bundles_spatial";
        public const string DefaultParentManifestRelativePath = "ZGConnect/manifest.json";
        public const string SpatialManifestRelativePath = "ZGConnect/spatial_manifest.json";
        /// <summary>
        /// Editor-only prefab staging (must NOT live under StreamingAssets — AssetDatabase cannot load those prefabs).
        /// </summary>
        public const string SpatialStagingAssetRoot = "Assets/ZGConnect/SpatialStreaming/_spatial_bake_staging";

        public const string BuildingMeshesSourceFolder = "building_meshes";

        /// <summary>Known source folders under the dataset root (not bake output).</summary>
        public static readonly IReadOnlyList<string> SourceFolderNames = new[]
        {
            "building_meshes",
            "building_meshes_ortho",
            "building_meshes_bin",
            "heightmaps",
            "basemaps",
            "vegetation_masks",
            "ortho_1x1",
            "ortho_2x2",
            "ortho_4x4",
        };

        public static string StreamingAssetsRoot => Application.streamingAssetsPath;

        public static string DatasetRoot =>
            Path.Combine(StreamingAssetsRoot, ZGConnectFolderName);

        public static string LegacyBundlesRoot =>
            Path.Combine(DatasetRoot, LegacyBundlesFolderName);

        public static string SpatialBundlesRoot =>
            Path.Combine(DatasetRoot, SpatialBundlesFolderName);

        public static string ParentManifestPath() =>
            Path.Combine(
                StreamingAssetsRoot,
                DefaultParentManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));

        public static string SpatialManifestPath() =>
            Path.Combine(
                StreamingAssetsRoot,
                SpatialManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));

        public static string ResolveDatasetRelativePath(string relativePath) =>
            Path.Combine(DatasetRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

        public static string ToDatasetRelativePath(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
                return string.Empty;

            string dataset = Path.GetFullPath(DatasetRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string normalized = Path.GetFullPath(fullPath);
            if (!normalized.StartsWith(dataset, System.StringComparison.OrdinalIgnoreCase))
                return fullPath.Replace('\\', '/');

            return normalized.Substring(dataset.Length).Replace('\\', '/');
        }

        public static bool IsBakeOutputPath(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
                return false;

            string normalized = Path.GetFullPath(fullPath).Replace('\\', '/');
            string spatial = SpatialBundlesRoot.Replace('\\', '/');
            string legacy = LegacyBundlesRoot.Replace('\\', '/');
            return normalized.StartsWith(spatial, System.StringComparison.OrdinalIgnoreCase) ||
                   normalized.StartsWith(legacy, System.StringComparison.OrdinalIgnoreCase);
        }

        public static bool FileExists(string fullPath)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return true;
#else
            return File.Exists(fullPath);
#endif
        }

        public static string GetBuildingGlbFileName(string tileId) => $"buildings_{tileId}.glb";

        public static string GetSubcellBundleRelativePath(string tileId, string subcellId) =>
            $"{SpatialBundlesFolderName}/{tileId}/{subcellId}";

        public static string GetTileCoarseBundleRelativePath(string tileId) =>
            $"{SpatialBundlesFolderName}/{tileId}/tile_coarse";

        public static string GetSubcellProxyBundleRelativePath(string tileId, string subcellId) =>
            $"{SpatialBundlesFolderName}/{tileId}/{subcellId}_proxy";

        public static string GetTileProxyBundleRelativePath(string tileId) =>
            $"{SpatialBundlesFolderName}/{tileId}/tile_proxy";

        public static string GetSupertileBundleRelativePath(int factor, int left, int bottom) =>
            $"{SpatialBundlesFolderName}/hlod{factor}/{left}_{bottom}";

        public static string GetSupertileId(int factor, int left, int bottom) =>
            $"hlod{factor}_{left}_{bottom}";
    }
}
