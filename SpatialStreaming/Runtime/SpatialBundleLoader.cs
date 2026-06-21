using System.Collections;
using System.IO;
using System.Threading.Tasks;
using GLTFast;
using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    public static class SpatialBundleLoader
    {
        public sealed class LoadResult
        {
            public bool Success;
            public string Error;
            public GameObject Prefab;
            public SpatialMeshDetailAsset MeshDetail;
            public AssetBundle Bundle;
            public string BundleFullPath;
            public bool IsMeshDetail => MeshDetail != null;
        }

        public static IEnumerator LoadVisualPrefabAsync(string bundleFullPath, LoadResult into)
        {
            yield return LoadVisualAsync(bundleFullPath, preferMeshDetail: false, into);
        }

        public static IEnumerator LoadVisualAsync(string bundleFullPath, bool preferMeshDetail, LoadResult into)
        {
            into.Success = false;
            into.Error = null;
            into.Prefab = null;
            into.MeshDetail = null;
            into.Bundle = null;
            into.BundleFullPath = bundleFullPath;

            if (string.IsNullOrEmpty(bundleFullPath) || !File.Exists(bundleFullPath))
            {
                into.Error = $"Spatial bundle not found: {bundleFullPath}";
                yield break;
            }

            AssetBundle bundle = null;
            string loadError = null;
            yield return SpatialAssetBundleCache.AcquireAsync(
                bundleFullPath,
                acquired => bundle = acquired,
                error => loadError = error);

            if (bundle == null)
            {
                into.Error = loadError ?? $"Failed to open spatial bundle: {bundleFullPath}";
                yield break;
            }

            string[] assetNames = bundle.GetAllAssetNames();
            if (preferMeshDetail && SpatialMeshDetailRuntime.IsAvailable)
            {
                string detailAssetName = FindMeshDetailAssetName(assetNames);
                if (!string.IsNullOrEmpty(detailAssetName))
                {
                    AssetBundleRequest detailRequest = bundle.LoadAssetAsync<SpatialMeshDetailAsset>(detailAssetName);
                    yield return detailRequest;

                    if (detailRequest.asset is SpatialMeshDetailAsset detail)
                    {
                        into.Success = true;
                        into.MeshDetail = detail;
                        into.Bundle = bundle;
                        into.BundleFullPath = bundleFullPath;
                        yield break;
                    }

                    SpatialMeshDetailRuntime.MarkUnavailable();
                }
            }

            if (TryLoadPrefabFromBundle(bundle, assetNames, bundleFullPath, into))
                yield break;

            if (preferMeshDetail && SpatialMeshDetailRuntime.IsAvailable)
            {
                string detailAssetName = FindMeshDetailAssetName(assetNames);
                if (!string.IsNullOrEmpty(detailAssetName))
                {
                    SpatialAssetBundleCache.Release(bundleFullPath, unloadAllLoadedObjects: true);
                    into.Error = $"Failed to load mesh detail descriptor '{detailAssetName}' from spatial bundle.";
                    yield break;
                }
            }

            string prefabAssetName = FindPrefabAssetName(assetNames);
            if (string.IsNullOrEmpty(prefabAssetName))
            {
                SpatialAssetBundleCache.Release(bundleFullPath, unloadAllLoadedObjects: true);
                into.Error = $"Prefab not found in spatial bundle: {bundleFullPath}";
                yield break;
            }

            AssetBundleRequest assetRequest = bundle.LoadAssetAsync<GameObject>(prefabAssetName);
            yield return assetRequest;

            GameObject prefab = assetRequest.asset as GameObject;
            if (prefab == null)
            {
                SpatialAssetBundleCache.Release(bundleFullPath, unloadAllLoadedObjects: true);
                into.Error = $"Failed to load prefab '{prefabAssetName}' from spatial bundle.";
                yield break;
            }

            into.Success = true;
            into.Prefab = prefab;
            into.Bundle = bundle;
            into.BundleFullPath = bundleFullPath;
        }

        public static void Release(string bundleFullPath, bool unloadAllLoadedObjects = false) =>
            SpatialAssetBundleCache.Release(bundleFullPath, unloadAllLoadedObjects);

        /// <summary>
        /// Unloads the bundle archive after spawn while keeping loaded meshes/materials alive on instances.
        /// Allows the next overlapping spatial bundle to load without "same files already loaded" errors.
        /// </summary>
        public static void ReleaseArchiveAfterSpawn(string bundleFullPath)
        {
            if (string.IsNullOrEmpty(bundleFullPath))
                return;

            SpatialAssetBundleCache.Release(bundleFullPath, unloadAllLoadedObjects: false);
        }

        static bool TryLoadPrefabFromBundle(
            AssetBundle bundle,
            string[] assetNames,
            string bundleFullPath,
            LoadResult into)
        {
            string prefabAssetName = FindPrefabAssetName(assetNames);
            if (string.IsNullOrEmpty(prefabAssetName))
                return false;

            GameObject prefab = bundle.LoadAsset<GameObject>(prefabAssetName);
            if (prefab == null)
                return false;

            into.Success = true;
            into.Prefab = prefab;
            into.Bundle = bundle;
            into.BundleFullPath = bundleFullPath;
            return true;
        }

        static string FindPrefabAssetName(string[] assetNames)
        {
            if (assetNames == null)
                return null;

            foreach (string name in assetNames)
            {
                if (!string.IsNullOrEmpty(name) &&
                    name.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
                    return name;
            }

            return assetNames.Length > 0 ? assetNames[0] : null;
        }

        static string FindMeshDetailAssetName(string[] assetNames)
        {
            if (assetNames == null)
                return null;

            foreach (string name in assetNames)
            {
                if (!string.IsNullOrEmpty(name) &&
                    name.EndsWith("_detail.asset", System.StringComparison.OrdinalIgnoreCase))
                    return name;
            }

            foreach (string name in assetNames)
            {
                if (!string.IsNullOrEmpty(name) &&
                    name.EndsWith(".asset", System.StringComparison.OrdinalIgnoreCase))
                    return name;
            }

            return null;
        }
    }

    public static class SpatialGlbRuntimeLoader
    {
        public static async Task<GameObject> InstantiateGlbAsync(byte[] glbBytes, string tileId, Transform parent)
        {
            if (glbBytes == null || glbBytes.Length == 0)
                return null;

            var import = new GltfImport();
            bool ok = await import.Load(glbBytes);
            if (!ok)
            {
                import.Dispose();
                return null;
            }

            var root = new GameObject($"SpatialSource_{tileId}");
            if (parent != null)
                root.transform.SetParent(parent, false);

            await import.InstantiateMainSceneAsync(root.transform);
            return root;
        }
    }
}
