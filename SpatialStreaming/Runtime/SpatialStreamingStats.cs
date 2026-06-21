namespace ZGConnect.SpatialStreaming
{
    public struct SpatialStreamingLodBandStats
    {
        public int Pending;
        public int Loading;
        public int Loaded;
        public int Shown;
    }

    public struct SpatialStreamingStats
    {
        public int Pending;
        public int Loading;
        public int LoadedTotal;
        public int LoadedDetail;
        public int LoadedSubcellProxy;
        public int LoadedTileProxy;
        public int LoadedHlod2x2;
        public int LoadedHlod4x4;
        public int VisibleBlocks;
        public int HiddenBySubstitution;
        public int TotalInstantiated;
        public int TotalLoadFailures;
        public int TotalMeshRenderers;
        public int BlockedByLoadFailure;
        public string LastLoadError;

        public SpatialStreamingLodBandStats Detail;
        public SpatialStreamingLodBandStats SubcellProxy;
        public SpatialStreamingLodBandStats TileProxy;
        public SpatialStreamingLodBandStats Hlod2x2;
        public SpatialStreamingLodBandStats Hlod4x4;

        public SpatialStreamingLodBandStats Band(SpatialStreamingLodLevel level) => level switch
        {
            SpatialStreamingLodLevel.Detail => Detail,
            SpatialStreamingLodLevel.SubcellProxy => SubcellProxy,
            SpatialStreamingLodLevel.TileProxy => TileProxy,
            SpatialStreamingLodLevel.Hlod2x2 => Hlod2x2,
            SpatialStreamingLodLevel.Hlod4x4 => Hlod4x4,
            _ => default,
        };
    }
}
