using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// Decides which spatial blocks belong in the want/pending sets from camera tile rings and manifest data.
    /// Substitution visibility/unload rules live in <see cref="SpatialStreamingLodSubstitution"/>.
    /// </summary>
    public static class SpatialStreamingHlodEvaluator
    {
        public struct LoadRequest
        {
            public string Key;
            public string TileId;
            public string SubcellId;
            public string SupertileId;
            public string BundleRel;
            public int GridX;
            public int GridY;
            public SpatialStreamingLodLevel LodLevel;
            public int HlodFactor;
            public int BlockLeft;
            public int BlockBottom;
            public float PriorityDistance;
        }

        public static void Evaluate(
            SpatialDatasetManifest manifest,
            Vector3 camPos,
            SpatialStreamingTileRings rings,
            bool enableHlod,
            HashSet<string> detailCompleteTileIds,
            HashSet<string> want,
            List<LoadRequest> pending,
            HashSet<string> loading,
            HashSet<string> loadedKeys,
            HashSet<string> pendingKeys,
            IReadOnlyDictionary<string, SpatialLoadedSubcellRecord> loadedRecords,
            SpatialStreamingLodSubstitution.Context substitutionContext)
        {
            if (manifest == null)
                return;

            if (!SpatialStreamingTileRingUtility.TryResolveCameraTileGrid(
                    manifest,
                    substitutionContext.RuntimeIndex,
                    camPos,
                    out var cameraTile))
                return;

            detailCompleteTileIds ??= new HashSet<string>();
            int tileSize = manifest.TileSizeMeters;
            int subcellSize = manifest.SubcellSizeMeters > 0 ? manifest.SubcellSizeMeters : 250;

            SpatialStreamingLodSubstitution.Context ctx = substitutionContext;
            if (ctx.Manifest == null)
            {
                ctx = new SpatialStreamingLodSubstitution.Context
                {
                    Manifest = manifest,
                    CameraPosition = camPos,
                    Rings = rings,
                    CameraTile = cameraTile,
                    EnableHlod = enableHlod,
                    LoadedKeys = loadedKeys,
                    LoadingKeys = loading,
                    LoadedRecords = loadedRecords,
                };
            }
            else
            {
                ctx.CameraPosition = camPos;
                ctx.Rings = rings;
                ctx.CameraTile = cameraTile;
                ctx.EnableHlod = enableHlod;
                ctx.LoadedKeys = loadedKeys;
                ctx.LoadingKeys = loading;
                ctx.LoadedRecords = loadedRecords;
                if (ctx.LoadedState != null)
                {
                    ctx.Hlod2ByBlock = ctx.LoadedState.Hlod2ByBlock;
                    ctx.Hlod4ByBlock = ctx.LoadedState.Hlod4ByBlock;
                }
            }

            EvaluateSupertiles(manifest, ctx, rings, tileSize, want, pending, loading, loadedKeys, pendingKeys);
            EvaluateTiles(
                manifest,
                ctx,
                rings,
                tileSize,
                subcellSize,
                detailCompleteTileIds,
                want,
                pending,
                loading,
                loadedKeys,
                pendingKeys);
        }

        static void EvaluateSupertiles(
            SpatialDatasetManifest manifest,
            SpatialStreamingLodSubstitution.Context ctx,
            SpatialStreamingTileRings rings,
            int tileSize,
            HashSet<string> want,
            List<LoadRequest> pending,
            HashSet<string> loading,
            HashSet<string> loadedKeys,
            HashSet<string> pendingKeys)
        {
            if (!ctx.EnableHlod)
                return;

            IReadOnlyList<SpatialSupertileManifestEntry> supertiles = ctx.RuntimeIndex != null
                ? ctx.RuntimeIndex.CollectSupertilesInRing(ctx.CameraTile, rings, tileSize)
                : manifest.Supertiles;

            int supertileCount = supertiles?.Count ?? 0;
            int supertileStart = 0;
            int supertileEnd = supertileCount;
            if (ctx.EvaluateSlice != null &&
                ctx.EvaluateSlice.Enabled &&
                ctx.EvaluateSlice.SupertileBudgetPerEvaluate > 0 &&
                supertileCount > ctx.EvaluateSlice.SupertileBudgetPerEvaluate)
            {
                supertileStart = ctx.EvaluateSlice.SupertileCursor % supertileCount;
                supertileEnd = supertileStart + ctx.EvaluateSlice.SupertileBudgetPerEvaluate;
                ctx.EvaluateSlice.SupertileCursor =
                    (supertileStart + ctx.EvaluateSlice.SupertileBudgetPerEvaluate) % supertileCount;
            }

            for (int i = supertileStart; i < supertileEnd; i++)
            {
                int index = i >= supertileCount ? i - supertileCount : i;
                if (index < 0 || index >= supertileCount)
                    continue;

                SpatialSupertileManifestEntry supertile = supertiles[index];
                if (supertile == null || string.IsNullOrEmpty(supertile.BundleRel))
                    continue;

                int factor = supertile.Factor >= 4 ? 4 : 2;
                SpatialStreamingLodLevel lod = factor >= 4
                    ? SpatialStreamingLodLevel.Hlod4x4
                    : SpatialStreamingLodLevel.Hlod2x2;

                int blockRing = SpatialStreamingTileRingUtility.ChebyshevBlockRing(
                    supertile.Left,
                    supertile.Bottom,
                    ctx.CameraTile,
                    tileSize * factor);

                string key = BuildSupertileKey(supertile.SupertileId);
                if (SpatialStreamingTileRingUtility.ShouldWantSupertile(
                        rings, supertile.Left, supertile.Bottom, ctx.CameraTile, tileSize, factor))
                {
                    want.Add(key);
                }

                if (!SpatialStreamingTileRingUtility.ShouldQueueSupertile(
                        rings, supertile.Left, supertile.Bottom, ctx.CameraTile, tileSize, factor) ||
                    loadedKeys.Contains(key) ||
                    loading.Contains(key) ||
                    pendingKeys.Contains(key))
                {
                    continue;
                }

                TryQueuePendingLoad(ctx, new LoadRequest
                {
                    Key = key,
                    SupertileId = supertile.SupertileId,
                    SubcellId = supertile.SupertileId,
                    BundleRel = supertile.BundleRel,
                    LodLevel = lod,
                    HlodFactor = supertile.Factor,
                    BlockLeft = supertile.Left,
                    BlockBottom = supertile.Bottom,
                    PriorityDistance = blockRing,
                }, pending);
            }
        }

        static void EvaluateTiles(
            SpatialDatasetManifest manifest,
            SpatialStreamingLodSubstitution.Context ctx,
            SpatialStreamingTileRings rings,
            int tileSize,
            int subcellSize,
            HashSet<string> detailCompleteTileIds,
            HashSet<string> want,
            List<LoadRequest> pending,
            HashSet<string> loading,
            HashSet<string> loadedKeys,
            HashSet<string> pendingKeys)
        {
            int maxTileRing = rings.FurthestConfiguredRingEnd + 1;

            IReadOnlyList<SpatialTileManifestEntry> tiles = ctx.RuntimeIndex != null
                ? ctx.RuntimeIndex.CollectTilesInRing(ctx.CameraTile, maxTileRing)
                : manifest.Tiles;

            int tileCount = tiles?.Count ?? 0;
            int tileStart = 0;
            int tileEnd = tileCount;
            if (ctx.EvaluateSlice != null &&
                ctx.EvaluateSlice.Enabled &&
                ctx.EvaluateSlice.TileBudgetPerEvaluate > 0 &&
                tileCount > ctx.EvaluateSlice.TileBudgetPerEvaluate)
            {
                tileStart = ctx.EvaluateSlice.TileCursor % tileCount;
                tileEnd = tileStart + ctx.EvaluateSlice.TileBudgetPerEvaluate;
                ctx.EvaluateSlice.TileCursor =
                    (tileStart + ctx.EvaluateSlice.TileBudgetPerEvaluate) % tileCount;
            }

            for (int i = tileStart; i < tileEnd; i++)
            {
                int index = i >= tileCount ? i - tileCount : i;
                if (index < 0 || index >= tileCount)
                    continue;

                SpatialTileManifestEntry tile = tiles[index];
                if (tile == null || string.IsNullOrEmpty(tile.TileId))
                    continue;

                if (!SpatialTileIdUtility.TryParse(tile.TileId, out int tileLeft, out int tileBottom))
                    continue;

                int tileRing = SpatialStreamingTileRingUtility.ChebyshevTileRing(
                    tileLeft, tileBottom, ctx.CameraTile, tileSize);
                bool detailComplete = detailCompleteTileIds.Contains(tile.TileId);
                bool usesSubcellProxies = TileUsesSubcellProxies(tile);

                if (ctx.EnableHlod &&
                    !detailComplete &&
                    !usesSubcellProxies &&
                    !string.IsNullOrEmpty(tile.ProxyBundleRel))
                {
                    EvaluateTileProxy(
                        ctx,
                        tile,
                        tileRing,
                        rings,
                        want,
                        pending,
                        loading,
                        loadedKeys,
                        pendingKeys);
                }

                if (tile.UsesSubcells)
                {
                    EvaluateSubcellsCoarse(
                        ctx,
                        tile,
                        tileLeft,
                        tileBottom,
                        tileRing,
                        subcellSize,
                        detailComplete,
                        usesSubcellProxies,
                        rings,
                        want,
                        pending,
                        loading,
                        loadedKeys,
                        pendingKeys);

                    EvaluateSubcellsDetail(
                        ctx,
                        tile,
                        tileRing,
                        rings,
                        want,
                        pending,
                        loading,
                        loadedKeys,
                        pendingKeys);
                }
                else
                {
                    EvaluateMonolithicTile(
                        ctx, tile, tileRing, rings, want, pending, loading, loadedKeys, pendingKeys);
                }
            }
        }

        static void EvaluateSubcellsCoarse(
            SpatialStreamingLodSubstitution.Context ctx,
            SpatialTileManifestEntry tile,
            int tileLeft,
            int tileBottom,
            int tileRing,
            int subcellSize,
            bool detailComplete,
            bool usesSubcellProxies,
            SpatialStreamingTileRings rings,
            HashSet<string> want,
            List<LoadRequest> pending,
            HashSet<string> loading,
            HashSet<string> loadedKeys,
            HashSet<string> pendingKeys)
        {
            if (!usesSubcellProxies)
                return;

            foreach (SpatialSubcellManifestEntry subcell in tile.Subcells)
            {
                if (subcell == null || string.IsNullOrEmpty(subcell.ProxyBundleRel))
                    continue;

                if (!SpatialStreamingLodSubstitution.ShouldWantSubcellProxy(
                        ctx, tile.TileId, subcell.SubcellId, tileRing, detailComplete, usesSubcellProxies))
                {
                    continue;
                }

                string proxyKey = BuildSubcellProxyKey(tile.TileId, subcell.SubcellId);
                want.Add(proxyKey);

                if (!SpatialStreamingLodSubstitution.ShouldQueueSubcellProxy(
                        ctx, tile.TileId, subcell.SubcellId, tileRing, detailComplete, usesSubcellProxies) ||
                    SpatialStreamingLodSubstitution.IsDetailLoaded(ctx, tile.TileId, subcell.SubcellId) ||
                    loadedKeys.Contains(proxyKey) ||
                    loading.Contains(proxyKey) ||
                    pendingKeys.Contains(proxyKey))
                {
                    continue;
                }

                TryQueuePendingLoad(ctx, new LoadRequest
                {
                    Key = proxyKey,
                    TileId = tile.TileId,
                    SubcellId = subcell.SubcellId,
                    BundleRel = subcell.ProxyBundleRel,
                    GridX = subcell.GridX,
                    GridY = subcell.GridY,
                    LodLevel = SpatialStreamingLodLevel.SubcellProxy,
                    PriorityDistance = tileRing,
                }, pending);
            }
        }

        static void EvaluateSubcellsDetail(
            SpatialStreamingLodSubstitution.Context ctx,
            SpatialTileManifestEntry tile,
            int tileRing,
            SpatialStreamingTileRings rings,
            HashSet<string> want,
            List<LoadRequest> pending,
            HashSet<string> loading,
            HashSet<string> loadedKeys,
            HashSet<string> pendingKeys)
        {
            foreach (SpatialSubcellManifestEntry subcell in tile.Subcells)
            {
                if (subcell == null || string.IsNullOrEmpty(subcell.BundleRel))
                    continue;

                string detailKey = BuildDetailKey(tile.TileId, subcell.SubcellId);

                if (!SpatialStreamingLodSubstitution.ShouldWantDetail(tileRing, ctx))
                    continue;

                want.Add(detailKey);

                if (loadedKeys.Contains(detailKey) ||
                    loading.Contains(detailKey) ||
                    pendingKeys.Contains(detailKey))
                {
                    continue;
                }

                if (!SpatialStreamingLodSubstitution.ShouldQueueDetail(tileRing, ctx))
                    continue;

                var detailRequest = new LoadRequest
                {
                    Key = detailKey,
                    TileId = tile.TileId,
                    SubcellId = subcell.SubcellId,
                    LodLevel = SpatialStreamingLodLevel.Detail,
                };

                if (!SpatialStreamingLodSubstitution.IsDetailCoarseReadyForLoad(ctx, detailRequest))
                    continue;

                TryQueuePendingLoad(ctx, new LoadRequest
                {
                    Key = detailKey,
                    TileId = tile.TileId,
                    SubcellId = subcell.SubcellId,
                    BundleRel = subcell.BundleRel,
                    GridX = subcell.GridX,
                    GridY = subcell.GridY,
                    LodLevel = SpatialStreamingLodLevel.Detail,
                    PriorityDistance = tileRing,
                }, pending);
            }
        }

        static void EvaluateMonolithicTile(
            SpatialStreamingLodSubstitution.Context ctx,
            SpatialTileManifestEntry tile,
            int tileRing,
            SpatialStreamingTileRings rings,
            HashSet<string> want,
            List<LoadRequest> pending,
            HashSet<string> loading,
            HashSet<string> loadedKeys,
            HashSet<string> pendingKeys)
        {
            if (!SpatialStreamingLodSubstitution.ShouldWantDetail(tileRing, ctx))
                return;

            if (string.IsNullOrEmpty(tile.CoarseBundleRel))
                return;

            string key = BuildDetailKey(tile.TileId, "tile_coarse");
            want.Add(key);

            if (loadedKeys.Contains(key) ||
                loading.Contains(key) ||
                pendingKeys.Contains(key) ||
                !SpatialStreamingLodSubstitution.ShouldQueueDetail(tileRing, ctx))
            {
                return;
            }

            TryQueuePendingLoad(ctx, new LoadRequest
            {
                Key = key,
                TileId = tile.TileId,
                SubcellId = "tile_coarse",
                BundleRel = tile.CoarseBundleRel,
                LodLevel = SpatialStreamingLodLevel.Detail,
                PriorityDistance = tileRing,
            }, pending);
        }

        static void EvaluateTileProxy(
            SpatialStreamingLodSubstitution.Context ctx,
            SpatialTileManifestEntry tile,
            int tileRing,
            SpatialStreamingTileRings rings,
            HashSet<string> want,
            List<LoadRequest> pending,
            HashSet<string> loading,
            HashSet<string> loadedKeys,
            HashSet<string> pendingKeys)
        {
            if (!SpatialStreamingLodSubstitution.ShouldKeepTileProxy(ctx, tile.TileId))
                return;

            string proxyKey = BuildProxyKey(tile.TileId);
            if (SpatialStreamingLodSubstitution.ShouldWantTileProxy(ctx, tile.TileId, tileRing))
                want.Add(proxyKey);

            if (!SpatialStreamingLodSubstitution.ShouldQueueTileProxy(ctx, tile.TileId, tileRing) ||
                loadedKeys.Contains(proxyKey) ||
                loading.Contains(proxyKey) ||
                pendingKeys.Contains(proxyKey))
            {
                return;
            }

            TryQueuePendingLoad(ctx, new LoadRequest
            {
                Key = proxyKey,
                TileId = tile.TileId,
                SubcellId = "tile_proxy",
                BundleRel = tile.ProxyBundleRel,
                LodLevel = SpatialStreamingLodLevel.TileProxy,
                PriorityDistance = tileRing,
            }, pending);
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

        public static string BuildDetailKey(string tileId, string subcellId) => $"{tileId}|{subcellId}";

        public static bool IsSubcellDetailLoaded(
            string tileId,
            string subcellId,
            HashSet<string> loadedKeys) =>
            !string.IsNullOrEmpty(tileId) &&
            !string.IsNullOrEmpty(subcellId) &&
            loadedKeys != null &&
            loadedKeys.Contains(BuildDetailKey(tileId, subcellId));

        public static bool TileHasAnyDetailLoaded(string tileId, HashSet<string> loadedKeys)
        {
            if (string.IsNullOrEmpty(tileId) || loadedKeys == null || loadedKeys.Count == 0)
                return false;

            string prefix = tileId + "|";
            foreach (string key in loadedKeys)
            {
                if (!key.StartsWith(prefix))
                    continue;

                if (key.EndsWith("|proxy") || key.EndsWith("|hlod"))
                    continue;

                return true;
            }

            return false;
        }

        public static string BuildProxyKey(string tileId) => $"{tileId}|proxy";

        public static string BuildSubcellProxyKey(string tileId, string subcellId) => $"{tileId}|{subcellId}|proxy";

        public static string BuildSupertileKey(string supertileId) => $"{supertileId}|hlod";

        public static bool TryInferLodLevelFromKey(
            string key,
            SpatialDatasetManifest manifest,
            out SpatialStreamingLodLevel lodLevel)
        {
            lodLevel = SpatialStreamingLodLevel.Detail;
            if (string.IsNullOrEmpty(key))
                return false;

            if (key.EndsWith("|proxy"))
            {
                int pipeCount = 0;
                for (int i = 0; i < key.Length; i++)
                {
                    if (key[i] == '|')
                        pipeCount++;
                }

                lodLevel = pipeCount >= 2
                    ? SpatialStreamingLodLevel.SubcellProxy
                    : SpatialStreamingLodLevel.TileProxy;
                return true;
            }

            if (key.EndsWith("|hlod"))
            {
                string supertileId = key.Substring(0, key.Length - "|hlod".Length);
                SpatialSupertileManifestEntry supertile = manifest?.FindSupertile(supertileId);
                lodLevel = supertile != null && supertile.Factor >= 4
                    ? SpatialStreamingLodLevel.Hlod4x4
                    : SpatialStreamingLodLevel.Hlod2x2;
                return true;
            }

            lodLevel = SpatialStreamingLodLevel.Detail;
            return true;
        }

        static bool TryQueuePendingLoad(
            SpatialStreamingLodSubstitution.Context ctx,
            LoadRequest request,
            List<LoadRequest> pending)
        {
            if (!SpatialStreamingLodSubstitution.ShouldQueueLoadRequest(ctx, request))
                return false;

            pending.Add(request);
            return true;
        }
    }
}
