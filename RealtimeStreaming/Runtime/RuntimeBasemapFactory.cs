using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ZGConnect.RealtimeStreaming
{
    public class RuntimeBasemapFactory
    {
        readonly string _datasetRoot;
        readonly Dictionary<string, SharedTiledLayerCache> _tiledCaches = new();

        sealed class SharedTiledLayerCache
        {
            public string BasemapId;
            public TerrainLayer[] Layers;
            public readonly List<Texture2D> Textures = new();
        }

        public RuntimeBasemapFactory(string datasetRoot)
        {
            _datasetRoot = datasetRoot;
        }

        public Texture2D LoadPngTexture(string relativePath, bool linear = false)
        {
            return LoadPngFromBytes(ReadDatasetBytes(relativePath), linear, TextureWrapMode.Clamp);
        }

        /// <summary>
        /// Ortho PNGs are RGBA; URP Terrain treats diffuse alpha as smoothness when an alpha channel exists.
        /// </summary>
        public Texture2D LoadOrthoPngTexture(string relativePath)
        {
            return LoadOrthoPngTextureFromBytes(ReadDatasetBytes(relativePath));
        }

        public Texture2D LoadOrthoPngTextureFromBytes(byte[] bytes)
        {
            Texture2D rgba = LoadPngFromBytes(bytes, linear: false, TextureWrapMode.Clamp);
            return PrepareOrthoDiffuseForTerrain(rgba);
        }

        public static Texture2D LoadPngFromBytes(byte[] bytes, bool linear, TextureWrapMode wrapMode)
        {
            if (bytes == null || bytes.Length == 0)
                return null;

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false, linear);
            if (!tex.LoadImage(bytes))
            {
                DestroyObject(tex);
                return null;
            }

            tex.wrapMode = wrapMode;
            tex.filterMode = FilterMode.Bilinear;
            tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            return tex;
        }

        /// <summary>
        /// Strips alpha so Terrain/Lit uses layer smoothness instead of texture alpha (which defaults to glossy).
        /// </summary>
        public static Texture2D PrepareOrthoDiffuseForTerrain(Texture2D src)
        {
            if (src == null)
                return null;

            var rgb = new Texture2D(src.width, src.height, TextureFormat.RGB24, mipChain: false, linear: false)
            {
                wrapMode = src.wrapMode,
                filterMode = src.filterMode,
                anisoLevel = src.anisoLevel,
            };

            Color[] srcPixels = src.GetPixels();
            rgb.SetPixels(srcPixels);
            rgb.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            DestroyObject(src);
            return rgb;
        }

        public TerrainLayer CreateOrthoLayer(Texture2D diffuse, string name, int tileSizeMeters = 1000)
        {
            if (diffuse == null)
                return null;

            var layer = new TerrainLayer
            {
                name = name,
                diffuseTexture = diffuse,
                tileSize = new Vector2(tileSizeMeters, tileSizeMeters),
                tileOffset = Vector2.zero,
                smoothnessSource = TerrainLayerSmoothnessSource.Constant,
                smoothness = 0f,
                metallic = 0f,
            };
            return layer;
        }

        static Vector4 ResolveHlodDiffuseRemapMax(Color tint, bool enabled) =>
            enabled && tint != Color.white
                ? new Vector4(tint.r, tint.g, tint.b, 1f)
                : Vector4.one;

        /// <summary>
        /// URP Terrain/Lit tints splat albedo via _DiffuseRemapScale (diffuseRemapMax - diffuseRemapMin),
        /// not material _BaseColor.
        /// </summary>
        public static void ApplyHlodTintToTerrainLayers(TerrainData td, Color tint, bool enabled)
        {
            if (td?.terrainLayers == null || td.terrainLayers.Length == 0)
                return;

            Vector4 remapMax = ResolveHlodDiffuseRemapMax(tint, enabled);
            foreach (TerrainLayer layer in td.terrainLayers)
            {
                if (layer == null)
                    continue;

                layer.diffuseRemapMin = Vector4.zero;
                layer.diffuseRemapMax = remapMax;
            }

            td.terrainLayers = td.terrainLayers;
        }

        public readonly struct BasemapVisualOptions
        {
            public readonly bool TintByHlod;
            public readonly Color HlodTint;
            public readonly bool DebugBorder;
            public readonly Color BorderColor;
            public readonly int BorderPixels;

            public BasemapVisualOptions(
                bool tintByHlod,
                Color hlodTint,
                bool debugBorder,
                Color borderColor,
                int borderPixels)
            {
                TintByHlod = tintByHlod;
                HlodTint = hlodTint;
                DebugBorder = debugBorder;
                BorderColor = borderColor;
                BorderPixels = borderPixels;
            }
        }

        public static Texture2D ApplyBasemapDebugBorder(Texture2D tex, Color color, int borderPixels)
        {
            if (tex == null || borderPixels <= 0)
                return tex;

            Texture2D bordered = CopyTextureReadable(tex);
            if (bordered == null)
                return tex;

            DrawBasemapBorder(bordered, color, borderPixels);
            return bordered;
        }

        public static void DrawBasemapBorder(Texture2D tex, Color color, int borderPixels)
        {
            if (tex == null || borderPixels <= 0)
                return;

            int w = tex.width;
            int h = tex.height;
            int thickness = Mathf.Clamp(borderPixels, 1, Mathf.Min(w, h) / 2);
            Color[] pixels = tex.GetPixels();

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (x < thickness || x >= w - thickness || y < thickness || y >= h - thickness)
                        pixels[y * w + x] = color;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
        }

        public static Texture2D CopyTextureReadable(Texture2D source)
        {
            if (source == null)
                return null;

            Texture2D copy = null;
            try
            {
                copy = new Texture2D(source.width, source.height, TextureFormat.RGB24, mipChain: false, linear: false)
                {
                    wrapMode = source.wrapMode,
                    filterMode = source.filterMode,
                    anisoLevel = source.anisoLevel,
                };
                copy.SetPixels(source.GetPixels());
                copy.Apply(updateMipmaps: false, makeNoLongerReadable: false);
                return copy;
            }
            catch
            {
                if (copy != null)
                    DestroyObject(copy);
            }

            var rt = RenderTexture.GetTemporary(
                source.width,
                source.height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB);
            Graphics.Blit(source, rt);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            copy = new Texture2D(source.width, source.height, TextureFormat.RGB24, mipChain: false, linear: false)
            {
                wrapMode = source.wrapMode,
                filterMode = source.filterMode,
                anisoLevel = source.anisoLevel,
            };
            copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            copy.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return copy;
        }

        public static TerrainLayer[] CloneTerrainLayersWithVisuals(
            TerrainLayer[] source,
            BasemapVisualOptions options,
            List<TerrainLayer> trackClones,
            List<Texture2D> trackTextures)
        {
            if (source == null || source.Length == 0)
                return source;

            Vector4 remapMax = ResolveHlodDiffuseRemapMax(options.HlodTint, options.TintByHlod);
            var cloned = new TerrainLayer[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                cloned[i] = source[i] == null
                    ? null
                    : CloneTerrainLayerWithVisuals(source[i], remapMax, options, trackTextures);
                if (cloned[i] != null)
                    trackClones?.Add(cloned[i]);
            }

            return cloned;
        }

        static TerrainLayer CloneTerrainLayerWithVisuals(
            TerrainLayer source,
            Vector4 remapMax,
            BasemapVisualOptions options,
            List<Texture2D> trackTextures)
        {
            Texture2D diffuse = source.diffuseTexture;
            if (options.DebugBorder && diffuse != null && options.BorderPixels > 0)
            {
                diffuse = ApplyBasemapDebugBorder(diffuse, options.BorderColor, options.BorderPixels);
                if (diffuse != null)
                    trackTextures?.Add(diffuse);
            }

            return new TerrainLayer
            {
                name = source.name,
                diffuseTexture = diffuse,
                normalMapTexture = source.normalMapTexture,
                maskMapTexture = source.maskMapTexture,
                tileSize = source.tileSize,
                tileOffset = source.tileOffset,
                specular = source.specular,
                metallic = source.metallic,
                smoothness = source.smoothness,
                normalScale = source.normalScale,
                smoothnessSource = source.smoothnessSource,
                diffuseRemapMin = Vector4.zero,
                diffuseRemapMax = remapMax,
                maskMapRemapMin = source.maskMapRemapMin,
                maskMapRemapMax = source.maskMapRemapMax,
            };
        }

        public static void ApplyMatteTerrainShading(Terrain terrain)
        {
            if (terrain == null)
                return;

            Material mat = terrain.materialTemplate;
            if (mat == null)
                return;

            for (int i = 0; i < 4; i++)
            {
                mat.SetFloat($"_Smoothness{i}", 0f);
                mat.SetFloat($"_Metallic{i}", 0f);
            }
        }

        byte[] ReadDatasetBytes(string relativePath)
        {
            string fullPath = Path.Combine(_datasetRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
                return null;
            return File.ReadAllBytes(fullPath);
        }

        public void ApplyOrthoBasemap(TerrainData td, TerrainLayer layer)
        {
            if (td == null || layer == null)
                return;

            td.terrainLayers = new[] { layer };
            ApplyUniformAlphamap(td, 1);
        }

        public TerrainLayer[] GetOrCreateSharedTiledLayers(string basemapId, string metadataRelativePath)
        {
            if (_tiledCaches.TryGetValue(basemapId, out SharedTiledLayerCache cached))
                return cached.Layers;

            string metaPath = Path.Combine(_datasetRoot, metadataRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(metaPath))
                return null;

            string json = File.ReadAllText(metaPath);
            var meta = Newtonsoft.Json.JsonConvert.DeserializeObject<RuntimeTiledMetadataJson>(json);
            if (meta?.Layers == null)
                return null;

            string basemapFolder = Path.GetDirectoryName(metaPath);
            var layers = new List<TerrainLayer>();
            var textures = new List<Texture2D>();

            foreach (var def in meta.Layers)
            {
                string texPath = Path.Combine(basemapFolder, def.TextureFile);
                if (!File.Exists(texPath))
                    continue;

                byte[] bytes = File.ReadAllBytes(texPath);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false, linear: false);
                if (!tex.LoadImage(bytes))
                {
                    DestroyObject(tex);
                    continue;
                }
                tex.wrapMode = TextureWrapMode.Repeat;
                tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
                textures.Add(tex);

                layers.Add(new TerrainLayer
                {
                    name = def.Id,
                    diffuseTexture = tex,
                    tileSize = new Vector2(def.TileSizeMeters, def.TileSizeMeters),
                    tileOffset = Vector2.zero,
                });
            }

            var cache = new SharedTiledLayerCache
            {
                BasemapId = basemapId,
                Layers = layers.ToArray(),
            };
            cache.Textures.AddRange(textures);
            _tiledCaches[basemapId] = cache;
            return cache.Layers;
        }

        public void ApplyTiledSplatmaps(TerrainData td, TerrainLayer[] sharedLayers, List<string> splatRelativePaths)
        {
            if (td == null || sharedLayers == null || sharedLayers.Length == 0 || splatRelativePaths == null)
                return;

            var splatTextures = new List<Texture2D>();
            foreach (string rel in splatRelativePaths)
            {
                Texture2D tex = LoadPngTexture(rel, linear: true);
                if (tex != null)
                    splatTextures.Add(tex);
            }

            if (splatTextures.Count == 0)
                return;

            td.terrainLayers = sharedLayers;
            ApplySplatmapAlphas(td, splatTextures, sharedLayers.Length);
        }

        public static void ApplyUniformAlphamap(TerrainData td, int activeLayerIndex)
        {
            int w = td.alphamapWidth;
            int h = td.alphamapHeight;
            int layerCount = Mathf.Max(1, td.terrainLayers?.Length ?? 1);
            float[,,] alphas = new float[h, w, layerCount];

            int idx = Mathf.Clamp(activeLayerIndex, 0, layerCount - 1);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    alphas[y, x, idx] = 1f;

            td.SetAlphamaps(0, 0, alphas);
        }

        public static void ApplySplatmapAlphas(TerrainData td, List<Texture2D> splatmaps, int layerCount)
        {
            int w = td.alphamapWidth;
            int h = td.alphamapHeight;
            float[,,] alphas = new float[h, w, layerCount];

            for (int si = 0; si < splatmaps.Count; si++)
            {
                Texture2D splat = splatmaps[si];
                if (splat == null)
                    continue;

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

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float sum = 0f;
                    for (int l = 0; l < layerCount; l++)
                        sum += alphas[y, x, l];
                    if (sum > 0.001f)
                    {
                        for (int l = 0; l < layerCount; l++)
                            alphas[y, x, l] /= sum;
                    }
                    else
                        alphas[y, x, 0] = 1f;
                }
            }

            td.SetAlphamaps(0, 0, alphas);
        }

        public void ReleaseSharedCaches(List<Texture2D> into)
        {
            foreach (SharedTiledLayerCache cache in _tiledCaches.Values)
                into.AddRange(cache.Textures);
            _tiledCaches.Clear();
        }

        static void DestroyObject(Object obj)
        {
            if (obj == null)
                return;

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                Object.DestroyImmediate(obj);
                return;
            }
#endif
            Object.Destroy(obj);
        }
    }
}
