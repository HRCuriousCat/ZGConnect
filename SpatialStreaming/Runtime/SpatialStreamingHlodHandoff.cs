using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// Hierarchical LOD handoff: HLOD4 fills the configured view; each finer tier expands to the
    /// full parent footprint when any child would activate. Detail rings use the 250 m subcell grid;
    /// detail replaces its subcell proxy per cell when loaded.
    /// </summary>
    public static class SpatialStreamingHlodHandoff
    {
        public struct CameraSubcellGrid
        {
            public int GlobalGridX;
            public int GlobalGridZ;
        }

        public static int ChebyshevRing(int gridX, int gridZ, int cameraGridX, int cameraGridZ) =>
            Mathf.Max(Mathf.Abs(gridX - cameraGridX), Mathf.Abs(gridZ - cameraGridZ));

        public static bool TryResolveCameraSubcellGrid(
            SpatialDatasetManifest manifest,
            SpatialDatasetRuntimeIndex runtimeIndex,
            Vector3 cameraWorldPos,
            out CameraSubcellGrid cameraSubcell,
            out SpatialStreamingTileRingUtility.CameraTileGrid cameraTile)
        {
            cameraSubcell = default;
            cameraTile = default;
            if (!SpatialStreamingTileRingUtility.TryResolveCameraTileGrid(
                    manifest, runtimeIndex, cameraWorldPos, out cameraTile))
            {
                return false;
            }

            int tileSize = manifest?.TileSizeMeters > 0 ? manifest.TileSizeMeters : 1000;
            int subcellSize = manifest?.SubcellSizeMeters > 0 ? manifest.SubcellSizeMeters : 250;
            if (subcellSize <= 0 || tileSize < subcellSize)
                return false;

            int cellsPerEdge = tileSize / subcellSize;
            if (cellsPerEdge <= 0)
                cellsPerEdge = 1;

            SpatialTileManifestEntry referenceTile = manifest?.FindTile(
                SpatialTileIdUtility.Format(cameraTile.Left, cameraTile.Bottom));
            Vector3 tileOrigin = referenceTile != null
                ? referenceTile.GetUnityPosition()
                : new Vector3(cameraTile.Left, 0f, cameraTile.Bottom);

            int localCellX = Mathf.Clamp(
                Mathf.FloorToInt((cameraWorldPos.x - tileOrigin.x) / subcellSize),
                0,
                cellsPerEdge - 1);
            int localCellZ = Mathf.Clamp(
                Mathf.FloorToInt((cameraWorldPos.z - tileOrigin.z) / subcellSize),
                0,
                cellsPerEdge - 1);

            SubcellToGlobalGrid(
                cameraTile.Left,
                cameraTile.Bottom,
                localCellX,
                localCellZ,
                tileSize,
                subcellSize,
                out cameraSubcell.GlobalGridX,
                out cameraSubcell.GlobalGridZ);
            return true;
        }

        public static void SubcellToGlobalGrid(
            int tileLeft,
            int tileBottom,
            int subcellGridX,
            int subcellGridY,
            int tileSizeMeters,
            int subcellSizeMeters,
            out int globalGridX,
            out int globalGridZ)
        {
            if (tileSizeMeters <= 0)
                tileSizeMeters = 1000;
            if (subcellSizeMeters <= 0)
                subcellSizeMeters = 250;

            int cellsPerEdge = Mathf.Max(1, tileSizeMeters / subcellSizeMeters);
            SpatialTileIdUtility.ToGridIndices(tileLeft, tileBottom, tileSizeMeters, out int tileGridX, out int tileGridZ);
            globalGridX = tileGridX * cellsPerEdge + subcellGridX;
            globalGridZ = tileGridZ * cellsPerEdge + subcellGridY;
        }

        public static int GetSubcellRing(
            int tileLeft,
            int tileBottom,
            int subcellGridX,
            int subcellGridY,
            in CameraSubcellGrid cameraSubcell,
            int tileSizeMeters,
            int subcellSizeMeters)
        {
            SubcellToGlobalGrid(
                tileLeft,
                tileBottom,
                subcellGridX,
                subcellGridY,
                tileSizeMeters,
                subcellSizeMeters,
                out int globalGridX,
                out int globalGridZ);
            return ChebyshevRing(
                globalGridX,
                globalGridZ,
                cameraSubcell.GlobalGridX,
                cameraSubcell.GlobalGridZ);
        }

        public static bool SubcellInDetailCoverage(SpatialStreamingTileRings rings, int subcellRing, bool forWant) =>
            rings.detailRings > 0 && subcellRing < rings.detailRings + (forWant ? 1 : 0);

        public static bool ShouldWantDetailForSubcell(
            SpatialStreamingTileRings rings,
            int subcellRing,
            bool forWant) =>
            rings.detailRings > 0 && SubcellInDetailCoverage(rings, subcellRing, forWant);

        public static bool TileInSubcellProxyCoverage(
            SpatialStreamingTileRings rings,
            int tileRing,
            int tileSizeMeters,
            int subcellSizeMeters,
            bool forWant)
        {
            if (rings.subcellProxyRings <= 0)
                return false;

            int end = rings.SubcellProxyBandEnd(tileSizeMeters, subcellSizeMeters);
            return tileRing < end + (forWant ? 1 : 0);
        }

        public static bool TileInTileProxyCoverage(
            SpatialStreamingTileRings rings,
            int tileRing,
            int tileSizeMeters,
            int subcellSizeMeters,
            bool forWant)
        {
            if (rings.tileProxyRings <= 0)
                return false;

            return tileRing < rings.TileProxyBandEnd(tileSizeMeters, subcellSizeMeters) + (forWant ? 1 : 0);
        }

        public static bool Block4InHlod4Coverage(
            SpatialStreamingTileRings rings,
            int block4Left,
            int block4Bottom,
            in SpatialStreamingTileRingUtility.CameraTileGrid cameraTile,
            int tileSizeMeters,
            bool forWant)
        {
            if (rings.hlod4x4Rings <= 0)
                return false;

            int block4Size = tileSizeMeters * 4;
            int blockRing = SpatialStreamingTileRingUtility.ChebyshevBlockRing(
                block4Left, block4Bottom, cameraTile, block4Size);
            return blockRing < rings.hlod4x4Rings + (forWant ? 1 : 0);
        }

        public static bool Block2DirectlyInHlod2Coverage(
            SpatialStreamingTileRings rings,
            int block2Left,
            int block2Bottom,
            in SpatialStreamingTileRingUtility.CameraTileGrid cameraTile,
            int tileSizeMeters,
            bool forWant)
        {
            if (rings.hlod2x2Rings <= 0)
                return false;

            int block2Size = tileSizeMeters * 2;
            int blockRing = SpatialStreamingTileRingUtility.ChebyshevBlockRing(
                block2Left, block2Bottom, cameraTile, block2Size);
            return blockRing < rings.hlod2x2Rings + (forWant ? 1 : 0);
        }

        public static void AlignHlod4BlockOrigin(
            int block2Left,
            int block2Bottom,
            int tileSizeMeters,
            out int block4Left,
            out int block4Bottom)
        {
            if (tileSizeMeters <= 0)
                tileSizeMeters = 1000;

            int block4Size = tileSizeMeters * 4;
            block4Left = SpatialTileIdUtility.AlignDownMeters(block2Left, block4Size);
            block4Bottom = SpatialTileIdUtility.AlignDownMeters(block2Bottom, block4Size);
        }

        public static bool Hlod4BlockHasAnyChildInDirectHlod2Coverage(
            SpatialStreamingTileRings rings,
            int block4Left,
            int block4Bottom,
            in SpatialStreamingTileRingUtility.CameraTileGrid cameraTile,
            int tileSizeMeters,
            bool forWant)
        {
            if (tileSizeMeters <= 0)
                tileSizeMeters = 1000;

            int block2Size = tileSizeMeters * 2;
            for (int dy = 0; dy < 2; dy++)
            {
                for (int dx = 0; dx < 2; dx++)
                {
                    int childLeft = block4Left + dx * block2Size;
                    int childBottom = block4Bottom + dy * block2Size;
                    if (Block2DirectlyInHlod2Coverage(
                            rings, childLeft, childBottom, cameraTile, tileSizeMeters, forWant))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static bool Block2InExpandedHlod2Coverage(
            SpatialStreamingTileRings rings,
            int block2Left,
            int block2Bottom,
            in SpatialStreamingTileRingUtility.CameraTileGrid cameraTile,
            int tileSizeMeters,
            bool forWant)
        {
            if (Block2DirectlyInHlod2Coverage(rings, block2Left, block2Bottom, cameraTile, tileSizeMeters, forWant))
                return true;

            if (rings.hlod4x4Rings <= 0 || rings.hlod2x2Rings <= 0)
                return false;

            AlignHlod4BlockOrigin(block2Left, block2Bottom, tileSizeMeters, out int block4Left, out int block4Bottom);
            return Hlod4BlockHasAnyChildInDirectHlod2Coverage(
                rings, block4Left, block4Bottom, cameraTile, tileSizeMeters, forWant);
        }

        public static bool TileDirectlyTriggersOneKmLayer(
            SpatialStreamingTileRings rings,
            int tileRing,
            int tileSizeMeters,
            int subcellSizeMeters,
            bool anySubcellInDetailBand)
        {
            if (anySubcellInDetailBand)
                return true;

            return TileInSubcellProxyCoverage(rings, tileRing, tileSizeMeters, subcellSizeMeters, forWant: true) ||
                   TileInTileProxyCoverage(rings, tileRing, tileSizeMeters, subcellSizeMeters, forWant: true);
        }

        public static bool Hlod2BlockHasAnyChildDirectlyTriggeringOneKm(
            SpatialStreamingTileRings rings,
            int block2Left,
            int block2Bottom,
            in SpatialStreamingTileRingUtility.CameraTileGrid cameraTile,
            int tileSizeMeters,
            int subcellSizeMeters,
            System.Func<int, int, bool> anySubcellInDetailBandForTile)
        {
            if (tileSizeMeters <= 0)
                tileSizeMeters = 1000;
            if (subcellSizeMeters <= 0)
                subcellSizeMeters = 250;

            for (int dy = 0; dy < 2; dy++)
            {
                for (int dx = 0; dx < 2; dx++)
                {
                    int tileLeft = block2Left + dx * tileSizeMeters;
                    int tileBottom = block2Bottom + dy * tileSizeMeters;
                    int tileRing = SpatialStreamingTileRingUtility.ChebyshevTileRing(
                        tileLeft, tileBottom, cameraTile, tileSizeMeters);
                    bool anyDetail = anySubcellInDetailBandForTile != null &&
                                     anySubcellInDetailBandForTile(tileLeft, tileBottom);
                    if (TileDirectlyTriggersOneKmLayer(
                            rings, tileRing, tileSizeMeters, subcellSizeMeters, anyDetail))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static bool TileInExpandedTileProxyCoverage(
            SpatialStreamingTileRings rings,
            int tileLeft,
            int tileBottom,
            in SpatialStreamingTileRingUtility.CameraTileGrid cameraTile,
            int tileSizeMeters,
            int subcellSizeMeters,
            bool forWant,
            System.Func<int, int, bool> anySubcellInDetailBandForTile)
        {
            if (tileSizeMeters <= 0)
                tileSizeMeters = 1000;
            if (subcellSizeMeters <= 0)
                subcellSizeMeters = 250;

            int tileRing = SpatialStreamingTileRingUtility.ChebyshevTileRing(
                tileLeft, tileBottom, cameraTile, tileSizeMeters);
            bool anyDetail = anySubcellInDetailBandForTile != null &&
                             anySubcellInDetailBandForTile(tileLeft, tileBottom);
            if (TileInTileProxyCoverage(rings, tileRing, tileSizeMeters, subcellSizeMeters, forWant) || anyDetail)
                return true;

            if (TileInSubcellProxyCoverage(rings, tileRing, tileSizeMeters, subcellSizeMeters, forWant))
                return true;

            if (rings.hlod2x2Rings <= 0)
                return false;

            SpatialStreamingTileRingUtility.AlignHlod2BlockOrigin(
                tileLeft, tileBottom, tileSizeMeters, out int block2Left, out int block2Bottom);
            return Hlod2BlockHasAnyChildDirectlyTriggeringOneKm(
                rings, block2Left, block2Bottom, cameraTile, tileSizeMeters, subcellSizeMeters, anySubcellInDetailBandForTile);
        }

        public static bool TileInExpandedSubcellProxyCoverage(
            SpatialStreamingTileRings rings,
            SpatialTileManifestEntry tile,
            int tileLeft,
            int tileBottom,
            in SpatialStreamingTileRingUtility.CameraTileGrid cameraTile,
            in CameraSubcellGrid cameraSubcell,
            int tileSizeMeters,
            int subcellSizeMeters,
            bool forWant)
        {
            if (TileWantsFullSubcellProxySet(
                    rings, tile, tileLeft, tileBottom, cameraTile, cameraSubcell, tileSizeMeters, subcellSizeMeters, forWant))
            {
                return true;
            }

            return false;
        }

        static System.Func<int, int, bool> BuildDetailBandProbe(
            SpatialStreamingTileRings rings,
            SpatialTileManifestEntry tile,
            CameraSubcellGrid cameraSubcell,
            int tileSizeMeters,
            int subcellSizeMeters,
            bool forWant)
        {
            return (left, bottom) =>
            {
                if (tile == null || !string.Equals(tile.TileId, SpatialTileIdUtility.Format(left, bottom)))
                    return false;

                return TileHasAnySubcellInDetailBand(
                    rings, tile, left, bottom, cameraSubcell, tileSizeMeters, subcellSizeMeters, forWant);
            };
        }

        public static System.Func<int, int, bool> CreateDetailBandProbe(
            SpatialStreamingTileRings rings,
            SpatialTileManifestEntry tile,
            CameraSubcellGrid cameraSubcell,
            int tileSizeMeters,
            int subcellSizeMeters,
            bool forWant) =>
            BuildDetailBandProbe(rings, tile, cameraSubcell, tileSizeMeters, subcellSizeMeters, forWant);

        public static bool TileInExpandedOneKmCoverage(
            SpatialStreamingTileRings rings,
            int tileLeft,
            int tileBottom,
            in SpatialStreamingTileRingUtility.CameraTileGrid cameraTile,
            int tileSizeMeters,
            int subcellSizeMeters,
            bool forWant,
            System.Func<int, int, bool> anySubcellInDetailBandForTile)
        {
            if (tileSizeMeters <= 0)
                tileSizeMeters = 1000;
            if (subcellSizeMeters <= 0)
                subcellSizeMeters = 250;

            int tileRing = SpatialStreamingTileRingUtility.ChebyshevTileRing(
                tileLeft, tileBottom, cameraTile, tileSizeMeters);
            bool anyDetail = anySubcellInDetailBandForTile != null &&
                             anySubcellInDetailBandForTile(tileLeft, tileBottom);
            if (TileDirectlyTriggersOneKmLayer(
                    rings, tileRing, tileSizeMeters, subcellSizeMeters, anyDetail))
            {
                return true;
            }

            if (rings.hlod2x2Rings <= 0)
                return false;

            SpatialStreamingTileRingUtility.AlignHlod2BlockOrigin(
                tileLeft, tileBottom, tileSizeMeters, out int block2Left, out int block2Bottom);
            return Hlod2BlockHasAnyChildDirectlyTriggeringOneKm(
                rings, block2Left, block2Bottom, cameraTile, tileSizeMeters, subcellSizeMeters, anySubcellInDetailBandForTile);
        }

        public static bool TileHasAnySubcellInDetailBand(
            SpatialStreamingTileRings rings,
            SpatialTileManifestEntry tile,
            int tileLeft,
            int tileBottom,
            in CameraSubcellGrid cameraSubcell,
            int tileSizeMeters,
            int subcellSizeMeters,
            bool forWant)
        {
            if (tile?.Subcells == null || rings.detailRings <= 0)
                return false;

            foreach (SpatialSubcellManifestEntry subcell in tile.Subcells)
            {
                if (subcell == null)
                    continue;

                int subcellRing = GetSubcellRing(
                    tileLeft,
                    tileBottom,
                    subcell.GridX,
                    subcell.GridY,
                    cameraSubcell,
                    tileSizeMeters,
                    subcellSizeMeters);
                if (SubcellInDetailCoverage(rings, subcellRing, forWant))
                    return true;
            }

            return false;
        }

        public static bool TileWantsFullSubcellProxySet(
            SpatialStreamingTileRings rings,
            SpatialTileManifestEntry tile,
            int tileLeft,
            int tileBottom,
            in SpatialStreamingTileRingUtility.CameraTileGrid cameraTile,
            in CameraSubcellGrid cameraSubcell,
            int tileSizeMeters,
            int subcellSizeMeters,
            bool forWant)
        {
            if (!TileInExpandedOneKmCoverage(
                    rings,
                    tileLeft,
                    tileBottom,
                    cameraTile,
                    tileSizeMeters,
                    subcellSizeMeters,
                    forWant,
                    CreateDetailBandProbe(
                        rings, tile, cameraSubcell, tileSizeMeters, subcellSizeMeters, forWant)))
            {
                return false;
            }

            int tileRing = SpatialStreamingTileRingUtility.ChebyshevTileRing(
                tileLeft, tileBottom, cameraTile, tileSizeMeters);
            if (TileInSubcellProxyCoverage(rings, tileRing, tileSizeMeters, subcellSizeMeters, forWant))
                return true;

            return TileHasAnySubcellInDetailBand(
                rings, tile, tileLeft, tileBottom, cameraSubcell, tileSizeMeters, subcellSizeMeters, forWant);
        }

        public static bool Hlod4BlockRequiresMonolithicHlod2Handoff(
            SpatialStreamingTileRings rings,
            int block4Left,
            int block4Bottom,
            in SpatialStreamingTileRingUtility.CameraTileGrid cameraTile,
            int tileSizeMeters) =>
            rings.hlod4x4Rings > 0 &&
            rings.hlod2x2Rings > 0 &&
            Hlod4BlockHasAnyChildInDirectHlod2Coverage(
                rings, block4Left, block4Bottom, cameraTile, tileSizeMeters, forWant: true);

        public static bool Hlod2BlockRequiresMonolithicOneKmHandoff(
            SpatialStreamingTileRings rings,
            int block2Left,
            int block2Bottom,
            in SpatialStreamingTileRingUtility.CameraTileGrid cameraTile,
            int tileSizeMeters,
            int subcellSizeMeters,
            System.Func<int, int, bool> anySubcellInDetailBandForTile) =>
            rings.hlod2x2Rings > 0 &&
            Hlod2BlockHasAnyChildDirectlyTriggeringOneKm(
                rings, block2Left, block2Bottom, cameraTile, tileSizeMeters, subcellSizeMeters, anySubcellInDetailBandForTile);

        public static int FurthestTileRingHorizon(SpatialStreamingTileRings rings, int tileSizeMeters, int subcellSizeMeters)
        {
            if (tileSizeMeters <= 0)
                tileSizeMeters = 1000;
            if (subcellSizeMeters <= 0)
                subcellSizeMeters = 250;

            return rings.FurthestConfiguredRingEnd(tileSizeMeters, subcellSizeMeters) + 1;
        }
    }
}
