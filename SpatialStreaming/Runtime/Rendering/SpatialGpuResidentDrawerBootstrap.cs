using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// Scene bootstrap for Spatial Streaming. Verifies GPU Resident Drawer at play start and
    /// exposes shared rendering settings for streamed tile roots.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-500)]
    public sealed class SpatialGpuResidentDrawerBootstrap : MonoBehaviour
    {
        [SerializeField] SpatialGpuResidentRenderingSettings _settings;
        [SerializeField] bool _logStatusOnStart = true;

        public static SpatialGpuResidentRenderingSettings ActiveSettings { get; private set; }

        public SpatialGpuResidentDrawerStatus LastStatus { get; private set; }

        void Awake()
        {
            ActiveSettings = _settings;
            TryActivateDrawerIfConfigured();
            LastStatus = SpatialGpuResidentDrawerStatus.Query();
            LogStartupStatus();
        }

        void TryActivateDrawerIfConfigured()
        {
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null ||
                pipeline.gpuResidentDrawerMode == GPUResidentDrawerMode.Disabled)
            {
                return;
            }

            if (IGPUResidentRenderPipeline.IsGPUResidentDrawerEnabled())
                return;

            IGPUResidentRenderPipeline.ReinitializeGPUResidentDrawer();
        }

        void LogStartupStatus()
        {
            if (!_logStatusOnStart)
                return;

            if (LastStatus.IsReadyForStreaming)
            {
                Debug.Log($"[ZGConnect.Spatial] {LastStatus.Message}");
                return;
            }

            bool shouldWarn = _settings == null || _settings.warnWhenDrawerDisabled;
            if (shouldWarn)
                Debug.LogWarning($"[ZGConnect.Spatial] {LastStatus.Message}");
        }

        void OnDestroy()
        {
            if (ActiveSettings == _settings)
                ActiveSettings = null;
        }

        [ContextMenu("Refresh GPU Resident Drawer Status")]
        void RefreshStatusContextMenu()
        {
            LastStatus = SpatialGpuResidentDrawerStatus.Query();
            Debug.Log($"[ZGConnect.Spatial] {LastStatus.Message}");
        }
    }
}
