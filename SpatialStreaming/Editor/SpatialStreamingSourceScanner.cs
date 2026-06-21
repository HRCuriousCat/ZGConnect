using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ZGConnect.SpatialStreaming;

namespace ZGConnect.SpatialStreaming.Editor
{
    public sealed class SpatialStreamingSourceScanResult
    {
        public string DatasetRoot;
        public string ParentManifestPath;
        public bool ParentManifestExists;
        public string SpatialManifestPath;
        public bool SpatialManifestExists;
        public int SpatialBundleFileCount;
        public int StagedPrefabCount;
        public int BuildingGlbCount;
        public int BuildingOrthoGlbCount;
        public int HeightmapRawCount;
        public int BasemapFileCount;
        public readonly List<string> PresentSourceFolders = new();
        public readonly List<string> Warnings = new();

        public int TotalSourceGlbCount => BuildingGlbCount + BuildingOrthoGlbCount;
    }

    public static class SpatialStreamingSourceScanner
    {
        public static SpatialStreamingSourceScanResult Scan(string datasetRootOverride = null)
        {
            var result = new SpatialStreamingSourceScanResult
            {
                DatasetRoot = string.IsNullOrEmpty(datasetRootOverride)
                    ? SpatialStreamingPaths.DatasetRoot
                    : datasetRootOverride,
            };

            result.ParentManifestPath = SpatialStreamingPaths.ParentManifestPath();
            result.ParentManifestExists = File.Exists(result.ParentManifestPath);
            result.SpatialManifestPath = SpatialStreamingPaths.SpatialManifestPath();
            result.SpatialManifestExists = File.Exists(result.SpatialManifestPath);

            if (!Directory.Exists(result.DatasetRoot))
            {
                result.Warnings.Add($"Dataset root not found: {result.DatasetRoot}");
                return result;
            }

            foreach (string folderName in SpatialStreamingPaths.SourceFolderNames)
            {
                string folderPath = Path.Combine(result.DatasetRoot, folderName);
                if (!Directory.Exists(folderPath))
                    continue;

                result.PresentSourceFolders.Add(folderName);
            }

            result.BuildingGlbCount = CountSourceFiles(
                Path.Combine(result.DatasetRoot, "building_meshes"), "*.glb");
            result.BuildingOrthoGlbCount = CountSourceFiles(
                Path.Combine(result.DatasetRoot, "building_meshes_ortho"), "*.glb");
            result.HeightmapRawCount = CountSourceFiles(
                Path.Combine(result.DatasetRoot, "heightmaps"), "*.raw");
            result.BasemapFileCount = CountSourceFiles(
                Path.Combine(result.DatasetRoot, "basemaps"),
                "*.png", "*.jpg", "*.jpeg");

            string spatialBundlesPath = SpatialStreamingPaths.SpatialBundlesRoot;
            if (Directory.Exists(spatialBundlesPath))
            {
                result.SpatialBundleFileCount = Directory
                    .EnumerateFiles(spatialBundlesPath, "*", SearchOption.AllDirectories)
                    .Count();
            }

            if (result.TotalSourceGlbCount == 0)
            {
                result.Warnings.Add(
                    "No building GLB source files found under building_meshes/ or building_meshes_ortho/.");
            }

            if (!result.SpatialManifestExists && result.SpatialBundleFileCount > 0)
            {
                result.Warnings.Add(
                    "bundles_spatial/ has files but spatial_manifest.json is missing — run Bake.");
            }

            if (result.SpatialManifestExists && result.SpatialBundleFileCount == 0)
            {
                result.Warnings.Add(
                    "spatial_manifest.json exists but bundles_spatial/ is empty — run Spatial Streaming Bake.");
            }

            result.StagedPrefabCount =
                SpatialStagingBundleUtility.CountStagedPrefabs(SpatialStreamingPaths.SpatialStagingAssetRoot);

            if (result.StagedPrefabCount > 0 && result.SpatialBundleFileCount == 0)
            {
                result.Warnings.Add(
                    $"{result.StagedPrefabCount} staged spatial asset(s) found but no bundles_spatial output — " +
                    "use 'Build bundles from staging' to finish without re-baking GLBs.");
            }

            if (result.SpatialManifestExists && result.SpatialBundleFileCount > 0)
            {
                TryAddManifestBundleMismatchWarning(result);
            }

            return result;
        }

        static void TryAddManifestBundleMismatchWarning(SpatialStreamingSourceScanResult result)
        {
            SpatialDatasetManifest manifest = SpatialDatasetManifest.LoadFromFile(result.SpatialManifestPath);
            if (manifest?.Tiles == null || manifest.Tiles.Count == 0)
                return;

            int tilesOnDisk = 0;
            string bundlesRoot = SpatialStreamingPaths.SpatialBundlesRoot;
            if (Directory.Exists(bundlesRoot))
            {
                foreach (string tileDir in Directory.GetDirectories(bundlesRoot))
                {
                    string name = Path.GetFileName(tileDir);
                    if (string.IsNullOrEmpty(name) || name.StartsWith("hlod", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (Directory.EnumerateFiles(tileDir).Any(path =>
                            !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)))
                    {
                        tilesOnDisk++;
                    }
                }
            }

            SpatialTileManifestEntry firstTile = manifest.Tiles[0];
            if (firstTile != null &&
                !SpatialStagingBundleUtility.TileEntryHasBundleOnDisk(firstTile) &&
                tilesOnDisk > 0 &&
                tilesOnDisk < manifest.Tiles.Count)
            {
                result.Warnings.Add(
                    $"spatial_manifest.json lists {manifest.Tiles.Count} tile(s) but ~{tilesOnDisk} have bundles on disk. " +
                    "Use 'Sync manifest to bundles on disk' or re-bake missing tiles.");
            }
        }

        static int CountSourceFiles(string folder, params string[] patterns)
        {
            if (!Directory.Exists(folder))
                return 0;

            var paths = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (string pattern in patterns)
            {
                foreach (string path in Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories))
                {
                    if (!SpatialStreamingPaths.IsBakeOutputPath(path))
                        paths.Add(path);
                }
            }

            return paths.Count;
        }
    }
}
