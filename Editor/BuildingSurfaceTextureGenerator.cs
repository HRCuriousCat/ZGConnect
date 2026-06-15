using System.IO;
using UnityEditor;
using UnityEngine;

namespace ZGConnect.Editor
{
    public static class BuildingSurfaceTextureGenerator
    {
        private const string UrpLitShader = "Universal Render Pipeline/Lit";
        private const int DefaultVariantCount = 1;

        [MenuItem("ZG Connect/Buildings/Generate Grid Placeholder Textures")]
        public static void GenerateAll()
        {
            GenerateAll(DefaultVariantCount);
        }

        [MenuItem("ZG Connect/Buildings/Create Default Building Surface Settings")]
        public static void CreateDefaultSettingsAsset()
        {
            string path = "Assets/ZGConnect/Assets/Buildings/BuildingSurfaceSettings.asset";
            EnsureFolder("Assets/ZGConnect/Assets/Buildings");

            var existing = AssetDatabase.LoadAssetAtPath<BuildingSurfaceSettings>(path);
            if (existing != null)
            {
                Debug.Log($"[ZGConnect] Settings already exist at {path}");
                Selection.activeObject = existing;
                return;
            }

            var so = ScriptableObject.CreateInstance<BuildingSurfaceSettings>();
            AssetDatabase.CreateAsset(so, path);
            AssetDatabase.SaveAssets();
            GenerateAll(DefaultVariantCount);
            Selection.activeObject = so;
            Debug.Log($"[ZGConnect] Created {path} and generated placeholder textures.");
        }

        public static void GenerateAll(int variantsPerCategory)
        {
            variantsPerCategory = 1; // one placeholder per category by design
            EnsureFolder(BuildingSurfaceSettings.TexturesRoot);
            EnsureFolder(BuildingSurfaceSettings.MaterialsRoot);

            var settings = FindOrCreateSettings();

            GenerateCategory(settings.houseProfile, "Houses", variantsPerCategory);
            GenerateCategory(settings.midRiseProfile, "Buildings", variantsPerCategory);
            GenerateCategory(settings.highRiseProfile, "Skyscrapers", variantsPerCategory);

            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[ZGConnect] Generated building grid textures (1 variant per category).");
        }

        private static void GenerateCategory(
            BuildingCategoryProfile profile,
            string folderName,
            int variantCount)
        {
            string texFolder = $"{BuildingSurfaceSettings.TexturesRoot}/{folderName}";
            string matFolder = $"{BuildingSurfaceSettings.MaterialsRoot}/{folderName}";
            EnsureFolder(texFolder);
            EnsureFolder(matFolder);

            profile.facadeVariants = new BuildingSurfaceVariant[1];
            profile.flatRoofVariants = new BuildingSurfaceVariant[1];
            profile.slopedRoofVariants = new BuildingSurfaceVariant[1];

            string facadePath = $"{texFolder}/facade_grid.png";
            string flatPath = $"{texFolder}/roof_flat_grid.png";
            string slopedPath = $"{texFolder}/roof_sloped_grid.png";

            Texture2D facadeTex = GenerateFacadeGrid(
                profile.textureWidth,
                profile.textureHeight,
                profile.maxFloorsInTexture,
                profile.category);
            SaveTexture(facadePath, facadeTex);

            Texture2D flatTex = GenerateRoofGrid(512, 512, profile.category, sloped: false);
            SaveTexture(flatPath, flatTex);

            Texture2D slopedTex = GenerateRoofGrid(512, 512, profile.category, sloped: true);
            SaveTexture(slopedPath, slopedTex);

            profile.facadeVariants[0] = new BuildingSurfaceVariant
            {
                material = CreateMaterial(matFolder, "facade_grid", facadePath),
            };
            profile.flatRoofVariants[0] = new BuildingSurfaceVariant
            {
                material = CreateMaterial(matFolder, "roof_flat_grid", flatPath),
            };
            profile.slopedRoofVariants[0] = new BuildingSurfaceVariant
            {
                material = CreateMaterial(matFolder, "roof_sloped_grid", slopedPath),
            };
        }

        private static Texture2D GenerateFacadeGrid(
            int width, int height, int floorRows, BuildingCategory category)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            float baseGray = 0.35f;
            Color bg = new Color(baseGray, baseGray, baseGray + 0.02f, 1f);
            Color line = new Color(0.12f, 0.12f, 0.14f, 1f);
            Color accent = CategoryAccent(category);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float v = y / (float)height;
                    float u = x / (float)width;
                    Color c = bg;

                    int row = Mathf.FloorToInt(v * floorRows);
                    float rowFrac = v * floorRows - row;
                    if (rowFrac < 0.02f || rowFrac > 0.98f)
                        c = line;

                    int col = Mathf.FloorToInt(u * 8f);
                    float colFrac = u * 8f - col;
                    if (colFrac < 0.03f)
                        c = Color.Lerp(c, line, 0.7f);

                    if (x < 4 && y > height - 8)
                        c = accent;

                    tex.SetPixel(x, y, c);
                }
            }

            DrawCellLabels(tex, 8, floorRows, CategoryLetter(category), accent);

            tex.Apply();
            return tex;
        }

        private static Texture2D GenerateRoofGrid(int width, int height, BuildingCategory category, bool sloped)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            float baseVal = sloped ? 0.42f : 0.38f;
            Color bg = new Color(baseVal, baseVal, baseVal + 0.03f, 1f);
            Color line = new Color(0.1f, 0.1f, 0.12f, 1f);

            int cells = 8;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float u = x / (float)width * cells;
                    float v = y / (float)height * cells;
                    bool gridLine = (u - Mathf.Floor(u) < 0.06f) || (v - Mathf.Floor(v) < 0.06f);
                    Color c = gridLine ? line : bg;
                    if (sloped && ((x + y) % 17 < 2))
                        c = Color.Lerp(c, CategoryAccent(category), 0.25f);
                    tex.SetPixel(x, y, c);
                }
            }

            DrawCellLabels(tex, cells, cells, CategoryLetter(category), CategoryAccent(category));

            tex.Apply();
            return tex;
        }

        private static char CategoryLetter(BuildingCategory cat)
        {
            switch (cat)
            {
                case BuildingCategory.MidRise:  return 'M';
                case BuildingCategory.HighRise: return 'S';
                default:                        return 'H';
            }
        }

        private static void DrawCellLabels(Texture2D tex, int cols, int rows, char categoryLetter, Color color)
        {
            cols = Mathf.Max(1, cols);
            rows = Mathf.Max(1, rows);

            float cellW = tex.width / (float)cols;
            float cellH = tex.height / (float)rows;
            int letterScale = Mathf.Max(2, Mathf.Min((int)(cellW / 10f), (int)(cellH / 8f)));
            int coordScale = Mathf.Max(1, letterScale / 2);

            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < cols; col++)
                {
                    // Bottom-left indexing: (1,1) is the bottom-left grid cell.
                    int ix = col + 1;
                    int iy = row + 1;
                    string coords = $"({ix},{iy})";

                    int cellX = Mathf.RoundToInt(col * cellW);
                    int cellY = Mathf.RoundToInt(row * cellH);
                    DrawCellLabel(tex, cellX, cellY, Mathf.RoundToInt(cellW), Mathf.RoundToInt(cellH),
                        categoryLetter, coords, letterScale, coordScale, color);
                }
            }
        }

        private static void DrawCellLabel(
            Texture2D tex,
            int cellX,
            int cellY,
            int cellW,
            int cellH,
            char letter,
            string coords,
            int letterScale,
            int coordScale,
            Color color)
        {
            int pad = Mathf.Max(2, coordScale);
            int x = cellX + pad;
            int y = cellY + pad;

            // Draw letter (larger) on first line.
            DrawPixelText(tex, x, y + (5 * coordScale) + pad, letter.ToString(), letterScale, color);
            // Coordinates smaller, on second line.
            DrawPixelText(tex, x, y, coords, coordScale, Color.Lerp(color, Color.white, 0.15f));

            // Thin underline in each cell for readability.
            int lineY = cellY + Mathf.Clamp(cellH - pad - 1, 0, tex.height - 1);
            for (int px = cellX + 1; px < cellX + cellW - 1; px++)
            {
                if (px < 0 || px >= tex.width || lineY < 0 || lineY >= tex.height) continue;
                tex.SetPixel(px, lineY, Color.Lerp(tex.GetPixel(px, lineY), Color.black, 0.25f));
            }
        }

        private static void DrawPixelText(Texture2D tex, int xLeft, int yBottom, string text, int scale, Color color)
        {
            int cursor = xLeft;
            int maxY = yBottom;
            for (int i = 0; i < text.Length; i++)
            {
                string[] glyph = Glyph(text[i]);
                int gw = glyph[0].Length;
                int gh = glyph.Length;

                // Faint plate per glyph.
                for (int py = 0; py < gh * scale; py++)
                {
                    for (int px = 0; px < gw * scale; px++)
                    {
                        int tx = cursor + px;
                        int ty = yBottom + py;
                        if (tx < 0 || ty < 0 || tx >= tex.width || ty >= tex.height) continue;
                        tex.SetPixel(tx, ty, Color.Lerp(tex.GetPixel(tx, ty), Color.black, 0.28f));
                    }
                }

                for (int r = 0; r < gh; r++)
                {
                    string rowBits = glyph[r];
                    for (int c = 0; c < gw; c++)
                    {
                        if (rowBits[c] != '1') continue;
                        for (int sy = 0; sy < scale; sy++)
                        {
                            for (int sx = 0; sx < scale; sx++)
                            {
                                int tx = cursor + c * scale + sx;
                                // r=0 is top row in glyph data; flip to draw upright.
                                int ty = yBottom + ((gh - 1 - r) * scale + sy);
                                if (tx < 0 || ty < 0 || tx >= tex.width || ty >= tex.height) continue;
                                tex.SetPixel(tx, ty, color);
                            }
                        }
                    }
                }

                cursor += gw * scale + scale;
                maxY = Mathf.Max(maxY, yBottom + gh * scale);
                if (cursor >= tex.width - 2) return;
            }
        }

        private static string[] Glyph(char c)
        {
            switch (c)
            {
                case 'H': return new[] { "101", "101", "111", "101", "101" };
                case 'M': return new[] { "101", "111", "111", "101", "101" };
                case 'S': return new[] { "111", "100", "111", "001", "111" };
                case '0': return new[] { "111", "101", "101", "101", "111" };
                case '1': return new[] { "010", "110", "010", "010", "111" };
                case '2': return new[] { "111", "001", "111", "100", "111" };
                case '3': return new[] { "111", "001", "111", "001", "111" };
                case '4': return new[] { "101", "101", "111", "001", "001" };
                case '5': return new[] { "111", "100", "111", "001", "111" };
                case '6': return new[] { "111", "100", "111", "101", "111" };
                case '7': return new[] { "111", "001", "010", "010", "010" };
                case '8': return new[] { "111", "101", "111", "101", "111" };
                case '9': return new[] { "111", "101", "111", "001", "111" };
                case '(': return new[] { "011", "100", "100", "100", "011" };
                case ')': return new[] { "110", "001", "001", "001", "110" };
                case ',': return new[] { "000", "000", "000", "010", "100" };
                default:  return new[] { "000", "000", "000", "000", "000" };
            }
        }

        private static Color CategoryAccent(BuildingCategory cat)
        {
            switch (cat)
            {
                case BuildingCategory.MidRise:  return new Color(0.2f, 0.45f, 0.85f, 1f);
                case BuildingCategory.HighRise: return new Color(0.85f, 0.35f, 0.2f, 1f);
                default:                        return new Color(0.25f, 0.7f, 0.35f, 1f);
            }
        }

        private static Material CreateMaterial(string folder, string name, string textureAssetPath)
        {
            string matPath = $"{folder}/{name}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (existing != null)
            {
                AssignTexture(existing, textureAssetPath);
                return existing;
            }

            Shader shader = Shader.Find(UrpLitShader);
            if (shader == null)
                shader = Shader.Find("Standard");

            var mat = new Material(shader);
            AssignTexture(mat, textureAssetPath);
            AssetDatabase.CreateAsset(mat, matPath);
            return mat;
        }

        private static void AssignTexture(Material mat, string textureAssetPath)
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(textureAssetPath);
            if (tex == null) return;
            if (mat.HasProperty("_BaseMap"))
                mat.SetTexture("_BaseMap", tex);
            else if (mat.HasProperty("_MainTex"))
                mat.SetTexture("_MainTex", tex);
        }

        private static void SaveTexture(string assetPath, Texture2D tex)
        {
            EnsureFolder(Path.GetDirectoryName(assetPath)?.Replace('\\', '/'));
            byte[] png = tex.EncodeToPNG();
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? "";
            string full = Path.Combine(projectRoot, assetPath);
            File.WriteAllBytes(full, png);
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(assetPath);

            var imp = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (imp != null)
            {
                imp.textureType = TextureImporterType.Default;
                imp.sRGBTexture = true;
                imp.mipmapEnabled = true;
                imp.wrapMode = TextureWrapMode.Repeat;
                imp.SaveAndReimport();
            }
        }

        private static BuildingSurfaceSettings FindOrCreateSettings()
        {
            string[] guids = AssetDatabase.FindAssets("t:BuildingSurfaceSettings");
            if (guids.Length > 0)
                return AssetDatabase.LoadAssetAtPath<BuildingSurfaceSettings>(
                    AssetDatabase.GUIDToAssetPath(guids[0]));

            string path = "Assets/ZGConnect/Assets/Buildings/BuildingSurfaceSettings.asset";
            EnsureFolder("Assets/ZGConnect/Assets/Buildings");
            var so = ScriptableObject.CreateInstance<BuildingSurfaceSettings>();
            AssetDatabase.CreateAsset(so, path);
            return so;
        }

        private static void EnsureFolder(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || AssetDatabase.IsValidFolder(assetPath))
                return;

            string parent = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            string leaf = Path.GetFileName(assetPath);
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);
            if (!AssetDatabase.IsValidFolder(assetPath))
                AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
