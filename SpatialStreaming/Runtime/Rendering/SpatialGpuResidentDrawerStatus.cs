using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ZGConnect.SpatialStreaming
{
    public readonly struct SpatialGpuResidentDrawerStatus
    {
        public readonly bool DrawerEnabled;
        public readonly bool ProjectSupported;
        public readonly bool PipelineIsUrp;
        public readonly GPUResidentDrawerMode DrawerMode;
        public readonly bool OcclusionCullingInCameras;
        public readonly float SmallMeshScreenPercentage;
        public readonly string Message;

        public bool IsReadyForStreaming =>
            DrawerEnabled && ProjectSupported && PipelineIsUrp;

        public static SpatialGpuResidentDrawerStatus Query()
        {
            bool projectSupported = IGPUResidentRenderPipeline.IsGPUResidentDrawerSupportedByProjectConfiguration();
            bool drawerEnabled = IGPUResidentRenderPipeline.IsGPUResidentDrawerEnabled();

            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            bool pipelineIsUrp = pipeline != null;

            GPUResidentDrawerMode mode = GPUResidentDrawerMode.Disabled;
            bool occlusion = false;
            float smallMesh = 0f;

            if (pipeline != null)
            {
                mode = pipeline.gpuResidentDrawerMode;
                occlusion = pipeline.gpuResidentDrawerEnableOcclusionCullingInCameras;
                smallMesh = pipeline.smallMeshScreenPercentage;
            }

            string message = BuildMessage(drawerEnabled, projectSupported, pipelineIsUrp, mode, occlusion, pipeline);
            return new SpatialGpuResidentDrawerStatus(
                drawerEnabled,
                projectSupported,
                pipelineIsUrp,
                mode,
                occlusion,
                smallMesh,
                message);
        }

        SpatialGpuResidentDrawerStatus(
            bool drawerEnabled,
            bool projectSupported,
            bool pipelineIsUrp,
            GPUResidentDrawerMode drawerMode,
            bool occlusionCullingInCameras,
            float smallMeshScreenPercentage,
            string message)
        {
            DrawerEnabled = drawerEnabled;
            ProjectSupported = projectSupported;
            PipelineIsUrp = pipelineIsUrp;
            DrawerMode = drawerMode;
            OcclusionCullingInCameras = occlusionCullingInCameras;
            SmallMeshScreenPercentage = smallMeshScreenPercentage;
            Message = message;
        }

        static string BuildMessage(
            bool drawerEnabled,
            bool projectSupported,
            bool pipelineIsUrp,
            GPUResidentDrawerMode mode,
            bool occlusion,
            UniversalRenderPipelineAsset pipeline)
        {
            if (!pipelineIsUrp)
                return "Active render pipeline is not a UniversalRenderPipelineAsset.";

            if (!projectSupported)
                return DescribeProjectConfigurationBlocker();

            if (mode == GPUResidentDrawerMode.Disabled)
                return "GPU Resident Drawer is disabled on the active URP asset. Enable Instanced Drawing in Spatial Streaming window or URP asset.";

            if (pipeline is IGPUResidentRenderPipeline grdPipeline &&
                !grdPipeline.IsGPUResidentDrawerSupportedBySRP(out string srpMessage, out _))
            {
                return string.IsNullOrEmpty(srpMessage)
                    ? "GPU Resident Drawer is configured but the active URP asset rejected it. " +
                      "Set every Universal Renderer Data asset to Forward+ or Deferred+."
                    : srpMessage;
            }

            if (!drawerEnabled)
            {
                string incompatible = FormatIncompatibleRenderers(pipeline);
                if (!string.IsNullOrEmpty(incompatible))
                {
                    return "GPU Resident Drawer is configured but not active. Incompatible renderer(s): " +
                           incompatible + ". Open each Universal Renderer Data asset and set Rendering Path to Forward+ or Deferred+.";
                }

                return "GPU Resident Drawer is configured but not active yet. " +
                       "Use Apply Project Settings, then Reinitialize Drawer (Spatial Streaming window), or restart Play mode.";
            }

            return occlusion
                ? "GPU Resident Drawer and GPU occlusion culling are active."
                : "GPU Resident Drawer is active. GPU occlusion culling is off on the URP asset.";
        }

#if UNITY_EDITOR
        static string DescribeProjectConfigurationBlocker()
        {
            if (Application.platform == RuntimePlatform.VisionOS)
                return "GPU Resident Drawer is not supported on VisionOS.";

            if (BatchRendererGroup.BufferTarget != BatchBufferTarget.RawBuffer)
            {
                return "GPU Resident Drawer requires BatchRendererGroup RawBuffer support on this platform/graphics API.";
            }

            return "GPU Resident Drawer is not supported by the current project configuration. " +
                   "Open Project Settings > Graphics and set BatchRendererGroup Variants to Keep All.";
        }
#else
        static string DescribeProjectConfigurationBlocker() =>
            "GPU Resident Drawer is not supported by the current project configuration (platform, BRG stripping, or graphics API).";
#endif

        static string FormatIncompatibleRenderers(UniversalRenderPipelineAsset pipeline)
        {
            if (pipeline == null)
                return string.Empty;

            return SpatialGpuResidentRendererCompatibility.FormatIssueList(
                SpatialGpuResidentRendererCompatibility.FindIncompatibleRenderers(pipeline));
        }
    }
}
