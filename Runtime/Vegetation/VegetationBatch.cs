using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect
{
    public class VegetationBatch
    {
        public int prototypeIndex;
        public VegetationPrototype prototype;
        public readonly List<VegetationInstance> instances = new List<VegetationInstance>();
        public Matrix4x4[] matrices;
        public Matrix4x4[] drawMatrices;
        public int         drawCount;
        public Bounds bounds;

        readonly VegetationSpatialGrid _spatialGrid = new VegetationSpatialGrid();

        public void BuildMatrices() =>
            BuildMatricesWithGrowth(1f, VegetationSpawnAnimationMode.None, 0f, 0f, 0f);

        public void BuildDrawMatrices(
            VegetationCullContext cull,
            float chunkGrowth,
            VegetationSpawnAnimationMode mode,
            float elapsed,
            float perTreeDuration,
            float perTreeMaxStagger)
        {
            if (instances.Count == 0)
            {
                drawCount = 0;
                return;
            }

            bool applyGrowth = mode != VegetationSpawnAnimationMode.None;
            bool filter      = cull.RequiresInstanceLoop;

            if (!filter && !applyGrowth)
            {
                if (matrices == null || matrices.Length != instances.Count)
                    BuildMatrices();
                drawMatrices = matrices;
                drawCount    = matrices.Length;
                return;
            }

            if (drawMatrices == null || drawMatrices.Length < instances.Count)
                drawMatrices = new Matrix4x4[instances.Count];

            if (!applyGrowth)
            {
                if (matrices == null || matrices.Length != instances.Count)
                    BuildMatrices();
            }

            if (filter && _spatialGrid.IsBuilt)
            {
                BuildDrawMatricesFromSpatialGrid(
                    cull, chunkGrowth, mode, elapsed, perTreeDuration, perTreeMaxStagger, applyGrowth);
                return;
            }

            BuildDrawMatricesPerInstance(
                cull, chunkGrowth, mode, elapsed, perTreeDuration, perTreeMaxStagger, applyGrowth);
        }

        void BuildDrawMatricesFromSpatialGrid(
            VegetationCullContext cull,
            float chunkGrowth,
            VegetationSpawnAnimationMode mode,
            float elapsed,
            float perTreeDuration,
            float perTreeMaxStagger,
            bool applyGrowth)
        {
            int frustumSkipped  = 0;
            int distanceSkipped = 0;
            int n = 0;
            float falloffStart = cull.UsesPerInstanceDistance
                ? Mathf.Min(cull.drawDistance.densityFalloffStart, cull.drawDistance.maxDrawDistance)
                : float.MaxValue;

            for (int c = 0; c < _spatialGrid.CellCount; c++)
            {
                VegetationSpatialGrid.Cell cell = _spatialGrid.GetCell(c);

                float cellClosest = 0f;
                if (cull.UsesPerInstanceDistance)
                {
                    cellClosest = VegetationChunkRenderer.HorizontalDistanceToBoundsXZ(
                        cull.cameraPosition, cell.bounds);
                    if (cellClosest >= cull.drawDistance.maxDrawDistance)
                    {
                        distanceSkipped += cell.count;
                        continue;
                    }
                }

                bool cellFullyDense = !cull.UsesPerInstanceDistance || cellClosest <= falloffStart;

                if (cull.UsesFrustum &&
                    !VegetationFrustumUtility.TestBounds(cull.frustumPlanes, cell.bounds))
                {
                    frustumSkipped += cell.count;
                    continue;
                }

                for (int i = 0; i < cell.count; i++)
                {
                    int instIndex = _spatialGrid.GetInstanceIndex(c, i);
                    VegetationInstance inst = instances[instIndex];

                    if (cull.UsesPerInstanceDistance && !cellFullyDense)
                    {
                        float horizDist = VegetationDistanceUtility.HorizontalDistanceXZ(
                            cull.cameraPosition, inst.position);
                        float density = cull.drawDistance.EvaluateDensity(horizDist);
                        if (density <= 0f)
                        {
                            distanceSkipped++;
                            continue;
                        }

                        if (density < 1f && inst.random > density)
                        continue;
                    }

                    float growth = applyGrowth
                        ? ResolveGrowth(chunkGrowth, mode, elapsed, perTreeDuration, perTreeMaxStagger, inst.random)
                        : 1f;

                    drawMatrices[n++] = !applyGrowth
                        ? matrices[instIndex]
                        : Matrix4x4.TRS(inst.position, inst.rotation, inst.scale * growth);
                }
            }

            drawCount = n;
            if (frustumSkipped > 0)
                VegetationRenderStats.RecordInstanceFrustumCull(frustumSkipped);
            if (distanceSkipped > 0)
                VegetationRenderStats.RecordInstanceDistanceCull(distanceSkipped);
        }

        void BuildDrawMatricesPerInstance(
            VegetationCullContext cull,
            float chunkGrowth,
            VegetationSpawnAnimationMode mode,
            float elapsed,
            float perTreeDuration,
            float perTreeMaxStagger,
            bool applyGrowth)
        {
            int frustumSkipped  = 0;
            int distanceSkipped = 0;
            int n = 0;

            for (int i = 0; i < instances.Count; i++)
            {
                VegetationInstance inst = instances[i];

                if (cull.UsesPerInstanceDistance)
                {
                    float horizDist = VegetationDistanceUtility.HorizontalDistanceXZ(
                        cull.cameraPosition, inst.position);
                    float density = cull.drawDistance.EvaluateDensity(horizDist);
                    if (density <= 0f)
                    {
                        distanceSkipped++;
                        continue;
                    }

                    if (density < 1f && inst.random > density)
                        continue;
                }

                if (cull.UsesFrustum &&
                    !VegetationFrustumUtility.TestInstance(inst.position, inst.scale, cull.frustumPlanes))
                {
                    frustumSkipped++;
                    continue;
                }

                float growth = applyGrowth
                    ? ResolveGrowth(chunkGrowth, mode, elapsed, perTreeDuration, perTreeMaxStagger, inst.random)
                    : 1f;

                drawMatrices[n++] = !applyGrowth
                    ? matrices[i]
                    : Matrix4x4.TRS(inst.position, inst.rotation, inst.scale * growth);
            }

            drawCount = n;
            if (frustumSkipped > 0)
                VegetationRenderStats.RecordInstanceFrustumCull(frustumSkipped);
            if (distanceSkipped > 0)
                VegetationRenderStats.RecordInstanceDistanceCull(distanceSkipped);
        }

        public void BuildMatricesWithGrowth(
            float chunkGrowth,
            VegetationSpawnAnimationMode mode,
            float elapsed,
            float perTreeDuration,
            float perTreeMaxStagger)
        {
            if (instances.Count == 0)
            {
                matrices = new Matrix4x4[0];
                bounds = new Bounds();
                _spatialGrid.Build(instances);
                return;
            }

            if (matrices == null || matrices.Length != instances.Count)
                matrices = new Matrix4x4[instances.Count];

            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for (int i = 0; i < instances.Count; i++)
            {
                VegetationInstance inst = instances[i];
                float growth = ResolveGrowth(
                    chunkGrowth, mode, elapsed, perTreeDuration, perTreeMaxStagger, inst.random);

                Vector3 scale = inst.scale * growth;
                matrices[i] = Matrix4x4.TRS(inst.position, inst.rotation, scale);
                min = Vector3.Min(min, inst.position);
                max = Vector3.Max(max, inst.position);
            }

            bounds = new Bounds((min + max) * 0.5f, max - min + Vector3.one * 10f);
            _spatialGrid.Build(instances);
        }

        private static float ResolveGrowth(
            float chunkGrowth,
            VegetationSpawnAnimationMode mode,
            float elapsed,
            float perTreeDuration,
            float perTreeMaxStagger,
            float random01)
        {
            switch (mode)
            {
                case VegetationSpawnAnimationMode.ChunkPopIn:
                    return chunkGrowth;
                case VegetationSpawnAnimationMode.PerTree:
                    return VegetationGrowthAnimation.PerTreeGrowth(
                        elapsed, perTreeDuration, perTreeMaxStagger, random01);
                default:
                    return 1f;
            }
        }
    }
}
