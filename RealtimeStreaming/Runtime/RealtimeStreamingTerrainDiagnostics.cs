using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace ZGConnect.RealtimeStreaming
{
    /// <summary>
    /// One-shot and periodic console reports for terrain streaming failures.
    /// </summary>
    public sealed class RealtimeStreamingTerrainDiagnostics
    {
        readonly HashSet<string> _loggedKeys = new();
        float _nextCoverageReportTime;

        public bool Enabled { get; set; } = true;
        public float CoverageReportIntervalSeconds { get; set; } = 4f;

        public void Reset()
        {
            _loggedKeys.Clear();
            _nextCoverageReportTime = 0f;
        }

        public void LogOnce(string dedupeKey, string message, bool asError = true)
        {
            if (!Enabled || string.IsNullOrEmpty(dedupeKey) || !_loggedKeys.Add(dedupeKey))
                return;

            if (asError)
                Debug.LogError(message);
            else
                Debug.LogWarning(message);
        }

        public void ReportLoadFailed(
            RuntimeTileRecord record,
            string datasetRoot,
            string activeBasemapId,
            bool preferTerrainBundles,
            bool terrainBundleOnlyMode,
            string detail)
        {
            if (record == null)
                return;

            string describe = DescribeAvailability(
                record, datasetRoot, activeBasemapId, preferTerrainBundles, terrainBundleOnlyMode);
            LogOnce(
                $"load-fail:{record.Key}",
                $"[ZGConnect.Realtime] Terrain load failed for '{record.Key}' ({record.HlodFactor}x HLOD). " +
                $"{detail} {describe}");
        }

        public void ReportPeriodicCoverage(
            RealtimeStreamingController streamer,
            StreamingDatasetManifest manifest,
            string datasetRoot,
            Vector3 camPos,
            float loadRadius,
            HashSet<string> currentDesiredKeys,
            IReadOnlyDictionary<string, RuntimeTileRecord> loaded,
            IReadOnlyDictionary<string, RuntimeTileRecord> loading,
            System.Func<string, bool> isTerrainQueued)
        {
            if (!Enabled || streamer == null || manifest == null || Time.time < _nextCoverageReportTime)
                return;

            _nextCoverageReportTime = Time.time + CoverageReportIntervalSeconds;

            if (manifest.Tiles == null)
                return;

            int tileSize = manifest.TileSizeMeters;
            var hlodMetric = streamer.GetHlodDistanceMetricPublic();

            foreach (StreamingTileEntry leaf in manifest.Tiles)
            {
                float dist = HlodRingEvaluator.TileBoundaryDistance(
                    camPos, leaf.GetUnityPosition(), tileSize, hlodMetric);
                if (dist > loadRadius)
                    continue;

                var record = RuntimeTileRecord.FromLeaf(leaf, tileSize);
                if (streamer.IsTerrainDataAvailable(record))
                    continue;

                LogOnce(
                    $"no-terrain:{leaf.TileId}",
                    $"[ZGConnect.Realtime] Tile '{leaf.TileId}' is within load range ({dist:F0}m) " +
                    "but has no streamable terrain on disk. Re-pack this tile or restore its heightmap/bundle. " +
                    DescribeAvailability(
                        record,
                        datasetRoot,
                        streamer.ActiveBasemapId,
                        streamer.PreferTerrainBundles,
                        streamer.TerrainBundleOnlyMode));
            }

            if (currentDesiredKeys == null)
                return;

            foreach (string key in currentDesiredKeys)
            {
                if (loaded.ContainsKey(key) || loading.ContainsKey(key))
                    continue;
                if (isTerrainQueued != null && isTerrainQueued(key))
                    continue;

                LogOnce(
                    $"desired-not-loading:{key}",
                    $"[ZGConnect.Realtime] Tile '{key}' is required for coverage near the camera " +
                    "but is not loaded or loading (check maxLoadedTiles / concurrent load limits).",
                    asError: false);
            }
        }

        public static string DescribeAvailability(
            RuntimeTileRecord record,
            string datasetRoot,
            string activeBasemapId,
            bool preferTerrainBundles,
            bool terrainBundleOnlyMode = false)
        {
            if (record == null)
                return string.Empty;

            var sb = new StringBuilder();
            if (terrainBundleOnlyMode)
                sb.Append("bundle-only mode (no RAW fallback). ");

            string hmRel = record.IsSupertile
                ? record.Supertile?.HeightmapPath
                : record.Leaf?.HeightmapPath;

            if (terrainBundleOnlyMode)
            {
                // Heightmaps are not used in bundle-only mode.
            }
            else if (string.IsNullOrEmpty(hmRel))
            {
                sb.Append("manifest heightmapPath is empty. ");
            }
            else
            {
                string hmFull = Path.Combine(
                    datasetRoot, hmRel.Replace('/', Path.DirectorySeparatorChar));
                sb.Append(File.Exists(hmFull)
                    ? $"heightmap present ({hmRel}). "
                    : $"heightmap MISSING ({hmFull}). ");
            }

            Dictionary<string, string> bundlePaths = record.IsSupertile
                ? record.Supertile?.TerrainBundlePaths
                : record.Leaf?.TerrainBundlePaths;

            if (!preferTerrainBundles)
            {
                sb.Append("preferTerrainBundles is off. ");
                return sb.ToString().Trim();
            }

            if (string.IsNullOrEmpty(activeBasemapId))
            {
                sb.Append("activeBasemapId is empty. ");
                return sb.ToString().Trim();
            }

            if (bundlePaths == null || bundlePaths.Count == 0)
            {
                sb.Append("no terrainBundlePaths in manifest. ");
                return sb.ToString().Trim();
            }

            if (bundlePaths.TryGetValue(activeBasemapId, out string bundleRel) &&
                !string.IsNullOrEmpty(bundleRel))
            {
                string bundleFull = Path.Combine(
                    datasetRoot, bundleRel.Replace('/', Path.DirectorySeparatorChar));
                sb.Append(File.Exists(bundleFull)
                    ? $"bundle present ({bundleRel}). "
                    : $"bundle MISSING ({bundleFull}). ");
            }
            else
            {
                sb.Append($"no bundle path for basemap '{activeBasemapId}'. ");
            }

            return sb.ToString().Trim();
        }
    }
}
