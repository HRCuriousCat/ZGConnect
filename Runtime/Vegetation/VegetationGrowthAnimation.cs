using UnityEngine;

namespace ZGConnect
{
    public static class VegetationGrowthAnimation
    {
        public static float EaseOutCubic(float t) =>
            1f - Mathf.Pow(1f - Mathf.Clamp01(t), 3f);

        public static float ChunkGrowth(float elapsed, float duration) =>
            EaseOutCubic(elapsed / Mathf.Max(0.01f, duration));

        public static float PerTreeGrowth(float elapsed, float duration, float maxStagger, float random01) =>
            EaseOutCubic((elapsed - random01 * maxStagger) / Mathf.Max(0.01f, duration));

        public static bool IsChunkComplete(float elapsed, VegetationSpawnAnimationSettings settings) =>
            elapsed >= settings.CompletionTime;

        public static bool IsPerTreeComplete(float elapsed, VegetationSpawnAnimationSettings settings) =>
            elapsed >= settings.CompletionTime;
    }
}

