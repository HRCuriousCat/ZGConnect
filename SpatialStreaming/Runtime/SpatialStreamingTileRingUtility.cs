using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// Cumulative tile rings from camera tile. Detail and 1 km coarse layers use cumulative bands
    /// (load at ring &lt; N, keep loaded at ring &lt; N+1). HLOD2/HLOD4 supertiles use exclusive shells
    /// keyed off the nearest child tile ring in each block.
    /// </summary>
    public static class SpatialStreamingTileRingUtility
    {
        public struct CameraTileGrid
        {
            public int Left;
            public int Bottom;
            public int GridX;
            public int GridZ;
        }

        public static bool TryResolveCameraTileGrid(
            SpatialDatasetManifest manifest,
            Vector3 cameraWorldPos,
            out CameraTileGrid cameraTile)
        {
            return TryResolveCameraTileGrid(manifest, null, cameraWorldPos, out cameraTile);
        }

        public static bool TryResolveCameraTileGrid(
            SpatialDatasetManifest manifest,
            SpatialDatasetRuntimeIndex runtimeIndex,
            Vector3 cameraWorldPos,
            out CameraTileGrid cameraTile)
        {
            cameraTile = default;
            if (runtimeIndex != null && runtimeIndex.TryResolveCameraTileGrid(cameraWorldPos, out cameraTile))
                return true;

            if (manifest?.Tiles == null || manifest.Tiles.Count == 0)
                return false;

            int tileSize = manifest.TileSizeMeters > 0 ? manifest.TileSizeMeters : 1000;

            foreach (SpatialTileManifestEntry tile in manifest.Tiles)
            {
                if (tile == null || string.IsNullOrEmpty(tile.TileId))
                    continue;

                Vector3 origin = tile.GetUnityPosition();
                if (cameraWorldPos.x < origin.x || cameraWorldPos.x >= origin.x + tileSize ||
                    cameraWorldPos.z < origin.z || cameraWorldPos.z >= origin.z + tileSize)
                {
                    continue;
                }

                if (!SpatialTileIdUtility.TryParse(tile.TileId, out int left, out int bottom))
                    return false;

                SpatialTileIdUtility.ToGridIndices(left, bottom, tileSize, out int gridX, out int gridZ);
                cameraTile = new CameraTileGrid
                {
                    Left = left,
                    Bottom = bottom,
                    GridX = gridX,
                    GridZ = gridZ,
                };
                return true;
            }

            SpatialTileManifestEntry reference = manifest.Tiles[0];
            if (reference == null ||
                !SpatialTileIdUtility.TryParse(reference.TileId, out int refLeft, out int refBottom))
            {
                return false;
            }

            Vector3 refOrigin = reference.GetUnityPosition();
            int offsetX = Mathf.FloorToInt((cameraWorldPos.x - refOrigin.x) / tileSize);
            int offsetZ = Mathf.FloorToInt((cameraWorldPos.z - refOrigin.z) / tileSize);
            cameraTile = new CameraTileGrid
            {
                Left = refLeft + offsetX * tileSize,
                Bottom = refBottom + offsetZ * tileSize,
            };
            SpatialTileIdUtility.ToGridIndices(cameraTile.Left, cameraTile.Bottom, tileSize,
                out cameraTile.GridX, out cameraTile.GridZ);
            return true;
        }

        public static int ChebyshevTileRing(int tileLeft, int tileBottom, in CameraTileGrid cameraTile, int tileSizeMeters)
        {
            if (tileSizeMeters <= 0)
                tileSizeMeters = 1000;

            SpatialTileIdUtility.ToGridIndices(tileLeft, tileBottom, tileSizeMeters, out int gridX, out int gridZ);
            return Mathf.Max(Mathf.Abs(gridX - cameraTile.GridX), Mathf.Abs(gridZ - cameraTile.GridZ));
        }

        public static int ChebyshevBlockRing(
            int blockLeft,
            int blockBottom,
            in CameraTileGrid cameraTile,
            int blockSizeMeters)
        {
            if (blockSizeMeters <= 0)
                return int.MaxValue;

            SpatialTileIdUtility.ToGridIndices(blockLeft, blockBottom, blockSizeMeters, out int blockGridX, out int blockGridZ);
            int camBlockLeft = SpatialTileIdUtility.AlignDownMeters(cameraTile.Left, blockSizeMeters);
            int camBlockBottom = SpatialTileIdUtility.AlignDownMeters(cameraTile.Bottom, blockSizeMeters);
            SpatialTileIdUtility.ToGridIndices(camBlockLeft, camBlockBottom, blockSizeMeters, out int camBlockGridX, out int camBlockGridZ);
            return Mathf.Max(Mathf.Abs(blockGridX - camBlockGridX), Mathf.Abs(blockGridZ - camBlockGridZ));
        }

        public static int MinTileRingInBlock(
            int blockLeft,
            int blockBottom,
            in CameraTileGrid cameraTile,
            int tileSizeMeters,
            int factor)
        {
            if (tileSizeMeters <= 0)
                tileSizeMeters = 1000;

            int tilesPerSide = Mathf.Max(1, factor);
            int minRing = int.MaxValue;
            for (int dy = 0; dy < tilesPerSide; dy++)
            {
                for (int dx = 0; dx < tilesPerSide; dx++)
                {
                    int tileLeft = blockLeft + dx * tileSizeMeters;
                    int tileBottom = blockBottom + dy * tileSizeMeters;
                    int tileRing = ChebyshevTileRing(tileLeft, tileBottom, cameraTile, tileSizeMeters);
                    if (tileRing < minRing)
                        minRing = tileRing;
                }
            }

            return minRing == int.MaxValue ? 0 : minRing;
        }

        static bool TileInExclusiveBand(int tileRing, int bandStart, int bandWidth, bool forWant)
        {
            if (bandWidth <= 0)
                return false;

            int limit = bandStart + bandWidth + (forWant ? 1 : 0);
            return tileRing >= bandStart && tileRing < limit;
        }

        public static bool TileInDetailCoverage(SpatialStreamingTileRings rings, int tileRing, bool forWant) =>
            rings.detailRings > 0 && tileRing < rings.detailRings + (forWant ? 1 : 0);

        public static bool TileInExclusiveSubcellProxyCoverage(SpatialStreamingTileRings rings, int tileRing, bool forWant) =>
            TileInExclusiveBand(tileRing, rings.SubcellProxyBandStart, rings.subcellProxyRings, forWant);

        public static bool TileInExclusiveTileProxyCoverage(SpatialStreamingTileRings rings, int tileRing, bool forWant) =>
            TileInExclusiveBand(tileRing, rings.TileProxyBandStart, rings.tileProxyRings, forWant);

        public static bool TileInExclusiveHlod2Coverage(SpatialStreamingTileRings rings, int tileRing, bool forWant) =>
            TileInExclusiveBand(tileRing, rings.Hlod2BandStart, rings.hlod2x2Rings, forWant);

        public static bool TileInExclusiveHlod4Coverage(SpatialStreamingTileRings rings, int tileRing, bool forWant) =>
            TileInExclusiveBand(tileRing, rings.Hlod4BandStart, rings.hlod4x4Rings, forWant);

        /// <summary>
        /// Cumulative 1 km coarse through sub-cell band, extended through the tile-proxy band so subcell tiles
        /// stay covered until HLOD2 takes over (avoids a void at the tile-proxy shell).
        /// </summary>
        public static bool TileInSubcellProxyCoverage(SpatialStreamingTileRings rings, int tileRing, bool forWant)
        {
            if (rings.subcellProxyRings <= 0 && rings.detailRings <= 0)
                return false;

            int end = rings.tileProxyRings > 0 ? rings.TileProxyBandEnd : rings.SubcellProxyBandEnd;
            return tileRing < end + (forWant ? 1 : 0);
        }

        /// <summary>Cumulative 1 km tile-proxy band (includes detail + sub-cell coarse).</summary>
        public static bool TileInTileProxyCoverage(SpatialStreamingTileRings rings, int tileRing, bool forWant)
        {
            if (rings.tileProxyRings <= 0)
                return false;

            return tileRing < rings.TileProxyBandEnd + (forWant ? 1 : 0);
        }

        public static bool ShouldWantDetail(SpatialStreamingTileRings rings, int tileRing) =>
            TileInDetailCoverage(rings, tileRing, forWant: true);

        public static bool ShouldQueueDetail(SpatialStreamingTileRings rings, int tileRing) =>
            TileInDetailCoverage(rings, tileRing, forWant: false);

        public static bool ShouldWantSubcellProxy(SpatialStreamingTileRings rings, int tileRing) =>
            TileInSubcellProxyCoverage(rings, tileRing, forWant: true);

        public static bool ShouldQueueSubcellProxy(SpatialStreamingTileRings rings, int tileRing) =>
            TileInSubcellProxyCoverage(rings, tileRing, forWant: false);

        public static bool ShouldWantTileProxy(SpatialStreamingTileRings rings, int tileRing) =>
            TileInTileProxyCoverage(rings, tileRing, forWant: true);

        public static bool ShouldQueueTileProxy(SpatialStreamingTileRings rings, int tileRing) =>
            TileInTileProxyCoverage(rings, tileRing, forWant: false);

        public static bool ShouldWantSupertile(
            SpatialStreamingTileRings rings,
            int blockLeft,
            int blockBottom,
            in CameraTileGrid cameraTile,
            int tileSizeMeters,
            int factor) =>
            SupertileInCoverage(rings, blockLeft, blockBottom, cameraTile, tileSizeMeters, factor, forWant: true);

        public static bool ShouldQueueSupertile(
            SpatialStreamingTileRings rings,
            int blockLeft,
            int blockBottom,
            in CameraTileGrid cameraTile,
            int tileSizeMeters,
            int factor) =>
            SupertileInCoverage(rings, blockLeft, blockBottom, cameraTile, tileSizeMeters, factor, forWant: false);

        public static int MaxSupertileBlockScanRing(SpatialStreamingTileRings rings, int factor)
        {
            int maxTileRing = factor >= 4 ? rings.Hlod4BandEnd : rings.Hlod2BandEnd;
            if (maxTileRing <= 0)
                return 0;

            return (maxTileRing + factor) / factor;
        }

        /// <summary>
        /// Supertile is in band when its nearest 1 km child tile lies in the exclusive HLOD shell for that factor.
        /// </summary>
        public static bool SupertileInCoverage(
            SpatialStreamingTileRings rings,
            int blockLeft,
            int blockBottom,
            in CameraTileGrid cameraTile,
            int tileSizeMeters,
            int factor,
            bool forWant)
        {
            if (tileSizeMeters <= 0)
                tileSizeMeters = 1000;

            if (factor >= 4)
            {
                if (rings.hlod4x4Rings <= 0)
                    return false;
            }
            else if (rings.hlod2x2Rings <= 0)
            {
                return false;
            }

            int minTileRing = MinTileRingInBlock(blockLeft, blockBottom, cameraTile, tileSizeMeters, factor);
            return factor >= 4
                ? TileInExclusiveHlod4Coverage(rings, minTileRing, forWant)
                : TileInExclusiveHlod2Coverage(rings, minTileRing, forWant);
        }
    }
}
