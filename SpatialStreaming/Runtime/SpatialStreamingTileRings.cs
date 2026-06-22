using System;
using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    [Serializable]
    public struct SpatialStreamingTileRings
    {
        [Tooltip("Square of 250 m subcells with full detail around the camera. 1 = camera subcell only, 2 = 3×3. 0 = skip.")]
        public int detailRings;

        [Tooltip("Cumulative 1 km tile rings outside the detail band for sub-cell proxies. 0 = skip.")]
        public int subcellProxyRings;

        [Tooltip("Cumulative 1 km tile rings outside the sub-cell band for full tile proxies. 0 = skip.")]
        public int tileProxyRings;

        [Tooltip("Cumulative 2×2 km block rings outside finer bands for HLOD2. 0 = skip.")]
        public int hlod2x2Rings;

        [Tooltip("Cumulative 4×4 km block rings — base HLOD layer filling the view. 0 = skip.")]
        public int hlod4x4Rings;

        public static SpatialStreamingTileRings Default => new()
        {
            detailRings = 2,
            subcellProxyRings = 2,
            tileProxyRings = 2,
            hlod2x2Rings = 3,
            hlod4x4Rings = 5,
        };

        public int DetailBandEnd => detailRings > 0 ? detailRings : 0;

        public int SubcellProxyBandStart => DetailBandEnd;

        public int SubcellProxyBandEnd =>
            SubcellProxyBandStart + (subcellProxyRings > 0 ? subcellProxyRings : 0);

        public int TileProxyBandStart => SubcellProxyBandEnd;

        public int TileProxyBandEnd =>
            TileProxyBandStart + (tileProxyRings > 0 ? tileProxyRings : 0);

        public int Hlod2BandStart => TileProxyBandEnd;

        public int Hlod2BandEnd =>
            Hlod2BandStart + (hlod2x2Rings > 0 ? hlod2x2Rings : 0);

        public int Hlod4BandEnd =>
            hlod4x4Rings > 0 ? hlod4x4Rings : 0;

        /// <summary>True when any coarse layer (proxy / HLOD) is configured. 0 = detail-only streaming.</summary>
        public bool UsesCoarseLodChain =>
            subcellProxyRings > 0 || tileProxyRings > 0 || hlod2x2Rings > 0 || hlod4x4Rings > 0;

        /// <summary>Outermost configured cumulative ring end (detail-only uses <see cref="DetailBandEnd"/>).</summary>
        public int FurthestConfiguredRingEnd
        {
            get
            {
                if (hlod4x4Rings > 0)
                    return Hlod4BandEnd;
                if (hlod2x2Rings > 0)
                    return Hlod2BandEnd;
                if (tileProxyRings > 0)
                    return TileProxyBandEnd;
                if (subcellProxyRings > 0)
                    return SubcellProxyBandEnd;
                return DetailBandEnd;
            }
        }
    }
}
