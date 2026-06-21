using System.Collections;
using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>Spreads heavy spawn finalize work across frames when multiple loads complete together.</summary>
    public static class SpatialSpawnFrameBudget
    {
        static int _frame = -1;
        static float _frameStart;
        static int _finalizeOps;
        static int _spawnSteps;

        static void ResetFrameIfNeeded()
        {
            if (Time.frameCount == _frame)
                return;

            _frame = Time.frameCount;
            _frameStart = Time.realtimeSinceStartup;
            _finalizeOps = 0;
            _spawnSteps = 0;
        }

        public static IEnumerator WaitForFinalizeSlot(SpatialStreamingLoadBudget budget)
        {
            int maxFinalize = Mathf.Max(1, budget.maxSpawnFinalizePerFrame);
            while (true)
            {
                ResetFrameIfNeeded();
                if (_finalizeOps < maxFinalize && !ShouldYieldStep(budget))
                {
                    _finalizeOps++;
                    yield break;
                }

                yield return null;
            }
        }

        public static void ReleaseFinalizeSlot()
        {
            if (_finalizeOps > 0)
                _finalizeOps--;
        }

        public static IEnumerator WaitForSpawnStep(SpatialStreamingLoadBudget budget)
        {
            while (ShouldYieldStep(budget))
                yield return null;

            _spawnSteps++;
        }

        static bool ShouldYieldStep(SpatialStreamingLoadBudget budget)
        {
            ResetFrameIfNeeded();
            return SpatialFrameBudget.ShouldYield(
                _spawnSteps,
                budget.maxSpawnStepsPerFrame,
                _frameStart,
                budget.spawnMainThreadMsBudget);
        }
    }
}
