using System;
using UnityEngine;

namespace ZGConnect.SpatialStreaming.Editor
{
    public enum SpatialBakeStage
    {
        Prepare = 0,
        Tiles = 1,
        Hlod = 2,
        AssetBundles = 3,
        Manifest = 4,
        CollectStaging = 5,
    }

    public struct SpatialBakeProgress
    {
        public SpatialBakeStage Stage;
        public float StageProgress;
        public int Current;
        public int Total;
        public string Detail;
        public float Overall;

        public static SpatialBakeProgress Create(
            SpatialBakeStage stage,
            float stageProgress,
            float overall,
            int current = 0,
            int total = 0,
            string detail = null)
        {
            return new SpatialBakeProgress
            {
                Stage = stage,
                StageProgress = Mathf.Clamp01(stageProgress),
                Overall = Mathf.Clamp01(overall),
                Current = current,
                Total = total,
                Detail = detail,
            };
        }
    }

    public sealed class SpatialBakeProgressTracker
    {
        public struct StageSlot
        {
            public string Label;
            public float Progress;
            public bool Enabled;
            public string Detail;
        }

        public float Master { get; private set; }
        public StageSlot[] Stages { get; private set; } = Array.Empty<StageSlot>();
        public string StatusDetail { get; private set; }
        public bool HasStarted { get; private set; }

        float[] _stageStart = new float[6];
        float[] _stageEnd = new float[6];

        public void ResetForFullBake(SpatialBakeProfile profile)
        {
            bool hlod = profile != null && profile.bakeHlodSupertiles;
            BuildStageRanges(hlod, stagingOnly: false);

            Stages = new[]
            {
                new StageSlot { Label = "Prepare", Enabled = true },
                new StageSlot { Label = "Tiles", Enabled = true },
                new StageSlot { Label = "HLOD supertiles", Enabled = hlod },
                new StageSlot { Label = "Asset bundles", Enabled = true },
                new StageSlot { Label = "Manifest", Enabled = true },
                new StageSlot { Label = "Collect staging", Enabled = false },
            };

            Master = 0f;
            StatusDetail = null;
            HasStarted = true;
            Apply(SpatialBakeProgress.Create(SpatialBakeStage.Prepare, 0f, 0f, detail: "Starting..."));
        }

        public void ResetForStagingBuild(bool rebuildManifest)
        {
            BuildStageRanges(hlod: false, stagingOnly: true);

            Stages = new[]
            {
                new StageSlot { Label = "Prepare", Enabled = false },
                new StageSlot { Label = "Tiles", Enabled = false },
                new StageSlot { Label = "HLOD supertiles", Enabled = false },
                new StageSlot { Label = "Asset bundles", Enabled = true },
                new StageSlot { Label = "Manifest", Enabled = rebuildManifest },
                new StageSlot { Label = "Collect staging", Enabled = true },
            };

            Master = 0f;
            StatusDetail = null;
            HasStarted = true;
            Apply(SpatialBakeProgress.Create(SpatialBakeStage.CollectStaging, 0f, 0f, detail: "Collecting..."));
        }

        public void Apply(SpatialBakeProgress progress)
        {
            Master = progress.Overall;
            StatusDetail = progress.Detail;
            int stageIndex = (int)progress.Stage;

            for (int i = 0; i < Stages.Length; i++)
            {
                StageSlot slot = Stages[i];
                if (!slot.Enabled)
                    continue;

                if (i < stageIndex)
                {
                    slot.Progress = 1f;
                    slot.Detail = null;
                }
                else if (i > stageIndex)
                {
                    slot.Progress = 0f;
                    slot.Detail = null;
                }
                else
                {
                    slot.Progress = progress.StageProgress;
                    slot.Detail = BuildDetail(progress);
                }

                Stages[i] = slot;
            }
        }

        public void Clear()
        {
            Stages = Array.Empty<StageSlot>();
            Master = 0f;
            StatusDetail = null;
            HasStarted = false;
        }

        public float StageToOverall(SpatialBakeStage stage, float stageProgress)
        {
            int index = (int)stage;
            float start = _stageStart[index];
            float end = _stageEnd[index];
            return Mathf.Lerp(start, end, Mathf.Clamp01(stageProgress));
        }

        void BuildStageRanges(bool hlod, bool stagingOnly)
        {
            if (stagingOnly)
            {
                _stageStart[(int)SpatialBakeStage.CollectStaging] = 0f;
                _stageEnd[(int)SpatialBakeStage.CollectStaging] = 0.08f;
                _stageStart[(int)SpatialBakeStage.AssetBundles] = 0.08f;
                _stageEnd[(int)SpatialBakeStage.AssetBundles] = 0.96f;
                _stageStart[(int)SpatialBakeStage.Manifest] = 0.96f;
                _stageEnd[(int)SpatialBakeStage.Manifest] = 1f;
                return;
            }

            _stageStart[(int)SpatialBakeStage.Prepare] = 0f;
            _stageEnd[(int)SpatialBakeStage.Prepare] = 0.02f;
            _stageStart[(int)SpatialBakeStage.Tiles] = 0.02f;
            _stageEnd[(int)SpatialBakeStage.Tiles] = hlod ? 0.72f : 0.76f;

            if (hlod)
            {
                _stageStart[(int)SpatialBakeStage.Hlod] = 0.72f;
                _stageEnd[(int)SpatialBakeStage.Hlod] = 0.78f;
                _stageStart[(int)SpatialBakeStage.AssetBundles] = 0.78f;
                _stageEnd[(int)SpatialBakeStage.AssetBundles] = 0.98f;
            }
            else
            {
                _stageStart[(int)SpatialBakeStage.Hlod] = 0.72f;
                _stageEnd[(int)SpatialBakeStage.Hlod] = 0.72f;
                _stageStart[(int)SpatialBakeStage.AssetBundles] = 0.76f;
                _stageEnd[(int)SpatialBakeStage.AssetBundles] = 0.98f;
            }

            _stageStart[(int)SpatialBakeStage.Manifest] = 0.98f;
            _stageEnd[(int)SpatialBakeStage.Manifest] = 1f;
        }

        static string BuildDetail(SpatialBakeProgress progress)
        {
            if (progress.Total > 0)
                return $"{progress.Current}/{progress.Total}";
            return progress.Detail;
        }
    }
}
