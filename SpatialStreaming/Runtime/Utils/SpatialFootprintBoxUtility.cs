using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
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

        static Mesh _facadeRoofUnitBoxMesh;

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

        sealed class FootprintCategoryGroup
        {
            public BuildingCategory Category;
            public readonly List<CombineInstance> FacadeInstances = new();
            public readonly List<CombineInstance> RoofInstances = new();
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

            Mesh unitBox = GetFacadeRoofUnitBoxMesh();
            var groups = new Dictionary<BuildingCategory, FootprintCategoryGroup>();

            foreach (Transform building in buildings)
            {
                if (building == null || !TryGetFloorExtrusionMatrix(building, out Matrix4x4 extrusionMatrix))
                    continue;

                BuildingCategory category = BuildingCategory.House;
                if (settings != null &&
                    SpatialBuildingMaterialApplier.TryResolveBuildingProxyMaterials(
                        building,
                        tileId,
                        settings,
                        out _,
                        out _,
                        out string combinedKey) &&
                    BuildingSurfaceUtility.TryResolveCategoryAndSurfaceFromSlotKey(
                        combinedKey,
                        out BuildingCategory resolvedCategory,
                        out _))
                {
                    category = resolvedCategory;
                }

                if (!groups.TryGetValue(category, out FootprintCategoryGroup group))
                {
                    group = new FootprintCategoryGroup { Category = category };
                    groups[category] = group;
                }

                Matrix4x4 localMatrix = bakeRoot.worldToLocalMatrix * extrusionMatrix;
                group.FacadeInstances.Add(new CombineInstance
                {
                    mesh = unitBox,
                    transform = localMatrix,
                });
                group.RoofInstances.Add(new CombineInstance
                {
                    mesh = unitBox,
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
            foreach (KeyValuePair<BuildingCategory, FootprintCategoryGroup> kvp in groups)
            {
                FootprintCategoryGroup group = kvp.Value;
                BuildingCategory category = group.Category;
                BuildingSurfaceMaterialType roofSurface = SpatialBuildingMaterialApplier.ResolveProxyRoofSurfaceType(
                    settings,
                    category);

                if (TryCombineFootprintSubmesh(
                        group.FacadeInstances,
                        subMeshIndex: 0,
                        mergeSubMeshes: true,
                        BuildFootprintProxyMeshName(category, BuildingSurfaceMaterialType.Facade),
                        ownedMeshes,
                        out Mesh facadeMesh) &&
                    facadeMesh != null)
                {
                    CreateCombinedProxyRenderer(
                        root.transform,
                        BuildCombinedProxyObjectName(category, BuildingSurfaceMaterialType.Facade),
                        facadeMesh,
                        category,
                        BuildingSurfaceMaterialType.Facade,
                        fallbackMaterial);
                }

                if (TryCombineFootprintSubmesh(
                        group.RoofInstances,
                        subMeshIndex: 1,
                        mergeSubMeshes: true,
                        BuildFootprintProxyMeshName(category, roofSurface),
                        ownedMeshes,
                        out Mesh roofMesh) &&
                    roofMesh != null)
                {
                    CreateCombinedProxyRenderer(
                        root.transform,
                        BuildCombinedProxyObjectName(category, roofSurface),
                        roofMesh,
                        category,
                        roofSurface,
                        fallbackMaterial);
                }

                groupIndex++;
            }

            return root.transform.childCount > 0 ? root : null;
        }

        public static bool TryGetFloorExtrusionMatrix(Transform building, out Matrix4x4 worldMatrix)
        {
            worldMatrix = Matrix4x4.identity;
            if (building == null || !TryComputeFloorExtrusion(building, out worldMatrix))
                return false;

            return true;
        }

        static bool TryComputeFloorExtrusion(Transform building, out Matrix4x4 worldMatrix)
        {
            worldMatrix = Matrix4x4.identity;

            var worldPoints = new List<Vector3>(512);
            CollectBuildingPointsWorld(building, worldPoints);
            if (worldPoints.Count == 0)
                return false;

            float minY = float.MaxValue;
            float maxY = float.MinValue;
            for (int i = 0; i < worldPoints.Count; i++)
            {
                float y = worldPoints[i].y;
                minY = Mathf.Min(minY, y);
                maxY = Mathf.Max(maxY, y);
            }

            float height = maxY - minY;
            if (height < 0.05f)
                return false;

            float floorCeiling = minY + Mathf.Max(FloorBandMinMeters, height * FloorBandHeightRatio);
            float yawDegrees = building.eulerAngles.y;
            Quaternion buildingYaw = Quaternion.Euler(0f, yawDegrees, 0f);
            Quaternion invBuildingYaw = Quaternion.Inverse(buildingYaw);
            Vector3 buildingOrigin = building.position;

            var localFootprint = new List<Vector2>(128);
            for (int i = 0; i < worldPoints.Count; i++)
            {
                Vector3 point = worldPoints[i];
                if (point.y > floorCeiling)
                    continue;

                Vector3 local = invBuildingYaw * (point - buildingOrigin);
                localFootprint.Add(new Vector2(local.x, local.z));
            }

            if (localFootprint.Count < 3)
            {
                localFootprint.Clear();
                for (int i = 0; i < worldPoints.Count; i++)
                {
                    Vector3 local = invBuildingYaw * (worldPoints[i] - buildingOrigin);
                    localFootprint.Add(new Vector2(local.x, local.z));
                }
            }

            if (localFootprint.Count == 0)
                return false;

            ComputeBuildingLocalFootprintExtents(
                localFootprint,
                out Vector2 localCenter,
                out Vector2 localHalfExtents);

            Vector3 worldCenter = buildingOrigin + buildingYaw * new Vector3(localCenter.x, 0f, localCenter.y);
            worldCenter.y = minY + Mathf.Max(MinHeightMeters, height) * 0.5f;

            Vector3 size = new Vector3(
                Mathf.Max(MinFootprintMeters, localHalfExtents.x * 2f),
                Mathf.Max(MinHeightMeters, height),
                Mathf.Max(MinFootprintMeters, localHalfExtents.y * 2f));

            worldMatrix = Matrix4x4.TRS(worldCenter, buildingYaw, size);
            return true;
        }

        static void ComputeBuildingLocalFootprintExtents(
            List<Vector2> localFootprint,
            out Vector2 localCenter,
            out Vector2 localHalfExtents)
        {
            localCenter = Vector2.zero;
            localHalfExtents = new Vector2(MinFootprintMeters * 0.5f, MinFootprintMeters * 0.5f);

            if (localFootprint == null || localFootprint.Count == 0)
                return;

            float minX = float.MaxValue;
            float maxX = float.MinValue;
            float minZ = float.MaxValue;
            float maxZ = float.MinValue;
            for (int i = 0; i < localFootprint.Count; i++)
            {
                Vector2 point = localFootprint[i];
                minX = Mathf.Min(minX, point.x);
                maxX = Mathf.Max(maxX, point.x);
                minZ = Mathf.Min(minZ, point.y);
                maxZ = Mathf.Max(maxZ, point.y);
            }

            localCenter = new Vector2((minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f);
            localHalfExtents = new Vector2(
                Mathf.Max(MinFootprintMeters * 0.5f, (maxX - minX) * 0.5f),
                Mathf.Max(MinFootprintMeters * 0.5f, (maxZ - minZ) * 0.5f));
        }

        static void CollectBuildingPointsWorld(Transform building, List<Vector3> points)
        {
            foreach (MeshFilter filter in building.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter?.sharedMesh == null)
                    continue;

                Matrix4x4 meshToWorld = filter.transform.localToWorldMatrix;
                Mesh mesh = filter.sharedMesh;
                if (TryAppendMeshVertices(meshToWorld, mesh, points))
                    continue;

                if (mesh.isReadable)
                {
                    Vector3[] vertices = mesh.vertices;
                    for (int i = 0; i < vertices.Length; i++)
                        points.Add(meshToWorld.MultiplyPoint(vertices[i]));
                    continue;
                }

                AppendBoundsCorners(meshToWorld, mesh.bounds, points);
            }

            if (points.Count > 0)
                return;

            foreach (MeshRenderer renderer in building.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (renderer == null)
                    continue;

                AppendWorldBoundsCorners(renderer.bounds, points);
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
                points.Add(matrix.MultiplyPoint(new Vector3(
                    ix == 0 ? min.x : max.x,
                    iy == 0 ? min.y : max.y,
                    iz == 0 ? min.z : max.z)));
            }
        }

        static void AppendWorldBoundsCorners(Bounds bounds, List<Vector3> points)
        {
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            for (int ix = 0; ix < 2; ix++)
            for (int iy = 0; iy < 2; iy++)
            for (int iz = 0; iz < 2; iz++)
            {
                points.Add(new Vector3(
                    ix == 0 ? min.x : max.x,
                    iy == 0 ? min.y : max.y,
                    iz == 0 ? min.z : max.z));
            }
        }

        static bool TryAppendMeshVertices(Matrix4x4 matrix, Mesh mesh, List<Vector3> points)
        {
            if (mesh == null)
                return false;

            using Mesh.MeshDataArray dataArray = Mesh.AcquireReadOnlyMeshData(mesh);
            if (dataArray.Length == 0)
                return false;

            Mesh.MeshData data = dataArray[0];
            int vertCount = data.vertexCount;
            if (vertCount == 0)
                return false;

            var vertices = new NativeArray<Vector3>(vertCount, Allocator.Temp);
            try
            {
                data.GetVertices(vertices);
                for (int i = 0; i < vertCount; i++)
                    points.Add(matrix.MultiplyPoint(vertices[i]));
            }
            finally
            {
                vertices.Dispose();
            }

            return true;
        }

        static bool TryCombineFootprintSubmesh(
            List<CombineInstance> boxInstances,
            int subMeshIndex,
            bool mergeSubMeshes,
            string meshName,
            ICollection<Mesh> ownedMeshes,
            out Mesh combinedMesh)
        {
            combinedMesh = null;
            if (boxInstances == null || boxInstances.Count == 0)
                return false;

            var submeshInstances = new List<CombineInstance>(boxInstances.Count);
            foreach (CombineInstance instance in boxInstances)
            {
                if (instance.mesh == null)
                    continue;

                submeshInstances.Add(new CombineInstance
                {
                    mesh = instance.mesh,
                    subMeshIndex = subMeshIndex,
                    transform = instance.transform,
                });
            }

            if (submeshInstances.Count == 0)
                return false;

            combinedMesh = SpatialMeshCombineUtility.CombineMeshesInSpace(
                submeshInstances,
                mergeSubMeshes,
                meshName,
                ownedMeshes);
            return combinedMesh != null;
        }

        static void CreateCombinedProxyRenderer(
            Transform parent,
            string objectName,
            Mesh mesh,
            BuildingCategory category,
            BuildingSurfaceMaterialType surfaceType,
            Material placeholderMaterial)
        {
            if (parent == null || mesh == null)
                return;

            var child = new GameObject(objectName);
            child.transform.SetParent(parent, false);
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = child.AddComponent<MeshRenderer>();
            if (placeholderMaterial != null)
                renderer.sharedMaterial = placeholderMaterial;

            var hint = child.AddComponent<SpatialFootprintProxySurfaceHint>();
            hint.category = category;
            hint.surfaceType = surfaceType;
        }

        public static string BuildFootprintProxyMeshName(
            BuildingCategory category,
            BuildingSurfaceMaterialType surfaceType) =>
            "FootprintProxy_" +
            BuildingSurfaceUtility.CategoryToSlotToken(category) + "_" +
            BuildingSurfaceUtility.SurfaceToSlotToken(surfaceType);

        public static string BuildCombinedProxyObjectName(
            BuildingCategory category,
            BuildingSurfaceMaterialType surfaceType)
        {
            string slot = BuildingSurfaceUtility.CategoryToSlotToken(category) + "_" +
                          BuildingSurfaceUtility.SurfaceToSlotToken(surfaceType);
            return "Combined_" + slot;
        }

        public static bool TryResolveProxySurfaceHint(
            MeshRenderer renderer,
            out BuildingCategory category,
            out BuildingSurfaceMaterialType surfaceType)
        {
            category = BuildingCategory.House;
            surfaceType = BuildingSurfaceMaterialType.Facade;

            if (renderer == null)
                return false;

            if (renderer.TryGetComponent(out SpatialFootprintProxySurfaceHint hint))
            {
                category = hint.category;
                surfaceType = hint.surfaceType;
                return true;
            }

            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            string meshName = filter != null && filter.sharedMesh != null
                ? filter.sharedMesh.name
                : null;

            if (!string.IsNullOrEmpty(meshName) &&
                meshName.StartsWith("FootprintProxy_", System.StringComparison.Ordinal))
            {
                string slotKey = meshName.Substring("FootprintProxy_".Length);
                if (BuildingSurfaceUtility.TryResolveCategoryAndSurfaceFromSlotKey(
                        slotKey,
                        out category,
                        out surfaceType))
                {
                    return true;
                }
            }

            if (BuildingSurfaceUtility.TryResolveCategoryAndSurfaceFromSlotKey(
                    StripCombinedNamePrefix(renderer.gameObject.name),
                    out category,
                    out surfaceType))
            {
                return true;
            }

            return false;
        }

        static string StripCombinedNamePrefix(string objectName)
        {
            if (string.IsNullOrEmpty(objectName))
                return objectName;

            const string prefix = "Combined_";
            return objectName.StartsWith(prefix, System.StringComparison.Ordinal)
                ? objectName.Substring(prefix.Length)
                : objectName;
        }

        static Mesh GetFacadeRoofUnitBoxMesh()
        {
            if (_facadeRoofUnitBoxMesh != null)
                return _facadeRoofUnitBoxMesh;

            var mesh = new Mesh { name = "SpatialFootprintFacadeRoofUnitBox" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f),
                new Vector3(0.5f, 0.5f, -0.5f), new Vector3(-0.5f, 0.5f, -0.5f),
                new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(0.5f, -0.5f, 0.5f),
                new Vector3(0.5f, 0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.5f),
            };

            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[]
            {
                0, 2, 1, 0, 3, 2,
                1, 6, 5, 1, 2, 6,
                5, 7, 4, 5, 6, 7,
                4, 3, 0, 4, 7, 3,
                4, 1, 5, 4, 0, 1,
            }, 0);
            mesh.SetTriangles(new[]
            {
                3, 6, 2, 3, 7, 6,
            }, 1);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh.UploadMeshData(markNoLongerReadable: false);
            _facadeRoofUnitBoxMesh = mesh;
            return _facadeRoofUnitBoxMesh;
        }
    }
}
