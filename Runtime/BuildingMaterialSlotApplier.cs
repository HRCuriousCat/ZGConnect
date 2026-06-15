using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect
{
    /// <summary>
    /// Applies shared materials from JSON slot keys (e.g. house_facade, building_roof_flat).
    /// </summary>
    public static class BuildingMaterialSlotApplier
    {
        public static void ApplyTileFromMetadata(
            GameObject tileRoot,
            BuildingsMetadataJson meta,
            CityTileRecord tileRecord,
            BuildingSurfaceSettings settings,
            BuildingMaterialApplyStyle style,
            string roofOrthophotoBasemapId,
            Material roofOrthophotoTemplate,
            Dictionary<string, Material> roofMaterialCache,
            bool useOrthophotoBasemapForRoofs = true)
        {
            if (tileRoot == null || meta?.Buildings == null || settings == null)
                return;

            CollectMetadataBuildingEntries(tileRoot, meta, out _, out List<(GameObject building, BuildingEntryJson entry)> pairs);
            foreach ((GameObject building, BuildingEntryJson entry) pair in pairs)
            {
                ApplyBuildingFromMetadata(
                    pair.building,
                    pair.entry,
                    tileRecord,
                    settings,
                    style,
                    roofOrthophotoBasemapId,
                    roofOrthophotoTemplate,
                    roofMaterialCache,
                    useOrthophotoBasemapForRoofs);
            }
        }

        public static void CollectMetadataBuildingEntries(
            GameObject tileRoot,
            BuildingsMetadataJson meta,
            out Dictionary<string, BuildingEntryJson> lookup,
            out List<(GameObject building, BuildingEntryJson entry)> pairs)
        {
            lookup = BuildMetadataLookup(meta);
            var buildingPairs = new List<(GameObject, BuildingEntryJson)>();
            if (tileRoot == null || meta?.Buildings == null)
            {
                pairs = buildingPairs;
                return;
            }

            var visited = new HashSet<GameObject>();

            int childCount = tileRoot.transform.childCount;
            for (int c = 0; c < childCount; c++)
            {
                Transform child = tileRoot.transform.GetChild(c);
                if (!TryResolveBuildingEntry(child, lookup, out BuildingEntryJson entry))
                    continue;

                visited.Add(child.gameObject);
                buildingPairs.Add((child.gameObject, entry));
            }

            ForEachNestedMetadataBuilding(
                tileRoot.transform,
                lookup,
                (buildingTransform, entry) =>
                {
                    if (visited.Contains(buildingTransform.gameObject))
                        return;

                    visited.Add(buildingTransform.gameObject);
                    buildingPairs.Add((buildingTransform.gameObject, entry));
                });

            pairs = buildingPairs;
        }

        static void ForEachNestedMetadataBuilding(
            Transform tileRoot,
            Dictionary<string, BuildingEntryJson> lookup,
            Action<Transform, BuildingEntryJson> action)
        {
            if (tileRoot == null || lookup == null || lookup.Count == 0 || action == null)
                return;

            var visited = new HashSet<Transform>();
            foreach (Transform transform in tileRoot.GetComponentsInChildren<Transform>(true))
            {
                if (transform == tileRoot || visited.Contains(transform))
                    continue;

                if (!TryResolveBuildingEntry(transform, lookup, out BuildingEntryJson entry))
                    continue;

                Transform buildingRoot = FindMetadataBuildingRoot(transform, tileRoot, lookup);
                if (buildingRoot == null || visited.Contains(buildingRoot))
                    continue;

                visited.Add(buildingRoot);
                if (lookup.TryGetValue(ResolveMetadataLookupKey(buildingRoot, lookup), out BuildingEntryJson resolved))
                    action(buildingRoot, resolved);
                else
                    action(buildingRoot, entry);
            }
        }

        static Transform FindMetadataBuildingRoot(
            Transform match,
            Transform tileRoot,
            Dictionary<string, BuildingEntryJson> lookup)
        {
            Transform current = match;
            Transform best = match;

            while (current != null && current != tileRoot)
            {
                if (TryResolveBuildingEntry(current, lookup, out _))
                    best = current;
                current = current.parent;
            }

            return best;
        }

        static string ResolveMetadataLookupKey(
            Transform buildingRoot,
            Dictionary<string, BuildingEntryJson> lookup)
        {
            if (TryResolveBuildingEntry(buildingRoot, lookup, out _))
            {
                if (lookup.TryGetValue(buildingRoot.name, out _))
                    return buildingRoot.name;

                string id = BuildingSurfaceUtility.ExtractBuildingIdFromObjectName(buildingRoot.name);
                if (lookup.ContainsKey(id))
                    return id;
            }

            return buildingRoot.name;
        }

        public static void ApplyBuildingFromMetadata(
            GameObject building,
            BuildingEntryJson entry,
            CityTileRecord tileRecord,
            BuildingSurfaceSettings settings,
            BuildingMaterialApplyStyle style,
            string roofOrthophotoBasemapId,
            Material roofOrthophotoTemplate,
            Dictionary<string, Material> roofMaterialCache,
            bool useOrthophotoBasemapForRoofs = true)
        {
            if (building == null || entry == null || settings == null)
                return;

            BuildingData bd = building.GetComponent<BuildingData>();
            string buildingId = bd != null && !string.IsNullOrEmpty(bd.buildingId)
                ? bd.buildingId
                : building.name;
            string variantSeed = BuildingSurfaceUtility.ComposeVariantSeedForBuilding(
                building, buildingId);

            if (entry.MeshMaterials != null && entry.MeshMaterials.Count > 0)
            {
                bool appliedAny = false;
                foreach (BuildingMeshMaterialsJson meshEntry in entry.MeshMaterials)
                {
                    MeshRenderer renderer = ResolveRenderer(building.transform, meshEntry.RendererPath);
                    if (renderer == null)
                        continue;

                    ApplyRendererFromSlotKeys(
                        renderer, meshEntry.MaterialSlots, tileRecord, settings, variantSeed, style,
                        roofOrthophotoBasemapId, roofOrthophotoTemplate, roofMaterialCache,
                        useOrthophotoBasemapForRoofs);
                    appliedAny = true;
                }

                if (appliedAny)
                    return;
            }

            if (entry.MaterialSlots == null || entry.MaterialSlots.Count == 0)
                return;

            MeshRenderer[] renderers = building.GetComponentsInChildren<MeshRenderer>(true);
            if (renderers.Length == 0)
                return;

            foreach (MeshRenderer renderer in renderers)
            {
                ApplyRendererFromSlotKeys(
                    renderer, entry.MaterialSlots, tileRecord, settings, variantSeed, style,
                    roofOrthophotoBasemapId, roofOrthophotoTemplate, roofMaterialCache,
                    useOrthophotoBasemapForRoofs);
            }
        }

        public static void ApplyRendererFromSlotKeys(
            MeshRenderer renderer,
            IList<string> slotKeys,
            CityTileRecord tileRecord,
            BuildingSurfaceSettings settings,
            string variantSeed,
            BuildingMaterialApplyStyle style,
            string roofOrthophotoBasemapId,
            Material roofOrthophotoTemplate,
            Dictionary<string, Material> roofMaterialCache,
            bool useOrthophotoBasemapForRoofs = true)
        {
            if (renderer == null || slotKeys == null || slotKeys.Count == 0 || settings == null)
                return;

            MeshFilter mf = renderer.GetComponent<MeshFilter>();
            int slotCount = Mathf.Max(renderer.sharedMaterials.Length, slotKeys.Count);
            if (slotCount == 0)
                return;

            bool useRoofOrtho = style == BuildingMaterialApplyStyle.RoofOrthophoto;
            Material roofOrtho = useRoofOrtho
                ? BuildingSharedMaterialApplier.ResolveRoofMaterial(
                    tileRecord, roofOrthophotoBasemapId, roofOrthophotoTemplate, roofMaterialCache,
                    useOrthophotoBasemapForRoofs)
                : null;

            var mapped = new Material[slotCount];
            for (int i = 0; i < slotCount; i++)
            {
                string key = i < slotKeys.Count ? slotKeys[i] : null;
                if (string.IsNullOrEmpty(key))
                {
                    mapped[i] = i < renderer.sharedMaterials.Length
                        ? renderer.sharedMaterials[i]
                        : null;
                    continue;
                }

                if (!BuildingSurfaceUtility.TryResolveCategoryAndSurfaceFromSlotKey(
                        key, out BuildingCategory category, out BuildingSurfaceMaterialType surfaceType))
                {
                    mapped[i] = i < renderer.sharedMaterials.Length
                        ? renderer.sharedMaterials[i]
                        : null;
                    continue;
                }

                Material shared = BuildingSharedMaterialApplier.ResolveSharedMaterial(
                    settings, variantSeed, category, surfaceType, useRoofOrtho, roofOrtho);
                mapped[i] = shared != null
                    ? shared
                    : (i < renderer.sharedMaterials.Length ? renderer.sharedMaterials[i] : null);
            }

            renderer.sharedMaterials = mapped;
        }

        private static bool TryResolveBuildingEntry(
            Transform child,
            Dictionary<string, BuildingEntryJson> lookup,
            out BuildingEntryJson entry)
        {
            entry = null;
            if (child == null || lookup == null || lookup.Count == 0)
                return false;

            if (lookup.TryGetValue(child.name, out entry))
                return true;

            string buildingId = BuildingSurfaceUtility.ExtractBuildingIdFromObjectName(child.name);
            if (!string.IsNullOrEmpty(buildingId) && lookup.TryGetValue(buildingId, out entry))
                return true;

            BuildingData bd = child.GetComponent<BuildingData>();
            if (bd != null && !string.IsNullOrEmpty(bd.buildingId)
                && lookup.TryGetValue(bd.buildingId, out entry))
                return true;

            return false;
        }

        public static BuildingEntryJson CollectBuildingEntry(Transform building)
        {
            var entry = new BuildingEntryJson
            {
                Name = building.name,
                LocalPosition = ToPos(building.localPosition),
                LocalRotation = ToPos(building.localEulerAngles),
                LocalScale    = ToPos(building.localScale),
            };

            MeshRenderer[] renderers = building.GetComponentsInChildren<MeshRenderer>(true);
            if (renderers.Length == 0)
                return entry;

            if (renderers.Length == 1)
            {
                entry.MaterialSlots = CollectSlotKeys(renderers[0]);
                return entry;
            }

            entry.MeshMaterials = new List<BuildingMeshMaterialsJson>(renderers.Length);
            foreach (MeshRenderer renderer in renderers)
            {
                entry.MeshMaterials.Add(new BuildingMeshMaterialsJson
                {
                    RendererPath = GetRelativePath(renderer.transform, building),
                    MaterialSlots = CollectSlotKeys(renderer),
                });
            }

            return entry;
        }

        public static List<string> CollectSlotKeys(MeshRenderer renderer)
        {
            var keys = new List<string>();
            if (renderer == null)
                return keys;

            Material[] mats = renderer.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                string raw = mats[i] != null ? mats[i].name : string.Empty;
                keys.Add(BuildingSurfaceUtility.CanonicalizeSlotKey(raw));
            }

            return keys;
        }

        /// <summary>Captures per-building slot keys from current renderer materials (e.g. raw GLB names).</summary>
        public static void CaptureTileSlotSnapshot(
            GameObject tileRoot,
            Dictionary<string, BuildingEntryJson> into)
        {
            if (tileRoot == null || into == null)
                return;

            int childCount = tileRoot.transform.childCount;
            for (int c = 0; c < childCount; c++)
            {
                Transform child = tileRoot.transform.GetChild(c);
                into[child.name] = CollectBuildingEntry(child);
            }
        }

        public static bool TileMetadataHasSlotKeys(BuildingsMetadataJson meta)
        {
            if (meta?.Buildings == null)
                return false;

            foreach (BuildingEntryJson entry in meta.Buildings)
            {
                if (EntryHasResolvableSlotKeys(entry))
                    return true;
            }

            return false;
        }

        static Dictionary<string, BuildingEntryJson> BuildMetadataLookup(BuildingsMetadataJson meta)
        {
            var lookup = new Dictionary<string, BuildingEntryJson>(System.StringComparer.OrdinalIgnoreCase);
            if (meta?.Buildings == null)
                return lookup;

            foreach (BuildingEntryJson entry in meta.Buildings)
            {
                if (entry == null || string.IsNullOrEmpty(entry.Name))
                    continue;

                if (!lookup.ContainsKey(entry.Name))
                    lookup[entry.Name] = entry;

                string id = BuildingSurfaceUtility.ExtractBuildingIdFromObjectName(entry.Name);
                if (!string.IsNullOrEmpty(id) && !lookup.ContainsKey(id))
                    lookup[id] = entry;
            }

            return lookup;
        }

        public static bool EntryHasResolvableSlotKeys(BuildingEntryJson entry)
        {
            if (entry == null)
                return false;

            if (HasResolvableSlotKeys(entry.MaterialSlots))
                return true;

            if (entry.MeshMaterials == null)
                return false;

            foreach (BuildingMeshMaterialsJson mesh in entry.MeshMaterials)
            {
                if (HasResolvableSlotKeys(mesh.MaterialSlots))
                    return true;
            }

            return false;
        }

        private static bool HasResolvableSlotKeys(IList<string> keys)
        {
            if (keys == null)
                return false;

            foreach (string key in keys)
            {
                if (string.IsNullOrEmpty(key))
                    continue;

                if (BuildingSurfaceUtility.TryResolveCategoryAndSurfaceFromSlotKey(
                        key, out _, out _))
                    return true;
            }

            return false;
        }

        public static void MergeSlotSnapshot(BuildingEntryJson target, BuildingEntryJson snapshot)
        {
            if (target == null || snapshot == null || !EntryHasResolvableSlotKeys(snapshot))
                return;

            if (snapshot.MeshMaterials != null && snapshot.MeshMaterials.Count > 0)
                target.MeshMaterials = snapshot.MeshMaterials;
            else if (snapshot.MaterialSlots != null && snapshot.MaterialSlots.Count > 0)
                target.MaterialSlots = snapshot.MaterialSlots;
        }

        public static MeshRenderer ResolveRenderer(Transform buildingRoot, string rendererPath)
        {
            if (buildingRoot == null)
                return null;

            if (string.IsNullOrEmpty(rendererPath))
            {
                MeshRenderer onRoot = buildingRoot.GetComponent<MeshRenderer>();
                return onRoot != null
                    ? onRoot
                    : buildingRoot.GetComponentInChildren<MeshRenderer>(true);
            }

            Transform node = FindChildPath(buildingRoot, rendererPath);
            return node != null ? node.GetComponent<MeshRenderer>() : null;
        }

        static Transform FindChildPath(Transform buildingRoot, string rendererPath)
        {
            if (buildingRoot == null)
                return null;

            if (string.IsNullOrEmpty(rendererPath))
                return buildingRoot;

            Transform direct = ResolvePathSegments(buildingRoot, rendererPath);
            if (direct != null)
                return direct;

            Transform lod0 = buildingRoot.Find("LOD0");
            if (lod0 != null)
            {
                Transform underLod0 = ResolvePathSegments(lod0, rendererPath);
                if (underLod0 != null)
                    return underLod0;
            }

            return null;
        }

        static Transform ResolvePathSegments(Transform root, string rendererPath)
        {
            Transform t = root;
            string[] parts = rendererPath.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i]))
                    continue;
                t = t.Find(parts[i]);
                if (t == null)
                    return null;
            }

            return t;
        }

        public static string GetRelativePath(Transform t, Transform buildingRoot)
        {
            if (t == null || buildingRoot == null)
                return string.Empty;
            if (t == buildingRoot)
                return string.Empty;

            var parts = new List<string>();
            Transform cur = t;
            while (cur != null && cur != buildingRoot)
            {
                parts.Insert(0, cur.name);
                cur = cur.parent;
            }

            return string.Join("/", parts);
        }

        private static TilePositionJson ToPos(Vector3 v) =>
            new TilePositionJson { X = v.x, Y = v.y, Z = v.z };
    }
}
