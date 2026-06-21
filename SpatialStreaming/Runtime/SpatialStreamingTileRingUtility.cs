using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// Cumulative tile rings from camera tile. Inner rings inherit all outer load layers.
    /// Load at ring &lt; N, keep loaded at ring &lt; N+1.
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

            int blockGridX = blockLeft / blockSizeMeters;
            int blockGridZ = blockBottom / blockSizeMeters;
            int camBlockGridX = SpatialTileIdUtility.AlignDownMeters(cameraTile.Left, blockSizeMeters) / blockSizeMeters;
            int camBlockGridZ = SpatialTileIdUtility.AlignDownMeters(cameraTile.Bottom, blockSizeMeters) / blockSizeMeters;
            return Mathf.Max(Mathf.Abs(blockGridX - camBlockGridX), Mathf.Abs(blockGridZ - camBlockGridZ));
        }

        public static int ChebyshevBlockRingForTile(
            int tileLeft,
            int tileBottom,
            in CameraTileGrid cameraTile,
            int tileSizeMeters,
            int factor)
        {
            int blockSize = tileSizeMeters * Mathf.Max(1, factor);
            int blockLeft = SpatialTileIdUtility.AlignDownMeters(tileLeft, blockSize);
            int blockBottom = SpatialTileIdUtility.AlignDownMeters(tileBottom, blockSize);
            return ChebyshevBlockRing(blockLeft, blockBottom, cameraTile, blockSize);
        }

        public static bool TileInDetailCoverage(SpatialStreamingTileRings rings, int tileRing, bool forWant) =>
            rings.detailRings > 0 && tileRing < rings.detailRings + (forWant ? 1 : 0);

        public static bool TileInSubcellProxyCoverage(SpatialStreamingTileRings rings, int tileRing, bool forWant) =>
            rings.subcellProxyRings > 0 && tileRing < rings.SubcellProxyBandEnd + (forWant ? 1 : 0);

        public static bool TileInTileProxyCoverage(SpatialStreamingTileRings rings, int tileRing, bool forWant) =>
            rings.tileProxyRings > 0 && tileRing < rings.TileProxyBandEnd + (forWant ? 1 : 0);

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

        /// <summary>
        /// True when any 1 km child tile in the supertile block lies inside the cumulative HLOD band.
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

            int bandEnd = factor >= 4 ? rings.Hlod4BandEnd : rings.Hlod2BandEnd;
            if (bandEnd <= 0)
                return false;

            if (factor >= 4 && rings.hlod4x4Rings <= 0)
                return false;

            if (factor < 4 && rings.hlod2x2Rings <= 0)
                return false;

            int limit = bandEnd + (forWant ? 1 : 0);
            int tilesPerSide = Mathf.Max(1, factor);

            for (int dy = 0; dy < tilesPerSide; dy++)
            {
                for (int dx = 0; dx < tilesPerSide; dx++)
                {
                    int tileLeft = blockLeft + dx * tileSizeMeters;
                    int tileBottom = blockBottom + dy * tileSizeMeters;
                    int tileRing = ChebyshevTileRing(tileLeft, tileBottom, cameraTile, tileSizeMeters);
                    if (tileRing < limit)
                        return true;
                }
            }

            return false;
        }
    }
}
