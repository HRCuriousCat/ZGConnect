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

            int tileSize = ctx.Manifest?.TileSizeMeters ?? 1000;
            if (!SpatialTileIdUtility.TryParse(tileId, out int tileLeft, out int tileBottom))
                return false;

            SpatialTileManifestEntry tile = ctx.Manifest.FindTile(tileId);
            int subcellSize = ctx.Manifest?.SubcellSizeMeters > 0 ? ctx.Manifest.SubcellSizeMeters : 250;
            if (!ctx.HasCameraSubcell ||
                !SpatialStreamingHlodHandoff.TileInExpandedTileProxyCoverage(
                    ctx.Rings,
                    tileLeft,
                    tileBottom,
                    ctx.CameraTile,
                    tileSize,
                    subcellSize,
                    forWant: true,
                    SpatialStreamingHlodHandoff.CreateDetailBandProbe(
                        ctx.Rings, tile, ctx.CameraSubcell, tileSize, subcellSize, forWant: true)))
            {
                return false;
            }

            if (tile == null)
                return true;

            if (!tile.UsesSubcells)
                return true;

            return !TileSubcellLayerActivelyCovering(ctx, tile);
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

            int tileSize = ctx.Manifest?.TileSizeMeters ?? 1000;
            if (!SpatialTileIdUtility.TryParse(tileId, out int tileLeft, out int tileBottom))
                return false;

            SpatialTileManifestEntry tile = ctx.Manifest.FindTile(tileId);
            int subcellSize = ctx.Manifest?.SubcellSizeMeters > 0 ? ctx.Manifest.SubcellSizeMeters : 250;
            if (!ctx.HasCameraSubcell ||
                !SpatialStreamingHlodHandoff.TileWantsFullSubcellProxySet(
                    ctx.Rings,
                    tile,
                    tileLeft,
                    tileBottom,
                    ctx.CameraTile,
                    ctx.CameraSubcell,
                    tileSize,
                    subcellSize,
                    forWant: true))
            {
                return false;
            }

            return HasLoadedSubcellProxy(ctx, tileId, subcellId);
        }

        /// <summary>
        /// HLOD2 is a monolithic 2×2 km block. Finer 1 km draws stay suppressed until the full block
        /// handoff completes when any child tile triggered an expanded fine-tier load.
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

            SpatialStreamingTileRingUtility.AlignHlod2BlockOrigin(left, bottom, tileSize, out int block2Left, out int block2Bottom);

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

            return ShouldRenderHlod2(ctx, hlod2);
        }

        static bool TileRequiresFineOneKmSubstitution(
            SpatialStreamingLodSubstitution.Context ctx,
            string tileId,
            int tileLeft,
            int tileBottom)
        {
            if (!ctx.Rings.UsesCoarseLodChain)
                return false;

            SpatialTileManifestEntry tile = ctx.Manifest?.FindTile(tileId);
            int tileSize = ctx.Manifest?.TileSizeMeters ?? 1000;
            int subcellSize = ctx.Manifest?.SubcellSizeMeters > 0 ? ctx.Manifest.SubcellSizeMeters : 250;
            var detailProbe = ctx.HasCameraSubcell
                ? SpatialStreamingHlodHandoff.CreateDetailBandProbe(
                    ctx.Rings,
                    tile,
                    ctx.CameraSubcell,
                    tileSize,
                    subcellSize,
                    forWant: true)
                : null;

            int tileRing = SpatialStreamingLodSubstitution.GetTileRing(ctx, tileId);
            bool anyDetail = detailProbe != null && detailProbe(tileLeft, tileBottom);
            return SpatialStreamingHlodHandoff.TileDirectlyTriggersOneKmLayer(
                ctx.Rings, tileRing, tileSize, subcellSize, anyDetail);
        }

        static System.Func<int, int, bool> DetailBandProbeForBlock(
            SpatialStreamingLodSubstitution.Context ctx,
            int block2Left,
            int block2Bottom,
            int tileSizeMeters)
        {
            if (!ctx.HasCameraSubcell)
                return null;

            int subcellSize = ctx.Manifest?.SubcellSizeMeters > 0 ? ctx.Manifest.SubcellSizeMeters : 250;
            return (left, bottom) =>
            {
                string tileId = SpatialTileIdUtility.Format(left, bottom);
                SpatialTileManifestEntry tile = ctx.Manifest?.FindTile(tileId);
                return tile != null &&
                       SpatialStreamingHlodHandoff.TileHasAnySubcellInDetailBand(
                           ctx.Rings,
                           tile,
                           left,
                           bottom,
                           ctx.CameraSubcell,
                           tileSizeMeters,
                           subcellSize,
                           forWant: true);
            };
        }

        static bool TileOneKmLayerActivelyCovering(
            SpatialStreamingLodSubstitution.Context ctx,
            string tileId)
        {
            if (!TileOneKmLayerReadyStrict(ctx, tileId))
                return false;

            SpatialTileManifestEntry tile = ctx.Manifest?.FindTile(tileId);
            if (tile != null && tile.UsesSubcells)
                return TileSubcellLayerActivelyCovering(ctx, tile);

            return HasVisibleFinerGeometryOnTile(ctx, tileId);
        }

        static bool HasVisibleFinerGeometryOnTile(
            SpatialStreamingLodSubstitution.Context ctx,
            string tileId)
        {
            if (ctx.LoadedRecords == null || string.IsNullOrEmpty(tileId))
                return false;

            foreach (SpatialLoadedSubcellRecord record in ctx.LoadedRecords.Values)
            {
                if (record?.Root == null || !record.Root.activeSelf)
                    continue;

                if (!string.Equals(record.TileId, tileId, System.StringComparison.Ordinal))
                    continue;

                if (record.LodLevel is SpatialStreamingLodLevel.Detail or
                    SpatialStreamingLodLevel.SubcellProxy or
                    SpatialStreamingLodLevel.TileProxy)
                {
                    return true;
                }
            }

            return false;
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
            int horizon = SpatialStreamingHlodHandoff.FurthestTileRingHorizon(
                ctx.Rings, tileSizeMeters, ctx.Manifest?.SubcellSizeMeters > 0 ? ctx.Manifest.SubcellSizeMeters : 250);
            bool monolithicHlod2 = SpatialStreamingHlodHandoff.Hlod4BlockRequiresMonolithicHlod2Handoff(
                ctx.Rings, block4Left, block4Bottom, ctx.CameraTile, tileSizeMeters);
            bool anyParticipatingChild = false;
            for (int dy = 0; dy < 2; dy++)
            {
                for (int dx = 0; dx < 2; dx++)
                {
                    int childLeft = block4Left + dx * block2Size;
                    int childBottom = block4Bottom + dy * block2Size;
                    if (!ChildBlockHasTileWithinHorizon(ctx, childLeft, childBottom, tileSizeMeters, horizon))
                        continue;

                    anyParticipatingChild = true;
                    bool childNeedsHlod2 = monolithicHlod2 ||
                        SpatialStreamingHlodHandoff.Block2DirectlyInHlod2Coverage(
                            ctx.Rings, childLeft, childBottom, ctx.CameraTile, tileSizeMeters, forWant: true);
                    if (!childNeedsHlod2)
                        continue;

                    if (!Hlod2BlockReadyToReplaceHlod4(ctx, childLeft, childBottom, tileSizeMeters, forceRequiresHlod2: monolithicHlod2))
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

            int horizon = SpatialStreamingHlodHandoff.FurthestTileRingHorizon(
                ctx.Rings, tileSizeMeters, ctx.Manifest?.SubcellSizeMeters > 0 ? ctx.Manifest.SubcellSizeMeters : 250);
            int subcellSize = ctx.Manifest?.SubcellSizeMeters > 0 ? ctx.Manifest.SubcellSizeMeters : 250;
            var detailProbe = DetailBandProbeForBlock(ctx, block2Left, block2Bottom, tileSizeMeters);
            bool monolithicHandoff = SpatialStreamingHlodHandoff.Hlod2BlockRequiresMonolithicOneKmHandoff(
                ctx.Rings, block2Left, block2Bottom, ctx.CameraTile, tileSizeMeters, subcellSize, detailProbe);
            bool anyInScope = false;
            for (int dy = 0; dy < 2; dy++)
            {
                for (int dx = 0; dx < 2; dx++)
                {
                    int tileLeft = block2Left + dx * tileSizeMeters;
                    int tileBottom = block2Bottom + dy * tileSizeMeters;
                    string tileId = SpatialTileIdUtility.Format(tileLeft, tileBottom);
                    int tileRing = SpatialStreamingLodSubstitution.GetTileRing(ctx, tileId);
                    if (tileRing >= horizon)
                        continue;

                    anyInScope = true;

                    if (monolithicHandoff)
                    {
                        if (!TileOneKmLayerActivelyCovering(ctx, tileId))
                            return false;
                        continue;
                    }

                    if (TileRequiresFineOneKmSubstitution(ctx, tileId, tileLeft, tileBottom))
                    {
                        if (!TileOneKmLayerActivelyCovering(ctx, tileId))
                            return false;
                    }
                    else if (SpatialStreamingHlodHandoff.Block2DirectlyInHlod2Coverage(
                                 ctx.Rings, block2Left, block2Bottom, ctx.CameraTile, tileSizeMeters, forWant: true))
                    {
                        return false;
                    }
                }
            }

            return anyInScope;
        }

        static bool Hlod2BlockReadyToReplaceHlod4(
            SpatialStreamingLodSubstitution.Context ctx,
            int block2Left,
            int block2Bottom,
            int tileSizeMeters,
            bool forceRequiresHlod2 = false)
        {
            int horizon = SpatialStreamingHlodHandoff.FurthestTileRingHorizon(
                ctx.Rings, tileSizeMeters, ctx.Manifest?.SubcellSizeMeters > 0 ? ctx.Manifest.SubcellSizeMeters : 250);
            if (!forceRequiresHlod2 &&
                !ChildBlockNeedsHlod2Representation(ctx, block2Left, block2Bottom, tileSizeMeters, horizon))
            {
                return true;
            }

            if (Hlod2BlockFullyCoveredByOneKm(ctx, block2Left, block2Bottom, tileSizeMeters))
                return true;

            if (!TryGetLoadedSupertileAt(
                    ctx, block2Left, block2Bottom, SpatialStreamingLodLevel.Hlod2x2, tileSizeMeters, out SpatialLoadedSubcellRecord hlod2) ||
                hlod2?.Root == null ||
                !hlod2.Root.activeSelf)
            {
                return false;
            }

            return true;
        }

        static bool ChildBlockNeedsHlod2Representation(
            SpatialStreamingLodSubstitution.Context ctx,
            int blockLeft,
            int blockBottom,
            int tileSizeMeters,
            int horizon)
        {
            var detailProbe = DetailBandProbeForBlock(ctx, blockLeft, blockBottom, tileSizeMeters);
            int subcellSize = ctx.Manifest?.SubcellSizeMeters > 0 ? ctx.Manifest.SubcellSizeMeters : 250;
            if (SpatialStreamingHlodHandoff.Hlod2BlockRequiresMonolithicOneKmHandoff(
                    ctx.Rings, blockLeft, blockBottom, ctx.CameraTile, tileSizeMeters, subcellSize, detailProbe))
            {
                return ChildBlockHasTileWithinHorizon(ctx, blockLeft, blockBottom, tileSizeMeters, horizon) &&
                       !Hlod2BlockFullyCoveredByOneKm(ctx, blockLeft, blockBottom, tileSizeMeters);
            }

            for (int dy = 0; dy < 2; dy++)
            {
                for (int dx = 0; dx < 2; dx++)
                {
                    int tileLeft = blockLeft + dx * tileSizeMeters;
                    int tileBottom = blockBottom + dy * tileSizeMeters;
                    string tileId = SpatialTileIdUtility.Format(tileLeft, tileBottom);
                    int tileRing = SpatialStreamingLodSubstitution.GetTileRing(ctx, tileId);
                    if (tileRing >= horizon)
                        continue;

                    if (TileRequiresFineOneKmSubstitution(ctx, tileId, tileLeft, tileBottom) &&
                        !TileOneKmLayerActivelyCovering(ctx, tileId))
                    {
                        return true;
                    }

                    if (SpatialStreamingHlodHandoff.Block2DirectlyInHlod2Coverage(
                            ctx.Rings, blockLeft, blockBottom, ctx.CameraTile, tileSizeMeters, forWant: true))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        static bool ChildBlockHasTileWithinHorizon(
            SpatialStreamingLodSubstitution.Context ctx,
            int blockLeft,
            int blockBottom,
            int tileSizeMeters,
            int horizon)
        {
            for (int dy = 0; dy < 2; dy++)
            {
                for (int dx = 0; dx < 2; dx++)
                {
                    int tileLeft = blockLeft + dx * tileSizeMeters;
                    int tileBottom = blockBottom + dy * tileSizeMeters;
                    string tileId = SpatialTileIdUtility.Format(tileLeft, tileBottom);
                    if (SpatialStreamingLodSubstitution.GetTileRing(ctx, tileId) < horizon)
                        return true;
                }
            }

            return false;
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
            int subcellSize = ctx.Manifest?.SubcellSizeMeters > 0 ? ctx.Manifest.SubcellSizeMeters : 250;
            int tileRing = SpatialStreamingLodSubstitution.GetTileRing(ctx, tileId);
            if (!SpatialStreamingHlodHandoff.TileInTileProxyCoverage(
                    ctx.Rings, tileRing, tileSizeMeters, subcellSize, forWant: true))
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

            if (!SpatialStreamingHlodHandoff.TileInSubcellProxyCoverage(
                    ctx.Rings, tileRing, tileSizeMeters, subcellSize, forWant: true))
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

        /// <summary>
        /// True when every required subcell slot is visibly covered by detail or its proxy.
        /// </summary>
        public static bool TileSubcellLayerActivelyCovering(
            SpatialStreamingLodSubstitution.Context ctx,
            SpatialTileManifestEntry tile)
        {
            if (tile == null || !tile.UsesSubcells || tile.Subcells == null)
                return false;

            bool requiresAny = false;
            foreach (SpatialSubcellManifestEntry subcell in tile.Subcells)
            {
                if (subcell == null || string.IsNullOrEmpty(subcell.ProxyBundleRel))
                    continue;

                requiresAny = true;
                if (SpatialStreamingLodSubstitution.IsDetailLoaded(ctx, tile.TileId, subcell.SubcellId))
                {
                    if (!IsDetailRecordVisible(ctx, tile.TileId, subcell.SubcellId))
                        return false;

                    continue;
                }

                if (!IsSubcellProxyRecordVisible(ctx, tile.TileId, subcell.SubcellId))
                    return false;
            }

            return requiresAny;
        }

        static bool IsDetailRecordVisible(SpatialStreamingLodSubstitution.Context ctx, string tileId, string subcellId)
        {
            if (ctx.LoadedRecords == null)
                return false;

            string detailKey = SpatialStreamingHlodEvaluator.BuildDetailKey(tileId, subcellId);
            return ctx.LoadedRecords.TryGetValue(detailKey, out SpatialLoadedSubcellRecord record) &&
                   record?.Root != null &&
                   record.Root.activeSelf;
        }

        static bool IsSubcellProxyRecordVisible(SpatialStreamingLodSubstitution.Context ctx, string tileId, string subcellId)
        {
            if (ctx.LoadedRecords == null)
                return false;

            string proxyKey = SpatialStreamingHlodEvaluator.BuildSubcellProxyKey(tileId, subcellId);
            if (!ctx.LoadedRecords.TryGetValue(proxyKey, out SpatialLoadedSubcellRecord record) ||
                record?.Root == null ||
                !record.Root.activeSelf)
            {
                return false;
            }

            return ShouldRenderSubcellProxy(ctx, tileId, subcellId);
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
