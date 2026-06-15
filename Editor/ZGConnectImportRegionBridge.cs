using System;
using System.Collections.Generic;

namespace ZGConnect.Editor
{
    /// <summary>
    /// Connects <see cref="ZGConnectTileMapWindow"/> to the active import/pack window.
    /// </summary>
    public static class ZGConnectImportRegionBridge
    {
        public static Func<List<HeightmapTileJson>> GetTiles;
        /// <summary>
        /// EPSG bounds of the overview texture (full heightmap metadata coverage).
        /// When set, map background georef uses this instead of the displayed tile union.
        /// </summary>
        public static Func<(int minE, int maxE, int minN, int maxN)> GetOverviewGeorefBounds;
        public static Func<(int minE, int maxE, int minN, int maxN)> GetRegionEpsg;
        public static Action<int, int, int, int> ApplyRegionEpsg;
        public static Action RepaintImporter;
        /// <summary>Tile ids (left_bottom) already packed/imported. Optional â€” map uses distinct color.</summary>
        public static Func<HashSet<string>> GetPackedTileIds;
        /// <summary>When true, map selection snaps to the pack HLOD grid (4×4). Streamer load limit uses false.</summary>
        public static bool AlignSelectionToPackGrid = true;
        public static string ApplyTargetLabel = "Import Manager";

        public static bool IsImporterReady => GetTiles != null && ApplyRegionEpsg != null;

        public static void Clear()
        {
            GetTiles                 = null;
            GetOverviewGeorefBounds  = null;
            GetRegionEpsg            = null;
            ApplyRegionEpsg  = null;
            RepaintImporter  = null;
            GetPackedTileIds = null;
            AlignSelectionToPackGrid = true;
            ApplyTargetLabel = "Import Manager";
        }
    }
}
