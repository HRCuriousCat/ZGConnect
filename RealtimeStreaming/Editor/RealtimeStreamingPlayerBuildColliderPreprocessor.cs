using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;
using ZGConnect.Editor;
using ZGConnect.RealtimeStreaming;

namespace ZGConnect.RealtimeStreaming.Editor
{
    /// <summary>
    /// When Player Settings → Bake Collision Meshes is enabled, Unity cooks every convex
    /// MeshCollider in built content (including StreamingAssets building bundles).
    /// Invalid hulls fail the player build — sanitize them before Unity's bake pass.
    /// </summary>
    public sealed class RealtimeStreamingPlayerBuildColliderPreprocessor :
        IPreprocessBuildWithReport,
        IProcessSceneWithReport
    {
        public int callbackOrder => -1000;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (!PlayerSettings.bakeCollisionMeshes)
                return;

            if (report.summary.options.HasFlag(BuildOptions.BuildScriptsOnly))
                return;

            RealtimeStreamingBuildColliderBundleSanitizer.SanitizeStreamingBuildingBundles(
                report.summary.platform,
                showProgress: true);
        }

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (!PlayerSettings.bakeCollisionMeshes)
                return;

            foreach (GameObject root in scene.GetRootGameObjects())
                RuntimeBuildingTilePostProcessor.SanitizeConvexMeshColliders(root.transform);
        }
    }

    public static class RealtimeStreamingBuildColliderBundleSanitizer
    {
        const string StagingAssetRoot = "Assets/ZGConnect/RealtimeStreaming/_player_build_collider_fix";
        const string BundlesFolderToken = "bundles";

        [MenuItem("ZG Connect/Realtime Streaming/Sanitize Building Bundle Colliders (Player Build)")]
        public static void SanitizeFromMenu()
        {
            int fixedCount = SanitizeStreamingBuildingBundles(
                EditorUserBuildSettings.activeBuildTarget,
                showProgress: true);

            EditorUtility.DisplayDialog(
                "ZG Connect",
                fixedCount > 0
                    ? $"Sanitized and rebuilt {fixedCount} building bundle(s) under StreamingAssets."
                    : "No building bundles required collider sanitization.",
                "OK");
        }

        public static int SanitizeStreamingBuildingBundles(BuildTarget buildTarget, bool showProgress)
        {
            string streamingRoot = Path.Combine(Application.streamingAssetsPath, "ZGConnect");
            if (!Directory.Exists(streamingRoot))
                return 0;

            var bundleFiles = new List<string>();
            foreach (string path in Directory.EnumerateFiles(streamingRoot, "*", SearchOption.AllDirectories))
            {
                if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (path.IndexOf($"{BundlesFolderToken}{Path.DirectorySeparatorChar}buildings", StringComparison.OrdinalIgnoreCase) < 0 &&
                    path.IndexOf($"{BundlesFolderToken}/buildings", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                bundleFiles.Add(path);
            }

            if (bundleFiles.Count == 0)
                return 0;

            int fixedCount = 0;
            try
            {
                for (int i = 0; i < bundleFiles.Count; i++)
                {
                    string bundlePath = bundleFiles[i];
                    if (showProgress &&
                        EditorUtility.DisplayCancelableProgressBar(
                            "Sanitizing building bundle colliders",
                            bundlePath,
                            (float)i / bundleFiles.Count))
                    {
                        break;
                    }

                    if (TrySanitizeBuildingBundleFile(bundlePath, buildTarget))
                        fixedCount++;
                }
            }
            finally
            {
                if (showProgress)
                    EditorUtility.ClearProgressBar();

                CleanupStagingFolder();
            }

            if (fixedCount > 0)
            {
                AssetDatabase.Refresh();
                Debug.Log(
                    $"[ZGConnect.Realtime] Sanitized convex colliders in {fixedCount} building bundle(s) for player build.");
            }

            return fixedCount;
        }

        static bool TrySanitizeBuildingBundleFile(string bundleFullPath, BuildTarget buildTarget)
        {
            if (string.IsNullOrEmpty(bundleFullPath) || !File.Exists(bundleFullPath))
                return false;

            if (!TryGetBundleAssetName(bundleFullPath, out string assetBundleName))
                return false;

            var loadResult = new RuntimeBuildingBundleLoader.LoadResult();
            if (!RuntimeBuildingBundleLoader.TryLoadBuildingVisualSync(bundleFullPath, loadResult) ||
                loadResult.Prefab == null ||
                loadResult.Bundle == null)
            {
                return false;
            }

            try
            {
                GameObject physicsPrefab = null;
                if (loadResult.HasSplitPhysicsPrefab)
                {
                    physicsPrefab = RuntimeBuildingBundleLoader.TryLoadBuildingPhysicsSync(
                        loadResult.Bundle,
                        loadResult.PhysicsPrefabAssetName);
                }

                bool visualNeedsFix = PrefabNeedsSanitize(loadResult.Prefab);
                bool physicsNeedsFix = PrefabNeedsSanitize(physicsPrefab);
                if (!visualNeedsFix && !physicsNeedsFix)
                    return false;

                GameObject visualInstance = UnityEngine.Object.Instantiate(loadResult.Prefab);
                visualInstance.name = loadResult.Prefab.name;
                RealtimeStreamingBuildBundlePrepareUtility.PrepareVisualInstance(visualInstance);
                if (visualNeedsFix)
                    RuntimeBuildingTilePostProcessor.SanitizeConvexMeshColliders(visualInstance.transform);

                GameObject physicsInstance = null;
                if (physicsPrefab != null)
                {
                    physicsInstance = UnityEngine.Object.Instantiate(physicsPrefab);
                    physicsInstance.name = physicsPrefab.name;
                    RealtimeStreamingBuildBundlePrepareUtility.PreparePhysicsInstance(physicsInstance);
                    if (physicsNeedsFix)
                        RuntimeBuildingTilePostProcessor.SanitizeConvexMeshColliders(physicsInstance.transform);
                }

                bool rebuilt = TryRebuildBundle(
                    bundleFullPath,
                    assetBundleName,
                    visualInstance,
                    physicsInstance,
                    buildTarget);

                if (visualInstance != null)
                    UnityEngine.Object.DestroyImmediate(visualInstance);
                if (physicsInstance != null)
                    UnityEngine.Object.DestroyImmediate(physicsInstance);

                return rebuilt;
            }
            finally
            {
                RuntimeBuildingBundleLoader.ReleaseEditorBundle(bundleFullPath, loadResult.Bundle);
            }
        }

        static bool PrefabNeedsSanitize(GameObject prefabRoot)
        {
            if (prefabRoot == null)
                return false;

            foreach (MeshCollider meshCollider in prefabRoot.GetComponentsInChildren<MeshCollider>(true))
            {
                if (meshCollider == null || !meshCollider.convex || meshCollider.sharedMesh == null)
                    continue;

                if (!RuntimeBuildingMeshUtility.CanBakeConvexCollider(meshCollider.sharedMesh))
                    return true;
            }

            return false;
        }

        static bool TryRebuildBundle(
            string bundleFullPath,
            string assetBundleName,
            GameObject visualInstance,
            GameObject physicsInstance,
            BuildTarget buildTarget)
        {
            CleanupStagingFolder();
            ZGConnectPathUtils.EnsureAssetFolder(StagingAssetRoot);
            ZGConnectPathUtils.EnsureAssetFolder($"{StagingAssetRoot}/output");

            string visualPrefabPath = $"{StagingAssetRoot}/{visualInstance.name}.prefab";
            if (!RealtimePackBuildingBakeUtility.SaveInstanceAsBakedBuildingPrefab(
                    visualInstance,
                    visualPrefabPath,
                    visualInstance.name))
            {
                Debug.LogWarning(
                    $"[ZGConnect.Realtime] Failed to stage visual prefab for bundle rebuild: {bundleFullPath}");
                return false;
            }

            if (!ValidateStagedPrefabMeshes(visualPrefabPath, bundleFullPath, "visual"))
                return false;

            var assetPaths = new List<string> { visualPrefabPath };
            if (physicsInstance != null)
            {
                string physicsPrefabPath = $"{StagingAssetRoot}/{physicsInstance.name}.prefab";
                if (!RealtimePackBuildingBakeUtility.SaveInstanceAsBakedBuildingPrefab(
                        physicsInstance,
                        physicsPrefabPath,
                        physicsInstance.name))
                {
                    Debug.LogWarning(
                        $"[ZGConnect.Realtime] Failed to stage physics prefab for bundle rebuild: {bundleFullPath}");
                    return false;
                }

                if (!ValidateStagedPrefabMeshes(physicsPrefabPath, bundleFullPath, "physics"))
                    return false;

                assetPaths.Add(physicsPrefabPath);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            string outputFolder = $"{StagingAssetRoot}/output";
            string outputFolderOs = ZGConnectPathUtils.AssetPathToFullPath(outputFolder);
            Directory.CreateDirectory(outputFolderOs);

            bool ok = BuildPipeline.BuildAssetBundles(
                outputFolder,
                new[]
                {
                    new AssetBundleBuild
                    {
                        assetBundleName = assetBundleName,
                        assetNames = assetPaths.ToArray(),
                    },
                },
                RealtimeAssetBundleBuildUtility.PackBuildOptions,
                buildTarget);

            if (!ok)
            {
                Debug.LogWarning($"[ZGConnect.Realtime] BuildPipeline failed rebuilding bundle: {bundleFullPath}");
                return false;
            }

            string builtBundlePath = Path.Combine(outputFolderOs, assetBundleName);
            if (!File.Exists(builtBundlePath))
            {
                Debug.LogWarning($"[ZGConnect.Realtime] Rebuilt bundle not found at: {builtBundlePath}");
                return false;
            }

            File.Copy(builtBundlePath, bundleFullPath, overwrite: true);
            return true;
        }

        static bool ValidateStagedPrefabMeshes(string prefabAssetPath, string bundleFullPath, string role)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath);
            if (prefab == null)
            {
                Debug.LogWarning(
                    $"[ZGConnect.Realtime] Staged {role} prefab could not be loaded: {prefabAssetPath} ({bundleFullPath})");
                return false;
            }

            Transform combinedRoot = prefab.transform.Find(RuntimeBuildingTilePostProcessor.CombinedRenderRootName);
            if (combinedRoot == null)
                return true;

            foreach (MeshFilter meshFilter in combinedRoot.GetComponentsInChildren<MeshFilter>(true))
            {
                if (meshFilter == null || meshFilter.sharedMesh != null)
                    continue;

                Debug.LogWarning(
                    $"[ZGConnect.Realtime] Aborting bundle rebuild — {role} CombinedRender mesh missing on " +
                    $"'{meshFilter.gameObject.name}' in {bundleFullPath}");
                return false;
            }

            return true;
        }

        static bool TryGetBundleAssetName(string bundleFilePath, out string assetBundleName)
        {
            assetBundleName = null;
            if (string.IsNullOrEmpty(bundleFilePath))
                return false;

            string normalized = bundleFilePath.Replace('\\', '/');
            const string marker = "/bundles/";
            int markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
                return false;

            assetBundleName = normalized.Substring(markerIndex + marker.Length);
            return !string.IsNullOrEmpty(assetBundleName);
        }

        static void CleanupStagingFolder()
        {
            if (AssetDatabase.IsValidFolder(StagingAssetRoot))
                AssetDatabase.DeleteAsset(StagingAssetRoot);
        }
    }
}
