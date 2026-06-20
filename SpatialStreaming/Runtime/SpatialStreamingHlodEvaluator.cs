using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
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
            SpatialStreamingHlodDistances distances,
            bool enableHlod,
            HashSet<string> detailCompleteTileIds,
            HashSet<string> want,
            List<LoadRequest> pending,
            HashSet<string> loading,
            HashSet<string> loadedKeys,
            HashSet<string> pendingKeys)
        {
            if (manifest == null)
                return;

            detailCompleteTileIds ??= new HashSet<string>();
            int tileSize = manifest.TileSizeMeters;
            int subcellSize = manifest.SubcellSizeMeters > 0 ? manifest.SubcellSizeMeters : 250;

            if (enableHlod && manifest.Supertiles != null)
            {
                foreach (SpatialSupertileManifestEntry supertile in manifest.Supertiles)
                {
                    if (supertile == null || string.IsNullOrEmpty(supertile.BundleRel))
                        continue;

                    if (SupertileHasDetailCompleteChild(supertile, detailCompleteTileIds))
                        continue;

                    SpatialStreamingLodLevel lod = supertile.Factor >= 4
                        ? SpatialStreamingLodLevel.Hlod4x4
                        : SpatialStreamingLodLevel.Hlod2x2;

                    float dist = SpatialTileDistanceUtility.TileBoundaryDistance(
                        camPos,
                        supertile.GetUnityPosition(),
                        tileSize * supertile.Factor);

                    string key = BuildSupertileKey(supertile.SupertileId);
                    if (dist <= distances.UnloadDistanceFor(lod))
                        want.Add(key);

                    if (dist <= distances.LoadDistanceFor(lod) &&
                        !loadedKeys.Contains(key) &&
                        !loading.Contains(key) &&
                        !pendingKeys.Contains(key))
                    {
                        if (lod == SpatialStreamingLodLevel.Hlod2x2 &&
                            !IsCoarserHlodReadyForSupertile(
                                manifest,
                                supertile,
                                tileSize,
                                loadedKeys,
                                loading,
                                pendingKeys))
                        {
                            continue;
                        }

                        pending.Add(new LoadRequest
                        {
                            Key = key,
                            SupertileId = supertile.SupertileId,
                            SubcellId = supertile.SupertileId,
                            BundleRel = supertile.BundleRel,
                            LodLevel = lod,
                            HlodFactor = supertile.Factor,
                            BlockLeft = supertile.Left,
                            BlockBottom = supertile.Bottom,
                            PriorityDistance = dist,
                        });
                    }
                }
            }

            foreach (SpatialTileManifestEntry tile in manifest.Tiles ?? new List<SpatialTileManifestEntry>())
            {
                if (tile == null || string.IsNullOrEmpty(tile.TileId))
                    continue;

                Vector3 tileOrigin = tile.GetUnityPosition();
                float tileDist = SpatialTileDistanceUtility.TileBoundaryDistance(camPos, tileOrigin, tileSize);
                bool detailComplete = detailCompleteTileIds.Contains(tile.TileId);
                bool usesSubcellProxies = TileUsesSubcellProxies(tile);

                if (enableHlod && !detailComplete && !usesSubcellProxies && !string.IsNullOrEmpty(tile.ProxyBundleRel))
                {
                    QueueTileProxy(
                        manifest,
                        tile,
                        tileDist,
                        distances,
                        tileSize,
                        want,
                        pending,
                        loading,
                        loadedKeys,
                        pendingKeys);
                }

                if (tile.UsesSubcells)
                {
                    foreach (SpatialSubcellManifestEntry subcell in tile.Subcells)
                    {
                        if (subcell == null)
                            continue;

                        float subcellDist = SpatialTileDistanceUtility.SubcellBoundaryDistance(
                            camPos,
                            tileOrigin,
                            subcellSize,
                            subcell.GridX,
                            subcell.GridY);

                        if (enableHlod &&
                            !detailComplete &&
                            usesSubcellProxies &&
                            !string.IsNullOrEmpty(subcell.ProxyBundleRel))
                        {
                            QueueSubcellProxy(
                                manifest,
                                tile,
                                subcell,
                                subcellDist,
                                distances,
                                tileSize,
                                want,
                                pending,
                                loading,
                                loadedKeys,
                                pendingKeys);
                        }

                        if (string.IsNullOrEmpty(subcell.BundleRel))
                            continue;

                        if (subcellDist > distances.detailUnloadMeters)
                            continue;

                        string detailKey = BuildDetailKey(tile.TileId, subcell.SubcellId);
                        want.Add(detailKey);

                        if (loadedKeys.Contains(detailKey) ||
                            loading.Contains(detailKey) ||
                            pendingKeys.Contains(detailKey))
                        {
                            continue;
                        }

                        if (subcellDist <= distances.detailLoadMeters)
                        {
                            if (!IsCoarserLodReadyForDetail(
                                    enableHlod,
                                    tile,
                                    subcell,
                                    subcellDist,
                                    distances,
                                    usesSubcellProxies,
                                    loadedKeys,
                                    loading))
                            {
                                continue;
                            }

                            pending.Add(new LoadRequest
                            {
                                Key = detailKey,
                                TileId = tile.TileId,
                                SubcellId = subcell.SubcellId,
                                BundleRel = subcell.BundleRel,
                                GridX = subcell.GridX,
                                GridY = subcell.GridY,
                                LodLevel = SpatialStreamingLodLevel.Detail,
                                PriorityDistance = subcellDist,
                            });
                        }
                    }
                }
                else
                {
                    if (tileDist > distances.detailUnloadMeters)
                        continue;

                    if (!string.IsNullOrEmpty(tile.CoarseBundleRel))
                    {
                        string key = BuildDetailKey(tile.TileId, "tile_coarse");
                        want.Add(key);

                        if (!loadedKeys.Contains(key) &&
                            !loading.Contains(key) &&
                            !pendingKeys.Contains(key) &&
                            tileDist <= distances.detailLoadMeters)
                        {
                            pending.Add(new LoadRequest
                            {
                                Key = key,
                                TileId = tile.TileId,
                                SubcellId = "tile_coarse",
                                BundleRel = tile.CoarseBundleRel,
                                LodLevel = SpatialStreamingLodLevel.Detail,
                                PriorityDistance = tileDist,
                            });
                        }
                    }
                }
            }
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

        static void QueueTileProxy(
            SpatialDatasetManifest manifest,
            SpatialTileManifestEntry tile,
            float tileDist,
            SpatialStreamingHlodDistances distances,
            int tileSizeMeters,
            HashSet<string> want,
            List<LoadRequest> pending,
            HashSet<string> loading,
            HashSet<string> loadedKeys,
            HashSet<string> pendingKeys)
        {
            string proxyKey = BuildProxyKey(tile.TileId);
            if (tileDist <= distances.proxyUnloadMeters)
                want.Add(proxyKey);

            if (tileDist <= distances.proxyLoadMeters &&
                !loadedKeys.Contains(proxyKey) &&
                !loading.Contains(proxyKey) &&
                !pendingKeys.Contains(proxyKey))
            {
                if (!IsCoarserHlodReadyForTileProxy(
                        manifest,
                        tile,
                        tileDist,
                        distances,
                        tileSizeMeters,
                        loadedKeys,
                        loading,
                        pendingKeys))
                {
                    return;
                }

                pending.Add(new LoadRequest
                {
                    Key = proxyKey,
                    TileId = tile.TileId,
                    SubcellId = "tile_proxy",
                    BundleRel = tile.ProxyBundleRel,
                    LodLevel = SpatialStreamingLodLevel.TileProxy,
                    PriorityDistance = tileDist,
                });
            }
        }

        static void QueueSubcellProxy(
            SpatialDatasetManifest manifest,
            SpatialTileManifestEntry tile,
            SpatialSubcellManifestEntry subcell,
            float subcellDist,
            SpatialStreamingHlodDistances distances,
            int tileSizeMeters,
            HashSet<string> want,
            List<LoadRequest> pending,
            HashSet<string> loading,
            HashSet<string> loadedKeys,
            HashSet<string> pendingKeys)
        {
            string proxyKey = BuildSubcellProxyKey(tile.TileId, subcell.SubcellId);
            if (subcellDist <= distances.proxyUnloadMeters)
                want.Add(proxyKey);

            if (subcellDist <= distances.proxyLoadMeters &&
                !loadedKeys.Contains(proxyKey) &&
                !loading.Contains(proxyKey) &&
                !pendingKeys.Contains(proxyKey))
            {
                if (!IsCoarserHlodReadyForSubcellProxy(
                        manifest,
                        tile,
                        subcellDist,
                        distances,
                        tileSizeMeters,
                        loadedKeys,
                        loading,
                        pendingKeys))
                {
                    return;
                }

                pending.Add(new LoadRequest
                {
                    Key = proxyKey,
                    TileId = tile.TileId,
                    SubcellId = subcell.SubcellId,
                    BundleRel = subcell.ProxyBundleRel,
                    GridX = subcell.GridX,
                    GridY = subcell.GridY,
                    LodLevel = SpatialStreamingLodLevel.SubcellProxy,
                    PriorityDistance = subcellDist,
                });
            }
        }

        static bool IsCoarserLodReadyForDetail(
            bool enableHlod,
            SpatialTileManifestEntry tile,
            SpatialSubcellManifestEntry subcell,
            float subcellDist,
            SpatialStreamingHlodDistances distances,
            bool usesSubcellProxies,
            HashSet<string> loadedKeys,
            HashSet<string> loading)
        {
            if (!enableHlod || subcellDist > distances.detailLoadMeters)
                return true;

            if (usesSubcellProxies && !string.IsNullOrEmpty(subcell.ProxyBundleRel))
            {
                string proxyKey = BuildSubcellProxyKey(tile.TileId, subcell.SubcellId);
                return loadedKeys.Contains(proxyKey) || loading.Contains(proxyKey);
            }

            if (!usesSubcellProxies && !string.IsNullOrEmpty(tile.ProxyBundleRel) &&
                subcellDist <= distances.proxyLoadMeters)
            {
                string proxyKey = BuildProxyKey(tile.TileId);
                return loadedKeys.Contains(proxyKey) || loading.Contains(proxyKey);
            }

            return true;
        }

        static bool IsCoarserHlodReadyForTileProxy(
            SpatialDatasetManifest manifest,
            SpatialTileManifestEntry tile,
            float tileDist,
            SpatialStreamingHlodDistances distances,
            int tileSizeMeters,
            HashSet<string> loadedKeys,
            HashSet<string> loading,
            HashSet<string> pendingKeys)
        {
            if (tileDist > distances.hlod2LoadMeters)
                return true;

            if (!TryFindCoveringSupertileKey(tile, tileSizeMeters, factor: 2, out string hlod2Id))
                return true;

            if (manifest?.FindSupertile(hlod2Id) == null)
                return true;

            string hlod2Key = BuildSupertileKey(hlod2Id);
            return IsSupertileSatisfied(hlod2Key, loadedKeys, loading, pendingKeys);
        }

        static bool IsCoarserHlodReadyForSubcellProxy(
            SpatialDatasetManifest manifest,
            SpatialTileManifestEntry tile,
            float subcellDist,
            SpatialStreamingHlodDistances distances,
            int tileSizeMeters,
            HashSet<string> loadedKeys,
            HashSet<string> loading,
            HashSet<string> pendingKeys) =>
            IsCoarserHlodReadyForTileProxy(
                manifest,
                tile,
                subcellDist,
                distances,
                tileSizeMeters,
                loadedKeys,
                loading,
                pendingKeys);

        static bool IsCoarserHlodReadyForSupertile(
            SpatialDatasetManifest manifest,
            SpatialSupertileManifestEntry supertile,
            int tileSizeMeters,
            HashSet<string> loadedKeys,
            HashSet<string> loading,
            HashSet<string> pendingKeys)
        {
            if (supertile == null || supertile.Factor != 2)
                return true;

            int blockSize4 = tileSizeMeters * 4;
            int blockLeft4 = SpatialTileIdUtility.AlignDownMeters(supertile.Left, blockSize4);
            int blockBottom4 = SpatialTileIdUtility.AlignDownMeters(supertile.Bottom, blockSize4);
            string hlod4Id = SpatialStreamingPaths.GetSupertileId(4, blockLeft4, blockBottom4);
            if (manifest?.FindSupertile(hlod4Id) == null)
                return true;

            string hlod4Key = BuildSupertileKey(hlod4Id);
            return IsSupertileSatisfied(hlod4Key, loadedKeys, loading, pendingKeys);
        }

        static bool IsSupertileSatisfied(
            string supertileKey,
            HashSet<string> loadedKeys,
            HashSet<string> loading,
            HashSet<string> pendingKeys) =>
            loadedKeys.Contains(supertileKey) ||
            loading.Contains(supertileKey) ||
            pendingKeys.Contains(supertileKey);

        static bool TryFindCoveringSupertileKey(
            SpatialTileManifestEntry tile,
            int tileSizeMeters,
            int factor,
            out string supertileId)
        {
            supertileId = null;
            if (tile == null || !SpatialTileIdUtility.TryParse(tile.TileId, out int left, out int bottom))
                return false;

            int blockSizeMeters = tileSizeMeters * factor;
            int blockLeft = SpatialTileIdUtility.AlignDownMeters(left, blockSizeMeters);
            int blockBottom = SpatialTileIdUtility.AlignDownMeters(bottom, blockSizeMeters);
            supertileId = SpatialStreamingPaths.GetSupertileId(factor, blockLeft, blockBottom);
            return true;
        }

        public static string BuildDetailKey(string tileId, string subcellId) => $"{tileId}|{subcellId}";

        public static string BuildProxyKey(string tileId) => $"{tileId}|proxy";

        public static string BuildSubcellProxyKey(string tileId, string subcellId) => $"{tileId}|{subcellId}|proxy";

        public static string BuildSupertileKey(string supertileId) => $"{supertileId}|hlod";

        static bool SupertileHasDetailCompleteChild(
            SpatialSupertileManifestEntry supertile,
            HashSet<string> detailCompleteTileIds)
        {
            if (supertile?.ChildTileIds == null || detailCompleteTileIds == null || detailCompleteTileIds.Count == 0)
                return false;

            foreach (string tileId in supertile.ChildTileIds)
            {
                if (detailCompleteTileIds.Contains(tileId))
                    return true;
            }

            return false;
        }
    }
}
