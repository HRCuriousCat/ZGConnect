using System;

namespace ZGConnect.RealtimeStreaming
{
    [Serializable]
    public struct StreamingRegionBoundsEpsg
    {
        public bool Enabled;
        public int MinE;
        public int MaxE;
        public int MinN;
        public int MaxN;

        public bool IntersectsTile(int left, int right, int bottom, int top) =>
            Enabled &&
            left < MaxE && right > MinE &&
            bottom < MaxN && top > MinN;

        public static StreamingRegionBoundsEpsg FromFields(
            bool enabled, int minE, int maxE, int minN, int maxN)
        {
            bool valid = enabled && minE < maxE && minN < maxN;
            return new StreamingRegionBoundsEpsg
            {
                Enabled = valid,
                MinE = minE,
                MaxE = maxE,
                MinN = minN,
                MaxN = maxN,
            };
        }
    }
}
