using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// Prepares streamed <see cref="MeshRenderer"/> instances for URP GPU Resident Drawer.
    /// Uses reflection for <c>allowGPUDrivenRendering</c> because it is not exposed on the
    /// public <see cref="MeshRenderer"/> scripting surface in all Unity 6.x builds.
    /// </summary>
    public static class SpatialMeshRendererGpuSetup
    {
        const BindingFlags GpuDrivenPropertyFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        static readonly PropertyInfo AllowGpuDrivenRenderingProperty =
            typeof(MeshRenderer).GetProperty("allowGPUDrivenRendering", GpuDrivenPropertyFlags);

        public struct ApplyResult
        {
            public int MeshRendererCount;
            public int EnabledCount;
            public int SkinnedMeshRendererCount;
        }

        public static ApplyResult Apply(Transform root, bool recursive = true, int maxRenderersForGpuOptIn = 0)
        {
            var result = new ApplyResult();
            if (root == null)
                return result;

            if (recursive)
            {
                var meshRenderers = root.GetComponentsInChildren<MeshRenderer>(true);
                result.MeshRendererCount = meshRenderers.Length;
                int gpuOptInBudget = maxRenderersForGpuOptIn <= 0
                    ? int.MaxValue
                    : maxRenderersForGpuOptIn;

                foreach (MeshRenderer renderer in meshRenderers)
                {
                    bool allowGpuOptIn = gpuOptInBudget > 0;
                    if (TryPrepareRenderer(renderer, allowGpuOptIn))
                        result.EnabledCount++;

                    if (allowGpuOptIn && IsGpuDrivenOptedIn(renderer))
                        gpuOptInBudget--;
                }

                result.SkinnedMeshRendererCount = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;
            }
            else
            {
                if (root.TryGetComponent(out MeshRenderer renderer) &&
                    TryPrepareRenderer(renderer, allowGpuOptIn: true))
                {
                    result.MeshRendererCount = 1;
                    result.EnabledCount = 1;
                }

                if (root.TryGetComponent(out SkinnedMeshRenderer _))
                    result.SkinnedMeshRendererCount = 1;
            }

            return result;
        }

        static bool TryPrepareRenderer(MeshRenderer renderer, bool allowGpuOptIn)
        {
            if (renderer == null)
                return false;

            bool changed = ApplyCompatibilityFixes(renderer);
            if (allowGpuOptIn)
                changed |= TryOptInGpuDrivenRendering(renderer);
            return changed || (allowGpuOptIn && IsGpuDrivenOptedIn(renderer));
        }

        static bool ApplyCompatibilityFixes(MeshRenderer renderer)
        {
            bool changed = false;

            if (renderer.HasPropertyBlock())
            {
                renderer.SetPropertyBlock(null);
                changed = true;
            }

            if (renderer.lightProbeUsage == LightProbeUsage.UseProxyVolume)
            {
                renderer.lightProbeUsage = LightProbeUsage.Off;
                changed = true;
            }

            return changed;
        }

        static bool TryOptInGpuDrivenRendering(MeshRenderer renderer)
        {
            if (AllowGpuDrivenRenderingProperty == null)
                return false;

            if (AllowGpuDrivenRenderingProperty.GetValue(renderer) is bool alreadyEnabled && alreadyEnabled)
                return false;

            AllowGpuDrivenRenderingProperty.SetValue(renderer, true);
            return true;
        }

        static bool IsGpuDrivenOptedIn(MeshRenderer renderer)
        {
            if (AllowGpuDrivenRenderingProperty == null)
                return true;

            return AllowGpuDrivenRenderingProperty.GetValue(renderer) is bool optedIn && optedIn;
        }
    }
}
