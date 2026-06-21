using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using ZGConnect;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// Facade/roof material remap for Spatial Streaming (GLB slot names, no UV remesh / ortho roofs).
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
        /// Facade + roof materials for footprint proxy boxes — follows GLB slot naming on each building.
        /// </summary>
        public static bool TryResolveBuildingProxyMaterials(
            Transform building,
            string tileId,
            BuildingSurfaceSettings settings,
            out Material facadeMaterial,
            out Material roofMaterial,
            out string combinedObjectKey)
        {
            facadeMaterial = null;
            roofMaterial = null;
            combinedObjectKey = null;

            if (building == null || settings == null)
                return false;

            Material slopedRoof = null;
            string buildingId = BuildingSurfaceUtility.ExtractBuildingIdFromObjectName(building.name);
            string variantSeed = BuildingSurfaceUtility.ComposeVariantSeedForBuilding(
                building.gameObject,
                buildingId);

            foreach (MeshRenderer renderer in building.GetComponentsInChildren<MeshRenderer>(true))
            {
                Material[] sources = renderer.sharedMaterials;
                if (sources == null)
                    continue;

                Vector3 w = renderer.bounds.size.sqrMagnitude > 1e-8f
                    ? renderer.bounds.center
                    : renderer.transform.position;
                string slotSeed = variantSeed + "|mr:" + renderer.GetEntityId()
                    + "|" + Mathf.RoundToInt(w.x * 100f)
                    + "," + Mathf.RoundToInt(w.z * 100f);

                for (int i = 0; i < sources.Length; i++)
                {
                    Material source = sources[i];
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

                    Material shared = BuildingSharedMaterialApplier.ResolveSharedMaterial(
                        settings,
                        slotSeed,
                        category,
                        surfaceType,
                        useRoofOrtho: false,
                        roofOrtho: null);

                    if (shared == null)
                        continue;

                    switch (surfaceType)
                    {
                        case BuildingSurfaceMaterialType.Facade:
                            facadeMaterial ??= shared;
                            combinedObjectKey ??= BuildingSurfaceUtility.CanonicalizeSlotKey(sourceName);
                            break;
                        case BuildingSurfaceMaterialType.RoofFlat:
                            roofMaterial ??= shared;
                            break;
                        case BuildingSurfaceMaterialType.RoofSloped:
                            slopedRoof ??= shared;
                            break;
                    }
                }
            }

            roofMaterial ??= slopedRoof;
            if (facadeMaterial != null && roofMaterial == null)
                roofMaterial = ResolveProxyRoofFallback(settings, facadeMaterial);

            if (facadeMaterial != null && roofMaterial != null)
                return true;

            combinedObjectKey ??= "house_facade";
            facadeMaterial ??= settings.GetFacadeMaterial(BuildingCategory.House, 0);
            roofMaterial ??= ResolveProxyRoofFallback(settings, facadeMaterial);
            return facadeMaterial != null && roofMaterial != null;
        }

        /// <summary>Category-matched roof when GLB slots omit an explicit roof material.</summary>
        public static Material ResolveProxyRoofFallback(
            BuildingSurfaceSettings settings,
            Material facadeMaterial)
        {
            if (settings == null)
                return null;

            BuildingCategory category = BuildingCategory.House;
            if (facadeMaterial != null &&
                TryResolveMaterialKey(facadeMaterial.name, out _, out category, out _))
            {
                // category from facade name
            }

            return settings.GetFlatRoofMaterial(category, 0)
                   ?? settings.GetSlopedRoofMaterial(category, 0)
                   ?? settings.GetFlatRoofMaterial(BuildingCategory.House, 0)
                   ?? settings.GetSlopedRoofMaterial(BuildingCategory.House, 0);
        }

        /// <summary>Facade material for footprint proxy boxes — follows GLB slot naming.</summary>
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
            string buildingId = BuildingSurfaceUtility.ExtractBuildingIdFromObjectName(building.name);
            string variantSeed = BuildingSurfaceUtility.ComposeVariantSeedForBuilding(
                building.gameObject,
                buildingId);

            foreach (MeshRenderer renderer in building.GetComponentsInChildren<MeshRenderer>(true))
            {
                Material[] sources = renderer.sharedMaterials;
                if (sources == null)
                    continue;

                Vector3 w = renderer.bounds.size.sqrMagnitude > 1e-8f
                    ? renderer.bounds.center
                    : renderer.transform.position;
                string slotSeed = variantSeed + "|mr:" + renderer.GetEntityId()
                    + "|" + Mathf.RoundToInt(w.x * 100f)
                    + "," + Mathf.RoundToInt(w.z * 100f);

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

                    Material shared = BuildingSharedMaterialApplier.ResolveSharedMaterial(
                        settings,
                        slotSeed,
                        category,
                        surfaceType,
                        useRoofOrtho: false,
                        roofOrtho: null);

                    if (shared == null)
                        continue;

                    if (surfaceType == BuildingSurfaceMaterialType.Facade)
                    {
                        material = shared;
                        combinedObjectKey = BuildingSurfaceUtility.CanonicalizeSlotKey(sourceName);
                        return true;
                    }

                    if (fallback == null)
                    {
                        fallback = shared;
                        fallbackKey = BuildingSurfaceUtility.CanonicalizeSlotKey(sourceName);
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
            combinedObjectKey = "house_facade";
            return material != null;
        }

        /// <summary>Preferred roof surface slot for footprint proxy boxes in a category.</summary>
        public static BuildingSurfaceMaterialType ResolveProxyRoofSurfaceType(
            BuildingSurfaceSettings settings,
            BuildingCategory category)
        {
            if (settings != null)
            {
                if (settings.GetFlatRoofMaterial(category, 0) != null)
                    return BuildingSurfaceMaterialType.RoofFlat;
                if (settings.GetSlopedRoofMaterial(category, 0) != null)
                    return BuildingSurfaceMaterialType.RoofSloped;
            }

            return BuildingSurfaceMaterialType.RoofFlat;
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

        public static IEnumerator ApplyFacadeMaterialsToCombinedInstanceRoutine(
            GameObject instance,
            string tileId,
            Vector3 unityPosition,
            int tileSizeMeters,
            BuildingSurfaceSettings settings,
            SpatialStreamingLoadBudget budget)
        {
            if (instance == null || settings == null)
                yield break;

            MeshRenderer[] renderers = instance.GetComponentsInChildren<MeshRenderer>(true);
            if (renderers == null || renderers.Length == 0)
                yield break;

            int batch = Mathf.Max(1, budget.renderersPerSpawnStep);
            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer renderer = renderers[i];
                if (renderer != null)
                    RemapRendererMaterials(renderer, tileId, settings);

                if ((i + 1) % batch != 0 && i + 1 < renderers.Length)
                    continue;

                yield return SpatialSpawnFrameBudget.WaitForSpawnStep(budget);
            }
        }

        static int RemapRendererMaterials(
            MeshRenderer renderer,
            string tileId,
            BuildingSurfaceSettings settings)
        {
            if (renderer == null)
                return 0;

            if (SpatialFootprintBoxUtility.TryResolveProxySurfaceHint(
                    renderer,
                    out BuildingCategory proxyCategory,
                    out BuildingSurfaceMaterialType proxySurface))
            {
                string sourceName = BuildingSurfaceUtility.CanonicalizeSlotKey(
                    BuildingSurfaceUtility.CategoryToSlotToken(proxyCategory) + "_" +
                    BuildingSurfaceUtility.SurfaceToSlotToken(proxySurface));
                string variantSeed = tileId + "|combined|" + sourceName;
                Material shared = BuildingSharedMaterialApplier.ResolveSharedMaterial(
                    settings,
                    variantSeed,
                    proxyCategory,
                    proxySurface,
                    useRoofOrtho: false,
                    roofOrtho: null);

                if (shared != null && ApplySingleMaterial(renderer, shared))
                    return 1;

                return 0;
            }

            MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
            string meshName = meshFilter != null && meshFilter.sharedMesh != null
                ? meshFilter.sharedMesh.name
                : null;

            if (IsFootprintProxyMeshName(meshName) &&
                TryRemapFromSurfaceHint(
                    renderer,
                    tileId,
                    settings,
                    meshName,
                    renderer.sharedMaterial,
                    out int meshSlots))
            {
                return meshSlots;
            }

            if (TryRemapFromSurfaceHint(
                    renderer,
                    tileId,
                    settings,
                    renderer.gameObject.name,
                    renderer.sharedMaterial,
                    out int objectSlots))
            {
                return objectSlots;
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

                string sourceName;
                BuildingCategory category;
                BuildingSurfaceMaterialType surfaceType;

                if (IsFootprintProxyMeshName(meshName) &&
                    TryResolveFootprintProxyKey(meshName, source, out sourceName, out category, out surfaceType))
                {
                    // Footprint proxy submesh — mesh name selects facade vs roof.
                }
                else if (!TryResolveMaterialKey(source.name, out sourceName, out category, out surfaceType))
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

        static bool TryRemapFromSurfaceHint(
            MeshRenderer renderer,
            string tileId,
            BuildingSurfaceSettings settings,
            string surfaceHint,
            Material categoryHint,
            out int slotsChanged)
        {
            slotsChanged = 0;
            if (renderer == null || string.IsNullOrEmpty(surfaceHint))
                return false;

            if (!TryResolveMaterialKey(surfaceHint, out string sourceName,
                    out BuildingCategory category, out BuildingSurfaceMaterialType surfaceType) &&
                !TryResolveFootprintProxyKey(surfaceHint, categoryHint, out sourceName, out category, out surfaceType))
            {
                return false;
            }

            string variantSeed = tileId + "|combined|" + sourceName;
            Material shared = BuildingSharedMaterialApplier.ResolveSharedMaterial(
                settings,
                variantSeed,
                category,
                surfaceType,
                useRoofOrtho: false,
                roofOrtho: null);

            if (shared == null || !ApplySingleMaterial(renderer, shared))
                return false;

            slotsChanged = 1;
            return true;
        }

        /// <summary>
        /// Footprint proxy meshes are named FootprintProxy_{n}_facade|roof without a category token.
        /// Infer category from the baked material slot on the renderer.
        /// </summary>
        static bool TryResolveFootprintProxyKey(
            string surfaceHint,
            Material categoryHint,
            out string sourceName,
            out BuildingCategory category,
            out BuildingSurfaceMaterialType surfaceType)
        {
            sourceName = null;
            category = BuildingCategory.House;
            surfaceType = BuildingSurfaceMaterialType.Facade;

            if (string.IsNullOrEmpty(surfaceHint) ||
                !surfaceHint.ToLowerInvariant().Contains("footprintproxy"))
            {
                return false;
            }

            if (!BuildingSurfaceUtility.TryResolveSurfaceTypeFromMaterialName(surfaceHint, out surfaceType))
                return false;

            if (categoryHint != null &&
                BuildingSurfaceUtility.TryResolveCategoryFromMaterialName(categoryHint.name, out BuildingCategory hinted))
            {
                category = hinted;
            }
            else if (!BuildingSurfaceUtility.TryResolveCategoryFromMaterialName(surfaceHint, out category))
            {
                category = BuildingCategory.House;
            }

            sourceName = BuildingSurfaceUtility.CanonicalizeSlotKey(
                BuildingSurfaceUtility.CategoryToSlotToken(category) + "_" +
                BuildingSurfaceUtility.SurfaceToSlotToken(surfaceType));
            return true;
        }

        static bool IsFootprintProxyMeshName(string meshName)
        {
            if (string.IsNullOrEmpty(meshName))
                return false;

            string n = meshName.ToLowerInvariant();
            return n.Contains("footprintproxy");
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
