using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// Marks a streamed tile/building root and prepares its <see cref="MeshRenderer"/> children for
    /// URP GPU Resident Drawer compatibility.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SpatialStreamedMeshRoot : MonoBehaviour
    {
        public struct AttachResult
        {
            public int MeshRendererCount;
            public int EnabledCount;
        }

        static int _attachApplyDepth;

        [SerializeField] SpatialGpuResidentRenderingSettings _settings;
        [SerializeField] bool _applyOnEnable = true;
        [SerializeField] bool _applyRecursively = true;

        public SpatialGpuResidentRenderingSettings Settings
        {
            get => _settings;
            set => _settings = value;
        }

        public SpatialMeshRendererGpuSetup.ApplyResult ApplyGpuFriendlySettings()
        {
            SpatialGpuResidentRenderingSettings settings = ResolveSettings();
            SpatialMeshRendererGpuSetup.ApplyResult result = SpatialMeshRendererGpuSetup.Apply(
                transform,
                _applyRecursively,
                settings != null ? settings.maxRenderersForGpuOptIn : 32);

            if (settings != null && settings.logRendererCounts)
            {
                int gpuOptInLimit = settings.maxRenderersForGpuOptIn;
                bool gpuCapped = gpuOptInLimit > 0 && result.MeshRendererCount > gpuOptInLimit;
                Debug.Log(
                    $"[ZGConnect.Spatial] GPU setup on '{name}': " +
                    $"meshRenderers={result.MeshRendererCount}, prepared={result.EnabledCount}, " +
                    $"skinned={result.SkinnedMeshRendererCount}" +
                    (gpuCapped ? $", gpuOptInCapped({gpuOptInLimit})" : string.Empty));
            }

            return result;
        }

        void OnEnable()
        {
            if (_attachApplyDepth > 0)
                return;

            SpatialGpuResidentRenderingSettings settings = ResolveSettings();
            if (!ShouldApply(settings))
                return;

            ApplyGpuFriendlySettings();
        }

        bool ShouldApply(SpatialGpuResidentRenderingSettings settings)
        {
            if (!_applyOnEnable)
                return false;

            return settings == null || settings.applyToSpawnedTileRoots;
        }

        SpatialGpuResidentRenderingSettings ResolveSettings()
        {
            if (_settings != null)
                return _settings;

            return SpatialGpuResidentDrawerBootstrap.ActiveSettings;
        }

        /// <summary>Attach to a spawned hierarchy and apply GPU-friendly renderer flags.</summary>
        public static AttachResult Attach(
            GameObject root,
            SpatialGpuResidentRenderingSettings settings = null)
        {
            if (root == null)
                return default;

            _attachApplyDepth++;
            try
            {
                var marker = root.GetComponent<SpatialStreamedMeshRoot>();
                if (marker == null)
                    marker = root.AddComponent<SpatialStreamedMeshRoot>();

                if (settings != null)
                    marker._settings = settings;

                SpatialMeshRendererGpuSetup.ApplyResult result = marker.ApplyGpuFriendlySettings();
                return new AttachResult
                {
                    MeshRendererCount = result.MeshRendererCount,
                    EnabledCount = result.EnabledCount,
                };
            }
            finally
            {
                _attachApplyDepth--;
            }
        }
    }
}
