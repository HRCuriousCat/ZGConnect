using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using ZGConnect;
using ZGConnect.Editor;

namespace ZGConnect.RealtimeStreaming.Editor
{
    public static class RealtimeVegetationBundleBuilder
    {
        const string IncrementalBundleSubfolder = "_incremental_build";

        public static IEnumerator BuildCoroutine(
            RealtimePackOptions options,
            List<StreamingTileEntry> tiles,
            float progressStart,
            float progressEnd,
            Action<RealtimePackProgress> onProgress = null)
        {
            if (tiles == null || tiles.Count == 0 ||
                options.VegetationRuleSet == null ||
                options.VegetationPrototypes == null ||
                options.VegetationPrototypes.Length == 0)
            {
                yield break;
            }

            string stagingRoot = RealtimePackStagingUtility.VegetationRoot;
            RealtimePackStagingUtility.DeleteAssetFolderIfExists(stagingRoot);
            ZGConnectPathUtils.EnsureAssetFolder(stagingRoot);

            var builds = new List<AssetBundleBuild>();
            int total = tiles.Count(t =>
                options.SelectedTileIds.Contains(t.TileId) && t.HasVegetationMask);
            int done = 0;
            float stagingEnd = progressStart + (progressEnd - progressStart) * 0.85f;
            string bundleAssetOutput = $"{RuntimeStreamingPaths.PackedDatasetAssetRoot}/bundles";

            Report(onProgress, RealtimePackProgress.Create(
                RealtimePackStage.VegetationBake, progressStart, 0f, 0, total, "Baking vegetation"));
            yield return null;

            foreach (StreamingTileEntry tile in tiles)
            {
                if (!options.SelectedTileIds.Contains(tile.TileId) || !tile.HasVegetationMask)
                    continue;

                string bundleRel = $"bundles/vegetation/tiles_1x1/{tile.TileId}";
                if (ShouldSkipBuild(options, tile, bundleRel))
                {
                    done++;
                    yield return null;
                    continue;
                }

                float stageProgress = (float)(done + 1) / Mathf.Max(total, 1);
                Report(onProgress, RealtimePackProgress.Create(
                    RealtimePackStage.VegetationBake,
                    Mathf.Lerp(progressStart, stagingEnd, stageProgress),
                    stageProgress,
                    done + 1,
                    total,
                    tile.TileId));

                if (TryBakeVegetationAsset(options, tile, out string assetPath))
                {
                    builds.Add(new AssetBundleBuild
                    {
                        assetBundleName = $"vegetation/tiles_1x1/{tile.TileId}",
                        assetNames = new[] { assetPath },
                    });
                    tile.VegetationBundlePath = bundleRel;
                }

                done++;
                yield return null;
            }

            if (builds.Count == 0)
            {
                Debug.LogWarning("[ZGConnect.Realtime] No vegetation bundles staged — skipping build.");
                RealtimePackStagingUtility.CleanupVegetation();
                yield break;
            }

            ZGConnectPathUtils.EnsureAssetFolder(bundleAssetOutput);
            bool incremental = options.SkipExisting && builds.Count < total;
            string buildOutputAsset = incremental
                ? $"{bundleAssetOutput}/{IncrementalBundleSubfolder}"
                : bundleAssetOutput;

            if (incremental)
            {
                RealtimePackStagingUtility.DeleteAssetFolderIfExists(buildOutputAsset);
                ZGConnectPathUtils.EnsureAssetFolder(buildOutputAsset);
            }

            bool ok = BuildPipeline.BuildAssetBundles(
                buildOutputAsset,
                builds.ToArray(),
                RealtimeAssetBundleBuildUtility.PackBuildOptions,
                EditorUserBuildSettings.activeBuildTarget);

            if (!ok)
                throw new InvalidOperationException("[ZGConnect.Realtime] Vegetation bundle build failed.");

            if (incremental)
            {
                CopyBuiltBundles(buildOutputAsset, bundleAssetOutput, builds);
                RealtimePackStagingUtility.DeleteAssetFolderIfExists(buildOutputAsset);
            }

            RealtimePackStagingUtility.CleanupVegetation();
            AssetDatabase.Refresh();

            Report(onProgress, RealtimePackProgress.Create(
                RealtimePackStage.VegetationBake, progressEnd, 1f, detail: $"{builds.Count} vegetation bundle(s)"));
            yield return null;
        }

        static bool TryBakeVegetationAsset(RealtimePackOptions options, StreamingTileEntry tile, out string assetPath)
        {
            assetPath = null;
            string maskRel = $"vegetation_masks/{tile.TileId}_vegetation.png";
            string maskFull = Path.Combine(options.OutputRoot,
                maskRel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(maskFull))
                return false;

            string hmRel = tile.HeightmapPath;
            string hmFull = Path.Combine(options.OutputRoot,
                hmRel.Replace('/', Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(hmRel) || !File.Exists(hmFull))
                return false;

            string stagingFolder = $"{RealtimePackStagingUtility.VegetationRoot}/{tile.TileId}";
            ZGConnectPathUtils.EnsureAssetFolder(stagingFolder);

            string maskAsset = $"{stagingFolder}/mask.png";
            File.Copy(maskFull, ZGConnectPathUtils.AssetPathToFullPath(maskAsset), true);
            AssetDatabase.ImportAsset(maskAsset, ImportAssetOptions.ForceUpdate);
            var maskImporter = AssetImporter.GetAtPath(maskAsset) as TextureImporter;
            if (maskImporter != null)
            {
                maskImporter.isReadable = true;
                maskImporter.textureCompression = TextureImporterCompression.Uncompressed;
                maskImporter.SaveAndReimport();
            }

            Texture2D mask = AssetDatabase.LoadAssetAtPath<Texture2D>(maskAsset);
            if (mask == null)
                return false;

            Vector3 terrainSize = tile.GetTerrainSize();
            TerrainData td = RuntimeTerrainFactory.CreateTerrainData(
                hmFull,
                tile.HeightmapRes,
                terrainSize,
                flipHeightmapVertically: options.FlipHeightmapVertically);

            var terrainGo = Terrain.CreateTerrainGameObject(td);
            terrainGo.hideFlags = HideFlags.HideAndDontSave;
            Terrain terrain = terrainGo.GetComponent<Terrain>();

            float halfY = (options.Heightmap.Metadata.Settings.MaxHeight -
                           options.Heightmap.Metadata.Settings.MinHeight) * 0.5f;
            float midY = options.Heightmap.Metadata.Settings.MinHeight + halfY;
            int tileSize = options.Heightmap.Metadata.Settings.TileSizeMeters;
            Vector3 pos = tile.GetUnityPosition();
            var bounds = new Bounds(
                new Vector3(pos.x + tileSize * 0.5f, midY, pos.z + tileSize * 0.5f),
                new Vector3(tileSize, halfY * 2f, tileSize));

            try
            {
                var generator = new VegetationInstanceGenerator();
                VegetationChunk chunk = generator.GenerateChunk(
                    tile.TileId,
                    bounds,
                    mask,
                    options.VegetationRuleSet,
                    options.VegetationPrototypes,
                    terrain);

                BakedVegetationChunkAsset baked = BakedVegetationChunkAsset.FromRuntimeChunk(chunk);
                assetPath = $"{stagingFolder}/vegetation_{tile.TileId}.asset";
                AssetDatabase.CreateAsset(baked, assetPath);
                AssetDatabase.SaveAssets();
                return AssetDatabase.LoadAssetAtPath<BakedVegetationChunkAsset>(assetPath) != null;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(terrainGo);
                UnityEngine.Object.DestroyImmediate(td);
            }
        }

        static bool ShouldSkipBuild(RealtimePackOptions options, StreamingTileEntry tile, string bundleRel)
        {
            if (!options.SkipExisting)
                return false;

            if (options.FreshlyPackedTileIds != null && options.FreshlyPackedTileIds.Contains(tile.TileId))
                return false;

            return !string.IsNullOrEmpty(tile.VegetationBundlePath) &&
                   tile.VegetationBundlePath == bundleRel &&
                   RealtimePackReuseUtility.BundleFileExists(options.OutputRoot, bundleRel);
        }

        static void Report(Action<RealtimePackProgress> onProgress, RealtimePackProgress progress) =>
            onProgress?.Invoke(progress);

        static void CopyBuiltBundles(string buildOutputAsset, string bundleAssetOutput, List<AssetBundleBuild> builds)
        {
            foreach (AssetBundleBuild build in builds)
            {
                string src = Path.Combine(
                    ZGConnectPathUtils.AssetPathToFullPath(buildOutputAsset),
                    build.assetBundleName);
                string dst = Path.Combine(
                    ZGConnectPathUtils.AssetPathToFullPath(bundleAssetOutput),
                    build.assetBundleName);
                string dstDir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dstDir))
                    Directory.CreateDirectory(dstDir);
                if (File.Exists(src))
                    File.Copy(src, dst, true);
            }
        }

    }
}
