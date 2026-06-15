using UnityEngine;
using UnityEditor;

namespace ZGConnect
{
    /// <summary>
    /// Quick in-editor test: applies a set of TerrainLayers + splatmap PNGs
    /// directly to the selected terrain without going through the full import pipeline.
    /// Remove this file before shipping.
    /// </summary>
    public class SplatmapTester : EditorWindow
    {
        private Terrain      _terrain;
        private TerrainLayer[] _layers = new TerrainLayer[8];
        private Texture2D    _splat0;
        private Texture2D    _splat1;

        [MenuItem("ZG Connect/Splatmap Tester (Dev)")]
        static void Open() => GetWindow<SplatmapTester>("Splatmap Tester");

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Target", EditorStyles.boldLabel);
            _terrain = (Terrain)EditorGUILayout.ObjectField("Terrain", _terrain, typeof(Terrain), true);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Splatmap PNGs", EditorStyles.boldLabel);
            _splat0 = (Texture2D)EditorGUILayout.ObjectField("splat0  (R=grass G=forest B=urban_light A=urban_dark)", _splat0, typeof(Texture2D), false);
            _splat1 = (Texture2D)EditorGUILayout.ObjectField("splat1  (R=water  G=sand   B=rock        A=asphalt)",   _splat1, typeof(Texture2D), false);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("TerrainLayers  (indices match splatmap channels)", EditorStyles.boldLabel);
            string[] labels = { "0 grass", "1 forest", "2 urban_light", "3 urban_dark",
                                 "4 water",  "5 sand",   "6 rock",        "7 asphalt" };
            for (int i = 0; i < 8; i++)
                _layers[i] = (TerrainLayer)EditorGUILayout.ObjectField(labels[i], _layers[i], typeof(TerrainLayer), false);

            EditorGUILayout.Space();
            GUI.enabled = _terrain != null;

            if (GUILayout.Button("Apply to Terrain"))
                Apply();

            if (GUILayout.Button("Reset to single layer (clear)"))
                Clear();

            GUI.enabled = true;

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Textures must have Read/Write enabled and no crunch compression.\n" +
                "TerrainLayers can be placeholder assets with a solid-colour texture for testing.",
                MessageType.Info);
        }

        private void Apply()
        {
            if (_terrain == null) return;

            // Collect non-null layers in order
            int count = 0;
            for (int i = 0; i < 8; i++) if (_layers[i] != null) count = i + 1;

            if (count == 0)
            {
                Debug.LogWarning("[SplatmapTester] No TerrainLayers assigned.");
                return;
            }

            var activeLayers = new TerrainLayer[count];
            for (int i = 0; i < count; i++) activeLayers[i] = _layers[i];

            TerrainData td = _terrain.terrainData;
            Undo.RecordObject(td, "Apply Splatmap Test");

            td.terrainLayers = activeLayers;

            Texture2D[] splats = { _splat0, _splat1 };
            int w = td.alphamapWidth;
            int h = td.alphamapHeight;
            float[,,] alphas = new float[h, w, count];

            for (int si = 0; si < splats.Length; si++)
            {
                Texture2D tex = splats[si];
                if (tex == null) continue;

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        Color c = tex.GetPixelBilinear(
                            (float)x / Mathf.Max(w - 1, 1),
                            (float)y / Mathf.Max(h - 1, 1));

                        int b = si * 4;
                        if (b + 0 < count) alphas[y, x, b + 0] = c.r;
                        if (b + 1 < count) alphas[y, x, b + 1] = c.g;
                        if (b + 2 < count) alphas[y, x, b + 2] = c.b;
                        if (b + 3 < count) alphas[y, x, b + 3] = c.a;
                    }
                }
            }

            // Normalise
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float sum = 0f;
                    for (int l = 0; l < count; l++) sum += alphas[y, x, l];
                    if (sum > 0.001f)
                        for (int l = 0; l < count; l++) alphas[y, x, l] /= sum;
                    else
                        alphas[y, x, 0] = 1f;
                }

            td.SetAlphamaps(0, 0, alphas);
            Debug.Log($"[SplatmapTester] Applied {count} layers to {_terrain.name}.");
        }

        private void Clear()
        {
            if (_terrain == null) return;
            TerrainData td = _terrain.terrainData;
            Undo.RecordObject(td, "Clear Splatmap Test");
            td.terrainLayers = new TerrainLayer[0];
            Debug.Log($"[SplatmapTester] Cleared terrain layers on {_terrain.name}.");
        }
    }
}
