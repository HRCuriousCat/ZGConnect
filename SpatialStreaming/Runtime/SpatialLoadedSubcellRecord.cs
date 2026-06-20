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
    }

    [Serializable]
    public struct SpatialStreamingLoadBudget
    {
        public int maxConcurrentLoads;
        public int maxInstantiatesPerFrame;
        public float instantiateMsBudget;

        public static SpatialStreamingLoadBudget Default => new()
        {
            maxConcurrentLoads = 3,
            maxInstantiatesPerFrame = 2,
            instantiateMsBudget = 6f,
        };
    }
}
