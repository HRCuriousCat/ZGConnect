using System;
using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    [Serializable]
    public struct SpatialStreamingHlodDistances
    {
        public float hlod4LoadMeters;
        public float hlod4UnloadMeters;
        public float hlod2LoadMeters;
        public float hlod2UnloadMeters;
        public float proxyLoadMeters;
        public float proxyUnloadMeters;
        public float detailLoadMeters;
        public float detailUnloadMeters;

        public static SpatialStreamingHlodDistances Default => new()
        {
            hlod4LoadMeters = 15000f,
            hlod4UnloadMeters = 17000f,
            hlod2LoadMeters = 5000f,
            hlod2UnloadMeters = 6000f,
            proxyLoadMeters = 2800f,
            proxyUnloadMeters = 3200f,
            detailLoadMeters = 1200f,
            detailUnloadMeters = 1600f,
        };

        public float LoadDistanceFor(SpatialStreamingLodLevel lodLevel) => lodLevel switch
        {
            SpatialStreamingLodLevel.Hlod4x4 => hlod4LoadMeters,
            SpatialStreamingLodLevel.Hlod2x2 => hlod2LoadMeters,
            SpatialStreamingLodLevel.TileProxy => proxyLoadMeters,
            SpatialStreamingLodLevel.SubcellProxy => proxyLoadMeters,
            _ => detailLoadMeters,
        };

        public float UnloadDistanceFor(SpatialStreamingLodLevel lodLevel) => lodLevel switch
        {
            SpatialStreamingLodLevel.Hlod4x4 => hlod4UnloadMeters,
            SpatialStreamingLodLevel.Hlod2x2 => hlod2UnloadMeters,
            SpatialStreamingLodLevel.TileProxy => proxyUnloadMeters,
            SpatialStreamingLodLevel.SubcellProxy => proxyUnloadMeters,
            _ => detailUnloadMeters,
        };
    }
}
