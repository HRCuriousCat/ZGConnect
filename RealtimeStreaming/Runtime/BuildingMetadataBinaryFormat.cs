using System;
using System.Text;

namespace ZGConnect.RealtimeStreaming
{
    /// <summary>On-disk layout for buildings_{tileId}.bytes (ZGBM v1).</summary>
    public static class BuildingMetadataBinaryFormat
    {
        public const int Version = 1;
        public const int HeaderSize = 48;

        public static readonly byte[] Magic = { (byte)'Z', (byte)'G', (byte)'B', (byte)'M' };

        public const int IndexEntrySize = 16;
    }
}
