using System.IO;
using UnityEditor;
using UnityEngine;

namespace ZGConnect.Editor
{
    public static class ZGConnectMapPlaceholderUtility
    {
        private const int kWidth = ZGConnectMapExtent.PlaceholderWidth;

        [MenuItem("ZG Connect/Generate Map Placeholder Texture")]
        public static void GenerateFromMenu()
        {
            Generate(force: true);
            EditorUtility.DisplayDialog(
                "ZG Connect",
                $"Placeholder saved to:\n{ZGConnectMapExtent.PlaceholderAssetPath}",
                "OK");
        }

        /// <summary>
        /// Returns the built-in placeholder texture. Generates it only when the asset file is missing.
        /// Never overwrites an existing file — use <see cref="Generate"/> for explicit regeneration.
        /// </summary>
        public static Texture2D EnsurePlaceholder()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(ZGConnectMapExtent.PlaceholderAssetPath);
            if (existing != null)
                return existing;

            return Generate(force: false);
        }

        public static Texture2D Generate(bool force)
        {
            string path = ZGConnectMapExtent.PlaceholderAssetPath;
            if (!force && AssetDatabase.LoadAssetAtPath<Texture2D>(path) != null)
                return AssetDatabase.LoadAssetAtPath<Texture2D>(path);

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            int height = ZGConnectMapExtent.ExpectedPlaceholderHeight(kWidth);
            var tex = BuildPlaceholderTexture(kWidth, height);
            File.WriteAllBytes(path, tex.EncodeToJPG(92));
            Object.DestroyImmediate(tex);

            AssetDatabase.Refresh();

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                importer.textureCompression = TextureImporterCompression.Compressed;
                importer.mipmapEnabled      = false;
                importer.npotScale          = TextureImporterNPOTScale.None;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static Texture2D BuildPlaceholderTexture(int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);

            var cLand  = new Color(0.72f, 0.78f, 0.62f);
            var cPark  = new Color(0.55f, 0.72f, 0.48f);
            var cUrban = new Color(0.82f, 0.80f, 0.76f);
            var cRiver = new Color(0.55f, 0.68f, 0.82f);

            for (int y = 0; y < h; y++)
            {
                float v = (float)y / h;
                for (int x = 0; x < w; x++)
                {
                    float u = (float)x / w;

                    Color baseCol = Color.Lerp(cLand, cUrban, Mathf.PerlinNoise(u * 3.1f, v * 2.7f));
                    baseCol = Color.Lerp(baseCol, cPark, Mathf.PerlinNoise(u * 5f + 2f, v * 5f) * 0.35f);

                    float river = Mathf.Exp(-Mathf.Pow((v - 0.22f - u * 0.15f) / 0.04f, 2f));
                    baseCol = Color.Lerp(baseCol, cRiver, river * 0.85f);

                    tex.SetPixel(x, y, baseCol);
                }
            }

            int kmStepPx = Mathf.Max(8, Mathf.RoundToInt(w / (float)ZGConnectMapExtent.ExtentWidthM * 1000f));
            DrawGrid(tex, w, h, kmStepPx, new Color(1f, 1f, 1f, 0.14f));
            DrawBorder(tex, w, h, new Color(0.15f, 0.18f, 0.22f, 0.9f), 3);
            DrawNorthArrow(tex, w, h);

            tex.Apply();
            return tex;
        }

        private static void DrawGrid(Texture2D tex, int w, int h, int stepPx, Color line)
        {
            for (int x = 0; x < w; x += stepPx)
            {
                for (int y = 0; y < h; y++)
                {
                    Color c = tex.GetPixel(x, y);
                    tex.SetPixel(x, y, Color.Lerp(c, line, line.a));
                }
            }

            for (int y = 0; y < h; y += stepPx)
            {
                for (int x = 0; x < w; x++)
                {
                    Color c = tex.GetPixel(x, y);
                    tex.SetPixel(x, y, Color.Lerp(c, line, line.a));
                }
            }
        }

        private static void DrawBorder(Texture2D tex, int w, int h, Color col, int thickness)
        {
            for (int t = 0; t < thickness; t++)
            {
                for (int x = 0; x < w; x++)
                {
                    tex.SetPixel(x, t, col);
                    tex.SetPixel(x, h - 1 - t, col);
                }

                for (int y = 0; y < h; y++)
                {
                    tex.SetPixel(t, y, col);
                    tex.SetPixel(w - 1 - t, y, col);
                }
            }
        }

        private static void DrawNorthArrow(Texture2D tex, int w, int h)
        {
            int cx = w - 48;
            int cy = 48;
            var col = new Color(1f, 1f, 1f, 0.65f);
            for (int i = -12; i <= 12; i++)
                tex.SetPixel(cx + i, cy - 18, col);
            for (int dy = 0; dy < 28; dy++)
            {
                int half = 12 - dy / 3;
                for (int i = -half; i <= half; i++)
                    tex.SetPixel(cx + i, cy - 18 + dy, col);
            }
        }
    }
}
