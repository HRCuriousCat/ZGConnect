using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using ZGConnect.SpatialStreaming;

namespace ZGConnect.SpatialStreaming.Editor
{
    public static class SpatialGpuResidentDrawerProjectUtility
    {
        public sealed class ApplyResult
        {
            public bool Success;
            public string Message;
            public UniversalRenderPipelineAsset PipelineAsset;
        }

        public const string PcPipelineAssetPath = "Assets/Settings/PC_RPAsset.asset";

        public static UniversalRenderPipelineAsset GetActiveUrpAsset() =>
            GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;

        public static UniversalRenderPipelineAsset LoadPcPipelineAsset() =>
            AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PcPipelineAssetPath);

        public static ApplyResult ApplyToPipelineAsset(
            UniversalRenderPipelineAsset pipeline,
            bool enableDrawer,
            bool enableOcclusionCulling,
            float smallMeshScreenPercentage)
        {
            var result = new ApplyResult { PipelineAsset = pipeline };
            if (pipeline == null)
            {
                result.Message = "URP pipeline asset is null.";
                return result;
            }

            pipeline.gpuResidentDrawerMode = enableDrawer
                ? GPUResidentDrawerMode.InstancedDrawing
                : GPUResidentDrawerMode.Disabled;
            pipeline.gpuResidentDrawerEnableOcclusionCullingInCameras = enableOcclusionCulling;
            pipeline.smallMeshScreenPercentage = Mathf.Clamp(smallMeshScreenPercentage, 0f, 20f);

            EditorUtility.SetDirty(pipeline);
            AssetDatabase.SaveAssets();

            if (enableDrawer)
                IGPUResidentRenderPipeline.ReinitializeGPUResidentDrawer();

            result.Success = true;
            result.Message = enableDrawer
                ? "GPU Resident Drawer enabled (Instanced Drawing)."
                : "GPU Resident Drawer disabled.";
            return result;
        }

        public static ApplyResult EnsureDrawerActive(
            UniversalRenderPipelineAsset pipeline,
            bool enableDrawer,
            bool enableOcclusionCulling,
            float smallMeshScreenPercentage)
        {
            ApplyResult applyResult = ApplyToPipelineAsset(
                pipeline,
                enableDrawer,
                enableOcclusionCulling,
                smallMeshScreenPercentage);

            if (!applyResult.Success || !enableDrawer)
                return applyResult;

            EnsureBrgShaderStrippingKeepAll(out string brgMessage);
            if (!string.IsNullOrEmpty(brgMessage))
                applyResult.Message += " " + brgMessage;

            int upgraded = UpgradeIncompatibleRenderersInProject(out List<string> upgradedPaths);
            if (upgraded > 0)
            {
                applyResult.Message += $" Upgraded {upgraded} renderer(s) to Forward+/Deferred+.";
                IGPUResidentRenderPipeline.ReinitializeGPUResidentDrawer();
            }

            if (!IGPUResidentRenderPipeline.IsGPUResidentDrawerEnabled())
                IGPUResidentRenderPipeline.ReinitializeGPUResidentDrawer();

            SpatialGpuResidentDrawerStatus status = SpatialGpuResidentDrawerStatus.Query();
            if (!status.IsReadyForStreaming)
                applyResult.Message += " " + status.Message;
            else
                applyResult.Message = status.Message;

            applyResult.Success = status.IsReadyForStreaming;
            return applyResult;
        }

        public static int UpgradeIncompatibleRenderersInProject(out List<string> upgradedAssetPaths)
        {
            upgradedAssetPaths = new List<string>();
            var seen = new HashSet<string>();

            foreach (UniversalRenderPipelineAsset pipeline in EnumerateProjectPipelineAssets())
            {
                foreach (UniversalRendererData renderer in EnumerateIncompatibleRenderers(pipeline))
                {
                    string path = AssetDatabase.GetAssetPath(renderer);
                    if (string.IsNullOrEmpty(path) || !seen.Add(path))
                        continue;

                    RenderingMode target = renderer.renderingMode switch
                    {
                        RenderingMode.Deferred => RenderingMode.DeferredPlus,
                        RenderingMode.Forward => RenderingMode.ForwardPlus,
                        _ => RenderingMode.ForwardPlus,
                    };

                    if (renderer.renderingMode == target)
                        continue;

                    Undo.RecordObject(renderer, "Upgrade URP renderer for GPU Resident Drawer");
                    renderer.renderingMode = target;
                    EditorUtility.SetDirty(renderer);
                    upgradedAssetPaths.Add(path);
                }
            }

            if (upgradedAssetPaths.Count > 0)
                AssetDatabase.SaveAssets();

            return upgradedAssetPaths.Count;
        }

        static IEnumerable<UniversalRenderPipelineAsset> EnumerateProjectPipelineAssets()
        {
            var seen = new HashSet<UniversalRenderPipelineAsset>();
            UniversalRenderPipelineAsset active = GetActiveUrpAsset();
            if (active != null && seen.Add(active))
                yield return active;

            UniversalRenderPipelineAsset pc = LoadPcPipelineAsset();
            if (pc != null && seen.Add(pc))
                yield return pc;

            string[] guids = AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
                if (pipeline != null && seen.Add(pipeline))
                    yield return pipeline;
            }
        }

        static IEnumerable<UniversalRendererData> EnumerateIncompatibleRenderers(
            UniversalRenderPipelineAsset pipeline)
        {
            if (pipeline == null)
                yield break;

            ScriptableRendererData[] renderers = pipeline.rendererDataList.ToArray();
            foreach (ScriptableRendererData rendererData in renderers)
            {
                if (rendererData is UniversalRendererData universalRenderer &&
                    !universalRenderer.usesClusterLightLoop)
                {
                    yield return universalRenderer;
                }
            }
        }

        public static bool EnsureBrgShaderStrippingKeepAll(out string message)
        {
            message = string.Empty;

            if (EditorGraphicsSettings.batchRendererGroupShaderStrippingMode ==
                BatchRendererGroupStrippingMode.KeepAll)
            {
                return true;
            }

            Object graphicsSettings = Unsupported.GetSerializedAssetInterfaceSingleton("GraphicsSettings");
            if (graphicsSettings == null)
            {
                message =
                    "Open Project Settings > Graphics and set BatchRendererGroup Variants to Keep All.";
                return false;
            }

            SerializedObject graphicsSettingsObject = new SerializedObject(graphicsSettings);
            SerializedProperty brgStripping = graphicsSettingsObject.FindProperty("m_BrgStripping");
            if (brgStripping == null)
            {
                message =
                    "Open Project Settings > Graphics and set BatchRendererGroup Variants to Keep All.";
                return false;
            }

            int keepAll = (int)BatchRendererGroupStrippingMode.KeepAll;
            if (brgStripping.intValue == keepAll)
                return true;

            brgStripping.intValue = keepAll;
            graphicsSettingsObject.ApplyModifiedPropertiesWithoutUndo();

            message = "Set Graphics Settings > BatchRendererGroup Variants to Keep All.";
            return true;
        }

        public static List<string> ValidateRendererCompatibility(UniversalRenderPipelineAsset pipeline)
        {
            var issues = new List<string>();
            if (pipeline == null)
            {
                issues.Add("No UniversalRenderPipelineAsset assigned as the active render pipeline.");
                return issues;
            }

            foreach (SpatialRendererCompatibilityIssue issue in
                     SpatialGpuResidentRendererCompatibility.FindIncompatibleRenderers(pipeline))
            {
                string assetPath = string.Empty;
                UniversalRendererData rendererData = FindRendererData(pipeline, issue.RendererName);
                if (rendererData != null)
                    assetPath = AssetDatabase.GetAssetPath(rendererData);

                string location = string.IsNullOrEmpty(assetPath) ? issue.RendererName : $"{issue.RendererName} ({assetPath})";
                issues.Add(
                    $"{location} uses {issue.RenderingMode}. GPU Resident Drawer needs Forward+ or Deferred+. " +
                    $"Change: {issue.DescribeRequiredMode()}.");
            }

            return issues;
        }

        public static List<string> FindAllIncompatibleRendererAssetsInProject()
        {
            var issues = new List<string>();
            string[] pipelineGuids = AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset");
            foreach (string guid in pipelineGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
                if (pipeline == null)
                    continue;

                foreach (SpatialRendererCompatibilityIssue issue in
                         SpatialGpuResidentRendererCompatibility.FindIncompatibleRenderers(pipeline))
                {
                    UniversalRendererData rendererData = FindRendererData(pipeline, issue.RendererName);
                    string rendererPath = rendererData != null
                        ? AssetDatabase.GetAssetPath(rendererData)
                        : issue.RendererName;

                    issues.Add($"{rendererPath} — {issue.RenderingMode} (pipeline: {pipeline.name})");
                }
            }

            return issues;
        }

        static UniversalRendererData FindRendererData(
            UniversalRenderPipelineAsset pipeline,
            string rendererName)
        {
            if (pipeline == null || string.IsNullOrEmpty(rendererName))
                return null;

            foreach (ScriptableRendererData rendererData in pipeline.rendererDataList)
            {
                if (rendererData is UniversalRendererData universalRenderer &&
                    universalRenderer.name == rendererName)
                {
                    return universalRenderer;
                }
            }

            return null;
        }

        public static void EnsureBootstrapInOpenScenes()
        {
            foreach (UnityEngine.SceneManagement.Scene scene in EditorHelpers.GetLoadedScenes())
            {
                if (!scene.isLoaded)
                    continue;

                if (Object.FindObjectsByType<SpatialGpuResidentDrawerBootstrap>(
                        FindObjectsInactive.Include).Length > 0)
                {
                    return;
                }
            }

            var bootstrapGo = new GameObject("SpatialStreaming");
            bootstrapGo.AddComponent<SpatialGpuResidentDrawerBootstrap>();
            Undo.RegisterCreatedObjectUndo(bootstrapGo, "Create Spatial Streaming Bootstrap");
            Selection.activeGameObject = bootstrapGo;
        }

        static class EditorHelpers
        {
            public static IEnumerable<UnityEngine.SceneManagement.Scene> GetLoadedScenes()
            {
                for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                    yield return UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
            }
        }
    }
}
