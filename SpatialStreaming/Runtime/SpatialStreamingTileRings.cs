using System;
using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    [Serializable]
    public struct SpatialStreamingTileRings
    {
        [Tooltip("Square of 1 km tiles with full detail. 1 = camera tile only, 2 = 3×3 (9 tiles). 0 = skip.")]
        public int detailRings;

        [Tooltip("Exclusive 1 km tile rings outside the detail band for sub-cell proxies. 0 = skip.")]
        public int subcellProxyRings;

        [Tooltip("Exclusive 1 km tile rings outside the sub-cell band for full tile proxies. 0 = skip.")]
        public int tileProxyRings;

        [Tooltip("Exclusive 1 km tile rings outside the tile-proxy band for HLOD2 (2×2 km blocks). 0 = skip.")]
        public int hlod2x2Rings;

        [Tooltip("Exclusive 1 km tile rings outside the HLOD2 band for HLOD4 (4×4 km blocks). 0 = skip.")]
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

        public int Hlod4BandStart => Hlod2BandEnd;

        public int Hlod4BandEnd =>
            Hlod4BandStart + (hlod4x4Rings > 0 ? hlod4x4Rings : 0);

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
