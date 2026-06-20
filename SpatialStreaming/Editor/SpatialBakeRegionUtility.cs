using System.Collections.Generic;
using System.IO;
using ZGConnect.Editor;
using ZGConnect.RealtimeStreaming;
using ZGConnect.SpatialStreaming;

namespace ZGConnect.SpatialStreaming.Editor
{
    public static class SpatialBakeRegionUtility
    {
        static List<HeightmapTileJson> _cachedMapTiles;
        static int _cachedTileSizeMeters = 1000;
        static long _cachedParentManifestTimestamp;

        static HashSet<string> _cachedSpatialBakedTileIds;
        static long _cachedSpatialManifestTimestamp;

        public static void InvalidateMapCaches()
        {
            _cachedMapTiles = null;
            _cachedSpatialBakedTileIds = null;
            _cachedParentManifestTimestamp = 0;
            _cachedSpatialManifestTimestamp = 0;
        }

        public static bool TryLoadMapTiles(out List<HeightmapTileJson> tiles, out int tileSizeMeters, out string error)
        {
            tiles = new List<HeightmapTileJson>();
            tileSizeMeters = 1000;
            error = null;

            string manifestPath = SpatialStreamingPaths.ParentManifestPath();
            long manifestTimestamp = File.Exists(manifestPath)
                ? File.GetLastWriteTimeUtc(manifestPath).Ticks
                : 0;

            if (_cachedMapTiles != null && manifestTimestamp == _cachedParentManifestTimestamp)
            {
                tiles.AddRange(_cachedMapTiles);
                tileSizeMeters = _cachedTileSizeMeters;
                return tiles.Count > 0;
            }

            if (!SpatialParentManifestReader.TryLoad(
                    manifestPath,
                    out tileSizeMeters,
                    out _,
                    out List<SpatialParentTileInfo> parentTiles,
                    out error))
            {
                return false;
            }

            var loaded = new List<HeightmapTileJson>();
            foreach (SpatialParentTileInfo parentTile in parentTiles)
            {
                if (parentTile == null || string.IsNullOrEmpty(parentTile.TileId))
                    continue;

                if (!SpatialTileIdUtility.TryParse(parentTile.TileId, out int left, out int bottom))
                    continue;

                loaded.Add(new HeightmapTileJson
                {
                    Left = left,
                    Bottom = bottom,
                    Right = left + tileSizeMeters,
                    Top = bottom + tileSizeMeters,
                });
            }

            if (loaded.Count == 0)
            {
                error = "Parent manifest has no parseable tiles.";
                return false;
            }

            _cachedMapTiles = loaded;
            _cachedTileSizeMeters = tileSizeMeters;
            _cachedParentManifestTimestamp = manifestTimestamp;
            tiles.AddRange(loaded);
            return true;
        }

        public static bool TryGetOverviewBounds(out ZGConnectMapGeorefBounds bounds)
        {
            if (!TryLoadMapTiles(out List<HeightmapTileJson> tiles, out _, out _))
            {
                bounds = default;
                return false;
            }

            return TryGetTileUnion(tiles, out bounds);
        }

        public static bool TryGetTileUnion(IReadOnlyList<HeightmapTileJson> tiles, out ZGConnectMapGeorefBounds bounds)
        {
            bounds = default;
            if (tiles == null || tiles.Count == 0)
                return false;

            int minE = int.MaxValue;
            int maxE = int.MinValue;
            int minN = int.MaxValue;
            int maxN = int.MinValue;

            foreach (HeightmapTileJson tile in tiles)
            {
                if (tile.Left < minE)
                    minE = tile.Left;
                if (tile.Right > maxE)
                    maxE = tile.Right;
                if (tile.Bottom < minN)
                    minN = tile.Bottom;
                if (tile.Top > maxN)
                    maxN = tile.Top;
            }

            if (maxE <= minE || maxN <= minN)
                return false;

            bounds = new ZGConnectMapGeorefBounds
            {
                MinE = minE,
                MaxE = maxE,
                MinN = minN,
                MaxN = maxN,
            };
            return true;
        }

        public static HashSet<string> GetSpatialBakedTileIds()
        {
            string manifestPath = SpatialStreamingPaths.SpatialManifestPath();
            long manifestTimestamp = File.Exists(manifestPath)
                ? File.GetLastWriteTimeUtc(manifestPath).Ticks
                : 0;

            if (_cachedSpatialBakedTileIds != null && manifestTimestamp == _cachedSpatialManifestTimestamp)
                return _cachedSpatialBakedTileIds;

            var ids = new HashSet<string>();
            SpatialDatasetManifest manifest = SpatialDatasetManifest.LoadFromFile(manifestPath);
            if (manifest?.Tiles != null)
            {
                foreach (SpatialTileManifestEntry tile in manifest.Tiles)
                {
                    if (tile != null && !string.IsNullOrEmpty(tile.TileId))
                        ids.Add(tile.TileId);
                }
            }

            _cachedSpatialBakedTileIds = ids;
            _cachedSpatialManifestTimestamp = manifestTimestamp;
            return ids;
        }

        public static HashSet<string> GetSourceGlbTileIds(string sourceFolder = null)
        {
            var ids = new HashSet<string>();
            sourceFolder ??= SpatialStreamingPaths.BuildingMeshesSourceFolder;
            string folder = Path.Combine(SpatialStreamingPaths.DatasetRoot, sourceFolder);
            if (!Directory.Exists(folder))
                return ids;

            foreach (string path in Directory.EnumerateFiles(folder, "*.glb", SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileNameWithoutExtension(path);
                if (!fileName.StartsWith("buildings_"))
                    continue;

                string tileId = fileName.Substring("buildings_".Length);
                if (!string.IsNullOrEmpty(tileId))
                    ids.Add(tileId);
            }

            return ids;
        }

        public static void AlignRegionToHlodGrid(
            ref int minE,
            ref int maxE,
            ref int minN,
            ref int maxN,
            int tileSizeMeters)
        {
            if (tileSizeMeters <= 0)
                tileSizeMeters = 1000;

            HlodGridZones.AlignRegionEpsg(
                minE,
                maxE,
                minN,
                maxN,
                tileSizeMeters,
                HlodGridZones.PackRegionAlignFactor,
                out minE,
                out maxE,
                out minN,
                out maxN);
        }

        public static List<string> ResolveBakeTileIdsInRegion(
            int minE,
            int maxE,
            int minN,
            int maxN,
            string sourceFolder,
            out int tilesInRegion,
            out int tilesWithSourceGlb)
        {
            tilesInRegion = 0;
            tilesWithSourceGlb = 0;
            var result = new List<string>();

            if (maxE <= minE || maxN <= minN)
                return result;

            string manifestPath = SpatialStreamingPaths.ParentManifestPath();
            if (!SpatialParentManifestReader.TryLoad(
                    manifestPath,
                    out int tileSizeMeters,
                    out _,
                    out List<SpatialParentTileInfo> parentTiles,
                    out _))
            {
                return result;
            }

            string datasetRoot = SpatialStreamingPaths.DatasetRoot;
            sourceFolder ??= SpatialStreamingPaths.BuildingMeshesSourceFolder;

            foreach (SpatialParentTileInfo tile in parentTiles)
            {
                if (tile == null || string.IsNullOrEmpty(tile.TileId))
                    continue;

                if (!SpatialTileIdUtility.TryParse(tile.TileId, out int left, out int bottom))
                    continue;

                int right = left + tileSizeMeters;
                int top = bottom + tileSizeMeters;
                if (left >= maxE || right <= minE || bottom >= maxN || top <= minN)
                    continue;

                tilesInRegion++;

                string glbPath = Path.Combine(
                    datasetRoot,
                    sourceFolder,
                    SpatialStreamingPaths.GetBuildingGlbFileName(tile.TileId));

                if (!File.Exists(glbPath))
                    continue;

                tilesWithSourceGlb++;
                result.Add(tile.TileId);
            }

            return result;
        }

        public static int CountTilesInRegion(IReadOnlyList<HeightmapTileJson> tiles, int minE, int maxE, int minN, int maxN)
        {
            if (tiles == null || maxE <= minE || maxN <= minN)
                return 0;

            int count = 0;
            foreach (HeightmapTileJson tile in tiles)
            {
                if (tile.Left < maxE && tile.Right > minE &&
                    tile.Bottom < maxN && tile.Top > minN)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
