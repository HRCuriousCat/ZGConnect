using System.Collections.Generic;
using UnityEngine;
using ZGConnect;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// Facade-only material remap for Spatial Streaming (name-based, no UV remap / ortho roofs).
    /// Bake-time: per-building before combine. Runtime: per combined mesh after instantiate.
    /// </summary>
    public static class SpatialBuildingMaterialApplier
    {
        const string CombinedNamePrefix = "Combined_";

        public static readonly BuildingMaterialApplyStyle FacadeStyle =
            BuildingMaterialApplyStyle.FacadeAndRoofVariants;

        public static CityTileRecord BuildCityTileRecord(
            string tileId,
            Vector3 unityPosition,
            int tileSizeMeters)
        {
            var record = new CityTileRecord
            {
                tileId = tileId,
                unityPosition = unityPosition,
            };

            if (TryParseTileId(tileId, out int left, out int bottom))
            {
                record.left = left;
                record.bottom = bottom;
                record.right = left + tileSizeMeters;
                record.top = bottom + tileSizeMeters;
            }

            return record;
        }

        /// <summary>Remap GLB materials on each building transform before mesh combine (editor bake).</summary>
        public static int ApplyFacadeMaterialsToTileRoot(
            Transform tileRoot,
            string tileId,
            Vector3 unityPosition,
            int tileSizeMeters,
            BuildingSurfaceSettings settings)
        {
            if (tileRoot == null || settings == null)
                return 0;

            CityTileRecord tileRecord = BuildCityTileRecord(tileId, unityPosition, tileSizeMeters);
            var roofCache = new Dictionary<string, Material>();
            int buildings = 0;

            SpatialBuildingHierarchyUtility.ForEachBuildingTransform(tileRoot, building =>
            {
                if (building == null)
                    return;

                BuildingSharedMaterialApplier.ApplyBuilding(
                    building.gameObject,
                    tileRecord,
                    settings,
                    FacadeStyle,
                    roofOrthophotoBasemapId: null,
                    roofOrthophotoTemplate: null,
                    roofMaterialCache: roofCache,
                    useOrthophotoBasemapForRoofs: false);

                buildings++;
            });

            BuildingSharedMaterialApplier.ReleaseCachedRoofMaterials(roofCache);
            return buildings;
        }

        /// <summary>
        /// Facade material for footprint proxy boxes — matches detail subcell variant seeds.
        /// </summary>
        public static bool TryResolveBuildingProxySurface(
            Transform building,
            string tileId,
            BuildingSurfaceSettings settings,
            out Material material,
            out string combinedObjectKey)
        {
            material = null;
            combinedObjectKey = null;

            if (building == null || settings == null)
                return false;

            Material fallback = null;
            string fallbackKey = null;

            foreach (MeshRenderer renderer in building.GetComponentsInChildren<MeshRenderer>(true))
            {
                Material[] sources = renderer.sharedMaterials;
                if (sources == null)
                    continue;

                foreach (Material source in sources)
                {
                    if (source == null)
                        continue;

                    if (!TryResolveMaterialKey(
                            source.name,
                            out string sourceName,
                            out BuildingCategory category,
                            out BuildingSurfaceMaterialType surfaceType))
                    {
                        continue;
                    }

                    string variantSeed = tileId + "|combined|" + sourceName;
                    Material shared = BuildingSharedMaterialApplier.ResolveSharedMaterial(
                        settings,
                        variantSeed,
                        category,
                        surfaceType,
                        useRoofOrtho: false,
                        roofOrtho: null);

                    if (shared == null)
                        continue;

                    if (surfaceType == BuildingSurfaceMaterialType.Facade)
                    {
                        material = shared;
                        combinedObjectKey = sourceName;
                        return true;
                    }

                    if (fallback == null)
                    {
                        fallback = shared;
                        fallbackKey = sourceName;
                    }
                }
            }

            if (fallback != null)
            {
                material = fallback;
                combinedObjectKey = fallbackKey;
                return true;
            }

            material = settings.GetFacadeMaterial(BuildingCategory.House, 0);
            combinedObjectKey = "house";
            return material != null;
        }

        /// <summary>Remap materials on a streamed combined prefab instance (runtime refresh).</summary>
        public static int ApplyFacadeMaterialsToCombinedInstance(
            GameObject instance,
            string tileId,
            Vector3 unityPosition,
            int tileSizeMeters,
            BuildingSurfaceSettings settings)
        {
            if (instance == null || settings == null)
                return 0;

            int slots = 0;

            foreach (MeshRenderer renderer in instance.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (renderer == null)
                    continue;

                slots += RemapRendererMaterials(renderer, tileId, settings);
            }

            return slots;
        }

        static int RemapRendererMaterials(
            MeshRenderer renderer,
            string tileId,
            BuildingSurfaceSettings settings)
        {
            if (TryResolveMaterialKey(renderer.gameObject.name, out string objectSourceName,
                    out BuildingCategory objectCategory, out BuildingSurfaceMaterialType objectSurfaceType))
            {
                string variantSeed = tileId + "|combined|" + objectSourceName;
                Material shared = BuildingSharedMaterialApplier.ResolveSharedMaterial(
                    settings,
                    variantSeed,
                    objectCategory,
                    objectSurfaceType,
                    useRoofOrtho: false,
                    roofOrtho: null);

                if (shared != null && ApplySingleMaterial(renderer, shared))
                    return 1;
            }

            Material[] sources = renderer.sharedMaterials;
            if (sources == null || sources.Length == 0)
                return 0;

            var mapped = new Material[sources.Length];
            int changed = 0;

            for (int i = 0; i < sources.Length; i++)
            {
                Material source = sources[i];
                if (source == null)
                {
                    mapped[i] = null;
                    continue;
                }

                if (!TryResolveMaterialKey(source.name, out string sourceName, out BuildingCategory category,
                        out BuildingSurfaceMaterialType surfaceType))
                {
                    mapped[i] = source;
                    continue;
                }

                string variantSeed = tileId + "|combined|" + sourceName;
                Material shared = BuildingSharedMaterialApplier.ResolveSharedMaterial(
                    settings,
                    variantSeed,
                    category,
                    surfaceType,
                    useRoofOrtho: false,
                    roofOrtho: null);

                Material resolved = shared != null ? shared : source;
                mapped[i] = resolved;
                if (resolved != source)
                    changed++;
            }

            if (changed > 0)
                renderer.sharedMaterials = mapped;

            return changed;
        }

        static bool ApplySingleMaterial(MeshRenderer renderer, Material material)
        {
            if (renderer == null || material == null)
                return false;

            Material[] slots = renderer.sharedMaterials;
            if (slots == null || slots.Length == 0)
                return false;

            bool already = true;
            foreach (Material slot in slots)
            {
                if (slot != material)
                {
                    already = false;
                    break;
                }
            }

            if (already)
                return false;

            var mapped = new Material[slots.Length];
            for (int i = 0; i < slots.Length; i++)
                mapped[i] = material;

            renderer.sharedMaterials = mapped;
            return true;
        }

        static bool TryResolveMaterialKey(
            string materialOrObjectName,
            out string sourceName,
            out BuildingCategory category,
            out BuildingSurfaceMaterialType surfaceType)
        {
            sourceName = null;
            category = BuildingCategory.House;
            surfaceType = BuildingSurfaceMaterialType.Facade;

            if (string.IsNullOrEmpty(materialOrObjectName))
                return false;

            if (materialOrObjectName.StartsWith(CombinedNamePrefix, System.StringComparison.Ordinal))
            {
                sourceName = BuildingSurfaceUtility.NormalizeMaterialName(
                    materialOrObjectName.Substring(CombinedNamePrefix.Length));
            }
            else
            {
                sourceName = BuildingSurfaceUtility.NormalizeMaterialName(materialOrObjectName);
            }

            if (string.IsNullOrEmpty(sourceName))
                return false;

            return BuildingSurfaceUtility.TryResolveCategoryFromMaterialName(sourceName, out category) &&
                   BuildingSurfaceUtility.TryResolveSurfaceTypeFromMaterialName(sourceName, out surfaceType);
        }

        public static bool TryParseTileId(string tileId, out int tileLeft, out int tileBottom)
        {
            tileLeft = 0;
            tileBottom = 0;
            if (string.IsNullOrEmpty(tileId))
                return false;

            string[] parts = tileId.Split('_');
            return parts.Length == 2
                   && int.TryParse(parts[0], out tileLeft)
                   && int.TryParse(parts[1], out tileBottom);
        }
    }
}
