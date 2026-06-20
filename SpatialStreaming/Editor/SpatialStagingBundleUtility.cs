using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using ZGConnect;
using ZGConnect.Editor;
using ZGConnect.SpatialStreaming;

namespace ZGConnect.SpatialStreaming.Editor
{
    public static class SpatialStagingBundleUtility
    {
        public const int DefaultBatchSize = 200;

        static readonly Regex HlodPrefabRegex = new(
            @"^Spatial_hlod(?<factor>\d+)_(?<left>\d+)_(?<bottom>\d+)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static int CountStagedPrefabs(string stagingAssetRoot = null)
        {
            string fullRoot = ZGConnectPathUtils.AssetPathToFullPath(
                stagingAssetRoot ?? SpatialStreamingPaths.SpatialStagingAssetRoot);
            if (!Directory.Exists(fullRoot))
                return 0;

            int prefabs = Directory.GetFiles(fullRoot, "*.prefab", SearchOption.AllDirectories).Length;
            int meshDetails = Directory.GetFiles(fullRoot, "*_Detail.asset", SearchOption.AllDirectories).Length;
            return prefabs + meshDetails;
        }

        public static List<AssetBundleBuild> CollectBundleBuildsFromStaging(string stagingAssetRoot = null)
        {
            stagingAssetRoot ??= SpatialStreamingPaths.SpatialStagingAssetRoot;
            string fullRoot = ZGConnectPathUtils.AssetPathToFullPath(stagingAssetRoot);
            var builds = new List<AssetBundleBuild>();
            if (!Directory.Exists(fullRoot))
                return builds;

            foreach (string tileDir in Directory.GetDirectories(fullRoot))
            {
                string tileId = Path.GetFileName(tileDir);
                if (IsReservedStagingFolder(tileId))
                    continue;

                CollectTileFolderBuilds(stagingAssetRoot, tileId, builds);
            }

            CollectHlodBuilds(stagingAssetRoot, factor: 2, builds);
            CollectHlodBuilds(stagingAssetRoot, factor: 4, builds);
            return builds;
        }

        static bool IsReservedStagingFolder(string folderName) =>
            folderName == "_bundle_output" ||
            folderName == "hlod2" ||
            folderName == "hlod4";

        static void CollectTileFolderBuilds(string stagingAssetRoot, string tileId, List<AssetBundleBuild> builds)
        {
            string tileFolder = $"{stagingAssetRoot}/{tileId}";
            string fullTileFolder = ZGConnectPathUtils.AssetPathToFullPath(tileFolder);
            if (!Directory.Exists(fullTileFolder))
                return;

            string prefix = $"Spatial_{tileId}_";
            foreach (string prefabFull in Directory.GetFiles(fullTileFolder, "Spatial_*.prefab"))
            {
                string fileName = Path.GetFileNameWithoutExtension(prefabFull);
                if (!fileName.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                string suffix = fileName.Substring(prefix.Length);
                if (string.IsNullOrEmpty(suffix))
                    continue;

                string assetPath = $"{tileFolder}/{Path.GetFileName(prefabFull)}";
                if (AssetDatabase.LoadAssetAtPath<GameObject>(assetPath) == null)
                {
                    SpatialBakeVerboseLog.TileWarning(
                        tileId,
                        "staging-prefab-missing",
                        assetPath);
                    continue;
                }

                builds.Add(new AssetBundleBuild
                {
                    assetBundleName = $"{tileId}/{suffix}",
                    assetNames = new[] { assetPath },
                });
            }

            foreach (string detailFull in Directory.GetFiles(fullTileFolder, "Spatial_*_Detail.asset"))
            {
                string fileName = Path.GetFileNameWithoutExtension(detailFull);
                if (!fileName.StartsWith(prefix, StringComparison.Ordinal) ||
                    !fileName.EndsWith("_Detail", StringComparison.Ordinal))
                    continue;

                string suffix = fileName.Substring(prefix.Length);
                suffix = suffix.Substring(0, suffix.Length - "_Detail".Length);
                if (string.IsNullOrEmpty(suffix) ||
                    suffix == "tile_coarse" ||
                    suffix.EndsWith("_proxy", StringComparison.Ordinal))
                    continue;

                string assetPath = $"{tileFolder}/{Path.GetFileName(detailFull)}";
                if (AssetDatabase.LoadAssetAtPath<SpatialMeshDetailAsset>(assetPath) == null)
                {
                    SpatialBakeVerboseLog.TileWarning(
                        tileId,
                        "staging-mesh-detail-missing",
                        assetPath);
                    continue;
                }

                builds.Add(new AssetBundleBuild
                {
                    assetBundleName = $"{tileId}/{suffix}",
                    assetNames = new[] { assetPath },
                });
            }
        }

        static void CollectHlodBuilds(string stagingAssetRoot, int factor, List<AssetBundleBuild> builds)
        {
            string hlodFolder = $"{stagingAssetRoot}/hlod{factor}";
            string fullHlodFolder = ZGConnectPathUtils.AssetPathToFullPath(hlodFolder);
            if (!Directory.Exists(fullHlodFolder))
                return;

            foreach (string prefabFull in Directory.GetFiles(fullHlodFolder, "Spatial_hlod*.prefab"))
            {
                string fileName = Path.GetFileNameWithoutExtension(prefabFull);
                Match match = HlodPrefabRegex.Match(fileName);
                if (!match.Success)
                    continue;

                string left = match.Groups["left"].Value;
                string bottom = match.Groups["bottom"].Value;
                string assetPath = $"{hlodFolder}/{Path.GetFileName(prefabFull)}";
                if (AssetDatabase.LoadAssetAtPath<GameObject>(assetPath) == null)
                {
                    SpatialBakeVerboseLog.Global("staging-hlod-missing", assetPath);
                    continue;
                }

                builds.Add(new AssetBundleBuild
                {
                    assetBundleName = $"hlod{factor}/{left}_{bottom}",
                    assetNames = new[] { assetPath },
                });
            }
        }

        public static bool BuildAndCopyBundlesBatched(
            SpatialBakeProfile profile,
            List<AssetBundleBuild> builds,
            Action<float, string> onProgress,
            out string error,
            int batchSize = DefaultBatchSize)
        {
            error = null;
            if (builds == null || builds.Count == 0)
            {
                error = "No bundles to build.";
                return false;
            }

            if (batchSize < 1)
                batchSize = DefaultBatchSize;

            string stagingOutput = $"{SpatialStreamingPaths.SpatialStagingAssetRoot}/_bundle_output";
            SpatialBakeStagingUtility.DeleteAssetFolderIfExists(stagingOutput);
            ZGConnectPathUtils.EnsureAssetFolder(stagingOutput);

            string spatialOutputRoot = SpatialStreamingPaths.SpatialBundlesRoot;
            Directory.CreateDirectory(spatialOutputRoot);

            string stagingFull = ZGConnectPathUtils.AssetPathToFullPath(stagingOutput);
            BuildAssetBundleOptions options =
                SpatialAssetBundleBuildUtility.GetBuildOptions(profile != null && profile.uncompressedBundles);
            BuildTarget buildTarget = EditorUserBuildSettings.activeBuildTarget;

            int total = builds.Count;
            int copied = 0;
            int batchIndex = 0;

            SpatialBakeVerboseLog.Global(
                "asset-bundles-batched-start",
                $"count={total} batchSize={batchSize} output={spatialOutputRoot}");

            for (int offset = 0; offset < total; offset += batchSize)
            {
                int count = Math.Min(batchSize, total - offset);
                var batch = builds.GetRange(offset, count);
                batchIndex++;

                onProgress?.Invoke(
                    (float)copied / total,
                    $"Building bundle batch {batchIndex} ({copied}/{total})...");

                bool ok = BuildPipeline.BuildAssetBundles(
                    stagingOutput,
                    batch.ToArray(),
                    options,
                    buildTarget);

                if (!ok)
                {
                    error =
                        $"BuildPipeline.BuildAssetBundles failed on batch {batchIndex} " +
                        $"(bundles {offset + 1}-{offset + count} of {total}). " +
                        "Staged prefabs were kept — try 'Build bundles from staging'.";
                    SpatialBakeVerboseLog.Global("asset-bundles-batch-failed", error);
                    return false;
                }

                foreach (AssetBundleBuild build in batch)
                {
                    string src = Path.Combine(stagingFull, build.assetBundleName);
                    if (!File.Exists(src))
                    {
                        error = $"Built bundle missing: {src} (batch {batchIndex}).";
                        SpatialBakeVerboseLog.Global("asset-bundles-missing-file", error);
                        return false;
                    }

                    string dest = Path.Combine(spatialOutputRoot, build.assetBundleName);
                    string destDir = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(destDir))
                        Directory.CreateDirectory(destDir);

                    File.Copy(src, dest, overwrite: true);
                    copied++;

                    TryDeleteFile(src);
                    TryDeleteFile(src + ".manifest");
                }

                TryDeleteFile(Path.Combine(stagingFull, "bundles"));
                TryDeleteFile(Path.Combine(stagingFull, "bundles.manifest"));
            }

            SpatialBakeVerboseLog.Global(
                "asset-bundles-batched-done",
                $"copied={copied} output={spatialOutputRoot}");
            onProgress?.Invoke(1f, $"Copied {copied} bundle(s).");
            return true;
        }

        public static SpatialDatasetManifest RebuildManifestFromStaging(
            SpatialBakeProfile profile,
            string stagingAssetRoot = null)
        {
            stagingAssetRoot ??= SpatialStreamingPaths.SpatialStagingAssetRoot;
            string fullRoot = ZGConnectPathUtils.AssetPathToFullPath(stagingAssetRoot);

            if (!SpatialParentManifestReader.TryLoad(
                    SpatialStreamingPaths.ParentManifestPath(),
                    out int tileSizeMeters,
                    out _,
                    out List<SpatialParentTileInfo> parentTiles,
                    out string parentError))
            {
                throw new InvalidOperationException(parentError);
            }

            var parentById = new Dictionary<string, SpatialParentTileInfo>(StringComparer.Ordinal);
            foreach (SpatialParentTileInfo tile in parentTiles)
            {
                if (tile != null && !string.IsNullOrEmpty(tile.TileId))
                    parentById[tile.TileId] = tile;
            }

            var manifest = new SpatialDatasetManifest
            {
                Version = "1",
                SourceManifest = SpatialStreamingPaths.DefaultParentManifestRelativePath,
                BakeFingerprint = profile != null ? profile.ComputeFingerprintString() : "staging-recovery",
                TileSizeMeters = tileSizeMeters,
                SubcellSizeMeters = profile?.subcellSizeMeters ?? 250,
            };

            if (!Directory.Exists(fullRoot))
                return manifest;

            foreach (string tileDir in Directory.GetDirectories(fullRoot))
            {
                string tileId = Path.GetFileName(tileDir);
                if (IsReservedStagingFolder(tileId))
                    continue;

                if (!parentById.TryGetValue(tileId, out SpatialParentTileInfo parentTile))
                {
                    SpatialBakeVerboseLog.TileWarning(tileId, "manifest-skip", "tile not in parent manifest");
                    continue;
                }

                var tileEntry = new SpatialTileManifestEntry
                {
                    TileId = tileId,
                    UnityPosition = new[]
                    {
                        parentTile.UnityPosition.x,
                        parentTile.UnityPosition.y,
                        parentTile.UnityPosition.z,
                    },
                };

                string tileFolder = $"{stagingAssetRoot}/{tileId}";
                string prefix = $"Spatial_{tileId}_";
                foreach (string prefabFull in Directory.GetFiles(
                             ZGConnectPathUtils.AssetPathToFullPath(tileFolder),
                             "Spatial_*.prefab"))
                {
                    string fileName = Path.GetFileNameWithoutExtension(prefabFull);
                    if (!fileName.StartsWith(prefix, StringComparison.Ordinal))
                        continue;

                    string suffix = fileName.Substring(prefix.Length);
                    if (suffix == "tile_coarse")
                    {
                        tileEntry.CoarseBundleRel =
                            SpatialStreamingPaths.GetTileCoarseBundleRelativePath(tileId);
                        continue;
                    }

                    if (suffix == "tile_proxy")
                    {
                        tileEntry.ProxyBundleRel =
                            SpatialStreamingPaths.GetTileProxyBundleRelativePath(tileId);
                        continue;
                    }

                    if (suffix.EndsWith("_proxy", StringComparison.Ordinal))
                    {
                        string subcellId = suffix.Substring(0, suffix.Length - "_proxy".Length);
                        if (!TryParseSubcellId(subcellId, out int gx, out int gy))
                            continue;

                        SpatialSubcellManifestEntry subcell = FindOrAddSubcell(tileEntry, subcellId, gx, gy);
                        subcell.ProxyBundleRel =
                            SpatialStreamingPaths.GetSubcellProxyBundleRelativePath(tileId, subcellId);
                        continue;
                    }

                    if (!TryParseSubcellId(suffix, out int gridX, out int gridY))
                        continue;

                    tileEntry.Subcells.Add(new SpatialSubcellManifestEntry
                    {
                        SubcellId = suffix,
                        GridX = gridX,
                        GridY = gridY,
                        BundleRel = SpatialStreamingPaths.GetSubcellBundleRelativePath(tileId, suffix),
                        BuildingCount = 0,
                    });
                }

                foreach (string detailFull in Directory.GetFiles(
                             ZGConnectPathUtils.AssetPathToFullPath(tileFolder),
                             "Spatial_*_Detail.asset"))
                {
                    string fileName = Path.GetFileNameWithoutExtension(detailFull);
                    if (!fileName.StartsWith(prefix, StringComparison.Ordinal) ||
                        !fileName.EndsWith("_Detail", StringComparison.Ordinal))
                        continue;

                    string suffix = fileName.Substring(prefix.Length);
                    suffix = suffix.Substring(0, suffix.Length - "_Detail".Length);
                    if (!TryParseSubcellId(suffix, out int gridX, out int gridY))
                        continue;

                    SpatialSubcellManifestEntry subcell = FindOrAddSubcell(tileEntry, suffix, gridX, gridY);
                    subcell.BundleRel = SpatialStreamingPaths.GetSubcellBundleRelativePath(tileId, suffix);
                    subcell.LoadKind = SpatialSubcellManifestEntry.MeshDetailLoadKind;

                    string assetPath = $"{tileFolder}/{Path.GetFileName(detailFull)}";
                    SpatialMeshDetailAsset detailAsset =
                        AssetDatabase.LoadAssetAtPath<SpatialMeshDetailAsset>(assetPath);
                    subcell.BuildingCount = detailAsset != null && detailAsset.Renderers != null
                        ? detailAsset.Renderers.Length
                        : 0;
                }

                if (tileEntry.UsesSubcells || !string.IsNullOrEmpty(tileEntry.CoarseBundleRel))
                    manifest.Tiles.Add(tileEntry);
            }

            RebuildSupertileManifestEntries(manifest, stagingAssetRoot, parentById, tileSizeMeters);
            return manifest;
        }

        static SpatialSubcellManifestEntry FindOrAddSubcell(
            SpatialTileManifestEntry tileEntry,
            string subcellId,
            int gridX,
            int gridY)
        {
            foreach (SpatialSubcellManifestEntry existing in tileEntry.Subcells)
            {
                if (existing != null && existing.SubcellId == subcellId)
                    return existing;
            }

            var created = new SpatialSubcellManifestEntry
            {
                SubcellId = subcellId,
                GridX = gridX,
                GridY = gridY,
                BundleRel = SpatialStreamingPaths.GetSubcellBundleRelativePath(tileEntry.TileId, subcellId),
                BuildingCount = 0,
            };
            tileEntry.Subcells.Add(created);
            return created;
        }

        static void RebuildSupertileManifestEntries(
            SpatialDatasetManifest manifest,
            string stagingAssetRoot,
            Dictionary<string, SpatialParentTileInfo> parentById,
            int tileSizeMeters)
        {
            manifest.Supertiles ??= new List<SpatialSupertileManifestEntry>();

            foreach (int factor in new[] { 2, 4 })
            {
                string hlodFolder = $"{stagingAssetRoot}/hlod{factor}";
                string fullHlodFolder = ZGConnectPathUtils.AssetPathToFullPath(hlodFolder);
                if (!Directory.Exists(fullHlodFolder))
                    continue;

                int blockSizeMeters = tileSizeMeters * factor;
                foreach (string prefabFull in Directory.GetFiles(fullHlodFolder, "Spatial_hlod*.prefab"))
                {
                    string fileName = Path.GetFileNameWithoutExtension(prefabFull);
                    Match match = HlodPrefabRegex.Match(fileName);
                    if (!match.Success)
                        continue;

                    int left = int.Parse(match.Groups["left"].Value);
                    int bottom = int.Parse(match.Groups["bottom"].Value);
                    string supertileId = SpatialStreamingPaths.GetSupertileId(factor, left, bottom);
                    var childTileIds = new List<string>();
                    var origin = new Vector3(float.MaxValue, 0f, float.MaxValue);

                    foreach (SpatialTileManifestEntry tile in manifest.Tiles)
                    {
                        if (tile == null ||
                            !SpatialTileIdUtility.TryParse(tile.TileId, out int tileLeft, out int tileBottom))
                        {
                            continue;
                        }

                        if (tileLeft < left || tileLeft >= left + blockSizeMeters ||
                            tileBottom < bottom || tileBottom >= bottom + blockSizeMeters)
                        {
                            continue;
                        }

                        childTileIds.Add(tile.TileId);
                        Vector3 pos = tile.GetUnityPosition();
                        origin.x = Mathf.Min(origin.x, pos.x);
                        origin.z = Mathf.Min(origin.z, pos.z);
                    }

                    if (childTileIds.Count == 0 && parentById.Count > 0)
                    {
                        foreach (KeyValuePair<string, SpatialParentTileInfo> kvp in parentById)
                        {
                            if (!SpatialTileIdUtility.TryParse(kvp.Key, out int tileLeft, out int tileBottom))
                                continue;

                            if (tileLeft < left || tileLeft >= left + blockSizeMeters ||
                                tileBottom < bottom || tileBottom >= bottom + blockSizeMeters)
                            {
                                continue;
                            }

                            childTileIds.Add(kvp.Key);
                            origin.x = Mathf.Min(origin.x, kvp.Value.UnityPosition.x);
                            origin.z = Mathf.Min(origin.z, kvp.Value.UnityPosition.z);
                        }
                    }

                    if (childTileIds.Count == 0)
                        origin = Vector3.zero;

                    manifest.Supertiles.Add(new SpatialSupertileManifestEntry
                    {
                        SupertileId = supertileId,
                        Factor = factor,
                        Left = left,
                        Bottom = bottom,
                        UnityPosition = new[] { origin.x, origin.y, origin.z },
                        BundleRel = SpatialStreamingPaths.GetSupertileBundleRelativePath(factor, left, bottom),
                        ChildTileIds = childTileIds,
                    });
                }
            }
        }

        static bool TryParseSubcellId(string subcellId, out int gridX, out int gridY)
        {
            gridX = 0;
            gridY = 0;
            if (string.IsNullOrEmpty(subcellId))
                return false;

            string[] parts = subcellId.Split('_');
            if (parts.Length != 2)
                return false;

            return int.TryParse(parts[0], out gridX) && int.TryParse(parts[1], out gridY);
        }

        static void TryDeleteFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;

            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                SpatialBakeVerboseLog.Global("staging-cleanup-warn", $"{path}: {ex.Message}");
            }
        }
    }
}
