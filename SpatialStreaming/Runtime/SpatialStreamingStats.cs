namespace ZGConnect.SpatialStreaming
{
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
    }
}
