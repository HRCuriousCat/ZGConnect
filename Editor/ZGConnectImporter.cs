using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace ZGConnect.Editor
{
    /// <summary>
    /// Describes one basemap source selected for import.
    /// Built by ZGConnectDatasetManagerWindow and passed to ZGConnectImporter.Import().
    /// </summary>
    public class BasemapImportSource
    {
        public string BasemapId;
        public string DisplayName;
        public BasemapType Type;
        /// <summary>Populated for Ortho sources; null for Tiled.</summary>
        public OrthoMetadataJson Metadata;
        /// <summary>Populated for Tiled sources; null for Ortho.</summary>
        public TiledMetadataJson TiledMetadata;
        public string FolderPath;
    }

    public class ZGConnectImportSettings
    {
        public string OutputFolder = "Assets/Generated/ZGConnect";

        public int HeightmapResolution  = 1025;  // 513 (downsampled) or 1025 (native)
        public int BasemapMaxTextureSize = 2048;  // applied via TextureImporter; source stays unchanged

        public bool ImportAllTiles = true;
        public int  MaxTiles = 25;
        public float MaxInvalidRatio = 0.10f;

        public bool SkipExisting           = true;
        public bool CreateSceneObjects     = true;
        public bool AddCityTileComponents  = true;
        public bool CreateCityDatasetAsset = true;
        public bool SetTerrainNeighbors    = true;

        // Region filter — import only tiles whose bounding box intersects this rectangle.
        // Coordinates are in EPSG:3765 (metres). Ignored when FilterByRegion = false.
        public bool  FilterByRegion = false;
        public float RegionMinE = 0;   // easting min
        public float RegionMaxE = 0;   // easting max
        public float RegionMinN = 0;   // northing min
        public float RegionMaxN = 0;   // northing max

        // Advanced
        public bool  FlipHeightmapVertically = true;
        public bool  DrawInstanced    = true;
        public float PixelError       = 5f;
        public int   BasemapDistance  = 1000;
    }

    /// <summary>
    /// Settings for the buildings import pass.
    /// Passed from ZGConnectDatasetManagerWindow to ZGConnectImporter.ImportBuildings().
    /// </summary>
    public class BuildingsImportSettings
    {
        /// <summary>Asset output root. GLB copies go to OutputFolder/Buildings/,
        /// generated prefabs to OutputFolder/Prefabs/Buildings/.</summary>
        public string OutputFolder = "Assets/Generated/ZGConnect";

        /// <summary>Skip tiles that already have buildingsLod0Prefab set in the dataset.</summary>
        public bool SkipExisting = true;

        /// <summary>When importing from raw building_meshes/, skip tiles that already
        /// have a baked GLB under building_meshes/Processed/.</summary>
        public bool SkipProcessed = false;

        /// <summary>Attach a BuildingData component to every direct-child building
        /// inside each tile prefab and populate buildingId + tileId from the GLB
        /// node name and companion JSON.</summary>
        public bool AttachBuildingData = true;

        /// <summary>When true, only GLB files whose tile bounding box intersects
        /// the region defined by RegionMin/MaxE/N are imported.</summary>
        public bool  FilterByRegion = false;
        public float RegionMinE = 0;
        public float RegionMaxE = 0;
        public float RegionMinN = 0;
        public float RegionMaxN = 0;

        [Tooltip("Remap UVs, split wall/roof submeshes, and assign facade/roof materials.")]
        public bool ProcessBuildingSurfaces = true;

        /// <summary>Process roof triangles only: tile-space UVs (0–1) and orthophoto diffuse.
        /// Mutually exclusive with <see cref="ProcessBuildingSurfaces"/>.</summary>
        public bool ProcessRoofOrthophotoUv = false;

        /// <summary>Basemap id for roof orthophoto (e.g. "ortho"). Matches terrain import naming.</summary>
        public string RoofOrthophotoBasemapId = "ortho";

        /// <summary>When tile textures are missing, copy ortho PNGs from this source (first selected ortho in UI).</summary>
        public BasemapImportSource RoofOrthophotoSource;

        /// <summary>Max orthophoto import size when copying from <see cref="RoofOrthophotoSource"/>.</summary>
        public int BasemapMaxTextureSize = 2048;

        /// <summary>Skip facade/roof mesh processing for faster bulk import.
        /// Re-run surfaces later via ZG Connect → Buildings → Reprocess Surfaces.</summary>
        public bool FastImport = false;

        [Tooltip("Facade/roof profiles and texture variants.")]
        public BuildingSurfaceSettings SurfaceSettings;
    }

    /// <summary>Options for baking processed GLBs into a <see cref="BuildingSurfacePipeline.ProcessedFolderName"/> subfolder.</summary>
    public class BuildingsBakeSettings
    {
        public string OutputFolder = "Assets/Generated/ZGConnect";
        public BuildingSurfaceSettings SurfaceSettings;
        public bool SkipExisting = true;
        public bool FilterByRegion = false;
        public float RegionMinE, RegionMaxE, RegionMinN, RegionMaxN;

        public bool ProcessBuildingSurfaces = true;
        public bool ProcessRoofOrthophotoUv = false;
        public string RoofOrthophotoBasemapId = "ortho";
        public BasemapImportSource RoofOrthophotoSource;
        public int BasemapMaxTextureSize = 2048;
    }

    public static class ZGConnectImporter
    {
        /// <param name="heightmapMeta">Parsed heightmap metadata.json.</param>
        /// <param name="heightmapFolder">OS path to the folder containing the .raw files.</param>
        /// <param name="selectedBasemaps">
        ///   All basemap sources to import (may be empty for heightmap-only import).
        ///   Order determines layer index: index 0 is active by default.
        /// </param>
        public static void Import(
            HeightmapMetadataJson       heightmapMeta,
            string                      heightmapFolder,
            List<BasemapImportSource>   selectedBasemaps,
            ZGConnectImportSettings     settings,
            Action<float, string>       onProgress = null)
        {
            bool hasBasemaps = selectedBasemaps != null && selectedBasemaps.Count > 0;

            string terrainDataFolder = $"{settings.OutputFolder}/TerrainData";
            string configFolder      = $"{settings.OutputFolder}/Config";

            ZGConnectPathUtils.EnsureAssetFolder(terrainDataFolder);
            ZGConnectPathUtils.EnsureAssetFolder(configFolder);

            // Pre-compute per-basemap folders and tile lookups
            string[]                                 bmTexFolders        = null;
            string[]                                 bmLayerFolders      = null;
            string[]                                 bmSharedLayerFolders = null;
            string[]                                 bmSplatmapFolders   = null;
            Dictionary<string, OrthoTileJson>[]      bmLookups           = null;
            Dictionary<string, SplatmapTileJson>[]   bmTiledLookups      = null;
            TerrainLayer[][]                         bmSharedLayers      = null;

            if (hasBasemaps)
            {
                int n = selectedBasemaps.Count;
                bmTexFolders         = new string[n];
                bmLayerFolders       = new string[n];
                bmSharedLayerFolders = new string[n];
                bmSplatmapFolders    = new string[n];
                bmLookups            = new Dictionary<string, OrthoTileJson>[n];
                bmTiledLookups       = new Dictionary<string, SplatmapTileJson>[n];
                bmSharedLayers       = new TerrainLayer[n][];

                for (int b = 0; b < n; b++)
                {
                    var bm = selectedBasemaps[b];
                    string bmSubfolder = bm.Type == BasemapType.Ortho
                        ? $"{bm.BasemapId}_{bm.Metadata.Settings.TextureResolution}"
                        : bm.BasemapId;

                    bmTexFolders[b]         = $"{settings.OutputFolder}/Textures/{bmSubfolder}";
                    bmLayerFolders[b]       = $"{settings.OutputFolder}/TerrainLayers/{bmSubfolder}";
                    bmSharedLayerFolders[b] = $"{bmLayerFolders[b]}/Shared";
                    bmSplatmapFolders[b]    = $"{bmLayerFolders[b]}/Splatmaps";

                    ZGConnectPathUtils.EnsureAssetFolder(bmTexFolders[b]);
                    ZGConnectPathUtils.EnsureAssetFolder(bmLayerFolders[b]);

                    if (bm.Type == BasemapType.Ortho)
                    {
                        bmLookups[b]      = ZGConnectPathUtils.BuildOrthoLookup(bm.Metadata.Tiles);
                        bmTiledLookups[b] = new Dictionary<string, SplatmapTileJson>();
                    }
                    else
                    {
                        ZGConnectPathUtils.EnsureAssetFolder(bmSharedLayerFolders[b]);
                        ZGConnectPathUtils.EnsureAssetFolder(bmSplatmapFolders[b]);
                        bmLookups[b]      = new Dictionary<string, OrthoTileJson>();
                        bmTiledLookups[b] = ZGConnectPathUtils.BuildTiledLookup(bm.TiledMetadata.Tiles);
                        bmSharedLayers[b] = ImportTiledSharedLayers(
                            bm, bmSharedLayerFolders[b], bmTexFolders[b], settings.BasemapMaxTextureSize);
                    }
                }
            }

            CityDataset datasetAsset = LoadOrCreateDatasetAsset(
                heightmapMeta, selectedBasemaps, configFolder, settings);

            string parentName = "ZG Connect Terrains";
            GameObject parent = GameObject.Find(parentName) ?? new GameObject(parentName);

            var terrainByCoord = new Dictionary<string, Terrain>();
            var tiles = heightmapMeta.Tiles;

            int importedCount = 0;
            int skippedCount  = 0;
            int limit = settings.ImportAllTiles
                ? tiles.Count
                : Mathf.Min(settings.MaxTiles, tiles.Count);

            try
            {
                for (int i = 0; i < tiles.Count; i++)
                {
                    HeightmapTileJson tile = tiles[i];

                    if (tile.InvalidRatio > settings.MaxInvalidRatio)
                    {
                        skippedCount++;
                        continue;
                    }

                    if (settings.FilterByRegion && !TileIntersectsRegion(tile, settings))
                    {
                        skippedCount++;
                        continue;
                    }

                    if (!settings.ImportAllTiles && importedCount >= settings.MaxTiles)
                        break;

                    float progress = limit > 0 ? (float)importedCount / limit : 0f;
                    onProgress?.Invoke(progress, $"Tile {tile.Left}_{tile.Bottom}");

                    string rawPath = Path.Combine(heightmapFolder, tile.RawFile);
                    if (!File.Exists(rawPath))
                    {
                        Debug.LogWarning($"[ZGConnect] RAW not found: {rawPath}");
                        skippedCount++;
                        continue;
                    }

                    string tileId      = $"{tile.Left}_{tile.Bottom}";
                    string assetName   = $"Tile_{tileId}";
                    string tdAssetPath = $"{terrainDataFolder}/{assetName}.asset";

                    bool tdAlreadyExists = AssetDatabase.LoadAssetAtPath<TerrainData>(tdAssetPath) != null;

                    // Determine which basemap layers need to be created
                    string[] layerPaths  = hasBasemaps ? new string[selectedBasemaps.Count] : null;
                    bool[]   layerNeeded = hasBasemaps ? new bool[selectedBasemaps.Count]   : null;
                    bool     needsAnyBasemap = false;

                    if (hasBasemaps)
                    {
                        for (int b = 0; b < selectedBasemaps.Count; b++)
                        {
                            var bm = selectedBasemaps[b];
                            if (bm.Type == BasemapType.Ortho)
                            {
                                layerPaths[b]  = $"{bmLayerFolders[b]}/{assetName}_{bm.BasemapId}_{bm.Metadata.Settings.TextureResolution}.terrainlayer";
                                layerNeeded[b] = AssetDatabase.LoadAssetAtPath<TerrainLayer>(layerPaths[b]) == null;
                            }
                            else
                            {
                                layerPaths[b]  = $"{bmLayerFolders[b]}/{assetName}_{bm.BasemapId}.asset";
                                layerNeeded[b] = AssetDatabase.LoadAssetAtPath<TerrainLayerSet>(layerPaths[b]) == null;
                            }
                            if (layerNeeded[b]) needsAnyBasemap = true;
                        }
                    }

                    bool needsTerrain = !tdAlreadyExists;

                    if (settings.SkipExisting && !needsTerrain && !needsAnyBasemap)
                    {
                        skippedCount++;
                        continue;
                    }

                    // ── TerrainData: create or load ───────────────────────────
                    TerrainData td;
                    if (tdAlreadyExists && settings.SkipExisting)
                    {
                        td = AssetDatabase.LoadAssetAtPath<TerrainData>(tdAssetPath);
                    }
                    else
                    {
                        td = CreateTerrainData(rawPath, tile, settings);
                        AssetDatabase.CreateAsset(td, tdAssetPath);
                    }

                    // ── Basemap layers: create new / load existing ────────────
                    var allLayers       = new List<TerrainLayer>();
                    var allLayerEntries = new List<BasemapLayerEntry>();
                    var allLayerSets    = new List<TerrainLayerSet>();
                    Texture2D    primaryTex   = null;
                    TerrainLayer primaryLayer = null;

                    if (hasBasemaps)
                    {
                        for (int b = 0; b < selectedBasemaps.Count; b++)
                        {
                            var bm = selectedBasemaps[b];

                            // ── Ortho ──────────────────────────────────────────
                            if (bm.Type == BasemapType.Ortho)
                            {
                                TerrainLayer layer = null;
                                Texture2D    tex   = null;

                                if (!layerNeeded[b])
                                {
                                    layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(layerPaths[b]);
                                    if (layer != null) tex = layer.diffuseTexture;
                                }
                                else if (bmLookups[b].TryGetValue(tileId, out OrthoTileJson orthoTile))
                                {
                                    string srcTex  = Path.Combine(bm.FolderPath, orthoTile.TextureFile);
                                    string ext     = Path.GetExtension(orthoTile.TextureFile);
                                    string dstPath = $"{bmTexFolders[b]}/{tileId}_{bm.BasemapId}_{bm.Metadata.Settings.TextureResolution}{ext}";
                                    string dstFull = ZGConnectPathUtils.AssetPathToFullPath(dstPath);

                                    if (File.Exists(srcTex))
                                    {
                                        if (!File.Exists(dstFull))
                                            File.Copy(srcTex, dstFull);

                                        AssetDatabase.ImportAsset(dstPath);
                                        ConfigureBasemapTextureImport(dstPath, settings.BasemapMaxTextureSize);
                                        tex = AssetDatabase.LoadAssetAtPath<Texture2D>(dstPath);

                                        if (tex != null)
                                        {
                                            layer = new TerrainLayer
                                            {
                                                diffuseTexture = tex,
                                                tileSize       = new Vector2(
                                                    heightmapMeta.Settings.TileSizeMeters,
                                                    heightmapMeta.Settings.TileSizeMeters),
                                                tileOffset = Vector2.zero
                                            };
                                            AssetDatabase.CreateAsset(layer, layerPaths[b]);
                                        }
                                    }
                                    else
                                    {
                                        Debug.LogWarning($"[ZGConnect] Basemap PNG not found: {srcTex}");
                                    }
                                }

                                if (layer != null)
                                {
                                    allLayers.Add(layer);
                                    allLayerEntries.Add(new BasemapLayerEntry
                                    {
                                        basemapId    = bm.BasemapId,
                                        texture      = tex,
                                        terrainLayer = layer
                                    });
                                    if (b == 0) { primaryTex = tex; primaryLayer = layer; }
                                }
                            }
                            // ── Tiled ──────────────────────────────────────────
                            else
                            {
                                TerrainLayerSet layerSet = null;

                                if (!layerNeeded[b])
                                {
                                    layerSet = AssetDatabase.LoadAssetAtPath<TerrainLayerSet>(layerPaths[b]);
                                }
                                else if (bmSharedLayers[b] != null && bmSharedLayers[b].Length > 0)
                                {
                                    layerSet = ImportTiledSplatmapForTile(
                                        td, bm, bmSharedLayers[b], tileId,
                                        layerPaths[b], bmSplatmapFolders[b],
                                        bmTiledLookups[b]);
                                }

                                if (layerSet != null)
                                    allLayerSets.Add(layerSet);
                            }
                        }

                        // Apply ortho layers if any (tiled already applied inside ImportTiledSplatmapForTile)
                        if (allLayers.Count > 0)
                        {
                            td.terrainLayers = allLayers.ToArray();
                            ApplyDefaultAlphamap(td);
                            EditorUtility.SetDirty(td);
                        }
                    }

                    // ── Scene object: create or find existing ─────────────────
                    GameObject terrainObj = null;
                    if (settings.CreateSceneObjects)
                    {
                        Transform existing = parent.transform.Find(assetName);
                        if (existing != null)
                        {
                            terrainObj = existing.gameObject;
                        }
                        else
                        {
                            terrainObj = Terrain.CreateTerrainGameObject(td);
                            terrainObj.name = assetName;
                            terrainObj.transform.SetParent(parent.transform);
                            terrainObj.transform.position = new Vector3(
                                tile.UnityPosition.X,
                                tile.UnityPosition.Y,
                                tile.UnityPosition.Z);

                            Terrain terrain = terrainObj.GetComponent<Terrain>();
                            terrain.drawInstanced        = settings.DrawInstanced;
                            terrain.heightmapPixelError  = settings.PixelError;
                            terrain.basemapDistance       = settings.BasemapDistance;
                            terrain.allowAutoConnect      = false;

                            TerrainCollider col = terrainObj.GetComponent<TerrainCollider>();
                            if (col != null) col.terrainData = td;
                        }

                        Terrain      terrainComp = terrainObj.GetComponent<Terrain>();
                        TerrainCollider terrainCol = terrainObj.GetComponent<TerrainCollider>();
                        terrainByCoord[tileId] = terrainComp;

                        if (settings.AddCityTileComponents)
                        {
                            CityTile ct = terrainObj.GetComponent<CityTile>()
                                       ?? terrainObj.AddComponent<CityTile>();
                            ct.tileId          = tileId;
                            ct.left            = tile.Left;
                            ct.bottom          = tile.Bottom;
                            ct.right           = tile.Right;
                            ct.top             = tile.Top;
                            ct.unityPosition   = terrainObj.transform.position;
                            ct.terrain         = terrainComp;
                            ct.terrainCollider = terrainCol;
                            if (primaryLayer != null) ct.basemapAssigned = true;
                        }
                    }

                    // ── CityDataset record ────────────────────────────────────
                    if (settings.CreateCityDatasetAsset && datasetAsset != null)
                    {
                        UpdateDatasetRecord(
                            datasetAsset, tileId, tile,
                            td, primaryTex, primaryLayer,
                            terrainObj, allLayerEntries, allLayerSets);
                    }

                    importedCount++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (settings.SetTerrainNeighbors && settings.CreateSceneObjects)
                ApplyNeighbors(tiles, terrainByCoord, heightmapMeta.Settings.TileSizeMeters);

            if (datasetAsset != null)
                EditorUtility.SetDirty(datasetAsset);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[ZGConnect] Done. Imported: {importedCount}, Skipped: {skippedCount}");
            EditorUtility.DisplayDialog(
                "ZG Connect Import Complete",
                $"Imported:  {importedCount}\nSkipped:   {skippedCount}",
                "OK");
        }

        // ── Scene instantiation ────────────────────────────────────────────────

        /// <summary>
        /// Creates terrain GameObjects in the active scene for every tile in the
        /// dataset that has TerrainData but no scene object assigned yet.
        /// Tiles that are already instantiated are skipped.
        /// Called from the TerrainStreamingController inspector in edit mode.
        /// </summary>
        public static void InstantiateAllTerrains(CityDataset dataset)
        {
            if (dataset == null) return;

            string    parentName = "ZG Connect Terrains";
            GameObject parent   = GameObject.Find(parentName) ?? new GameObject(parentName);

            var terrainByCoord = new Dictionary<string, Terrain>();
            int created = 0;
            int skipped = 0;

            foreach (CityTileRecord record in dataset.tiles)
            {
                // Resolve TerrainData — may be a legacy direct ref or an Addressable asset
                TerrainData td = record.terrainData;
                if (td == null && record.terrainDataRef != null &&
                    record.terrainDataRef.RuntimeKeyIsValid())
                {
                    // Editor-only synchronous load via asset path convention
                    string path = $"Assets/Generated/ZGConnect/TerrainData/Tile_{record.tileId}.asset";
                    td = AssetDatabase.LoadAssetAtPath<TerrainData>(path);
                }

                if (td == null) { skipped++; continue; }

                if (record.sceneObject != null)
                {
                    Terrain t = record.sceneObject.GetComponent<Terrain>();
                    if (t != null)
                        terrainByCoord[$"{record.left}_{record.bottom}"] = t;
                    skipped++;
                    continue;
                }

                GameObject terrainObj = Terrain.CreateTerrainGameObject(td);
                terrainObj.name = $"Tile_{record.tileId}";
                terrainObj.transform.SetParent(parent.transform);
                terrainObj.transform.position = record.unityPosition;

                Terrain terrain = terrainObj.GetComponent<Terrain>();
                terrain.drawInstanced       = true;
                terrain.heightmapPixelError = 5f;
                terrain.basemapDistance     = 1000;
                terrain.allowAutoConnect    = false;

                TerrainCollider col = terrainObj.GetComponent<TerrainCollider>();
                if (col != null) col.terrainData = td;

                CityTile ct = terrainObj.AddComponent<CityTile>();
                ct.tileId          = record.tileId;
                ct.left            = record.left;
                ct.bottom          = record.bottom;
                ct.right           = record.right;
                ct.top             = record.top;
                ct.unityPosition   = record.unityPosition;
                ct.terrain         = terrain;
                ct.terrainCollider = col;
                ct.basemapAssigned = record.primaryBasemapTexture != null
                                  || (record.basemapLayers != null && record.basemapLayers.Count > 0);

                record.sceneObject = terrainObj;
                terrainByCoord[$"{record.left}_{record.bottom}"] = terrain;
                created++;
            }

            int tileSize = dataset.tileSizeMeters;
            foreach (CityTileRecord record in dataset.tiles)
            {
                string key = $"{record.left}_{record.bottom}";
                if (!terrainByCoord.TryGetValue(key, out Terrain current)) continue;

                terrainByCoord.TryGetValue($"{record.left - tileSize}_{record.bottom}", out Terrain left);
                terrainByCoord.TryGetValue($"{record.left + tileSize}_{record.bottom}", out Terrain right);
                terrainByCoord.TryGetValue($"{record.left}_{record.bottom + tileSize}",  out Terrain top);
                terrainByCoord.TryGetValue($"{record.left}_{record.bottom - tileSize}", out Terrain bottom);

                current.SetNeighbors(left, top, right, bottom);
                current.Flush();
            }

            EditorUtility.SetDirty(dataset);
            AssetDatabase.SaveAssets();

            Debug.Log($"[ZGConnect] Instantiated {created} terrain tile(s), skipped {skipped}.");
        }

        /// <summary>
        /// Instantiates a building group GameObject in the active scene for every
        /// tile that has a buildingsLod0Prefab but no buildingsSceneObject yet.
        /// Each instance is parented under "ZG Connect Buildings", starts active,
        /// and is wired into the dataset so TerrainStreamingController can still
        /// enable/disable tiles by distance via SetActive.
        /// </summary>
        public static void InstantiateAllBuildings(CityDataset dataset)
        {
            if (dataset == null) return;

            string     parentName = "ZG Connect Buildings";
            GameObject parent     = GameObject.Find(parentName) ?? new GameObject(parentName);
            parent.SetActive(true);

            int created = 0;
            int skipped = 0;

            foreach (CityTileRecord record in dataset.tiles)
            {
                if (record.buildingsLod0Prefab == null) { skipped++; continue; }

                if (record.buildingsSceneObject != null) { skipped++; continue; }

                // PrefabUtility.InstantiatePrefab preserves the prefab connection
                // and registers with the Undo system.
                GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(
                    record.buildingsLod0Prefab, parent.transform);

                instance.name = $"TileBuildings_{record.tileId}";
                instance.SetActive(true);

                Undo.RegisterCreatedObjectUndo(instance, "Instantiate All Buildings");

                record.buildingsSceneObject = instance;

                TerrainStreamingController streamer =
                    UnityEngine.Object.FindFirstObjectByType<TerrainStreamingController>();
                if (streamer != null)
                    streamer.ApplySharedBuildingMaterials(instance, record);

                created++;
            }

            EditorUtility.SetDirty(dataset);
            AssetDatabase.SaveAssets();

            Debug.Log($"[ZGConnect] Instantiated {created} building group(s), skipped {skipped}.");
        }

        // ── Private helpers ────────────────────────────────────────────────────

        private static TerrainData CreateTerrainData(
            string rawPath, HeightmapTileJson tile, ZGConnectImportSettings settings)
        {
            int srcRes        = tile.HeightmapResolution;
            int dstRes        = settings.HeightmapResolution;
            int expectedBytes = srcRes * srcRes * 2;
            byte[] bytes      = File.ReadAllBytes(rawPath);

            if (bytes.Length != expectedBytes)
                throw new Exception(
                    $"[ZGConnect] RAW size mismatch: {rawPath}\n" +
                    $"Expected {expectedBytes} bytes, got {bytes.Length}.");

            float[,] src = new float[srcRes, srcRes];
            int idx = 0;
            for (int y = 0; y < srcRes; y++)
            {
                // GDAL exports rows top→bottom; Unity terrain expects bottom→top.
                int destY = settings.FlipHeightmapVertically ? (srcRes - 1 - y) : y;
                for (int x = 0; x < srcRes; x++)
                {
                    src[destY, x] = BitConverter.ToUInt16(bytes, idx) / 65535f;
                    idx += 2;
                }
            }

            float[,] heights;
            int finalRes;
            if (dstRes < srcRes)
            {
                heights  = BilinearDownsample(src, srcRes, dstRes);
                finalRes = dstRes;
            }
            else
            {
                if (dstRes > srcRes)
                    Debug.LogWarning($"[ZGConnect] Requested resolution {dstRes} exceeds source {srcRes}. Using native {srcRes}.");
                heights  = src;
                finalRes = srcRes;
            }

            TerrainData td         = new TerrainData();
            td.heightmapResolution = finalRes;
            td.size                = new Vector3(
                tile.TerrainSize.X,
                tile.TerrainSize.Y,
                tile.TerrainSize.Z);
            td.SetHeights(0, 0, heights);
            return td;
        }

        /// <summary>
        /// Bilinearly resamples a square heightmap from srcRes to dstRes.
        /// Endpoint-aligned: corners map exactly, no border clamping artifacts.
        /// </summary>
        private static float[,] BilinearDownsample(float[,] src, int srcRes, int dstRes)
        {
            float[,] dst   = new float[dstRes, dstRes];
            float    scale = (float)(srcRes - 1) / (dstRes - 1);

            for (int y = 0; y < dstRes; y++)
            {
                float srcY = y * scale;
                int   y0   = Mathf.FloorToInt(srcY);
                int   y1   = Mathf.Min(y0 + 1, srcRes - 1);
                float fy   = srcY - y0;

                for (int x = 0; x < dstRes; x++)
                {
                    float srcX = x * scale;
                    int   x0   = Mathf.FloorToInt(srcX);
                    int   x1   = Mathf.Min(x0 + 1, srcRes - 1);
                    float fx   = srcX - x0;

                    dst[y, x] = Mathf.Lerp(
                        Mathf.Lerp(src[y0, x0], src[y0, x1], fx),
                        Mathf.Lerp(src[y1, x0], src[y1, x1], fx),
                        fy);
                }
            }

            return dst;
        }

        /// <summary>
        /// Writes a uniform alphamap where layer 0 = 1.0 and all other channels = 0.0.
        /// TerrainData.terrainLayers must be set before calling this.
        /// </summary>
        private static void ApplyDefaultAlphamap(TerrainData td)
        {
            int n = td.terrainLayers != null ? td.terrainLayers.Length : 1;
            int w = td.alphamapWidth;
            int h = td.alphamapHeight;
            float[,,] maps = new float[h, w, n];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    maps[y, x, 0] = 1f;
            td.SetAlphamaps(0, 0, maps);
        }

        private static void ConfigureBasemapTextureImport(string assetPath, int maxTextureSize)
        {
            TextureImporter imp = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (imp == null) return;
            imp.textureType             = TextureImporterType.Default;
            imp.sRGBTexture             = true;
            imp.mipmapEnabled           = true;
            imp.streamingMipmaps        = true;   // lets Unity drop to lower mips for distant tiles
            imp.streamingMipmapsPriority = 0;
            imp.wrapMode                = TextureWrapMode.Clamp;   // ortho tiles map once — no repeat
            imp.maxTextureSize          = maxTextureSize;
            imp.SaveAndReimport();
        }

        /// <summary>Shared ortho texture import settings (terrain + building roof orthophoto).</summary>
        public static void ConfigureOrthoBasemapTexture(string assetPath, int maxTextureSize) =>
            ConfigureBasemapTextureImport(assetPath, maxTextureSize);

        /// <summary>
        /// Import settings for tiled terrain layer textures (grass, forest, asphalt, etc.).
        /// Wrap mode is Repeat so the texture tiles across the terrain surface.
        /// </summary>
        private static void ConfigureTiledLayerTextureImport(string assetPath, int maxTextureSize)
        {
            TextureImporter imp = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (imp == null) return;
            imp.textureType             = TextureImporterType.Default;
            imp.sRGBTexture             = true;
            imp.mipmapEnabled           = true;
            imp.streamingMipmaps        = true;
            imp.streamingMipmapsPriority = 0;
            imp.wrapMode                = TextureWrapMode.Repeat;  // must repeat — texture tiles across terrain
            imp.maxTextureSize          = maxTextureSize;
            imp.SaveAndReimport();
        }

        /// <summary>
        /// Looks for a normal map alongside the source diffuse texture.
        /// Convention: if diffuse is "grass.png", normal map is "grass_normal.png"
        /// in the same source directory. If found, copies it to texFolder and imports
        /// it as a normal map. Returns the Texture2D asset or null if not found.
        /// </summary>
        private static Texture2D TryImportNormalMap(string srcDiffusePath, string texFolder, int maxTextureSize)
        {
            string dir          = Path.GetDirectoryName(srcDiffusePath);
            string nameNoExt    = Path.GetFileNameWithoutExtension(srcDiffusePath);
            string ext          = Path.GetExtension(srcDiffusePath);
            string normalFile   = $"{nameNoExt}_normal{ext}";
            string srcNormal    = Path.Combine(dir, normalFile);

            if (!File.Exists(srcNormal))
                return null;

            string dstPath = $"{texFolder}/{normalFile}";
            string dstFull = ZGConnectPathUtils.AssetPathToFullPath(dstPath);

            if (!File.Exists(dstFull))
                File.Copy(srcNormal, dstFull);

            AssetDatabase.ImportAsset(dstPath);
            ConfigureNormalMapTextureImport(dstPath, maxTextureSize);

            return AssetDatabase.LoadAssetAtPath<Texture2D>(dstPath);
        }

        private static void ConfigureNormalMapTextureImport(string assetPath, int maxTextureSize)
        {
            TextureImporter imp = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (imp == null) return;
            imp.textureType    = TextureImporterType.NormalMap;
            imp.mipmapEnabled  = true;
            imp.wrapMode       = TextureWrapMode.Repeat;
            imp.maxTextureSize = maxTextureSize;
            imp.SaveAndReimport();
        }

        private static void ApplyNeighbors(
            List<HeightmapTileJson> tiles,
            Dictionary<string, Terrain> terrainByCoord,
            int tileSize)
        {
            foreach (HeightmapTileJson tile in tiles)
            {
                string key = $"{tile.Left}_{tile.Bottom}";
                if (!terrainByCoord.TryGetValue(key, out Terrain current)) continue;

                terrainByCoord.TryGetValue($"{tile.Left - tileSize}_{tile.Bottom}", out Terrain left);
                terrainByCoord.TryGetValue($"{tile.Left + tileSize}_{tile.Bottom}", out Terrain right);
                terrainByCoord.TryGetValue($"{tile.Left}_{tile.Bottom + tileSize}", out Terrain top);
                terrainByCoord.TryGetValue($"{tile.Left}_{tile.Bottom - tileSize}", out Terrain bottom);

                current.SetNeighbors(left, top, right, bottom);
                current.Flush();
            }
            Debug.Log("[ZGConnect] Terrain neighbors applied.");
        }

        private static CityDataset LoadOrCreateDatasetAsset(
            HeightmapMetadataJson       heightmapMeta,
            List<BasemapImportSource>   selectedBasemaps,
            string                      configFolder,
            ZGConnectImportSettings     settings)
        {
            if (!settings.CreateCityDatasetAsset) return null;

            string path = $"{configFolder}/ZGConnectDataset.asset";
            CityDataset asset = AssetDatabase.LoadAssetAtPath<CityDataset>(path);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<CityDataset>();
                AssetDatabase.CreateAsset(asset, path);
            }

            var s = heightmapMeta.Settings;
            string crs = "EPSG:3765";
            if (selectedBasemaps != null)
            {
                foreach (var bm in selectedBasemaps)
                {
                    string candidate = bm.Type == BasemapType.Ortho
                        ? bm.Metadata?.Settings?.Crs
                        : bm.TiledMetadata?.Settings?.Crs;
                    if (!string.IsNullOrEmpty(candidate)) { crs = candidate; break; }
                }
            }

            asset.datasetId      = "zagreb_dmr";
            asset.datasetName    = "ZG Connect Zagreb DMR";
            asset.version        = "1.0.0";
            asset.crs            = crs;
            asset.tileSizeMeters = s.TileSizeMeters;
            asset.unityOrigin    = new Vector2Int(s.UnityOriginX, s.UnityOriginY);
            asset.minHeight      = s.MinHeight;
            asset.maxHeight      = s.MaxHeight;

            if (asset.tiles == null)
                asset.tiles = new List<CityTileRecord>();
            if (asset.availableBasemaps == null)
                asset.availableBasemaps = new List<BasemapDefinition>();

            // Register any newly selected basemap types
            if (selectedBasemaps != null)
            {
                foreach (var bm in selectedBasemaps)
                {
                    bool alreadyRegistered = asset.availableBasemaps
                        .Exists(d => d.basemapId == bm.BasemapId);
                    if (!alreadyRegistered)
                    {
                        asset.availableBasemaps.Add(new BasemapDefinition
                        {
                            basemapId   = bm.BasemapId,
                            displayName = CapitalizeFirst(bm.BasemapId),
                            type        = bm.Type
                        });
                    }
                }
            }

            return asset;
        }

        private static void UpdateDatasetRecord(
            CityDataset              asset,
            string                   tileId,
            HeightmapTileJson        tile,
            TerrainData              td,
            Texture2D                primaryTex,
            TerrainLayer             primaryLayer,
            GameObject               sceneObj,
            List<BasemapLayerEntry>  layerEntries,
            List<TerrainLayerSet>    layerSets = null)
        {
            CityTileRecord rec = asset.tiles.Find(r => r.tileId == tileId);
            if (rec == null) { rec = new CityTileRecord(); asset.tiles.Add(rec); }

            rec.tileId        = tileId;
            rec.left          = tile.Left;
            rec.bottom        = tile.Bottom;
            rec.right         = tile.Right;
            rec.top           = tile.Top;
            rec.unityPosition = new Vector3(
                tile.UnityPosition.X, tile.UnityPosition.Y, tile.UnityPosition.Z);
            rec.sceneObject   = sceneObj;
            rec.invalidRatio  = tile.InvalidRatio;

            // Register TerrainData as Addressable (preferred) or fall back to direct ref
            string tdAssetPath = AssetDatabase.GetAssetPath(td);
            AssetReferenceT<TerrainData> addrRef = RegisterTerrainAsAddressable(tdAssetPath, tileId);
            if (addrRef != null)
            {
                rec.terrainDataRef = addrRef;
                rec.terrainData    = null;   // clear legacy ref — was the source of the freeze

                // Clear redundant texture/layer refs too (embedded in TerrainData.terrainLayers)
                rec.primaryBasemapTexture = null;
                rec.primaryTerrainLayer   = null;
            }
            else
            {
                // Addressables not yet set up — use legacy path
                rec.terrainData = td;
                if (primaryTex   != null) rec.primaryBasemapTexture = primaryTex;
                if (primaryLayer != null) rec.primaryTerrainLayer   = primaryLayer;
            }

            // Merge ortho layer entries
            if (rec.basemapLayers == null)
                rec.basemapLayers = new List<BasemapLayerEntry>();

            foreach (var entry in layerEntries)
            {
                // When Addressables are active, null texture/terrainLayer refs (avoid eager loading).
                // The basemapId string is kept for basemap switching identification.
                var merged = new BasemapLayerEntry
                {
                    basemapId    = entry.basemapId,
                    texture      = addrRef != null ? null : entry.texture,
                    terrainLayer = addrRef != null ? null : entry.terrainLayer
                };
                int idx = rec.basemapLayers.FindIndex(e => e.basemapId == entry.basemapId);
                if (idx >= 0) rec.basemapLayers[idx] = merged;
                else          rec.basemapLayers.Add(merged);
            }

            // Merge tiled layer sets
            if (layerSets != null && layerSets.Count > 0)
            {
                if (rec.basemapLayerSets == null)
                    rec.basemapLayerSets = new List<TerrainLayerSet>();

                foreach (var set in layerSets)
                {
                    if (set == null) continue;
                    int idx = rec.basemapLayerSets.FindIndex(s => s != null && s.basemapId == set.basemapId);
                    if (idx >= 0) rec.basemapLayerSets[idx] = set;
                    else          rec.basemapLayerSets.Add(set);
                }
            }
        }

        // ── Tile coordinate helpers ────────────────────────────────────────────────

        /// <summary>
        /// Parses a tileId string like "459000_5070000" into EPSG:3765 bounding-box
        /// coordinates. Returns false if the string cannot be parsed.
        /// </summary>
        private static bool TryParseTileCoords(string tileId, int tileSize,
            out int left, out int bottom, out int right, out int top)
        {
            left = bottom = right = top = 0;
            string[] parts = tileId.Split('_');
            if (parts.Length != 2) return false;
            if (!int.TryParse(parts[0], out left) ||
                !int.TryParse(parts[1], out bottom)) return false;
            right = left   + tileSize;
            top   = bottom + tileSize;
            return true;
        }

        // ── Region filter helper ───────────────────────────────────────────────────

        /// <summary>
        /// Returns true if the tile's bounding box overlaps with the import region.
        /// Uses standard AABB intersection: two rectangles overlap if neither is
        /// entirely to one side of the other.
        /// </summary>
        private static bool TileIntersectsRegion(HeightmapTileJson tile, ZGConnectImportSettings s) =>
            tile.Left   < s.RegionMaxE &&
            tile.Right  > s.RegionMinE &&
            tile.Bottom < s.RegionMaxN &&
            tile.Top    > s.RegionMinN;

        // ── Addressables helper ────────────────────────────────────────────────────

        /// <summary>
        /// Marks the TerrainData asset at <paramref name="tdAssetPath"/> as Addressable
        /// using the same group and address scheme as <see cref="ZGConnectAddressablesMigrator"/>,
        /// and returns an <see cref="AssetReferenceT{TerrainData}"/> pointing to it.
        /// Returns null if the Addressable system has not been initialised yet.
        /// </summary>
        private static AssetReferenceT<TerrainData> RegisterTerrainAsAddressable(
            string tdAssetPath, string tileId)
        {
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
                return null;   // Addressables not set up — caller falls back to legacy ref

            // Delegate group creation/repair entirely to the migrator helper
            // so schema setup stays in one place and doesn't duplicate types.
            AddressableAssetGroup group =
                ZGConnectAddressablesMigrator.GetOrCreateGroup(settings);

            string guid  = AssetDatabase.AssetPathToGUID(tdAssetPath);
            AddressableAssetEntry entry = settings.CreateOrMoveEntry(guid, group, false, false);
            entry.address = ZGConnectAddressablesMigrator.AddressRoot + tileId;

            return new AssetReferenceT<TerrainData>(guid);
        }

        // ── Tiled basemap helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Creates shared TerrainLayer assets (one per layer definition) for a tiled basemap.
        /// Called once per tiled basemap per import session, not per tile.
        /// </summary>
        private static TerrainLayer[] ImportTiledSharedLayers(
            BasemapImportSource bm,
            string              sharedLayerFolder,
            string              texFolder,
            int                 maxTextureSize)
        {
            var layers = new List<TerrainLayer>();

            foreach (LayerDefinitionJson def in bm.TiledMetadata.Layers)
            {
                string layerAssetPath = $"{sharedLayerFolder}/{def.Id}.terrainlayer";

                TerrainLayer existing = AssetDatabase.LoadAssetAtPath<TerrainLayer>(layerAssetPath);
                if (existing != null)
                {
                    layers.Add(existing);
                    continue;
                }

                string srcTex  = Path.Combine(bm.FolderPath, def.TextureFile);
                string texName = Path.GetFileName(def.TextureFile);
                string dstPath = $"{texFolder}/{texName}";
                string dstFull = ZGConnectPathUtils.AssetPathToFullPath(dstPath);

                if (!File.Exists(srcTex))
                {
                    Debug.LogWarning($"[ZGConnect] Tiled layer texture not found: {srcTex}");
                    layers.Add(null);
                    continue;
                }

                if (!File.Exists(dstFull))
                    File.Copy(srcTex, dstFull);

                AssetDatabase.ImportAsset(dstPath);
                ConfigureTiledLayerTextureImport(dstPath, maxTextureSize);
                Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(dstPath);

                if (tex == null)
                {
                    layers.Add(null);
                    continue;
                }

                Texture2D normalTex = TryImportNormalMap(srcTex, texFolder, maxTextureSize);

                TerrainLayer layer = new TerrainLayer
                {
                    diffuseTexture   = tex,
                    normalMapTexture = normalTex,
                    tileSize         = new Vector2(def.TileSizeMeters, def.TileSizeMeters),
                    tileOffset       = Vector2.zero
                };
                AssetDatabase.CreateAsset(layer, layerAssetPath);
                layers.Add(layer);
            }

            // Strip nulls — Unity terrain doesn't accept null layer slots
            layers.RemoveAll(l => l == null);
            return layers.ToArray();
        }

        /// <summary>
        /// Imports per-tile splatmap PNG(s), decodes them into TerrainData alphamaps,
        /// and creates a TerrainLayerSet asset that bundles the shared layers + splatmap refs.
        /// </summary>
        private static TerrainLayerSet ImportTiledSplatmapForTile(
            TerrainData                          td,
            BasemapImportSource                  bm,
            TerrainLayer[]                       sharedLayers,
            string                               tileId,
            string                               layerSetAssetPath,
            string                               splatmapFolder,
            Dictionary<string, SplatmapTileJson> tiledLookup)
        {
            if (!tiledLookup.TryGetValue(tileId, out SplatmapTileJson splatTile))
                return null;

            var splatTextures = new List<Texture2D>();

            foreach (string splatFile in splatTile.Splatmaps)
            {
                string srcPath = Path.Combine(bm.FolderPath, splatFile);
                // Prefix tileId to avoid name collisions from multiple basemaps
                string dstName = $"{tileId}_{Path.GetFileName(splatFile)}";
                string dstPath = $"{splatmapFolder}/{dstName}";
                string dstFull = ZGConnectPathUtils.AssetPathToFullPath(dstPath);

                if (!File.Exists(srcPath))
                {
                    Debug.LogWarning($"[ZGConnect] Splatmap not found: {srcPath}");
                    continue;
                }

                if (!File.Exists(dstFull))
                    File.Copy(srcPath, dstFull);

                AssetDatabase.ImportAsset(dstPath);
                ConfigureSplatmapTextureImport(dstPath);
                Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(dstPath);
                if (tex != null)
                    splatTextures.Add(tex);
            }

            if (splatTextures.Count == 0)
                return null;

            td.terrainLayers = sharedLayers;
            ApplySplatmapAlphas(td, splatTextures, sharedLayers.Length);
            EditorUtility.SetDirty(td);

            TerrainLayerSet set = ScriptableObject.CreateInstance<TerrainLayerSet>();
            set.basemapId = bm.BasemapId;
            set.type      = BasemapType.Tiled;
            set.layers    = sharedLayers;
            set.splatmaps = splatTextures.ToArray();
            AssetDatabase.CreateAsset(set, layerSetAssetPath);

            return set;
        }

        /// <summary>
        /// Decodes RGBA splatmap Texture2D(s) into a Unity alphamap float array and applies it.
        /// Normalises each pixel so weights sum to 1.0.
        /// </summary>
        private static void ApplySplatmapAlphas(TerrainData td, List<Texture2D> splatmaps, int layerCount)
        {
            int w = td.alphamapWidth;
            int h = td.alphamapHeight;
            float[,,] alphas = new float[h, w, layerCount];

            for (int si = 0; si < splatmaps.Count; si++)
            {
                Texture2D splat = splatmaps[si];
                if (splat == null) continue;

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        Color c = splat.GetPixelBilinear((float)x / Mathf.Max(w - 1, 1),
                                                          (float)y / Mathf.Max(h - 1, 1));
                        int baseIdx = si * 4;
                        if (baseIdx + 0 < layerCount) alphas[y, x, baseIdx + 0] = c.r;
                        if (baseIdx + 1 < layerCount) alphas[y, x, baseIdx + 1] = c.g;
                        if (baseIdx + 2 < layerCount) alphas[y, x, baseIdx + 2] = c.b;
                        if (baseIdx + 3 < layerCount) alphas[y, x, baseIdx + 3] = c.a;
                    }
                }
            }

            // Normalise so each pixel's weights sum to 1
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float sum = 0f;
                    for (int l = 0; l < layerCount; l++) sum += alphas[y, x, l];
                    if (sum > 0.001f)
                        for (int l = 0; l < layerCount; l++) alphas[y, x, l] /= sum;
                    else
                        alphas[y, x, 0] = 1f;  // fallback to layer 0 where no data
                }
            }

            td.SetAlphamaps(0, 0, alphas);
        }

        /// <summary>
        /// Two-phase splatmap import settings.
        /// isReadable=true and Uncompressed are required so GetPixelBilinear returns
        /// accurate linear float values during the import pass.
        /// After alphamaps are baked into TerrainData, call ConfigureSplatmapTextureRuntime
        /// to compress and mark non-readable, reducing VRAM by 4–16× per tile.
        /// </summary>
        private static void ConfigureSplatmapTextureImport(string assetPath)
        {
            TextureImporter imp = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (imp == null) return;
            imp.textureType        = TextureImporterType.Default;
            imp.sRGBTexture        = false;    // Linear — critical for correct weight values
            imp.mipmapEnabled      = false;
            imp.wrapMode           = TextureWrapMode.Clamp;
            imp.isReadable         = true;     // required for GetPixelBilinear during this import pass
            imp.textureCompression = TextureImporterCompression.Uncompressed;
            imp.maxTextureSize     = 4096;
            imp.SaveAndReimport();
        }

        /// <summary>
        /// Called after alphamaps have been baked into TerrainData.
        /// Compresses splatmaps and removes CPU readback — cuts their VRAM footprint by 4–16×.
        /// The splatmap PNG assets are only needed again if BasemapController re-applies them
        /// (basemap switching); BasemapController calls GetPixelBilinear which requires isReadable,
        /// so only call this when basemap switching is not needed at runtime.
        /// </summary>
        public static void CompressSplatmapsInFolder(string splatmapAssetFolder)
        {
            string[] guids = UnityEditor.AssetDatabase.FindAssets("t:Texture2D", new[] { splatmapAssetFolder });
            int count = 0;
            foreach (string guid in guids)
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                TextureImporter imp = UnityEditor.AssetImporter.GetAtPath(path) as TextureImporter;
                if (imp == null) continue;
                if (imp.isReadable || imp.textureCompression == TextureImporterCompression.Uncompressed)
                {
                    imp.isReadable         = false;
                    imp.textureCompression = TextureImporterCompression.CompressedHQ;
                    imp.SaveAndReimport();
                    count++;
                }
            }
            Debug.Log($"[ZGConnect] Compressed {count} splatmap texture(s) in {splatmapAssetFolder}.");
        }

        private static string CapitalizeFirst(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpper(s[0]) + s.Substring(1);
        }

        // ══════════════════════════════════════════════════════════════════════
        // Buildings import
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Imports per-tile building GLB files produced by the Building Exporter,
        /// creates standalone Unity prefabs with <see cref="BuildingData"/> components
        /// on each individual building, and wires the prefabs into the dataset as
        /// <see cref="CityTileRecord.buildingsLod0Prefab"/>.
        ///
        /// Input folder layout expected:
        ///   buildings_{tileId}.glb   — one per tile
        ///   buildings_{tileId}.json  — companion metadata (optional but recommended)
        /// </summary>
        /// <param name="dataset">Dataset to update with the generated prefab references.</param>
        /// <param name="buildingsFolder">OS path to the folder containing the exported GLB files.</param>
        /// <param name="settings">Import options.</param>
        /// <param name="onProgress">Optional progress callback (0–1, message).</param>
        public static void ImportBuildings(
            CityDataset              dataset,
            string                   buildingsFolder,
            BuildingsImportSettings  settings,
            Action<float, string>    onProgress = null)
        {
            BuildingSurfaceMeshProcessor.ResetDebugSession();

            if (dataset == null)
                throw new ArgumentNullException(nameof(dataset), "[ZGConnect] No CityDataset provided.");

            // Pin asset path — UnloadUnusedAssets must never drop this ScriptableObject mid-import.
            string datasetAssetPath = AssetDatabase.GetAssetPath(dataset);

            if (!Directory.Exists(buildingsFolder))
                throw new DirectoryNotFoundException(
                    $"[ZGConnect] Buildings folder not found: {buildingsFolder}");

            string[] glbFiles = Directory.GetFiles(buildingsFolder, "buildings_*.glb");
            if (glbFiles.Length == 0)
            {
                EditorUtility.DisplayDialog(
                    "ZG Connect — Buildings Import",
                    $"No buildings_*.glb files found in:\n{buildingsFolder}",
                    "OK");
                return;
            }

            string buildingMeshFolder = $"{settings.OutputFolder}/Buildings";
            string prefabAssetFolder  = $"{settings.OutputFolder}/Prefabs/Buildings";
            ZGConnectPathUtils.EnsureAssetFolder(buildingMeshFolder);
            ZGConnectPathUtils.EnsureAssetFolder(prefabAssetFolder);

            // Build O(1) lookup: tileId → CityTileRecord
            var tileLookup = new Dictionary<string, CityTileRecord>(dataset.tiles.Count);
            foreach (var rec in dataset.tiles)
                if (!string.IsNullOrEmpty(rec.tileId))
                    tileLookup[rec.tileId] = rec;

            int total   = glbFiles.Length;
            int skipped = 0;
            int failed  = 0;

            bool skipProcessedGlb = settings.SkipProcessed
                && !IsProcessedBuildingsFolder(buildingsFolder);
            string processedFolderOs = skipProcessedGlb
                ? BuildingSurfacePipeline.GetProcessedFolder(buildingsFolder)
                : null;

            // ── Phase 1 — Collect tiles and copy GLB files ────────────────────────
            // Copies run inside StartAssetEditing so the watcher does not import per file.

            var workItems = new List<(CityTileRecord Rec, string GlbOsPath, string MeshAssetPath, string PrefabPath)>();

            AssetDatabase.DisallowAutoRefresh();
            try
            {
            AssetDatabase.StartAssetEditing();
            try
            {
            for (int i = 0; i < total; i++)
            {
                string glbOsPath = glbFiles[i];
                string baseName  = Path.GetFileNameWithoutExtension(glbOsPath);

                const string kPrefix = "buildings_";
                string tileId = baseName.StartsWith(kPrefix)
                    ? baseName.Substring(kPrefix.Length)
                    : baseName;

                // ── Region filter: apply BEFORE dataset lookup ─────────────────
                // Derive tile bounds directly from the tileId (e.g. "459000_5070000")
                // so that GLBs outside the imported region are skipped silently,
                // not counted as "failed" just because they have no terrain tile.
                if (settings.FilterByRegion && TryParseTileCoords(tileId, dataset.tileSizeMeters,
                    out int tLeft, out int tBottom, out int tRight, out int tTop))
                {
                    if (!(tLeft < settings.RegionMaxE && tRight  > settings.RegionMinE &&
                          tBottom < settings.RegionMaxN && tTop  > settings.RegionMinN))
                    {
                        skipped++;
                        continue;
                    }
                }

                if (!tileLookup.TryGetValue(tileId, out CityTileRecord rec))
                {
                    // Only warn if we're NOT filtering by region, or if the tile
                    // genuinely falls inside the region but has no terrain.
                    Debug.LogWarning(
                        $"[ZGConnect] Buildings import: no dataset tile for '{tileId}' — skipping.");
                    failed++;
                    continue;
                }

                string xFolder       = $"{buildingMeshFolder}/{rec.left}";
                ZGConnectPathUtils.EnsureAssetFolder(xFolder);
                string meshAssetPath = $"{xFolder}/buildings_{tileId}.glb";
                string prefabPath    = $"{prefabAssetFolder}/TileBuildings_{tileId}.prefab";

                if (settings.SkipExisting &&
                    rec.buildingsLod0Prefab != null &&
                    AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null)
                {
                    skipped++;
                    continue;
                }

                if (skipProcessedGlb)
                {
                    string processedGlb = Path.Combine(processedFolderOs, $"buildings_{tileId}.glb");
                    if (File.Exists(processedGlb))
                    {
                        skipped++;
                        continue;
                    }
                }

                string dstFullPath = ZGConnectPathUtils.AssetPathToFullPath(meshAssetPath);
                try
                {
                    if (!File.Exists(dstFullPath))
                        File.Copy(glbOsPath, dstFullPath);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ZGConnect] Could not copy GLB '{glbOsPath}': {ex.Message}");
                    failed++;
                    continue;
                }

                workItems.Add((rec, glbOsPath, meshAssetPath, prefabPath));
            }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            if (workItems.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "ZG Connect — Buildings Import Complete",
                    $"Imported:  0\nSkipped:   {skipped}\n" +
                    (failed > 0 ? $"Failed:    {failed}  (see Console)\n" : ""),
                    "OK");
                return;
            }

            // ── Phase 2 — One refresh imports all new GLB files ───────────────────
            EditorUtility.DisplayProgressBar(
                "ZG Connect — Buildings Import",
                $"Importing {workItems.Count} GLB file(s)…",
                0.35f);

            AssetDatabase.Refresh();

            if (settings.ProcessBuildingSurfaces || settings.ProcessRoofOrthophotoUv)
            {
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (var item in workItems)
                        ConfigureBuildingGlbImport(item.MeshAssetPath);
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }
            }

            // ── Phase 3 — Build and save each tile prefab ─────────────────────────
            // SaveAsPrefabAsset must run outside StartAssetEditing (returns null inside it).
            // Ephemeral surface meshes also require an active asset pipeline for AddObjectToAsset.

            int workCount = workItems.Count;
            var savedPrefabs = new List<(string TileId, string PrefabPath)>(workCount);

            for (int i = 0; i < workCount; i++)
            {
                var item = workItems[i];

                bool cancelled = EditorUtility.DisplayCancelableProgressBar(
                    "ZG Connect — Buildings Import",
                    $"Building prefab: Tile {item.Rec.tileId}   ({i + 1} / {workCount})",
                    0.35f + 0.65f * ((float)(i + 1) / workCount));

                if (cancelled) break;

                GameObject instance = null;
                try
                {
                    BuildingsMetadataJson meta = null;
                    string jsonOsPath = Path.ChangeExtension(item.GlbOsPath, ".json");
                    if (File.Exists(jsonOsPath))
                        meta = LoadBuildingsJson(jsonOsPath);

                    instance = BuildTileBuildingsInstance(
                        item.MeshAssetPath, item.Rec, dataset, meta, settings,
                        null, buildingsFolder);
                    if (instance == null)
                    {
                        failed++;
                        continue;
                    }

                    if (SaveTileBuildingsPrefab(instance, item.PrefabPath, deferAssetSave: true))
                        savedPrefabs.Add((item.Rec.tileId, item.PrefabPath));
                    else
                        failed++;
                }
                catch (Exception ex)
                {
                    Debug.LogError(
                        $"[ZGConnect] Buildings import failed for tile '{item.Rec.tileId}': {ex.Message}\n{ex.StackTrace}");
                    failed++;
                }
                finally
                {
                    if (instance != null)
                        UnityEngine.Object.DestroyImmediate(instance);
                }
            }

            EditorUtility.ClearProgressBar();

            // ── Phase 3b — Load stable prefab references ──────────────────────────
            CityDataset datasetToSave = ReloadDatasetIfNeeded(dataset, datasetAssetPath);
            if (datasetToSave == null)
            {
                Debug.LogError(
                    "[ZGConnect] Buildings import: CityDataset asset was unloaded during import. " +
                    "Prefab files were written but dataset was not updated.");
            }
            else
            {
                var freshTileLookup = new Dictionary<string, CityTileRecord>(datasetToSave.tiles.Count);
                foreach (CityTileRecord tile in datasetToSave.tiles)
                    if (!string.IsNullOrEmpty(tile.tileId))
                        freshTileLookup[tile.tileId] = tile;

                int imported = 0;
                foreach (var (tileId, prefabPath) in savedPrefabs)
                {
                    GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                    if (prefab == null || !freshTileLookup.TryGetValue(tileId, out CityTileRecord rec))
                    {
                        failed++;
                        continue;
                    }

                    rec.buildingsLod0Prefab = prefab;
                    imported++;
                }

                Debug.Log($"[ZGConnect] Buildings import done. " +
                          $"Imported: {imported}, Skipped: {skipped}, Failed: {failed}" +
                          (!ShouldProcessSurfaces(settings, null, buildingsFolder)
                           && !ShouldProcessRoofOrthophoto(settings, null, buildingsFolder)
                               ? " (building surface processing skipped)" : ""));

                EditorUtility.SetDirty(datasetToSave);
                AssetDatabase.SaveAssets();

                EditorUtility.DisplayDialog(
                    "ZG Connect — Buildings Import Complete",
                    $"Imported:  {imported}\n" +
                    $"Skipped:   {skipped}\n" +
                    (failed > 0 ? $"Failed:    {failed}  (see Console)\n" : "") +
                    (!ShouldProcessSurfaces(settings, null, buildingsFolder)
                     && !ShouldProcessRoofOrthophoto(settings, null, buildingsFolder)
                         ? "\nSurface processing was skipped (fast import).\n" +
                           "Re-run: ZG Connect → Buildings → Reprocess Surfaces On Selected Prefabs\n"
                         : "") +
                    $"\nPrefabs saved to:\n{prefabAssetFolder}",
                    "OK");
                return;
            }

            AssetDatabase.SaveAssets();

            EditorUtility.DisplayDialog(
                "ZG Connect — Buildings Import Complete",
                $"Imported:  0\n" +
                $"Skipped:   {skipped}\n" +
                (failed > 0 ? $"Failed:    {failed}  (see Console)\n" : "") +
                "\nCityDataset asset was unloaded during import.\n" +
                "Prefab files were written but dataset tile references were not updated.\n" +
                "Re-run import or assign prefabs manually.\n" +
                $"\nPrefabs saved to:\n{prefabAssetFolder}",
                "OK");
            }
            finally
            {
                AssetDatabase.AllowAutoRefresh();
            }
        }

        /// <summary>
        /// Bakes facade/roof UVs into GLB files under
        /// <c>{rawBuildingsFolder}/Processed/</c>. Raw GLBs in the parent folder are unchanged.
        /// </summary>
        public static void BakeProcessedBuildings(
            CityDataset            dataset,
            string                 rawBuildingsFolder,
            BuildingsBakeSettings  bakeSettings,
            Action<float, string>  onProgress = null)
        {
            BuildingSurfaceMeshProcessor.ResetDebugSession();

            if (dataset == null)
                throw new ArgumentNullException(nameof(dataset));

            if (!Directory.Exists(rawBuildingsFolder))
                throw new DirectoryNotFoundException(
                    $"[ZGConnect] Buildings folder not found: {rawBuildingsFolder}");

            if (bakeSettings?.SurfaceSettings == null)
            {
                EditorUtility.DisplayDialog(
                    "ZG Connect — Bake Processed GLBs",
                    "Assign BuildingSurfaceSettings first.\n\n" +
                    "ZG Connect → Buildings → Create Default Building Surface Settings",
                    "OK");
                return;
            }

            if (!ZGConnectGlbExportUtility.IsAvailable)
            {
                EditorUtility.DisplayDialog(
                    "ZG Connect — Bake Processed GLBs",
                    "UnityGLTF is not installed.\n\n" +
                    "Package Manager → Add by name → com.khronos.unitygltf",
                    "OK");
                return;
            }

            string processedFolder = BuildingSurfacePipeline.GetProcessedFolder(rawBuildingsFolder);
            Directory.CreateDirectory(processedFolder);

            string[] glbFiles = Directory.GetFiles(rawBuildingsFolder, "buildings_*.glb");
            if (glbFiles.Length == 0)
            {
                EditorUtility.DisplayDialog(
                    "ZG Connect — Bake Processed GLBs",
                    $"No buildings_*.glb files in:\n{rawBuildingsFolder}",
                    "OK");
                return;
            }

            string stagingRoot = $"{bakeSettings.OutputFolder}/Buildings/_BakeStaging";
            ZGConnectPathUtils.EnsureAssetFolder(stagingRoot);

            var tileLookup = new Dictionary<string, CityTileRecord>(dataset.tiles.Count);
            foreach (var rec in dataset.tiles)
                if (!string.IsNullOrEmpty(rec.tileId))
                    tileLookup[rec.tileId] = rec;

            bool bakeRoofOrtho = bakeSettings.ProcessRoofOrthophotoUv;
            var importSettings = new BuildingsImportSettings
            {
                OutputFolder            = bakeSettings.OutputFolder,
                AttachBuildingData      = true,
                FastImport              = false,
                ProcessBuildingSurfaces = !bakeRoofOrtho && bakeSettings.ProcessBuildingSurfaces,
                ProcessRoofOrthophotoUv = bakeRoofOrtho,
                RoofOrthophotoBasemapId = bakeSettings.RoofOrthophotoBasemapId,
                RoofOrthophotoSource    = bakeSettings.RoofOrthophotoSource,
                BasemapMaxTextureSize   = bakeSettings.BasemapMaxTextureSize,
                SurfaceSettings         = bakeSettings.SurfaceSettings,
                FilterByRegion          = bakeSettings.FilterByRegion,
                RegionMinE              = bakeSettings.RegionMinE,
                RegionMaxE              = bakeSettings.RegionMaxE,
                RegionMinN              = bakeSettings.RegionMinN,
                RegionMaxN              = bakeSettings.RegionMaxN,
            };

            int baked = 0, skipped = 0, failed = 0;
            int total = glbFiles.Length;

            for (int i = 0; i < total; i++)
            {
                string glbOsPath = glbFiles[i];
                string baseName  = Path.GetFileNameWithoutExtension(glbOsPath);

                const string kPrefix = "buildings_";
                string tileId = baseName.StartsWith(kPrefix)
                    ? baseName.Substring(kPrefix.Length)
                    : baseName;

                string processedGlb = Path.Combine(processedFolder, $"buildings_{tileId}.glb");
                if (bakeSettings.SkipExisting && File.Exists(processedGlb))
                {
                    skipped++;
                    continue;
                }

                if (bakeSettings.FilterByRegion && TryParseTileCoords(tileId, dataset.tileSizeMeters,
                    out int tLeft, out int tBottom, out int tRight, out int tTop))
                {
                    if (!(tLeft < bakeSettings.RegionMaxE && tRight > bakeSettings.RegionMinE &&
                          tBottom < bakeSettings.RegionMaxN && tTop > bakeSettings.RegionMinN))
                    {
                        skipped++;
                        continue;
                    }
                }

                if (!tileLookup.TryGetValue(tileId, out CityTileRecord rec))
                {
                    skipped++;
                    continue;
                }

                bool cancelled = EditorUtility.DisplayCancelableProgressBar(
                    "ZG Connect — Bake Processed GLBs",
                    $"Tile {tileId}  ({i + 1} / {total})",
                    (float)(i + 1) / total);
                if (cancelled) break;

                string stagingFolder = $"{stagingRoot}/{tileId}";
                ZGConnectPathUtils.EnsureAssetFolder(stagingFolder);
                string stagingAssetPath = $"{stagingFolder}/buildings_{tileId}.glb";
                string stagingFullPath  = ZGConnectPathUtils.AssetPathToFullPath(stagingAssetPath);

                GameObject instance = null;
                var slotSnapshot = new Dictionary<string, BuildingEntryJson>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    File.Copy(glbOsPath, stagingFullPath, overwrite: true);
                    AssetDatabase.ImportAsset(stagingAssetPath, ImportAssetOptions.ForceUpdate);
                    ConfigureBuildingGlbImport(stagingAssetPath);

                    BuildingsMetadataJson meta = null;
                    string jsonOsPath = Path.ChangeExtension(glbOsPath, ".json");
                    if (File.Exists(jsonOsPath))
                        meta = LoadBuildingsJson(jsonOsPath);

                    instance = BuildTileBuildingsInstance(
                        stagingAssetPath, rec, dataset, meta, importSettings, slotSnapshot,
                        rawBuildingsFolder);
                    if (instance == null)
                    {
                        failed++;
                        continue;
                    }

                    int meshCount = ZGConnectGlbExportUtility.CountExportableMeshes(instance);
                    if (meshCount == 0)
                    {
                        Debug.LogError(
                            $"[ZGConnect] Bake skipped tile '{tileId}': no mesh geometry after surface processing. " +
                            "Check GLB import (Read/Write) and BuildingSurfaceSettings.");
                        failed++;
                        continue;
                    }

                    PrepareTileRootForProcessedExport(instance.transform);
                    ZGConnectGlbExportUtility.ExportRootGeometryOnly(instance, processedGlb);

                    string processedJson = Path.ChangeExtension(processedGlb, ".json");
                    WriteBakedMetadataJson(processedJson, meta, rec, instance, slotSnapshot, importSettings);

                    baked++;
                }
                catch (Exception ex)
                {
                    Debug.LogError(
                        $"[ZGConnect] Bake failed for tile '{tileId}': {ex.Message}\n{ex.StackTrace}");
                    failed++;
                }
                finally
                {
                    if (instance != null)
                        UnityEngine.Object.DestroyImmediate(instance);

                    if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(stagingAssetPath) != null)
                        AssetDatabase.DeleteAsset(stagingAssetPath);

                    string stagingTileFolder = stagingFolder;
                    if (AssetDatabase.IsValidFolder(stagingTileFolder)
                        && AssetDatabase.FindAssets("", new[] { stagingTileFolder }).Length == 0)
                    {
                        AssetDatabase.DeleteAsset(stagingTileFolder);
                    }
                }
            }

            EditorUtility.ClearProgressBar();
            AssetDatabase.Refresh();

            Debug.Log($"[ZGConnect] Bake processed GLBs done. Baked: {baked}, Skipped: {skipped}, Failed: {failed}. " +
                      $"Output: {processedFolder}");

            EditorUtility.DisplayDialog(
                "ZG Connect — Bake Complete",
                $"Baked:    {baked}\nSkipped:  {skipped}\n" +
                (failed > 0 ? $"Failed:   {failed}  (see Console)\n" : "") +
                $"\nProcessed GLBs:\n{processedFolder}\n\n" +
                "Enable \"Import from Processed folder\" in the Buildings tab for fast import.",
                "OK");
        }

        public static bool MetadataHasBakedSurfaces(BuildingsMetadataJson meta) =>
            meta != null
            && meta.SurfaceBaked
            && meta.SurfacePipelineVersion >= 1;

        public static bool MetadataUsesMaterialSlotsJson(BuildingsMetadataJson meta)
        {
            if (meta == null || !meta.SurfaceBaked || meta.SurfacePipelineVersion < 2
                || meta.Buildings == null)
                return false;

            foreach (BuildingEntryJson entry in meta.Buildings)
            {
                if (entry.MaterialSlots != null && entry.MaterialSlots.Count > 0)
                    return true;

                if (entry.MeshMaterials == null)
                    continue;

                foreach (BuildingMeshMaterialsJson mesh in entry.MeshMaterials)
                {
                    if (mesh.MaterialSlots != null && mesh.MaterialSlots.Count > 0)
                        return true;
                }
            }

            return false;
        }

        private static bool IsProcessedBuildingsFolder(string buildingsFolderOsPath)
        {
            if (string.IsNullOrEmpty(buildingsFolderOsPath))
                return false;
            string normalized = buildingsFolderOsPath.Replace('\\', '/');
            return normalized.EndsWith("/" + BuildingSurfacePipeline.ProcessedFolderName,
                       StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldProcessSurfaces(
            BuildingsImportSettings settings,
            BuildingsMetadataJson   meta,
            string                  buildingsFolderOsPath)
        {
            if (IsProcessedBuildingsFolder(buildingsFolderOsPath))
                return false;

            if (settings.ProcessRoofOrthophotoUv)
                return false;

            return settings.ProcessBuildingSurfaces
                   && settings.SurfaceSettings != null
                   && !settings.FastImport;
        }

        private static bool ShouldProcessRoofOrthophoto(
            BuildingsImportSettings settings,
            BuildingsMetadataJson   meta,
            string                  buildingsFolderOsPath)
        {
            if (IsProcessedBuildingsFolder(buildingsFolderOsPath))
                return false;

            // Explicit roof-ortho request always remeshes UVs on raw GLBs (independent of Fast Import).
            return settings.ProcessRoofOrthophotoUv
                   && settings.SurfaceSettings != null;
        }

        /// <summary>Roof ortho UV pass needs a material on roof submeshes; falls back if ortho PNG is missing.</summary>
        private static Material ResolveRoofMaterialForProcessing(
            CityTileRecord          rec,
            CityDataset             dataset,
            BuildingsImportSettings settings,
            string                  tileId)
        {
            if (BuildingRoofOrthophotoResolver.TryEnsureTileRoofMaterial(
                    rec, dataset, settings, out Material roofMat))
                return roofMat;

            Material fallback = settings.SurfaceSettings?.GetFlatRoofMaterial(
                BuildingCategory.House, 0);
            if (fallback != null)
            {
                Debug.LogWarning(
                    $"[ZGConnect] Tile '{tileId}': no ortho texture — roof UVs still processed; " +
                    "import terrain ortho or select ortho basemap for runtime roof material.");
                return fallback;
            }

            Debug.LogError(
                $"[ZGConnect] Tile '{tileId}': roof orthophoto UV processing needs a material in SurfaceSettings.");
            return null;
        }

        private static void WriteBakedMetadataJson(
            string                                    jsonPath,
            BuildingsMetadataJson                     source,
            CityTileRecord                            rec,
            GameObject                                instance,
            Dictionary<string, BuildingEntryJson> preProcessSlotSnapshot = null,
            BuildingsImportSettings                   bakeSettings = null)
        {
            BuildingsMetadataJson meta = BuildingTileMetadataWriter.BuildFromTile(
                instance, source, preProcessSlotSnapshot);
            meta.TileId         = rec.tileId;
            meta.TileSizeMeters = rec.right - rec.left;

            if (bakeSettings != null)
            {
                meta.RoofOrthophotoUv      = bakeSettings.ProcessRoofOrthophotoUv;
                meta.RoofOrthophotoBasemapId = bakeSettings.RoofOrthophotoBasemapId;
            }

            if (meta.TileOriginUnity == null)
            {
                Vector3 o = rec.unityPosition;
                meta.TileOriginUnity = new TilePositionJson { X = o.x, Y = o.y, Z = o.z };
            }

            File.WriteAllText(jsonPath, JsonConvert.SerializeObject(meta, Formatting.Indented));
        }

        // ── Buildings helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Copies the GLB to the target asset folder (if not already there), ensures
        /// Unity knows about it, then configures the model importer for static mesh use.
        /// Returns the Unity asset path, or null on failure.
        /// </summary>
        private static string CopyAndImportGlb(string srcOsPath, string outputAssetFolder)
        {
            string fileName     = Path.GetFileName(srcOsPath);
            string dstAssetPath = $"{outputAssetFolder}/{fileName}";
            string dstFullPath  = ZGConnectPathUtils.AssetPathToFullPath(dstAssetPath);

            try
            {
                if (!File.Exists(dstFullPath))
                    File.Copy(srcOsPath, dstFullPath);

                // Make Unity aware of the file before touching the importer
                AssetDatabase.ImportAsset(dstAssetPath, ImportAssetOptions.Default);

                // Reconfigure and reimport (animation, cameras, lights all off).
                // For GLB files imported via gltfast the importer is not a ModelImporter;
                // ConfigureBuildingGlbImport silently skips in that case.
                ConfigureBuildingGlbImport(dstAssetPath);

                return dstAssetPath;
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[ZGConnect] Could not copy/import GLB '{srcOsPath}':\n{ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Applies static-mesh model importer settings suitable for city buildings.
        /// GLB files are already in metres — no scale correction needed.
        /// If the active importer is not a ModelImporter (e.g. gltfast), this is a no-op.
        /// </summary>
        private static void ConfigureBuildingGlbImport(string assetPath)
        {
            ModelImporter imp = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (imp == null) return;

            imp.animationType    = ModelImporterAnimationType.None;
            imp.importAnimation  = false;
            imp.importCameras    = false;
            imp.importLights     = false;
            imp.isReadable       = true;    // required for BuildingSurfaceMeshProcessor at prefab build
            imp.generateSecondaryUV = false;

            imp.SaveAndReimport();
        }

        /// <summary>
        /// Builds an in-memory tile prefab hierarchy. Caller must destroy the instance.
        /// </summary>
        private static GameObject BuildTileBuildingsInstance(
            string                                    meshAssetPath,
            CityTileRecord                            rec,
            CityDataset                               dataset,
            BuildingsMetadataJson                     meta,
            BuildingsImportSettings                   settings,
            Dictionary<string, BuildingEntryJson> preProcessSlotSnapshot = null,
            string                                    buildingsFolderOsPath = null)
        {
            string tileId = rec.tileId;
            GameObject modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(meshAssetPath);
            if (modelAsset == null)
            {
                Debug.LogError($"[ZGConnect] Could not load model asset: {meshAssetPath}");
                return null;
            }

            bool processSurfaces = ShouldProcessSurfaces(settings, meta, buildingsFolderOsPath);
            bool processRoofOrtho = ShouldProcessRoofOrthophoto(settings, meta, buildingsFolderOsPath);

            var buildingLookup = BuildBuildingMetadataLookup(meta);

            // HideInHierarchy only — HideAndDontSave includes DontSaveInEditor which
            // causes SaveAsPrefabAsset to see zero valid objects and return null.
            GameObject instance = (GameObject)UnityEngine.Object.Instantiate(modelAsset);
            instance.hideFlags = HideFlags.HideInHierarchy;
            instance.name      = $"TileBuildings_{tileId}";

            if (PrefabUtility.IsPartOfPrefabInstance(instance))
            {
                PrefabUtility.UnpackPrefabInstance(
                    instance,
                    PrefabUnpackMode.Completely,
                    InteractionMode.AutomatedAction);
            }

            FlattenTileBuildingsWrappers(instance.transform);

            if ((processSurfaces || processRoofOrtho) && preProcessSlotSnapshot != null)
                BuildingMaterialSlotApplier.CaptureTileSlotSnapshot(instance, preProcessSlotSnapshot);

            if (settings.AttachBuildingData)
            {
                int childCount = instance.transform.childCount;
                for (int c = 0; c < childCount; c++)
                {
                    Transform child = instance.transform.GetChild(c);

                    BuildingData bd = child.gameObject.AddComponent<BuildingData>();
                    bd.tileId     = tileId;
                    bd.buildingId = ExtractBuildingId(child.name);

                    if (TryGetBuildingMetadataEntry(child.name, buildingLookup, out BuildingEntryJson jsonEntry))
                        BuildingMetadataApplier.Apply(bd, jsonEntry);
                }
            }

            if (!ShouldSkipGlbOriginCorrection(meta, buildingsFolderOsPath)
                && meta?.TileOriginUnity != null
                && meta.Buildings != null)
            {
                Vector3 correction = ComputeGlbOriginCorrection(
                    instance.transform, buildingLookup, tileId, meta);

                if (correction.sqrMagnitude > 0.01f)
                    instance.transform.position -= correction;
            }

            if (processSurfaces)
            {
                int childCount = instance.transform.childCount;
                for (int c = 0; c < childCount; c++)
                {
                    Transform child = instance.transform.GetChild(c);
                    string buildingId = ExtractBuildingId(child.name);
                    BuildingData bd = child.GetComponent<BuildingData>();
                    if (bd != null && !string.IsNullOrEmpty(bd.buildingId))
                        buildingId = bd.buildingId;

                    BuildingSurfaceMeshProcessor.ProcessBuilding(
                        child.gameObject, settings.SurfaceSettings, buildingId);
                }
            }
            else if (processRoofOrtho)
            {
                Material roofMat = ResolveRoofMaterialForProcessing(rec, dataset, settings, tileId);
                if (roofMat != null)
                {
                    int processed = BuildingRoofOrthophotoProcessor.ProcessTile(
                        instance, settings.SurfaceSettings, roofMat, rec, dataset, meta);
                    if (processed == 0)
                    {
                        Debug.LogError(
                            $"[ZGConnect] Tile '{tileId}': roof orthophoto UV pass processed 0 meshes. " +
                            "Check mesh readability and roof normal thresholds in BuildingSurfaceSettings.");
                    }
                }
            }
            else if (!settings.FastImport
                     && (settings.ProcessBuildingSurfaces || settings.ProcessRoofOrthophotoUv)
                     && settings.SurfaceSettings == null)
            {
                Debug.LogWarning(
                    "[ZGConnect] Building surface processing is enabled but SurfaceSettings is not assigned. " +
                    "Use ZG Connect → Buildings → Create Default Building Surface Settings.");
            }

            if (!processSurfaces && !processRoofOrtho && settings.SurfaceSettings != null)
            {
                if (settings.ProcessRoofOrthophotoUv
                    && !IsProcessedBuildingsFolder(buildingsFolderOsPath))
                {
                    Debug.LogError(
                        $"[ZGConnect] Tile '{tileId}': roof orthophoto UV was enabled but the mesh pass " +
                        "did not run. Assign BuildingSurfaceSettings and re-import.");
                }
                else if (MetadataUsesMaterialSlotsJson(meta))
                {
                    bool useRoofOrtho = meta.RoofOrthophotoUv;
                    BuildingMaterialApplyStyle matStyle = useRoofOrtho
                        ? BuildingMaterialApplyStyle.RoofOrthophoto
                        : BuildingMaterialApplyStyle.FacadeAndRoofVariants;

                    string basemapId = !string.IsNullOrEmpty(meta.RoofOrthophotoBasemapId)
                        ? meta.RoofOrthophotoBasemapId
                        : settings.RoofOrthophotoBasemapId;

                    Material roofTemplate = settings.SurfaceSettings.GetFlatRoofMaterial(
                        BuildingCategory.House, 0);

                    Dictionary<string, Material> roofMaterialCache = null;
                    if (useRoofOrtho)
                    {
                        roofMaterialCache = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
                        if (BuildingRoofOrthophotoResolver.TryEnsureTileRoofMaterial(
                                rec, dataset, settings, basemapId, out Material roofMat))
                        {
                            roofMaterialCache[rec.tileId] = roofMat;
                        }
                        else
                        {
                            Debug.LogWarning(
                                $"[ZGConnect] Tile '{tileId}': could not resolve ortho material for processed import " +
                                $"(basemap '{basemapId}'). Import terrain ortho or select ortho basemap on Terrain tab.");
                        }
                    }

                    BuildingMaterialSlotApplier.ApplyTileFromMetadata(
                        instance,
                        meta,
                        rec,
                        settings.SurfaceSettings,
                        matStyle,
                        basemapId,
                        roofTemplate,
                        roofMaterialCache);
                }
                else if (MetadataHasBakedSurfaces(meta))
                {
                    int childCount = instance.transform.childCount;
                    for (int c = 0; c < childCount; c++)
                    {
                        Transform child = instance.transform.GetChild(c);
                        string buildingId = ExtractBuildingId(child.name);
                        BuildingData bd = child.GetComponent<BuildingData>();
                        if (bd != null && !string.IsNullOrEmpty(bd.buildingId))
                            buildingId = bd.buildingId;

                        BuildingSurfaceMeshProcessor.AssignBuildingMaterials(
                            child.gameObject, settings.SurfaceSettings, buildingId);
                    }
                }
            }

            NormalizeBuildingChildNames(instance.transform, preProcessSlotSnapshot);

            return instance;
        }

        /// <summary>
        /// Renames each direct building child to its numeric id (e.g. zagreb_Part_49684 → 49684).
        /// Run after metadata/name-based processing; re-keys <paramref name="slotSnapshot"/> when present.
        /// </summary>
        private static void NormalizeBuildingChildNames(
            Transform root,
            Dictionary<string, BuildingEntryJson> slotSnapshot = null)
        {
            int childCount = root.childCount;
            for (int c = 0; c < childCount; c++)
            {
                Transform child = root.GetChild(c);
                string id = ExtractBuildingId(child.name);
                if (string.IsNullOrEmpty(id) || id == child.name)
                    continue;

                if (slotSnapshot != null
                    && slotSnapshot.TryGetValue(child.name, out BuildingEntryJson entry))
                {
                    slotSnapshot.Remove(child.name);
                    slotSnapshot[id] = entry;
                }

                child.name = id;
            }
        }

        /// <summary>
        /// Embeds procedural meshes and writes the prefab asset.
        /// Must not be called inside <see cref="AssetDatabase.StartAssetEditing"/>.
        /// </summary>
        private static bool SaveTileBuildingsPrefab(
            GameObject instance,
            string     prefabPath,
            bool       deferAssetSave = false)
        {
            if (instance == null)
                return false;

            string folder = Path.GetDirectoryName(prefabPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(folder))
                ZGConnectPathUtils.EnsureAssetFolder(folder);

            HideFlags prevFlags = instance.hideFlags;
            instance.hideFlags = HideFlags.None;

            try
            {
                // 1) Create the prefab asset first — AddObjectToAsset requires a tracked parent.
                PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);

                // 2) Embed procedural surface meshes as sub-assets, then save again.
                if (BuildingSurfaceMeshProcessor.PersistEphemeralMeshes(instance, prefabPath)
                    && !deferAssetSave)
                    AssetDatabase.SaveAssets();

                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
                if (prefab != null)
                    return true;

                string fullPath = ZGConnectPathUtils.AssetPathToFullPath(prefabPath);
                if (File.Exists(fullPath))
                {
                    Debug.LogWarning(
                        $"[ZGConnect] SaveAsPrefabAsset returned null but prefab file exists: {prefabPath}");
                    return true;
                }

                Debug.LogError(
                    $"[ZGConnect] SaveAsPrefabAsset returned null: {prefabPath}\n" +
                    $"  children={instance.transform.childCount}");
                return false;
            }
            finally
            {
                instance.hideFlags = prevFlags;
            }
        }

        /// <summary>
        /// Parses a buildings companion JSON file. Returns null and logs a warning on failure.
        /// </summary>
        private static BuildingsMetadataJson LoadBuildingsJson(string jsonOsPath)
        {
            try
            {
                return JsonConvert.DeserializeObject<BuildingsMetadataJson>(
                    File.ReadAllText(jsonOsPath));
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[ZGConnect] Could not parse buildings JSON '{jsonOsPath}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// GLB re-import often keeps an inner <c>TileBuildings_*</c> group under the file root.
        /// After renaming the instance root to <c>TileBuildings_{tileId}</c>, that wrapper becomes
        /// a redundant direct child and is mistaken for a single building. Hoist its children to
        /// the prefab root and remove empty group nodes.
        /// </summary>
        private static void FlattenTileBuildingsWrappers(Transform root)
        {
            while (root.childCount == 1)
            {
                Transform wrapper = root.GetChild(0);
                if (!IsTileBuildingsGroupNode(wrapper))
                    break;

                while (wrapper.childCount > 0)
                    wrapper.GetChild(0).SetParent(root, true);

                UnityEngine.Object.DestroyImmediate(wrapper.gameObject);
            }
        }

        private static bool IsTileBuildingsGroupNode(Transform node)
        {
            if (node == null || !node.name.StartsWith("TileBuildings_", StringComparison.Ordinal))
                return false;

            if (node.GetComponent<MeshFilter>() != null ||
                node.GetComponent<MeshRenderer>() != null ||
                node.GetComponent<SkinnedMeshRenderer>() != null)
                return false;

            return node.childCount > 0;
        }

        /// <summary>
        /// Extracts the numeric building ID from a Unity GameObject name.
        /// E.g. "zagreb_Part_1619" → "1619", "zagreb_Part_1619 1" → "1619".
        /// Returns the cleaned name as a fallback if no trailing numeric segment is found.
        /// </summary>
        private static string ExtractBuildingId(string objectName) =>
            BuildingSurfaceUtility.ExtractBuildingIdFromObjectName(objectName);

        /// <summary>
        /// Computes the XZ correction vector needed to shift GLB node positions from the
        /// origin frame used when the GLB was generated to the origin frame of the current
        /// terrain import.
        ///
        /// Two strategies, in priority order:
        ///
        ///   1. Name matching (accurate) — searches all descendants for a node whose name
        ///      matches a JSON entry, then derives the correction from the difference between
        ///      the GLB world position and the JSON position. Handles any number of intermediate
        ///      group nodes between the root and the actual building meshes.
        ///
        ///   2. Tile-ID fallback (reliable) — derives the current origin from the tileId string
        ///      and the JSON tileOriginUnity, then subtracts the known GLB pipeline origin
        ///      (default 442000 / 5051000 from patch_buildings_origin.py). Used when no name
        ///      matches are found (e.g. the GLB was exported from a tool that renamed nodes).
        /// </summary>
        /// <summary>
        /// Processed/baked GLBs already include the world-origin shift; re-applying GLB pipeline
        /// correction (~442000 / 5051000) would offset tiles by ~17k / ~24k meters.
        /// </summary>
        private static bool ShouldSkipGlbOriginCorrection(
            BuildingsMetadataJson meta,
            string                buildingsFolderOsPath)
        {
            if (IsProcessedBuildingsFolder(buildingsFolderOsPath))
                return true;

            return meta != null && meta.SurfaceBaked;
        }

        private static Dictionary<string, BuildingEntryJson> BuildBuildingMetadataLookup(
            BuildingsMetadataJson meta)
        {
            var lookup = new Dictionary<string, BuildingEntryJson>(StringComparer.OrdinalIgnoreCase);
            if (meta?.Buildings == null)
                return lookup;

            foreach (BuildingEntryJson entry in meta.Buildings)
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                if (!lookup.ContainsKey(entry.Name))
                    lookup[entry.Name] = entry;

                string id = BuildingSurfaceUtility.ExtractBuildingIdFromObjectName(entry.Name);
                if (!string.IsNullOrEmpty(id) && !lookup.ContainsKey(id))
                    lookup[id] = entry;
            }

            return lookup;
        }

        /// <summary>
        /// Zeros the tile root while keeping building world transforms (for portable processed GLBs).
        /// </summary>
        private static void PrepareTileRootForProcessedExport(Transform tileRoot)
        {
            if (tileRoot == null || tileRoot.childCount == 0)
                return;

            int childCount = tileRoot.childCount;
            var worldPositions = new Vector3[childCount];
            var worldRotations = new Quaternion[childCount];
            var localScales = new Vector3[childCount];

            for (int c = 0; c < childCount; c++)
            {
                Transform child = tileRoot.GetChild(c);
                worldPositions[c] = child.position;
                worldRotations[c] = child.rotation;
                localScales[c] = child.localScale;
            }

            tileRoot.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            for (int c = 0; c < childCount; c++)
            {
                Transform child = tileRoot.GetChild(c);
                child.SetPositionAndRotation(worldPositions[c], worldRotations[c]);
                child.localScale = localScales[c];
            }
        }

        private static Vector3 ComputeGlbOriginCorrection(
            Transform                                root,
            Dictionary<string, BuildingEntryJson>    buildingLookup,
            string                                   tileId,
            BuildingsMetadataJson                    meta)
        {
            // ── Strategy 1: find any matching descendant and measure the offset ─────
            var stack = new Stack<Transform>();
            for (int i = 0; i < root.childCount; i++)
                stack.Push(root.GetChild(i));

            while (stack.Count > 0)
            {
                Transform t = stack.Pop();
                if (TryGetBuildingMetadataEntry(t.name, buildingLookup, out BuildingEntryJson entry)
                    && entry.LocalPosition != null)
                {
                    // GLB world pos minus JSON new-origin pos = the correction to subtract
                    return new Vector3(
                        t.position.x - entry.LocalPosition.X,
                        0f,
                        t.position.z - entry.LocalPosition.Z);
                }
                for (int i = 0; i < t.childCount; i++)
                    stack.Push(t.GetChild(i));
            }

            // ── Strategy 2: derive from tileId + JSON tileOriginUnity ────────────────
            // new_ox = tileLeft  - meta.TileOriginUnity.X
            // new_oy = tileBottom - meta.TileOriginUnity.Z
            // correction = (new_ox - glb_ox,  0,  new_oy - glb_oy)
            // where glb_ox / glb_oy are the defaults used by patch_buildings_origin.py
            const float GlbOriginX = 442000f;
            const float GlbOriginY = 5051000f;

            string[] parts = tileId.Split('_');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out int tileLeft) &&
                int.TryParse(parts[1], out int tileBottom))
            {
                float newOx = tileLeft  - meta.TileOriginUnity.X;
                float newOy = tileBottom - meta.TileOriginUnity.Z;
                return new Vector3(newOx - GlbOriginX, 0f, newOy - GlbOriginY);
            }

            return Vector3.zero;
        }

        private static CityDataset ReloadDatasetIfNeeded(CityDataset dataset, string assetPath)
        {
            if (dataset != null)
                return dataset;

            if (string.IsNullOrEmpty(assetPath))
                return null;

            return AssetDatabase.LoadAssetAtPath<CityDataset>(assetPath);
        }

        private static bool TryGetBuildingMetadataEntry(
            string objectName,
            Dictionary<string, BuildingEntryJson> lookup,
            out BuildingEntryJson entry)
        {
            entry = null;
            if (lookup == null || string.IsNullOrEmpty(objectName))
                return false;

            if (lookup.TryGetValue(objectName, out entry))
                return true;

            string id = BuildingSurfaceUtility.ExtractBuildingIdFromObjectName(objectName);
            return !string.IsNullOrEmpty(id) && lookup.TryGetValue(id, out entry);
        }
    }
}
