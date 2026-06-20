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
            "Skip GPU-driven opt-in when a spawned root has more renderers than this. " +
            "Prevents thousands of BRG indirect draws when mesh combine did not run.")]
        public int maxRenderersForGpuOptIn = 32;
    }
}
