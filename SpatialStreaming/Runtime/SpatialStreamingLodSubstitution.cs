using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// Single source of truth for HLOD / proxy / detail substitution rules.
    ///
    /// Cascade coverage (coarse → fine): HLOD4 → HLOD2 → tile proxy → subcell proxy → detail.
    /// Each coarser layer stays visible until the next layer fully covers its region (block or tile).
    /// Detail replaces its subcell proxy immediately when loaded.
    /// </summary>
    public static class SpatialStreamingLodSubstitution
    {
        /// <summary>Progressive substitution: at most this many detail loads per tile at once.</summary>
        public const int MaxConcurrentDetailLoadsPerTile = 1;

        public struct Context
        {
            public SpatialDatasetManifest Manifest;
            public SpatialDatasetRuntimeIndex RuntimeIndex;
            public SpatialStreamingLoadedStateIndex LoadedState;
            public SpatialStreamingCoverageRefCounts Coverage;
            public SpatialStreamingHlodDag Dag;
            public Vector3 CameraPosition;
            public SpatialStreamingTileRings Rings;
            public SpatialStreamingTileRingUtility.CameraTileGrid CameraTile;
            public SpatialStreamingHlodHandoff.CameraSubcellGrid CameraSubcell;
            public bool HasCameraSubcell;
            public bool EnableHlod;
            public HashSet<string> LoadedKeys;
            public HashSet<string> LoadingKeys;
            public IReadOnlyDictionary<string, SpatialLoadedSubcellRecord> LoadedRecords;
            public IReadOnlyDictionary<long, SpatialLoadedSubcellRecord> Hlod2ByBlock;
            public IReadOnlyDictionary<long, SpatialLoadedSubcellRecord> Hlod4ByBlock;
            public bool UseScreenSpaceLodPriority;
            public float ScreenSpaceFovDegrees;
            public SpatialStreamingEvaluateSliceState EvaluateSlice;
        }

        public static long PackBlockOrigin(int blockLeft, int blockBottom) =>
            ((long)blockLeft << 32) | (uint)blockBottom;

        public static int GetTileRing(Context ctx, string tileId)
        {
            if (ctx.Manifest == null ||
                !SpatialTileIdUtility.TryParse(tileId, out int left, out int bottom))
            {
                return int.MaxValue;
            }

            int tileSize = ctx.Manifest.TileSizeMeters > 0 ? ctx.Manifest.TileSizeMeters : 1000;
            return SpatialStreamingTileRingUtility.ChebyshevTileRing(left, bottom, ctx.CameraTile, tileSize);
        }

        public static bool IsDetailLoaded(Context ctx, string tileId, string subcellId)
        {
            if (string.IsNullOrEmpty(subcellId))
                return false;

            if (subcellId == "tile_coarse")
                return !string.IsNullOrEmpty(tileId) && IsTileDetailComplete(ctx, tileId);

            if (ctx.LoadedState != null)
                return ctx.LoadedState.IsDetailLoaded(tileId, subcellId);

            if (!string.IsNullOrEmpty(tileId) &&
                ctx.LoadedKeys != null &&
                ctx.LoadedKeys.Contains(SpatialStreamingHlodEvaluator.BuildDetailKey(tileId, subcellId)))
            {
                return true;
            }

            return HasDetailRecord(ctx, tileId, subcellId);
        }

        public static bool HasDetailRecord(Context ctx, string tileId, string subcellId)
        {
            if (ctx.LoadedRecords == null || string.IsNullOrEmpty(subcellId))
                return false;

            foreach (SpatialLoadedSubcellRecord record in ctx.LoadedRecords.Values)
            {
                if (record == null || record.LodLevel != SpatialStreamingLodLevel.Detail)
                    continue;

                if (!string.Equals(record.SubcellId, subcellId, System.StringComparison.Ordinal))
                    continue;

                if (!string.IsNullOrEmpty(tileId) &&
                    !string.Equals(record.TileId, tileId, System.StringComparison.Ordinal))
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        public static bool TryParseDetailKey(string key, out string tileId, out string subcellId)
        {
            tileId = null;
            subcellId = null;
            if (string.IsNullOrEmpty(key) ||
                key.EndsWith("|proxy", System.StringComparison.Ordinal) ||
                key.EndsWith("|hlod", System.StringComparison.Ordinal))
            {
                return false;
            }

            int split = key.IndexOf('|');
            if (split <= 0 || split >= key.Length - 1)
                return false;

            tileId = key.Substring(0, split);
            subcellId = key.Substring(split + 1);
            return !string.IsNullOrEmpty(tileId) && !string.IsNullOrEmpty(subcellId);
        }

        public static bool TryParseSubcellProxyKey(string key, out string tileId, out string subcellId)
        {
            tileId = null;
            subcellId = null;
            if (string.IsNullOrEmpty(key) || !key.EndsWith("|proxy", System.StringComparison.Ordinal))
                return false;

            string body = key.Substring(0, key.Length - "|proxy".Length);
            int split = body.IndexOf('|');
            if (split <= 0 || split >= body.Length - 1)
                return false;

            tileId = body.Substring(0, split);
            subcellId = body.Substring(split + 1);
            return !string.IsNullOrEmpty(tileId) && !string.IsNullOrEmpty(subcellId);
        }

        public static bool ShouldKeepSubcellProxy(Context ctx, string tileId, string subcellId)
        {
            if (!ctx.EnableHlod)
                return false;

            return !IsDetailLoaded(ctx, tileId, subcellId);
        }

        public static bool ShouldKeepSubcellProxyRecord(Context ctx, SpatialLoadedSubcellRecord record)
        {
            if (!ctx.EnableHlod || record == null)
                return false;

            string tileId = record.TileId;
            string subcellId = record.SubcellId;
            if (string.IsNullOrEmpty(tileId) || string.IsNullOrEmpty(subcellId))
                TryParseSubcellProxyKey(record.Key, out tileId, out subcellId);

            return ShouldKeepSubcellProxy(ctx, tileId, subcellId);
        }

        public static bool ShouldRenderSubcellProxyRecord(Context ctx, SpatialLoadedSubcellRecord record)
        {
            if (!ShouldKeepSubcellProxyRecord(ctx, record))
                return false;

            string tileId = record.TileId;
            string subcellId = record.SubcellId;
            if (string.IsNullOrEmpty(tileId) || string.IsNullOrEmpty(subcellId))
                TryParseSubcellProxyKey(record.Key, out tileId, out subcellId);

            return SpatialStreamingRingCoverage.ShouldRenderSubcellProxy(ctx, tileId, subcellId);
        }

        public static bool ShouldRenderTileProxy(Context ctx, string tileId)
        {
            if (!ShouldKeepTileProxy(ctx, tileId))
                return false;

            return SpatialStreamingRingCoverage.ShouldRenderTileProxy(ctx, tileId);
        }

        public static bool TileHasAnyDetailLoaded(Context ctx, string tileId)
        {
            if (ctx.LoadedState != null)
                return ctx.LoadedState.TileHasAnyDetailLoaded(tileId);

            return SpatialStreamingHlodEvaluator.TileHasAnyDetailLoaded(tileId, ctx.LoadedKeys);
        }

        public static bool ShouldKeepTileProxy(Context ctx, string tileId)
        {
            if (!ctx.EnableHlod)
                return false;

            SpatialTileManifestEntry tile = ctx.Manifest?.FindTile(tileId);
            if (tile == null)
                return !IsTileDetailComplete(ctx, tileId);

            if (tile.UsesSubcells)
                return !TileHasAnyDetailLoaded(ctx, tileId);

            return !IsTileDetailComplete(ctx, tileId);
        }

        public static bool ShouldRenderRecord(Context ctx, SpatialLoadedSubcellRecord record)
        {
            if (record?.Root == null)
                return false;

            switch (record.LodLevel)
            {
                case SpatialStreamingLodLevel.Detail:
                    return true;
                case SpatialStreamingLodLevel.SubcellProxy:
                    return ShouldRenderSubcellProxyRecord(ctx, record);
                case SpatialStreamingLodLevel.TileProxy:
                    return ShouldRenderTileProxy(ctx, record.TileId);
                case SpatialStreamingLodLevel.Hlod2x2:
                    return SpatialStreamingRingCoverage.ShouldRenderHlod2(ctx, record);
                case SpatialStreamingLodLevel.Hlod4x4:
                    return SpatialStreamingRingCoverage.ShouldRenderHlod4(ctx, record);
                default:
                    return true;
            }
        }

        public static bool ShouldCommitLoad(Context ctx, SpatialStreamingHlodEvaluator.LoadRequest request)
        {
            switch (request.LodLevel)
            {
                case SpatialStreamingLodLevel.Detail:
                    if (IsDetailLoaded(ctx, request.TileId, request.SubcellId))
                        return false;

                    return IsDetailCoarseReadyForLoad(ctx, request);
                case SpatialStreamingLodLevel.SubcellProxy:
                    return ShouldKeepSubcellProxyRecord(ctx, new SpatialLoadedSubcellRecord
                    {
                        Key = request.Key,
                        TileId = request.TileId,
                        SubcellId = request.SubcellId,
                        LodLevel = request.LodLevel,
                    });
                case SpatialStreamingLodLevel.TileProxy:
                    return ShouldKeepTileProxy(ctx, request.TileId);
                case SpatialStreamingLodLevel.Hlod2x2:
                case SpatialStreamingLodLevel.Hlod4x4:
                {
                    int tileSize = ctx.Manifest?.TileSizeMeters ?? 1000;
                    int factor = request.LodLevel == SpatialStreamingLodLevel.Hlod4x4 ? 4 : 2;
                    return SpatialStreamingTileRingUtility.ShouldWantSupertile(
                        ctx.Rings,
                        request.BlockLeft,
                        request.BlockBottom,
                        ctx.CameraTile,
                        tileSize,
                        factor);
                }
                default:
                    return true;
            }
        }

        /// <summary>Skip queueing loads that would be rejected at commit (prevents idle reload loops).</summary>
        public static bool ShouldQueueLoadRequest(
            Context ctx,
            SpatialStreamingHlodEvaluator.LoadRequest request) =>
            ShouldCommitLoad(ctx, request);

        public static bool ShouldDropSupertile(Context ctx, SpatialLoadedSubcellRecord supertileRecord)
        {
            if (!ctx.EnableHlod || supertileRecord?.Root == null)
                return true;

            return supertileRecord.LodLevel switch
            {
                SpatialStreamingLodLevel.Hlod4x4 => !SpatialStreamingRingCoverage.ShouldRenderHlod4(ctx, supertileRecord),
                SpatialStreamingLodLevel.Hlod2x2 => !SpatialStreamingRingCoverage.ShouldRenderHlod2(ctx, supertileRecord),
                _ => true,
            };
        }

        static int SubcellSizeMeters(Context ctx) =>
            ctx.Manifest?.SubcellSizeMeters > 0 ? ctx.Manifest.SubcellSizeMeters : 250;

        static System.Func<int, int, bool> DetailBandProbeForTile(Context ctx, SpatialTileManifestEntry tile, bool forWant)
        {
            if (!ctx.HasCameraSubcell || tile == null)
                return null;

            int tileSize = ctx.Manifest?.TileSizeMeters ?? 1000;
            return SpatialStreamingHlodHandoff.CreateDetailBandProbe(
                ctx.Rings,
                tile,
                ctx.CameraSubcell,
                tileSize,
                SubcellSizeMeters(ctx),
                forWant);
        }

        public static bool ShouldWantSubcellProxy(
            Context ctx,
            string tileId,
            string subcellId,
            int tileLeft,
            int tileBottom,
            int tileRing,
            bool detailCompleteForTile,
            bool usesSubcellProxies)
        {
            if (!ctx.EnableHlod || detailCompleteForTile || !usesSubcellProxies)
                return false;

            if (string.IsNullOrEmpty(subcellId))
                return false;

            SpatialTileManifestEntry tile = ctx.Manifest?.FindTile(tileId);
            if (tile == null || !ctx.HasCameraSubcell)
                return false;

            int tileSize = ctx.Manifest?.TileSizeMeters ?? 1000;
            return SpatialStreamingHlodHandoff.TileWantsFullSubcellProxySet(
                       ctx.Rings,
                       tile,
                       tileLeft,
                       tileBottom,
                       ctx.CameraTile,
                       ctx.CameraSubcell,
                       tileSize,
                       SubcellSizeMeters(ctx),
                       forWant: true) &&
                   !IsDetailLoaded(ctx, tileId, subcellId);
        }

        public static bool ShouldQueueSubcellProxy(
            Context ctx,
            string tileId,
            string subcellId,
            int tileLeft,
            int tileBottom,
            int tileRing,
            bool detailCompleteForTile,
            bool usesSubcellProxies)
        {
            if (!ShouldWantSubcellProxy(
                    ctx, tileId, subcellId, tileLeft, tileBottom, tileRing, detailCompleteForTile, usesSubcellProxies))
            {
                return false;
            }

            SpatialTileManifestEntry tile = ctx.Manifest?.FindTile(tileId);
            if (tile == null || !ctx.HasCameraSubcell)
                return false;

            int tileSize = ctx.Manifest?.TileSizeMeters ?? 1000;
            return SpatialStreamingHlodHandoff.TileWantsFullSubcellProxySet(
                ctx.Rings,
                tile,
                tileLeft,
                tileBottom,
                ctx.CameraTile,
                ctx.CameraSubcell,
                tileSize,
                SubcellSizeMeters(ctx),
                forWant: false);
        }

        public static bool ShouldWantTileProxy(Context ctx, string tileId, int tileLeft, int tileBottom)
        {
            if (!ShouldKeepTileProxy(ctx, tileId))
                return false;

            SpatialTileManifestEntry tile = ctx.Manifest?.FindTile(tileId);
            int tileSize = ctx.Manifest?.TileSizeMeters ?? 1000;
            var detailProbe = DetailBandProbeForTile(ctx, tile, forWant: true);
            return SpatialStreamingHlodHandoff.TileInExpandedTileProxyCoverage(
                ctx.Rings, tileLeft, tileBottom, ctx.CameraTile, tileSize, forWant: true, detailProbe);
        }

        public static bool ShouldQueueTileProxy(Context ctx, string tileId, int tileLeft, int tileBottom)
        {
            if (!ShouldKeepTileProxy(ctx, tileId))
                return false;

            SpatialTileManifestEntry tile = ctx.Manifest?.FindTile(tileId);
            int tileSize = ctx.Manifest?.TileSizeMeters ?? 1000;
            var detailProbe = DetailBandProbeForTile(ctx, tile, forWant: false);
            return SpatialStreamingHlodHandoff.TileInExpandedTileProxyCoverage(
                ctx.Rings, tileLeft, tileBottom, ctx.CameraTile, tileSize, forWant: false, detailProbe);
        }

        public static bool ShouldWantDetail(
            Context ctx,
            int tileLeft,
            int tileBottom,
            int subcellGridX,
            int subcellGridY)
        {
            if (!ctx.HasCameraSubcell || ctx.Rings.detailRings <= 0)
                return false;

            int tileSize = ctx.Manifest?.TileSizeMeters ?? 1000;
            int subcellRing = SpatialStreamingHlodHandoff.GetSubcellRing(
                tileLeft,
                tileBottom,
                subcellGridX,
                subcellGridY,
                ctx.CameraSubcell,
                tileSize,
                SubcellSizeMeters(ctx));
            return SpatialStreamingHlodHandoff.ShouldWantDetailForSubcell(ctx.Rings, subcellRing, forWant: true);
        }

        public static bool ShouldQueueDetail(
            Context ctx,
            int tileLeft,
            int tileBottom,
            int subcellGridX,
            int subcellGridY)
        {
            if (!ctx.HasCameraSubcell || ctx.Rings.detailRings <= 0)
                return false;

            int tileSize = ctx.Manifest?.TileSizeMeters ?? 1000;
            int subcellRing = SpatialStreamingHlodHandoff.GetSubcellRing(
                tileLeft,
                tileBottom,
                subcellGridX,
                subcellGridY,
                ctx.CameraSubcell,
                tileSize,
                SubcellSizeMeters(ctx));
            return SpatialStreamingHlodHandoff.ShouldWantDetailForSubcell(ctx.Rings, subcellRing, forWant: false);
        }

        public static bool ShouldWantDetail(int tileLeft, int tileBottom, Context ctx) =>
            false;

        public static bool ShouldQueueDetail(int tileLeft, int tileBottom, Context ctx) =>
            false;

        public static bool TileWantsMonolithicDetail(
            Context ctx,
            int tileLeft,
            int tileBottom)
        {
            if (!ctx.HasCameraSubcell || ctx.Rings.detailRings <= 0)
                return false;

            int tileSize = ctx.Manifest?.TileSizeMeters ?? 1000;
            int subcellSize = SubcellSizeMeters(ctx);
            int cellsPerEdge = Mathf.Max(1, tileSize / subcellSize);
            for (int gy = 0; gy < cellsPerEdge; gy++)
            {
                for (int gx = 0; gx < cellsPerEdge; gx++)
                {
                    if (ShouldWantDetail(ctx, tileLeft, tileBottom, gx, gy))
                        return true;
                }
            }

            return false;
        }

        public static bool TileQueuesMonolithicDetail(
            Context ctx,
            int tileLeft,
            int tileBottom)
        {
            if (!ctx.HasCameraSubcell || ctx.Rings.detailRings <= 0)
                return false;

            int tileSize = ctx.Manifest?.TileSizeMeters ?? 1000;
            int subcellSize = SubcellSizeMeters(ctx);
            int cellsPerEdge = Mathf.Max(1, tileSize / subcellSize);
            for (int gy = 0; gy < cellsPerEdge; gy++)
            {
                for (int gx = 0; gx < cellsPerEdge; gx++)
                {
                    if (ShouldQueueDetail(ctx, tileLeft, tileBottom, gx, gy))
                        return true;
                }
            }

            return false;
        }

        public static int GetLoadPriority(SpatialStreamingLodLevel lodLevel) => lodLevel switch
        {
            SpatialStreamingLodLevel.Hlod4x4 => 0,
            SpatialStreamingLodLevel.Hlod2x2 => 1,
            SpatialStreamingLodLevel.TileProxy => 2,
            SpatialStreamingLodLevel.SubcellProxy => 3,
            SpatialStreamingLodLevel.Detail => 4,
            _ => 5,
        };

        /// <summary>
        /// Detail stays after subcell proxies in the global queue; per-slot coarse must already be committed.
        /// </summary>
        public static int GetEffectiveLoadPriority(
            Context ctx,
            SpatialStreamingHlodEvaluator.LoadRequest request) =>
            GetLoadPriority(request.LodLevel);

        public static bool IsDetailCoarseReadyForLoad(
            Context ctx,
            SpatialStreamingHlodEvaluator.LoadRequest request)
        {
            if (!ctx.EnableHlod || !ctx.Rings.UsesCoarseLodChain)
                return true;

            if (string.IsNullOrEmpty(request.TileId))
                return true;

            if (request.SubcellId == "tile_coarse")
                return true;

            if (string.IsNullOrEmpty(request.SubcellId))
                return false;

            SpatialTileManifestEntry tile = ctx.Manifest?.FindTile(request.TileId);
            if (tile == null)
                return false;

            SpatialSubcellManifestEntry subcell = FindSubcell(ctx, request.TileId, request.SubcellId);
            if (subcell == null)
                return false;

            bool usesSubcellProxies = TileUsesSubcellProxies(tile);
            if (!SpatialTileIdUtility.TryParse(request.TileId, out int tileLeft, out int tileBottom))
                return false;

            int tileSize = ctx.Manifest?.TileSizeMeters ?? 1000;
            if (usesSubcellProxies &&
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
                        SubcellSizeMeters(ctx)),
                    forWant: false) &&
                !SpatialStreamingRingCoverage.TileHasAllSubcellProxiesLoaded(ctx, tile))
            {
                return false;
            }

            if (!SpatialStreamingCoveragePlanner.TryResolveCoarseCoverage(
                    ctx,
                    tile,
                    subcell,
                    usesSubcellProxies,
                    out string coarseKey,
                    out _))
            {
                return true;
            }

            if (string.IsNullOrEmpty(coarseKey))
                return true;

            if (ctx.LoadingKeys != null && ctx.LoadingKeys.Contains(coarseKey))
                return false;

            if (ctx.LoadedKeys != null && ctx.LoadedKeys.Contains(coarseKey))
                return true;

            return false;
        }

        public static int CountDetailInFlightForTile(
            Context ctx,
            string tileId,
            IReadOnlyList<SpatialStreamingHlodEvaluator.LoadRequest> queuedPending)
        {
            if (string.IsNullOrEmpty(tileId))
                return 0;

            int count = ctx.LoadedState != null ? ctx.LoadedState.CountDetailInFlight(tileId) : 0;
            if (ctx.LoadingKeys != null && ctx.LoadedState == null)
            {
                foreach (string key in ctx.LoadingKeys)
                {
                    if (!TryParseDetailKey(key, out string loadingTileId, out _))
                        continue;

                    if (loadingTileId == tileId)
                        count++;
                }
            }

            if (queuedPending == null)
                return count;

            foreach (SpatialStreamingHlodEvaluator.LoadRequest request in queuedPending)
            {
                if (request.LodLevel != SpatialStreamingLodLevel.Detail ||
                    request.TileId != tileId)
                {
                    continue;
                }

                count++;
            }

            return count;
        }

        static SpatialSubcellManifestEntry FindSubcell(SpatialTileManifestEntry tile, string subcellId)
        {
            if (tile?.Subcells == null || string.IsNullOrEmpty(subcellId))
                return null;

            foreach (SpatialSubcellManifestEntry subcell in tile.Subcells)
            {
                if (subcell != null && subcell.SubcellId == subcellId)
                    return subcell;
            }

            return null;
        }

        public static SpatialSubcellManifestEntry FindSubcell(Context ctx, string tileId, string subcellId)
        {
            if (ctx.RuntimeIndex != null &&
                ctx.RuntimeIndex.TryGetSubcell(tileId, subcellId, out SpatialSubcellManifestEntry indexed))
            {
                return indexed;
            }

            return FindSubcell(ctx.Manifest?.FindTile(tileId), subcellId);
        }

        static bool TileUsesSubcellProxies(SpatialTileManifestEntry tile)
        {
            if (tile?.Subcells == null)
                return false;

            foreach (SpatialSubcellManifestEntry subcell in tile.Subcells)
            {
                if (subcell != null && !string.IsNullOrEmpty(subcell.ProxyBundleRel))
                    return true;
            }

            return false;
        }

        public static int CompareLoadRequests(
            Context ctx,
            SpatialStreamingHlodEvaluator.LoadRequest a,
            SpatialStreamingHlodEvaluator.LoadRequest b)
        {
            int lodCmp = GetEffectiveLoadPriority(ctx, a).CompareTo(GetEffectiveLoadPriority(ctx, b));
            if (lodCmp != 0)
                return lodCmp;

            if (ctx.UseScreenSpaceLodPriority)
            {
                float da = ComputeScreenSpacePriority(ctx, a);
                float db = ComputeScreenSpacePriority(ctx, b);
                int screenCmp = da.CompareTo(db);
                if (screenCmp != 0)
                    return screenCmp;
            }

            return a.PriorityDistance.CompareTo(b.PriorityDistance);
        }

        static float ComputeScreenSpacePriority(
            Context ctx,
            SpatialStreamingHlodEvaluator.LoadRequest request)
        {
            Vector3 worldPos = ResolveRequestWorldPosition(ctx, request);
            float fov = ctx.ScreenSpaceFovDegrees > 1f ? ctx.ScreenSpaceFovDegrees : 60f;
            float size = request.LodLevel switch
            {
                SpatialStreamingLodLevel.Hlod4x4 => (ctx.Manifest?.TileSizeMeters ?? 1000) * 4f,
                SpatialStreamingLodLevel.Hlod2x2 => (ctx.Manifest?.TileSizeMeters ?? 1000) * 2f,
                _ => ctx.Manifest?.SubcellSizeMeters > 0 ? ctx.Manifest.SubcellSizeMeters : 250f,
            };

            return SpatialStreamingLodMetrics.ComputePriorityDistance(
                ctx.CameraPosition,
                worldPos,
                new SpatialStreamingLodMetrics.LodMetricEntry { MaxExtentMeters = size },
                useScreenSpaceError: true,
                fov);
        }

        static Vector3 ResolveRequestWorldPosition(
            Context ctx,
            SpatialStreamingHlodEvaluator.LoadRequest request)
        {
            if (request.LodLevel == SpatialStreamingLodLevel.Hlod2x2 ||
                request.LodLevel == SpatialStreamingLodLevel.Hlod4x4)
            {
                if (ctx.RuntimeIndex != null &&
                    ctx.RuntimeIndex.TryGetSupertile(request.SupertileId, out SpatialSupertileManifestEntry supertile))
                {
                    return supertile.GetUnityPosition();
                }

                SpatialSupertileManifestEntry manifestSupertile = ctx.Manifest?.FindSupertile(request.SupertileId);
                if (manifestSupertile != null)
                    return manifestSupertile.GetUnityPosition();
            }

            if (!string.IsNullOrEmpty(request.TileId))
            {
                if (ctx.RuntimeIndex != null &&
                    ctx.RuntimeIndex.TryGetTile(request.TileId, out SpatialTileManifestEntry indexedTile))
                {
                    return indexedTile.GetUnityPosition();
                }

                SpatialTileManifestEntry tile = ctx.Manifest?.FindTile(request.TileId);
                if (tile != null)
                    return tile.GetUnityPosition();
            }

            return ctx.CameraPosition;
        }

        public static void CollectSupersededKeys(Context ctx, HashSet<string> want, List<string> into)
        {
            if (ctx.LoadedRecords == null || into == null)
                return;

            foreach (KeyValuePair<string, SpatialLoadedSubcellRecord> kvp in ctx.LoadedRecords)
            {
                SpatialLoadedSubcellRecord record = kvp.Value;
                if (record == null)
                    continue;

                switch (record.LodLevel)
                {
                    case SpatialStreamingLodLevel.SubcellProxy:
                        if (!ShouldKeepSubcellProxyRecord(ctx, record))
                            into.Add(kvp.Key);
                        break;

                    case SpatialStreamingLodLevel.TileProxy:
                        if (!ShouldKeepTileProxy(ctx, record.TileId))
                            into.Add(kvp.Key);
                        break;

                    case SpatialStreamingLodLevel.Hlod2x2:
                    case SpatialStreamingLodLevel.Hlod4x4:
                        // Supertiles are visibility-hidden while finer layers cover them.
                        // Unload only when evaluate drops them from want (see EvaluateStreaming).
                        break;
                }
            }
        }

        public static bool IsTileDetailCompletePublic(Context ctx, string tileId) =>
            IsTileDetailComplete(ctx, tileId);

        static bool IsTileDetailComplete(Context ctx, string tileId)
        {
            if (string.IsNullOrEmpty(tileId))
                return false;

            if (ctx.LoadedState != null)
                return ctx.LoadedState.IsTileDetailComplete(tileId);

            if (ctx.Manifest == null)
                return false;

            SpatialTileManifestEntry tile = ctx.Manifest.FindTile(tileId);
            if (tile == null)
                return false;

            if (!tile.UsesSubcells)
            {
                string coarseKey = SpatialStreamingHlodEvaluator.BuildDetailKey(tileId, "tile_coarse");
                return ctx.LoadedKeys != null && ctx.LoadedKeys.Contains(coarseKey);
            }

            bool requiresAny = false;
            foreach (SpatialSubcellManifestEntry subcell in tile.Subcells)
            {
                if (subcell == null || string.IsNullOrEmpty(subcell.BundleRel))
                    continue;

                requiresAny = true;
                if (!IsDetailLoaded(ctx, tileId, subcell.SubcellId))
                    return false;
            }

            return requiresAny;
        }
    }

    /// <summary>
    /// Optional time-sliced evaluate cursor shared between controller and evaluator.
    /// </summary>
    public sealed class SpatialStreamingEvaluateSliceState
    {
        public bool Enabled;
        public int TileBudgetPerEvaluate = 48;
        public int SupertileBudgetPerEvaluate = 16;
        public int TileCursor;
        public int SupertileCursor;
    }

    /// <summary>
    /// Optional screen-space / projected-size helpers for advanced LOD prioritization.
    /// </summary>
    public static class SpatialStreamingLodMetrics
    {
        public struct LodMetricEntry
        {
            public float MaxExtentMeters;
            public int TriangleCount;
            public float GeometricErrorMeters;
        }

        public static float ComputeScreenSpaceErrorMeters(
            float worldSizeMeters,
            float distanceMeters,
            float fieldOfViewDegrees)
        {
            if (distanceMeters <= 0.01f || worldSizeMeters <= 0f)
                return float.MaxValue;

            float halfFovRad = fieldOfViewDegrees * Mathf.Deg2Rad * 0.5f;
            return worldSizeMeters / (distanceMeters * Mathf.Tan(halfFovRad));
        }

        public static float ComputePriorityDistance(
            Vector3 cameraPosition,
            Vector3 worldPosition,
            LodMetricEntry metric,
            bool useScreenSpaceError,
            float fieldOfViewDegrees)
        {
            float distance = Vector3.Distance(cameraPosition, worldPosition);
            if (!useScreenSpaceError)
                return distance;

            float size = metric.MaxExtentMeters > 0f ? metric.MaxExtentMeters : 1f;
            return ComputeScreenSpaceErrorMeters(size, Mathf.Max(distance, 1f), fieldOfViewDegrees);
        }
    }

    /// <summary>
    /// Optional LRU-style residency cap: evict loaded blocks farthest from camera that are not in want.
    /// </summary>
    public static class SpatialStreamingResidencyBudget
    {
        public static void EnforceBudget(
            int maxResidentBlocks,
            IReadOnlyDictionary<string, SpatialLoadedSubcellRecord> loaded,
            HashSet<string> want,
            Vector3 cameraPosition,
            System.Action<string> enqueueUnload)
        {
            if (maxResidentBlocks <= 0 || loaded == null || enqueueUnload == null || loaded.Count <= maxResidentBlocks)
                return;

            var candidates = new List<(string key, float distance)>(loaded.Count);
            foreach (KeyValuePair<string, SpatialLoadedSubcellRecord> kvp in loaded)
            {
                if (want != null && want.Contains(kvp.Key))
                    continue;

                SpatialLoadedSubcellRecord record = kvp.Value;
                if (record?.Root == null)
                    continue;

                candidates.Add((kvp.Key, Vector3.SqrMagnitude(record.Root.transform.position - cameraPosition)));
            }

            candidates.Sort((a, b) => b.distance.CompareTo(a.distance));

            int toEvict = loaded.Count - maxResidentBlocks;
            for (int i = 0; i < candidates.Count && toEvict > 0; i++)
            {
                enqueueUnload(candidates[i].key);
                toEvict--;
            }
        }
    }
}
