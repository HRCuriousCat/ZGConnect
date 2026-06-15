using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace ZGConnect.RealtimeStreaming
{
    /// <summary>Runtime mesh copy/combine helpers for streamed building tiles.</summary>
    public static class RuntimeBuildingMeshUtility
    {
        public static Mesh CloneMeshGeometry(Mesh source)
        {
            if (source == null)
                return null;

            if (source.isReadable)
                return CopyReadableMesh(source);

            using Mesh.MeshDataArray dataArray = Mesh.AcquireReadOnlyMeshData(source);
            if (dataArray.Length == 0)
                return null;

            Mesh.MeshData data = dataArray[0];
            int vertCount = data.vertexCount;
            if (vertCount == 0)
                return null;

            var dst = new Mesh { name = source.name + "_Runtime" };

            var vertices = new NativeArray<Vector3>(vertCount, Allocator.Temp);
            try
            {
                data.GetVertices(vertices);
                dst.vertices = vertices.ToArray();
            }
            finally
            {
                vertices.Dispose();
            }

            if (data.HasVertexAttribute(VertexAttribute.Normal))
            {
                var normals = new NativeArray<Vector3>(vertCount, Allocator.Temp);
                try
                {
                    data.GetNormals(normals);
                    dst.normals = normals.ToArray();
                }
                finally
                {
                    normals.Dispose();
                }
            }

            if (data.HasVertexAttribute(VertexAttribute.TexCoord0))
            {
                var uvs = new NativeArray<Vector2>(vertCount, Allocator.Temp);
                try
                {
                    data.GetUVs(0, uvs);
                    dst.uv = uvs.ToArray();
                }
                finally
                {
                    uvs.Dispose();
                }
            }

            dst.subMeshCount = data.subMeshCount;
            for (int s = 0; s < data.subMeshCount; s++)
            {
                SubMeshDescriptor subMesh = data.GetSubMesh(s);
                var indices = new NativeArray<int>((int)subMesh.indexCount, Allocator.Temp);
                try
                {
                    data.GetIndices(indices, s);
                    dst.SetTriangles(indices.ToArray(), s);
                }
                finally
                {
                    indices.Dispose();
                }
            }

            if (!data.HasVertexAttribute(VertexAttribute.Normal))
                dst.RecalculateNormals();

            dst.RecalculateBounds();
            return dst;
        }

        public static Mesh CombineMeshesInSpace(
            IList<CombineInstance> instances,
            bool mergeSubMeshes,
            string meshName,
            ICollection<Mesh> ownedClones = null)
        {
            if (instances == null || instances.Count == 0)
                return null;

            var array = new CombineInstance[instances.Count];
            for (int i = 0; i < instances.Count; i++)
            {
                CombineInstance ci = instances[i];
                if (ci.mesh != null && !ci.mesh.isReadable)
                {
                    Mesh readable = CloneMeshGeometry(ci.mesh);
                    if (readable != null)
                    {
                        ci.mesh = readable;
                        ownedClones?.Add(readable);
                    }
                }

                array[i] = ci;
            }

            var combined = new Mesh { name = meshName, indexFormat = IndexFormat.UInt32 };
            combined.CombineMeshes(array, mergeSubMeshes, true);
            combined.RecalculateBounds();
            return combined;
        }

        public static Mesh CombineBuildingLod0Meshes(Transform building, Transform spaceRoot)
        {
            if (building == null || spaceRoot == null)
                return null;

            var instances = new List<CombineInstance>();
            Matrix4x4 rootToLocal = spaceRoot.worldToLocalMatrix;

            foreach (MeshRenderer renderer in GetLod0Renderers(building))
            {
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null)
                    continue;

                Mesh mesh = filter.sharedMesh;
                int subMeshCount = Mathf.Max(1, mesh.subMeshCount);
                for (int s = 0; s < subMeshCount; s++)
                {
                    instances.Add(new CombineInstance
                    {
                        mesh = mesh,
                        subMeshIndex = s,
                        transform = rootToLocal * filter.transform.localToWorldMatrix,
                    });
                }
            }

            return CombineMeshesInSpace(instances, mergeSubMeshes: true, $"{building.name}_ColliderMesh");
        }

        public static Bounds ComputeLod0WorldBounds(Transform building)
        {
            bool hasBounds = false;
            var bounds = new Bounds(building.position, Vector3.zero);

            foreach (MeshRenderer renderer in GetLod0Renderers(building))
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

            return bounds;
        }

        public static IEnumerable<MeshRenderer> GetLod0Renderers(Transform building)
        {
            if (building == null)
                yield break;

            Transform lod0 = building.Find("LOD0");
            Transform lod1 = building.Find("LOD1");

            if (lod0 != null)
            {
                foreach (MeshRenderer renderer in lod0.GetComponentsInChildren<MeshRenderer>(true))
                    yield return renderer;
                yield break;
            }

            foreach (MeshRenderer renderer in building.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (lod1 != null && renderer.transform.IsChildOf(lod1))
                    continue;

                yield return renderer;
            }
        }

        public static bool IsSuitableForConvexCollider(
            Mesh mesh,
            float positionEpsilon = 0.001f,
            float minExtent = 0.01f)
        {
            if (mesh == null || mesh.vertexCount < 4)
                return false;

            Vector3[] vertices = mesh.vertices;
            if (vertices == null || vertices.Length < 4)
                return false;

            if (CountUniquePositions(vertices, positionEpsilon) < 4)
                return false;

            if (GetTriangleCount(mesh) < 4)
                return false;

            Vector3 size = mesh.bounds.size;
            if (Mathf.Max(size.x, Mathf.Max(size.y, size.z)) < minExtent)
                return false;

            return !AreVerticesCoplanar(vertices, positionEpsilon);
        }

        public static bool CanBakeConvexCollider(Mesh mesh)
        {
            if (!IsSuitableForConvexCollider(mesh))
                return false;

            return TryBakeConvexMesh(mesh);
        }

        static bool TryBakeConvexMesh(Mesh mesh)
        {
            Mesh testMesh = Object.Instantiate(mesh);
            testMesh.name = mesh.name + "_ConvexBakeTest";
            try
            {
                bool bakeFailed = false;
                Application.LogCallback onLog = (condition, stackTrace, type) =>
                {
                    if (type != LogType.Error && type != LogType.Assert)
                        return;

                    string lower = condition.ToLowerInvariant();
                    if (lower.Contains("physx")
                        || lower.Contains("quickhull")
                        || lower.Contains("unable to cook"))
                    {
                        bakeFailed = true;
                    }
                };

                Application.logMessageReceived += onLog;
                try
                {
                    Physics.BakeMesh(testMesh.GetEntityId(), true);
                }
                finally
                {
                    Application.logMessageReceived -= onLog;
                }

                return !bakeFailed;
            }
            finally
            {
                RuntimeObjectUtility.Destroy(testMesh);
            }
        }

        public static int GetTriangleCount(Mesh mesh)
        {
            if (mesh == null)
                return 0;

            int count = 0;
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                count += mesh.GetTriangles(subMesh).Length / 3;

            return count;
        }

        static int CountUniquePositions(Vector3[] vertices, float epsilon)
        {
            float epsilonSq = epsilon * epsilon;
            var unique = new List<Vector3>(vertices.Length);
            foreach (Vector3 vertex in vertices)
            {
                bool found = false;
                for (int i = 0; i < unique.Count; i++)
                {
                    if ((unique[i] - vertex).sqrMagnitude <= epsilonSq)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                    unique.Add(vertex);
            }

            return unique.Count;
        }

        static bool AreVerticesCoplanar(Vector3[] vertices, float epsilon)
        {
            if (vertices == null || vertices.Length < 4)
                return true;

            Vector3? p0 = null;
            Vector3? p1 = null;
            Vector3? p2 = null;
            float epsilonSq = epsilon * epsilon;

            foreach (Vector3 vertex in vertices)
            {
                if (!p0.HasValue)
                {
                    p0 = vertex;
                    continue;
                }

                if (!p1.HasValue)
                {
                    if ((vertex - p0.Value).sqrMagnitude > epsilonSq)
                        p1 = vertex;
                    continue;
                }

                if (!p2.HasValue)
                {
                    Vector3 cross = Vector3.Cross(p1.Value - p0.Value, vertex - p0.Value);
                    if (cross.sqrMagnitude > epsilonSq * epsilonSq)
                        p2 = vertex;
                }
            }

            if (!p2.HasValue)
                return true;

            Vector3 normal = Vector3.Cross(p1.Value - p0.Value, p2.Value - p0.Value).normalized;
            float planeDistance = -Vector3.Dot(normal, p0.Value);
            float planeEpsilon = epsilon * 10f;

            foreach (Vector3 vertex in vertices)
            {
                float distance = Mathf.Abs(Vector3.Dot(normal, vertex) + planeDistance);
                if (distance > planeEpsilon)
                    return false;
            }

            return true;
        }

        static Mesh CopyReadableMesh(Mesh source)
        {
            var dst = new Mesh { name = source.name + "_Runtime" };
            dst.vertices = source.vertices;

            if (source.normals != null && source.normals.Length == source.vertexCount)
                dst.normals = source.normals;
            else
                dst.RecalculateNormals();

            if (source.uv != null && source.uv.Length == source.vertexCount)
                dst.uv = source.uv;

            dst.subMeshCount = source.subMeshCount;
            for (int s = 0; s < source.subMeshCount; s++)
                dst.SetTriangles(source.GetTriangles(s), s);

            dst.RecalculateBounds();
            return dst;
        }
    }

}
