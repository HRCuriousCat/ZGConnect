using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using ZGConnect.Editor;

namespace ZGConnect.RealtimeStreaming.Editor
{
    /// <summary>
    /// Resolves overview-map EPSG bounds from heightmap metadata (full dataset coverage).
    /// </summary>
    public static class RealtimeStreamingMapGeorefUtility
    {
        public const string ImporterPrefsPrefix = "ZGConnect.RealtimeImporter.";

        static string _cachedCoverageKey;
        static ZGConnectMapGeorefBounds _cachedCoverageBounds;
        static readonly Dictionary<string, List<string>> kHeightmapMetadataPathsByRoot = new();

        public static void InvalidateCaches()
        {
            _cachedCoverageKey = null;
            kHeightmapMetadataPathsByRoot.Clear();
            RealtimeStreamingManifestMapCache.Invalidate();
        }

        public static bool TryGetTileUnion(
            IReadOnlyList<HeightmapTileJson> tiles,
            out ZGConnectMapGeorefBounds bounds)
        {
            bounds = default;
            if (tiles == null || tiles.Count == 0)
                return false;

            int tMinE = int.MaxValue;
            int tMaxE = int.MinValue;
            int tMinN = int.MaxValue;
            int tMaxN = int.MinValue;

            foreach (HeightmapTileJson tile in tiles)
            {
                if (tile.Left < tMinE) tMinE = tile.Left;
                if (tile.Right > tMaxE) tMaxE = tile.Right;
                if (tile.Bottom < tMinN) tMinN = tile.Bottom;
                if (tile.Top > tMaxN) tMaxN = tile.Top;
            }

            if (tMaxE <= tMinE || tMaxN <= tMinN)
                return false;

            bounds = new ZGConnectMapGeorefBounds
            {
                MinE = tMinE,
                MaxE = tMaxE,
                MinN = tMinN,
                MaxN = tMaxN,
            };
            return true;
        }

        /// <summary>
        /// Georef for the map background image: full source heightmap metadata when known, else city placeholder.
        /// </summary>
        public static bool TryGetBackgroundMapGeoref(out ZGConnectMapGeorefBounds bounds)
        {
            if (TryGetHeightmapCoverageFromImporterPrefs(out bounds))
                return true;

            ZGConnectMapExtent.GetCityBoundingBox(out int minE, out int maxE, out int minN, out int maxN);
            bounds = new ZGConnectMapGeorefBounds
            {
                MinE = minE,
                MaxE = maxE,
                MinN = minN,
                MaxN = maxN,
            };
            return true;
        }

        public static bool TryGetHeightmapCoverageFromImporterPrefs(out ZGConnectMapGeorefBounds bounds)
        {
            bounds = default;
            string rootFolder = EditorPrefs.GetString(ImporterPrefsPrefix + "RootFolder", "");
            if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder))
                return false;

            int heightmapIdx = EditorPrefs.GetInt(ImporterPrefsPrefix + "HeightmapIdx", 0);
            string cacheKey = rootFolder + "|" + heightmapIdx;
            if (_cachedCoverageKey == cacheKey && _cachedCoverageBounds.IsValid)
            {
                bounds = _cachedCoverageBounds;
                return true;
            }

            if (!TryGetHeightmapMetadataPath(rootFolder, heightmapIdx, out string metadataPath))
                return false;

            if (!TryLoadHeightmapMetadata(metadataPath, out HeightmapMetadataJson metadata))
                return false;

            if (!TryGetTileUnion(metadata?.Tiles, out bounds))
                return false;

            _cachedCoverageKey = cacheKey;
            _cachedCoverageBounds = bounds;
            return true;
        }

        static bool TryGetHeightmapMetadataPath(string rootFolder, int heightmapIdx, out string metadataPath)
        {
            metadataPath = null;
            if (!kHeightmapMetadataPathsByRoot.TryGetValue(rootFolder, out List<string> paths))
            {
                paths = new List<string>();
                foreach (string folder in Directory.GetDirectories(rootFolder))
                {
                    string candidate = Path.Combine(folder, "metadata.json");
                    if (TryLoadHeightmapMetadata(candidate, out _))
                        paths.Add(candidate);
                }

                kHeightmapMetadataPathsByRoot[rootFolder] = paths;
            }

            if (paths.Count == 0)
                return false;

            heightmapIdx = Mathf.Clamp(heightmapIdx, 0, paths.Count - 1);
            metadataPath = paths[heightmapIdx];
            return !string.IsNullOrEmpty(metadataPath);
        }

        public static bool TryLoadHeightmapMetadata(
            string metadataPath,
            out HeightmapMetadataJson metadata)
        {
            metadata = null;
            if (string.IsNullOrEmpty(metadataPath) || !File.Exists(metadataPath))
                return false;

            try
            {
                metadata = JsonConvert.DeserializeObject<HeightmapMetadataJson>(File.ReadAllText(metadataPath));
                return metadata?.Tiles != null && metadata.Tiles.Count > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Caches manifest tile lists for the tile map (avoids disk I/O every IMGUI frame).</summary>
    public static class RealtimeStreamingManifestMapCache
    {
        struct Entry
        {
            public string ManifestPath;
            public long WriteTimeUtcTicks;
            public List<HeightmapTileJson> Tiles;
            public HashSet<string> TileIds;
        }

        static Entry _entry;

        public static void Invalidate() => _entry = default;

        public static List<HeightmapTileJson> GetTiles(
            string manifestRelativePath,
            System.Func<string, List<HeightmapTileJson>> loader)
        {
            if (TryGetFreshEntry(manifestRelativePath, loader, out Entry entry))
                return entry.Tiles;

            return loader(manifestRelativePath) ?? new List<HeightmapTileJson>();
        }

        public static HashSet<string> GetTileIds(
            string manifestRelativePath,
            System.Func<string, List<HeightmapTileJson>> tilesLoader,
            System.Func<string, HashSet<string>> idsLoader)
        {
            if (TryGetFreshEntry(manifestRelativePath, tilesLoader, out Entry entry))
                return entry.TileIds;

            return idsLoader(manifestRelativePath) ?? new HashSet<string>();
        }

        static bool TryGetFreshEntry(
            string manifestRelativePath,
            System.Func<string, List<HeightmapTileJson>> loader,
            out Entry entry)
        {
            entry = _entry;
            string path = ResolveManifestPath(manifestRelativePath);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;

            long writeTicks = File.GetLastWriteTimeUtc(path).Ticks;
            if (_entry.ManifestPath == path &&
                _entry.WriteTimeUtcTicks == writeTicks &&
                _entry.Tiles != null)
            {
                entry = _entry;
                return true;
            }

            List<HeightmapTileJson> tiles = loader(manifestRelativePath) ?? new List<HeightmapTileJson>();
            var tileIds = new HashSet<string>(tiles.Count);
            foreach (HeightmapTileJson tile in tiles)
                tileIds.Add($"{tile.Left}_{tile.Bottom}");

            _entry = new Entry
            {
                ManifestPath = path,
                WriteTimeUtcTicks = writeTicks,
                Tiles = tiles,
                TileIds = tileIds,
            };
            entry = _entry;
            return true;
        }

        static string ResolveManifestPath(string manifestRelativePath)
        {
            string rel = string.IsNullOrEmpty(manifestRelativePath)
                ? RuntimeStreamingPaths.DefaultManifestRelativePath
                : manifestRelativePath;
            return RuntimeStreamingPaths.ManifestPath(rel);
        }
    }
}
