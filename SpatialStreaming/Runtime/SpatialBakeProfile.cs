using UnityEngine;
using ZGConnect;

namespace ZGConnect.SpatialStreaming
{
    public enum SpatialDetailBundleMode
    {
        MeshAssets = 0,
        Prefab = 1,
    }

    [CreateAssetMenu(fileName = "SpatialBakeProfile", menuName = "ZG Connect/Spatial Streaming/Bake Profile")]
    public sealed class SpatialBakeProfile : ScriptableObject
    {
        [Header("Subcell split")]
        [Tooltip("Subcell edge length in meters (e.g. 250 → 4×4 grid per 1 km tile).")]
        public int subcellSizeMeters = 250;

        [Tooltip("When a tile has fewer buildings than this, bake one coarse bundle for the whole tile.")]
        public int minBuildingsForSubcellSplit = 80;

        [Tooltip("Maximum subcells per tile edge (safety cap).")]
        public int maxSubcellsPerEdge = 8;

        [Header("Mesh combine")]
        public bool combineMeshesPerMaterial = true;

        [Tooltip("When set, all subcell renderers use this material before combine (best draw-call count).")]
        public Material combineMaterialOverride;

        [Tooltip(
            "Merge glTF material instances that share the same shader before combine. " +
            "Disable only if you need per-building materials.")]
        public bool deduplicateMaterialsByShader = true;

        [Header("Materials (facade remap by GLB material name)")]
        [Tooltip("When set, building materials are remapped from BuildingSurfaceSettings before combine.")]
        public BuildingSurfaceSettings buildingSurfaceSettings;

        [Header("Footprint proxy / HLOD")]
        [Tooltip("Bake floor-aligned footprint extrusions per tile (load-first placeholders).")]
        public bool bakeFootprintProxies = true;

        [Tooltip("When subcells are used, bake one proxy bundle per subcell (recommended).")]
        public bool bakeSubcellFootprintProxies = true;

        [Tooltip("Merge tile proxies into 2×2 and 4×4 supertile bundles.")]
        public bool bakeHlodSupertiles = true;

        [Tooltip("Fallback when BuildingSurfaceSettings is not assigned. Ignored when settings are set.")]
        public Material proxyMaterialOverride;

        [Header("Asset bundles")]
        public bool uncompressedBundles;

        [Tooltip("Detail subcell bundle payload. MeshAssets skips prefab deserialization at runtime.")]
        public SpatialDetailBundleMode detailBundleMode = SpatialDetailBundleMode.MeshAssets;

        [Header("Bake scope")]
        [Tooltip("When empty, all tiles with source GLBs from parent manifest are baked.")]
        public string[] selectedTileIds = System.Array.Empty<string>();

        [Header("Debug")]
        [Tooltip("Log per-tile bake steps to the Console (warnings always include detail).")]
        public bool verboseBakeLogging = true;

        public string GetSourceFolder() => SpatialStreamingPaths.BuildingMeshesSourceFolder;

        public int ComputeFingerprint()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + subcellSizeMeters;
                hash = hash * 31 + minBuildingsForSubcellSplit;
                hash = hash * 31 + maxSubcellsPerEdge;
                hash = hash * 31 + (combineMeshesPerMaterial ? 1 : 0);
                hash = hash * 31 + (deduplicateMaterialsByShader ? 1 : 0);
                hash = hash * 31 + (combineMaterialOverride != null
                    ? combineMaterialOverride.GetEntityId().GetHashCode()
                    : 0);
                hash = hash * 31 + (buildingSurfaceSettings != null
                    ? buildingSurfaceSettings.GetEntityId().GetHashCode()
                    : 0);
                hash = hash * 31 + (bakeFootprintProxies ? 1 : 0);
                hash = hash * 31 + (bakeSubcellFootprintProxies ? 1 : 0);
                hash = hash * 31 + (bakeHlodSupertiles ? 1 : 0);
                hash = hash * 31 + (proxyMaterialOverride != null
                    ? proxyMaterialOverride.GetEntityId().GetHashCode()
                    : 0);
                hash = hash * 31 + (uncompressedBundles ? 1 : 0);
                hash = hash * 31 + (int)detailBundleMode;
                return hash;
            }
        }

        public string ComputeFingerprintString() => $"sp-{ComputeFingerprint():x8}";
    }
}
