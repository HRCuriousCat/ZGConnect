using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;

namespace ZGConnect.RealtimeStreaming
{
    public static class RuntimeTerrainBundleLoader
    {
        public sealed class LoadResult
        {
            public bool Success;
            public string Error;
            public TerrainData TerrainData;
            public GameObject TerrainPrefab;
            public AssetBundle Bundle;
            public string BundleFullPath;
            public UnityEngine.Object[] AllAssets;
        }

        public static bool TryLoadTerrainDataSync(string bundleFullPath, LoadResult into)
        {
            into.Success = false;
            into.Error = null;
            into.TerrainData = null;
            into.TerrainPrefab = null;
            into.Bundle = null;
            into.BundleFullPath = bundleFullPath;
            into.AllAssets = null;

            if (string.IsNullOrEmpty(bundleFullPath) || !File.Exists(bundleFullPath))
            {
                into.Error = $"Terrain bundle not found: {bundleFullPath}";
                return false;
            }

            AssetBundle bundle = AssetBundle.LoadFromFile(bundleFullPath);
            if (bundle == null)
            {
                into.Error = $"Failed to open AssetBundle: {bundleFullPath}";
                return false;
            }

            UnityEngine.Object[] allAssets = bundle.LoadAllAssets();
            into.AllAssets = allAssets;
            TerrainData td = null;
            GameObject terrainPrefab = null;
            if (allAssets != null)
            {
                foreach (UnityEngine.Object asset in allAssets)
                {
                    if (td == null && asset is TerrainData terrainData)
                        td = terrainData;
                }

                foreach (UnityEngine.Object asset in allAssets)
                {
                    if (asset is not GameObject go || go.GetComponent<Terrain>() == null)
                        continue;

                    terrainPrefab = go;
                    if (td == null)
                        td = go.GetComponent<Terrain>()?.terrainData;
                    break;
                }
            }

            if (td == null && terrainPrefab == null)
            {
                bundle.Unload(unloadAllLoadedObjects: true);
                into.Error = $"TerrainData not found in bundle: {bundleFullPath}. {DescribeBundleAssets(allAssets)}";
                return false;
            }

            if (td != null)
                RepairTerrainLayerTextures(td, allAssets);

            into.Success = true;
            into.TerrainData = td;
            into.TerrainPrefab = terrainPrefab;
            into.Bundle = bundle;
            into.BundleFullPath = bundleFullPath;
            return true;
        }

        public static void ReleaseEditorBundle(
            string bundleFullPath,
            AssetBundle bundle,
            bool unloadAllLoadedObjects = true)
        {
            if (bundle != null)
                bundle.Unload(unloadAllLoadedObjects);
        }

        public static IEnumerator LoadTerrainDataAsync(string bundleFullPath, LoadResult into)
        {
            into.Success = false;
            into.Error = null;
            into.TerrainData = null;
            into.TerrainPrefab = null;
            into.Bundle = null;
            into.BundleFullPath = bundleFullPath;

            AssetBundle bundle = null;
            string loadError = null;
            yield return RuntimeAssetBundleCache.AcquireAsync(
                bundleFullPath,
                acquired => bundle = acquired,
                error => loadError = error);

            if (bundle == null)
            {
                into.Error = loadError ?? $"Failed to open AssetBundle: {bundleFullPath}";
                yield break;
            }

            AssetBundleRequest assetRequest = bundle.LoadAllAssetsAsync();
            yield return assetRequest;

            TerrainData td = null;
            GameObject terrainPrefab = null;
            if (assetRequest.allAssets != null)
            {
                foreach (UnityEngine.Object asset in assetRequest.allAssets)
                {
                    if (td == null && asset is TerrainData terrainData)
                        td = terrainData;
                }

                foreach (UnityEngine.Object asset in assetRequest.allAssets)
                {
                    if (asset is not GameObject go || go.GetComponent<Terrain>() == null)
                        continue;

                    terrainPrefab = go;
                    if (td == null)
                        td = go.GetComponent<Terrain>()?.terrainData;
                    break;
                }
            }

            if (td == null && terrainPrefab == null)
            {
                RuntimeAssetBundleCache.Release(bundleFullPath, unloadAllLoadedObjects: true);
                into.Error = $"TerrainData not found in bundle: {bundleFullPath}. {DescribeBundleAssets(assetRequest.allAssets)}";
                yield break;
            }

            if (td != null)
                RepairTerrainLayerTextures(td, assetRequest.allAssets);

            into.Success = true;
            into.TerrainData = td;
            into.TerrainPrefab = terrainPrefab;
            into.Bundle = bundle;
            into.BundleFullPath = bundleFullPath;
        }

        static string DescribeBundleAssets(UnityEngine.Object[] allAssets)
        {
            if (allAssets == null || allAssets.Length == 0)
                return "Bundle contained no assets.";

            var sb = new StringBuilder();
            sb.Append($"Assets ({allAssets.Length}): ");
            int limit = Mathf.Min(allAssets.Length, 6);
            for (int i = 0; i < limit; i++)
            {
                UnityEngine.Object asset = allAssets[i];
                if (i > 0)
                    sb.Append(", ");
                sb.Append(asset != null ? asset.GetType().Name : "null");
            }

            if (allAssets.Length > limit)
                sb.Append($", +{allAssets.Length - limit} more");

            return sb.ToString();
        }

        public static void RepairTerrainLayerTextures(TerrainData td, UnityEngine.Object[] allAssets)
        {
            if (td?.terrainLayers == null || td.terrainLayers.Length == 0)
                return;

            Texture2D fallbackTex = null;
            if (allAssets != null)
            {
                foreach (UnityEngine.Object asset in allAssets)
                {
                    if (asset is Texture2D tex && tex != null)
                    {
                        fallbackTex = tex;
                        break;
                    }
                }
            }

            bool repaired = false;
            foreach (TerrainLayer layer in td.terrainLayers)
            {
                if (layer == null)
                    continue;

                if (layer.diffuseTexture == null && fallbackTex != null)
                {
                    layer.diffuseTexture = fallbackTex;
                    repaired = true;
                }
            }

            if (repaired)
                td.terrainLayers = td.terrainLayers;
        }
    }
}
