using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// Cascade coverage: coarse LOD stays visible until the next finer layer fully covers its region.
    /// Once a child layer satisfies coverage, parent layers stay hidden (no reappearance when
    /// intermediate meshes are unloaded during progressive substitution).
    /// HLOD4 → HLOD2 (2×2 blocks) → 1×1 tile proxy → subcell proxies → detail (per subcell).
    /// </summary>
    public static class SpatialStreamingRingCoverage
    {
        public static bool ShouldRenderHlod4(
            SpatialStreamingLodSubstitution.Context ctx,
            SpatialLoadedSubcellRecord supertile)
        {
            if (!ctx.EnableHlod || supertile == null)
                return false;

            int tileSize = ctx.Manifest?.TileSizeMeters ?? 1000;
            int factor = supertile.HlodFactor >= 4 ? 4 : 2;
            if (!SpatialStreamingTileRingUtility.ShouldWantSupertile(
                    ctx.Rings,
                    supertile.BlockLeft,
                    supertile.BlockBottom,
                    ctx.CameraTile,
                    tileSize,
                    factor))
            {
                return false;
            }

            return !Hlod4BlockFullyCoveredByHlod2(ctx, supertile.BlockLeft, supertile.BlockBottom, tileSize);
        }

        public static bool ShouldRenderHlod2(
            SpatialStreamingLodSubstitution.Context ctx,
            SpatialLoadedSubcellRecord supertile)
        {
            if (!ctx.EnableHlod || supertile == null)
                return false;

            int tileSize = ctx.Manifest?.TileSizeMeters ?? 1000;
            if (!SpatialStreamingTileRingUtility.ShouldWantSupertile(
                    ctx.Rings,
                    supertile.BlockLeft,
                    supertile.BlockBottom,
                    ctx.CameraTile,
                    tileSize,
                    factor: 2))
            {
                return false;
            }

            return !Hlod2BlockFullyCoveredByOneKm(ctx, supertile.BlockLeft, supertile.BlockBottom, tileSize);
        }

        public static bool ShouldRenderTileProxy(
            SpatialStreamingLodSubstitution.Context ctx,
            string tileId)
        {
            if (!ctx.EnableHlod || !ctx.Rings.UsesCoarseLodChain || ctx.Manifest == null || string.IsNullOrEmpty(tileId))
                return false;

            if (IsParentHlod2StillRenderingForTile(ctx, tileId))
                return false;

            int tileRing = SpatialStreamingLodSubstitution.GetTileRing(ctx, tileId);
            if (!SpatialStreamingTileRingUtility.TileInTileProxyCoverage(ctx.Rings, tileRing, forWant: true))
                return false;

            SpatialTileManifestEntry tile = ctx.Manifest.FindTile(tileId);
            if (tile == null)
                return true;

            if (!tile.UsesSubcells)
                return true;

            return !TileSubcellLayerReady(ctx, tile);
        }

        public static bool ShouldRenderSubcellProxy(
            SpatialStreamingLodSubstitution.Context ctx,
            string tileId,
            string subcellId)
        {
            if (!ctx.EnableHlod || !ctx.Rings.UsesCoarseLodChain || ctx.Manifest == null)
                return false;

            if (SpatialStreamingLodSubstitution.IsDetailLoaded(ctx, tileId, subcellId))
                return false;

            if (IsParentHlod2StillRenderingForTile(ctx, tileId))
                return false;

            int tileRing = SpatialStreamingLodSubstitution.GetTileRing(ctx, tileId);
            if (!SpatialStreamingTileRingUtility.TileInSubcellProxyCoverage(ctx.Rings, tileRing, forWant: true) &&
                !SpatialStreamingTileRingUtility.TileInDetailCoverage(ctx.Rings, tileRing, forWant: true))
            {
                return false;
            }

            return HasLoadedSubcellProxy(ctx, tileId, subcellId);
        }

        /// <summary>
        /// HLOD2 is a monolithic 2×2 km block — finer 1 km layers must not draw until it hands off.
        /// </summary>
        public static bool IsParentHlod2StillRenderingForTile(
            SpatialStreamingLodSubstitution.Context ctx,
            string tileId)
        {
            if (!ctx.EnableHlod ||
                !ctx.Rings.UsesCoarseLodChain ||
                ctx.Rings.hlod2x2Rings <= 0 ||
                ctx.Manifest == null ||
                string.IsNullOrEmpty(tileId))
            {
                return false;
            }

            int tileSize = ctx.Manifest.TileSizeMeters > 0 ? ctx.Manifest.TileSizeMeters : 1000;
            if (!SpatialTileIdUtility.TryParse(tileId, out int left, out int bottom))
                return false;

            int block2Size = tileSize * 2;
            int block2Left = SpatialTileIdUtility.AlignDownMeters(left, block2Size);
            int block2Bottom = SpatialTileIdUtility.AlignDownMeters(bottom, block2Size);

            if (!TryGetLoadedSupertileAt(
                    ctx,
                    block2Left,
                    block2Bottom,
                    SpatialStreamingLodLevel.Hlod2x2,
                    tileSize,
                    out SpatialLoadedSubcellRecord hlod2))
            {
                return false;
            }

            if (!ShouldRenderHlod2(ctx, hlod2))
                return false;

            return true;
        }

        static bool TileRequiresFineOneKmSubstitution(SpatialStreamingLodSubstitution.Context ctx, int tileRing)
        {
            if (!ctx.Rings.UsesCoarseLodChain)
                return false;

            return SpatialStreamingTileRingUtility.TileInDetailCoverage(ctx.Rings, tileRing, forWant: true) ||
                   SpatialStreamingTileRingUtility.TileInSubcellProxyCoverage(ctx.Rings, tileRing, forWant: true) ||
                   SpatialStreamingTileRingUtility.TileInTileProxyCoverage(ctx.Rings, tileRing, forWant: true);
        }

        static bool TileOneKmLayerReadyStrict(
            SpatialStreamingLodSubstitution.Context ctx,
            string tileId)
        {
            SpatialTileManifestEntry tile = ctx.Manifest?.FindTile(tileId);
            if (tile == null)
                return false;

            if (!tile.UsesSubcells)
            {
                return HasLoadedTileProxy(ctx, tileId) ||
                       SpatialStreamingLodSubstitution.IsTileDetailCompletePublic(ctx, tileId);
            }

            return TileSubcellLayerReady(ctx, tile);
        }

        public static bool Hlod4BlockFullyCoveredByHlod2(
            SpatialStreamingLodSubstitution.Context ctx,
            int block4Left,
            int block4Bottom,
            int tileSizeMeters)
        {
            if (ctx.Manifest == null || tileSizeMeters <= 0)
                return false;

            int block2Size = tileSizeMeters * 2;
            bool anyParticipatingChild = false;
            for (int dy = 0; dy < 2; dy++)
            {
                for (int dx = 0; dx < 2; dx++)
                {
                    int childLeft = block4Left + dx * block2Size;
                    int childBottom = block4Bottom + dy * block2Size;
                    if (!SpatialStreamingTileRingUtility.SupertileInCoverage(
                            ctx.Rings,
                            childLeft,
                            childBottom,
                            ctx.CameraTile,
                            tileSizeMeters,
                            factor: 2,
                            forWant: false))
                    {
                        continue;
                    }

                    anyParticipatingChild = true;
                    if (!Hlod2BlockReadyToReplaceHlod4(ctx, childLeft, childBottom, tileSizeMeters))
                        return false;
                }
            }

            return anyParticipatingChild;
        }

        public static bool Hlod2BlockFullyCoveredByOneKm(
            SpatialStreamingLodSubstitution.Context ctx,
            int block2Left,
            int block2Bottom,
            int tileSizeMeters)
        {
            if (ctx.Manifest == null || tileSizeMeters <= 0)
                return false;

            bool anyParticipant = false;
            for (int dy = 0; dy < 2; dy++)
            {
                for (int dx = 0; dx < 2; dx++)
                {
                    int tileLeft = block2Left + dx * tileSizeMeters;
                    int tileBottom = block2Bottom + dy * tileSizeMeters;
                    string tileId = SpatialTileIdUtility.Format(tileLeft, tileBottom);
                    int tileRing = SpatialStreamingLodSubstitution.GetTileRing(ctx, tileId);
                    if (!TileRequiresFineOneKmSubstitution(ctx, tileRing))
                        continue;

                    anyParticipant = true;
                    if (!TileOneKmLayerReadyStrict(ctx, tileId))
                        return false;
                }
            }

            return anyParticipant;
        }

        static bool Hlod2BlockReadyToReplaceHlod4(
            SpatialStreamingLodSubstitution.Context ctx,
            int block2Left,
            int block2Bottom,
            int tileSizeMeters)
        {
            if (Hlod2BlockFullyCoveredByOneKm(ctx, block2Left, block2Bottom, tileSizeMeters))
                return true;

            if (!TryGetLoadedSupertileAt(
                    ctx, block2Left, block2Bottom, SpatialStreamingLodLevel.Hlod2x2, tileSizeMeters, out SpatialLoadedSubcellRecord hlod2) ||
                hlod2 == null)
            {
                return false;
            }

            return ShouldRenderHlod2(ctx, hlod2);
        }

        public static bool IsHlod2BlockReadyToReplaceHlod4(
            SpatialStreamingLodSubstitution.Context ctx,
            int block2Left,
            int block2Bottom,
            int tileSizeMeters) =>
            Hlod2BlockReadyToReplaceHlod4(ctx, block2Left, block2Bottom, tileSizeMeters);

        static bool TileOneKmLayerReady(
            SpatialStreamingLodSubstitution.Context ctx,
            string tileId,
            int tileSizeMeters)
        {
            int tileRing = SpatialStreamingLodSubstitution.GetTileRing(ctx, tileId);
            if (!SpatialStreamingTileRingUtility.TileInTileProxyCoverage(ctx.Rings, tileRing, forWant: true))
                return true;

            SpatialTileManifestEntry tile = ctx.Manifest?.FindTile(tileId);
            if (tile == null)
                return true;

            if (!tile.UsesSubcells)
            {
                return HasLoadedTileProxy(ctx, tileId) ||
                       SpatialStreamingLodSubstitution.IsTileDetailCompletePublic(ctx, tileId);
            }

            if (TileSubcellLayerReady(ctx, tile))
                return true;

            if (!SpatialStreamingTileRingUtility.TileInSubcellProxyCoverage(ctx.Rings, tileRing, forWant: true))
            {
                return HasLoadedTileProxy(ctx, tileId) ||
                       SpatialStreamingLodSubstitution.IsTileDetailCompletePublic(ctx, tileId);
            }

            return false;
        }

        public static bool IsTileOneKmLayerReady(
            SpatialStreamingLodSubstitution.Context ctx,
            string tileId,
            int tileSizeMeters) =>
            TileOneKmLayerReady(ctx, tileId, tileSizeMeters);

        public static bool TileSubcellLayerReady(
            SpatialStreamingLodSubstitution.Context ctx,
            SpatialTileManifestEntry tile)
        {
            if (tile == null)
                return false;

            if (ctx.LoadedState != null)
                return ctx.LoadedState.TileSubcellLayerReady(tile.TileId);

            if (!tile.UsesSubcells)
                return HasLoadedTileProxy(ctx, tile.TileId);

            return TileHasAllSubcellProxiesLoaded(ctx, tile) ||
                   SpatialStreamingLodSubstitution.IsTileDetailCompletePublic(ctx, tile.TileId);
        }

        public static bool TileHasAllSubcellProxiesLoaded(
            SpatialStreamingLodSubstitution.Context ctx,
            SpatialTileManifestEntry tile)
        {
            if (tile == null || tile.Subcells == null || ctx.Manifest == null)
                return false;

            bool requiresAny = false;
            foreach (SpatialSubcellManifestEntry subcell in tile.Subcells)
            {
                if (subcell == null || string.IsNullOrEmpty(subcell.ProxyBundleRel))
                    continue;

                if (SpatialStreamingLodSubstitution.IsDetailLoaded(ctx, tile.TileId, subcell.SubcellId))
                    continue;

                requiresAny = true;
                if (!HasLoadedSubcellProxy(ctx, tile.TileId, subcell.SubcellId))
                    return false;
            }

            return requiresAny;
        }

        static bool HasLoadedTileProxy(SpatialStreamingLodSubstitution.Context ctx, string tileId)
        {
            if (ctx.LoadedState != null)
                return ctx.LoadedState.IsTileProxyLoaded(tileId);

            return ctx.LoadedKeys != null &&
                   ctx.LoadedKeys.Contains(SpatialStreamingHlodEvaluator.BuildProxyKey(tileId));
        }

        static bool HasLoadedSubcellProxy(
            SpatialStreamingLodSubstitution.Context ctx,
            string tileId,
            string subcellId)
        {
            if (ctx.LoadedState != null)
                return ctx.LoadedState.IsSubcellProxyLoaded(tileId, subcellId);

            return ctx.LoadedKeys != null &&
                   ctx.LoadedKeys.Contains(SpatialStreamingHlodEvaluator.BuildSubcellProxyKey(tileId, subcellId));
        }

        static bool TryGetLoadedSupertileAt(
            SpatialStreamingLodSubstitution.Context ctx,
            int blockLeft,
            int blockBottom,
            SpatialStreamingLodLevel lod,
            int tileSizeMeters,
            out SpatialLoadedSubcellRecord record)
        {
            record = null;
            if (ctx.LoadedRecords == null)
                return false;

            IReadOnlyDictionary<long, SpatialLoadedSubcellRecord> index =
                lod == SpatialStreamingLodLevel.Hlod4x4 ? ctx.Hlod4ByBlock : ctx.Hlod2ByBlock;
            if (index == null && ctx.LoadedState != null)
                index = lod == SpatialStreamingLodLevel.Hlod4x4 ? ctx.LoadedState.Hlod4ByBlock : ctx.LoadedState.Hlod2ByBlock;
            if (index != null &&
                index.TryGetValue(SpatialStreamingLodSubstitution.PackBlockOrigin(blockLeft, blockBottom), out record))
            {
                return true;
            }

            int factor = lod == SpatialStreamingLodLevel.Hlod4x4 ? 4 : 2;
            int blockSpan = tileSizeMeters * factor;

            foreach (SpatialLoadedSubcellRecord candidate in ctx.LoadedRecords.Values)
            {
                if (candidate?.Root == null || candidate.LodLevel != lod)
                    continue;

                if (candidate.BlockLeft == blockLeft && candidate.BlockBottom == blockBottom)
                {
                    record = candidate;
                    return true;
                }

                bool insideBlock = blockLeft >= candidate.BlockLeft &&
                                   blockLeft + blockSpan <= candidate.BlockLeft + blockSpan &&
                                   blockBottom >= candidate.BlockBottom &&
                                   blockBottom + blockSpan <= candidate.BlockBottom + blockSpan;
                if (!insideBlock)
                    continue;

                record = candidate;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Incremental hierarchical coverage readiness for HLOD4 → HLOD2 → 1 km tile layers.
    /// </summary>
    public sealed class SpatialStreamingCoverageRefCounts
    {
        readonly Dictionary<long, int> _hlod2ReadyChildrenForHlod4 = new();
        readonly Dictionary<long, int> _tileReadyChildrenForHlod2 = new();
        readonly Dictionary<string, bool> _tileOneKmReady = new();
        readonly Dictionary<long, bool> _hlod2BlockReady = new();
        readonly Dictionary<long, bool> _hlod4BlockReady = new();
        readonly SpatialDatasetRuntimeIndex _runtimeIndex;
        readonly SpatialStreamingLoadedStateIndex _loadedState;
        readonly SpatialStreamingHlodDag _dag;
        readonly Dictionary<long, bool> _memoScratch = new();

        public SpatialStreamingCoverageRefCounts(
            SpatialDatasetRuntimeIndex runtimeIndex,
            SpatialStreamingLoadedStateIndex loadedState,
            SpatialStreamingHlodDag dag)
        {
            _runtimeIndex = runtimeIndex;
            _loadedState = loadedState;
            _dag = dag;
        }

        public void RebuildAll()
        {
            _hlod2ReadyChildrenForHlod4.Clear();
            _tileReadyChildrenForHlod2.Clear();
            _tileOneKmReady.Clear();
            _hlod2BlockReady.Clear();
            _hlod4BlockReady.Clear();
            _memoScratch.Clear();

            if (_runtimeIndex == null || _loadedState == null)
                return;

            foreach (KeyValuePair<long, SpatialLoadedSubcellRecord> kvp in _loadedState.Hlod4ByBlock)
                RefreshHlod4Block(kvp.Key);

            foreach (KeyValuePair<long, SpatialLoadedSubcellRecord> kvp in _loadedState.Hlod2ByBlock)
                RefreshHlod2Block(kvp.Key);
        }

        public void OnRecordLoaded(SpatialLoadedSubcellRecord record)
        {
            if (record == null)
                return;

            switch (record.LodLevel)
            {
                case SpatialStreamingLodLevel.Detail:
                case SpatialStreamingLodLevel.SubcellProxy:
                case SpatialStreamingLodLevel.TileProxy:
                    if (!string.IsNullOrEmpty(record.TileId))
                        RefreshTileOneKm(record.TileId);
                    break;
                case SpatialStreamingLodLevel.Hlod2x2:
                    RefreshHlod2Block(SpatialDatasetRuntimeIndex.PackBlockOrigin(record.BlockLeft, record.BlockBottom));
                    break;
                case SpatialStreamingLodLevel.Hlod4x4:
                    RefreshHlod4Block(SpatialDatasetRuntimeIndex.PackBlockOrigin(record.BlockLeft, record.BlockBottom));
                    break;
            }
        }

        public void OnRecordUnloaded(SpatialLoadedSubcellRecord record) => OnRecordLoaded(record);

        public bool IsHlod2BlockReady(SpatialStreamingLodSubstitution.Context ctx, long blockPacked)
        {
            if (ctx.Manifest == null)
                return false;

            int tileSize = ctx.Manifest.TileSizeMeters > 0 ? ctx.Manifest.TileSizeMeters : 1000;
            return SpatialStreamingRingCoverage.Hlod2BlockFullyCoveredByOneKm(
                ctx,
                UnpackLeft(blockPacked),
                UnpackBottom(blockPacked),
                tileSize);
        }

        public bool IsHlod4BlockReady(SpatialStreamingLodSubstitution.Context ctx, long blockPacked)
        {
            if (ctx.Manifest == null)
                return false;

            int tileSize = ctx.Manifest.TileSizeMeters > 0 ? ctx.Manifest.TileSizeMeters : 1000;
            return SpatialStreamingRingCoverage.Hlod4BlockFullyCoveredByHlod2(
                ctx,
                UnpackLeft(blockPacked),
                UnpackBottom(blockPacked),
                tileSize);
        }

        void RefreshTileOneKm(string tileId)
        {
            _tileOneKmReady.Remove(tileId);

            if (_runtimeIndex != null &&
                _runtimeIndex.TryGetParentHlod2Id(tileId, out string hlod2Id) &&
                _runtimeIndex.TryGetSupertile(hlod2Id, out SpatialSupertileManifestEntry hlod2))
            {
                _hlod2BlockReady.Remove(SpatialDatasetRuntimeIndex.PackBlockOrigin(hlod2.Left, hlod2.Bottom));
            }

            if (_runtimeIndex != null &&
                _runtimeIndex.TryGetParentHlod4Id(tileId, out string hlod4Id) &&
                _runtimeIndex.TryGetSupertile(hlod4Id, out SpatialSupertileManifestEntry hlod4))
            {
                _hlod4BlockReady.Remove(SpatialDatasetRuntimeIndex.PackBlockOrigin(hlod4.Left, hlod4.Bottom));
            }
        }

        void RefreshHlod2Block(long blockPacked)
        {
            _hlod2BlockReady.Remove(blockPacked);

            foreach (KeyValuePair<long, SpatialLoadedSubcellRecord> kvp in _loadedState.Hlod4ByBlock)
                _hlod4BlockReady.Remove(kvp.Key);
        }

        void RefreshHlod4Block(long blockPacked)
        {
            _hlod4BlockReady.Remove(blockPacked);
        }

        static int UnpackLeft(long packed) => (int)(packed >> 32);
        static int UnpackBottom(long packed) => (int)(packed & 0xFFFFFFFF);
    }
}
