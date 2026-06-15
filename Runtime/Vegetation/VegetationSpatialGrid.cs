using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect
{
    /// <summary>
    /// Partitions batch instances into world XZ cells for coarse frustum / distance culling.
    /// Built once when instance matrices are generated.
    /// </summary>
    public sealed class VegetationSpatialGrid
    {
        public struct Cell
        {
            public Bounds  bounds;
            public int     startIndex;
            public int     count;
        }

        const float DefaultCellSizeMeters = 128f;
        const float BoundsPaddingMeters   = 4f;

        Cell[] _cells = System.Array.Empty<Cell>();
        int[]  _indices = System.Array.Empty<int>();

        public int CellCount => _cells.Length;
        public bool IsBuilt  => _cells.Length > 0;

        public Cell GetCell(int index) => _cells[index];

        public int GetInstanceIndex(int cellIndex, int localIndex) =>
            _indices[_cells[cellIndex].startIndex + localIndex];

        public void Build(IReadOnlyList<VegetationInstance> instances, float cellSizeMeters = DefaultCellSizeMeters)
        {
            if (instances == null || instances.Count == 0)
            {
                _cells = System.Array.Empty<Cell>();
                _indices = System.Array.Empty<int>();
                return;
            }

            float cellSize = Mathf.Max(16f, cellSizeMeters);
            float minX = float.MaxValue;
            float minZ = float.MaxValue;
            float maxX = float.MinValue;
            float maxZ = float.MinValue;

            for (int i = 0; i < instances.Count; i++)
            {
                Vector3 p = instances[i].position;
                minX = Mathf.Min(minX, p.x);
                minZ = Mathf.Min(minZ, p.z);
                maxX = Mathf.Max(maxX, p.x);
                maxZ = Mathf.Max(maxZ, p.z);
            }

            int gridW = Mathf.Max(1, Mathf.CeilToInt((maxX - minX) / cellSize) + 1);
            var buckets = new Dictionary<int, List<int>>();

            for (int i = 0; i < instances.Count; i++)
            {
                Vector3 p = instances[i].position;
                int gx = Mathf.FloorToInt((p.x - minX) / cellSize);
                int gz = Mathf.FloorToInt((p.z - minZ) / cellSize);
                int key = gx + gz * gridW;

                if (!buckets.TryGetValue(key, out List<int> bucket))
                {
                    bucket = new List<int>();
                    buckets[key] = bucket;
                }

                bucket.Add(i);
            }

            var cells = new List<Cell>(buckets.Count);
            var indices = new List<int>(instances.Count);

            foreach (List<int> bucket in buckets.Values)
            {
                int start = indices.Count;
                indices.AddRange(bucket);

                Vector3 bMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                Vector3 bMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                for (int b = 0; b < bucket.Count; b++)
                {
                    Vector3 p = instances[bucket[b]].position;
                    Vector3 s = instances[bucket[b]].scale;
                    bMin = Vector3.Min(bMin, p);
                    bMax = Vector3.Max(bMax, p + Vector3.up * s.y);
                }

                float pad = BoundsPaddingMeters;
                var bounds = new Bounds(
                    (bMin + bMax) * 0.5f,
                    (bMax - bMin) + new Vector3(pad * 2f, pad * 2f, pad * 2f));

                cells.Add(new Cell
                {
                    bounds     = bounds,
                    startIndex = start,
                    count      = bucket.Count
                });
            }

            _cells = cells.ToArray();
            _indices = indices.ToArray();
        }
    }
}

