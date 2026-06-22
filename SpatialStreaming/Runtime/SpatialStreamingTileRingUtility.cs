using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// Camera/tile grid math. Tier want/handoff rules live in <see cref="SpatialStreamingHlodHandoff"/>.
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
            if (factor >= 4)
                return rings.hlod4x4Rings > 0 ? rings.hlod4x4Rings + 1 : 0;

            return rings.hlod2x2Rings > 0 ? rings.hlod2x2Rings + 1 : 0;
        }

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
                return SpatialStreamingHlodHandoff.Block4InHlod4Coverage(
                    rings, blockLeft, blockBottom, cameraTile, tileSizeMeters, forWant);
            }

            return SpatialStreamingHlodHandoff.Block2InExpandedHlod2Coverage(
                rings, blockLeft, blockBottom, cameraTile, tileSizeMeters, forWant);
        }

        public static void AlignHlod2BlockOrigin(
            int tileLeft,
            int tileBottom,
            int tileSizeMeters,
            out int block2Left,
            out int block2Bottom)
        {
            if (tileSizeMeters <= 0)
                tileSizeMeters = 1000;

            int block2Size = tileSizeMeters * 2;
            block2Left = SpatialTileIdUtility.AlignDownMeters(tileLeft, block2Size);
            block2Bottom = SpatialTileIdUtility.AlignDownMeters(tileBottom, block2Size);
        }
    }
}
