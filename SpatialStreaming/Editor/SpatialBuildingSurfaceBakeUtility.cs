using System.Collections.Generic;
using UnityEngine;
using ZGConnect;

namespace ZGConnect.SpatialStreaming.Editor
{
    /// <summary>
    /// Applies shared facade/roof materials from prepared GLB slot names before spatial combine.
    /// </summary>
    public static class SpatialBuildingSurfaceBakeUtility
    {
        public static int PrepareTileBuildingsForSurfaceMaterials(
            Transform tileRoot,
            string tileId,
            Vector3 tileUnityPosition,
            int tileSizeMeters,
            BuildingsMetadataJson meta,
            BuildingSurfaceSettings settings,
            bool verbose = false)
        {
            if (tileRoot == null || settings == null)
                return 0;

            CityTileRecord tileRecord = SpatialBuildingMaterialApplier.BuildCityTileRecord(
                tileId,
                tileUnityPosition,
                tileSizeMeters);
            var roofCache = new Dictionary<string, Material>();
            int processed;

            if (BuildingMaterialSlotApplier.TileMetadataHasSlotKeys(meta))
            {
                BuildingMaterialSlotApplier.ApplyTileFromMetadata(
                    tileRoot.gameObject,
                    meta,
                    tileRecord,
                    settings,
                    SpatialBuildingMaterialApplier.FacadeStyle,
                    roofOrthophotoBasemapId: null,
                    roofOrthophotoTemplate: null,
                    roofMaterialCache: roofCache,
                    useOrthophotoBasemapForRoofs: false);

                processed = 0;
                SpatialBuildingHierarchyUtility.ForEachBuildingTransform(tileRoot, _ => processed++);
            }
            else
            {
                processed = SpatialBuildingMaterialApplier.ApplyFacadeMaterialsToTileRoot(
                    tileRoot,
                    tileId,
                    tileUnityPosition,
                    tileSizeMeters,
                    settings);
            }

            BuildingSharedMaterialApplier.ReleaseCachedRoofMaterials(roofCache);

            if (verbose && processed > 0)
            {
                SpatialBakeVerboseLog.Global(
                    "surfaces",
                    $"tile={tileId} buildings={processed} mode={(BuildingMaterialSlotApplier.TileMetadataHasSlotKeys(meta) ? "metadata-slots" : "glb-names")}");
            }

            return processed;
        }
    }
}
