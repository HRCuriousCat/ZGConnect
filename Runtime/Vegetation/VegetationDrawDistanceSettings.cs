using System;
using UnityEngine;

namespace ZGConnect
{
    [Serializable]
    public struct VegetationDrawDistanceSettings
    {
        [Tooltip("Full instance density up to this horizontal distance (m). " +
                 "Beyond it, density linearly decreases until Max Draw Distance.")]
        public float densityFalloffStart;

        [Tooltip("Vegetation is not drawn beyond this distance (density = 0). " +
                 "Set to 0 to disable distance culling.")]
        public float maxDrawDistance;

        public bool IsEnabled => maxDrawDistance > 0f;

        /// <summary>
        /// Returns 1 below falloff start, 0 at or beyond max distance, linear in between.
        /// </summary>
        public float EvaluateDensity(float horizontalDistanceMeters)
        {
            if (!IsEnabled)
                return 1f;

            if (horizontalDistanceMeters >= maxDrawDistance)
                return 0f;

            float start = Mathf.Min(densityFalloffStart, maxDrawDistance);
            if (horizontalDistanceMeters <= start)
                return 1f;

            float range = maxDrawDistance - start;
            if (range <= 0f)
                return 0f;

            return 1f - (horizontalDistanceMeters - start) / range;
        }

        public static VegetationDrawDistanceSettings Disabled => new VegetationDrawDistanceSettings
        {
            densityFalloffStart = 0f,
            maxDrawDistance     = 0f
        };
    }
}

