using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using ZGConnect;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace ZGConnect.RealtimeStreaming
{
    public static class RuntimeBuildingHierarchyUtility
    {
        public static void FlattenRuntimeGltfWrappers(Transform root)
        {
            if (root == null)
                return;

            // Collapse mesh-less single-child wrapper chains (GLTF scene root, TileBuildings_* groups, etc.).
            while (root.childCount == 1)
            {
                Transform wrapper = root.GetChild(0);
                if (wrapper.childCount == 0 || HasMeshComponents(wrapper))
                    break;

                while (wrapper.childCount > 0)
                    wrapper.GetChild(0).SetParent(root, worldPositionStays: true);

                RuntimeObjectUtility.Destroy(wrapper.gameObject);
            }

            // GLB exports may nest TileBuildings_* twice; unwrap every mesh-less group under the tile root.
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = root.childCount - 1; i >= 0; i--)
                {
                    Transform child = root.GetChild(i);
                    if (!IsTileBuildingsGroupNode(child))
                        continue;

                    while (child.childCount > 0)
                        child.GetChild(0).SetParent(root, worldPositionStays: true);

                    RuntimeObjectUtility.Destroy(child.gameObject);
                    changed = true;
                    break;
                }
            }
        }

        static bool IsTileBuildingsGroupNode(Transform node)
        {
            if (node == null || !node.name.StartsWith("TileBuildings_", System.StringComparison.Ordinal))
                return false;

            return !HasMeshComponents(node) && node.childCount > 0;
        }

        public static void ForEachMetadataBuilding(
            Transform tileRoot,
            BuildingsMetadataJson meta,
            Action<Transform, BuildingEntryJson> action)
        {
            if (tileRoot == null || meta?.Buildings == null || action == null)
                return;

            var lookup = BuildMetadataLookup(meta);
            var visited = new HashSet<Transform>();

            foreach (Transform transform in tileRoot.GetComponentsInChildren<Transform>(true))
            {
                if (transform == tileRoot || visited.Contains(transform))
                    continue;

                if (!TryResolveBuildingEntry(transform, lookup, out BuildingEntryJson entry))
                    continue;

                Transform buildingRoot = FindBuildingRoot(transform, tileRoot, lookup);
                if (buildingRoot == null || visited.Contains(buildingRoot))
                    continue;

                visited.Add(buildingRoot);
                if (lookup.TryGetValue(ResolveLookupKey(buildingRoot, lookup), out BuildingEntryJson resolved))
                    action(buildingRoot, resolved);
                else
                    action(buildingRoot, entry);
            }
        }

        /// <summary>
        /// Asset-bundle prefabs instantiated in the editor often keep serialized BuildingData fields
        /// on a missing-script component. Strip those before attaching a real BuildingData.
        /// </summary>
        public static void PrepareBuildingHierarchyForMetadata(Transform tileRoot)
        {
            if (tileRoot == null)
                return;

#if UNITY_EDITOR
            foreach (Transform transform in tileRoot.GetComponentsInChildren<Transform>(true))
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(transform.gameObject);
#endif
        }

        public static bool AnyBuildingMissingData(Transform tileRoot)
        {
            if (tileRoot == null)
                return false;

            bool anyMissing = false;
            BuildingLodController.ForEachBuildingTransform(tileRoot, building =>
            {
                if (building.GetComponent<BuildingData>() == null)
                    anyMissing = true;
            });
            return anyMissing;
        }

        public static bool TryAttachFromJsonFile(Transform tileRoot, string tileId, string jsonPath) =>
            TryAttachFromMetadataSources(tileRoot, tileId, jsonPath, null, BuildingMetadataSourceMode.JsonOnly);

        public static bool TryAttachFromMetadataSources(
            Transform tileRoot,
            string tileId,
            string jsonPath,
            string bytesPath,
            BuildingMetadataSourceMode mode)
        {
            if (tileRoot == null)
                return false;

            if (!BuildingMetadataLoadUtility.TryLoad(jsonPath, bytesPath, mode, out BuildingsMetadataJson meta, out string error))
            {
                Debug.LogWarning(
                    $"[ZGConnect.Realtime] Failed to read building metadata for '{tileId}': {error}");
                return false;
            }

            PrepareBuildingHierarchyForMetadata(tileRoot);
            AttachBuildingDataFromMetadata(tileRoot, tileId, meta);
            return true;
        }

        public static IEnumerator TryAttachFromMetadataSourcesSpread(
            Transform tileRoot,
            string tileId,
            string jsonPath,
            string bytesPath,
            BuildingMetadataSourceMode mode,
            int buildingsPerFrame,
            float msBudget)
        {
            if (tileRoot == null)
                yield break;

            if (!BuildingMetadataLoadUtility.TryLoad(jsonPath, bytesPath, mode, out BuildingsMetadataJson meta, out string error))
            {
                Debug.LogWarning(
                    $"[ZGConnect.Realtime] Failed to read building metadata for '{tileId}': {error}");
                yield break;
            }

            PrepareBuildingHierarchyForMetadata(tileRoot);
            yield return AttachBuildingDataFromMetadataSpread(
                tileRoot, tileId, meta, buildingsPerFrame, msBudget);
        }

        public static void AttachBuildingDataFromMetadata(
            Transform tileRoot,
            string tileId,
            BuildingsMetadataJson meta)
        {
            if (tileRoot == null || meta?.Buildings == null)
                return;

            PrepareBuildingHierarchyForMetadata(tileRoot);

            ForEachMetadataBuilding(tileRoot, meta, (buildingTransform, entry) =>
            {
                AttachBuildingData(buildingTransform, tileId, entry);
            });
        }

        public static IEnumerator AttachBuildingDataFromMetadataSpread(
            Transform tileRoot,
            string tileId,
            BuildingsMetadataJson meta,
            int buildingsPerFrame,
            float msBudget)
        {
            if (tileRoot == null || meta?.Buildings == null)
                yield break;

            var pairs = new List<(Transform building, BuildingEntryJson entry)>();
            ForEachMetadataBuilding(tileRoot, meta, (buildingTransform, entry) =>
            {
                pairs.Add((buildingTransform, entry));
            });

            int index = 0;
            while (index < pairs.Count)
            {
                float frameStart = Time.realtimeSinceStartup;
                int applied = 0;
                while (index < pairs.Count)
                {
                    (Transform building, BuildingEntryJson entry) pair = pairs[index];
                    AttachBuildingData(pair.building, tileId, pair.entry);
                    index++;
                    applied++;

                    if (RuntimeFrameBudget.ShouldYield(applied, buildingsPerFrame, frameStart, msBudget))
                        break;
                }

                if (index < pairs.Count)
                    yield return null;
            }
        }

        public static BuildingData AttachBuildingData(Transform buildingTransform, string tileId, BuildingEntryJson entry)
        {
            if (buildingTransform == null || entry == null)
                return null;

#if UNITY_EDITOR
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(buildingTransform.gameObject);
#endif

            BuildingData bd = buildingTransform.gameObject.GetComponent<BuildingData>();
            if (bd == null)
                bd = buildingTransform.gameObject.AddComponent<BuildingData>();

            bd.tileId = tileId;
            bd.buildingId = BuildingSurfaceUtility.ExtractBuildingIdFromObjectName(buildingTransform.name);
            BuildingMetadataApplier.Apply(bd, entry);
            return bd;
        }

        static Transform FindBuildingRoot(
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

        static string ResolveLookupKey(Transform buildingRoot, Dictionary<string, BuildingEntryJson> lookup)
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

        static Dictionary<string, BuildingEntryJson> BuildMetadataLookup(BuildingsMetadataJson meta)
        {
            var lookup = new Dictionary<string, BuildingEntryJson>(StringComparer.OrdinalIgnoreCase);
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

        static bool TryResolveBuildingEntry(
            Transform transform,
            Dictionary<string, BuildingEntryJson> lookup,
            out BuildingEntryJson entry)
        {
            entry = null;
            if (transform == null || lookup == null || lookup.Count == 0)
                return false;

            if (lookup.TryGetValue(transform.name, out entry))
                return true;

            string id = BuildingSurfaceUtility.ExtractBuildingIdFromObjectName(transform.name);
            if (!string.IsNullOrEmpty(id) && lookup.TryGetValue(id, out entry))
                return true;

            BuildingData bd = transform.GetComponent<BuildingData>();
            if (bd != null && !string.IsNullOrEmpty(bd.buildingId) && lookup.TryGetValue(bd.buildingId, out entry))
                return true;

            return false;
        }

        static bool HasMeshComponents(Transform node)
        {
            if (node == null)
                return false;

            return node.GetComponent<MeshFilter>() != null
                   || node.GetComponent<MeshRenderer>() != null
                   || node.GetComponent<SkinnedMeshRenderer>() != null;
        }
    }
}

