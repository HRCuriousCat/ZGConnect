using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect
{
    /// <summary>
    /// Replaces per-GLB embedded materials with shared project materials at runtime (or in editor).
    /// Category and surface type are read only from each source material's name.
    /// Unrecognized slots keep their original GLB material.
    /// </summary>
    public static class BuildingSharedMaterialApplier
    {
        public static void ApplyTile(
            GameObject tileRoot,
            CityTileRecord tileRecord,
            BuildingSurfaceSettings settings,
            BuildingMaterialApplyStyle style,
            string roofOrthophotoBasemapId,
            Material roofOrthophotoTemplate,
            Dictionary<string, Material> roofMaterialCache,
            bool useOrthophotoBasemapForRoofs = true)
        {
            if (tileRoot == null || settings == null)
                return;

            int childCount = tileRoot.transform.childCount;
            for (int c = 0; c < childCount; c++)
            {
                Transform child = tileRoot.transform.GetChild(c);
                ApplyBuilding(child.gameObject, tileRecord, settings, style,
                    roofOrthophotoBasemapId, roofOrthophotoTemplate, roofMaterialCache,
                    useOrthophotoBasemapForRoofs);
            }
        }

        public static void ApplyBuilding(
            GameObject building,
            CityTileRecord tileRecord,
            BuildingSurfaceSettings settings,
            BuildingMaterialApplyStyle style,
            string roofOrthophotoBasemapId,
            Material roofOrthophotoTemplate,
            Dictionary<string, Material> roofMaterialCache,
            bool useOrthophotoBasemapForRoofs = true)
        {
            if (building == null || settings == null)
                return;

            BuildingData bd = building.GetComponent<BuildingData>();
            string buildingId = bd != null && !string.IsNullOrEmpty(bd.buildingId)
                ? bd.buildingId
                : building.name;
            string variantSeed = BuildingSurfaceUtility.ComposeVariantSeedForBuilding(
                building, buildingId);

            foreach (MeshRenderer renderer in building.GetComponentsInChildren<MeshRenderer>(true))
            {
                MeshFilter mf = renderer.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null)
                    continue;

                ApplyMaterialsFromSource(
                    renderer, tileRecord, settings, variantSeed, style,
                    roofOrthophotoBasemapId, roofOrthophotoTemplate, roofMaterialCache,
                    useOrthophotoBasemapForRoofs);
            }
        }

        public static Texture2D GetTileOrthoTexture(CityTileRecord rec, string basemapId)
        {
            if (rec == null)
                return null;

            string id = string.IsNullOrEmpty(basemapId) ? "ortho" : basemapId;

            if (rec.basemapLayers != null)
            {
                foreach (BasemapLayerEntry entry in rec.basemapLayers)
                {
                    if (entry.basemapId == id && entry.texture != null)
                        return entry.texture;
                }
            }

            return null;
        }

        public static string ComposeTileRoofCacheKey(string tileId, string basemapId)
        {
            string id = string.IsNullOrEmpty(basemapId) ? "ortho" : basemapId;
            return tileId + "|" + id;
        }

        public static Material ResolveRoofMaterial(
            CityTileRecord rec,
            string basemapId,
            Material template,
            Dictionary<string, Material> cache,
            bool useOrthophotoBasemap)
        {
            if (!useOrthophotoBasemap)
                return template;

            return GetOrCreateTileRoofMaterial(rec, basemapId, template, cache);
        }

        public static Material GetOrCreateTileRoofMaterial(
            CityTileRecord rec,
            string basemapId,
            Material template,
            Dictionary<string, Material> cache)
        {
            if (rec == null || string.IsNullOrEmpty(rec.tileId))
                return null;

            string cacheKey = ComposeTileRoofCacheKey(rec.tileId, basemapId);
            if (cache != null && cache.TryGetValue(cacheKey, out Material cached) && cached != null)
                return cached;

            Texture2D tex = GetTileOrthoTexture(rec, basemapId);
            if (tex == null)
                return null;

            Material source = template;
            if (source == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null)
                    shader = Shader.Find("Standard");
                source = new Material(shader);
            }

            string id = string.IsNullOrEmpty(basemapId) ? "ortho" : basemapId;
            Material instance = new Material(source);
            AssignTexture(instance, tex);
            instance.name = $"RoofOrtho_Runtime_{rec.tileId}_{id}";

            cache?.Add(cacheKey, instance);
            return instance;
        }

        public static void ReleaseCachedRoofMaterials(Dictionary<string, Material> cache)
        {
            if (cache == null)
                return;

            foreach (Material m in cache.Values)
            {
                if (m != null)
                    Object.Destroy(m);
            }

            cache.Clear();
        }

        private static void ApplyMaterialsFromSource(
            MeshRenderer renderer,
            CityTileRecord tileRecord,
            BuildingSurfaceSettings settings,
            string variantSeed,
            BuildingMaterialApplyStyle style,
            string basemapId,
            Material roofTemplate,
            Dictionary<string, Material> roofCache,
            bool useOrthophotoBasemapForRoofs)
        {
            Material[] sources = renderer.sharedMaterials;
            if (sources == null || sources.Length == 0)
                return;

            bool useRoofOrtho = style == BuildingMaterialApplyStyle.RoofOrthophoto;
            Material roofOrtho = useRoofOrtho
                ? ResolveRoofMaterial(
                    tileRecord, basemapId, roofTemplate, roofCache, useOrthophotoBasemapForRoofs)
                : null;

            var mapped = new Material[sources.Length];
            for (int i = 0; i < sources.Length; i++)
            {
                Material source = sources[i];
                if (source == null)
                {
                    mapped[i] = null;
                    continue;
                }

                string sourceName = BuildingSurfaceUtility.NormalizeMaterialName(source.name);
                if (!BuildingSurfaceUtility.TryResolveCategoryFromMaterialName(
                        sourceName, out BuildingCategory category) ||
                    !BuildingSurfaceUtility.TryResolveSurfaceTypeFromMaterialName(
                        sourceName, out BuildingSurfaceMaterialType surfaceType))
                {
                    mapped[i] = source;
                    continue;
                }

                Vector3 w = renderer.bounds.size.sqrMagnitude > 1e-8f
                    ? renderer.bounds.center
                    : renderer.transform.position;
                string slotSeed = variantSeed + "|mr:" + renderer.GetEntityId()
                    + "|" + Mathf.RoundToInt(w.x * 100f)
                    + "," + Mathf.RoundToInt(w.z * 100f);

                Material shared = ResolveSharedMaterial(
                    settings, slotSeed, category, surfaceType, useRoofOrtho, roofOrtho);
                mapped[i] = shared != null ? shared : source;
            }

            renderer.sharedMaterials = mapped;
        }

        public static Material ResolveSharedMaterial(
            BuildingSurfaceSettings settings,
            string variantSeed,
            BuildingCategory category,
            BuildingSurfaceMaterialType surfaceType,
            bool useRoofOrtho,
            Material roofOrtho)
        {
            BuildingCategoryProfile profile = settings.GetProfile(category);
            int catSalt = BuildingSurfaceUtility.CategoryVariantSalt(category);
            int facadeVar = BuildingSurfaceUtility.VariantIndex(
                variantSeed,
                profile.facadeVariants?.Length ?? 0,
                BuildingSurfaceUtility.SaltFacade ^ catSalt);
            int flatVar = BuildingSurfaceUtility.VariantIndex(
                variantSeed,
                profile.flatRoofVariants?.Length ?? 0,
                BuildingSurfaceUtility.SaltFlatRoof ^ catSalt);
            int slopedVar = BuildingSurfaceUtility.VariantIndex(
                variantSeed,
                profile.slopedRoofVariants?.Length ?? 0,
                BuildingSurfaceUtility.SaltSlopedRoof ^ catSalt);

            if (useRoofOrtho &&
                (surfaceType == BuildingSurfaceMaterialType.RoofFlat ||
                 surfaceType == BuildingSurfaceMaterialType.RoofSloped))
            {
                if (roofOrtho != null)
                    return roofOrtho;
            }

            switch (surfaceType)
            {
                case BuildingSurfaceMaterialType.RoofFlat:
                {
                    Material flat = settings.GetFlatRoofMaterial(category, flatVar);
                    return flat != null
                        ? flat
                        : settings.GetSlopedRoofMaterial(category, slopedVar);
                }
                case BuildingSurfaceMaterialType.RoofSloped:
                {
                    Material sloped = settings.GetSlopedRoofMaterial(category, slopedVar);
                    return sloped != null
                        ? sloped
                        : settings.GetFlatRoofMaterial(category, flatVar);
                }
                default:
                {
                    Material facade = settings.GetFacadeMaterial(category, facadeVar);
                    return facade != null
                        ? facade
                        : settings.GetFlatRoofMaterial(category, flatVar);
                }
            }
        }

        public static void AssignFacadeRoofMaterialSlots(
            MeshRenderer renderer,
            BuildingSurfaceSettings settings,
            BuildingCategory category,
            int facadeVar,
            int flatVar,
            int slopedVar,
            bool hasFlat,
            bool hasSloped)
        {
            Material facade = settings.GetFacadeMaterial(category, facadeVar);
            if (facade == null)
            {
                Debug.LogWarning("[ZGConnect] No facade material for category " + category);
                return;
            }

            var mats = new List<Material> { facade };
            if (hasFlat && hasSloped)
            {
                Material flat = settings.GetFlatRoofMaterial(category, flatVar);
                Material sloped = settings.GetSlopedRoofMaterial(category, slopedVar);
                mats.Add(flat != null ? flat : sloped);
                mats.Add(sloped != null ? sloped : flat);
            }
            else if (hasSloped)
            {
                Material sloped = settings.GetSlopedRoofMaterial(category, slopedVar);
                mats.Add(sloped != null ? sloped : settings.GetFlatRoofMaterial(category, flatVar));
            }
            else if (hasFlat)
            {
                Material flat = settings.GetFlatRoofMaterial(category, flatVar);
                mats.Add(flat != null ? flat : settings.GetSlopedRoofMaterial(category, slopedVar));
            }

            renderer.sharedMaterials = mats.ToArray();
        }

        private static void AssignTexture(Material mat, Texture2D texture)
        {
            if (mat == null || texture == null)
                return;
            if (mat.HasProperty("_BaseMap"))
                mat.SetTexture("_BaseMap", texture);
            if (mat.HasProperty("_MainTex"))
                mat.SetTexture("_MainTex", texture);
        }
    }
}
