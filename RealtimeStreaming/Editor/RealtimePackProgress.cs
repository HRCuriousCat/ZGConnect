using System;

namespace ZGConnect.RealtimeStreaming.Editor
{
    public enum RealtimePackStage
    {
        Tiles = 0,
        Hlod = 1,
        TerrainBundle = 2,
        BuildingBake = 3,
        VegetationBake = 4,
        Manifest = 5,
    }

    public struct RealtimePackProgress
    {
        public float Overall;
        public RealtimePackStage Stage;
        public float StageProgress;
        public int Current;
        public int Total;
        public string Detail;

        public static RealtimePackProgress Create(
            RealtimePackStage stage,
            float overall,
            float stageProgress,
            int current = 0,
            int total = 0,
            string detail = null)
        {
            return new RealtimePackProgress
            {
                Stage = stage,
                Overall = UnityEngine.Mathf.Clamp01(overall),
                StageProgress = UnityEngine.Mathf.Clamp01(stageProgress),
                Current = current,
                Total = total,
                Detail = detail,
            };
        }
    }

    public class RealtimePackProgressTracker
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
        public string ResultMessage { get; private set; }
        public bool HasStarted { get; private set; }
        public bool IsComplete { get; private set; }
        public bool IsFailed { get; private set; }

        public void Reset(RealtimePackOptions options)
        {
            bool hlod = options.PackTerrain || options.PackOrtho;
            bool terrainBundles = options.PackTerrain && options.PackTerrainBundles;
            bool buildingBundles = options.PackBuildingBundles &&
                                   (options.PackFacadeBuildings || options.PackOrthoRoofBuildings);
            bool vegetationBundles = options.BakeVegetationBundles && options.PackVegetation;

            Stages = new[]
            {
                new StageSlot { Label = "Tiles", Enabled = true, Progress = 0f },
                new StageSlot { Label = "HLOD merge", Enabled = hlod, Progress = 0f },
                new StageSlot { Label = "Terrain bundles", Enabled = terrainBundles, Progress = 0f },
                new StageSlot { Label = "Building bake", Enabled = buildingBundles, Progress = 0f },
                new StageSlot { Label = "Vegetation bake", Enabled = vegetationBundles, Progress = 0f },
                new StageSlot { Label = "Manifest", Enabled = true, Progress = 0f },
            };

            Master = 0f;
            ResultMessage = null;
            HasStarted = true;
            IsComplete = false;
            IsFailed = false;
        }

        public void Apply(RealtimePackProgress progress)
        {
            Master = progress.Overall;
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

        public void MarkComplete(string message)
        {
            Master = 1f;
            ResultMessage = message;
            IsComplete = true;
            IsFailed = false;

            for (int i = 0; i < Stages.Length; i++)
            {
                StageSlot slot = Stages[i];
                if (!slot.Enabled)
                    continue;

                slot.Progress = 1f;
                slot.Detail = null;
                Stages[i] = slot;
            }
        }

        public void MarkFailed(string message, float overall)
        {
            Master = overall;
            ResultMessage = message;
            IsComplete = true;
            IsFailed = true;
        }

        public void Clear()
        {
            Stages = Array.Empty<StageSlot>();
            Master = 0f;
            ResultMessage = null;
            HasStarted = false;
            IsComplete = false;
            IsFailed = false;
        }

        static string BuildDetail(RealtimePackProgress progress)
        {
            if (progress.Total > 0)
                return $"{progress.Current} / {progress.Total}";
            if (!string.IsNullOrEmpty(progress.Detail))
                return progress.Detail;
            return null;
        }
    }
}
