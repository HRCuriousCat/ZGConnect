using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using ZGConnect;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>Mesh combine helpers for spatial bake (copied from RealtimeStreaming patterns).</summary>
    public static class SpatialMeshCombineUtility
    {
        public const string CombinedRenderRootName = "CombinedRender";

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

        public static GameObject BuildCombinedRenderRoot(
            Transform tileRoot,
            IReadOnlyList<MeshRenderer> renderers,
            bool perMaterial,
            ICollection<Mesh> ownedMeshes)
        {
            if (tileRoot == null || renderers == null || renderers.Count == 0)
                return null;

            var combinedRoot = new GameObject(CombinedRenderRootName);
            combinedRoot.transform.SetParent(tileRoot, false);
            combinedRoot.transform.localPosition = Vector3.zero;
            combinedRoot.transform.localRotation = Quaternion.identity;
            combinedRoot.transform.localScale = Vector3.one;

            Matrix4x4 tileToLocal = tileRoot.worldToLocalMatrix;

            if (perMaterial)
            {
                var groups = new Dictionary<string, MaterialGroup>(System.StringComparer.Ordinal);
                foreach (MeshRenderer renderer in renderers)
                {
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null)
                        continue;

                    Material[] materials = renderer.sharedMaterials;
                    Mesh mesh = filter.sharedMesh;
                    int subMeshCount = Mathf.Max(1, mesh.subMeshCount);
                    Matrix4x4 matrix = tileToLocal * filter.transform.localToWorldMatrix;

                    for (int s = 0; s < subMeshCount; s++)
                    {
                        Material mat = s < materials.Length ? materials[s] : materials[0];
                        if (mat == null)
                            continue;

                        string key = BuildingSurfaceUtility.NormalizeMaterialName(mat.name);
                        if (string.IsNullOrEmpty(key))
                            key = mat.name;

                        if (!groups.TryGetValue(key, out MaterialGroup group))
                        {
                            group = new MaterialGroup { Representative = mat };
                            groups[key] = group;
                        }

                        group.Instances.Add(new CombineInstance
                        {
                            mesh = mesh,
                            subMeshIndex = s,
                            transform = matrix,
                        });
                    }
                }

                int groupIndex = 0;
                foreach (KeyValuePair<string, MaterialGroup> kvp in groups)
                {
                    Mesh combinedMesh = CombineMeshesInSpace(
                        kvp.Value.Instances,
                        mergeSubMeshes: true,
                        $"{tileRoot.name}_Combined_{groupIndex++}",
                        ownedMeshes);
                    if (combinedMesh == null)
                        continue;

                    CreateCombinedRenderer(
                        combinedRoot.transform,
                        combinedMesh,
                        kvp.Value.Representative,
                        ownedMeshes,
                        $"Combined_{kvp.Key}");
                }
            }
            else
            {
                var instances = new List<CombineInstance>();
                var materials = new List<Material>();

                foreach (MeshRenderer renderer in renderers)
                {
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null)
                        continue;

                    Material[] rendererMaterials = renderer.sharedMaterials;
                    Mesh mesh = filter.sharedMesh;
                    int subMeshCount = Mathf.Max(1, mesh.subMeshCount);
                    Matrix4x4 matrix = tileToLocal * filter.transform.localToWorldMatrix;

                    for (int s = 0; s < subMeshCount; s++)
                    {
                        Material mat = s < rendererMaterials.Length ? rendererMaterials[s] : rendererMaterials[0];
                        instances.Add(new CombineInstance
                        {
                            mesh = mesh,
                            subMeshIndex = s,
                            transform = matrix,
                        });
                        materials.Add(mat);
                    }
                }

                Mesh combinedMesh = CombineMeshesInSpace(
                    instances,
                    mergeSubMeshes: false,
                    $"{tileRoot.name}_Combined",
                    ownedMeshes);
                if (combinedMesh != null)
                    CreateCombinedRenderer(combinedRoot.transform, combinedMesh, materials, ownedMeshes);
            }

            return combinedRoot;
        }

        sealed class MaterialGroup
        {
            public Material Representative;
            public readonly List<CombineInstance> Instances = new();
        }

        public static void DisableSourceRenderers(IReadOnlyList<MeshRenderer> renderers)
        {
            if (renderers == null)
                return;

            foreach (MeshRenderer renderer in renderers)
            {
                if (renderer != null)
                    renderer.enabled = false;
            }
        }

        static void CreateCombinedRenderer(
            Transform parent,
            Mesh mesh,
            Material material,
            ICollection<Mesh> ownedMeshes,
            string objectName = null)
        {
            if (mesh == null || material == null)
                return;

            ownedMeshes?.Add(mesh);
            var go = new GameObject(string.IsNullOrEmpty(objectName) ? BuildCombinedObjectName(material) : objectName);
            go.transform.SetParent(parent, false);
            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
        }

        static void CreateCombinedRenderer(
            Transform parent,
            Mesh mesh,
            List<Material> materials,
            ICollection<Mesh> ownedMeshes)
        {
            if (mesh == null || materials == null || materials.Count == 0)
                return;

            ownedMeshes?.Add(mesh);
            var go = new GameObject(mesh.name);
            go.transform.SetParent(parent, false);
            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = materials.ToArray();
        }

        static string BuildCombinedObjectName(Material material)
        {
            if (material == null)
                return SpatialMeshCombineUtility.CombinedRenderRootName;

            return $"Combined_{BuildingSurfaceUtility.NormalizeMaterialName(material.name)}";
        }

        static Mesh CloneMeshGeometry(Mesh source)
        {
            if (source == null)
                return null;

            if (source.isReadable)
            {
                var copy = Object.Instantiate(source);
                copy.name = source.name + "_Readable";
                return copy;
            }

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
    }
}
