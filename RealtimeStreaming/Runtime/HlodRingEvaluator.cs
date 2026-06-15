using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ZGConnect.RealtimeStreaming
{
    public enum HlodRing
    {
        None = 0,
        Near1x1 = 1,
        Mid2x2 = 2,
        Far4x4 = 4,
    }

    public readonly struct HlodSelection
    {
        public readonly HlodRing Ring;
        public readonly int Factor;

        public HlodSelection(HlodRing ring, int factor)
        {
            Ring = ring;
            Factor = factor;
        }

        public static HlodSelection None => new(HlodRing.None, 0);
    }

    public enum HlodDistanceMetric
    {
        /// <summary>Euclidean distance to tile AABB — circular rings in XZ.</summary>
        Circular = 0,
        /// <summary>Chebyshev (L∞) distance to tile AABB — square rings aligned with the tile grid.</summary>
        Rectangular = 1,
    }

    public static class HlodRingEvaluator
    {
        public static float TileBoundaryDistance(Vector3 camPos, Vector3 tileOrigin, int tileSizeMeters) =>
            TileBoundaryDistance(camPos, tileOrigin, tileSizeMeters, HlodDistanceMetric.Circular);

        public static float TileBoundaryDistance(
            Vector3 camPos,
            Vector3 tileOrigin,
            int tileSizeMeters,
            HlodDistanceMetric metric)
        {
            float dx = Mathf.Max(0f, tileOrigin.x - camPos.x, camPos.x - (tileOrigin.x + tileSizeMeters));
            float dz = Mathf.Max(0f, tileOrigin.z - camPos.z, camPos.z - (tileOrigin.z + tileSizeMeters));
            return metric == HlodDistanceMetric.Rectangular
                ? Mathf.Max(dx, dz)
                : Mathf.Sqrt(dx * dx + dz * dz);
        }

        public static HlodSelection EvaluateRing(
            float distance,
            IReadOnlyList<StreamingHlodLevel> levels,
            HlodRing currentlyLoaded)
        {
            if (levels == null || levels.Count == 0)
                return distance <= 1500f ? new HlodSelection(HlodRing.Near1x1, 1) : HlodSelection.None;

            StreamingHlodLevel near = null;
            StreamingHlodLevel mid = null;
            StreamingHlodLevel far = null;

            foreach (StreamingHlodLevel level in levels)
            {
                switch (level.Factor)
                {
                    case 1: near = level; break;
                    case 2: mid = level; break;
                    case 4: far = level; break;
                }
            }

            if (currentlyLoaded == HlodRing.Near1x1 && near != null)
            {
                if (distance > near.UnloadDistance)
                    return PickLoadRing(distance, near, mid, far);
                return new HlodSelection(HlodRing.Near1x1, 1);
            }

            if (currentlyLoaded == HlodRing.Mid2x2 && mid != null)
            {
                if (distance <= mid.LoadDistance)
                    return new HlodSelection(HlodRing.Near1x1, 1);
                if (distance > mid.UnloadDistance)
                    return PickLoadRing(distance, near, mid, far);
                return new HlodSelection(HlodRing.Mid2x2, 2);
            }

            if (currentlyLoaded == HlodRing.Far4x4 && far != null)
            {
                if (distance <= far.LoadDistance)
                {
                    if (mid != null && distance <= mid.LoadDistance)
                        return new HlodSelection(HlodRing.Near1x1, 1);
                    return new HlodSelection(HlodRing.Mid2x2, 2);
                }
                return new HlodSelection(HlodRing.Far4x4, 4);
            }

            return PickLoadRing(distance, near, mid, far);
        }

        static HlodSelection PickLoadRing(
            float distance,
            StreamingHlodLevel near,
            StreamingHlodLevel mid,
            StreamingHlodLevel far)
        {
            if (near != null && distance <= near.LoadDistance)
                return new HlodSelection(HlodRing.Near1x1, 1);
            if (mid != null && distance <= mid.LoadDistance)
                return new HlodSelection(HlodRing.Mid2x2, 2);
            if (far != null && distance <= far.LoadDistance)
                return new HlodSelection(HlodRing.Far4x4, 4);
            return HlodSelection.None;
        }

        public static int GetIdealFactor(float distance, IReadOnlyList<StreamingHlodLevel> levels)
        {
            HlodSelection pick = PickLoadRing(distance,
                FindLevel(levels, 1), FindLevel(levels, 2), FindLevel(levels, 4));
            return pick.Factor;
        }

        /// <summary>
        /// Ideal HLOD factor with fallback when coarser supertiles are not packed (e.g. only 2x2, no 4x4).
        /// </summary>
        public static int ResolveEffectiveFactor(
            float distance,
            IReadOnlyList<StreamingHlodLevel> levels,
            IReadOnlyCollection<int> availableFactors)
        {
            int ideal = GetIdealFactor(distance, levels);
            if (availableFactors == null || availableFactors.Count == 0)
                return ideal > 0 ? ideal : 1;

            if (availableFactors.Contains(ideal))
                return ideal;

            if (ideal >= 4 && availableFactors.Contains(2))
                return 2;

            return 1;
        }

        static StreamingHlodLevel FindLevel(IReadOnlyList<StreamingHlodLevel> levels, int factor)
        {
            if (levels == null)
                return null;
            foreach (StreamingHlodLevel level in levels)
            {
                if (level.Factor == factor)
                    return level;
            }
            return null;
        }

        /// <summary>
        /// Higher factor = coarser tile. Supertile may be coarser than the band (4×4 when 2×2 is ideal).
        /// 2×2 is also accepted in the far (4×4) band as gap-fill when 4×4 does not cover those cells.
        /// </summary>
        public static bool SupertileSatisfiesFactor(int supertileFactor, int effectiveFactor)
        {
            if (effectiveFactor <= 1)
                return false;
            if (supertileFactor >= effectiveFactor)
                return true;
            return effectiveFactor == 4 && supertileFactor == 2;
        }

        public static string SupertileId(int left, int bottom, int factor) =>
            $"st_{left}_{bottom}_{factor}x{factor}";
    }
}
