using UnityEngine;
using UnityEngine.Rendering;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// Project-level defaults for GPU Resident Drawer usage in the Spatial Streaming workflow.
    /// </summary>
    [CreateAssetMenu(
        fileName = "SpatialGpuResidentRenderingSettings",
        menuName = "ZG Connect/Spatial Streaming/GPU Resident Rendering Settings")]
    public sealed class SpatialGpuResidentRenderingSettings : ScriptableObject
    {
        [Tooltip("When enabled, spawned tile roots get GPU Resident Drawer compatibility fixes.")]
        public bool applyToSpawnedTileRoots = true;

        [Tooltip("Walk the full hierarchy under each streamed tile root.")]
        public bool applyRecursively = true;

        [Tooltip("Log a warning at startup when the active URP asset does not have the drawer enabled.")]
        public bool warnWhenDrawerDisabled = true;

        [Tooltip("Log renderer counts after applying GPU-friendly settings on tile roots.")]
        public bool logRendererCounts;

        [Tooltip(
            "Max mesh renderers per spawned root that opt into GPU-driven drawing. " +
            "0 = no limit. When set, the first N renderers in hierarchy order are opted in.")]
        public int maxRenderersForGpuOptIn = 32;
    }
}
