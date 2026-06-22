using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// Four-step detail substitution pipeline:
    /// 1. Plan which detail slots in range need non-detail coverage.
    /// 2. Queue and show coarse blocks (HLOD / proxy) while detail is missing.
    /// 3. Gate detail loads until every required coarse block in the zone is shown.
    /// 4. On detail commit the controller shows detail and unloads coarse (see OnDetailCommitted).
    /// </summary>
    public static class SpatialStreamingCoveragePlanner
    {
        public struct DetailSlot
        {
            public string DetailKey;
            public string TileId;
            public string SubcellId;
            public string DetailBundleRel;
            public string CoarseKey;
            public SpatialStreamingLodLevel CoarseLod;
            public int GridX;
            public int GridY;
            public float Distance;
        }

        /// <summary>Step 1 — record a detail slot that still needs coarse coverage before detail may load.</summary>
        public static void PlanDetailSlot(
            SpatialStreamingLodSubstitution.Context ctx,
            SpatialTileManifestEntry tile,
            SpatialSubcellManifestEntry subcell,
            int tileLeft,
            int tileBottom,
            int tileRing,
            bool usesSubcellProxies,
            List<DetailSlot> into)
        {
            if (into == null || tile == null || subcell == null)
                return;

            if (string.IsNullOrEmpty(subcell.BundleRel))
                return;

            if (!SpatialStreamingLodSubstitution.ShouldQueueDetail(
                    ctx, tileLeft, tileBottom, subcell.GridX, subcell.GridY))
                return;

            if (SpatialStreamingLodSubstitution.IsDetailLoaded(ctx, tile.TileId, subcell.SubcellId))
                return;

            TryResolveCoarseCoverage(ctx, tile, subcell, usesSubcellProxies, out string coarseKey, out SpatialStreamingLodLevel coarseLod);

            into.Add(new DetailSlot
            {
                DetailKey = SpatialStreamingHlodEvaluator.BuildDetailKey(tile.TileId, subcell.SubcellId),
                TileId = tile.TileId,
                SubcellId = subcell.SubcellId,
                DetailBundleRel = subcell.BundleRel,
                CoarseKey = coarseKey,
                CoarseLod = coarseLod,
                GridX = subcell.GridX,
                GridY = subcell.GridY,
                Distance = tileRing,
            });
        }

        /// <summary>Step 3 — true when every planned slot has its coarse block loaded and visible.</summary>
        public static bool IsDetailZoneCoarseReady(
            SpatialStreamingLodSubstitution.Context ctx,
            IReadOnlyList<DetailSlot> plannedSlots)
        {
            if (!ctx.EnableHlod || plannedSlots == null || plannedSlots.Count == 0)
                return true;

            foreach (DetailSlot slot in plannedSlots)
            {
                if (!IsCoarseCoverageReadyForSlot(ctx, slot))
                    return false;
            }

            return true;
        }

        /// <summary>Per-slot gate — detail may load once this slot's coarse placeholder is committed.</summary>
        public static bool IsCoarseCoverageReadyForSlot(
            SpatialStreamingLodSubstitution.Context ctx,
            DetailSlot slot)
        {
            if (SpatialStreamingLodSubstitution.IsDetailLoaded(ctx, slot.TileId, slot.SubcellId))
                return true;

            return IsCoarseCoverageReady(ctx, slot.CoarseKey);
        }

        /// <summary>Step 3 (per slot) — detail may enter the pending queue for this slot.</summary>
        public static bool CanQueueDetail(
            SpatialStreamingLodSubstitution.Context ctx,
            DetailSlot slot)
        {
            if (SpatialStreamingLodSubstitution.IsDetailLoaded(ctx, slot.TileId, slot.SubcellId))
                return false;

            if (!ctx.EnableHlod)
                return true;

            return IsCoarseCoverageReady(ctx, slot.CoarseKey);
        }

        public static bool IsCoarseCoverageReady(
            SpatialStreamingLodSubstitution.Context ctx,
            string coarseKey)
        {
            if (string.IsNullOrEmpty(coarseKey))
                return true;

            if (ctx.LoadedKeys != null && ctx.LoadedKeys.Contains(coarseKey))
                return true;

            return ctx.LoadingKeys != null && ctx.LoadingKeys.Contains(coarseKey);
        }

        public static bool TryResolveCoarseCoverage(
            SpatialStreamingLodSubstitution.Context ctx,
            SpatialTileManifestEntry tile,
            SpatialSubcellManifestEntry subcell,
            bool usesSubcellProxies,
            out string coarseKey,
            out SpatialStreamingLodLevel coarseLod)
        {
            coarseKey = null;
            coarseLod = default;

            if (!ctx.EnableHlod || tile == null || !ctx.Rings.UsesCoarseLodChain)
                return false;

            if (!SpatialTileIdUtility.TryParse(tile.TileId, out int tileLeft, out int tileBottom))
                return false;

            int tileSize = ctx.Manifest?.TileSizeMeters ?? 1000;
            int subcellSize = ctx.Manifest?.SubcellSizeMeters > 0 ? ctx.Manifest.SubcellSizeMeters : 250;

            if (usesSubcellProxies &&
                subcell != null &&
                !string.IsNullOrEmpty(subcell.ProxyBundleRel) &&
                ctx.HasCameraSubcell &&
                SpatialStreamingHlodHandoff.ShouldWantDetailForSubcell(
                    ctx.Rings,
                    SpatialStreamingHlodHandoff.GetSubcellRing(
                        tileLeft,
                        tileBottom,
                        subcell.GridX,
                        subcell.GridY,
                        ctx.CameraSubcell,
                        tileSize,
                        subcellSize),
                    forWant: false))
            {
                coarseKey = SpatialStreamingHlodEvaluator.BuildSubcellProxyKey(tile.TileId, subcell.SubcellId);
                coarseLod = SpatialStreamingLodLevel.SubcellProxy;
                return true;
            }

            if (usesSubcellProxies &&
                subcell != null &&
                !string.IsNullOrEmpty(subcell.ProxyBundleRel) &&
                ctx.Rings.subcellProxyRings > 0 &&
                ctx.HasCameraSubcell &&
                SpatialStreamingHlodHandoff.TileWantsFullSubcellProxySet(
                    ctx.Rings,
                    tile,
                    tileLeft,
                    tileBottom,
                    ctx.CameraTile,
                    ctx.CameraSubcell,
                    tileSize,
                    subcellSize,
                    forWant: false))
            {
                coarseKey = SpatialStreamingHlodEvaluator.BuildSubcellProxyKey(tile.TileId, subcell.SubcellId);
                coarseLod = SpatialStreamingLodLevel.SubcellProxy;
                return true;
            }

            if (!usesSubcellProxies &&
                !string.IsNullOrEmpty(tile.ProxyBundleRel) &&
                ctx.Rings.tileProxyRings > 0 &&
                SpatialStreamingHlodHandoff.TileInExpandedTileProxyCoverage(
                    ctx.Rings,
                    tileLeft,
                    tileBottom,
                    ctx.CameraTile,
                    tileSize,
                    forWant: false,
                    SpatialStreamingHlodHandoff.CreateDetailBandProbe(
                        ctx.Rings, tile, ctx.CameraSubcell, tileSize, subcellSize, forWant: false)))
            {
                coarseKey = SpatialStreamingHlodEvaluator.BuildProxyKey(tile.TileId);
                coarseLod = SpatialStreamingLodLevel.TileProxy;
                return true;
            }

            return false;
        }
    }
}
