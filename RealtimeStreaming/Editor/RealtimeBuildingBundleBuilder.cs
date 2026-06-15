using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Stopwatch = System.Diagnostics.Stopwatch;
using UnityEditor;
using UnityEngine;
using ZGConnect.Editor;
using ZGConnect.RealtimeStreaming;

namespace ZGConnect.RealtimeStreaming.Editor
{
    public static class RealtimeBuildingBundleBuilder
    {
        public readonly struct BuildingBakeTarget
        {
            public readonly StreamingTileEntry Tile;
            public readonly string StyleKey;
            public readonly string FolderName;

            public BuildingBakeTarget(StreamingTileEntry tile, string styleKey, string folderName)
            {
                Tile = tile;
                StyleKey = styleKey;
                FolderName = folderName;
            }
        }

        public static List<BuildingBakeTarget> CollectBakeTargets(
            RealtimePackOptions options,
            List<StreamingTileEntry> tiles,
            string outputRoot)
        {
            var targets = new List<BuildingBakeTarget>();
            if (!options.PackBuildingBundles || tiles == null || tiles.Count == 0)
                return targets;

            foreach (StreamingTileEntry tile in tiles)
            {
                if (!IsSelectedTile(options, tile))
                    continue;

                if (ShouldBakeStyle(
                        options, tile, outputRoot,
                        StreamingPackBakeKeys.Facade, "building_meshes", options.PackFacadeBuildings,
                        facade => tile.HasFacadeBuildings = facade))
                {
                    targets.Add(new BuildingBakeTarget(tile, StreamingPackBakeKeys.Facade, "building_meshes"));
                }

                if (ShouldBakeStyle(
                        options, tile, outputRoot,
                        StreamingPackBakeKeys.OrthoRoof, "building_meshes_ortho", options.PackOrthoRoofBuildings,
                        ortho => tile.HasOrthoRoofBuildings = ortho))
                {
                    targets.Add(new BuildingBakeTarget(tile, StreamingPackBakeKeys.OrthoRoof, "building_meshes_ortho"));
                }
            }

            return targets;
        }

        public static IEnumerator BuildCoroutine(
            RealtimePackOptions options,
            List<StreamingTileEntry> tiles,
            BuildingLodStorageMode lodMode,
            string bakeFingerprint,
            float progressStart,
            float progressEnd,
            Action<RealtimePackProgress> onProgress = null)
        {
            if (tiles == null || tiles.Count == 0)
                yield break;

            string outputRoot = options.OutputRoot;
            if (string.IsNullOrEmpty(outputRoot))
                outputRoot = ZGConnectPathUtils.AssetPathToFullPath(RuntimeStreamingPaths.PackedDatasetAssetRoot);

            BuildingBakeVerboseLog.Global("bundle-build-start",
                $"tiles={tiles.Count} outputRoot={outputRoot} " +
                $"staging={RealtimePackStagingUtility.BuildingRoot} (temp, deleted after success)");

            RealtimePackReuseUtility.SyncBuildingFlagsFromDisk(tiles, outputRoot);
            StreamingDatasetManifest existingManifest =
                RealtimePackReuseUtility.TryLoadExistingManifest(outputRoot);
            string expectedFingerprint = bakeFingerprint;
            List<BuildingBakeTarget> targets = CollectBakeTargets(options, tiles, outputRoot);
            int total = targets.Count;
            BuildingBakeVerboseLog.Global("targets-collected", $"count={total}");
            if (total == 0)
            {
                Debug.LogWarning(
                    "[ZGConnect.Realtime] Building bundle bake enabled but no facade/ortho-roof tile targets were found. " +
                    "Enable Facade buildings and/or Ortho-roof buildings, or ensure packed GLBs exist under " +
                    "building_meshes/ and building_meshes_ortho/ in StreamingAssets/ZGConnect.");
                RealtimePackStagingUtility.CleanupBuildings();
                yield break;
            }

            string stagingRoot = RealtimePackStagingUtility.BuildingRoot;
            RealtimePackStagingUtility.DeleteAssetFolderIfExists(stagingRoot);
            ZGConnectPathUtils.EnsureAssetFolder(stagingRoot);

            var builds = new List<AssetBundleBuild>();
            int done = 0;
            int baked = 0;
            int failed = 0;
            int skipped = 0;
            int attempted = 0;
            float stagingEnd = progressStart + (progressEnd - progressStart) * 0.85f;
            string bundleAssetOutput = $"{RuntimeStreamingPaths.PackedDatasetAssetRoot}/bundles";

            Report(onProgress, RealtimePackProgress.Create(
                RealtimePackStage.BuildingBake, progressStart, 0f, 0, total, "Staging building prefabs"));
            yield return null;

            foreach (BuildingBakeTarget bakeTarget in targets)
            {
                StreamingTileEntry tile = bakeTarget.Tile;
                tile.BuildingBundlePaths ??= new Dictionary<string, string>();
                string styleKey = bakeTarget.StyleKey;
                string folderName = bakeTarget.FolderName;
                string bundleRel = $"bundles/buildings/{styleKey}/tiles_1x1/{tile.TileId}";

                if (ShouldSkipBuild(options, tile, styleKey, expectedFingerprint, existingManifest))
                {
                    BuildingBakeVerboseLog.Tile(tile.TileId, "bundle-skip",
                        $"style={styleKey} (Skip Existing — bundle + fingerprint match)");
                    skipped++;
                    done++;
                    yield return null;
                    continue;
                }

                attempted++;
                BuildingBakeVerboseLog.Tile(tile.TileId, "bundle-bake-start",
                    $"style={styleKey} target={done + 1}/{total}");

                float stageProgress = (float)(done + 1) / Mathf.Max(total, 1);
                Report(onProgress, RealtimePackProgress.Create(
                    RealtimePackStage.BuildingBake,
                    Mathf.Lerp(progressStart, stagingEnd, stageProgress),
                    stageProgress,
                    done + 1,
                    total,
                    $"{tile.TileId}/{styleKey}"));

                string stagingFolder = $"{RealtimePackStagingUtility.BuildingRoot}/{styleKey}/{tile.TileId}";
                var bakeResult = new BuildingBakeResult();
                IEnumerator bakeRoutine = RealtimePackBuildingBakeUtility.BakeBuildingTilePrefabCoroutine(
                    options,
                    tile,
                    styleKey,
                    folderName,
                    lodMode,
                    stagingFolder,
                    bakeFingerprint,
                    bakeResult);
                while (bakeRoutine.MoveNext())
                    yield return bakeRoutine.Current;

                if (bakeResult.Success && !string.IsNullOrEmpty(bakeResult.PrefabAssetPath))
                {
                    string[] bundleAssets = string.IsNullOrEmpty(bakeResult.PhysicsPrefabAssetPath)
                        ? new[] { bakeResult.PrefabAssetPath }
                        : new[] { bakeResult.PrefabAssetPath, bakeResult.PhysicsPrefabAssetPath };

                    builds.Add(new AssetBundleBuild
                    {
                        assetBundleName = $"buildings/{styleKey}/tiles_1x1/{tile.TileId}",
                        assetNames = bundleAssets,
                    });
                    tile.BuildingBundlePaths[styleKey] = bundleRel;
                    baked++;
                    BuildingBakeVerboseLog.Tile(tile.TileId, "bundle-bake-ok",
                        $"prefab={bakeResult.PrefabAssetPath}");
                }
                else
                {
                    failed++;
                    BuildingBakeVerboseLog.TileWarning(tile.TileId, "bundle-bake-failed",
                        $"style={styleKey} prefab={bakeResult.PrefabAssetPath ?? "null"}");
                }

                done++;
                yield return null;
            }

            if (builds.Count == 0)
            {
                string hint = attempted == 0
                    ? "No tiles entered the bake step — check Facade/Ortho pack options and packed GLB paths."
                    : failed > 0
                        ? "Check the Console for per-tile bake warnings."
                        : "All targets were skipped by Skip Existing.";
                Debug.LogError(
                    $"[ZGConnect.Realtime] No building bundles staged " +
                    $"(attempted {attempted}, baked {baked}, failed {failed}, skipped {skipped} of {total} targets). " +
                    "Runtime will fall back to raw GLB tiles without combined meshes or colliders. " +
                    hint);
                RealtimePackStagingUtility.CleanupBuildings();
                yield break;
            }

            if (failed > 0)
            {
                Debug.LogWarning(
                    $"[ZGConnect.Realtime] Building bake: {baked} succeeded, {failed} failed of {total}.");
            }

            ZGConnectPathUtils.EnsureAssetFolder(bundleAssetOutput);

            AssetDatabase.SaveAssets();

            if (!ValidateBundleSourceAssets(builds, out string validateError))
            {
                BuildingBakeVerboseLog.Global("asset-bundles-validate-failed", validateError);
                throw new InvalidOperationException(
                    $"[ZGConnect.Realtime] Building bundle build aborted: {validateError}");
            }

            string buildOutputAsset = $"{RealtimePackStagingUtility.BuildingRoot}/_bundle_output";
            RealtimePackStagingUtility.DeleteAssetFolderIfExists(buildOutputAsset);
            ZGConnectPathUtils.EnsureAssetFolder(buildOutputAsset);

            string compressionLabel = options.PackUncompressedAssetBundles ? "uncompressed" : "LZ4";
            BuildingBakeVerboseLog.Global("asset-bundles-build-start",
                $"count={builds.Count} compression={compressionLabel} batch=true " +
                $"stagingOutput={buildOutputAsset} finalOutput={bundleAssetOutput}");

            Report(onProgress, RealtimePackProgress.Create(
                RealtimePackStage.BuildingBake,
                stagingEnd,
                0f,
                detail: $"Archiving {builds.Count} bundle(s)"));
            yield return null;

            var archiveSw = Stopwatch.StartNew();
            BuildTarget activeBuildTarget = EditorUserBuildSettings.activeBuildTarget;
            bool archiveOk = BuildPipeline.BuildAssetBundles(
                buildOutputAsset,
                builds.ToArray(),
                RealtimeAssetBundleBuildUtility.GetPackBuildOptions(options.PackUncompressedAssetBundles),
                activeBuildTarget);

            if (!archiveOk)
            {
                BuildingBakeVerboseLog.Global("asset-bundles-build-failed",
                    $"BuildPipeline failed — prefab staging kept at {RealtimePackStagingUtility.BuildingRoot}");
                throw new InvalidOperationException(
                    "[ZGConnect.Realtime] Building bundle build failed. " +
                    $"Check Console for [BuildingBake] logs. Staging prefabs: {RealtimePackStagingUtility.BuildingRoot}");
            }

            long totalBytes = 0;
            foreach (AssetBundleBuild build in builds)
            {
                string src = Path.Combine(
                    ZGConnectPathUtils.AssetPathToFullPath(buildOutputAsset),
                    build.assetBundleName);
                long bytes = File.Exists(src) ? new FileInfo(src).Length : 0;
                totalBytes += bytes;
                BuildingBakeVerboseLog.Global("asset-bundle-archive-ok",
                    $"{build.assetBundleName} sizeMB={bytes / (1024f * 1024f):F1}");
            }

            BuildingBakeVerboseLog.Global("asset-bundles-archive-done",
                $"count={builds.Count} totalMB={totalBytes / (1024f * 1024f):F1} " +
                $"elapsedMs={archiveSw.ElapsedMilliseconds}");

            CopyBuiltBundlesToOutput(buildOutputAsset, bundleAssetOutput, builds);
            RealtimePackStagingUtility.DeleteAssetFolderIfExists(buildOutputAsset);

            BuildingBakeVerboseLog.Global("asset-bundles-build-ok",
                $"baked={baked} failed={failed} skipped={skipped} output={bundleAssetOutput}");

            RealtimePackStagingUtility.CleanupBuildings();
            AssetDatabase.Refresh();

            Report(onProgress, RealtimePackProgress.Create(
                RealtimePackStage.BuildingBake, progressEnd, 1f, detail: $"{builds.Count} building bundle(s)"));
            yield return null;
        }

        static bool IsSelectedTile(RealtimePackOptions options, StreamingTileEntry tile) =>
            tile != null &&
            !string.IsNullOrEmpty(tile.TileId) &&
            options.SelectedTileIds != null &&
            options.SelectedTileIds.Contains(tile.TileId);

        static bool ShouldBakeStyle(
            RealtimePackOptions options,
            StreamingTileEntry tile,
            string outputRoot,
            string styleKey,
            string folderName,
            bool packStyleEnabled,
            Action<bool> setFlag)
        {
            bool onDisk = RealtimePackReuseUtility.PackedBuildingGlbExists(outputRoot, folderName, tile.TileId);
            if (onDisk)
                setFlag(true);

            if (!onDisk && !packStyleEnabled)
                return false;

            if (!onDisk)
            {
                string datasetGlb = Path.Combine(
                    options.DatasetRoot ?? string.Empty,
                    folderName,
                    $"buildings_{tile.TileId}.glb");
                if (File.Exists(datasetGlb))
                    setFlag(true);
            }

            bool hasFlag = styleKey == StreamingPackBakeKeys.Facade
                ? tile.HasFacadeBuildings
                : tile.HasOrthoRoofBuildings;
            return onDisk || (packStyleEnabled && hasFlag);
        }

        static bool ShouldSkipBuild(
            RealtimePackOptions options,
            StreamingTileEntry tile,
            string styleKey,
            string expectedFingerprint,
            StreamingDatasetManifest existingManifest)
        {
            if (!options.SkipExisting)
                return false;

            if (options.FreshlyPackedTileIds != null && options.FreshlyPackedTileIds.Contains(tile.TileId))
                return false;

            if (!RealtimePackReuseUtility.HasBuildingBundle(tile, styleKey, options.OutputRoot))
                return false;

            if (existingManifest?.PackBake == null ||
                string.IsNullOrEmpty(expectedFingerprint))
                return true;

            return string.Equals(
                existingManifest.PackBake.Fingerprint,
                expectedFingerprint,
                StringComparison.Ordinal);
        }

        static bool ValidateBundleSourceAssets(List<AssetBundleBuild> builds, out string error)
        {
            error = null;
            foreach (AssetBundleBuild build in builds)
            {
                if (build.assetNames == null || build.assetNames.Length == 0)
                {
                    error = $"bundle '{build.assetBundleName}' has no source assets";
                    return false;
                }

                foreach (string assetPath in build.assetNames)
                {
                    if (string.IsNullOrEmpty(assetPath))
                    {
                        error = $"bundle '{build.assetBundleName}' has empty asset path";
                        return false;
                    }

                    UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                    if (asset != null)
                        continue;

                    if (assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

                    asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                    if (asset == null)
                    {
                        error = $"source asset not in AssetDatabase: {assetPath} (bundle {build.assetBundleName})";
                        BuildingBakeVerboseLog.Global("bundle-missing-source", error);
                        return false;
                    }
                }
            }

            return true;
        }

        static void Report(Action<RealtimePackProgress> onProgress, RealtimePackProgress progress) =>
            onProgress?.Invoke(progress);

        static void CopyBuiltBundlesToOutput(
            string buildOutputAsset,
            string bundleAssetOutput,
            List<AssetBundleBuild> builds)
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
