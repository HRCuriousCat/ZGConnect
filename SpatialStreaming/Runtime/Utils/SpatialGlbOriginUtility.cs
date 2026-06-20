using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using ZGConnect;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// Aligns raw building GLBs to manifest tile space (same rules as RealtimeStreaming / ZGConnectImporter).
    /// </summary>
    public static class SpatialGlbOriginUtility
    {
        const float GlbOriginX = 442000f;
        const float GlbOriginZ = 5051000f;

        public static BuildingsMetadataJson TryLoadMetadata(string datasetRoot, string sourceFolder, string tileId)
        {
            if (string.IsNullOrEmpty(datasetRoot) || string.IsNullOrEmpty(tileId))
                return null;

            string folder = string.IsNullOrEmpty(sourceFolder)
                ? SpatialStreamingPaths.BuildingMeshesSourceFolder
                : sourceFolder;
            string jsonPath = Path.Combine(
                datasetRoot,
                folder,
                $"buildings_{tileId}.json");

            if (!File.Exists(jsonPath))
                return null;

            try
            {
                return JsonConvert.DeserializeObject<BuildingsMetadataJson>(File.ReadAllText(jsonPath));
            }
            catch
            {
                return null;
            }
        }

        public static void PrepareTileRootForBake(
            Transform tileRoot,
            string tileId,
            Vector3 tileUnityPosition,
            int tileSizeMeters,
            BuildingsMetadataJson meta,
            Vector2Int manifestUnityOrigin)
        {
            if (tileRoot == null)
                return;

            if (!ShouldSkipOriginCorrection(meta) && NeedsLegacyOriginCorrection(tileRoot, tileUnityPosition, tileSizeMeters))
            {
                Vector3 correction = ComputeGlbOriginCorrection(
                    tileRoot,
                    BuildMetadataLookup(meta),
                    tileId,
                    tileUnityPosition,
                    manifestUnityOrigin,
                    meta);

                if (correction.sqrMagnitude > 0.01f)
                    tileRoot.position -= correction;
            }

            AlignTileRootToManifestOrigin(tileRoot, tileUnityPosition);
        }

        public static void AlignTileRootToManifestOrigin(Transform tileRoot, Vector3 tileUnityPosition)
        {
            if (tileRoot == null)
                return;

            int childCount = tileRoot.childCount;
            if (childCount == 0)
            {
                tileRoot.position = tileUnityPosition;
                return;
            }

            var worldPositions = new Vector3[childCount];
            var worldRotations = new Quaternion[childCount];
            var localScales = new Vector3[childCount];

            for (int i = 0; i < childCount; i++)
            {
                Transform child = tileRoot.GetChild(i);
                worldPositions[i] = child.position;
                worldRotations[i] = child.rotation;
                localScales[i] = child.localScale;
            }

            tileRoot.SetPositionAndRotation(tileUnityPosition, Quaternion.identity);

            for (int i = 0; i < childCount; i++)
            {
                Transform child = tileRoot.GetChild(i);
                child.SetPositionAndRotation(worldPositions[i], worldRotations[i]);
                child.localScale = localScales[i];
            }
        }

        static bool ShouldSkipOriginCorrection(BuildingsMetadataJson meta) =>
            meta != null && meta.SurfaceBaked;

        static bool NeedsLegacyOriginCorrection(Transform root, Vector3 tileUnityPosition, int tileSizeMeters)
        {
            if (!TryGetRendererBounds(root, out Bounds bounds))
                return false;

            if (IsWithinTileFootprint(bounds, tileUnityPosition, tileSizeMeters))
                return false;

            return bounds.center.x > 10000f || bounds.center.z > 10000f;
        }

        static bool IsWithinTileFootprint(Bounds bounds, Vector3 tileUnityPosition, int tileSizeMeters)
        {
            float minX = tileUnityPosition.x;
            float maxX = tileUnityPosition.x + tileSizeMeters;
            float minZ = tileUnityPosition.z;
            float maxZ = tileUnityPosition.z + tileSizeMeters;

            const float margin = 2f;
            return bounds.min.x >= minX - margin
                   && bounds.max.x <= maxX + margin
                   && bounds.min.z >= minZ - margin
                   && bounds.max.z <= maxZ + margin;
        }

        static Vector3 ComputeGlbOriginCorrection(
            Transform root,
            Dictionary<string, BuildingEntryJson> lookup,
            string tileId,
            Vector3 tileUnityPosition,
            Vector2Int manifestUnityOrigin,
            BuildingsMetadataJson meta)
        {
            if (TryFindMetadataCorrection(root, lookup, out Vector3 metadataCorrection))
                return metadataCorrection;

            if (!TryParseTileId(tileId, out int tileLeft, out int tileBottom))
                return Vector3.zero;

            float originX = meta?.TileOriginUnity != null
                ? meta.TileOriginUnity.X
                : manifestUnityOrigin.x != 0 || manifestUnityOrigin.y != 0
                    ? manifestUnityOrigin.x
                    : tileLeft - tileUnityPosition.x;

            float originZ = meta?.TileOriginUnity != null
                ? meta.TileOriginUnity.Z
                : manifestUnityOrigin.x != 0 || manifestUnityOrigin.y != 0
                    ? manifestUnityOrigin.y
                    : tileBottom - tileUnityPosition.z;

            float newOx = tileLeft - originX;
            float newOz = tileBottom - originZ;
            return new Vector3(newOx - GlbOriginX, 0f, newOz - GlbOriginZ);
        }

        static bool TryFindMetadataCorrection(
            Transform root,
            Dictionary<string, BuildingEntryJson> lookup,
            out Vector3 correction)
        {
            correction = Vector3.zero;
            if (lookup == null || lookup.Count == 0)
                return false;

            var stack = new Stack<Transform>();
            for (int i = 0; i < root.childCount; i++)
                stack.Push(root.GetChild(i));

            while (stack.Count > 0)
            {
                Transform t = stack.Pop();
                if (TryGetMetadataEntry(t.name, lookup, out BuildingEntryJson entry) &&
                    entry.LocalPosition != null)
                {
                    correction = new Vector3(
                        t.position.x - entry.LocalPosition.X,
                        0f,
                        t.position.z - entry.LocalPosition.Z);
                    return true;
                }

                for (int i = 0; i < t.childCount; i++)
                    stack.Push(t.GetChild(i));
            }

            return false;
        }

        static bool TryGetRendererBounds(Transform root, out Bounds bounds)
        {
            bounds = default;
            bool hasBounds = false;

            foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return hasBounds;
        }

        static bool TryParseTileId(string tileId, out int tileLeft, out int tileBottom)
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
            }

            return lookup;
        }

        static bool TryGetMetadataEntry(
            string transformName,
            Dictionary<string, BuildingEntryJson> lookup,
            out BuildingEntryJson entry)
        {
            entry = null;
            if (lookup == null || string.IsNullOrEmpty(transformName))
                return false;

            return lookup.TryGetValue(transformName, out entry);
        }
    }
}
