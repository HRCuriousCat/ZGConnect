using System;
using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    public sealed class SpatialLoadedSubcellRecord
    {
        public string Key;
        public string TileId;
        public string SubcellId;
        public string BundleFullPath;
        public GameObject Root;
        public AssetBundle Bundle;
        public float LoadedAt;
        public SpatialStreamingLodLevel LodLevel;
        public int HlodFactor;
        public int BlockLeft;
        public int BlockBottom;
        public int MeshRendererCount;
    }

    [Serializable]
    public struct SpatialStreamingLoadBudget
    {
        [Tooltip("Maximum bundle load coroutines running at once.")]
        public int maxConcurrentLoads;
        [Tooltip("Maximum new load coroutines started per Unity frame (not per HLOD check interval).")]
        public int maxInstantiatesPerFrame;
        [Tooltip("Optional wall-time cap (ms) for starting new loads within a single frame.")]
        public float instantiateMsBudget;
        [Tooltip("Max subcells that may run spawn finalize (materials/GPU attach) in the same frame.")]
        public int maxSpawnFinalizePerFrame;
        [Tooltip("Main-thread ms budget per frame for chunked spawn work (material remap, mesh-detail build).")]
        public float spawnMainThreadMsBudget;
        [Tooltip("Max chunked spawn steps per frame (each step processes renderersPerSpawnStep renderers).")]
        public int maxSpawnStepsPerFrame;
        [Tooltip("Mesh renderers processed per spawn step before yielding.")]
        public int renderersPerSpawnStep;

        public static SpatialStreamingLoadBudget Default => new()
        {
            maxConcurrentLoads = 3,
            maxInstantiatesPerFrame = 2,
            instantiateMsBudget = 6f,
            maxSpawnFinalizePerFrame = 1,
            spawnMainThreadMsBudget = 5f,
            maxSpawnStepsPerFrame = 2,
            renderersPerSpawnStep = 12,
        };
    }
}
