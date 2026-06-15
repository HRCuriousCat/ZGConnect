namespace ZGConnect
{
    /// <summary>Per-frame vegetation draw counters for HUD / profiling. Reset once per frame.</summary>
    public static class VegetationRenderStats
    {
        static int _frame = -1;

        public static int ChunksActive;
        public static int ChunksFrustumCulled;
        public static int InstancesRegistered;
        public static int InstancesDrawn;
        public static int InstancesFrustumCulled;
        public static int InstancesDistanceCulled;
        public static int TrianglesDrawn;

        public static void BeginFrame()
        {
            if (_frame == UnityEngine.Time.frameCount)
                return;

            _frame = UnityEngine.Time.frameCount;
            ChunksActive = 0;
            ChunksFrustumCulled = 0;
            InstancesRegistered = 0;
            InstancesDrawn = 0;
            InstancesFrustumCulled = 0;
            InstancesDistanceCulled = 0;
            TrianglesDrawn = 0;
        }

        public static void RegisterChunk(int instanceCount)
        {
            BeginFrame();
            ChunksActive++;
            InstancesRegistered += instanceCount;
        }

        public static void ChunkFrustumCulled(int instanceCount)
        {
            ChunksFrustumCulled++;
            InstancesFrustumCulled += instanceCount;
        }

        public static void RecordDraw(int instanceCount, int trianglesPerInstance)
        {
            InstancesDrawn += instanceCount;
            TrianglesDrawn += instanceCount * trianglesPerInstance;
        }

        public static void RecordInstanceFrustumCull(int culledCount)
        {
            InstancesFrustumCulled += culledCount;
        }

        public static void RecordInstanceDistanceCull(int culledCount)
        {
            InstancesDistanceCulled += culledCount;
        }
    }
}
