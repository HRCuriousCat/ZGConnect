using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using GLTFast;
using Newtonsoft.Json;
using UnityEngine;
using ZGConnect;

namespace ZGConnect.RealtimeStreaming
{
    public static class RuntimeGlbLoader
    {
        public sealed class BuildingsLoadResult
        {
            public GameObject Root;
            public BuildingsMetadataJson Metadata;
        }

        public static async Task<BuildingsLoadResult> LoadTileBuildingsAsync(
            string glbPath,
            string jsonPath,
            Transform parent,
            string tileId,
            BuildingLodController lodController,
            BuildingLodStorageMode lodMode,
            string lod1GlbPath = null)
        {
            var prepared = new RuntimeBuildingPrepareResult
            {
                TileId = tileId,
                GlbPath = glbPath,
                JsonPath = jsonPath,
                Lod1GlbPath = lod1GlbPath,
            };

            if (!File.Exists(glbPath))
                return null;

            prepared.GlbBytes = File.ReadAllBytes(glbPath);
            if (!string.IsNullOrEmpty(lod1GlbPath) && File.Exists(lod1GlbPath))
                prepared.Lod1GlbBytes = File.ReadAllBytes(lod1GlbPath);
            if (!string.IsNullOrEmpty(jsonPath) && File.Exists(jsonPath))
                prepared.JsonText = File.ReadAllText(jsonPath);
            prepared.Success = true;

            return await LoadTileBuildingsFromPreparedAsync(
                prepared, parent, tileId, lodController, lodMode);
        }

        public static async Task<RuntimeGlbInstantiateResult> InstantiatePreparedGlbAsync(
            RuntimeBuildingPrepareResult prepared,
            Transform parent,
            string tileId,
            BuildingLodStorageMode lodMode)
        {
            if (prepared == null || !prepared.Success)
                return null;

            bool hasBytes = prepared.GlbBytes != null && prepared.GlbBytes.Length > 0;
            bool hasPath = !string.IsNullOrEmpty(prepared.GlbPath) && File.Exists(prepared.GlbPath);
            if (!hasBytes && !hasPath)
                return null;

            var import = new GltfImport();
            bool ok;
            if (hasBytes)
            {
                Uri uri = !string.IsNullOrEmpty(prepared.GlbPath)
                    ? new Uri(Path.GetFullPath(prepared.GlbPath))
                    : null;
                ok = await import.Load(prepared.GlbBytes, uri);
            }
            else
            {
                ok = await import.LoadFile(prepared.GlbPath);
            }

            if (!ok)
            {
                import.Dispose();
                return null;
            }

            var root = new GameObject($"TileBuildings_{tileId}");
            root.transform.SetParent(parent, false);
            await import.InstantiateMainSceneAsync(root.transform);
            RuntimeBuildingHierarchyUtility.FlattenRuntimeGltfWrappers(root.transform);

            return new RuntimeGlbInstantiateResult
            {
                Success = true,
                Root = root,
                Import = import,
                Prepared = prepared,
                TileId = tileId,
                LodMode = lodMode,
                Metadata = null,
            };
        }

        public static async Task<BuildingsLoadResult> LoadTileBuildingsFromPreparedAsync(
            RuntimeBuildingPrepareResult prepared,
            Transform parent,
            string tileId,
            BuildingLodController lodController,
            BuildingLodStorageMode lodMode)
        {
            RuntimeGlbInstantiateResult instantiateResult = await InstantiatePreparedGlbAsync(
                prepared, parent, tileId, lodMode);
            if (instantiateResult?.Root == null)
                return null;

            await CompleteSetupImmediateAsync(instantiateResult, lodController);
            return new BuildingsLoadResult
            {
                Root = instantiateResult.Root,
                Metadata = instantiateResult.Metadata,
            };
        }

        public static async Task CompleteSetupImmediateAsync(
            RuntimeGlbInstantiateResult result,
            BuildingLodController lodController)
        {
            if (result?.Root == null || result.Import == null)
                return;

            Transform root = result.Root.transform;
            GltfImport lod1Import = null;

            if (result.LodMode == BuildingLodStorageMode.DualFile)
            {
                lod1Import = await InstantiateLod1ForSpreadAsync(result.Prepared, result.TileId, root);
                if (lod1Import != null)
                {
                    var lod1Root = root.Find($"TileBuildings_{result.TileId}_lod1");
                    if (lod1Root != null)
                        lodController.MergeDualGlb(root, lod1Root);
                }
            }
            else
            {
                lodController.SetupLodGroupsFromHierarchy(root);
            }

            var holder = root.gameObject.GetComponent<RuntimeBuildingsGltfHolder>();
            if (holder == null)
                holder = root.gameObject.AddComponent<RuntimeBuildingsGltfHolder>();
            holder.SetImports(result.Import, lod1Import);

            RuntimeBuildingHierarchyUtility.AttachBuildingDataFromMetadata(root, result.TileId, result.Metadata);
            ApplyOriginCorrection(root, result.TileId, result.Metadata);
        }

        public static async Task<GltfImport> InstantiateLod1ForSpreadAsync(
            RuntimeBuildingPrepareResult prepared,
            string tileId,
            Transform root)
        {
            bool lod1HasBytes = prepared?.Lod1GlbBytes != null && prepared.Lod1GlbBytes.Length > 0;
            bool lod1HasPath = !string.IsNullOrEmpty(prepared?.Lod1GlbPath) && File.Exists(prepared.Lod1GlbPath);
            if (!lod1HasBytes && !lod1HasPath)
                return null;

            var lod1Import = new GltfImport();
                    bool lod1Ok;
                    if (lod1HasBytes)
                    {
                        Uri lod1Uri = !string.IsNullOrEmpty(prepared.Lod1GlbPath)
                            ? new Uri(Path.GetFullPath(prepared.Lod1GlbPath))
                            : null;
                        lod1Ok = await lod1Import.Load(prepared.Lod1GlbBytes, lod1Uri);
                    }
                    else
                    {
                        lod1Ok = await lod1Import.LoadFile(prepared.Lod1GlbPath);
                    }

            if (!lod1Ok)
            {
                lod1Import.Dispose();
                return null;
            }

            var lod1Root = new GameObject($"TileBuildings_{tileId}_lod1");
            lod1Root.transform.SetParent(root, false);
            await lod1Import.InstantiateMainSceneAsync(lod1Root.transform);
            RuntimeBuildingHierarchyUtility.FlattenRuntimeGltfWrappers(lod1Root.transform);
            lod1Root.SetActive(false);
            return lod1Import;
        }

        public static void AttachBuildingData(Transform root, string tileId, BuildingsMetadataJson meta)
        {
            RuntimeBuildingHierarchyUtility.AttachBuildingDataFromMetadata(root, tileId, meta);
        }

        public static void ApplyOriginCorrection(Transform root, string tileId, BuildingsMetadataJson meta)
        {
            if (meta?.TileOriginUnity == null || meta.Buildings == null)
                return;

            var lookup = BuildMetadataLookup(meta);
            Vector3 correction = ComputeGlbOriginCorrection(root, lookup, tileId, meta);
            if (correction.sqrMagnitude > 0.01f)
                root.position -= correction;
        }

        static Vector3 ComputeGlbOriginCorrection(
            Transform root,
            Dictionary<string, BuildingEntryJson> lookup,
            string tileId,
            BuildingsMetadataJson meta)
        {
            var stack = new Stack<Transform>();
            for (int i = 0; i < root.childCount; i++)
                stack.Push(root.GetChild(i));

            while (stack.Count > 0)
            {
                Transform t = stack.Pop();
                if (TryGetMetadataEntry(t.name, lookup, out BuildingEntryJson entry) && entry.LocalPosition != null)
                {
                    return new Vector3(
                        t.position.x - entry.LocalPosition.X,
                        0f,
                        t.position.z - entry.LocalPosition.Z);
                }

                for (int i = 0; i < t.childCount; i++)
                    stack.Push(t.GetChild(i));
            }

            const float GlbOriginX = 442000f;
            const float GlbOriginY = 5051000f;
            string[] parts = tileId.Split('_');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out int tileLeft) &&
                int.TryParse(parts[1], out int tileBottom))
            {
                float newOx = tileLeft - meta.TileOriginUnity.X;
                float newOy = tileBottom - meta.TileOriginUnity.Z;
                return new Vector3(newOx - GlbOriginX, 0f, newOy - GlbOriginY);
            }

            return Vector3.zero;
        }

        static Dictionary<string, BuildingEntryJson> BuildMetadataLookup(BuildingsMetadataJson meta)
        {
            var lookup = new Dictionary<string, BuildingEntryJson>(StringComparer.OrdinalIgnoreCase);
            if (meta?.Buildings == null)
                return lookup;

            foreach (BuildingEntryJson entry in meta.Buildings)
            {
                if (entry == null || string.IsNullOrEmpty(entry.Name))
                    continue;
                lookup[entry.Name] = entry;
            }

            return lookup;
        }

        static bool TryGetMetadataEntry(
            string transformName,
            Dictionary<string, BuildingEntryJson> lookup,
            out BuildingEntryJson entry)
        {
            entry = null;
            if (lookup == null || lookup.Count == 0)
                return false;

            string id = ExtractBuildingId(transformName);
            if (lookup.TryGetValue(id, out entry))
                return true;
            return lookup.TryGetValue(transformName, out entry);
        }

        static string ExtractBuildingId(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;
            int slash = name.LastIndexOf('/');
            return slash >= 0 ? name.Substring(slash + 1) : name;
        }
    }
}
