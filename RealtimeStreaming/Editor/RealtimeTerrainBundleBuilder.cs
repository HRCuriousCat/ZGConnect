using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using ZGConnect;
using ZGConnect.Editor;
using ZGConnect.RealtimeStreaming;

namespace ZGConnect.RealtimeStreaming.Editor
{
    public static class RealtimeTerrainBundleBuilder
    {
        const string IncrementalBundleSubfolder = "_incremental_build";

        public static IEnumerator BuildCoroutine(
            RealtimePackOptions options,
            List<StreamingTileEntry> tiles,
            List<StreamingSupertileEntry> supertiles,
            float progressStart,
            float progressEnd,
            Action<RealtimePackProgress> onProgress = null)
        {
            if (tiles == null || tiles.Count == 0)
                yield break;

            List<string> orthoBasemapIds = GetOrthoBasemapIds(options);
            if (orthoBasemapIds.Count == 0)
            {
                Debug.LogWarning(
                    "[ZGConnect.Realtime] No ortho basemaps selected â€” skipping terrain bundle build.");
                yield break;
            }

            string stagingRoot = RealtimePackStagingUtility.TerrainRoot;
            string bundleAssetOutput = $"{RuntimeStreamingPaths.PackedDatasetAssetRoot}/bundles";

            RealtimePackStagingUtility.DeleteAssetFolderIfExists(stagingRoot);
            ZGConnectPathUtils.EnsureAssetFolder(stagingRoot);

            var builds = new List<AssetBundleBuild>();
            int leafCount = tiles.Count(t => options.SelectedTileIds.Contains(t.TileId));
            int supertileCount = supertiles?.Count ?? 0;
            int total = (leafCount + supertileCount) * orthoBasemapIds.Count;
            int done = 0;
            float stagingEnd = progressStart + (progressEnd - progressStart) * 0.85f;

            Report(onProgress, RealtimePackProgress.Create(
                RealtimePackStage.TerrainBundle, progressStart, 0f, 0, total));
            yield return null;

            foreach (StreamingTileEntry tile in tiles)
            {
                if (!options.SelectedTileIds.Contains(tile.TileId))
                    continue;

                tile.TerrainBundlePaths ??= new Dictionary<string, string>();

                foreach (string basemapId in orthoBasemapIds)
                {
                    float stageProgress = (float)(done + 1) / Mathf.Max(total, 1);
                    Report(onProgress, RealtimePackProgress.Create(
                        RealtimePackStage.TerrainBundle,
                        Mathf.Lerp(progressStart, stagingEnd, stageProgress),
                        stageProgress,
                        done + 1,
                        total,
                        $"{tile.TileId}/{basemapId}"));

                    if (ShouldSkipBundleBuild(
                            options, tile.TileId, basemapId, tile.TerrainBundlePaths, isSupertile: false))
                    {
                        done++;
                        yield return null;
                        continue;
                    }

                    if (TryStageTile(options, stagingRoot, basemapId, tile, out string[] assetPaths))
                    {
                        string bundleName = $"{basemapId}/tiles_1x1/{tile.TileId}";
                        builds.Add(new AssetBundleBuild
                        {
                            assetBundleName = bundleName,
                            assetNames = assetPaths,
                        });
                        tile.TerrainBundlePaths[basemapId] = $"bundles/{bundleName}";
                    }

                    done++;
                    yield return null;
                }
            }

            if (supertiles != null)
            {
                foreach (StreamingSupertileEntry st in supertiles)
                {
                    st.TerrainBundlePaths ??= new Dictionary<string, string>();

                    foreach (string basemapId in orthoBasemapIds)
                    {
                        float stageProgress = (float)(done + 1) / Mathf.Max(total, 1);
                        Report(onProgress, RealtimePackProgress.Create(
                            RealtimePackStage.TerrainBundle,
                            Mathf.Lerp(progressStart, stagingEnd, stageProgress),
                            stageProgress,
                            done + 1,
                            total,
                            $"{st.SupertileId}/{basemapId}"));

                        if (ShouldSkipBundleBuild(
                                options, st.SupertileId, basemapId, st.TerrainBundlePaths, isSupertile: true))
                        {
                            done++;
                            yield return null;
                            continue;
                        }

                        if (TryStageSupertile(options, stagingRoot, basemapId, st, out string[] assetPaths))
                        {
                            string bundleName = $"{basemapId}/tiles_{st.Factor}x{st.Factor}/{st.SupertileId}";
                            builds.Add(new AssetBundleBuild
                            {
                                assetBundleName = bundleName,
                                assetNames = assetPaths,
                            });
                            st.TerrainBundlePaths[basemapId] = $"bundles/{bundleName}";
                        }

                        done++;
                        yield return null;
                    }
                }
            }

            if (builds.Count == 0)
            {
                Debug.LogWarning("[ZGConnect.Realtime] No terrain bundles staged — skipping AssetBundle build.");
                RealtimePackStagingUtility.CleanupTerrain();
                yield break;
            }

            if (total > 0)
            {
                Report(onProgress, RealtimePackProgress.Create(
                    RealtimePackStage.TerrainBundle, stagingEnd, 1f, total, total));
                yield return null;
            }

            ZGConnectPathUtils.EnsureAssetFolder(bundleAssetOutput);

            bool incrementalBundleBuild = options.SkipExisting && builds.Count < total;
            string buildOutputAsset = incrementalBundleBuild
                ? $"{bundleAssetOutput}/{IncrementalBundleSubfolder}"
                : bundleAssetOutput;

            if (incrementalBundleBuild)
            {
                RealtimePackStagingUtility.DeleteAssetFolderIfExists(buildOutputAsset);
                ZGConnectPathUtils.EnsureAssetFolder(buildOutputAsset);
            }

            Report(onProgress, RealtimePackProgress.Create(
                RealtimePackStage.TerrainBundle,
                stagingEnd,
                0f,
                detail: $"Building {builds.Count} bundle(s)"));
            yield return null;

            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            bool ok = BuildPipeline.BuildAssetBundles(
                buildOutputAsset,
                builds.ToArray(),
                RealtimeAssetBundleBuildUtility.PackBuildOptions,
                target);

            if (!ok)
                throw new InvalidOperationException("[ZGConnect.Realtime] BuildPipeline.BuildAssetBundles failed.");

            if (incrementalBundleBuild)
            {
                CopyBuiltBundlesToOutput(buildOutputAsset, bundleAssetOutput, builds);
                RealtimePackStagingUtility.DeleteAssetFolderIfExists(buildOutputAsset);
            }

            RealtimePackStagingUtility.CleanupTerrain();
            AssetDatabase.Refresh();
            Report(onProgress, RealtimePackProgress.Create(
                RealtimePackStage.TerrainBundle,
                progressEnd,
                1f,
                detail: $"{builds.Count} bundle(s)"));
            yield return null;
        }

        static List<string> GetOrthoBasemapIds(RealtimePackOptions options)
        {
            var ids = new List<string>();
            if (options.Basemaps == null)
                return ids;

            foreach (DatasetFolderScanner.BasemapSource bm in options.Basemaps)
            {
                if (bm.Type != BasemapType.Ortho)
                    continue;

                string id = ZGConnectPathUtils.DeriveBasemapId(bm.FolderPath);
                if (!string.IsNullOrEmpty(id) && !ids.Contains(id))
                    ids.Add(id);
            }

            return ids;
        }

        static void Report(Action<RealtimePackProgress> onProgress, RealtimePackProgress progress) =>
            onProgress?.Invoke(progress);

        static bool ShouldSkipBundleBuild(
            RealtimePackOptions options,
            string id,
            string basemapId,
            Dictionary<string, string> terrainBundlePaths,
            bool isSupertile)
        {
            if (!options.SkipExisting)
                return false;

            if (isSupertile)
            {
                if (options.RebuiltSupertileIds != null && options.RebuiltSupertileIds.Contains(id))
                    return false;
            }
            else if (options.FreshlyPackedTileIds != null && options.FreshlyPackedTileIds.Contains(id))
            {
                return false;
            }

            if (terrainBundlePaths == null ||
                !terrainBundlePaths.TryGetValue(basemapId, out string bundleRel))
            {
                return false;
            }

            return RealtimePackReuseUtility.BundleFileExists(options.OutputRoot, bundleRel);
        }

        static bool TryStageTile(
            RealtimePackOptions options,
            string stagingRoot,
            string basemapId,
            StreamingTileEntry tile,
            out string[] assetPaths)
        {
            assetPaths = null;
            if (string.IsNullOrEmpty(tile.HeightmapPath))
                return false;

            string rawFull = Path.Combine(options.OutputRoot,
                tile.HeightmapPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(rawFull))
                return false;

            string folder = $"{stagingRoot}/{basemapId}/tiles_1x1/{tile.TileId}";
            ZGConnectPathUtils.EnsureAssetFolder(folder);

            int tileSizeMeters = options.Heightmap.Metadata.Settings.TileSizeMeters;
            TerrainLayer layer = TryCreateOrthoLayer(
                options,
                basemapId,
                tile.OrthoPaths,
                tile.TileId,
                hlodFactor: 1,
                tileSizeMeters,
                options.OrthoMaxResolution,
                folder,
                out string texPath);

            if (layer == null)
                return false;

            TerrainData td = CreateTerrainDataAsset(
                rawFull,
                tile.HeightmapRes,
                tile.GetTerrainSize(),
                options.FlipHeightmapVertically,
                layer,
                $"{folder}/TerrainData.asset");

            var paths = new List<string> { AssetPath(td) };
            if (!string.IsNullOrEmpty(texPath))
                paths.Add(texPath);

            if (TryCreateTerrainPrefab(td, tile.GetUnityPosition(), tile.TileId, folder, out string prefabPath))
                paths.Add(prefabPath);

            assetPaths = paths.Distinct().ToArray();
            return true;
        }

        static bool TryCreateTerrainPrefab(
            TerrainData td,
            Vector3 position,
            string tileId,
            string folder,
            out string prefabAssetPath)
        {
            prefabAssetPath = null;
            if (td == null)
                return false;

            GameObject instance = Terrain.CreateTerrainGameObject(td);
            instance.name = $"TileTerrain_{tileId}";
            instance.transform.position = position;
            try
            {
                prefabAssetPath = $"{folder}/TileTerrain_{tileId}.prefab";
                PrefabUtility.SaveAsPrefabAsset(instance, prefabAssetPath);
                return AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath) != null;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        static bool TryStageSupertile(
            RealtimePackOptions options,
            string stagingRoot,
            string basemapId,
            StreamingSupertileEntry st,
            out string[] assetPaths)
        {
            assetPaths = null;
            if (string.IsNullOrEmpty(st.HeightmapPath))
                return false;

            string rawFull = Path.Combine(options.OutputRoot,
                st.HeightmapPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(rawFull))
                return false;

            string folder = $"{stagingRoot}/{basemapId}/tiles_{st.Factor}x{st.Factor}/{st.SupertileId}";
            ZGConnectPathUtils.EnsureAssetFolder(folder);

            int orthoRes = st.Factor switch
            {
                2 => options.Hlod2OrthoRes,
                4 => options.Hlod4OrthoRes,
                _ => options.OrthoMaxResolution,
            };

            int tileSizeMeters = st.Factor * options.Heightmap.Metadata.Settings.TileSizeMeters;
            TerrainLayer layer = TryCreateOrthoLayer(
                options,
                basemapId,
                st.OrthoPaths,
                st.SupertileId,
                st.Factor,
                tileSizeMeters,
                orthoRes,
                folder,
                out string texPath);

            if (layer == null)
                return false;

            bool flip = options.FlipHeightmapVertically;
            TerrainData td = CreateTerrainDataAsset(
                rawFull,
                st.HeightmapRes,
                st.GetTerrainSize(),
                flip,
                layer,
                $"{folder}/TerrainData.asset");

            var paths = new List<string> { AssetPath(td) };
            if (!string.IsNullOrEmpty(texPath))
                paths.Add(texPath);

            if (TryCreateTerrainPrefab(td, st.GetUnityPosition(), st.SupertileId, folder, out string prefabPath))
                paths.Add(prefabPath);

            assetPaths = paths.Distinct().ToArray();
            return true;
        }

        static TerrainData CreateTerrainDataAsset(
            string rawFullPath,
            int heightmapRes,
            Vector3 terrainSize,
            bool flipHeightmap,
            TerrainLayer layer,
            string assetPath)
        {
            float[,] heights = RuntimeHeightmapUtility.DecodeRaw16(rawFullPath, heightmapRes, flipHeightmap);
            var td = new TerrainData
            {
                heightmapResolution = heightmapRes,
                size = terrainSize,
            };
            td.SetHeights(0, 0, heights);

            EnsureParentFolder(assetPath);
            AssetDatabase.CreateAsset(td, assetPath);

            if (layer != null)
            {
                td.terrainLayers = new[] { layer };
                RuntimeBasemapFactory.ApplyUniformAlphamap(td, 0);
                AssetDatabase.AddObjectToAsset(layer, td);
                EditorUtility.SetDirty(td);
            }

            AssetDatabase.SaveAssets();
            return td;
        }

        static TerrainLayer TryCreateOrthoLayer(
            RealtimePackOptions options,
            string basemapId,
            Dictionary<string, string> orthoPaths,
            string coordId,
            int hlodFactor,
            int tileSizeMeters,
            int maxTextureSize,
            string folder,
            out string textureAssetPath)
        {
            textureAssetPath = null;

            if (!TryResolveOrthoSourceFile(
                    options, basemapId, orthoPaths, coordId, hlodFactor, out string orthoFull))
            {
                return null;
            }

            string ext = Path.GetExtension(orthoFull);
            if (string.IsNullOrEmpty(ext))
                ext = ".png";

            textureAssetPath = $"{folder}/ortho{ext}";
            EnsureParentFolder(textureAssetPath);
            string textureFullPath = AssetPathToFull(textureAssetPath);
            string textureDir = Path.GetDirectoryName(textureFullPath);
            if (!string.IsNullOrEmpty(textureDir) && !Directory.Exists(textureDir))
                Directory.CreateDirectory(textureDir);
            File.Copy(orthoFull, textureFullPath, true);
            AssetDatabase.ImportAsset(textureAssetPath);
            ConfigureOrthoTextureImport(textureAssetPath, maxTextureSize, options);

            Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(textureAssetPath);
            if (tex == null)
                return null;

            return new TerrainLayer
            {
                name = $"{basemapId}_{coordId}",
                diffuseTexture = tex,
                tileSize = new Vector2(tileSizeMeters, tileSizeMeters),
                tileOffset = Vector2.zero,
                smoothnessSource = TerrainLayerSmoothnessSource.Constant,
                smoothness = 0f,
                metallic = 0f,
            };
        }

        static bool TryResolveOrthoSourceFile(
            RealtimePackOptions options,
            string basemapId,
            Dictionary<string, string> orthoPaths,
            string coordId,
            int hlodFactor,
            out string fullPath)
        {
            fullPath = null;
            if (string.IsNullOrEmpty(basemapId))
                return false;

            if (orthoPaths != null &&
                orthoPaths.TryGetValue(basemapId, out string orthoRel) &&
                !string.IsNullOrEmpty(orthoRel))
                {
                    string packed = Path.Combine(options.OutputRoot,
                        orthoRel.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(packed))
                    {
                        fullPath = packed;
                        return true;
                }
            }

                string orthoFolder = hlodFactor == 1 ? "ortho_1x1" : $"ortho_{hlodFactor}x{hlodFactor}";
                foreach (string suffix in new[] { ".png", "png" })
                {
                    string rel = $"basemaps/{basemapId}/{orthoFolder}/{coordId}{suffix}";
                    string candidate = Path.Combine(options.OutputRoot,
                        rel.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(candidate))
                    {
                        fullPath = candidate;
                        return true;
                }
            }

            if (hlodFactor == 1)
                return TryResolveLeafOrthoFromSourceDataset(options, basemapId, coordId, out fullPath);

            return false;
        }

        static bool TryResolveLeafOrthoFromSourceDataset(
            RealtimePackOptions options,
            string basemapId,
            string tileId,
            out string fullPath)
        {
            fullPath = null;
            if (options.Basemaps == null)
                return false;

            foreach (DatasetFolderScanner.BasemapSource bm in options.Basemaps)
            {
                if (bm.Type != BasemapType.Ortho || bm.Metadata?.Tiles == null)
                    continue;

                if (ZGConnectPathUtils.DeriveBasemapId(bm.FolderPath) != basemapId)
                    continue;

                var lookup = ZGConnectPathUtils.BuildOrthoLookup(bm.Metadata.Tiles);
                if (!lookup.TryGetValue(tileId, out OrthoTileJson ortho))
                    continue;

                string candidate = Path.Combine(bm.FolderPath, ortho.TextureFile);
                if (!File.Exists(candidate))
                    continue;

                fullPath = candidate;
                return true;
            }

            return false;
        }

        public static void ConfigureOrthoTextureImportPublic(
            string assetPath,
            int maxTextureSize,
            RealtimePackOptions options) =>
            ConfigureOrthoTextureImport(assetPath, maxTextureSize, options);

        static void ConfigureOrthoTextureImport(string assetPath, int maxTextureSize, RealtimePackOptions options)
        {
            var imp = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (imp == null)
                return;

            bool crunch = options != null && options.OrthoCrunchCompression;
            int quality = Mathf.Clamp(options?.OrthoCrunchQuality ?? 75, 0, 100);

            imp.textureType = TextureImporterType.Default;
            imp.textureCompression = TextureImporterCompression.Compressed;
            imp.crunchedCompression = crunch;
            if (crunch)
                imp.compressionQuality = quality;
            imp.sRGBTexture = true;
            imp.mipmapEnabled = true;
            imp.streamingMipmaps = true;
            imp.wrapMode = TextureWrapMode.Clamp;
            imp.maxTextureSize = maxTextureSize;
            imp.alphaSource = TextureImporterAlphaSource.None;
            imp.SaveAndReimport();
        }

        static string AssetPath(UnityEngine.Object obj) => AssetDatabase.GetAssetPath(obj);

        static string AssetPathToFull(string assetPath) =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Application.dataPath), assetPath));

        static void EnsureParentFolder(string assetPath)
        {
            string folder = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(folder))
                ZGConnectPathUtils.EnsureAssetFolder(folder);
        }

        static void CopyBuiltBundlesToOutput(
            string tempBundleAssetRoot,
            string finalBundleAssetRoot,
            List<AssetBundleBuild> builds)
        {
            string tempFull = ZGConnectPathUtils.AssetPathToFullPath(tempBundleAssetRoot);
            string finalFull = ZGConnectPathUtils.AssetPathToFullPath(finalBundleAssetRoot);

            foreach (AssetBundleBuild build in builds)
            {
                string relative = build.assetBundleName.Replace('/', Path.DirectorySeparatorChar);
                string src = Path.Combine(tempFull, relative);
                string dst = Path.Combine(finalFull, relative);

                string dstDir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dstDir))
                    Directory.CreateDirectory(dstDir);

                if (File.Exists(src))
                    File.Copy(src, dst, true);

                string srcManifest = src + ".manifest";
                if (File.Exists(srcManifest))
                    File.Copy(srcManifest, dst + ".manifest", true);
            }
        }

    }
}
