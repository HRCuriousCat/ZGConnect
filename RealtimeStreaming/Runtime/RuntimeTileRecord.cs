using System.Collections.Generic;
using UnityEngine;
using ZGConnect;

namespace ZGConnect.RealtimeStreaming
{
    public enum RealtimeTileLodState
    {
        Unloaded = 0,
        Loading = 1,
        Loaded = 2,
    }

    public class RuntimeTileRecord
    {
        public string Key;
        public int HlodFactor = 1;
        public bool IsSupertile;

        public StreamingTileEntry Leaf;
        public StreamingSupertileEntry Supertile;

        public Vector3 UnityPosition;
        public int TileSizeMeters;
        public int HeightmapRes;

        public RealtimeTileLodState State = RealtimeTileLodState.Unloaded;

        public GameObject TerrainObject;
        public TerrainData TerrainData;
        public AssetBundle TerrainBundle;
        public string TerrainBundleFullPath;
        public bool LoadedFromTerrainBundle;
        public GameObject BuildingsObject;
        public BuildingsMetadataJson BuildingsMetadata;

        public readonly Dictionary<string, Texture2D> OrthoTextures = new();
        public readonly Dictionary<string, TerrainLayer> OrthoLayers = new();
        public readonly Dictionary<string, List<Texture2D>> TiledSplatTextures = new();
        public readonly List<Texture2D> GpuTextures = new();
        public readonly List<TerrainLayer> GpuTerrainLayers = new();

        public VegetationChunkRenderer VegetationRenderer;
        public Texture2D VegetationMask;
        /// <summary>Fingerprint of streamer vegetation display settings when vegetation was last generated.</summary>
        public int VegetationSettingsFingerprint;

        public string ActiveBasemapId;
        public string LoadedTerrainBundleBasemapId;
        public TerrainLayer[] BundleSourceTerrainLayers;

        public int Left;
        public int Bottom;

        public static RuntimeTileRecord FromLeaf(StreamingTileEntry entry, int tileSizeMeters)
        {
            return new RuntimeTileRecord
            {
                Key = entry.TileId,
                HlodFactor = 1,
                IsSupertile = false,
                Leaf = entry,
                UnityPosition = entry.GetUnityPosition(),
                TileSizeMeters = tileSizeMeters,
                HeightmapRes = entry.HeightmapRes,
                Left = entry.Left,
                Bottom = entry.Bottom,
            };
        }

        public static RuntimeTileRecord FromSupertile(StreamingSupertileEntry entry)
        {
            return new RuntimeTileRecord
            {
                Key = entry.SupertileId,
                HlodFactor = entry.Factor,
                IsSupertile = true,
                Supertile = entry,
                UnityPosition = entry.GetUnityPosition(),
                TileSizeMeters = entry.Factor * 1000,
                HeightmapRes = entry.HeightmapRes,
                Left = entry.Left,
                Bottom = entry.Bottom,
            };
        }

        public CityTileRecord ToCityTileRecord(string activeBasemapId, string roofOrthophotoBasemapId = null)
        {
            var rec = new CityTileRecord
            {
                tileId = Key,
                left = Left,
                bottom = Bottom,
                right = Left + TileSizeMeters,
                top = Bottom + TileSizeMeters,
                unityPosition = UnityPosition,
            };

            rec.basemapLayers = new List<BasemapLayerEntry>();
            TryAddBasemapLayer(rec, activeBasemapId);
            if (!string.IsNullOrEmpty(roofOrthophotoBasemapId)
                && !string.Equals(roofOrthophotoBasemapId, activeBasemapId, System.StringComparison.Ordinal))
            {
                TryAddBasemapLayer(rec, roofOrthophotoBasemapId);
            }

            if (rec.basemapLayers.Count == 0)
                return rec;

            BasemapLayerEntry primary = rec.basemapLayers[0];
            rec.primaryBasemapTexture = primary.texture;
            rec.primaryTerrainLayer = primary.terrainLayer;
            return rec;
        }

        void TryAddBasemapLayer(CityTileRecord rec, string basemapId)
        {
            if (string.IsNullOrEmpty(basemapId))
                return;

            Texture2D basemapTex = GetBasemapTexture(basemapId);
            if (basemapTex == null)
                return;

            OrthoLayers.TryGetValue(basemapId, out TerrainLayer terrainLayer);
            if (terrainLayer == null && LoadedFromTerrainBundle &&
                string.Equals(LoadedTerrainBundleBasemapId, basemapId, System.StringComparison.Ordinal) &&
                TerrainData?.terrainLayers != null)
            {
                foreach (TerrainLayer layer in TerrainData.terrainLayers)
                {
                    if (layer?.diffuseTexture != null)
                    {
                        terrainLayer = layer;
                        break;
                    }
                }
            }

            rec.basemapLayers.Add(new BasemapLayerEntry
            {
                basemapId = basemapId,
                texture = basemapTex,
                terrainLayer = terrainLayer,
            });
        }

        public Texture2D GetBasemapTexture(string basemapId)
        {
            if (string.IsNullOrEmpty(basemapId))
                return null;

            if (OrthoTextures.TryGetValue(basemapId, out Texture2D ortho) && ortho != null)
                return ortho;

            if (!LoadedFromTerrainBundle ||
                !string.Equals(LoadedTerrainBundleBasemapId, basemapId, System.StringComparison.Ordinal) ||
                TerrainData?.terrainLayers == null)
            {
                return null;
            }

            foreach (TerrainLayer layer in TerrainData.terrainLayers)
            {
                if (layer?.diffuseTexture != null)
                    return layer.diffuseTexture;
            }

            return null;
        }
    }
}
