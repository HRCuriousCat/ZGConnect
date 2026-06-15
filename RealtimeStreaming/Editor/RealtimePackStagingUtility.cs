using UnityEditor;

namespace ZGConnect.RealtimeStreaming.Editor
{
    /// <summary>
    /// Temporary AssetDatabase folders used only during pack/bake (editor scratch, not shipped as StreamingAssets).
    /// </summary>
    public static class RealtimePackStagingUtility
    {
        /// <summary>Editor-only scratch folder (not under StreamingAssets â€” avoids import/save conflicts).</summary>
        public const string StagingAssetRoot = "Assets/ZGConnect/RealtimeStreaming/_pack_staging";
        public const string TerrainRoot = StagingAssetRoot + "/terrain";
        public const string BuildingRoot = StagingAssetRoot + "/buildings";
        public const string VegetationRoot = StagingAssetRoot + "/vegetation";
        public const string LodPackTempRoot = "Assets/StreamingAssets/ZGConnect/_pack_temp";

        static readonly string[] LegacyStagingRoots =
        {
            "Assets/ZGConnect/RealtimeStreaming/BundleStaging",
            "Assets/ZGConnect/RealtimeStreaming/BuildingBundleStaging",
            "Assets/ZGConnect/RealtimeStreaming/VegetationBundleStaging",
            "Assets/StreamingAssets/ZGConnect/.pack_staging",
            "Assets/StreamingAssets/ZGConnect/_pack_staging",
        };

        public static void CleanupAll()
        {
            DeleteAssetFolderIfExists(TerrainRoot);
            DeleteAssetFolderIfExists(BuildingRoot);
            DeleteAssetFolderIfExists(VegetationRoot);
            DeleteAssetFolderIfExists(StagingAssetRoot);
            DeleteAssetFolderIfExists(LodPackTempRoot);

            foreach (string legacy in LegacyStagingRoots)
                DeleteAssetFolderIfExists(legacy);

            AssetDatabase.Refresh();
        }

        public static void CleanupTerrain() => DeleteAssetFolderIfExists(TerrainRoot);

        public static void CleanupBuildings() => DeleteAssetFolderIfExists(BuildingRoot);

        public static void CleanupVegetation() => DeleteAssetFolderIfExists(VegetationRoot);

        public static void DeleteAssetFolderIfExists(string assetFolder)
        {
            if (string.IsNullOrEmpty(assetFolder) || !AssetDatabase.IsValidFolder(assetFolder))
                return;

            AssetDatabase.DeleteAsset(assetFolder);
        }
    }
}
