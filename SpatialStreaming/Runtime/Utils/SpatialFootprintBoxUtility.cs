using System.Collections.Generic;
using UnityEngine;
using ZGConnect;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// Floor-aligned footprint extrusions from building mesh geometry (proxy / HLOD).
    /// </summary>
    public static class SpatialFootprintBoxUtility
    {
        const float MinFootprintMeters = 0.5f;
        const float MinHeightMeters = 1f;
        const float FloorBandHeightRatio = 0.12f;
        const float FloorBandMinMeters = 0.35f;

        static Mesh _unitBoxMesh;

        public static GameObject BuildCombinedFootprintRoot(
            Transform bakeRoot,
            IReadOnlyList<Transform> buildings,
            Material proxyMaterial,
            ICollection<Mesh> ownedMeshes)
        {
            return BuildCombinedFootprintRootInternal(
                bakeRoot, buildings, null, null, proxyMaterial, ownedMeshes);
        }

        public static GameObject BuildCombinedFootprintRoot(
            Transform bakeRoot,
            IReadOnlyList<Transform> buildings,
            string tileId,
            BuildingSurfaceSettings settings,
            Material fallbackMaterial,
            ICollection<Mesh> ownedMeshes)
        {
            return BuildCombinedFootprintRootInternal(
                bakeRoot, buildings, tileId, settings, fallbackMaterial, ownedMeshes);
        }

        static GameObject BuildCombinedFootprintRootInternal(
            Transform bakeRoot,
            IReadOnlyList<Transform> buildings,
            string tileId,
            BuildingSurfaceSettings settings,
            Material fallbackMaterial,
            ICollection<Mesh> ownedMeshes)
        {
            if (bakeRoot == null || buildings == null || buildings.Count == 0)
                return null;

            Mesh unitBox = GetUnitBoxMesh();
            var groups = new Dictionary<string, FootprintMaterialGroup>(System.StringComparer.Ordinal);

            foreach (Transform building in buildings)
            {
                if (building == null || !TryGetFloorExtrusionMatrix(building, out Matrix4x4 extrusionMatrix))
                    continue;

                Material material = fallbackMaterial;
                string groupKey = material != null
                    ? BuildingSurfaceUtility.NormalizeMaterialName(material.name)
                    : "proxy";

                if (settings != null &&
                    SpatialBuildingMaterialApplier.TryResolveBuildingProxySurface(
                        building, tileId, settings, out Material resolved, out string combinedKey))
                {
                    material = resolved;
                    groupKey = combinedKey;
                }

                if (material == null)
                    continue;

                if (!groups.TryGetValue(groupKey, out FootprintMaterialGroup group))
                {
                    group = new FootprintMaterialGroup { Material = material };
                    groups[groupKey] = group;
                }

                Matrix4x4 localMatrix = bakeRoot.worldToLocalMatrix * extrusionMatrix;
                group.Instances.Add(new CombineInstance
                {
                    mesh = unitBox,
                    subMeshIndex = 0,
                    transform = localMatrix,
                });
            }

            if (groups.Count == 0)
                return null;

            var root = new GameObject(SpatialMeshCombineUtility.CombinedRenderRootName);
            root.transform.SetParent(bakeRoot, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            int groupIndex = 0;
            foreach (KeyValuePair<string, FootprintMaterialGroup> kvp in groups)
            {
                FootprintMaterialGroup group = kvp.Value;
                if (group.Instances.Count == 0)
                    continue;

                Mesh combined = SpatialMeshCombineUtility.CombineMeshesInSpace(
                    group.Instances,
                    mergeSubMeshes: true,
                    $"FootprintProxy_{groupIndex++}",
                    ownedMeshes);

                if (combined == null)
                    continue;

                var child = new GameObject($"Combined_{kvp.Key}");
                child.transform.SetParent(root.transform, false);
                child.AddComponent<MeshFilter>().sharedMesh = combined;
                child.AddComponent<MeshRenderer>().sharedMaterial = group.Material;
            }

            return root.transform.childCount > 0 ? root : null;
        }

        public static bool TryGetFloorExtrusionMatrix(Transform building, out Matrix4x4 worldMatrix)
        {
            worldMatrix = Matrix4x4.identity;
            if (building == null ||
                !TryComputeFloorExtrusion(building, out Vector3 centerLocal, out Vector3 sizeLocal, out Quaternion rotationLocal))
            {
                return false;
            }

            worldMatrix = building.localToWorldMatrix * Matrix4x4.TRS(centerLocal, rotationLocal, sizeLocal);
            return true;
        }

        static bool TryComputeFloorExtrusion(
            Transform building,
            out Vector3 centerLocal,
            out Vector3 sizeLocal,
            out Quaternion rotationLocal)
        {
            centerLocal = Vector3.zero;
            sizeLocal = Vector3.one;
            rotationLocal = Quaternion.identity;

            var points = new List<Vector3>(256);
            CollectBuildingPointsLocal(building, points);
            if (points.Count == 0)
                return false;

            float minY = float.MaxValue;
            float maxY = float.MinValue;
            for (int i = 0; i < points.Count; i++)
            {
                float y = points[i].y;
                minY = Mathf.Min(minY, y);
                maxY = Mathf.Max(maxY, y);
            }

            float height = maxY - minY;
            if (height < 0.05f)
                return false;

            float floorCeiling = minY + Mathf.Max(FloorBandMinMeters, height * FloorBandHeightRatio);
            var footprint = new List<Vector2>(128);
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 point = points[i];
                if (point.y <= floorCeiling)
                    footprint.Add(new Vector2(point.x, point.z));
            }

            if (footprint.Count < 3)
            {
                footprint.Clear();
                for (int i = 0; i < points.Count; i++)
                    footprint.Add(new Vector2(points[i].x, points[i].z));
            }

            ComputeOrientedFootprint(footprint, out Vector2 footprintCenter, out Vector2 halfExtents, out float yawRadians);

            sizeLocal = new Vector3(
                Mathf.Max(MinFootprintMeters, halfExtents.x * 2f),
                Mathf.Max(MinHeightMeters, height),
                Mathf.Max(MinFootprintMeters, halfExtents.y * 2f));

            centerLocal = new Vector3(
                footprintCenter.x,
                minY + sizeLocal.y * 0.5f,
                footprintCenter.y);

            rotationLocal = Quaternion.Euler(0f, yawRadians * Mathf.Rad2Deg, 0f);
            return true;
        }

        static void CollectBuildingPointsLocal(Transform building, List<Vector3> points)
        {
            Matrix4x4 toBuilding = building.worldToLocalMatrix;

            foreach (MeshFilter filter in building.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter?.sharedMesh == null)
                    continue;

                Matrix4x4 meshToBuilding = toBuilding * filter.transform.localToWorldMatrix;
                Mesh mesh = filter.sharedMesh;
                if (mesh.isReadable)
                {
                    Vector3[] vertices = mesh.vertices;
                    for (int i = 0; i < vertices.Length; i++)
                        points.Add(meshToBuilding.MultiplyPoint3x4(vertices[i]));
                }
                else
                {
                    AppendBoundsCorners(meshToBuilding, mesh.bounds, points);
                }
            }

            if (points.Count > 0)
                return;

            foreach (MeshRenderer renderer in building.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (renderer != null)
                    AppendWorldBoundsCorners(toBuilding, renderer.bounds, points);
            }
        }

        static void AppendBoundsCorners(Matrix4x4 matrix, Bounds bounds, List<Vector3> points)
        {
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            for (int ix = 0; ix < 2; ix++)
            for (int iy = 0; iy < 2; iy++)
            for (int iz = 0; iz < 2; iz++)
            {
                points.Add(matrix.MultiplyPoint3x4(new Vector3(
                    ix == 0 ? min.x : max.x,
                    iy == 0 ? min.y : max.y,
                    iz == 0 ? min.z : max.z)));
            }
        }

        static void AppendWorldBoundsCorners(Matrix4x4 matrix, Bounds bounds, List<Vector3> points) =>
            AppendBoundsCorners(matrix, bounds, points);

        static void ComputeOrientedFootprint(
            List<Vector2> points,
            out Vector2 center,
            out Vector2 halfExtents,
            out float yawRadians)
        {
            center = Vector2.zero;
            halfExtents = new Vector2(MinFootprintMeters * 0.5f, MinFootprintMeters * 0.5f);
            yawRadians = 0f;

            if (points == null || points.Count == 0)
                return;

            for (int i = 0; i < points.Count; i++)
                center += points[i];
            center /= points.Count;

            float cxx = 0f;
            float czz = 0f;
            float cxz = 0f;
            for (int i = 0; i < points.Count; i++)
            {
                float x = points[i].x - center.x;
                float z = points[i].y - center.y;
                cxx += x * x;
                czz += z * z;
                cxz += x * z;
            }

            float invCount = 1f / points.Count;
            cxx *= invCount;
            czz *= invCount;
            cxz *= invCount;

            Vector2 axis1;
            if (Mathf.Abs(cxz) > 1e-6f)
            {
                float trace = cxx + czz;
                float det = cxx * czz - cxz * cxz;
                float lambda1 = trace * 0.5f + Mathf.Sqrt(Mathf.Max(0f, trace * trace * 0.25f - det));
                axis1 = new Vector2(lambda1 - czz, cxz);
                if (axis1.sqrMagnitude < 1e-8f)
                    axis1 = Vector2.right;
                axis1.Normalize();
            }
            else
            {
                axis1 = cxx >= czz ? Vector2.right : Vector2.up;
            }

            Vector2 axis2 = new Vector2(-axis1.y, axis1.x);

            float min1 = float.MaxValue;
            float max1 = float.MinValue;
            float min2 = float.MaxValue;
            float max2 = float.MinValue;
            for (int i = 0; i < points.Count; i++)
            {
                Vector2 delta = points[i] - center;
                float proj1 = Vector2.Dot(delta, axis1);
                float proj2 = Vector2.Dot(delta, axis2);
                min1 = Mathf.Min(min1, proj1);
                max1 = Mathf.Max(max1, proj1);
                min2 = Mathf.Min(min2, proj2);
                max2 = Mathf.Max(max2, proj2);
            }

            halfExtents = new Vector2(
                Mathf.Max(MinFootprintMeters * 0.5f, (max1 - min1) * 0.5f),
                Mathf.Max(MinFootprintMeters * 0.5f, (max2 - min2) * 0.5f));

            center += axis1 * ((min1 + max1) * 0.5f) + axis2 * ((min2 + max2) * 0.5f);
            yawRadians = Mathf.Atan2(axis1.y, axis1.x);
        }

        sealed class FootprintMaterialGroup
        {
            public Material Material;
            public readonly List<CombineInstance> Instances = new();
        }

        static Mesh GetUnitBoxMesh()
        {
            if (_unitBoxMesh != null)
                return _unitBoxMesh;

            var mesh = new Mesh { name = "SpatialFootprintUnitBox" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f),
                new Vector3(0.5f, 0.5f, -0.5f), new Vector3(-0.5f, 0.5f, -0.5f),
                new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(0.5f, -0.5f, 0.5f),
                new Vector3(0.5f, 0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.5f),
            };
            mesh.triangles = new[]
            {
                0, 2, 1, 0, 3, 2,
                1, 6, 5, 1, 2, 6,
                5, 7, 4, 5, 6, 7,
                4, 3, 0, 4, 7, 3,
                3, 6, 2, 3, 7, 6,
                4, 1, 5, 4, 0, 1,
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            _unitBoxMesh = mesh;
            return _unitBoxMesh;
        }
    }
}
