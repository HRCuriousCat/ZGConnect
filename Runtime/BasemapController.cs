using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect
{
    /// <summary>
    /// Switches or cross-fades between imported basemap types on all terrain tiles.
    ///
    /// Setup:
    ///   1. Add this component to any persistent GameObject.
    ///   2. Assign the CityDataset asset.
    ///   3. Optionally set Default Basemap Id to activate a specific layer on Start.
    ///
    /// Usage (runtime or editor scripts):
    ///   controller.SwitchTo("roadmap");             // instant cut
    ///   controller.TransitionTo("tron", 1.5f);      // smooth crossfade
    ///
    /// Streaming integration:
    ///   When TerrainStreamingController activates a tile, call
    ///   basemapController.ApplyToTile(record) so the tile starts with
    ///   the currently active basemap instead of the import default.
    /// </summary>
    [DisallowMultipleComponent]
    public class BasemapController : MonoBehaviour
    {
        [SerializeField] private CityDataset dataset;
        [Tooltip("Basemap id to activate on Start. Leave empty to keep import default (layer 0).")]
        [SerializeField] private string defaultBasemapId;

        /// <summary>The basemap id that is currently active (or being transitioned to).</summary>
        public string ActiveBasemapId { get; private set; }

        // ──────────────────────────────────────────────────────────────────────

        private void Start()
        {
            if (!string.IsNullOrEmpty(defaultBasemapId))
                SwitchTo(defaultBasemapId);
        }

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Instantly cuts all active tiles to <paramref name="basemapId"/>.
        /// Stops any in-progress transition.
        /// </summary>
        public void SwitchTo(string basemapId)
        {
            if (dataset == null) return;

            int idx = GetLayerIndex(basemapId);
            if (idx < 0)
            {
                Debug.LogWarning($"[ZGConnect] BasemapController: unknown basemap id '{basemapId}'.");
                return;
            }

            StopAllCoroutines();
            ActiveBasemapId = basemapId;

            foreach (CityTileRecord rec in dataset.tiles)
                ApplyUniform(rec, idx);
        }

        /// <summary>
        /// Smoothly cross-fades all active tiles from the current basemap to
        /// <paramref name="basemapId"/> over <paramref name="duration"/> seconds.
        /// Pass duration = 0 to cut immediately (same as SwitchTo).
        /// </summary>
        public void TransitionTo(string basemapId, float duration)
        {
            if (duration <= 0f) { SwitchTo(basemapId); return; }
            if (dataset == null) return;

            int targetIdx = GetLayerIndex(basemapId);
            if (targetIdx < 0)
            {
                Debug.LogWarning($"[ZGConnect] BasemapController: unknown basemap id '{basemapId}'.");
                return;
            }

            int currentIdx = GetLayerIndex(ActiveBasemapId);
            StopAllCoroutines();
            StartCoroutine(TransitionCoroutine(currentIdx, targetIdx, basemapId, duration));
        }

        /// <summary>
        /// Applies the currently active basemap to a single tile record.
        /// Call this when a tile is freshly enabled by TerrainStreamingController
        /// so it adopts the active basemap instead of the import default.
        /// </summary>
        public void ApplyToTile(CityTileRecord rec)
        {
            if (rec == null || string.IsNullOrEmpty(ActiveBasemapId)) return;
            int idx = GetLayerIndex(ActiveBasemapId);
            if (idx >= 0) ApplyUniform(rec, idx);
        }

        // ── Private helpers ────────────────────────────────────────────────────

        private int GetLayerIndex(string basemapId)
        {
            if (string.IsNullOrEmpty(basemapId) || dataset == null) return -1;
            for (int i = 0; i < dataset.availableBasemaps.Count; i++)
                if (dataset.availableBasemaps[i].basemapId == basemapId)
                    return i;
            return -1;
        }

        /// <summary>Writes a uniform alphamap: activeIndex channel = 1.0, all others = 0.0.</summary>
        private static void ApplyUniform(CityTileRecord rec, int activeIndex)
        {
            TerrainData td = GetTerrainData(rec);
            if (td == null) return;

            int n = td.terrainLayers != null ? td.terrainLayers.Length : 0;
            if (n == 0 || activeIndex >= n) return;

            int w = td.alphamapWidth;
            int h = td.alphamapHeight;
            float[,,] maps = new float[h, w, n];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    maps[y, x, activeIndex] = 1f;
            td.SetAlphamaps(0, 0, maps);
        }

        /// <summary>Blends between fromIndex (weight 1−t) and toIndex (weight t).</summary>
        private static void ApplyBlended(CityTileRecord rec, int fromIndex, int toIndex, float t)
        {
            TerrainData td = GetTerrainData(rec);
            if (td == null) return;

            int n = td.terrainLayers != null ? td.terrainLayers.Length : 0;
            if (n == 0) return;

            // Clamp indices — a tile might not have all layers if partially imported
            bool fromValid = fromIndex >= 0 && fromIndex < n;
            bool toValid   = toIndex   >= 0 && toIndex   < n;
            if (!toValid) return;

            int w = td.alphamapWidth;
            int h = td.alphamapHeight;
            float[,,] maps = new float[h, w, n];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (fromValid) maps[y, x, fromIndex] = 1f - t;
                    maps[y, x, toIndex] = t;
                }
            td.SetAlphamaps(0, 0, maps);
        }

        private static TerrainData GetTerrainData(CityTileRecord rec)
        {
            if (rec?.sceneObject == null) return null;
            Terrain terrain = rec.sceneObject.GetComponent<Terrain>();
            return terrain != null ? terrain.terrainData : null;
        }

        private IEnumerator TransitionCoroutine(
            int fromIndex, int toIndex, string targetId, float duration)
        {
            ActiveBasemapId = targetId;
            float elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t  = Mathf.Clamp01(elapsed / duration);

                foreach (CityTileRecord rec in dataset.tiles)
                {
                    if (rec.sceneObject == null || !rec.sceneObject.activeSelf) continue;
                    ApplyBlended(rec, fromIndex, toIndex, t);
                }

                yield return null;
            }

            // Final pass: clean uniform state, no floating-point residue
            foreach (CityTileRecord rec in dataset.tiles)
            {
                if (rec.sceneObject == null || !rec.sceneObject.activeSelf) continue;
                ApplyUniform(rec, toIndex);
            }
        }
    }
}
