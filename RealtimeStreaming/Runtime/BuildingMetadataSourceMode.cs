namespace ZGConnect.RealtimeStreaming
{
    /// <summary>How runtime resolves per-building metadata from StreamingAssets.</summary>
    public enum BuildingMetadataSourceMode
    {
        /// <summary>Use .bytes when present; fall back to JSON with a warning.</summary>
        PreferBinary = 0,

        /// <summary>Require buildings_{tileId}.bytes; no JSON fallback.</summary>
        BinaryOnly = 1,

        /// <summary>Always parse buildings_{tileId}.json (legacy / debug).</summary>
        JsonOnly = 2,
    }
}
