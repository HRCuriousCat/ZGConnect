using System.Collections;
using System.IO;
using UnityEngine;

namespace ZGConnect.RealtimeStreaming
{
    public static class RuntimeBuildingBundleLoader
    {
        public sealed class LoadResult
        {
            public bool Success;
            public string Error;
            public GameObject Prefab;
            public AssetBundle Bundle;
            public string BundleFullPath;
            public string PhysicsPrefabAssetName;
            public bool HasSplitPhysicsPrefab;
        }

        public static bool TryLoadBuildingVisualSync(string bundleFullPath, LoadResult into)
        {
            into.Success = false;
            into.Error = null;
            into.Prefab = null;
            into.Bundle = null;
            into.BundleFullPath = bundleFullPath;
            into.PhysicsPrefabAssetName = null;
            into.HasSplitPhysicsPrefab = false;

            if (string.IsNullOrEmpty(bundleFullPath) || !File.Exists(bundleFullPath))
            {
                into.Error = $"Building bundle not found: {bundleFullPath}";
                return false;
            }

            AssetBundle bundle = AssetBundle.LoadFromFile(bundleFullPath);
            if (bundle == null)
            {
                into.Error = $"Failed to open building bundle: {bundleFullPath}";
                return false;
            }

            string[] assetNames = bundle.GetAllAssetNames();
            string visualAssetName = FindVisualPrefabAssetName(assetNames);
            if (string.IsNullOrEmpty(visualAssetName))
            {
                bundle.Unload(unloadAllLoadedObjects: true);
                into.Error = $"Baked building visual prefab not found in bundle: {bundleFullPath}";
                return false;
            }

            into.PhysicsPrefabAssetName = FindPhysicsPrefabAssetName(assetNames);
            into.HasSplitPhysicsPrefab = !string.IsNullOrEmpty(into.PhysicsPrefabAssetName);

            GameObject prefab = bundle.LoadAsset<GameObject>(visualAssetName);
            if (prefab == null)
            {
                bundle.Unload(unloadAllLoadedObjects: true);
                into.Error = $"Failed to load visual prefab '{visualAssetName}' from bundle: {bundleFullPath}";
                return false;
            }

            into.Success = true;
            into.Prefab = prefab;
            into.Bundle = bundle;
            into.BundleFullPath = bundleFullPath;
            return true;
        }

        public static GameObject TryLoadBuildingPhysicsSync(AssetBundle bundle, string physicsPrefabAssetName)
        {
            if (bundle == null || string.IsNullOrEmpty(physicsPrefabAssetName))
                return null;

            return bundle.LoadAsset<GameObject>(physicsPrefabAssetName);
        }

        public static void ReleaseEditorBundle(
            string bundleFullPath,
            AssetBundle bundle,
            bool unloadAllLoadedObjects = true)
        {
            if (bundle != null)
                bundle.Unload(unloadAllLoadedObjects);
        }

        public static IEnumerator LoadBuildingVisualPrefabAsync(string bundleFullPath, LoadResult into)
        {
            into.Success = false;
            into.Error = null;
            into.Prefab = null;
            into.Bundle = null;
            into.BundleFullPath = bundleFullPath;
            into.PhysicsPrefabAssetName = null;
            into.HasSplitPhysicsPrefab = false;

            if (string.IsNullOrEmpty(bundleFullPath) || !File.Exists(bundleFullPath))
            {
                into.Error = $"Building bundle not found: {bundleFullPath}";
                yield break;
            }

            AssetBundle bundle = null;
            string loadError = null;
            yield return RuntimeAssetBundleCache.AcquireAsync(
                bundleFullPath,
                acquired => bundle = acquired,
                error => loadError = error);

            if (bundle == null)
            {
                into.Error = loadError ?? $"Failed to open building bundle: {bundleFullPath}";
                yield break;
            }

            string[] assetNames = bundle.GetAllAssetNames();
            string visualAssetName = FindVisualPrefabAssetName(assetNames);
            if (string.IsNullOrEmpty(visualAssetName))
            {
                RuntimeAssetBundleCache.Release(bundleFullPath, unloadAllLoadedObjects: true);
                into.Error = $"Baked building visual prefab not found in bundle: {bundleFullPath}";
                yield break;
            }

            into.PhysicsPrefabAssetName = FindPhysicsPrefabAssetName(assetNames);
            into.HasSplitPhysicsPrefab = !string.IsNullOrEmpty(into.PhysicsPrefabAssetName);

            AssetBundleRequest assetRequest = bundle.LoadAssetAsync<GameObject>(visualAssetName);
            yield return assetRequest;

            GameObject prefab = assetRequest.asset as GameObject;
            if (prefab == null)
            {
                RuntimeAssetBundleCache.Release(bundleFullPath, unloadAllLoadedObjects: true);
                into.Error = $"Failed to load visual prefab '{visualAssetName}' from bundle: {bundleFullPath}";
                yield break;
            }

            into.Success = true;
            into.Prefab = prefab;
            into.Bundle = bundle;
            into.BundleFullPath = bundleFullPath;
        }

        public static IEnumerator LoadBuildingPhysicsPrefabAsync(
            AssetBundle bundle,
            string physicsPrefabAssetName,
            LoadResult into)
        {
            into.Success = false;
            into.Error = null;
            into.Prefab = null;
            into.Bundle = bundle;
            into.PhysicsPrefabAssetName = physicsPrefabAssetName;
            into.HasSplitPhysicsPrefab = true;

            if (bundle == null)
            {
                into.Error = "Building bundle is null.";
                yield break;
            }

            if (string.IsNullOrEmpty(physicsPrefabAssetName))
            {
                into.Error = "Physics prefab asset name is missing.";
                yield break;
            }

            AssetBundleRequest assetRequest = bundle.LoadAssetAsync<GameObject>(physicsPrefabAssetName);
            yield return assetRequest;

            GameObject prefab = assetRequest.asset as GameObject;
            if (prefab == null)
            {
                into.Error = $"Failed to load physics prefab '{physicsPrefabAssetName}' from bundle.";
                yield break;
            }

            into.Success = true;
            into.Prefab = prefab;
        }

        static string FindVisualPrefabAssetName(string[] assetNames)
        {
            if (assetNames == null)
                return null;

            string fallback = null;
            foreach (string assetName in assetNames)
            {
                if (string.IsNullOrEmpty(assetName) ||
                    !assetName.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string fileName = Path.GetFileNameWithoutExtension(assetName);
                if (IsPhysicsPrefabFileName(fileName))
                    continue;

                fallback ??= assetName;
            }

            return fallback;
        }

        static string FindPhysicsPrefabAssetName(string[] assetNames)
        {
            if (assetNames == null)
                return null;

            string visual = FindVisualPrefabAssetName(assetNames);
            foreach (string assetName in assetNames)
            {
                if (string.IsNullOrEmpty(assetName) ||
                    !assetName.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string fileName = Path.GetFileNameWithoutExtension(assetName);
                if (IsPhysicsPrefabFileName(fileName))
                    return assetName;
            }

            // Fallback: exactly two prefabs — the non-visual one is physics.
            if (string.IsNullOrEmpty(visual))
                return null;

            int prefabCount = 0;
            string other = null;
            foreach (string assetName in assetNames)
            {
                if (string.IsNullOrEmpty(assetName) ||
                    !assetName.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                prefabCount++;
                if (!string.Equals(assetName, visual, System.StringComparison.OrdinalIgnoreCase))
                    other = assetName;
            }

            return prefabCount == 2 ? other : null;
        }

        static bool IsPhysicsPrefabFileName(string fileNameWithoutExtension)
        {
            if (string.IsNullOrEmpty(fileNameWithoutExtension))
                return false;

            return fileNameWithoutExtension.EndsWith("_Physics", System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
