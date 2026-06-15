using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect
{
    /// <summary>
    /// Switches the active basemap on a terrain tile at runtime.
    /// Supports both Ortho (single-layer, trivial alphamap) and Tiled (multi-layer, OSM splatmap).
    /// Attach to the same GameObject that has a Terrain component.
    /// </summary>
    [RequireComponent(typeof(Terrain))]
    public class BasemapSwitcher : MonoBehaviour
    {
        public CityDataset dataset;
        public string      tileId;
        [HideInInspector] public string activeBasemapId;

        private Terrain _terrain;

        private void Awake() => _terrain = GetComponent<Terrain>();

        /// <summary>
        /// Activates the named basemap on this tile's terrain.
        /// Checks tiled layer sets first, then falls back to ortho layer entries.
        /// </summary>
        public void ActivateBasemap(string basemapId)
        {
            if (dataset == null || string.IsNullOrEmpty(tileId)) return;

            CityTileRecord rec = dataset.tiles.Find(r => r.tileId == tileId);
            if (rec == null)
            {
                Debug.LogWarning($"[ZGConnect] BasemapSwitcher: tile '{tileId}' not found in dataset.");
                return;
            }

            // Try tiled layer sets first
            TerrainLayerSet set = rec.basemapLayerSets?.Find(s => s != null && s.basemapId == basemapId);
            if (set != null)
            {
                ApplyLayerSet(set);
                activeBasemapId = basemapId;
                return;
            }

            // Fall back to ortho entries
            BasemapLayerEntry entry = rec.basemapLayers?.Find(e => e.basemapId == basemapId);
            if (entry?.terrainLayer != null)
            {
                _terrain.terrainData.terrainLayers = new[] { entry.terrainLayer };
                ApplyTrivialAlphamap(1);
                activeBasemapId = basemapId;
                return;
            }

            Debug.LogWarning($"[ZGConnect] BasemapSwitcher: basemap '{basemapId}' not found for tile '{tileId}'.");
        }

        private void ApplyLayerSet(TerrainLayerSet set)
        {
            TerrainData td = _terrain.terrainData;

            if (set.layers == null || set.layers.Length == 0) return;
            td.terrainLayers = set.layers;

            if (set.type == BasemapType.Ortho || set.splatmaps == null || set.splatmaps.Length == 0)
            {
                ApplyTrivialAlphamap(set.layers.Length);
                return;
            }

            int layerCount = set.layers.Length;
            int w = td.alphamapWidth;
            int h = td.alphamapHeight;
            float[,,] alphas = new float[h, w, layerCount];

            for (int si = 0; si < set.splatmaps.Length; si++)
            {
                Texture2D splat = set.splatmaps[si];
                if (splat == null) continue;

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        Color c = splat.GetPixelBilinear(
                            (float)x / Mathf.Max(w - 1, 1),
                            (float)y / Mathf.Max(h - 1, 1));

                        int baseIdx = si * 4;
                        if (baseIdx + 0 < layerCount) alphas[y, x, baseIdx + 0] = c.r;
                        if (baseIdx + 1 < layerCount) alphas[y, x, baseIdx + 1] = c.g;
                        if (baseIdx + 2 < layerCount) alphas[y, x, baseIdx + 2] = c.b;
                        if (baseIdx + 3 < layerCount) alphas[y, x, baseIdx + 3] = c.a;
                    }
                }
            }

            // Normalise weights per pixel
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float sum = 0f;
                    for (int l = 0; l < layerCount; l++) sum += alphas[y, x, l];
                    if (sum > 0.001f)
                        for (int l = 0; l < layerCount; l++) alphas[y, x, l] /= sum;
                    else
                        alphas[y, x, 0] = 1f;
                }
            }

            td.SetAlphamaps(0, 0, alphas);
        }

        private void ApplyTrivialAlphamap(int layerCount)
        {
            TerrainData td = _terrain.terrainData;
            int w = td.alphamapWidth;
            int h = td.alphamapHeight;
            float[,,] maps = new float[h, w, Mathf.Max(layerCount, 1)];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    maps[y, x, 0] = 1f;
            td.SetAlphamaps(0, 0, maps);
        }
    }
}
