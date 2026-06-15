using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect
{
    /// <summary>
    /// Adjusts UV0 on roof triangles only (flat + sloped submeshes from building surface processing).
    /// Attach to a building root (same object as <see cref="BuildingData"/> or its mesh child).
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class BuildingRoofUvRotation : MonoBehaviour
    {
        [Header("Roof UV transform")]
        [Tooltip("Clockwise rotation in degrees (0–360). Facade UVs are unchanged.")]
        [Range(0f, 360f)]
        public float roofUvRotationDegrees;

        [Tooltip("Slides roof UVs along U (horizontal texture axis).")]
        [Range(-5f, 5f)]
        public float roofUvOffsetX;

        [Tooltip("Slides roof UVs along V (vertical texture axis).")]
        [Range(-5f, 5f)]
        public float roofUvOffsetY;

        [Tooltip("Roof texture repeat scale around the roof UV center. 1 = unchanged, 2 = twice as many tiles.")]
        [Range(0.1f, 10f)]
        public float roofUvTiling = 1f;

        [Header("Fine tuning (added to values above)")]
        [Range(-10f, 10f)]
        public float roofUvRotationFine;

        [Range(-0.25f, 0.25f)]
        public float roofUvOffsetXFine;

        [Range(-0.25f, 0.25f)]
        public float roofUvOffsetYFine;

        [Range(-0.5f, 0.5f)]
        public float roofUvTilingFine;

        [Tooltip("When no roof submeshes exist, classify roof triangles by world normal |Y|.")]
        [SerializeField] private float _flatRoofMinNormalY = 0.92f;

        MeshFilter _meshFilter;
        Mesh _workingMesh;
        Vector2[] _baseUvs;
        HashSet<int> _roofVertexIndices;
        Vector2 _uvPivot;
        float _lastRotation;
        float _lastOffsetX;
        float _lastOffsetY;
        float _lastTiling;
        float _lastRotationFine;
        float _lastOffsetXFine;
        float _lastOffsetYFine;
        float _lastTilingFine;
        static bool s_anyLastApplied = false;

        void OnEnable() => RebuildMeshCache();

        void OnValidate()
        {
            if (!isActiveAndEnabled) return;
            ApplyRoofUvs();
        }

        void Update()
        {
            if (Application.isPlaying && HasRoofUvChanged())
                ApplyRoofUvs();
        }

        /// <summary>Re-reads mesh topology and baseline UVs (call after mesh swap).</summary>
        public void RebuildMeshCache()
        {
            _meshFilter = FindTargetMeshFilter();
            if (_meshFilter == null || _meshFilter.sharedMesh == null)
            {
                ClearCache();
                return;
            }

            Mesh source = _meshFilter.sharedMesh;
            if (_workingMesh == null || _meshFilter.sharedMesh != _workingMesh)
            {
                if (_workingMesh != null)
                    DestroyMeshSafe(_workingMesh);

                _workingMesh = Instantiate(source);
                _workingMesh.name = source.name + "_RoofUv";
                _meshFilter.sharedMesh = _workingMesh;
            }

            _baseUvs = _workingMesh.uv;
            if (_baseUvs == null || _baseUvs.Length != _workingMesh.vertexCount)
                _baseUvs = new Vector2[_workingMesh.vertexCount];

            _baseUvs = (Vector2[])_baseUvs.Clone();
            _roofVertexIndices = CollectRoofVertexIndices(
                _workingMesh, _meshFilter.transform, _flatRoofMinNormalY);
            _uvPivot = ComputeUvPivot(_baseUvs, _roofVertexIndices);
            ResetLastApplied();
            ApplyRoofUvs();
        }

        /// <summary>Applies current slider values to the working mesh UVs.</summary>
        public void ApplyRoofUvs()
        {
            if (_workingMesh == null || _baseUvs == null || _roofVertexIndices == null || _roofVertexIndices.Count == 0)
                return;

            float tiling = Mathf.Clamp(roofUvTiling + roofUvTilingFine, 0.1f, 10f);
            float rotation = Mathf.Repeat(roofUvRotationDegrees + roofUvRotationFine, 360f);
            float offsetX = Mathf.Clamp(roofUvOffsetX + roofUvOffsetXFine, -5f, 5f);
            float offsetY = Mathf.Clamp(roofUvOffsetY + roofUvOffsetYFine, -5f, 5f);

            float rad = rotation * Mathf.Deg2Rad;
            float cos = Mathf.Cos(rad);
            float sin = Mathf.Sin(rad);
            var uvs = (Vector2[])_baseUvs.Clone();

            foreach (int vi in _roofVertexIndices)
            {
                Vector2 d = uvs[vi] - _uvPivot;
                d.x *= tiling;
                d.y *= tiling;

                Vector2 rotated = new Vector2(
                    d.x * cos - d.y * sin,
                    d.x * sin + d.y * cos);

                uvs[vi] = rotated + _uvPivot + new Vector2(offsetX, offsetY);
            }

            _workingMesh.uv = uvs;
            RememberLastApplied();
        }

        bool HasRoofUvChanged()
        {
            if (!s_anyLastApplied) return true;
            return !Mathf.Approximately(roofUvRotationDegrees, _lastRotation)
                   || !Mathf.Approximately(roofUvOffsetX, _lastOffsetX)
                   || !Mathf.Approximately(roofUvOffsetY, _lastOffsetY)
                   || !Mathf.Approximately(roofUvTiling, _lastTiling)
                   || !Mathf.Approximately(roofUvRotationFine, _lastRotationFine)
                   || !Mathf.Approximately(roofUvOffsetXFine, _lastOffsetXFine)
                   || !Mathf.Approximately(roofUvOffsetYFine, _lastOffsetYFine)
                   || !Mathf.Approximately(roofUvTilingFine, _lastTilingFine);
        }

        void RememberLastApplied()
        {
            s_anyLastApplied = true;
            _lastRotation     = roofUvRotationDegrees;
            _lastOffsetX      = roofUvOffsetX;
            _lastOffsetY      = roofUvOffsetY;
            _lastTiling       = roofUvTiling;
            _lastRotationFine = roofUvRotationFine;
            _lastOffsetXFine  = roofUvOffsetXFine;
            _lastOffsetYFine  = roofUvOffsetYFine;
            _lastTilingFine   = roofUvTilingFine;
        }

        void ResetLastApplied() => s_anyLastApplied = false;

        static MeshFilter FindTargetMeshFilter(GameObject root)
        {
            MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
            MeshFilter best = null;
            int bestSubMeshes = 0;

            foreach (MeshFilter mf in filters)
            {
                if (mf.sharedMesh == null) continue;
                int subCount = mf.sharedMesh.subMeshCount;
                if (subCount > bestSubMeshes)
                {
                    bestSubMeshes = subCount;
                    best = mf;
                }
            }

            return best;
        }

        MeshFilter FindTargetMeshFilter() => FindTargetMeshFilter(gameObject);

        static HashSet<int> CollectRoofVertexIndices(
            Mesh mesh, Transform meshTransform, float flatRoofMinNormalY)
        {
            var indices = new HashSet<int>();
            int subCount = mesh.subMeshCount;

            if (subCount > 1)
            {
                for (int s = 1; s < subCount; s++)
                {
                    int[] tris = mesh.GetTriangles(s);
                    for (int i = 0; i < tris.Length; i++)
                        indices.Add(tris[i]);
                }

                return indices;
            }

            CollectRoofVerticesByNormal(mesh, meshTransform, flatRoofMinNormalY, indices);
            return indices;
        }

        static void CollectRoofVerticesByNormal(
            Mesh mesh, Transform meshTransform, float flatRoofMinNormalY, HashSet<int> indices)
        {
            Vector3[] norms = mesh.normals;
            if (norms == null || norms.Length != mesh.vertexCount)
                return;

            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                int[] tris = mesh.GetTriangles(s);
                for (int i = 0; i < tris.Length; i += 3)
                {
                    int i0 = tris[i];
                    int i1 = tris[i + 1];
                    int i2 = tris[i + 2];

                    Vector3 n = (
                        meshTransform.TransformDirection(norms[i0]) +
                        meshTransform.TransformDirection(norms[i1]) +
                        meshTransform.TransformDirection(norms[i2])).normalized;

                    if (Mathf.Abs(n.y) >= flatRoofMinNormalY)
                    {
                        indices.Add(i0);
                        indices.Add(i1);
                        indices.Add(i2);
                    }
                }
            }
        }

        static Vector2 ComputeUvPivot(Vector2[] uvs, HashSet<int> roofVertices)
        {
            if (roofVertices == null || roofVertices.Count == 0)
                return new Vector2(0.5f, 0.5f);

            Vector2 sum = Vector2.zero;
            int count = 0;
            foreach (int vi in roofVertices)
            {
                sum += uvs[vi];
                count++;
            }

            return sum / Mathf.Max(1, count);
        }

        void ClearCache()
        {
            _meshFilter = null;
            _baseUvs = null;
            _roofVertexIndices = null;
            ResetLastApplied();
        }

        void OnDestroy()
        {
            if (_workingMesh != null)
                DestroyMeshSafe(_workingMesh);
        }

        static void DestroyMeshSafe(Mesh mesh)
        {
            if (mesh == null) return;
            if (Application.isPlaying)
                Destroy(mesh);
            else
                DestroyImmediate(mesh);
        }
    }
}
