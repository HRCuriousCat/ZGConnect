namespace ZGConnect
{
    public enum VegetationSpawnAnimationMode
    {
        None = 0,
        /// <summary>Whole tile chunk scales in quickly (single factor, cheap).</summary>
        ChunkPopIn = 1,
        /// <summary>Each instance grows with staggered timing (CPU matrix rebuild per frame).</summary>
        PerTree = 2
    }
}

