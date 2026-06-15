using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace ZGConnect
{
    public enum BasemapType { Ortho, Tiled }

    /// <summary>
    /// Groups all TerrainLayer assets and splatmap textures for one basemap type on one tile.
    /// Shared tiled layers are referenced (not duplicated) across all tiles.
    /// </summary>
    [CreateAssetMenu(menuName = "ZG Connect/Terrain Layer Set")]
    public class TerrainLayerSet : ScriptableObject
    {
        public string       basemapId;
        public BasemapType  type;
        public TerrainLayer[] layers;
        /// <summary>Null for Ortho. Linear Texture2D splatmap(s) for Tiled (up to 2 for 8 layers).</summary>
        public Texture2D[]  splatmaps;
    }

    [CreateAssetMenu(menuName = "ZG Connect/City Dataset")]
    public class CityDataset : ScriptableObject
    {
        public string datasetId;
        public string datasetName;
        public string version;
        public string crs;

        public int tileSizeMeters = 1000;
        public Vector2Int unityOrigin;

        public float minHeight;
        public float maxHeight;

        /// <summary>All basemap types imported for this dataset (e.g. ortho, roadmap, tron).</summary>
        public List<BasemapDefinition> availableBasemaps = new List<BasemapDefinition>();

        public List<CityTileRecord> tiles = new List<CityTileRecord>();
    }

    /// <summary>Dataset-level registry entry for one basemap type.</summary>
    [Serializable]
    public class BasemapDefinition
    {
        public string      basemapId;    // e.g. "ortho", "osm_tiled"
        public string      displayName;
        public BasemapType type;
    }

    /// <summary>Per-tile entry linking a basemap type to its texture and TerrainLayer.</summary>
    [Serializable]
    public class BasemapLayerEntry
    {
        public string       basemapId;
        public Texture2D    texture;
        public TerrainLayer terrainLayer;
    }

    [Serializable]
    public class CityTileRecord
    {
        public string tileId;

        public int left;
        public int bottom;
        public int right;
        public int top;

        public Vector3 unityPosition;

        /// <summary>
        /// Addressable reference to this tile's TerrainData. Populated by the importer
        /// (new tiles) or the "Migrate to Addressables" button (existing tiles).
        /// When valid, the streaming controller loads this asynchronously and releases
        /// it when the tile leaves the unload radius — keeping memory constant.
        /// </summary>
        public AssetReferenceT<TerrainData> terrainDataRef;

        /// <summary>
        /// Legacy direct reference — causes eager loading of ALL tiles on dataset load.
        /// Kept hidden so the migration tool can read the GUID before nulling it out.
        /// Will be null on all tiles after migration runs.
        /// </summary>
        [HideInInspector]
        public TerrainData  terrainData;

        /// <summary>First imported basemap texture (index 0). Null after Addressables migration
        /// (texture is embedded in TerrainData.terrainLayers and loaded transitively).</summary>
        public Texture2D    primaryBasemapTexture;
        /// <summary>First imported basemap layer (index 0). Null after Addressables migration.</summary>
        public TerrainLayer primaryTerrainLayer;

        public GameObject sceneObject;
        public GameObject prefab;

        public float invalidRatio;

        /// <summary>Ortho basemap layers — one entry per ortho basemap type (backward compat).</summary>
        public List<BasemapLayerEntry> basemapLayers = new List<BasemapLayerEntry>();

        /// <summary>Tiled basemap layer sets — one TerrainLayerSet per tiled basemap type.</summary>
        public List<TerrainLayerSet> basemapLayerSets = new List<TerrainLayerSet>();

        /// <summary>
        /// Per-tile building group GameObject in the scene.
        /// Assigned by ZGConnectBuildingAssociatorWindow after manual alignment.
        /// Enabled/disabled alongside sceneObject by TerrainStreamingController.
        /// </summary>
        public GameObject buildingsSceneObject;

        // Future fields — will hold prefab/addressable references when buildings migrate off SetActive
        public GameObject buildingsLod0Prefab;
        public GameObject buildingsLod1Prefab;
        public string addressableKey;

        /// <summary>
        /// RGBA linear texture encoding vegetation placement masks per tile.
        /// R = forest density, G = park/urban trees density, B = grass density,
        /// A = combined exclusion mask (roads + buildings + water).
        /// Must be marked Read/Write Enabled in the texture's import settings.
        /// </summary>
        public Texture2D vegetationMask;
    }
}
