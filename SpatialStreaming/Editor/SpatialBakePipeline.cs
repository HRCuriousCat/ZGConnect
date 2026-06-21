using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using ZGConnect;
using ZGConnect.Editor;
using ZGConnect.SpatialStreaming;

namespace ZGConnect.SpatialStreaming.Editor
{
    public static class SpatialBakePipeline
    {
        public sealed class BakeReport
        {
            public int TilesProcessed;
            public int SubcellsBaked;
            public int TilesFailed;
            public string Message;
        }

        public static IEnumerator BakeCoroutine(
            SpatialBakeProfile profile,
            SpatialBakeProgressTracker progressTracker,
            Action<SpatialBakeProgress> onProgress,
            BakeReport report,
            IReadOnlyList<string> runtimeTileFilter = null)
        {
            report ??= new BakeReport();
            report.TilesProcessed = 0;
            report.SubcellsBaked = 0;
            report.TilesFailed = 0;

            if (profile == null)
            {
                report.Message = "Bake profile is null.";
                yield break;
            }

            progressTracker?.ResetForFullBake(profile);
            ReportProgress(progressTracker, onProgress, SpatialBakeStage.Prepare, 0f, detail: "Cleaning staging...");
            yield return null;

            string parentManifestPath = SpatialStreamingPaths.ParentManifestPath();
            if (!SpatialParentManifestReader.TryLoad(
                    parentManifestPath,
                    out int tileSizeMeters,
                    out Vector2Int manifestUnityOrigin,
                    out List<SpatialParentTileInfo> parentTiles,
                    out string parentError))
            {
                report.Message = parentError;
                yield break;
            }

            var selected = new HashSet<string>(StringComparer.Ordinal);
            if (runtimeTileFilter != null && runtimeTileFilter.Count > 0)
            {
                foreach (string id in runtimeTileFilter)
                {
                    if (!string.IsNullOrEmpty(id))
                        selected.Add(id);
                }
            }
            else if (profile.selectedTileIds != null && profile.selectedTileIds.Length > 0)
            {
                foreach (string id in profile.selectedTileIds)
                {
                    if (!string.IsNullOrEmpty(id))
                        selected.Add(id);
                }
            }

            string sourceFolder = profile.GetSourceFolder();
            string datasetRoot = SpatialStreamingPaths.DatasetRoot;
            string fingerprint = profile.ComputeFingerprintString();

            string stagingRoot = SpatialStreamingPaths.SpatialStagingAssetRoot;
            ZGConnectPathUtils.EnsureAssetFolder(stagingRoot);
            SpatialBakeStagingUtility.CleanStaging();
            ReportProgress(progressTracker, onProgress, SpatialBakeStage.Prepare, 1f, detail: "Staging ready");
            yield return null;

            var manifest = new SpatialDatasetManifest
            {
                Version = "1",
                SourceManifest = SpatialStreamingPaths.DefaultParentManifestRelativePath,
                BakeFingerprint = fingerprint,
                TileSizeMeters = tileSizeMeters,
                SubcellSizeMeters = profile.subcellSizeMeters,
            };

            var bundleBuilds = new List<AssetBundleBuild>();
            var bakedTileProxies = new List<SpatialBakedTileProxyInfo>();
            int totalTiles = CountBakeTargets(parentTiles, profile, selected, datasetRoot, sourceFolder);
            int doneTiles = 0;
            int tilesWithGlb = totalTiles;
            int tilesLoaded = 0;
            int tilesWithoutBuildings = 0;
            int prefabSaveFailures = 0;

            if (totalTiles == 0)
            {
                report.Message =
                    "No spatial bundles were staged. No tiles with source GLBs matched the bake profile " +
                    $"(manifest tiles={parentTiles.Count}, source={sourceFolder}).";
                SpatialBakeVerboseLog.Global("abort", report.Message);
                yield break;
            }

            if (profile.verboseBakeLogging)
            {
                SpatialBakeVerboseLog.Global(
                    "start",
                    $"targets={totalTiles} subcell={profile.subcellSizeMeters}m " +
                    $"splitThreshold={profile.minBuildingsForSubcellSplit} combine={profile.combineMeshesPerMaterial}");
            }

            foreach (SpatialParentTileInfo parentTile in parentTiles)
            {
                if (selected.Count > 0 && !selected.Contains(parentTile.TileId))
                    continue;

                if (!TileHasSourceGlb(parentTile, datasetRoot, sourceFolder))
                    continue;

                doneTiles++;
                ReportProgress(
                    progressTracker,
                    onProgress,
                    SpatialBakeStage.Tiles,
                    (float)(doneTiles - 1) / Mathf.Max(totalTiles, 1),
                    doneTiles,
                    totalTiles,
                    $"Loading {parentTile.TileId}");

                string glbPath = Path.Combine(
                    datasetRoot,
                    sourceFolder,
                    SpatialStreamingPaths.GetBuildingGlbFileName(parentTile.TileId));

                var loadState = new SpatialGlbEditorLoader.LoadState();
                IEnumerator loadRoutine = SpatialGlbEditorLoader.LoadGlbCoroutine(
                    glbPath, parentTile.TileId, loadState);
                while (loadRoutine.MoveNext())
                    yield return loadRoutine.Current;

                if (loadState.Instance == null)
                {
                    report.TilesFailed++;
                    Debug.LogWarning(
                        $"[ZGConnect.Spatial] Bake skipped '{parentTile.TileId}': {loadState.Error}");
                    SpatialGlbEditorLoader.Dispose(loadState);
                    continue;
                }

                try
                {
                    GameObject tileRoot = loadState.Instance;
                    tilesLoaded++;

                    SpatialBuildingHierarchyUtility.FlattenRuntimeGltfWrappers(tileRoot.transform);

                    BuildingsMetadataJson buildingMeta = SpatialGlbOriginUtility.TryLoadMetadata(
                        datasetRoot,
                        sourceFolder,
                        parentTile.TileId);
                    SpatialGlbOriginUtility.PrepareTileRootForBake(
                        tileRoot.transform,
                        parentTile.TileId,
                        parentTile.UnityPosition,
                        tileSizeMeters,
                        buildingMeta,
                        manifestUnityOrigin);

                    if (profile.buildingSurfaceSettings != null)
                    {
                        SpatialBuildingSurfaceBakeUtility.PrepareTileBuildingsForSurfaceMaterials(
                            tileRoot.transform,
                            parentTile.TileId,
                            parentTile.UnityPosition,
                            tileSizeMeters,
                            buildingMeta,
                            profile.buildingSurfaceSettings,
                            profile.verboseBakeLogging);
                    }

                    SpatialBuildingSubcellSplitter.SplitResult split =
                        SpatialBuildingSubcellSplitter.SplitTileBuildings(
                            tileRoot.transform,
                            parentTile.UnityPosition,
                            tileSizeMeters,
                            profile);

                    if (split.Subcells.Count == 0)
                    {
                        tilesWithoutBuildings++;
                        Debug.LogWarning(
                            $"[ZGConnect.Spatial] Bake skipped '{parentTile.TileId}': no building meshes found after GLB flatten.");
                        continue;
                    }

                    var tileEntry = new SpatialTileManifestEntry
                    {
                        TileId = parentTile.TileId,
                        UnityPosition = new[]
                        {
                            parentTile.UnityPosition.x,
                            parentTile.UnityPosition.y,
                            parentTile.UnityPosition.z,
                        },
                    };

                    if (!split.UsedSubcells && split.Subcells.TryGetValue(split.CoarseSubcellId, out _))
                    {
                        if (TryBakeSubcellPrefab(
                                profile,
                                parentTile.TileId,
                                split.CoarseSubcellId,
                                0,
                                0,
                                parentTile.UnityPosition,
                                tileSizeMeters,
                                tileRoot.transform,
                                split.Subcells[split.CoarseSubcellId],
                                stagingRoot,
                                out string prefabAssetPath,
                                out string bundleName,
                                out int buildingCount,
                                out string bakeError))
                        {
                            tileEntry.CoarseBundleRel = SpatialStreamingPaths.GetTileCoarseBundleRelativePath(
                                parentTile.TileId);
                            bundleBuilds.Add(CreateBundleBuild(bundleName, prefabAssetPath));
                            report.SubcellsBaked++;
                        }
                        else
                        {
                            report.TilesFailed++;
                            prefabSaveFailures++;
                            SpatialBakeVerboseLog.TileWarning(
                                parentTile.TileId,
                                "coarse-bake-failed",
                                bakeError ?? "unknown");
                        }
                    }
                    else
                    {
                        int subcellTotal = split.Subcells.Count;
                        int subcellDone = 0;
                        foreach (KeyValuePair<string, List<Transform>> kvp in split.Subcells)
                        {
                            if (!TryParseSubcellId(kvp.Key, out int gx, out int gy))
                                continue;

                            subcellDone++;
                            float tileStageProgress =
                                ((doneTiles - 1) + (float)subcellDone / Mathf.Max(1, subcellTotal)) /
                                Mathf.Max(1, totalTiles);
                            ReportProgress(
                                progressTracker,
                                onProgress,
                                SpatialBakeStage.Tiles,
                                tileStageProgress,
                                subcellDone,
                                subcellTotal,
                                $"{parentTile.TileId} {kvp.Key}");

                            var subcellRoot = new GameObject($"Subcell_{parentTile.TileId}_{kvp.Key}");
                            subcellRoot.transform.SetParent(tileRoot.transform, false);
                            subcellRoot.transform.localPosition = Vector3.zero;

                            var duplicatedBuildings = new List<Transform>();
                            foreach (Transform building in kvp.Value)
                            {
                                if (building == null)
                                    continue;

                                GameObject copy = UnityEngine.Object.Instantiate(
                                    building.gameObject,
                                    building.position,
                                    building.rotation,
                                    subcellRoot.transform);
                                copy.name = building.name;
                                duplicatedBuildings.Add(copy.transform);
                            }

                            bool bakedDetail;
                            string detailAssetPath;
                            string bundleName;
                            int buildingCount;
                            string bakeError;
                            string loadKind;

                            if (profile.detailBundleMode == SpatialDetailBundleMode.MeshAssets)
                            {
                                var meshDetailState = new MeshDetailBakeState();
                                IEnumerator meshDetailRoutine = TryBakeSubcellMeshDetailAssetCoroutine(
                                    profile,
                                    parentTile.TileId,
                                    kvp.Key,
                                    parentTile.UnityPosition,
                                    tileSizeMeters,
                                    subcellRoot.transform,
                                    duplicatedBuildings,
                                    stagingRoot,
                                    meshDetailState);
                                while (meshDetailRoutine.MoveNext())
                                    yield return meshDetailRoutine.Current;

                                bakedDetail = meshDetailState.Success;
                                detailAssetPath = meshDetailState.DetailAssetPath;
                                bundleName = meshDetailState.BundleName;
                                buildingCount = meshDetailState.BuildingCount;
                                bakeError = meshDetailState.Error;
                                loadKind = SpatialSubcellManifestEntry.MeshDetailLoadKind;
                            }
                            else
                            {
                                bakedDetail = TryBakeSubcellPrefab(
                                    profile,
                                    parentTile.TileId,
                                    kvp.Key,
                                    gx,
                                    gy,
                                    parentTile.UnityPosition,
                                    tileSizeMeters,
                                    subcellRoot.transform,
                                    duplicatedBuildings,
                                    stagingRoot,
                                    out detailAssetPath,
                                    out bundleName,
                                    out buildingCount,
                                    out bakeError);
                                loadKind = SpatialSubcellManifestEntry.PrefabLoadKind;
                            }

                            if (!bakedDetail)
                            {
                                UnityEngine.Object.DestroyImmediate(subcellRoot);
                                prefabSaveFailures++;
                                SpatialBakeVerboseLog.SubcellWarning(
                                    parentTile.TileId,
                                    kvp.Key,
                                    "subcell-bake-failed",
                                    bakeError ?? "unknown");
                                continue;
                            }

                            tileEntry.Subcells.Add(new SpatialSubcellManifestEntry
                            {
                                SubcellId = kvp.Key,
                                GridX = gx,
                                GridY = gy,
                                BundleRel = SpatialStreamingPaths.GetSubcellBundleRelativePath(
                                    parentTile.TileId, kvp.Key),
                                BuildingCount = buildingCount,
                                LoadKind = loadKind,
                            });

                            bundleBuilds.Add(CreateBundleBuild(bundleName, detailAssetPath));
                            report.SubcellsBaked++;

                            string subcellProxyPath = null;
                            string subcellProxyBundle = null;
                            string subcellProxyError = null;
                            if (profile.bakeFootprintProxies && profile.bakeSubcellFootprintProxies &&
                                SpatialBakeProxyUtility.TryBakeSubcellProxy(
                                    profile,
                                    parentTile.TileId,
                                    kvp.Key,
                                    parentTile.UnityPosition,
                                    tileSizeMeters,
                                    subcellRoot.transform,
                                    duplicatedBuildings,
                                    stagingRoot,
                                    out subcellProxyPath,
                                    out subcellProxyBundle,
                                    out subcellProxyError))
                            {
                                SpatialSubcellManifestEntry subcellEntry =
                                    tileEntry.Subcells[tileEntry.Subcells.Count - 1];
                                subcellEntry.ProxyBundleRel =
                                    SpatialStreamingPaths.GetSubcellProxyBundleRelativePath(
                                        parentTile.TileId, kvp.Key);
                                bundleBuilds.Add(CreateBundleBuild(subcellProxyBundle, subcellProxyPath));

                                if (SpatialTileIdUtility.TryParse(parentTile.TileId, out int proxyLeft, out int proxyBottom))
                                {
                                    bakedTileProxies.Add(new SpatialBakedTileProxyInfo
                                    {
                                        TileId = parentTile.TileId,
                                        Left = proxyLeft,
                                        Bottom = proxyBottom,
                                        UnityPosition = parentTile.UnityPosition,
                                        PrefabAssetPath = subcellProxyPath,
                                    });
                                }
                            }
                            else if (profile.verboseBakeLogging &&
                                     profile.bakeFootprintProxies &&
                                     profile.bakeSubcellFootprintProxies)
                            {
                                SpatialBakeVerboseLog.SubcellWarning(
                                    parentTile.TileId,
                                    kvp.Key,
                                    "subcell-proxy-failed",
                                    subcellProxyError ?? "unknown");
                            }

                            UnityEngine.Object.DestroyImmediate(subcellRoot);
                            yield return null;
                        }

                        if (tileEntry.Subcells.Count == 0)
                            report.TilesFailed++;
                    }

                    if (tileEntry.UsesSubcells || !string.IsNullOrEmpty(tileEntry.CoarseBundleRel))
                    {
                        manifest.Tiles.Add(tileEntry);
                        report.TilesProcessed++;
                    }

                    if (profile.bakeFootprintProxies && !split.UsedSubcells)
                    {
                        List<Transform> proxyBuildings =
                            SpatialBuildingSubcellSplitter.CollectBuildingRoots(tileRoot.transform);
                        if (SpatialBakeProxyUtility.TryBakeTileProxy(
                                profile,
                                parentTile.TileId,
                                parentTile.UnityPosition,
                                manifest.TileSizeMeters,
                                tileRoot.transform,
                                proxyBuildings,
                                stagingRoot,
                                out string proxyPrefabPath,
                                out string proxyBundleName,
                                out string proxyError))
                        {
                            tileEntry.ProxyBundleRel =
                                SpatialStreamingPaths.GetTileProxyBundleRelativePath(parentTile.TileId);
                            bundleBuilds.Add(CreateBundleBuild(proxyBundleName, proxyPrefabPath));

                            if (SpatialTileIdUtility.TryParse(parentTile.TileId, out int left, out int bottom))
                            {
                                bakedTileProxies.Add(new SpatialBakedTileProxyInfo
                                {
                                    TileId = parentTile.TileId,
                                    Left = left,
                                    Bottom = bottom,
                                    UnityPosition = parentTile.UnityPosition,
                                    PrefabAssetPath = proxyPrefabPath,
                                });
                            }
                        }
                        else if (profile.verboseBakeLogging)
                        {
                            SpatialBakeVerboseLog.TileWarning(
                                parentTile.TileId,
                                "proxy-bake-failed",
                                proxyError ?? "unknown");
                        }
                    }
                }
                finally
                {
                    SpatialGlbEditorLoader.Dispose(loadState);
                }

                yield return null;
            }

            ReportProgress(progressTracker, onProgress, SpatialBakeStage.Tiles, 1f, totalTiles, totalTiles, "Tiles complete");
            yield return null;

            if (bundleBuilds.Count == 0)
            {
                report.Message =
                    $"No spatial bundles were staged. glbTargets={tilesWithGlb}, loaded={tilesLoaded}, " +
                    $"noBuildings={tilesWithoutBuildings}, prefabFailures={prefabSaveFailures}, " +
                    $"loadFailures={report.TilesFailed}.";
                yield break;
            }

            if (profile.bakeHlodSupertiles)
            {
                IEnumerator hlodRoutine = SpatialSupertileBakeUtility.BakeSupertilesCoroutine(
                    profile,
                    tileSizeMeters,
                    bakedTileProxies,
                    manifest,
                    bundleBuilds,
                    stagingRoot,
                    (hlodProgress, hlodDetail) =>
                    {
                        ReportProgress(
                            progressTracker,
                            onProgress,
                            SpatialBakeStage.Hlod,
                            hlodProgress,
                            detail: hlodDetail);
                    });
                while (hlodRoutine.MoveNext())
                    yield return hlodRoutine.Current;

                ReportProgress(progressTracker, onProgress, SpatialBakeStage.Hlod, 1f, detail: "HLOD complete");
                yield return null;
            }

            var bundleResult = new SpatialStagingBundleUtility.SpatialBundleBatchResult();
            IEnumerator bundleRoutine = SpatialStagingBundleUtility.BuildAndCopyBundlesBatchedCoroutine(
                profile,
                bundleBuilds,
                (bundleProgress, bundleDetail) =>
                {
                    ReportProgress(
                        progressTracker,
                        onProgress,
                        SpatialBakeStage.AssetBundles,
                        bundleProgress,
                        detail: bundleDetail);
                },
                bundleResult);
            while (bundleRoutine.MoveNext())
                yield return bundleRoutine.Current;

            if (!bundleResult.Succeeded)
            {
                report.Message = bundleResult.Error;
                yield break;
            }

            ReportProgress(progressTracker, onProgress, SpatialBakeStage.AssetBundles, 1f, detail: "Bundles copied");
            yield return null;

            ReportProgress(progressTracker, onProgress, SpatialBakeStage.Manifest, 0.2f, detail: "Writing manifest...");
            yield return null;

            string manifestPath = SpatialStreamingPaths.SpatialManifestPath();
            if (selected.Count > 0)
                MergeManifestWithExisting(manifest, selected, manifestPath);
            manifest.SaveToFile(manifestPath);
            SpatialBakeRegionUtility.InvalidateMapCaches();

            SpatialBakeStagingUtility.CleanStaging();
            AssetDatabase.Refresh();

            report.Message =
                $"Baked {report.SubcellsBaked} bundle(s) across {report.TilesProcessed} tile(s). " +
                $"Manifest: {manifestPath}";
            SpatialBakeVerboseLog.Global("complete", report.Message);
            ReportProgress(progressTracker, onProgress, SpatialBakeStage.Manifest, 1f, detail: "Done");
        }

        public static IEnumerator BuildBundlesFromStagingCoroutine(
            SpatialBakeProfile profile,
            bool rebuildManifest,
            SpatialBakeProgressTracker progressTracker,
            Action<SpatialBakeProgress> onProgress,
            BakeReport report)
        {
            report ??= new BakeReport();
            report.TilesProcessed = 0;
            report.SubcellsBaked = 0;
            report.TilesFailed = 0;

            progressTracker?.ResetForStagingBuild(rebuildManifest);

            string stagingRoot = SpatialStreamingPaths.SpatialStagingAssetRoot;
            int stagedPrefabs = SpatialStagingBundleUtility.CountStagedPrefabs(stagingRoot);
            if (stagedPrefabs == 0)
            {
                report.Message = "No staged spatial bundle assets found under _spatial_bake_staging.";
                yield break;
            }

            ReportProgress(
                progressTracker,
                onProgress,
                SpatialBakeStage.CollectStaging,
                0.2f,
                detail: $"Collecting {stagedPrefabs} staged asset(s)...");
            yield return null;

            List<AssetBundleBuild> builds = SpatialStagingBundleUtility.CollectBundleBuildsFromStaging(stagingRoot);
            if (builds.Count == 0)
            {
                report.Message = "Staged spatial assets exist but none could be mapped to bundle builds.";
                yield break;
            }

            report.SubcellsBaked = builds.Count;
            ReportProgress(
                progressTracker,
                onProgress,
                SpatialBakeStage.CollectStaging,
                1f,
                builds.Count,
                builds.Count,
                $"Mapped {builds.Count} bundle(s)");
            yield return null;

            var bundleResult = new SpatialStagingBundleUtility.SpatialBundleBatchResult();
            IEnumerator bundleRoutine = SpatialStagingBundleUtility.BuildAndCopyBundlesBatchedCoroutine(
                profile,
                builds,
                (bundleProgress, bundleDetail) =>
                {
                    ReportProgress(
                        progressTracker,
                        onProgress,
                        SpatialBakeStage.AssetBundles,
                        bundleProgress,
                        detail: bundleDetail);
                },
                bundleResult);
            while (bundleRoutine.MoveNext())
                yield return bundleRoutine.Current;

            if (!bundleResult.Succeeded)
            {
                report.Message = bundleResult.Error;
                yield break;
            }

            ReportProgress(progressTracker, onProgress, SpatialBakeStage.AssetBundles, 1f, detail: "Bundles copied");
            yield return null;

            if (rebuildManifest)
            {
                ReportProgress(progressTracker, onProgress, SpatialBakeStage.Manifest, 0.3f, detail: "Rebuilding manifest...");
                yield return null;

                SpatialDatasetManifest manifest =
                    SpatialStagingBundleUtility.RebuildManifestFromStaging(profile, stagingRoot);
                string manifestPath = SpatialStreamingPaths.SpatialManifestPath();
                manifest.SaveToFile(manifestPath);
                SpatialBakeRegionUtility.InvalidateMapCaches();
                report.TilesProcessed = manifest.Tiles?.Count ?? 0;
            }

            AssetDatabase.Refresh();

            report.Message =
                $"Built {builds.Count} bundle(s) from staging into {SpatialStreamingPaths.SpatialBundlesRoot}" +
                (rebuildManifest ? $" and updated {SpatialStreamingPaths.SpatialManifestRelativePath}" : ".");
            SpatialBakeVerboseLog.Global("staging-recovery-complete", report.Message);
            ReportProgress(
                progressTracker,
                onProgress,
                SpatialBakeStage.Manifest,
                1f,
                detail: "Done");
        }

        static void MergeManifestWithExisting(
            SpatialDatasetManifest baked,
            HashSet<string> bakedTileIds,
            string manifestPath)
        {
            if (baked == null || bakedTileIds == null || bakedTileIds.Count == 0)
                return;

            if (!File.Exists(manifestPath))
                return;

            SpatialDatasetManifest existing = SpatialDatasetManifest.LoadFromFile(manifestPath);
            if (existing?.Tiles == null)
                return;

            var mergedTiles = new List<SpatialTileManifestEntry>();
            foreach (SpatialTileManifestEntry tile in existing.Tiles)
            {
                if (tile == null || string.IsNullOrEmpty(tile.TileId))
                    continue;

                if (bakedTileIds.Contains(tile.TileId))
                    continue;

                if (!SpatialStagingBundleUtility.TileEntryHasBundleOnDisk(tile))
                    continue;

                mergedTiles.Add(tile);
            }

            if (baked.Tiles != null)
                mergedTiles.AddRange(baked.Tiles);

            baked.Tiles = mergedTiles;

            if (existing.Supertiles == null || existing.Supertiles.Count == 0)
                return;

            var rebakedSupertileIds = new HashSet<string>();
            if (baked.Supertiles != null)
            {
                foreach (SpatialSupertileManifestEntry entry in baked.Supertiles)
                {
                    if (entry != null && !string.IsNullOrEmpty(entry.SupertileId))
                        rebakedSupertileIds.Add(entry.SupertileId);
                }
            }

            var mergedSupertiles = new List<SpatialSupertileManifestEntry>();
            foreach (SpatialSupertileManifestEntry entry in existing.Supertiles)
            {
                if (entry == null || string.IsNullOrEmpty(entry.SupertileId))
                    continue;

                if (SupertileTouchesAnyTile(entry, bakedTileIds))
                    continue;

                mergedSupertiles.Add(entry);
            }

            if (baked.Supertiles != null)
                mergedSupertiles.AddRange(baked.Supertiles);

            baked.Supertiles = mergedSupertiles;
        }

        static bool SupertileTouchesAnyTile(SpatialSupertileManifestEntry entry, HashSet<string> tileIds)
        {
            if (entry?.ChildTileIds == null || tileIds == null || tileIds.Count == 0)
                return false;

            foreach (string tileId in entry.ChildTileIds)
            {
                if (tileIds.Contains(tileId))
                    return true;
            }

            return false;
        }

        static int CountBakeTargets(
            List<SpatialParentTileInfo> parentTiles,
            SpatialBakeProfile profile,
            HashSet<string> selected,
            string datasetRoot,
            string sourceFolder)
        {
            int count = 0;
            foreach (SpatialParentTileInfo tile in parentTiles)
            {
                if (selected.Count > 0 && !selected.Contains(tile.TileId))
                    continue;

                if (TileHasSourceGlb(tile, datasetRoot, sourceFolder))
                    count++;
            }

            return count;
        }

        static bool TileHasSourceGlb(
            SpatialParentTileInfo tile,
            string datasetRoot,
            string sourceFolder)
        {
            if (tile == null)
                return false;

            string glbPath = Path.Combine(
                datasetRoot,
                sourceFolder,
                SpatialStreamingPaths.GetBuildingGlbFileName(tile.TileId));

            return File.Exists(glbPath);
        }

        sealed class MeshDetailBakeState
        {
            public bool Success;
            public string DetailAssetPath;
            public string BundleName;
            public int BuildingCount;
            public string Error;
        }

        static void ReportProgress(
            SpatialBakeProgressTracker tracker,
            Action<SpatialBakeProgress> onProgress,
            SpatialBakeStage stage,
            float stageProgress,
            int current = 0,
            int total = 0,
            string detail = null)
        {
            if (tracker == null)
                return;

            var progress = SpatialBakeProgress.Create(
                stage,
                stageProgress,
                tracker.StageToOverall(stage, stageProgress),
                current,
                total,
                detail);
            tracker.Apply(progress);
            onProgress?.Invoke(progress);
        }

        static IEnumerator TryBakeSubcellMeshDetailAssetCoroutine(
            SpatialBakeProfile profile,
            string tileId,
            string subcellId,
            Vector3 tileUnityPosition,
            int tileSizeMeters,
            Transform bakeRoot,
            List<Transform> buildings,
            string stagingRoot,
            MeshDetailBakeState state)
        {
            state.Success = false;
            state.DetailAssetPath = null;
            state.BundleName = null;
            state.BuildingCount = buildings?.Count ?? 0;
            state.Error = null;

            if (bakeRoot == null)
            {
                state.Error = "bakeRoot is null";
                yield break;
            }

            if (state.BuildingCount == 0)
            {
                state.Error = "no buildings in subcell";
                yield break;
            }

            var ownedMeshes = new List<Mesh>();
            List<MeshRenderer> renderers = SpatialBuildingSubcellSplitter.CollectRenderers(buildings);
            if (renderers.Count == 0)
            {
                state.Error = "no MeshRenderers under buildings";
                yield break;
            }

            yield return null;

            int uniqueMaterialsBefore = SpatialMaterialCombineUtility.CountUniqueMaterials(renderers);
            int normalized = SpatialMaterialCombineUtility.NormalizeForCombine(
                renderers,
                profile.combineMaterialOverride,
                profile.deduplicateMaterialsByShader);
            int uniqueMaterialsAfter = SpatialMaterialCombineUtility.CountUniqueMaterials(renderers);

            if (profile.verboseBakeLogging)
            {
                SpatialBakeVerboseLog.Subcell(
                    tileId,
                    subcellId,
                    "mesh-detail-materials-normalized",
                    $"renderers={renderers.Count} uniqueMaterials {uniqueMaterialsBefore}->{uniqueMaterialsAfter} " +
                    $"slotsChanged={normalized}");
            }

            GameObject combinedRoot = SpatialMeshCombineUtility.BuildCombinedRenderRoot(
                bakeRoot,
                renderers,
                profile.combineMeshesPerMaterial,
                ownedMeshes);

            if (combinedRoot == null)
            {
                state.Error = "BuildCombinedRenderRoot returned null";
                yield break;
            }

            if (combinedRoot.transform.childCount == 0)
            {
                state.Error =
                    $"combined root has no render children (renderers={renderers.Count}, ownedMeshes={ownedMeshes.Count})";
                yield break;
            }

            yield return null;

            if (profile.buildingSurfaceSettings != null)
            {
                int remapped = SpatialBuildingMaterialApplier.ApplyFacadeMaterialsToCombinedInstance(
                    combinedRoot,
                    tileId,
                    tileUnityPosition,
                    tileSizeMeters,
                    profile.buildingSurfaceSettings);

                if (profile.verboseBakeLogging)
                {
                    SpatialBakeVerboseLog.Subcell(
                        tileId,
                        subcellId,
                        "mesh-detail-materials-facade",
                        $"slots={remapped} settings={profile.buildingSurfaceSettings.name}");
                }
            }

            SpatialMeshCombineUtility.DisableSourceRenderers(renderers);

            string stagingFolder = $"{stagingRoot}/{tileId}";
            ZGConnectPathUtils.EnsureAssetFolder(stagingFolder);
            state.DetailAssetPath = $"{stagingFolder}/Spatial_{tileId}_{subcellId}_Detail.asset";
            state.BundleName = $"{tileId}/{subcellId}";

            AssetDatabase.DeleteAsset(state.DetailAssetPath);
            yield return null;

            var detailAsset = ScriptableObject.CreateInstance<SpatialMeshDetailAsset>();
            detailAsset.TileId = tileId;
            detailAsset.SubcellId = subcellId;
            AssetDatabase.CreateAsset(detailAsset, state.DetailAssetPath);

            var entries = new List<SpatialMeshDetailAsset.RendererEntry>();
            MeshRenderer[] combinedRenderers = combinedRoot.GetComponentsInChildren<MeshRenderer>(true);
            const int renderersPerYield = 4;
            for (int r = 0; r < combinedRenderers.Length; r++)
            {
                MeshRenderer renderer = combinedRenderers[r];
                if (renderer == null)
                    continue;

                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null)
                    continue;

                Mesh meshAsset = UnityEngine.Object.Instantiate(filter.sharedMesh);
                meshAsset.name = MakeAssetObjectName(renderer.gameObject.name, entries.Count, "Mesh");
                meshAsset.hideFlags = HideFlags.None;
                AssetDatabase.AddObjectToAsset(meshAsset, detailAsset);

                Material[] sourceMaterials = renderer.sharedMaterials;
                var materials = new Material[sourceMaterials.Length];
                for (int i = 0; i < sourceMaterials.Length; i++)
                    materials[i] = PersistMaterialReference(sourceMaterials[i], detailAsset, renderer.gameObject.name, i);

                entries.Add(new SpatialMeshDetailAsset.RendererEntry
                {
                    Name = renderer.gameObject.name,
                    Mesh = meshAsset,
                    Materials = materials,
                });

                if ((r + 1) % renderersPerYield == 0)
                    yield return null;
            }

            if (entries.Count == 0)
            {
                AssetDatabase.DeleteAsset(state.DetailAssetPath);
                state.Error = "combined root produced no mesh detail renderer entries";
                yield break;
            }

            detailAsset.Renderers = entries.ToArray();
            EditorUtility.SetDirty(detailAsset);
            AssetDatabase.SaveAssets();
            yield return null;
            AssetDatabase.ImportAsset(state.DetailAssetPath);
            yield return null;

            if (profile.verboseBakeLogging)
            {
                SpatialBakeVerboseLog.Subcell(
                    tileId,
                    subcellId,
                    "mesh-detail-asset-ok",
                    $"{state.DetailAssetPath} renderers={entries.Count}");
            }

            state.Success = true;
        }

        static Material PersistMaterialReference(
            Material material,
            SpatialMeshDetailAsset owner,
            string rendererName,
            int slot)
        {
            if (material == null || owner == null)
                return material;

            if (!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(material)))
                return material;

            Material copy = new Material(material)
            {
                name = MakeAssetObjectName(rendererName, slot, "Material"),
                hideFlags = HideFlags.None,
            };
            AssetDatabase.AddObjectToAsset(copy, owner);
            EditorUtility.SetDirty(copy);
            return copy;
        }

        static string MakeAssetObjectName(string source, int index, string suffix)
        {
            string safe = string.IsNullOrEmpty(source) ? "Spatial" : source;
            foreach (char invalid in Path.GetInvalidFileNameChars())
                safe = safe.Replace(invalid, '_');
            return $"{safe}_{index}_{suffix}";
        }

        static bool TryBakeSubcellPrefab(
            SpatialBakeProfile profile,
            string tileId,
            string subcellId,
            int gridX,
            int gridY,
            Vector3 tileUnityPosition,
            int tileSizeMeters,
            Transform bakeRoot,
            List<Transform> buildings,
            string stagingRoot,
            out string prefabAssetPath,
            out string bundleName,
            out int buildingCount,
            out string error)
        {
            prefabAssetPath = null;
            bundleName = null;
            buildingCount = buildings?.Count ?? 0;
            error = null;

            if (bakeRoot == null)
            {
                error = "bakeRoot is null";
                return false;
            }

            if (buildingCount == 0)
            {
                error = "no buildings in subcell";
                return false;
            }

            if (profile.verboseBakeLogging)
            {
                SpatialBakeVerboseLog.Subcell(
                    tileId,
                    subcellId,
                    "start",
                    $"buildings={buildingCount} bakeRoot={bakeRoot.name}");
            }

            var ownedMeshes = new List<Mesh>();
            List<MeshRenderer> renderers = SpatialBuildingSubcellSplitter.CollectRenderers(buildings);
            if (renderers.Count == 0)
            {
                error = "no MeshRenderers under buildings";
                return false;
            }

            int uniqueMaterialsBefore = SpatialMaterialCombineUtility.CountUniqueMaterials(renderers);
            int normalized = SpatialMaterialCombineUtility.NormalizeForCombine(
                renderers,
                profile.combineMaterialOverride,
                profile.deduplicateMaterialsByShader);
            int uniqueMaterialsAfter = SpatialMaterialCombineUtility.CountUniqueMaterials(renderers);

            if (profile.verboseBakeLogging)
            {
                SpatialBakeVerboseLog.Subcell(
                    tileId,
                    subcellId,
                    "materials-normalized",
                    $"renderers={renderers.Count} uniqueMaterials {uniqueMaterialsBefore}->{uniqueMaterialsAfter} " +
                    $"slotsChanged={normalized}");
            }

            GameObject combinedRoot = SpatialMeshCombineUtility.BuildCombinedRenderRoot(
                bakeRoot,
                renderers,
                profile.combineMeshesPerMaterial,
                ownedMeshes);

            if (combinedRoot == null)
            {
                error = "BuildCombinedRenderRoot returned null";
                return false;
            }

            if (combinedRoot.transform.childCount == 0)
            {
                error = $"combined root has no render children (renderers={renderers.Count}, ownedMeshes={ownedMeshes.Count})";
                return false;
            }

            if (profile.verboseBakeLogging)
            {
                SpatialBakeVerboseLog.Subcell(
                    tileId,
                    subcellId,
                    "combine-ok",
                    $"{SpatialBakeVerboseLog.HierarchyStats(combinedRoot.transform)} " +
                    $"{SpatialBakeVerboseLog.CountEphemeralAssets(combinedRoot.transform)}");
            }

            if (profile.buildingSurfaceSettings != null)
            {
                int remapped = SpatialBuildingMaterialApplier.ApplyFacadeMaterialsToCombinedInstance(
                    combinedRoot,
                    tileId,
                    tileUnityPosition,
                    tileSizeMeters,
                    profile.buildingSurfaceSettings);

                if (profile.verboseBakeLogging)
                {
                    SpatialBakeVerboseLog.Subcell(
                        tileId,
                        subcellId,
                        "materials-facade",
                        $"slots={remapped} settings={profile.buildingSurfaceSettings.name}");
                }
            }
            else if (profile.verboseBakeLogging)
            {
                SpatialBakeVerboseLog.SubcellWarning(
                    tileId,
                    subcellId,
                    "materials-skipped",
                    "buildingSurfaceSettings not assigned on bake profile");
            }

            SpatialMeshCombineUtility.DisableSourceRenderers(renderers);

            string stagingFolder = $"{stagingRoot}/{tileId}";
            ZGConnectPathUtils.EnsureAssetFolder(stagingFolder);
            prefabAssetPath = $"{stagingFolder}/Spatial_{tileId}_{subcellId}.prefab";

            Transform originalParent = combinedRoot.transform.parent;
            // Detach at tile-local origin so runtime can place via manifest unityPosition.
            combinedRoot.transform.SetParent(null, false);
            combinedRoot.transform.localPosition = Vector3.zero;
            combinedRoot.transform.localRotation = Quaternion.identity;
            combinedRoot.transform.localScale = Vector3.one;

            try
            {
                PrepareHierarchyForPrefabSave(combinedRoot);
                foreach (Mesh mesh in ownedMeshes)
                {
                    if (mesh != null)
                        mesh.hideFlags = HideFlags.None;
                }

                if (profile.verboseBakeLogging)
                {
                    SpatialBakeVerboseLog.Subcell(
                        tileId,
                        subcellId,
                        "prefab-pass1-start",
                        SpatialBakeVerboseLog.PrefabSaveState(prefabAssetPath));
                }

                GameObject pass1 = PrefabUtility.SaveAsPrefabAsset(combinedRoot, prefabAssetPath);
                AssetDatabase.SaveAssets();
                bool pass1Loaded = pass1 != null || TryLoadPrefabAsset(prefabAssetPath, out pass1);
                if (!pass1Loaded)
                {
                    error =
                        $"prefab pass1 failed — {SpatialBakeVerboseLog.PrefabSaveState(prefabAssetPath)} " +
                        $"{SpatialBakeVerboseLog.CountEphemeralAssets(combinedRoot.transform)} " +
                        $"{SpatialBakeVerboseLog.HierarchyStats(combinedRoot.transform)}";
                    SpatialBakeVerboseLog.SubcellWarning(tileId, subcellId, "prefab-pass1-failed", error);
                    return false;
                }

                if (profile.verboseBakeLogging)
                    SpatialBakeVerboseLog.Subcell(tileId, subcellId, "prefab-pass1-ok", prefabAssetPath);

                int embeddedMeshes = PersistEphemeralMeshes(combinedRoot, prefabAssetPath, out int skippedMeshes);
                int embeddedMaterials = PersistEphemeralMaterials(combinedRoot, prefabAssetPath, out int skippedMaterials);

                if (profile.verboseBakeLogging)
                {
                    SpatialBakeVerboseLog.Subcell(
                        tileId,
                        subcellId,
                        "prefab-embed",
                        $"meshes={embeddedMeshes} skippedMeshes={skippedMeshes} " +
                        $"materials={embeddedMaterials} skippedMaterials={skippedMaterials}");
                }

                if (embeddedMeshes > 0 || embeddedMaterials > 0)
                {
                    AssetDatabase.SaveAssets();
                    pass1 = PrefabUtility.SaveAsPrefabAsset(combinedRoot, prefabAssetPath);
                }

                bool pass2Loaded = pass1 != null || TryLoadPrefabAsset(prefabAssetPath, out pass1);
                if (!pass2Loaded)
                {
                    error =
                        $"prefab pass2 failed after embed — {SpatialBakeVerboseLog.PrefabSaveState(prefabAssetPath)} " +
                        $"embeddedMeshes={embeddedMeshes} embeddedMaterials={embeddedMaterials}";
                    SpatialBakeVerboseLog.SubcellWarning(tileId, subcellId, "prefab-pass2-failed", error);
                    return false;
                }

                if (profile.verboseBakeLogging)
                    SpatialBakeVerboseLog.Subcell(tileId, subcellId, "prefab-pass2-ok", prefabAssetPath);
            }
            finally
            {
                if (originalParent != null && combinedRoot != null)
                    combinedRoot.transform.SetParent(originalParent, false);
            }

            bundleName = $"{tileId}/{subcellId}";
            return true;
        }

        static AssetBundleBuild CreateBundleBuild(string bundleName, params string[] assetPaths)
        {
            var names = new List<string>();
            if (assetPaths != null)
            {
                foreach (string path in assetPaths)
                {
                    if (!string.IsNullOrEmpty(path))
                        names.Add(path);
                }
            }

            return new AssetBundleBuild
            {
                assetBundleName = bundleName,
                assetNames = names.ToArray(),
            };
        }

        static bool TryParseSubcellId(string subcellId, out int gridX, out int gridY)
        {
            gridX = 0;
            gridY = 0;
            if (string.IsNullOrEmpty(subcellId))
                return false;

            if (subcellId == "tile_coarse")
                return true;

            string[] parts = subcellId.Split('_');
            if (parts.Length != 2)
                return false;

            return int.TryParse(parts[0], out gridX) && int.TryParse(parts[1], out gridY);
        }

        internal static void PrepareHierarchyForPrefabSavePublic(GameObject root) =>
            PrepareHierarchyForPrefabSave(root);

        internal static int PersistEphemeralMeshesPublic(
            GameObject root,
            string prefabAssetPath,
            out int skipped) =>
            PersistEphemeralMeshes(root, prefabAssetPath, out skipped);

        static void PrepareHierarchyForPrefabSave(GameObject root)
        {
            if (root == null)
                return;

            root.hideFlags = HideFlags.None;
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
                transform.gameObject.hideFlags = HideFlags.None;

            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh != null)
                    filter.sharedMesh.hideFlags = HideFlags.None;
            }
        }

        static bool TryLoadPrefabAsset(string prefabAssetPath, out GameObject prefab)
        {
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath);
            if (prefab != null)
                return true;

            string fullPath = ZGConnectPathUtils.AssetPathToFullPath(prefabAssetPath);
            if (!File.Exists(fullPath))
                return false;

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            AssetDatabase.ImportAsset(prefabAssetPath, ImportAssetOptions.ForceUpdate);
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath);
            return prefab != null;
        }

        static int PersistEphemeralMeshes(GameObject root, string prefabAssetPath, out int skipped)
        {
            skipped = 0;
            if (root == null || string.IsNullOrEmpty(prefabAssetPath))
                return 0;

            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath) == null)
            {
                skipped = CountEphemeralMeshFilters(root);
                return 0;
            }

            int embedded = 0;
            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = mf.sharedMesh;
                if (mesh == null)
                    continue;

                if (!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(mesh)))
                    continue;

                Mesh owned = UnityEngine.Object.Instantiate(mesh);
                owned.name = mesh.name;
                owned.hideFlags = HideFlags.None;
                mf.sharedMesh = owned;

                AssetDatabase.AddObjectToAsset(owned, prefabAssetPath);
                EditorUtility.SetDirty(owned);
                embedded++;
            }

            skipped = CountEphemeralMeshFilters(root);
            if (embedded > 0)
                EditorUtility.SetDirty(AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath));

            return embedded;
        }

        static int PersistEphemeralMaterials(GameObject root, string prefabAssetPath, out int skipped)
        {
            skipped = 0;
            if (root == null || string.IsNullOrEmpty(prefabAssetPath))
                return 0;

            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath) == null)
            {
                skipped = CountEphemeralMaterials(root);
                return 0;
            }

            int embedded = 0;
            foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                Material[] materials = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    Material material = materials[i];
                    if (material == null || !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(material)))
                        continue;

                    Material owned = new Material(material)
                    {
                        name = material.name + "_SpatialBake",
                        hideFlags = HideFlags.None
                    };
                    materials[i] = owned;
                    AssetDatabase.AddObjectToAsset(owned, prefabAssetPath);
                    EditorUtility.SetDirty(owned);
                    embedded++;
                    changed = true;
                }

                if (changed)
                    renderer.sharedMaterials = materials;
            }

            skipped = CountEphemeralMaterials(root);
            if (embedded > 0)
                EditorUtility.SetDirty(AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath));

            return embedded;
        }

        static int CountEphemeralMeshFilters(GameObject root)
        {
            int count = 0;
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(mesh)))
                    count++;
            }

            return count;
        }

        static int CountEphemeralMaterials(GameObject root)
        {
            int count = 0;
            foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(material)))
                        count++;
                }
            }

            return count;
        }
    }

    static class SpatialBakeStagingUtility
    {
        public static void CleanStaging()
        {
            // Legacy path (prefabs under StreamingAssets could not be loaded by AssetDatabase).
            DeleteAssetFolderIfExists("Assets/StreamingAssets/ZGConnect/_spatial_bake_staging");
            DeleteAssetFolderIfExists(SpatialStreamingPaths.SpatialStagingAssetRoot);
            ZGConnectPathUtils.EnsureAssetFolder(SpatialStreamingPaths.SpatialStagingAssetRoot);
        }

        public static void DeleteAssetFolderIfExists(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return;

            if (AssetDatabase.IsValidFolder(assetPath))
                AssetDatabase.DeleteAsset(assetPath);
        }
    }
}
