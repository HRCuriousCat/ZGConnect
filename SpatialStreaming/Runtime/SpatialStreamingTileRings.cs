using System;
using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    [Serializable]
    public struct SpatialStreamingTileRings
    {
        [Tooltip("Square of 250 m subcells with full detail around the camera. 1 = camera subcell only, 2 = 3×3. 0 = skip.")]
        public int detailRings;

        [Tooltip("Cumulative 1 km tile rings outside the detail footprint for sub-cell proxies. 0 = skip.")]
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

        /// <summary>
        /// Converts 250 m detail rings to an equivalent 1 km tile Chebyshev radius (ceil).
        /// </summary>
        public static int DetailAsTileRings(int detailRings, int tileSizeMeters, int subcellSizeMeters)
        {
            if (detailRings <= 0 || tileSizeMeters <= 0 || subcellSizeMeters <= 0)
                return 0;

            int cellsPerEdge = Mathf.Max(1, tileSizeMeters / subcellSizeMeters);
            return (detailRings + cellsPerEdge - 1) / cellsPerEdge;
        }

        public int DetailAsTileBandEnd(int tileSizeMeters, int subcellSizeMeters) =>
            DetailAsTileRings(detailRings, tileSizeMeters, subcellSizeMeters);

        public int SubcellProxyBandEnd(int tileSizeMeters, int subcellSizeMeters) =>
            DetailAsTileBandEnd(tileSizeMeters, subcellSizeMeters) +
            (subcellProxyRings > 0 ? subcellProxyRings : 0);

        public int TileProxyBandEnd(int tileSizeMeters, int subcellSizeMeters) =>
            SubcellProxyBandEnd(tileSizeMeters, subcellSizeMeters) +
            (tileProxyRings > 0 ? tileProxyRings : 0);

        public int Hlod2TileBandEnd(int tileSizeMeters, int subcellSizeMeters) =>
            TileProxyBandEnd(tileSizeMeters, subcellSizeMeters) +
            (hlod2x2Rings > 0 ? hlod2x2Rings : 0);

        /// <summary>True when any coarse layer (proxy / HLOD) is configured. 0 = detail-only streaming.</summary>
        public bool UsesCoarseLodChain =>
            subcellProxyRings > 0 || tileProxyRings > 0 || hlod2x2Rings > 0 || hlod4x4Rings > 0;

        /// <summary>Outermost configured 1 km tile-ring end for proxy layers.</summary>
        public int FurthestConfiguredTileRingEnd(int tileSizeMeters, int subcellSizeMeters)
        {
            if (tileProxyRings > 0)
                return TileProxyBandEnd(tileSizeMeters, subcellSizeMeters);
            if (subcellProxyRings > 0)
                return SubcellProxyBandEnd(tileSizeMeters, subcellSizeMeters);
            if (detailRings > 0)
                return DetailAsTileBandEnd(tileSizeMeters, subcellSizeMeters);
            return 0;
        }

        /// <summary>Outermost configured horizon for tile iteration (includes HLOD block rings as a floor).</summary>
        public int FurthestConfiguredRingEnd(int tileSizeMeters, int subcellSizeMeters)
        {
            int tileEnd = FurthestConfiguredTileRingEnd(tileSizeMeters, subcellSizeMeters);
            if (hlod2x2Rings > 0)
                tileEnd = Mathf.Max(tileEnd, Hlod2TileBandEnd(tileSizeMeters, subcellSizeMeters));
            if (hlod4x4Rings > 0)
                tileEnd = Mathf.Max(tileEnd, hlod4x4Rings * 4);
            return tileEnd;
        }
    }
}
