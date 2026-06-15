using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;
using ZGConnect;

namespace ZGConnect.RealtimeStreaming
{
    public enum RealtimeBuildingStyle
    {
        Facade = 0,
        OrthoRoof = 1,
    }

    /// <summary>
    /// How ortho-roof building materials get their roof albedo at runtime.
    /// </summary>
    public enum RealtimeRoofMaterialSource
    {
        /// <summary>Per-tile terrain orthophoto texture (tile-space roof UVs).</summary>
        OrthophotoBasemap = 0,
        /// <summary>One shared material for all roofs — see RealtimeStreamingController.roofOrthophotoMaterialTemplate.</summary>
        SingleMaterial = 1,
    }

    /// <summary>Per-building physics collider generated when a building tile streams in.</summary>
    public enum RealtimeBuildingColliderMode
    {
        None = 0,
        Box = 1,
        ConvexMesh = 2,
        FullMesh = 3,
    }

    public enum BuildingLodStorageMode
    {
        Hierarchy = 0,
        DualFile = 1,
    }

    [Serializable]
    public class StreamingDatasetManifest
    {
        [JsonProperty("version")] public string Version = "1";
        [JsonProperty("unityOrigin")] public int[] UnityOrigin;
        [JsonProperty("minHeight")] public float MinHeight;
        [JsonProperty("maxHeight")] public float MaxHeight;
        [JsonProperty("tileSizeMeters")] public int TileSizeMeters = 1000;
        [JsonProperty("crs")] public string Crs;
        [JsonProperty("buildingLodStorage")] public string BuildingLodStorage = "hierarchy";
        [JsonProperty("availableBasemaps")] public List<StreamingBasemapEntry> AvailableBasemaps = new();
        [JsonProperty("tiles")] public List<StreamingTileEntry> Tiles = new();
        [JsonProperty("supertiles")] public List<StreamingSupertileEntry> Supertiles = new();
        [JsonProperty("packBake")] public StreamingPackBakeManifest PackBake;

        public Vector2Int GetUnityOrigin()
        {
            if (UnityOrigin == null || UnityOrigin.Length < 2)
                return Vector2Int.zero;
            return new Vector2Int(UnityOrigin[0], UnityOrigin[1]);
        }

        public BuildingLodStorageMode GetLodStorageMode() =>
            BuildingLodStorage == "dual" ? BuildingLodStorageMode.DualFile : BuildingLodStorageMode.Hierarchy;

        /// <summary>
        /// v2+ supertile RAW files use GDAL row order (same as 1x1). v1 supertiles were stored in Unity row order.
        /// </summary>
        public bool UsesGdalSupertileHeightmaps()
        {
            if (string.IsNullOrEmpty(Version))
                return false;
            return int.TryParse(Version, out int v) && v >= 2;
        }

        public static StreamingDatasetManifest LoadFromFile(string manifestPath)
        {
            string json = System.IO.File.ReadAllText(manifestPath);
            return JsonConvert.DeserializeObject<StreamingDatasetManifest>(json);
        }
    }

    [Serializable]
    public class StreamingBasemapEntry
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("type")] public string Type;
        [JsonProperty("displayName")] public string DisplayName;

        public BasemapType GetBasemapType() =>
            string.Equals(Type, "Tiled", StringComparison.OrdinalIgnoreCase)
                ? BasemapType.Tiled
                : BasemapType.Ortho;
    }

    [Serializable]
    public class StreamingHlodLevel
    {
        public int Factor = 1;
        public float LoadDistance = 1500f;
        public float UnloadDistance = 2000f;
    }

    [Serializable]
    public class StreamingTileEntry
    {
        [JsonProperty("tileId")] public string TileId;
        [JsonProperty("left")] public int Left;
        [JsonProperty("bottom")] public int Bottom;
        [JsonProperty("right")] public int Right;
        [JsonProperty("top")] public int Top;
        [JsonProperty("unityPosition")] public float[] UnityPosition;
        [JsonProperty("terrainSize")] public float[] TerrainSize;
        [JsonProperty("heightmapPath")] public string HeightmapPath;
        [JsonProperty("terrainBundlePaths")]
        public Dictionary<string, string> TerrainBundlePaths = new();
        [JsonProperty("heightmapRes")] public int HeightmapRes = 1025;
        [JsonProperty("invalidRatio")] public float InvalidRatio;
        [JsonProperty("hasFacadeBuildings")] public bool HasFacadeBuildings;
        [JsonProperty("hasOrthoRoofBuildings")] public bool HasOrthoRoofBuildings;
        [JsonProperty("hasVegetationMask")] public bool HasVegetationMask;
        [JsonProperty("orthoPaths")] public Dictionary<string, string> OrthoPaths = new();
        [JsonProperty("tiledSplatPaths")] public Dictionary<string, List<string>> TiledSplatPaths = new();
        [JsonProperty("buildingBundlePaths")]
        public Dictionary<string, string> BuildingBundlePaths = new();
        [JsonProperty("vegetationBundlePath")] public string VegetationBundlePath;

        public Vector3 GetUnityPosition()
        {
            if (UnityPosition == null || UnityPosition.Length < 3)
                return Vector3.zero;
            return new Vector3(UnityPosition[0], UnityPosition[1], UnityPosition[2]);
        }

        public Vector3 GetTerrainSize()
        {
            if (TerrainSize == null || TerrainSize.Length < 3)
                return new Vector3(1000f, 600f, 1000f);
            return new Vector3(TerrainSize[0], TerrainSize[1], TerrainSize[2]);
        }
    }

    [Serializable]
    public class RuntimeTiledMetadataJson
    {
        [JsonProperty("layers")] public List<RuntimeTiledLayerJson> Layers;
    }

    [Serializable]
    public class RuntimeTiledLayerJson
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("texture_file")] public string TextureFile;
        [JsonProperty("tile_size_meters")] public float TileSizeMeters = 1000f;
    }

    [Serializable]
    public class StreamingSupertileEntry
    {
        [JsonProperty("supertileId")] public string SupertileId;
        [JsonProperty("factor")] public int Factor = 2;
        [JsonProperty("left")] public int Left;
        [JsonProperty("bottom")] public int Bottom;
        [JsonProperty("unityPosition")] public float[] UnityPosition;
        [JsonProperty("terrainSize")] public float[] TerrainSize;
        [JsonProperty("heightmapPath")] public string HeightmapPath;
        [JsonProperty("terrainBundlePaths")]
        public Dictionary<string, string> TerrainBundlePaths = new();
        [JsonProperty("heightmapRes")] public int HeightmapRes = 513;
        [JsonProperty("childTileIds")] public List<string> ChildTileIds = new();
        [JsonProperty("orthoPaths")] public Dictionary<string, string> OrthoPaths = new();

        public Vector3 GetUnityPosition()
        {
            if (UnityPosition == null || UnityPosition.Length < 3)
                return Vector3.zero;
            return new Vector3(UnityPosition[0], UnityPosition[1], UnityPosition[2]);
        }

        public Vector3 GetTerrainSize()
        {
            if (TerrainSize == null || TerrainSize.Length < 3)
                return new Vector3(1000f, 600f, 1000f);
            return new Vector3(TerrainSize[0], TerrainSize[1], TerrainSize[2]);
        }
    }
}
