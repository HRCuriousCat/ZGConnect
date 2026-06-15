using UnityEngine;

namespace ZGConnect.RealtimeStreaming
{
    /// <summary>Helpers for spreading main-thread work across frames.</summary>
    public static class RuntimeFrameBudget
    {
        public static bool ShouldYield(int itemsThisFrame, int maxItemsPerFrame, float frameStartSeconds, float msBudget)
        {
            if (maxItemsPerFrame > 0 && itemsThisFrame >= maxItemsPerFrame)
                return true;

            if (msBudget > 0f && (Time.realtimeSinceStartup - frameStartSeconds) * 1000f >= msBudget)
                return true;

            return false;
        }
    }
}

