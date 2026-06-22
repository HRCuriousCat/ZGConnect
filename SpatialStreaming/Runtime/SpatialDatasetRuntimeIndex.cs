using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// Bake-time/runtime spatial indexes for O(1) tile, subcell, and supertile lookup by grid or block origin.
    /// Built once when the manifest loads; does not mutate manifest JSON.
    /// </summary>
    public sealed class SpatialDatasetRuntimeIndex
    {
        public struct GridOrigin
        {
            public int TileSizeMeters;
            public int SubcellSizeMeters;
            public Vector3 UnityOrigin;
            public int RefLeft;
            public int RefBottom;
            public int MinGridX;
            public int MinGridZ;
            public int MaxGridX;
            public int MaxGridZ;
            public bool HasBounds;
            public bool HasReferenceTile;
        }

        readonly Dictionary<string, SpatialTileManifestEntry> _tileById = new(StringComparer.Ordinal);
        readonly Dictionary<long, SpatialTileManifestEntry> _tileByGrid = new();
        readonly Dictionary<string, SpatialSupertileManifestEntry> _supertileById = new(StringComparer.Ordinal);
        readonly Dictionary<long, SpatialSupertileManifestEntry> _hlod2ByBlock = new();
        readonly Dictionary<long, SpatialSupertileManifestEntry> _hlod4ByBlock = new();
        readonly Dictionary<string, Dictionary<string, SpatialSubcellManifestEntry>> _subcellByTile = new(StringComparer.Ordinal);
        readonly Dictionary<string, string> _tileToHlod2Id = new(StringComparer.Ordinal);
        readonly Dictionary<string, string> _tileToHlod4Id = new(StringComparer.Ordinal);
        readonly List<SpatialTileManifestEntry> _ringWindowScratch = new(256);
        readonly List<SpatialSupertileManifestEntry> _supertileWindowScratch = new(64);

        public GridOrigin Origin { get; private set; }
        public int TileCount => _tileById.Count;
        public int SupertileCount => _supertileById.Count;

        public static long PackGrid(int gridX, int gridZ) =>
            ((long)gridX << 32) | (uint)gridZ;

        public static long PackBlockOrigin(int blockLeft, int blockBottom) =>
            SpatialStreamingLodSubstitution.PackBlockOrigin(blockLeft, blockBottom);

        public static SpatialDatasetRuntimeIndex Build(SpatialDatasetManifest manifest)
        {
            var index = new SpatialDatasetRuntimeIndex();
            if (manifest == null)
                return index;

            index.BuildFromManifest(manifest);
            return index;
        }

        void BuildFromManifest(SpatialDatasetManifest manifest)
        {
            int tileSize = manifest.TileSizeMeters > 0 ? manifest.TileSizeMeters : 1000;
            Origin = new GridOrigin
            {
                TileSizeMeters = tileSize,
                SubcellSizeMeters = manifest.SubcellSizeMeters > 0 ? manifest.SubcellSizeMeters : 250,
                HasBounds = false,
            };

            if (manifest.Tiles != null)
            {
                foreach (SpatialTileManifestEntry tile in manifest.Tiles)
                {
                    if (tile == null || string.IsNullOrEmpty(tile.TileId))
                        continue;

                    _tileById[tile.TileId] = tile;
                    if (!SpatialTileIdUtility.TryParse(tile.TileId, out int left, out int bottom))
                        continue;

                    SpatialTileIdUtility.ToGridIndices(left, bottom, tileSize, out int gridX, out int gridZ);
                    _tileByGrid[PackGrid(gridX, gridZ)] = tile;
                    ExpandBounds(gridX, gridZ);

                    if (tile.Subcells != null && tile.Subcells.Count > 0)
                    {
                        var subcellMap = new Dictionary<string, SpatialSubcellManifestEntry>(tile.Subcells.Count, StringComparer.Ordinal);
                        foreach (SpatialSubcellManifestEntry subcell in tile.Subcells)
                        {
                            if (subcell == null || string.IsNullOrEmpty(subcell.SubcellId))
                                continue;

                            subcellMap[subcell.SubcellId] = subcell;
                        }

                        _subcellByTile[tile.TileId] = subcellMap;
                    }
                }
            }

            if (manifest.Tiles != null && manifest.Tiles.Count > 0 && manifest.Tiles[0] != null &&
                SpatialTileIdUtility.TryParse(manifest.Tiles[0].TileId, out int refLeft, out int refBottom))
            {
                GridOrigin origin = Origin;
                origin.UnityOrigin = manifest.Tiles[0].GetUnityPosition();
                origin.RefLeft = refLeft;
                origin.RefBottom = refBottom;
                origin.HasReferenceTile = true;
                Origin = origin;
            }

            if (manifest.Supertiles != null)
            {
                foreach (SpatialSupertileManifestEntry supertile in manifest.Supertiles)
                {
                    if (supertile == null || string.IsNullOrEmpty(supertile.SupertileId))
                        continue;

                    _supertileById[supertile.SupertileId] = supertile;
                    long packed = PackBlockOrigin(supertile.Left, supertile.Bottom);
                    if (supertile.Factor >= 4)
                        _hlod4ByBlock[packed] = supertile;
                    else
                        _hlod2ByBlock[packed] = supertile;

                    if (supertile.ChildTileIds == null)
                        continue;

                    foreach (string childTileId in supertile.ChildTileIds)
                    {
                        if (string.IsNullOrEmpty(childTileId))
                            continue;

                        if (supertile.Factor >= 4)
                            _tileToHlod4Id[childTileId] = supertile.SupertileId;
                        else
                            _tileToHlod2Id[childTileId] = supertile.SupertileId;
                    }
                }
            }
        }

        void ExpandBounds(int gridX, int gridZ)
        {
            GridOrigin origin = Origin;
            if (!origin.HasBounds)
            {
                origin.MinGridX = origin.MaxGridX = gridX;
                origin.MinGridZ = origin.MaxGridZ = gridZ;
                origin.HasBounds = true;
                Origin = origin;
                return;
            }

            if (gridX < origin.MinGridX) origin.MinGridX = gridX;
            if (gridX > origin.MaxGridX) origin.MaxGridX = gridX;
            if (gridZ < origin.MinGridZ) origin.MinGridZ = gridZ;
            if (gridZ > origin.MaxGridZ) origin.MaxGridZ = gridZ;
            Origin = origin;
        }

        public bool TryGetTile(string tileId, out SpatialTileManifestEntry tile) =>
            _tileById.TryGetValue(tileId, out tile);

        public bool TryGetTileAtGrid(int gridX, int gridZ, out SpatialTileManifestEntry tile) =>
            _tileByGrid.TryGetValue(PackGrid(gridX, gridZ), out tile);

        public bool TryGetSupertile(string supertileId, out SpatialSupertileManifestEntry supertile) =>
            _supertileById.TryGetValue(supertileId, out supertile);

        public bool TryGetSupertileAtBlock(int blockLeft, int blockBottom, int factor, out SpatialSupertileManifestEntry supertile)
        {
            supertile = null;
            long packed = PackBlockOrigin(blockLeft, blockBottom);
            if (factor >= 4)
                return _hlod4ByBlock.TryGetValue(packed, out supertile);

            return _hlod2ByBlock.TryGetValue(packed, out supertile);
        }

        public bool TryGetSubcell(string tileId, string subcellId, out SpatialSubcellManifestEntry subcell)
        {
            subcell = null;
            return !string.IsNullOrEmpty(tileId) &&
                   !string.IsNullOrEmpty(subcellId) &&
                   _subcellByTile.TryGetValue(tileId, out Dictionary<string, SpatialSubcellManifestEntry> map) &&
                   map.TryGetValue(subcellId, out subcell);
        }

        public bool TryGetParentHlod2Id(string tileId, out string supertileId) =>
            _tileToHlod2Id.TryGetValue(tileId, out supertileId);

        public bool TryGetParentHlod4Id(string tileId, out string supertileId) =>
            _tileToHlod4Id.TryGetValue(tileId, out supertileId);

        public IEnumerable<SpatialTileManifestEntry> EnumerateAllTiles()
        {
            foreach (SpatialTileManifestEntry tile in _tileById.Values)
                yield return tile;
        }

        public IEnumerable<SpatialSupertileManifestEntry> EnumerateAllSupertiles()
        {
            foreach (SpatialSupertileManifestEntry supertile in _supertileById.Values)
                yield return supertile;
        }

        public IEnumerable<KeyValuePair<long, SpatialSupertileManifestEntry>> EnumerateHlod4Blocks()
        {
            foreach (KeyValuePair<long, SpatialSupertileManifestEntry> kvp in _hlod4ByBlock)
                yield return kvp;
        }

        public IEnumerable<KeyValuePair<long, SpatialSupertileManifestEntry>> EnumerateHlod2Blocks()
        {
            foreach (KeyValuePair<long, SpatialSupertileManifestEntry> kvp in _hlod2ByBlock)
                yield return kvp;
        }

        public bool TryResolveCameraTileGrid(Vector3 cameraWorldPos, out SpatialStreamingTileRingUtility.CameraTileGrid cameraTile)
        {
            cameraTile = default;
            if (!Origin.HasBounds || Origin.TileSizeMeters <= 0 || !Origin.HasReferenceTile)
                return false;

            int tileSize = Origin.TileSizeMeters;
            int offsetX = Mathf.FloorToInt((cameraWorldPos.x - Origin.UnityOrigin.x) / tileSize);
            int offsetZ = Mathf.FloorToInt((cameraWorldPos.z - Origin.UnityOrigin.z) / tileSize);
            int leftMeters = Origin.RefLeft + offsetX * tileSize;
            int bottomMeters = Origin.RefBottom + offsetZ * tileSize;
            SpatialTileIdUtility.ToGridIndices(leftMeters, bottomMeters, tileSize, out int gridX, out int gridZ);

            if (TryGetTileAtGrid(gridX, gridZ, out SpatialTileManifestEntry tile) &&
                SpatialTileIdUtility.TryParse(tile.TileId, out int left, out int bottom))
            {
                SpatialTileIdUtility.ToGridIndices(left, bottom, tileSize, out gridX, out gridZ);
                leftMeters = left;
                bottomMeters = bottom;
            }

            cameraTile = new SpatialStreamingTileRingUtility.CameraTileGrid
            {
                Left = leftMeters,
                Bottom = bottomMeters,
                GridX = gridX,
                GridZ = gridZ,
            };
            return true;
        }

        public IReadOnlyList<SpatialTileManifestEntry> CollectTilesInRing(
            in SpatialStreamingTileRingUtility.CameraTileGrid cameraTile,
            int maxTileRing)
        {
            _ringWindowScratch.Clear();
            if (!Origin.HasBounds)
                return _ringWindowScratch;

            for (int dz = -maxTileRing; dz <= maxTileRing; dz++)
            {
                for (int dx = -maxTileRing; dx <= maxTileRing; dx++)
                {
                    int gridX = cameraTile.GridX + dx;
                    int gridZ = cameraTile.GridZ + dz;
                    if (!_tileByGrid.TryGetValue(PackGrid(gridX, gridZ), out SpatialTileManifestEntry tile) || tile == null)
                        continue;

                    int left = cameraTile.Left + dx * Origin.TileSizeMeters;
                    int bottom = cameraTile.Bottom + dz * Origin.TileSizeMeters;
                    int tileRing = SpatialStreamingTileRingUtility.ChebyshevTileRing(
                        left, bottom, cameraTile, Origin.TileSizeMeters);
                    if (tileRing > maxTileRing)
                        continue;

                    _ringWindowScratch.Add(tile);
                }
            }

            return _ringWindowScratch;
        }

        public IReadOnlyList<SpatialSupertileManifestEntry> CollectSupertilesInRing(
            in SpatialStreamingTileRingUtility.CameraTileGrid cameraTile,
            SpatialStreamingTileRings rings,
            int tileSizeMeters)
        {
            _supertileWindowScratch.Clear();
            if (rings.hlod2x2Rings > 0)
                CollectSupertilesForFactor(cameraTile, rings, tileSizeMeters, factor: 2);

            if (rings.hlod4x4Rings > 0)
                CollectSupertilesForFactor(cameraTile, rings, tileSizeMeters, factor: 4);

            return _supertileWindowScratch;
        }

        void CollectSupertilesForFactor(
            in SpatialStreamingTileRingUtility.CameraTileGrid cameraTile,
            SpatialStreamingTileRings rings,
            int tileSizeMeters,
            int factor)
        {
            int blockSize = tileSizeMeters * factor;
            int maxBlockRing = SpatialStreamingTileRingUtility.MaxSupertileBlockScanRing(rings, factor);
            int camBlockLeft = SpatialTileIdUtility.AlignDownMeters(cameraTile.Left, blockSize);
            int camBlockBottom = SpatialTileIdUtility.AlignDownMeters(cameraTile.Bottom, blockSize);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int dz = -maxBlockRing; dz <= maxBlockRing; dz++)
            {
                for (int dx = -maxBlockRing; dx <= maxBlockRing; dx++)
                {
                    int blockLeft = camBlockLeft + dx * blockSize;
                    int blockBottom = camBlockBottom + dz * blockSize;
                    if (!SpatialStreamingTileRingUtility.ShouldWantSupertile(
                            rings, blockLeft, blockBottom, cameraTile, tileSizeMeters, factor) &&
                        !SpatialStreamingTileRingUtility.ShouldQueueSupertile(
                            rings, blockLeft, blockBottom, cameraTile, tileSizeMeters, factor))
                    {
                        continue;
                    }

                    if (!TryGetSupertileAtBlock(blockLeft, blockBottom, factor, out SpatialSupertileManifestEntry supertile) ||
                        supertile == null ||
                        !seen.Add(supertile.SupertileId))
                    {
                        continue;
                    }

                    _supertileWindowScratch.Add(supertile);
                }
            }
        }
    }

    /// <summary>
    /// Lightweight baked hierarchy: tile → parent HLOD2/HLOD4 blocks and child tile lists.
    /// </summary>
    public sealed class SpatialStreamingHlodDag
    {
        readonly Dictionary<string, string> _tileToHlod2 = new(StringComparer.Ordinal);
        readonly Dictionary<string, string> _tileToHlod4 = new(StringComparer.Ordinal);
        readonly Dictionary<string, List<string>> _hlod2Children = new(StringComparer.Ordinal);
        readonly Dictionary<string, List<string>> _hlod4Children = new(StringComparer.Ordinal);

        public static SpatialStreamingHlodDag Build(SpatialDatasetRuntimeIndex runtimeIndex, SpatialDatasetManifest manifest)
        {
            var dag = new SpatialStreamingHlodDag();
            if (manifest?.Supertiles == null)
                return dag;

            foreach (SpatialSupertileManifestEntry supertile in manifest.Supertiles)
            {
                if (supertile == null || string.IsNullOrEmpty(supertile.SupertileId))
                    continue;

                var children = supertile.ChildTileIds ?? new List<string>();
                if (supertile.Factor >= 4)
                {
                    dag._hlod4Children[supertile.SupertileId] = new List<string>(children);
                    foreach (string tileId in children)
                    {
                        if (!string.IsNullOrEmpty(tileId))
                            dag._tileToHlod4[tileId] = supertile.SupertileId;
                    }
                }
                else
                {
                    dag._hlod2Children[supertile.SupertileId] = new List<string>(children);
                    foreach (string tileId in children)
                    {
                        if (!string.IsNullOrEmpty(tileId))
                            dag._tileToHlod2[tileId] = supertile.SupertileId;
                    }
                }
            }

            return dag;
        }

        public bool TryGetParentHlod2(string tileId, out string supertileId) =>
            _tileToHlod2.TryGetValue(tileId, out supertileId);

        public bool TryGetParentHlod4(string tileId, out string supertileId) =>
            _tileToHlod4.TryGetValue(tileId, out supertileId);

        public IReadOnlyList<string> GetHlod2Children(string supertileId) =>
            _hlod2Children.TryGetValue(supertileId, out List<string> children) ? children : Array.Empty<string>();

        public IReadOnlyList<string> GetHlod4Children(string supertileId) =>
            _hlod4Children.TryGetValue(supertileId, out List<string> children) ? children : Array.Empty<string>();
    }
}
