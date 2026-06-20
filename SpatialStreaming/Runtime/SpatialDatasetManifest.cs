using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    [Serializable]
    public class SpatialDatasetManifest
    {
        [JsonProperty("version")] public string Version = "1";
        [JsonProperty("sourceManifest")] public string SourceManifest = SpatialStreamingPaths.DefaultParentManifestRelativePath;
        [JsonProperty("bakeFingerprint")] public string BakeFingerprint;
        [JsonProperty("tileSizeMeters")] public int TileSizeMeters = 1000;
        [JsonProperty("subcellSizeMeters")] public int SubcellSizeMeters = 250;
        [JsonProperty("tiles")] public List<SpatialTileManifestEntry> Tiles = new();
        [JsonProperty("supertiles")] public List<SpatialSupertileManifestEntry> Supertiles = new();

        public SpatialSupertileManifestEntry FindSupertile(string supertileId)
        {
            if (string.IsNullOrEmpty(supertileId) || Supertiles == null)
                return null;

            foreach (SpatialSupertileManifestEntry entry in Supertiles)
            {
                if (entry != null && entry.SupertileId == supertileId)
                    return entry;
            }

            return null;
        }

        public static SpatialDatasetManifest LoadFromFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;

            string json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<SpatialDatasetManifest>(json);
        }

        public void SaveToFile(string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(path, json);
        }

        public SpatialTileManifestEntry FindTile(string tileId)
        {
            if (string.IsNullOrEmpty(tileId) || Tiles == null)
                return null;

            foreach (SpatialTileManifestEntry tile in Tiles)
            {
                if (tile != null && tile.TileId == tileId)
                    return tile;
            }

            return null;
        }
    }

    [Serializable]
    public class SpatialTileManifestEntry
    {
        [JsonProperty("tileId")] public string TileId;
        [JsonProperty("unityPosition")] public float[] UnityPosition;
        [JsonProperty("subcells")] public List<SpatialSubcellManifestEntry> Subcells = new();
        [JsonProperty("coarseBundleRel")] public string CoarseBundleRel;
        [JsonProperty("proxyBundleRel")] public string ProxyBundleRel;

        public Vector3 GetUnityPosition()
        {
            if (UnityPosition == null || UnityPosition.Length < 3)
                return Vector3.zero;
            return new Vector3(UnityPosition[0], UnityPosition[1], UnityPosition[2]);
        }

        public bool UsesSubcells => Subcells != null && Subcells.Count > 0;
    }

    [Serializable]
    public class SpatialSubcellManifestEntry
    {
        public const string PrefabLoadKind = "prefab";
        public const string MeshDetailLoadKind = "mesh_detail";

        [JsonProperty("subcellId")] public string SubcellId;
        [JsonProperty("gridX")] public int GridX;
        [JsonProperty("gridY")] public int GridY;
        [JsonProperty("bundleRel")] public string BundleRel;
        [JsonProperty("proxyBundleRel")] public string ProxyBundleRel;
        [JsonProperty("buildingCount")] public int BuildingCount;
        [JsonProperty("loadKind")] public string LoadKind;

        public bool UsesMeshDetail =>
            string.Equals(LoadKind, MeshDetailLoadKind, StringComparison.OrdinalIgnoreCase);
    }

    [Serializable]
    public class SpatialSupertileManifestEntry
    {
        [JsonProperty("supertileId")] public string SupertileId;
        [JsonProperty("factor")] public int Factor;
        [JsonProperty("left")] public int Left;
        [JsonProperty("bottom")] public int Bottom;
        [JsonProperty("unityPosition")] public float[] UnityPosition;
        [JsonProperty("bundleRel")] public string BundleRel;
        [JsonProperty("childTileIds")] public List<string> ChildTileIds = new();

        public Vector3 GetUnityPosition()
        {
            if (UnityPosition == null || UnityPosition.Length < 3)
                return Vector3.zero;
            return new Vector3(UnityPosition[0], UnityPosition[1], UnityPosition[2]);
        }
    }
}
