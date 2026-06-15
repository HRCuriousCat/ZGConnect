using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using ZGConnect;
using ZGConnect.Editor;
using ZGConnect.RealtimeStreaming;

namespace ZGConnect.RealtimeStreaming.Editor
{
    public class RealtimePackOptions
    {
        public string DatasetRoot;
        public string OutputRoot;
        public DatasetFolderScanner.HeightmapSource Heightmap;
        public List<DatasetFolderScanner.BasemapSource> Basemaps = new();
        public string VegetationMasksFolder;
        public HashSet<string> SelectedTileIds = new();
        public StreamingRegionBoundsEpsg RegionBounds;
        public bool PackTerrain = true;
        public bool PackTerrainBundles = true;
        public bool FlipHeightmapVertically = true;
        public bool PackOrtho;
        public bool PackTiled;
        public bool PackFacadeBuildings;
        public bool PackOrthoRoofBuildings;
        public bool PackVegetation;
        public bool PackBuildingLod = true;
        public bool PackBuildingBundles = true;
        public bool BakeVegetationBundles = true;
        public RealtimeBuildingColliderMode PackColliderMode = RealtimeBuildingColliderMode.ConvexMesh;
        public bool PackUncompressedAssetBundles;
        public bool PackCombineBuildingMeshes = true;
        public bool PackCombineMeshesPerMaterial = true;
        public string PackOrthoBasemapId = "ortho";
        public BuildingSurfaceSettings BuildingSurfaceSettings;
        public Material RoofOrthophotoMaterialTemplate;
        public VegetationRuleSet VegetationRuleSet;
        public VegetationPrototype[] VegetationPrototypes;
        public bool SkipExisting = true;
        public bool PackCopyBuildingMetadataBinary = true;
        public int HeightmapResolution;
        public int OrthoMaxResolution = 2048;
        public bool OrthoCrunchCompression = true;
        public int OrthoCrunchQuality = 75;
        public int Hlod2HeightRes = 513;
        public int Hlod4HeightRes = 257;
        public int Hlod2OrthoRes = 1024;
        public int Hlod4OrthoRes = 512;
        public bool UsedDualBuildingLod;

        internal HashSet<string> FreshlyPackedTileIds;
        internal HashSet<string> RebuiltSupertileIds;
    }

    public static class RealtimeDatasetPackager
    {
        const float PhaseTilesEnd = 0.55f;
        const float PhaseHlodEnd = 0.72f;
        const float PhaseTerrainBundlesEnd = 0.82f;
        const float PhaseBuildingBundlesEnd = 0.92f;
        const float PhaseVegetationBundlesEnd = 0.96f;

        public static IEnumerator PackCoroutine(
            RealtimePackOptions options,
            Action<RealtimePackProgress> onProgress = null,
            Action<StreamingDatasetManifest> onComplete = null)
        {
            if (options.Heightmap?.Metadata?.Tiles == null)
                throw new InvalidOperationException("No heightmap metadata.");

            if (options.PackTerrainBundles &&
                options.Basemaps.Exists(b => b.Type == BasemapType.Ortho))
            {
                options.PackOrtho = true;
            }

            string outputAssetRoot = RuntimeStreamingPaths.PackedDatasetAssetRoot;
            ZGConnectPathUtils.EnsureAssetFolder(outputAssetRoot);
            RealtimePackStagingUtility.CleanupAll();
            string outputRoot = ZGConnectPathUtils.AssetPathToFullPath(outputAssetRoot);

            var packedTiles = new List<StreamingTileEntry>();
            var orthoRelByTile = new Dictionary<string, Dictionary<string, string>>();
            int tileSize = options.Heightmap.Metadata.Settings.TileSizeMeters;
            int total = options.SelectedTileIds.Count;
            int done = 0;

            StreamingDatasetManifest existingManifest =
                RealtimePackReuseUtility.TryLoadExistingManifest(outputRoot);
            Dictionary<string, StreamingTileEntry> existingTilesById =
                RealtimePackReuseUtility.IndexTiles(existingManifest);
            Dictionary<string, StreamingSupertileEntry> existingSupertilesById =
                RealtimePackReuseUtility.IndexSupertiles(existingManifest);
            var freshlyPackedTileIds = new HashSet<string>();
            var rebuiltSupertileIds = new HashSet<string>();
            options.FreshlyPackedTileIds = freshlyPackedTileIds;
            options.RebuiltSupertileIds = rebuiltSupertileIds;

            if (existingManifest?.GetLodStorageMode() == BuildingLodStorageMode.DualFile)
                options.UsedDualBuildingLod = true;

            Report(onProgress, RealtimePackProgress.Create(
                RealtimePackStage.Tiles, 0f, 0f, 0, total, $"{total} tile(s)"));
            yield return null;

            foreach (HeightmapTileJson tile in options.Heightmap.Metadata.Tiles)
            {
                string tileId = $"{tile.Left}_{tile.Bottom}";
                if (!options.SelectedTileIds.Contains(tileId))
                    continue;

                float tileStageProgress = (float)(done + 1) / Mathf.Max(total, 1);
                Report(onProgress, RealtimePackProgress.Create(
                    RealtimePackStage.Tiles,
                    Mathf.Lerp(0f, PhaseTilesEnd, tileStageProgress),
                    tileStageProgress,
                    done + 1,
                    total,
                    tileId));
                yield return null;
                done++;

                if (options.SkipExisting &&
                    existingTilesById.TryGetValue(tileId, out StreamingTileEntry existingTile) &&
                    RealtimePackReuseUtility.CanReuseLeafTile(existingTile, options, outputRoot))
                {
                    packedTiles.Add(existingTile);
                    if (existingTile.OrthoPaths != null && existingTile.OrthoPaths.Count > 0)
                        orthoRelByTile[tileId] = existingTile.OrthoPaths;
                    continue;
                }

                freshlyPackedTileIds.Add(tileId);

                var entry = new StreamingTileEntry
                {
                    TileId = tileId,
                    Left = tile.Left,
                    Bottom = tile.Bottom,
                    Right = tile.Right,
                    Top = tile.Top,
                    HeightmapRes = options.HeightmapResolution > 0
                        ? options.HeightmapResolution
                        : tile.HeightmapResolution,
                    InvalidRatio = tile.InvalidRatio,
                    UnityPosition = new[] { tile.UnityPosition.X, tile.UnityPosition.Y, tile.UnityPosition.Z },
                    TerrainSize = new[] { tile.TerrainSize.X, tile.TerrainSize.Y, tile.TerrainSize.Z },
                };

                if (options.PackTerrain)
                {
                    string dstRel = $"heightmap/tiles_1x1/{tileId}.raw";
                    string dst = Path.Combine(outputRoot, dstRel.Replace('/', Path.DirectorySeparatorChar));
                    EnsureOutputDirectory(outputAssetRoot, dstRel);
                    File.Copy(Path.Combine(options.Heightmap.FolderPath, tile.RawFile), dst, true);
                    entry.HeightmapPath = dstRel;
                }

                if (options.PackOrtho || options.PackTiled)
                {
                    entry.OrthoPaths = new Dictionary<string, string>();
                    entry.TiledSplatPaths = new Dictionary<string, List<string>>();
                    orthoRelByTile[tileId] = entry.OrthoPaths;

                    foreach (DatasetFolderScanner.BasemapSource bm in options.Basemaps)
                    {
                        string basemapId = ZGConnectPathUtils.DeriveBasemapId(bm.FolderPath);
                        if (bm.Type == BasemapType.Ortho && options.PackOrtho)
                        {
                            var lookup = ZGConnectPathUtils.BuildOrthoLookup(bm.Metadata.Tiles);
                            if (lookup.TryGetValue(tileId, out OrthoTileJson ortho))
                            {
                                string ext = bm.Metadata.Settings.FileExtension ?? ".png";
                                if (!ext.StartsWith("."))
                                    ext = "." + ext;
                                string dstRel = $"basemaps/{basemapId}/ortho_1x1/{tileId}{ext}";
                                string dst = Path.Combine(outputRoot, dstRel.Replace('/', Path.DirectorySeparatorChar));
                                EnsureOutputDirectory(outputAssetRoot, dstRel);
                                File.Copy(Path.Combine(bm.FolderPath, ortho.TextureFile), dst, true);
                                entry.OrthoPaths[basemapId] = dstRel;
                            }
                        }
                        else if (bm.Type == BasemapType.Tiled && options.PackTiled)
                        {
                            var lookup = ZGConnectPathUtils.BuildTiledLookup(bm.TiledMetadata.Tiles);
                            if (lookup.TryGetValue(tileId, out SplatmapTileJson splat))
                            {
                                EnsureOutputDirectory(outputAssetRoot, $"basemaps/{basemapId}/tiled");
                                EnsureOutputDirectory(outputAssetRoot, $"basemaps/{basemapId}");
                                File.Copy(
                                    Path.Combine(bm.FolderPath, "metadata.json"),
                                    Path.Combine(outputRoot, $"basemaps/{basemapId}/metadata.json"), true);

                                var rels = new List<string>();
                                foreach (string splatFile in splat.Splatmaps)
                                {
                                    string dstRel = $"basemaps/{basemapId}/tiled/{tileId}_{Path.GetFileName(splatFile)}";
                                    string dst = Path.Combine(outputRoot, dstRel.Replace('/', Path.DirectorySeparatorChar));
                                    File.Copy(Path.Combine(bm.FolderPath, splatFile), dst, true);
                                    rels.Add(dstRel);
                                }
                                entry.TiledSplatPaths[basemapId] = rels;
                            }
                        }
                    }
                }

                if (options.PackFacadeBuildings)
                    entry.HasFacadeBuildings = TryPackBuildings(options, tileId, "building_meshes", options.PackBuildingLod);
                if (options.PackOrthoRoofBuildings)
                    entry.HasOrthoRoofBuildings = TryPackBuildings(options, tileId, "building_meshes_ortho", options.PackBuildingLod);

                if (options.PackVegetation && !string.IsNullOrEmpty(options.VegetationMasksFolder))
                {
                    string src = Path.Combine(options.VegetationMasksFolder, $"{tileId}_vegetation.png");
                    if (File.Exists(src))
                    {
                        string dstRel = $"vegetation_masks/{tileId}_vegetation.png";
                        string dst = Path.Combine(outputRoot, dstRel.Replace('/', Path.DirectorySeparatorChar));
                        EnsureOutputDirectory(outputAssetRoot, dstRel);
                        File.Copy(src, dst, true);
                        entry.HasVegetationMask = true;
                    }
                }

                packedTiles.Add(entry);
            }

            RealtimePackReuseUtility.AppendPreservedTiles(
                packedTiles,
                orthoRelByTile,
                existingTilesById,
                options,
                outputRoot,
                options.SelectedTileIds);

            RealtimePackReuseUtility.PruneDatasetToRegionBounds(
                packedTiles,
                null,
                options.RegionBounds,
                tileSize,
                options.SkipExisting);

            if (total > 0)
            {
                Report(onProgress, RealtimePackProgress.Create(
                    RealtimePackStage.Tiles, PhaseTilesEnd, 1f, total, total));
                yield return null;
            }

            var packedTilesById = new Dictionary<string, StreamingTileEntry>();
            foreach (StreamingTileEntry packed in packedTiles)
                packedTilesById[packed.TileId] = packed;

            var supertiles = new List<StreamingSupertileEntry>();
            var orthoPathsByBasemap = BuildOrthoPathsByBasemap(orthoRelByTile);
            if (options.PackTerrain || (options.PackOrtho && orthoPathsByBasemap.Count > 0))
            {
                Report(onProgress, RealtimePackProgress.Create(
                    RealtimePackStage.Hlod, PhaseTilesEnd, 0f, detail: "Merging supertiles"));
                yield return null;

                IEnumerator hlodRoutine = HlodMergeUtility.GenerateSupertilesCoroutine(
                    outputRoot,
                    packedTiles,
                    tileSize,
                    options.HeightmapResolution > 0 ? options.HeightmapResolution : 1025,
                    orthoPathsByBasemap,
                    new[] { options.OrthoMaxResolution, options.Hlod2OrthoRes, options.Hlod4OrthoRes },
                    new[] { 1025, options.Hlod2HeightRes, options.Hlod4HeightRes },
                    supertiles,
                    mergeHeightmaps: options.PackTerrain,
                    packOptions: options,
                    existingSupertilesById: existingSupertilesById,
                    freshlyPackedTileIds: freshlyPackedTileIds,
                    rebuiltSupertileIds: rebuiltSupertileIds,
                    onProgress: (factor, current, total, supertileId) =>
                    {
                        float span = PhaseHlodEnd - PhaseTilesEnd;
                        float factorStart = factor == 2 ? 0f : 0.5f;
                        float stageT = total > 0 ? (factorStart + 0.5f * (current / (float)total)) : factorStart;
                        Report(onProgress, RealtimePackProgress.Create(
                            RealtimePackStage.Hlod,
                            PhaseTilesEnd + span * stageT,
                            stageT,
                            current,
                            total,
                            supertileId));
                    });
                while (hlodRoutine.MoveNext())
                    yield return hlodRoutine.Current;

                Report(onProgress, RealtimePackProgress.Create(
                    RealtimePackStage.Hlod, PhaseHlodEnd, 1f, detail: $"{supertiles.Count} supertile(s)"));
                yield return null;
            }

            var packedTileIds = new HashSet<string>(packedTilesById.Keys);
            RealtimePackReuseUtility.AppendPreservedSupertiles(
                supertiles,
                existingSupertilesById,
                packedTileIds,
                rebuiltSupertileIds,
                options,
                outputRoot,
                packedTilesById);

            RealtimePackReuseUtility.PruneDatasetToRegionBounds(
                packedTiles,
                supertiles,
                options.RegionBounds,
                tileSize,
                options.SkipExisting);

            RealtimePackReuseUtility.PruneOrphanedBuildingMeshes(outputRoot, packedTiles);

            if (options.PackTerrain && options.PackTerrainBundles)
            {
                IEnumerator bundleRoutine = RealtimeTerrainBundleBuilder.BuildCoroutine(
                    options, packedTiles, supertiles, PhaseHlodEnd, PhaseTerrainBundlesEnd, onProgress);
                while (bundleRoutine.MoveNext())
                    yield return bundleRoutine.Current;
            }

            BuildingLodStorageMode lodMode = options.UsedDualBuildingLod ||
                                             existingManifest?.GetLodStorageMode() == BuildingLodStorageMode.DualFile
                ? BuildingLodStorageMode.DualFile
                : BuildingLodStorageMode.Hierarchy;

            StreamingPackBakeManifest packBake = RealtimePackBakeFingerprint.CreateManifest(options);

            if (options.PackBuildingBundles)
            {
                RealtimePackReuseUtility.SyncBuildingFlagsFromDisk(packedTiles, outputRoot);
                EnsureFreshBuildingBakeForMissingBundles(
                    options, packedTiles, outputRoot, freshlyPackedTileIds);

                IEnumerator buildingBundleRoutine = RealtimeBuildingBundleBuilder.BuildCoroutine(
                    options,
                    packedTiles,
                    lodMode,
                    packBake.Fingerprint,
                    PhaseTerrainBundlesEnd,
                    PhaseBuildingBundlesEnd,
                    onProgress);
                while (buildingBundleRoutine.MoveNext())
                    yield return buildingBundleRoutine.Current;
            }

            if (options.BakeVegetationBundles && options.PackVegetation)
            {
                IEnumerator vegetationBundleRoutine = RealtimeVegetationBundleBuilder.BuildCoroutine(
                    options,
                    packedTiles,
                    PhaseBuildingBundlesEnd,
                    PhaseVegetationBundlesEnd,
                    onProgress);
                while (vegetationBundleRoutine.MoveNext())
                    yield return vegetationBundleRoutine.Current;
            }

            var manifest = new StreamingDatasetManifest
            {
                Version = ComputeManifestVersion(options),
                UnityOrigin = new[]
                {
                    options.Heightmap.Metadata.Settings.UnityOriginX,
                    options.Heightmap.Metadata.Settings.UnityOriginY,
                },
                MinHeight = options.Heightmap.Metadata.Settings.MinHeight,
                MaxHeight = options.Heightmap.Metadata.Settings.MaxHeight,
                TileSizeMeters = tileSize,
                Tiles = packedTiles,
                Supertiles = supertiles,
                BuildingLodStorage = lodMode == BuildingLodStorageMode.DualFile ? "dual" : "hierarchy",
                PackBake = packBake,
            };

            var basemapIds = new HashSet<string>();
            foreach (DatasetFolderScanner.BasemapSource bm in options.Basemaps)
            {
                string id = ZGConnectPathUtils.DeriveBasemapId(bm.FolderPath);
                if (!basemapIds.Add(id))
                    continue;

                manifest.AvailableBasemaps.Add(new StreamingBasemapEntry
                {
                    Id = id,
                    Type = bm.Type.ToString(),
                    DisplayName = bm.DisplayName,
                });
            }

            if (options.SkipExisting && existingManifest?.AvailableBasemaps != null)
            {
                foreach (StreamingBasemapEntry existingBm in existingManifest.AvailableBasemaps)
                {
                    if (existingBm == null || string.IsNullOrEmpty(existingBm.Id) || !basemapIds.Add(existingBm.Id))
                        continue;

                    manifest.AvailableBasemaps.Add(existingBm);
                }
            }

            Report(onProgress, RealtimePackProgress.Create(
                RealtimePackStage.Manifest, PhaseVegetationBundlesEnd, 0.5f, detail: "Writing manifest.json"));
            yield return null;

            string manifestPath = Path.Combine(outputRoot, "manifest.json");
            File.WriteAllText(manifestPath, JsonConvert.SerializeObject(manifest, Formatting.Indented));
            AssetDatabase.Refresh();

            RealtimePackStagingUtility.CleanupAll();

            onComplete?.Invoke(manifest);
            Report(onProgress, RealtimePackProgress.Create(
                RealtimePackStage.Manifest, 1f, 1f, detail: "Packed to StreamingAssets/ZGConnect/"));
            yield return null;
        }

        static string ComputeManifestVersion(RealtimePackOptions options)
        {
            if (options.PackBuildingBundles || options.BakeVegetationBundles)
                return "5";
            if (options.PackTerrain && options.PackTerrainBundles)
                return "4";
            return "2";
        }

        static void Report(Action<RealtimePackProgress> onProgress, RealtimePackProgress progress) =>
            onProgress?.Invoke(progress);

        static void EnsureFreshBuildingBakeForMissingBundles(
            RealtimePackOptions options,
            List<StreamingTileEntry> packedTiles,
            string outputRoot,
            HashSet<string> freshlyPackedTileIds)
        {
            if (!options.PackBuildingBundles || packedTiles == null || freshlyPackedTileIds == null)
                return;

            List<RealtimeBuildingBundleBuilder.BuildingBakeTarget> targets =
                RealtimeBuildingBundleBuilder.CollectBakeTargets(options, packedTiles, outputRoot);
            foreach (RealtimeBuildingBundleBuilder.BuildingBakeTarget target in targets)
            {
                if (RealtimePackReuseUtility.HasBuildingBundle(
                        target.Tile, target.StyleKey, outputRoot))
                    continue;

                freshlyPackedTileIds.Add(target.Tile.TileId);
            }
        }

        static void EnsureOutputDirectory(string outputAssetRoot, string relativePathOrFolder)
        {
            string normalized = relativePathOrFolder.Replace('\\', '/').TrimEnd('/');
            string relativeDir = Path.HasExtension(normalized)
                ? Path.GetDirectoryName(normalized)?.Replace('\\', '/')
                : normalized;

            if (string.IsNullOrEmpty(relativeDir))
                return;

            ZGConnectPathUtils.EnsureAssetFolder($"{outputAssetRoot}/{relativeDir}");
        }

        /// <summary>basemapId → (tileId → packed relative path)</summary>
        static Dictionary<string, Dictionary<string, string>> BuildOrthoPathsByBasemap(
            Dictionary<string, Dictionary<string, string>> orthoRelByTile)
        {
            var byBasemap = new Dictionary<string, Dictionary<string, string>>();
            foreach (KeyValuePair<string, Dictionary<string, string>> tileKvp in orthoRelByTile)
            {
                if (tileKvp.Value == null)
                    continue;

                foreach (KeyValuePair<string, string> bmKvp in tileKvp.Value)
                {
                    if (!byBasemap.TryGetValue(bmKvp.Key, out Dictionary<string, string> byTile))
                    {
                        byTile = new Dictionary<string, string>();
                        byBasemap[bmKvp.Key] = byTile;
                    }

                    byTile[tileKvp.Key] = bmKvp.Value;
                }
            }

            return byBasemap;
        }

        static bool TryPackBuildings(RealtimePackOptions options, string tileId, string folder, bool withLod)
        {
            return TryPackBuildingsInternal(options, tileId, folder, withLod);
        }

        static bool TryPackBuildingsInternal(RealtimePackOptions options, string tileId, string folder, bool withLod)
        {
            string srcFolder = Path.Combine(options.DatasetRoot, folder);
            string srcGlb = Path.Combine(srcFolder, $"buildings_{tileId}.glb");
            if (!File.Exists(srcGlb))
                return false;

            string outputAssetRoot = RuntimeStreamingPaths.PackedDatasetAssetRoot;
            EnsureOutputDirectory(outputAssetRoot, folder);
            string dstDir = ZGConnectPathUtils.AssetPathToFullPath($"{outputAssetRoot}/{folder}");
            string dstGlb = Path.Combine(dstDir, $"buildings_{tileId}.glb");

            bool dual = false;
            if (withLod)
            {
                if (!BuildingLodPackUtility.TryPackTileWithLodHierarchy(srcGlb, dstGlb, tileId, out dual))
                    File.Copy(srcGlb, dstGlb, true);
            }
            else
            {
                File.Copy(srcGlb, dstGlb, true);
            }

            if (!BuildingLodPackUtility.OutputGlbLooksValid(dstGlb))
            {
                Debug.LogWarning(
                    $"[ZGConnect.Realtime] Building pack produced no output for tile '{tileId}' ({folder}). " +
                    "Copying source GLB as fallback.");
                File.Copy(srcGlb, dstGlb, true);
            }

            if (!BuildingLodPackUtility.OutputGlbLooksValid(dstGlb))
            {
                Debug.LogError(
                    $"[ZGConnect.Realtime] Building pack failed for tile '{tileId}' ({folder}).");
                return false;
            }

            if (dual)
                options.UsedDualBuildingLod = true;

            string srcJson = Path.Combine(srcFolder, $"buildings_{tileId}.json");
            string dstJson = Path.Combine(dstDir, $"buildings_{tileId}.json");
            if (File.Exists(srcJson))
                File.Copy(srcJson, dstJson, true);

            if (options.PackCopyBuildingMetadataBinary)
                PackBuildingMetadataBinary(options, outputAssetRoot, folder, tileId, srcJson, dstJson);

            return true;
        }

        static void PackBuildingMetadataBinary(
            RealtimePackOptions options,
            string outputAssetRoot,
            string jsonFolder,
            string tileId,
            string srcJsonPath,
            string dstJsonPath)
        {
            string binFolder = BuildingMetadataPathUtility.GetBinaryFolderForJsonFolder(jsonFolder);
            string fileName = BuildingMetadataPathUtility.GetBinaryFileName(tileId);
            string srcBytes = Path.Combine(options.DatasetRoot, binFolder, fileName);
            EnsureOutputDirectory(outputAssetRoot, binFolder);
            string dstBinDir = ZGConnectPathUtils.AssetPathToFullPath($"{outputAssetRoot}/{binFolder}");
            string dstBytes = Path.Combine(dstBinDir, fileName);

            if (File.Exists(srcBytes))
            {
                File.Copy(srcBytes, dstBytes, true);
                return;
            }

            string jsonForConvert = File.Exists(srcJsonPath)
                ? srcJsonPath
                : File.Exists(dstJsonPath) ? dstJsonPath : null;

            if (string.IsNullOrEmpty(jsonForConvert))
                return;

            if (options.SkipExisting &&
                File.Exists(dstBytes) &&
                File.GetLastWriteTimeUtc(dstBytes) >= File.GetLastWriteTimeUtc(jsonForConvert))
            {
                return;
            }

            if (BuildingMetadataBinaryWriter.TryConvertJsonFileToBytes(jsonForConvert, dstBytes, out string error))
                return;

            Debug.LogWarning(
                $"[ZGConnect.Realtime] Could not pack metadata binary for tile '{tileId}' ({jsonFolder}): {error}");
        }
    }
}
