using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace ZGConnect.Editor
{
    /// <summary>
    /// gltfast imports discard CPU mesh data by default; surface/roof passes need readable geometry.
    /// </summary>
    public static class BuildingMeshProcessingUtility
    {
        public static void MakeInstanceMeshesReadable(GameObject root)
        {
            if (root == null)
                return;

            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh src = mf.sharedMesh;
                if (src == null || src.isReadable)
                    continue;

                Mesh readable = CloneMeshGeometry(src);
                if (readable == null)
                {
                    Debug.LogWarning(
                        $"[ZGConnect] Could not clone mesh '{src.name}' on '{mf.name}' for processing. " +
                        "Re-import the GLB or enable GLTFAST_KEEP_MESH_DATA.");
                    continue;
                }

                readable.name = src.name + "_Readable";
                mf.sharedMesh = readable;
            }
        }

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

            var dst = new Mesh { name = source.name + "_Proc" };

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

        private static Mesh CopyReadableMesh(Mesh source)
        {
            var dst = new Mesh { name = source.name + "_Proc" };
            dst.vertices = source.vertices;

            if (source.normals != null && source.normals.Length == source.vertexCount)
                dst.normals = source.normals;
            else
                dst.RecalculateNormals();

            if (source.uv != null && source.uv.Length == source.vertexCount)
                dst.uv = source.uv;
            else
                dst.uv = new Vector2[source.vertexCount];

            dst.subMeshCount = source.subMeshCount;
            for (int s = 0; s < source.subMeshCount; s++)
                dst.SetTriangles(source.GetTriangles(s), s);

            dst.RecalculateBounds();
            return dst;
        }
    }
}
