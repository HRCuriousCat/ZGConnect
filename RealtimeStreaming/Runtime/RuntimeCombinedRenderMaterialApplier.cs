using System.Collections.Generic;
using UnityEngine;
using ZGConnect;

namespace ZGConnect.RealtimeStreaming
{
    /// <summary>
    /// Assigns shared facade/roof materials to pack-baked CombinedRender children.
    /// Combined mesh objects are named Combined_{sourceMaterialName} (e.g. Combined_building_facade_grid).
    /// </summary>
    public static class RuntimeCombinedRenderMaterialApplier
    {
        const string CombinedNamePrefix = "Combined_";

        public static void ApplyTile(
            Transform tileRoot,
            string tileId,
            CityTileRecord cityRecord,
            BuildingSurfaceSettings settings,
            BuildingMaterialApplyStyle style,
            string basemapId,
            Material roofTemplate,
            Dictionary<string, Material> roofMaterialCache)
        {
            if (tileRoot == null || settings == null || string.IsNullOrEmpty(tileId))
                return;

            Transform combinedRoot = tileRoot.Find(RuntimeBuildingTilePostProcessor.CombinedRenderRootName);
            if (combinedRoot == null)
                return;

            bool useRoofOrtho = style == BuildingMaterialApplyStyle.RoofOrthophoto;
            Material roofOrtho = useRoofOrtho
                ? BuildingSharedMaterialApplier.ResolveRoofMaterial(
                    cityRecord,
                    basemapId,
                    roofTemplate,
                    roofMaterialCache,
                    useOrthophotoBasemap: true)
                : null;

            for (int i = 0; i < combinedRoot.childCount; i++)
            {
                Transform child = combinedRoot.GetChild(i);
                MeshRenderer renderer = child.GetComponent<MeshRenderer>();
                if (renderer == null)
                    continue;

                if (!TryResolveCombinedMaterialKey(child.name, renderer, out string sourceName,
                        out BuildingCategory category, out BuildingSurfaceMaterialType surfaceType))
                    continue;

                string variantSeed = tileId + "|combined|" + sourceName;
                Material shared = BuildingSharedMaterialApplier.ResolveSharedMaterial(
                    settings,
                    variantSeed,
                    category,
                    surfaceType,
                    useRoofOrtho,
                    roofOrtho);

                if (shared != null)
                    renderer.sharedMaterial = shared;
            }
        }

        static bool TryResolveCombinedMaterialKey(
            string gameObjectName,
            MeshRenderer renderer,
            out string sourceName,
            out BuildingCategory category,
            out BuildingSurfaceMaterialType surfaceType)
        {
            sourceName = null;
            category = BuildingCategory.House;
            surfaceType = BuildingSurfaceMaterialType.Facade;

            if (!string.IsNullOrEmpty(gameObjectName) &&
                gameObjectName.StartsWith(CombinedNamePrefix, System.StringComparison.Ordinal))
            {
                sourceName = BuildingSurfaceUtility.NormalizeMaterialName(
                    gameObjectName.Substring(CombinedNamePrefix.Length));
            }
            else if (renderer.sharedMaterial != null)
            {
                sourceName = BuildingSurfaceUtility.NormalizeMaterialName(renderer.sharedMaterial.name);
            }

            if (string.IsNullOrEmpty(sourceName))
                return false;

            return BuildingSurfaceUtility.TryResolveCategoryFromMaterialName(sourceName, out category) &&
                   BuildingSurfaceUtility.TryResolveSurfaceTypeFromMaterialName(sourceName, out surfaceType);
        }
    }
}
