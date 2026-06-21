using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Profiling;
using ZGConnect;

namespace ZGConnect.SpatialStreaming
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(SpatialStreamingDetailCrossfade))]
    public sealed class SpatialStreamingController : MonoBehaviour
    {
        [Header("Dataset")]
        [SerializeField] string _manifestRelativePath = SpatialStreamingPaths.SpatialManifestRelativePath;
        [SerializeField] Transform _contentRoot;
        [SerializeField] Camera _targetCamera;

        [Header("HLOD")]
        [SerializeField] bool _enableHlod = true;

        [Header("LOD tile rings")]
        [Tooltip("Square of 1 km tiles with full detail. 1 = camera tile only, 2 = 3×3. 0 = skip.")]
        [SerializeField] int _detailRings = 2;
        [Tooltip("Exclusive tile rings outside detail — sub-cell footprint proxies. 0 = skip.")]
        [SerializeField] int _subcellProxyRings = 2;
        [Tooltip("Exclusive tile rings outside sub-cell proxy band — full tile proxies. 0 = skip.")]
        [SerializeField] int _tileProxyRings = 2;
        [Tooltip("Exclusive 1 km tile rings outside the tile-proxy band for HLOD2 (2×2 km blocks). 0 = skip.")]
        [SerializeField] int _hlod2x2Rings = 3;
        [Tooltip("Exclusive 1 km tile rings outside the HLOD2 band for HLOD4 (4×4 km blocks). 0 = skip.")]
        [SerializeField] int _hlod4x4Rings = 5;

        [Header("Streaming")]
        [Tooltip("How often to re-evaluate HLOD want/unload from camera distance. Does not throttle loading or instantiation.")]
        [SerializeField] float _checkInterval = 0.35f;
        [SerializeField] SpatialStreamingLoadBudget _loadBudget = SpatialStreamingLoadBudget.Default;

        SpatialStreamingTileRings _tileRings;

        [Header("Rendering")]
        [SerializeField] SpatialGpuResidentRenderingSettings _gpuSettings;
        [SerializeField] BuildingSurfaceSettings _buildingSurfaceSettings;
        [Tooltip("When enabled, remaps combined mesh materials on each detail subcell after load.")]
        [SerializeField] bool _applyMaterialsOnLoad = true;

        [Header("Debug")]
        [SerializeField] bool _showDebugHud = true;
        [SerializeField] bool _logStreaming;
        [Tooltip("Tint each streamed block by LOD level (detail=green, subcell proxy=cyan, tile proxy=yellow, HLOD2=orange, HLOD4=red).")]
        [SerializeField] bool _debugLodTint;
        [Tooltip("Uniform scale of the runtime debug HUD panel.")]
        [Range(0.25f, 2.5f)]
        [SerializeField] float _debugHudScale = 1f;

        [Header("Performance profiling")]
        [SerializeField] bool _enablePerformanceProfiling = true;
        [Tooltip("Log a CSV row when frame time exceeds this threshold during active streaming.")]
        [SerializeField] float _spikeFrameMsThreshold = 33.33f;
        [SerializeField] bool _logSpikesToCsv = true;
        [SerializeField] bool _logSpikesToConsole;

        [Header("Advanced LOD (optional)")]
        [Tooltip("Sort pending loads by projected screen size instead of raw distance.")]
        [SerializeField] bool _useScreenSpaceLodPriority;
        [SerializeField] float _screenSpaceFovDegrees = 60f;
        [Tooltip("Spread ring-window evaluate across frames when the active window is large.")]
        [SerializeField] bool _useTimeSlicedEvaluate;
        [SerializeField] int _timeSlicedEvaluateTilesPerFrame = 48;
        [SerializeField] int _timeSlicedEvaluateSupertilesPerFrame = 16;
        [Tooltip("0 = unlimited. When exceeded, farthest non-want blocks are queued for unload.")]
        [SerializeField] int _maxResidentBlocks;

        [Header("Detail crossfade")]
        [Tooltip("Dither crossfade when a detail subcell replaces its proxy.")]
        [SerializeField] bool _enableDetailCrossfade = true;
        [SerializeField] float _detailCrossfadeDurationSeconds = 0.12f;
        [SerializeField] int _detailCrossfadeMaxConcurrent = 16;
        [HideInInspector] [SerializeField] SpatialStreamingDetailCrossfade _detailCrossfade;

        SpatialDatasetManifest _manifest;
        SpatialDatasetRuntimeIndex _runtimeIndex;
        SpatialStreamingLoadedStateIndex _loadedStateIndex;
        SpatialStreamingCoverageRefCounts _coverage;
        SpatialStreamingHlodDag _hlodDag;
        readonly SpatialStreamingEvaluateSliceState _evaluateSliceState = new();
        SpatialStreamingDebugHud _debugHud;
        readonly SpatialStreamingPerformanceTracker _performanceTracker = new();
        string _datasetRoot;
        Transform _cam;
        float _nextCheck;
        SpatialStreamingStats _stats;
        int _totalInstantiated;
        int _totalLoadFailures;
        bool _datasetBundlesAvailable = true;
        string _lastLoadError;
        readonly Dictionary<string, float> _loadBlockedUntil = new();

        const float LoadFailureRetrySeconds = 30f;
        const float CommitRejectRetrySeconds = 15f;
        const float StaleLoadingTimeoutSeconds = 90f;

        readonly Dictionary<string, SpatialLoadedSubcellRecord> _loaded = new();
        readonly HashSet<string> _loading = new();
        readonly HashSet<string> _cancelledLoads = new();
        readonly Dictionary<string, float> _loadingStartedAt = new();
        readonly List<SpatialStreamingHlodEvaluator.LoadRequest> _pending = new();
        readonly HashSet<string> _loadedKeysScratch = new();
        readonly HashSet<string> _evaluateWantScratch = new(512);
        readonly HashSet<string> _evaluatePendingKeysScratch = new(512);
        readonly List<SpatialStreamingHlodEvaluator.LoadRequest> _evaluatedPendingScratch = new(128);
        readonly HashSet<string> _visibilityRefreshScratch = new(32);
        readonly HashSet<string> _pendingVisibilityKeys = new(64);
        readonly List<string> _visibilityQueueDrainScratch = new(64);
        readonly List<string> _fullVisibilityRefreshKeys = new(512);
        readonly HashSet<string> _pendingUnloadKeys = new(128);
        readonly List<string> _unloadDrainScratch = new(128);
        readonly HashSet<string> _wantSnapshot = new(512);
        readonly List<string> _evaluateLoadedKeySnapshot = new(512);
        readonly HashSet<string> _evaluateLoadedKeySnapshotSet = new(512);
        readonly Dictionary<string, SpatialStreamingHlodEvaluator.LoadRequest> _mergeEvaluatedByKeyScratch =
            new(128);
        readonly HashSet<string> _mergeExistingPendingKeysScratch = new(128);
        readonly List<string> _loadingCancelScratch = new(16);
        readonly HashSet<string> _detailCompleteTileIdsCache = new(32);
        int _evaluateSupersedeCursor;
        int _evaluateWantUnloadCursor;
        bool _evaluateCleanupActive;
        int _detailCompleteTileIdsRevision = -1;
        int _fullVisibilityRefreshCursor;
        int _fullVisibilityRefreshRevision = -1;
        Vector3 _lastEvaluateCamPos;
        bool _hasEvaluateCamPos;
        SpatialStreamingTileRingUtility.CameraTileGrid _lastEvaluateCameraTile;
        bool _hasEvaluateCameraTile;
        bool _evaluateFullRingPass = true;
        float _nextIdleEvaluate;
        float _nextStatsRefresh;
        Vector3 _lastVisibilityCamPos;
        bool _hasVisibilityCamPos;
        int _loadedRevision;
        int _lastEvaluateLoadedRevision = -1;
        int _lastVisibilityLoadedRevision;
        bool _pendingSortDirty = true;
        Vector3 _lastPendingSortCamPos;
        int _lodContextFrame = -1;
        SpatialStreamingLodSubstitution.Context _lodContext;
        const float MinEvaluateCameraMoveMeters = 2f;
        const float VisibilityCameraMoveMeters = 1f;
        const float StatsRefreshIntervalSeconds = 0.25f;
        const int VisibilityKeysPerFrameWhileLoading = 8;
        const int VisibilityWorkBudgetPerFrame = 32;
        const int UnloadsPerFrame = 4;
        const int UnloadsPerFrameWhenBacklogged = 2;
        const int UnloadBacklogThreshold = 32;
        const int EvaluateCleanupRecordsPerFrame = 24;

        public SpatialDatasetManifest Manifest => _manifest;
        public IReadOnlyDictionary<string, SpatialLoadedSubcellRecord> LoadedBlocks => _loaded;
        public SpatialStreamingStats Stats => _stats;
        public SpatialStreamingPerformanceStats PerformanceStats => _performanceTracker.Stats;
        public bool EnableHlod => _enableHlod;

        void Awake()
        {
            if (_targetCamera == null)
                _targetCamera = Camera.main;

            if (_contentRoot == null)
            {
                var rootGo = new GameObject("SpatialContent");
                rootGo.transform.SetParent(transform, false);
                _contentRoot = rootGo.transform;
            }

            _datasetRoot = SpatialStreamingPaths.DatasetRoot;
            SyncTileRings();
            EnsureDetailCrossfade();
            LoadManifest();
        }

        void EnsureDetailCrossfade()
        {
            if (_detailCrossfade == null)
                _detailCrossfade = GetComponent<SpatialStreamingDetailCrossfade>();

            if (_detailCrossfade == null)
                _detailCrossfade = gameObject.AddComponent<SpatialStreamingDetailCrossfade>();

            SyncDetailCrossfadeSettings();
        }

        void SyncDetailCrossfadeSettings()
        {
            if (_detailCrossfade == null)
                return;

            _detailCrossfade.Configure(_detailCrossfadeDurationSeconds, _detailCrossfadeMaxConcurrent);
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            SyncTileRings();
            EnsureDetailCrossfade();
            SyncDebugHudSettings();
        }
#endif

        void Start()
        {
            _cam = _targetCamera != null ? _targetCamera.transform : null;
            if (_manifest == null)
            {
                Debug.LogError(
                    "[ZGConnect.Spatial] No spatial_manifest.json — bake via ZG Connect → Spatial Streaming.");
                return;
            }

            if (_logStreaming)
            {
                Debug.Log(
                    $"[ZGConnect.Spatial] Ready: {_manifest.Tiles?.Count ?? 0} tile(s), " +
                    $"supertiles={_manifest.Supertiles?.Count ?? 0}, subcell={_manifest.SubcellSizeMeters}m");
            }

            ValidateDatasetBundles();
            EnsureDebugHud();
            KickInitialStreamingEvaluation();
        }

        void KickInitialStreamingEvaluation()
        {
            if (_manifest == null || _cam == null)
                return;

            _nextCheck = 0f;
            SyncTileRings();
            _lastEvaluateCamPos = _cam.position;
            _hasEvaluateCamPos = true;
            EvaluateStreaming();
            ProcessPendingLoads();
            UpdateLodVisibility();
        }

        void ValidateDatasetBundles()
        {
            string bundlesRoot = SpatialStreamingPaths.SpatialBundlesRoot;
            if (!Directory.Exists(bundlesRoot))
            {
                _datasetBundlesAvailable = false;
                _lastLoadError = $"bundles_spatial folder missing: {bundlesRoot}";
                Debug.LogError(
                    "[ZGConnect.Spatial] No bundles_spatial/ folder — spatial_manifest.json references bundles " +
                    "that are not on disk. Open ZG Connect → Spatial Streaming and run Bake.");
                return;
            }

            if (!TryFindSampleBundlePath(out string sampleRel, out string sampleFull) ||
                SpatialStreamingPaths.FileExists(sampleFull))
            {
                return;
            }

            _datasetBundlesAvailable = false;
            _lastLoadError = $"Sample bundle missing: {sampleFull}";
            int manifestTiles = _manifest.Tiles?.Count ?? 0;
            int tilesOnDisk = CountTilesWithBundlesOnDisk();
            Debug.LogError(
                $"[ZGConnect.Spatial] bundles_spatial/ exists but manifest bundle '{sampleRel}' was not found. " +
                $"Manifest lists {manifestTiles} tile(s); ~{tilesOnDisk} have bundle folders on disk. " +
                "Open ZG Connect → Spatial Streaming and use 'Sync manifest to bundles on disk', " +
                "or re-run Spatial Streaming Bake for the tiles you need.");
        }

        static int CountTilesWithBundlesOnDisk()
        {
            string bundlesRoot = SpatialStreamingPaths.SpatialBundlesRoot;
            if (!Directory.Exists(bundlesRoot))
                return 0;

            int count = 0;
            foreach (string tileDir in Directory.GetDirectories(bundlesRoot))
            {
                string name = Path.GetFileName(tileDir);
                if (string.IsNullOrEmpty(name) || name.StartsWith("hlod", System.StringComparison.OrdinalIgnoreCase))
                    continue;

                if (Directory.EnumerateFiles(tileDir).Any(path =>
                        !path.EndsWith(".meta", System.StringComparison.OrdinalIgnoreCase)))
                {
                    count++;
                }
            }

            return count;
        }

        bool TryFindSampleBundlePath(out string bundleRel, out string bundleFull)
        {
            bundleRel = null;
            bundleFull = null;
            if (_manifest?.Tiles == null)
                return false;

            foreach (SpatialTileManifestEntry tile in _manifest.Tiles)
            {
                if (tile == null)
                    continue;

                if (tile.UsesSubcells && tile.Subcells != null)
                {
                    foreach (SpatialSubcellManifestEntry subcell in tile.Subcells)
                    {
                        if (subcell == null || string.IsNullOrEmpty(subcell.BundleRel))
                            continue;

                        bundleRel = subcell.BundleRel;
                        bundleFull = SpatialStreamingPaths.ResolveDatasetRelativePath(bundleRel);
                        return true;
                    }
                }

                if (!string.IsNullOrEmpty(tile.CoarseBundleRel))
                {
                    bundleRel = tile.CoarseBundleRel;
                    bundleFull = SpatialStreamingPaths.ResolveDatasetRelativePath(bundleRel);
                    return true;
                }

                if (!string.IsNullOrEmpty(tile.ProxyBundleRel))
                {
                    bundleRel = tile.ProxyBundleRel;
                    bundleFull = SpatialStreamingPaths.ResolveDatasetRelativePath(bundleRel);
                    return true;
                }
            }

            if (_manifest.Supertiles != null)
            {
                foreach (SpatialSupertileManifestEntry supertile in _manifest.Supertiles)
                {
                    if (supertile == null || string.IsNullOrEmpty(supertile.BundleRel))
                        continue;

                    bundleRel = supertile.BundleRel;
                    bundleFull = SpatialStreamingPaths.ResolveDatasetRelativePath(bundleRel);
                    return true;
                }
            }

            return false;
        }

        void Update()
        {
            if (_enablePerformanceProfiling)
                _performanceTracker.BeginFrame();

            if (_manifest == null || _cam == null)
                return;

            PruneStaleLoading();

            Profiler.BeginSample("SpatialStreaming.ProcessPendingLoads");
            ProcessPendingLoads();
            Profiler.EndSample();

            int visibilityBudget = _loading.Count > 0
                ? VisibilityKeysPerFrameWhileLoading
                : VisibilityWorkBudgetPerFrame;
            visibilityBudget -= ProcessVisibilityQueue(visibilityBudget);
            UpdateLodVisibility(visibilityBudget);

            ProcessEvaluateCleanup(EvaluateCleanupRecordsPerFrame);

            if (Time.time >= _nextCheck)
            {
                _nextCheck = Time.time + Mathf.Max(0.05f, _checkInterval);

                if (ShouldRunStreamingEvaluate())
                {
                    _lastEvaluateCamPos = _cam.position;
                    _hasEvaluateCamPos = true;

                    Profiler.BeginSample("SpatialStreaming.Evaluate");
                    EvaluateStreaming();
                    Profiler.EndSample();
                }
            }

            if (ShouldRefreshStats())
                RefreshStats();

            ProcessPendingUnloads(ResolveUnloadsPerFrame());
        }

        bool ShouldRefreshStats()
        {
            if (_showDebugHud)
                return true;

            return _enablePerformanceProfiling && Time.unscaledTime >= _nextStatsRefresh;
        }

        void MarkStatsRefreshed() =>
            _nextStatsRefresh = Time.unscaledTime + StatsRefreshIntervalSeconds;

        bool NeedsFullVisibilityRefresh()
        {
            if (_loaded.Count == 0 || _loading.Count > 0)
                return false;

            if (_loadedRevision != _lastVisibilityLoadedRevision)
                return true;

            if (!_hasVisibilityCamPos)
                return true;

            return (_cam.position - _lastVisibilityCamPos).sqrMagnitude >=
                   VisibilityCameraMoveMeters * VisibilityCameraMoveMeters;
        }

        bool IsFullVisibilityRefreshInProgress() =>
            _fullVisibilityRefreshCursor < _fullVisibilityRefreshKeys.Count;

        void BeginFullVisibilityRefresh()
        {
            _fullVisibilityRefreshKeys.Clear();
            foreach (KeyValuePair<string, SpatialLoadedSubcellRecord> kvp in _loaded)
                _fullVisibilityRefreshKeys.Add(kvp.Key);

            _fullVisibilityRefreshCursor = 0;
            _fullVisibilityRefreshRevision = _loadedRevision;
            _pendingVisibilityKeys.Clear();
        }

        void MarkLodVisibilityRefreshed()
        {
            _lastVisibilityLoadedRevision = _loadedRevision;
            _lastVisibilityCamPos = _cam.position;
            _hasVisibilityCamPos = true;
        }

        void MarkLoadedStateDirty()
        {
            _loadedRevision++;
            _pendingSortDirty = true;
            _lodContextFrame = -1;
            _detailCompleteTileIdsRevision = -1;
        }

        SpatialStreamingLodSubstitution.Context GetOrBuildLodContext()
        {
            if (_lodContextFrame != Time.frameCount)
            {
                _lodContext = BuildLodContext();
                _lodContextFrame = Time.frameCount;
            }

            return _lodContext;
        }

        bool ShouldRunStreamingEvaluate()
        {
            if (_loaded.Count == 0 || !_hasEvaluateCamPos)
                return true;

            if (_loadedRevision != _lastEvaluateLoadedRevision)
            {
                _evaluateFullRingPass = true;
                return true;
            }

            if (_loading.Count > 0 || _pending.Count > 0)
            {
                _evaluateFullRingPass = false;
                return false;
            }

            if (HasExpiredLoadBlocks())
            {
                _evaluateFullRingPass = false;
                return true;
            }

            var cameraTile = default(SpatialStreamingTileRingUtility.CameraTileGrid);
            bool hasCameraTile = _runtimeIndex != null
                ? _runtimeIndex.TryResolveCameraTileGrid(_cam.position, out cameraTile)
                : SpatialStreamingTileRingUtility.TryResolveCameraTileGrid(_manifest, _cam.position, out cameraTile);

            if (hasCameraTile && _hasEvaluateCameraTile &&
                (cameraTile.GridX != _lastEvaluateCameraTile.GridX ||
                 cameraTile.GridZ != _lastEvaluateCameraTile.GridZ))
            {
                _evaluateFullRingPass = true;
                return true;
            }

            float moveThreshold = Mathf.Max(
                MinEvaluateCameraMoveMeters,
                (_manifest?.SubcellSizeMeters ?? 250) * 0.1f);
            if ((_cam.position - _lastEvaluateCamPos).sqrMagnitude >= moveThreshold * moveThreshold)
            {
                _evaluateFullRingPass = true;
                return true;
            }

            if (Time.time < _nextIdleEvaluate)
                return false;

            _nextIdleEvaluate = Time.time + Mathf.Max(0.15f, _checkInterval);
            _evaluateFullRingPass = false;
            return false;
        }

        bool HasExpiredLoadBlocks()
        {
            if (_loadBlockedUntil.Count == 0)
                return false;

            float now = Time.time;
            foreach (KeyValuePair<string, float> blocked in _loadBlockedUntil)
            {
                if (now >= blocked.Value)
                    return true;
            }

            return false;
        }

        void BlockLoadCommitReject(string key)
        {
            if (string.IsNullOrEmpty(key))
                return;

            _loadBlockedUntil[key] = Time.time + CommitRejectRetrySeconds;
        }

        void LateUpdate()
        {
            if (_detailCrossfade != null && _detailCrossfade.HasActiveTransitions && _cam != null)
                UpdateCrossfadeVisibility();

            if (!_enablePerformanceProfiling)
                return;

            _performanceTracker.EndFrame(
                Time.unscaledDeltaTime,
                _stats,
                _logSpikesToCsv,
                _logSpikesToConsole,
                _spikeFrameMsThreshold);
        }

        SpatialStreamingLodSubstitution.Context BuildLodContext()
        {
            Vector3 camPos = _cam != null ? _cam.position : Vector3.zero;
            var cameraTile = default(SpatialStreamingTileRingUtility.CameraTileGrid);
            if (_manifest != null)
            {
                SpatialStreamingTileRingUtility.TryResolveCameraTileGrid(
                    _manifest,
                    _runtimeIndex,
                    camPos,
                    out cameraTile);
            }

            _loadedKeysScratch.Clear();
            foreach (string key in _loaded.Keys)
                _loadedKeysScratch.Add(key);

            _evaluateSliceState.Enabled = _useTimeSlicedEvaluate && !_evaluateFullRingPass;
            _evaluateSliceState.TileBudgetPerEvaluate = Mathf.Max(1, _timeSlicedEvaluateTilesPerFrame);
            _evaluateSliceState.SupertileBudgetPerEvaluate = Mathf.Max(1, _timeSlicedEvaluateSupertilesPerFrame);

            return new SpatialStreamingLodSubstitution.Context
            {
                Manifest = _manifest,
                RuntimeIndex = _runtimeIndex,
                LoadedState = _loadedStateIndex,
                Coverage = _coverage,
                Dag = _hlodDag,
                CameraPosition = camPos,
                Rings = _tileRings,
                CameraTile = cameraTile,
                EnableHlod = _enableHlod,
                LoadedKeys = _loadedKeysScratch,
                LoadingKeys = _loading,
                LoadedRecords = _loaded,
                Hlod2ByBlock = _loadedStateIndex?.Hlod2ByBlock,
                Hlod4ByBlock = _loadedStateIndex?.Hlod4ByBlock,
                UseScreenSpaceLodPriority = _useScreenSpaceLodPriority,
                ScreenSpaceFovDegrees = _screenSpaceFovDegrees,
                EvaluateSlice = _evaluateSliceState,
            };
        }

        HashSet<string> GetDetailCompleteTileIdsCached()
        {
            if (_detailCompleteTileIdsRevision == _loadedRevision)
                return _detailCompleteTileIdsCache;

            _detailCompleteTileIdsCache.Clear();
            if (_manifest?.Tiles == null || _cam == null)
            {
                _detailCompleteTileIdsRevision = _loadedRevision;
                return _detailCompleteTileIdsCache;
            }

            if (!SpatialStreamingTileRingUtility.TryResolveCameraTileGrid(
                    _manifest,
                    _runtimeIndex,
                    _cam.position,
                    out var cameraTile))
            {
                _detailCompleteTileIdsRevision = _loadedRevision;
                return _detailCompleteTileIdsCache;
            }

            int maxRing = _tileRings.FurthestConfiguredRingEnd + 1;

            IEnumerable<SpatialTileManifestEntry> tiles = _runtimeIndex != null
                ? _runtimeIndex.CollectTilesInRing(cameraTile, maxRing + 1)
                : _manifest.Tiles;

            foreach (SpatialTileManifestEntry tile in tiles ?? Enumerable.Empty<SpatialTileManifestEntry>())
            {
                if (tile == null || string.IsNullOrEmpty(tile.TileId))
                    continue;

                if (IsTileDetailComplete(tile.TileId))
                    _detailCompleteTileIdsCache.Add(tile.TileId);
            }

            _detailCompleteTileIdsRevision = _loadedRevision;
            return _detailCompleteTileIdsCache;
        }

        void BeginEvaluateCleanup(HashSet<string> want)
        {
            _wantSnapshot.Clear();
            foreach (string key in want)
                _wantSnapshot.Add(key);

            if (!_evaluateCleanupActive)
            {
                _evaluateLoadedKeySnapshot.Clear();
                _evaluateLoadedKeySnapshotSet.Clear();
                foreach (string key in _loaded.Keys)
                {
                    _evaluateLoadedKeySnapshotSet.Add(key);
                    _evaluateLoadedKeySnapshot.Add(key);
                }

                _evaluateSupersedeCursor = 0;
                _evaluateWantUnloadCursor = 0;
                _evaluateCleanupActive = _evaluateLoadedKeySnapshot.Count > 0;
                return;
            }

            foreach (string key in _loaded.Keys)
            {
                if (_evaluateLoadedKeySnapshotSet.Add(key))
                    _evaluateLoadedKeySnapshot.Add(key);
            }
        }

        int ResolveUnloadsPerFrame()
        {
            if (_pendingUnloadKeys.Count >= UnloadBacklogThreshold)
                return UnloadsPerFrameWhenBacklogged;

            return UnloadsPerFrame;
        }

        void ProcessEvaluateCleanup(int recordBudget)
        {
            if (!_evaluateCleanupActive || recordBudget <= 0)
                return;

            Profiler.BeginSample("SpatialStreaming.EvaluateCleanup");
            SpatialStreamingLodSubstitution.Context ctx = GetOrBuildLodContext();
            int processed = 0;

            while (_evaluateSupersedeCursor < _evaluateLoadedKeySnapshot.Count && processed < recordBudget)
            {
                string key = _evaluateLoadedKeySnapshot[_evaluateSupersedeCursor++];
                if (!_loaded.TryGetValue(key, out SpatialLoadedSubcellRecord record) || record == null)
                    continue;

                processed++;
                switch (record.LodLevel)
                {
                    case SpatialStreamingLodLevel.SubcellProxy:
                        if (SpatialStreamingLodSubstitution.ShouldKeepSubcellProxyRecord(ctx, record))
                            break;

                        if (_detailCrossfade != null && _detailCrossfade.IsTransitioningProxy(key))
                            break;

                        EnqueueUnload(key);
                        break;

                    case SpatialStreamingLodLevel.TileProxy:
                        if (SpatialStreamingLodSubstitution.ShouldKeepTileProxy(ctx, record.TileId))
                            break;

                        EnqueueUnload(key);
                        break;
                }
            }

            bool skipWantUnload = _useTimeSlicedEvaluate && !_evaluateFullRingPass;
            if (!skipWantUnload)
            {
                while (_evaluateWantUnloadCursor < _evaluateLoadedKeySnapshot.Count && processed < recordBudget)
                {
                    string key = _evaluateLoadedKeySnapshot[_evaluateWantUnloadCursor++];
                    if (_wantSnapshot.Contains(key) || _loading.Contains(key))
                    {
                        if (_pendingUnloadKeys.Remove(key))
                            EnqueueVisibilityKey(key);
                        continue;
                    }

                    if (_loaded.TryGetValue(key, out SpatialLoadedSubcellRecord record) && record != null)
                    {
                        switch (record.LodLevel)
                        {
                            case SpatialStreamingLodLevel.SubcellProxy:
                                if (SpatialStreamingLodSubstitution.ShouldKeepSubcellProxyRecord(ctx, record))
                                    continue;
                                break;
                            case SpatialStreamingLodLevel.TileProxy:
                                if (SpatialStreamingLodSubstitution.ShouldKeepTileProxy(ctx, record.TileId))
                                    continue;
                                break;
                        }
                    }

                    processed++;
                    EnqueueUnload(key);
                }
            }

            if (_evaluateSupersedeCursor >= _evaluateLoadedKeySnapshot.Count &&
                (skipWantUnload || _evaluateWantUnloadCursor >= _evaluateLoadedKeySnapshot.Count))
            {
                _evaluateCleanupActive = false;
                _evaluateLoadedKeySnapshotSet.Clear();
            }

            Profiler.EndSample();
        }

        void SyncTileRings()
        {
            _tileRings = new SpatialStreamingTileRings
            {
                detailRings = Mathf.Max(0, _detailRings),
                subcellProxyRings = Mathf.Max(0, _subcellProxyRings),
                tileProxyRings = Mathf.Max(0, _tileProxyRings),
                hlod2x2Rings = Mathf.Max(0, _hlod2x2Rings),
                hlod4x4Rings = Mathf.Max(0, _hlod4x4Rings),
            };
        }

        public void LoadManifest()
        {
            string path = string.IsNullOrEmpty(_manifestRelativePath)
                ? SpatialStreamingPaths.SpatialManifestPath()
                : Path.Combine(
                    SpatialStreamingPaths.StreamingAssetsRoot,
                    _manifestRelativePath.Replace('/', Path.DirectorySeparatorChar));

            _manifest = SpatialDatasetManifest.LoadFromFile(path);
            RebuildRuntimeIndexes();
        }

        void RebuildRuntimeIndexes()
        {
            _runtimeIndex = SpatialDatasetRuntimeIndex.Build(_manifest);
            _hlodDag = SpatialStreamingHlodDag.Build(_runtimeIndex, _manifest);
            _loadedStateIndex = new SpatialStreamingLoadedStateIndex(_runtimeIndex);
            _coverage = new SpatialStreamingCoverageRefCounts(_runtimeIndex, _loadedStateIndex, _hlodDag);
            _loadedStateIndex.RebuildFromLoaded(_loaded);
            _coverage.RebuildAll();
            _lodContextFrame = -1;
        }

        void EvaluateStreaming()
        {
            Vector3 camPos = _cam.position;
            _evaluateWantScratch.Clear();
            _evaluatePendingKeysScratch.Clear();
            foreach (SpatialStreamingHlodEvaluator.LoadRequest request in _pending)
                _evaluatePendingKeysScratch.Add(request.Key);

            foreach (string loadingKey in _loading)
                _evaluatePendingKeysScratch.Add(loadingKey);

            PruneExpiredLoadBlocks();
            foreach (KeyValuePair<string, float> blocked in _loadBlockedUntil)
                _evaluatePendingKeysScratch.Add(blocked.Key);

            _evaluatedPendingScratch.Clear();
            SpatialStreamingLodSubstitution.Context ctx = BuildLodContext();

            var loadedKeys = ctx.LoadedKeys;
            HashSet<string> detailCompleteTileIds = GetDetailCompleteTileIdsCached();
            SpatialStreamingHlodEvaluator.Evaluate(
                _manifest,
                camPos,
                _tileRings,
                _enableHlod,
                detailCompleteTileIds,
                _evaluateWantScratch,
                _evaluatedPendingScratch,
                _loading,
                loadedKeys,
                _evaluatePendingKeysScratch,
                _loaded,
                ctx);

            MergePendingList(_evaluatedPendingScratch, _evaluateWantScratch, ctx);
            StripCoarseRequestsSupersededByDetail(_evaluateWantScratch, _pending);
            EnsureSubstitutionWant(_evaluateWantScratch);

            BeginEvaluateCleanup(_evaluateWantScratch);

            if (_maxResidentBlocks > 0)
            {
                SpatialStreamingResidencyBudget.EnforceBudget(
                    _maxResidentBlocks,
                    _loaded,
                    _evaluateWantScratch,
                    camPos,
                    EnqueueUnload);
            }

            if (_runtimeIndex != null)
                _runtimeIndex.TryResolveCameraTileGrid(camPos, out _lastEvaluateCameraTile);
            else
                SpatialStreamingTileRingUtility.TryResolveCameraTileGrid(_manifest, camPos, out _lastEvaluateCameraTile);

            _hasEvaluateCameraTile = true;
            _evaluateFullRingPass = false;

            _loadingCancelScratch.Clear();
            foreach (string key in _loading)
                _loadingCancelScratch.Add(key);

            foreach (string key in _loadingCancelScratch)
            {
                if (!_evaluateWantScratch.Contains(key))
                    _cancelledLoads.Add(key);
            }

            _lastEvaluateLoadedRevision = _loadedRevision;
        }

        void EnsureSubstitutionWant(HashSet<string> want)
        {
            foreach (SpatialLoadedSubcellRecord record in _loaded.Values)
            {
                if (record == null || record.LodLevel != SpatialStreamingLodLevel.Detail)
                    continue;

                string tileId = record.TileId;
                string subcellId = record.SubcellId;
                if (string.IsNullOrEmpty(tileId) || string.IsNullOrEmpty(subcellId))
                {
                    if (!SpatialStreamingLodSubstitution.TryParseDetailKey(record.Key, out tileId, out subcellId))
                        continue;
                }

                want.Remove(SpatialStreamingHlodEvaluator.BuildSubcellProxyKey(tileId, subcellId));
            }
        }

        void StripCoarseRequestsSupersededByDetail(
            HashSet<string> want,
            List<SpatialStreamingHlodEvaluator.LoadRequest> pending)
        {
            if (_loaded.Count == 0)
                return;

            foreach (SpatialLoadedSubcellRecord record in _loaded.Values)
            {
                if (record == null || record.LodLevel != SpatialStreamingLodLevel.Detail)
                    continue;

                string tileId = record.TileId;
                string subcellId = record.SubcellId;
                if (string.IsNullOrEmpty(tileId) || string.IsNullOrEmpty(subcellId))
                {
                    if (!SpatialStreamingLodSubstitution.TryParseDetailKey(record.Key, out tileId, out subcellId))
                        continue;
                }

                string proxyKey = SpatialStreamingHlodEvaluator.BuildSubcellProxyKey(tileId, subcellId);
                want.Remove(proxyKey);
                CancelLoadRequest(proxyKey);

                if (pending != null)
                {
                    for (int i = pending.Count - 1; i >= 0; i--)
                    {
                        if (pending[i].Key == proxyKey)
                            pending.RemoveAt(i);
                    }
                }
            }
        }

        void EnqueueUnload(string key)
        {
            if (string.IsNullOrEmpty(key) || !_loaded.ContainsKey(key))
                return;

            _pendingUnloadKeys.Add(key);
        }

        void ProcessPendingUnloads(int maxUnloads)
        {
            if (maxUnloads <= 0 || _pendingUnloadKeys.Count == 0)
                return;

            Profiler.BeginSample("SpatialStreaming.ProcessPendingUnloads");

            _unloadDrainScratch.Clear();
            foreach (string key in _pendingUnloadKeys)
            {
                _unloadDrainScratch.Add(key);
                if (_unloadDrainScratch.Count >= maxUnloads)
                    break;
            }

            for (int i = 0; i < _unloadDrainScratch.Count; i++)
            {
                string key = _unloadDrainScratch[i];
                if (!_pendingUnloadKeys.Remove(key))
                    continue;

                if (!_loaded.ContainsKey(key))
                    continue;

                if (_loaded.TryGetValue(key, out SpatialLoadedSubcellRecord record) &&
                    record?.Root != null &&
                    record.Root.activeSelf)
                {
                    record.Root.SetActive(false);
                }

                UnloadBlock(key);
            }

            Profiler.EndSample();
        }

        void CancelLoadRequest(string key)
        {
            if (string.IsNullOrEmpty(key))
                return;

            _cancelledLoads.Add(key);

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (_pending[i].Key == key)
                    _pending.RemoveAt(i);
            }
        }

        const int MaxNewPendingPerEvaluate = 64;
        const int MaxPendingBeforeCoarseThrottle = 96;
        const int MaxCoarseAddsWhenBacklogged = 8;

        void MergePendingList(
            List<SpatialStreamingHlodEvaluator.LoadRequest> evaluated,
            HashSet<string> want,
            SpatialStreamingLodSubstitution.Context ctx)
        {
            evaluated ??= _evaluatedPendingScratch;
            if (ctx.Manifest == null)
                ctx = BuildLodContext();

            _mergeEvaluatedByKeyScratch.Clear();
            foreach (SpatialStreamingHlodEvaluator.LoadRequest request in evaluated)
            {
                if (!_mergeEvaluatedByKeyScratch.ContainsKey(request.Key))
                    _mergeEvaluatedByKeyScratch[request.Key] = request;
            }

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                SpatialStreamingHlodEvaluator.LoadRequest request = _pending[i];
                if (!want.Contains(request.Key) ||
                    _loaded.ContainsKey(request.Key) ||
                    _loading.Contains(request.Key))
                {
                    _pending.RemoveAt(i);
                    continue;
                }

                if (_mergeEvaluatedByKeyScratch.TryGetValue(request.Key, out SpatialStreamingHlodEvaluator.LoadRequest fresh))
                    _pending[i] = fresh;
            }

            _mergeExistingPendingKeysScratch.Clear();
            foreach (SpatialStreamingHlodEvaluator.LoadRequest request in _pending)
                _mergeExistingPendingKeysScratch.Add(request.Key);

            evaluated.Sort((a, b) => SpatialStreamingLodSubstitution.CompareLoadRequests(ctx, a, b));

            bool backlog = _pending.Count >= MaxPendingBeforeCoarseThrottle;
            int added = 0;
            int coarseAdded = 0;
            foreach (SpatialStreamingHlodEvaluator.LoadRequest request in evaluated)
            {
                if (added >= MaxNewPendingPerEvaluate)
                    break;

                if (!want.Contains(request.Key))
                    continue;

                if (_loaded.ContainsKey(request.Key) ||
                    _loading.Contains(request.Key) ||
                    _mergeExistingPendingKeysScratch.Contains(request.Key))
                {
                    continue;
                }

                if (request.LodLevel == SpatialStreamingLodLevel.SubcellProxy &&
                    SpatialStreamingLodSubstitution.HasDetailRecord(ctx, request.TileId, request.SubcellId))
                {
                    continue;
                }

                if (backlog &&
                    request.LodLevel != SpatialStreamingLodLevel.Detail &&
                    coarseAdded >= MaxCoarseAddsWhenBacklogged)
                {
                    continue;
                }

                if (IsLoadBlocked(request.Key) ||
                    !SpatialStreamingLodSubstitution.ShouldQueueLoadRequest(ctx, request))
                {
                    continue;
                }

                _pending.Add(request);
                _mergeExistingPendingKeysScratch.Add(request.Key);
                added++;
                _pendingSortDirty = true;
                if (request.LodLevel != SpatialStreamingLodLevel.Detail)
                    coarseAdded++;
            }
        }

        bool IsLoadBlocked(string key) =>
            _loadBlockedUntil.TryGetValue(key, out float blockedUntil) && Time.time < blockedUntil;

        bool ShouldAcceptLoadedBlock(SpatialStreamingHlodEvaluator.LoadRequest request) =>
            SpatialStreamingLodSubstitution.ShouldCommitLoad(BuildLodContext(), request);

        void AbortLoadedInstance(
            GameObject instance,
            SpatialBundleLoader.LoadResult loadResult)
        {
            if (instance != null)
                SpatialObjectUtility.Destroy(instance);

            if (string.IsNullOrEmpty(loadResult.BundleFullPath))
                return;

            if (loadResult.Bundle != null)
                SpatialBundleLoader.Release(loadResult.BundleFullPath, unloadAllLoadedObjects: true);
            else if (!string.IsNullOrEmpty(loadResult.BundleFullPath))
                SpatialBundleLoader.Release(loadResult.BundleFullPath, unloadAllLoadedObjects: false);

            loadResult.Bundle = null;
        }

        static void DetachBundleArchiveAfterSpawn(SpatialBundleLoader.LoadResult loadResult)
        {
            if (loadResult.Bundle == null || string.IsNullOrEmpty(loadResult.BundleFullPath))
                return;

            SpatialBundleLoader.ReleaseArchiveAfterSpawn(loadResult.BundleFullPath);
            loadResult.Bundle = null;
        }

        void ProcessPendingLoads()
        {
            if (_pending.Count == 0 || !_datasetBundlesAvailable)
                return;

            bool cameraMoved = !_hasVisibilityCamPos ||
                               (_cam.position - _lastPendingSortCamPos).sqrMagnitude >=
                               VisibilityCameraMoveMeters * VisibilityCameraMoveMeters;
            if (_pendingSortDirty || cameraMoved)
            {
                SpatialStreamingLodSubstitution.Context ctx = GetOrBuildLodContext();
                _pending.Sort((a, b) => SpatialStreamingLodSubstitution.CompareLoadRequests(ctx, a, b));
                _pendingSortDirty = false;
                _lastPendingSortCamPos = _cam.position;
            }

            int active = _loading.Count;
            int budget = Mathf.Max(1, _loadBudget.maxConcurrentLoads);
            float frameStart = Time.realtimeSinceStartup;
            int started = 0;
            int skippedThisFrame = 0;

            while (_pending.Count > 0 && active < budget)
            {
                if (SpatialFrameBudget.ShouldYield(
                        started,
                        _loadBudget.maxInstantiatesPerFrame,
                        frameStart,
                        _loadBudget.instantiateMsBudget))
                {
                    break;
                }

                if (skippedThisFrame >= _pending.Count)
                    break;

                SpatialStreamingHlodEvaluator.LoadRequest request = _pending[0];
                if (request.LodLevel == SpatialStreamingLodLevel.Detail &&
                    SpatialStreamingLodSubstitution.CountDetailInFlightForTile(
                        GetOrBuildLodContext(), request.TileId, null) >=
                    SpatialStreamingLodSubstitution.MaxConcurrentDetailLoadsPerTile)
                {
                    _pending.RemoveAt(0);
                    _pending.Add(request);
                    skippedThisFrame++;
                    continue;
                }

                skippedThisFrame = 0;
                _pending.RemoveAt(0);

                if (_loaded.ContainsKey(request.Key) || _loading.Contains(request.Key))
                    continue;

                if (IsLoadBlocked(request.Key))
                    continue;

                _loading.Add(request.Key);
                _loadingStartedAt[request.Key] = Time.time;
                if (request.LodLevel == SpatialStreamingLodLevel.Detail && !string.IsNullOrEmpty(request.TileId))
                    _loadedStateIndex?.RegisterDetailLoading(request.TileId);
                active++;
                started++;
                if (_enablePerformanceProfiling)
                    _performanceTracker.RecordLoadStart();
                StartCoroutine(LoadBlockCoroutine(request));
            }
        }

        void PruneStaleLoading()
        {
            if (_loading.Count == 0)
                return;

            float now = Time.time;
            _loadingCancelScratch.Clear();
            foreach (string key in _loading)
                _loadingCancelScratch.Add(key);

            foreach (string key in _loadingCancelScratch)
            {
                if (!_loadingStartedAt.TryGetValue(key, out float startedAt))
                {
                    _loadingStartedAt[key] = now;
                    continue;
                }

                if (now - startedAt < StaleLoadingTimeoutSeconds)
                    continue;

                _cancelledLoads.Add(key);
                UnregisterDetailLoadingForKey(key);
                _loading.Remove(key);
                _loadingStartedAt.Remove(key);
                _loadBlockedUntil[key] = now + LoadFailureRetrySeconds;
                Debug.LogWarning(
                    $"[ZGConnect.Spatial] Timed out stale load '{key}' after {StaleLoadingTimeoutSeconds:F0}s — " +
                    "slot released for retry.");
            }
        }

        IEnumerator LoadBlockCoroutine(SpatialStreamingHlodEvaluator.LoadRequest request)
        {
            yield return null;

            IEnumerator body = LoadBlockCoroutineBody(request);
            while (true)
            {
                object current;
                try
                {
                    if (!body.MoveNext())
                        break;

                    current = body.Current;
                }
                catch (System.Exception ex)
                {
                    _totalLoadFailures++;
                    _lastLoadError = ex.Message;
                    _loadBlockedUntil[request.Key] = Time.time + LoadFailureRetrySeconds;
                    Debug.LogError($"[ZGConnect.Spatial] Load coroutine failed for '{request.Key}': {ex}");
                    break;
                }

                yield return current;
            }

            _loading.Remove(request.Key);
            _loadingStartedAt.Remove(request.Key);
            UnregisterDetailLoadingForKey(request.Key);
        }

        void UnregisterDetailLoadingForKey(string key)
        {
            if (_loadedStateIndex == null ||
                !SpatialStreamingLodSubstitution.TryParseDetailKey(key, out string tileId, out _))
            {
                return;
            }

            _loadedStateIndex.UnregisterDetailLoading(tileId);
        }

        IEnumerator LoadBlockCoroutineBody(SpatialStreamingHlodEvaluator.LoadRequest request)
        {
            if (!ShouldAcceptLoadedBlock(request))
            {
                BlockLoadCommitReject(request.Key);
                yield break;
            }

            float spawnMainThreadMs = 0f;
            float spawnStartMs = Time.realtimeSinceStartup * 1000f;
            float MarkMainThread() => Time.realtimeSinceStartup * 1000f;

            string fullPath = SpatialStreamingPaths.ResolveDatasetRelativePath(request.BundleRel);
            var loadResult = new SpatialBundleLoader.LoadResult();
            bool useMeshDetail = ShouldLoadMeshDetail(request);

            yield return SpatialBundleLoader.LoadVisualAsync(fullPath, useMeshDetail, loadResult);
            if (!loadResult.Success &&
                useMeshDetail &&
                request.LodLevel == SpatialStreamingLodLevel.Detail)
            {
                loadResult = new SpatialBundleLoader.LoadResult();
                yield return SpatialBundleLoader.LoadVisualAsync(fullPath, preferMeshDetail: false, loadResult);
            }

            yield return null;

            if (!loadResult.Success || (loadResult.Prefab == null && loadResult.MeshDetail == null))
            {
                _totalLoadFailures++;
                _lastLoadError = loadResult.Error;
                _loadBlockedUntil[request.Key] = Time.time + LoadFailureRetrySeconds;
                if (_logStreaming)
                {
                    Debug.LogWarning(
                        $"[ZGConnect.Spatial] Failed to load '{request.Key}': {loadResult.Error}");
                }

                yield break;
            }

            _loadBlockedUntil.Remove(request.Key);

            if (_cancelledLoads.Remove(request.Key))
            {
                SpatialBundleLoader.Release(loadResult.BundleFullPath, unloadAllLoadedObjects: false);
                yield break;
            }

            if (_loaded.ContainsKey(request.Key))
            {
                SpatialBundleLoader.Release(loadResult.BundleFullPath, unloadAllLoadedObjects: false);
                yield break;
            }

            if (!ShouldAcceptLoadedBlock(request))
            {
                SpatialBundleLoader.Release(loadResult.BundleFullPath, unloadAllLoadedObjects: false);
                BlockLoadCommitReject(request.Key);
                yield break;
            }

            Vector3 worldPos = ResolveInstanceWorldPosition(request);
            bool isProxyLod = IsProxyLod(request.LodLevel);
            bool isSupertileLod = IsSupertileLod(request.LodLevel);
            GameObject instance = null;

            if (loadResult.MeshDetail != null)
            {
                yield return InstantiateMeshDetailRoutine(
                    loadResult.MeshDetail,
                    worldPos,
                    _contentRoot,
                    _loadBudget,
                    created => instance = created);
            }
            else
            {
                yield return InstantiateLoadedPrefab(
                    loadResult.Prefab,
                    worldPos,
                    _contentRoot,
                    created => instance = created);
                yield return null;
            }

            if (instance == null)
            {
                if (loadResult.Bundle != null)
                    SpatialBundleLoader.Release(loadResult.BundleFullPath, unloadAllLoadedObjects: true);
                else if (!string.IsNullOrEmpty(loadResult.BundleFullPath))
                    SpatialBundleLoader.Release(loadResult.BundleFullPath, unloadAllLoadedObjects: false);
                yield break;
            }

            instance.SetActive(false);

            if (_cancelledLoads.Remove(request.Key) || !ShouldAcceptLoadedBlock(request))
            {
                AbortLoadedInstance(instance, loadResult);
                BlockLoadCommitReject(request.Key);
                yield break;
            }

            yield return SpatialSpawnFrameBudget.WaitForFinalizeSlot(_loadBudget);

            try
            {
                instance.name = BuildInstanceName(request);

                bool isCoarseProxy = request.LodLevel == SpatialStreamingLodLevel.TileProxy ||
                                     request.LodLevel == SpatialStreamingLodLevel.SubcellProxy;

                bool deferGpuAttachForCrossfade = request.LodLevel == SpatialStreamingLodLevel.Detail &&
                                                  _enableDetailCrossfade &&
                                                  _detailCrossfade != null &&
                                                  !string.IsNullOrEmpty(request.TileId) &&
                                                  !string.IsNullOrEmpty(request.SubcellId) &&
                                                  _loaded.ContainsKey(
                                                      SpatialStreamingHlodEvaluator.BuildSubcellProxyKey(
                                                          request.TileId,
                                                          request.SubcellId));

                int meshRendererCount = 0;
                if (_applyMaterialsOnLoad &&
                    _buildingSurfaceSettings != null &&
                    !loadResult.IsMeshDetail)
                {
                    float materialsStart = MarkMainThread();
                    string materialTileId = ResolveMaterialTileId(request);
                    yield return SpatialBuildingMaterialApplier.ApplyFacadeMaterialsToCombinedInstanceRoutine(
                        instance,
                        materialTileId,
                        ResolveMaterialTileOrigin(request, worldPos),
                        _manifest?.TileSizeMeters ?? 1000,
                        _buildingSurfaceSettings,
                        _loadBudget);
                    spawnMainThreadMs += MarkMainThread() - materialsStart;
                }

                if (!isCoarseProxy && !isSupertileLod && !deferGpuAttachForCrossfade)
                {
                    yield return SpatialSpawnFrameBudget.WaitForSpawnStep(_loadBudget);

                    Profiler.BeginSample("SpatialStreaming.GpuAttach");
                    float gpuStart = MarkMainThread();
                    meshRendererCount = SpatialStreamedMeshRoot.Attach(instance, _gpuSettings).MeshRendererCount;
                    spawnMainThreadMs += MarkMainThread() - gpuStart;
                    Profiler.EndSample();
                }

                if (meshRendererCount <= 0)
                {
                    float countStart = MarkMainThread();
                    meshRendererCount = instance.GetComponentsInChildren<MeshRenderer>(true).Length;
                    spawnMainThreadMs += MarkMainThread() - countStart;
                }

                if (_debugLodTint)
                    SpatialStreamingLodDebugTintUtility.Apply(instance, request.LodLevel);

                DetachBundleArchiveAfterSpawn(loadResult);

                if (_enablePerformanceProfiling)
                    _performanceTracker.RecordSpawn(request.Key, spawnMainThreadMs);

                _loaded[request.Key] = new SpatialLoadedSubcellRecord
                {
                    Key = request.Key,
                    TileId = request.TileId,
                    SubcellId = request.SubcellId,
                    BundleFullPath = loadResult.BundleFullPath,
                    Root = instance,
                    Bundle = loadResult.Bundle,
                    LoadedAt = Time.time,
                    LodLevel = request.LodLevel,
                    HlodFactor = request.HlodFactor,
                    BlockLeft = request.BlockLeft,
                    BlockBottom = request.BlockBottom,
                    MeshRendererCount = meshRendererCount,
                };

                RegisterLoadedRecordIndexes(_loaded[request.Key]);
                _totalInstantiated++;

                MarkLoadedStateDirty();
                EnqueueVisibilityKeysForLoad(request);
                CommitLoadedRecordVisibility(_loaded[request.Key]);

                yield return null;

                if (request.LodLevel == SpatialStreamingLodLevel.Detail &&
                    !string.IsNullOrEmpty(request.TileId) &&
                    !string.IsNullOrEmpty(request.SubcellId))
                {
                    OnDetailCommitted(request.TileId, request.SubcellId);
                }

                if (!string.IsNullOrEmpty(request.TileId) && IsTileDetailComplete(request.TileId))
                    UnloadCoarseLodForTile(request.TileId);

                if (_logStreaming)
                {
                    float spawnWallMs = MarkMainThread() - spawnStartMs;
                    string detailProgress = request.LodLevel == SpatialStreamingLodLevel.Detail &&
                                            !string.IsNullOrEmpty(request.TileId)
                        ? $" detail={CountLoadedDetailSubcellsForTile(request.TileId)}/" +
                          $"{CountRequiredDetailSubcellsForTile(request.TileId)}"
                        : string.Empty;
                    Debug.Log(
                        $"[ZGConnect.Spatial] Loaded '{request.Key}' lod={request.LodLevel} at {worldPos} " +
                        $"renderers={meshRendererCount}{detailProgress} " +
                        $"spawnMainMs={spawnMainThreadMs:F1} spawnWallMs={spawnWallMs:F1} from {request.BundleRel}");
                }
            }
            finally
            {
                SpatialSpawnFrameBudget.ReleaseFinalizeSlot();
            }
        }

        static bool IsSupertileLod(SpatialStreamingLodLevel lodLevel) =>
            lodLevel == SpatialStreamingLodLevel.Hlod2x2 ||
            lodLevel == SpatialStreamingLodLevel.Hlod4x4;

        static bool IsProxyLod(SpatialStreamingLodLevel lodLevel) =>
            lodLevel == SpatialStreamingLodLevel.SubcellProxy ||
            lodLevel == SpatialStreamingLodLevel.TileProxy;

        bool ShouldLoadMeshDetail(SpatialStreamingHlodEvaluator.LoadRequest request)
        {
            if (request.LodLevel != SpatialStreamingLodLevel.Detail ||
                string.IsNullOrEmpty(request.TileId) ||
                string.IsNullOrEmpty(request.SubcellId))
            {
                return false;
            }

            SpatialSubcellManifestEntry subcell = FindSubcellManifest(request.TileId, request.SubcellId);
            return subcell != null &&
                   subcell.UsesMeshDetail &&
                   SpatialMeshDetailRuntime.IsAvailable;
        }

        SpatialSubcellManifestEntry FindSubcellManifest(string tileId, string subcellId)
        {
            SpatialTileManifestEntry tile = _manifest?.FindTile(tileId);
            if (tile?.Subcells == null)
                return null;

            foreach (SpatialSubcellManifestEntry subcell in tile.Subcells)
            {
                if (subcell != null && subcell.SubcellId == subcellId)
                    return subcell;
            }

            return null;
        }

        static IEnumerator InstantiateMeshDetailRoutine(
            SpatialMeshDetailAsset detail,
            Vector3 worldPos,
            Transform parent,
            SpatialStreamingLoadBudget budget,
            System.Action<GameObject> onComplete)
        {
            if (detail == null)
            {
                onComplete?.Invoke(null);
                yield break;
            }

            var root = new GameObject("SpatialMeshDetail");
            root.SetActive(false);
            if (parent != null)
                root.transform.SetParent(parent, false);
            root.transform.SetPositionAndRotation(worldPos, Quaternion.identity);
            root.transform.localScale = Vector3.one;

            SpatialMeshDetailAsset.RendererEntry[] entries = detail.Renderers;
            if (entries == null || entries.Length == 0)
            {
                onComplete?.Invoke(root);
                yield break;
            }

            int batch = Mathf.Max(1, budget.renderersPerSpawnStep);
            for (int i = 0; i < entries.Length; i++)
            {
                SpatialMeshDetailAsset.RendererEntry entry = entries[i];
                if (entry == null || entry.Mesh == null)
                    continue;

                string childName = string.IsNullOrEmpty(entry.Name) ? $"MeshDetail_{i}" : entry.Name;
                var child = new GameObject(childName);
                child.transform.SetParent(root.transform, false);

                MeshFilter filter = child.AddComponent<MeshFilter>();
                filter.sharedMesh = entry.Mesh;

                MeshRenderer renderer = child.AddComponent<MeshRenderer>();
                if (entry.Materials != null && entry.Materials.Length > 0)
                    renderer.sharedMaterials = entry.Materials;

                if ((i + 1) % batch != 0 && i + 1 < entries.Length)
                    continue;

                yield return SpatialSpawnFrameBudget.WaitForSpawnStep(budget);
            }

            onComplete?.Invoke(root);
        }

        static IEnumerator InstantiateLoadedPrefab(
            GameObject prefab,
            Vector3 worldPos,
            Transform parent,
            System.Action<GameObject> onComplete)
        {
            if (prefab == null)
            {
                onComplete(null);
                yield break;
            }

#if UNITY_2022_3_OR_NEWER
            AsyncInstantiateOperation asyncOp = Object.InstantiateAsync(prefab, parent);
            yield return asyncOp;
            GameObject instance = null;
            if (asyncOp.isDone && asyncOp.Result != null && asyncOp.Result.Length > 0)
                instance = asyncOp.Result[0] as GameObject;

            if (instance != null)
            {
                instance.transform.SetPositionAndRotation(worldPos, Quaternion.identity);
                instance.SetActive(false);
            }

            onComplete(instance);
#else
            GameObject instance = Object.Instantiate(prefab, worldPos, Quaternion.identity, parent);
            if (instance != null)
                instance.SetActive(false);
            onComplete(instance);
            yield break;
#endif
        }

        void UpdateLodVisibility(int recordBudget = int.MaxValue)
        {
            if (_cam == null || recordBudget <= 0)
                return;

            if (!_enableHlod || !_tileRings.UsesCoarseLodChain)
            {
                if (_detailCrossfade != null && _detailCrossfade.HasActiveTransitions)
                    UpdateCrossfadeVisibility();

                if (NeedsFullVisibilityRefresh())
                    MarkLodVisibilityRefreshed();

                return;
            }

            if (_loading.Count > 0)
                return;

            bool needsRefresh = NeedsFullVisibilityRefresh();
            if (!needsRefresh && !IsFullVisibilityRefreshInProgress())
                return;

            if (needsRefresh &&
                (!IsFullVisibilityRefreshInProgress() || _fullVisibilityRefreshRevision != _loadedRevision))
            {
                BeginFullVisibilityRefresh();
            }

            if (!IsFullVisibilityRefreshInProgress())
                return;

            Profiler.BeginSample("SpatialStreaming.UpdateLodVisibility");
            SpatialStreamingLodSubstitution.Context ctx = GetOrBuildLodContext();

            int processed = 0;
            while (_fullVisibilityRefreshCursor < _fullVisibilityRefreshKeys.Count && processed < recordBudget)
            {
                string key = _fullVisibilityRefreshKeys[_fullVisibilityRefreshCursor++];
                if (_loaded.TryGetValue(key, out SpatialLoadedSubcellRecord record))
                    ApplyRecordVisibility(ctx, record);
                processed++;
            }

            if (_fullVisibilityRefreshCursor >= _fullVisibilityRefreshKeys.Count)
            {
                _fullVisibilityRefreshKeys.Clear();
                _fullVisibilityRefreshCursor = 0;
                MarkLodVisibilityRefreshed();
            }

            Profiler.EndSample();
        }

        void CommitLoadedRecordVisibility(SpatialLoadedSubcellRecord record)
        {
            if (record?.Root == null)
                return;

            ApplyRecordVisibility(GetOrBuildLodContext(), record);
        }

        void ApplyRecordVisibility(
            SpatialStreamingLodSubstitution.Context ctx,
            SpatialLoadedSubcellRecord record)
        {
            if (record?.Root == null)
                return;

            bool visible = SpatialStreamingLodSubstitution.ShouldRenderRecord(ctx, record);
            if (_detailCrossfade != null &&
                record.LodLevel == SpatialStreamingLodLevel.SubcellProxy &&
                _detailCrossfade.IsTransitioningProxy(record.Key))
            {
                visible = true;
            }

            if (record.Root.activeSelf != visible)
                record.Root.SetActive(visible);
        }

        void RegisterLoadedRecordIndexes(SpatialLoadedSubcellRecord record)
        {
            if (record?.Root == null)
                return;

            _loadedStateIndex?.RegisterLoaded(record);
            _coverage?.OnRecordLoaded(record);
        }

        void UnregisterLoadedRecordIndexes(SpatialLoadedSubcellRecord record)
        {
            if (record == null)
                return;

            _loadedStateIndex?.UnregisterLoaded(record);
            _coverage?.OnRecordUnloaded(record);
        }

        int ProcessVisibilityQueue(int maxRecords)
        {
            if (maxRecords <= 0 || _pendingVisibilityKeys.Count == 0 || _cam == null)
                return 0;

            Profiler.BeginSample("SpatialStreaming.ProcessVisibilityQueue");
            SpatialStreamingLodSubstitution.Context ctx = GetOrBuildLodContext();

            _visibilityQueueDrainScratch.Clear();
            foreach (string key in _pendingVisibilityKeys)
            {
                _visibilityQueueDrainScratch.Add(key);
                if (_visibilityQueueDrainScratch.Count >= maxRecords)
                    break;
            }

            int processed = 0;
            for (int i = 0; i < _visibilityQueueDrainScratch.Count; i++)
            {
                string key = _visibilityQueueDrainScratch[i];
                if (!_pendingVisibilityKeys.Remove(key))
                    continue;

                if (_loaded.TryGetValue(key, out SpatialLoadedSubcellRecord record))
                    ApplyRecordVisibility(ctx, record);

                processed++;
            }

            Profiler.EndSample();
            return processed;
        }

        void EnqueueVisibilityKey(string key)
        {
            if (!string.IsNullOrEmpty(key))
                _pendingVisibilityKeys.Add(key);
        }

        void EnqueueLoadedSupertileAt(
            int blockLeft,
            int blockBottom,
            SpatialStreamingLodLevel lod)
        {
            long packed = SpatialStreamingLodSubstitution.PackBlockOrigin(blockLeft, blockBottom);
            IReadOnlyDictionary<long, SpatialLoadedSubcellRecord> index =
                lod == SpatialStreamingLodLevel.Hlod4x4
                    ? _loadedStateIndex?.Hlod4ByBlock
                    : _loadedStateIndex?.Hlod2ByBlock;
            if (index == null)
                return;
            if (index.TryGetValue(packed, out SpatialLoadedSubcellRecord record) && record != null)
                EnqueueVisibilityKey(record.Key);
        }

        void EnqueueSupertileKeysForTile(string tileId)
        {
            if (_manifest == null || !SpatialTileIdUtility.TryParse(tileId, out int left, out int bottom))
                return;

            int tileSize = _manifest.TileSizeMeters > 0 ? _manifest.TileSizeMeters : 1000;
            int block2Size = tileSize * 2;
            int block4Size = tileSize * 4;
            int block2Left = SpatialTileIdUtility.AlignDownMeters(left, block2Size);
            int block2Bottom = SpatialTileIdUtility.AlignDownMeters(bottom, block2Size);
            int block4Left = SpatialTileIdUtility.AlignDownMeters(left, block4Size);
            int block4Bottom = SpatialTileIdUtility.AlignDownMeters(bottom, block4Size);

            EnqueueLoadedSupertileAt(block2Left, block2Bottom, SpatialStreamingLodLevel.Hlod2x2);
            EnqueueLoadedSupertileAt(block4Left, block4Bottom, SpatialStreamingLodLevel.Hlod4x4);
        }

        void EnqueueTileProxyKeysInBlock2(int block2Left, int block2Bottom)
        {
            if (_manifest == null)
                return;

            int tileSize = _manifest.TileSizeMeters > 0 ? _manifest.TileSizeMeters : 1000;
            for (int dy = 0; dy < 2; dy++)
            {
                for (int dx = 0; dx < 2; dx++)
                {
                    string tileId = SpatialTileIdUtility.Format(
                        block2Left + dx * tileSize,
                        block2Bottom + dy * tileSize);
                    EnqueueVisibilityKey(SpatialStreamingHlodEvaluator.BuildProxyKey(tileId));
                }
            }
        }

        void EnqueueLoadedSubcellProxyVisibilityForTile(string tileId, HashSet<string> scratch)
        {
            if (_manifest == null || string.IsNullOrEmpty(tileId) || scratch == null)
                return;

            SpatialTileManifestEntry tile = _manifest.FindTile(tileId);
            if (tile?.Subcells == null)
                return;

            foreach (SpatialSubcellManifestEntry subcell in tile.Subcells)
            {
                if (subcell == null || string.IsNullOrEmpty(subcell.ProxyBundleRel))
                    continue;

                string proxyKey = SpatialStreamingHlodEvaluator.BuildSubcellProxyKey(tileId, subcell.SubcellId);
                if (_loaded.ContainsKey(proxyKey))
                    scratch.Add(proxyKey);
            }
        }

        void EnqueueVisibilityKeysForLoad(SpatialStreamingHlodEvaluator.LoadRequest request)
        {
            _visibilityRefreshScratch.Clear();
            if (!string.IsNullOrEmpty(request.Key))
                _visibilityRefreshScratch.Add(request.Key);

            switch (request.LodLevel)
            {
                case SpatialStreamingLodLevel.Detail:
                case SpatialStreamingLodLevel.SubcellProxy:
                    if (!string.IsNullOrEmpty(request.TileId) && !string.IsNullOrEmpty(request.SubcellId))
                    {
                        _visibilityRefreshScratch.Add(
                            SpatialStreamingHlodEvaluator.BuildSubcellProxyKey(request.TileId, request.SubcellId));
                        _visibilityRefreshScratch.Add(
                            SpatialStreamingHlodEvaluator.BuildDetailKey(request.TileId, request.SubcellId));
                    }

                    if (!string.IsNullOrEmpty(request.TileId))
                    {
                        _visibilityRefreshScratch.Add(SpatialStreamingHlodEvaluator.BuildProxyKey(request.TileId));
                        EnqueueLoadedSubcellProxyVisibilityForTile(request.TileId, _visibilityRefreshScratch);
                        EnqueueSupertileKeysForTile(request.TileId);
                    }

                    break;

                case SpatialStreamingLodLevel.TileProxy:
                    if (!string.IsNullOrEmpty(request.TileId))
                        EnqueueSupertileKeysForTile(request.TileId);
                    break;

                case SpatialStreamingLodLevel.Hlod2x2:
                {
                    int tileSize = _manifest?.TileSizeMeters > 0 ? _manifest.TileSizeMeters : 1000;
                    int block4Size = tileSize * 4;
                    int block4Left = SpatialTileIdUtility.AlignDownMeters(request.BlockLeft, block4Size);
                    int block4Bottom = SpatialTileIdUtility.AlignDownMeters(request.BlockBottom, block4Size);
                    EnqueueLoadedSupertileAt(block4Left, block4Bottom, SpatialStreamingLodLevel.Hlod4x4);
                    EnqueueTileProxyKeysInBlock2(request.BlockLeft, request.BlockBottom);
                    break;
                }

                case SpatialStreamingLodLevel.Hlod4x4:
                    break;
            }

            foreach (string key in _visibilityRefreshScratch)
                EnqueueVisibilityKey(key);
        }

        void EnqueueVisibilityKeysForDetailCommit(string tileId, string subcellId, bool crossfadeStarted)
        {
            _visibilityRefreshScratch.Clear();
            _visibilityRefreshScratch.Add(SpatialStreamingHlodEvaluator.BuildDetailKey(tileId, subcellId));
            _visibilityRefreshScratch.Add(SpatialStreamingHlodEvaluator.BuildSubcellProxyKey(tileId, subcellId));
            _visibilityRefreshScratch.Add(SpatialStreamingHlodEvaluator.BuildProxyKey(tileId));

            foreach (string key in _visibilityRefreshScratch)
                EnqueueVisibilityKey(key);

            EnqueueSupertileKeysForTile(tileId);

            if (crossfadeStarted)
                UpdateCrossfadeVisibility();
        }

        void UpdateCrossfadeVisibility()
        {
            if (_detailCrossfade == null)
                return;

            Profiler.BeginSample("SpatialStreaming.UpdateCrossfadeVisibility");
            foreach (SpatialLoadedSubcellRecord record in _loaded.Values)
            {
                if (record?.Root == null ||
                    record.LodLevel != SpatialStreamingLodLevel.SubcellProxy ||
                    !_detailCrossfade.IsTransitioningProxy(record.Key))
                {
                    continue;
                }

                if (!record.Root.activeSelf)
                    record.Root.SetActive(true);
            }

            Profiler.EndSample();
        }

        void OnDetailCommitted(string tileId, string subcellId)
        {
            string proxyKey = SpatialStreamingHlodEvaluator.BuildSubcellProxyKey(tileId, subcellId);
            string detailKey = SpatialStreamingHlodEvaluator.BuildDetailKey(tileId, subcellId);
            CancelLoadRequest(proxyKey);

            _loaded.TryGetValue(proxyKey, out SpatialLoadedSubcellRecord proxyRecord);
            _loaded.TryGetValue(detailKey, out SpatialLoadedSubcellRecord detailRecord);

            bool crossfadeStarted = _enableDetailCrossfade &&
                                      _detailCrossfade != null &&
                                      proxyRecord?.Root != null &&
                                      detailRecord?.Root != null &&
                                      _detailCrossfade.TryBegin(
                                          proxyKey,
                                          proxyRecord.Root,
                                          detailRecord.Root,
                                          () =>
                                          {
                                              if (detailRecord.Root != null)
                                                  SpatialStreamedMeshRoot.Attach(detailRecord.Root, _gpuSettings);
                                              UnloadBlock(proxyKey);
                                          });

            if (!crossfadeStarted)
            {
                if (detailRecord?.Root != null)
                    SpatialStreamedMeshRoot.Attach(detailRecord.Root, _gpuSettings);
                UnloadSubcellProxy(tileId, subcellId);
            }

            UnloadTileProxyIfProgressiveDetail(tileId);
            EnqueueVisibilityKeysForDetailCommit(tileId, subcellId, crossfadeStarted);
        }

        void UnloadSubcellProxy(string tileId, string subcellId)
        {
            if (string.IsNullOrEmpty(tileId) || string.IsNullOrEmpty(subcellId))
                return;

            string proxyKey = SpatialStreamingHlodEvaluator.BuildSubcellProxyKey(tileId, subcellId);
            if (_loaded.ContainsKey(proxyKey))
                UnloadBlock(proxyKey);
        }

        void UnloadTileProxyIfProgressiveDetail(string tileId)
        {
            if (string.IsNullOrEmpty(tileId))
                return;

            SpatialTileManifestEntry tile = _manifest?.FindTile(tileId);
            if (tile == null || !tile.UsesSubcells)
                return;

            if (CountLoadedDetailSubcellsForTile(tileId) == 0)
                return;

            string proxyKey = SpatialStreamingHlodEvaluator.BuildProxyKey(tileId);
            if (_loaded.ContainsKey(proxyKey))
                UnloadBlock(proxyKey);
        }

        void UnloadCoarseLodForTile(string tileId)
        {
            if (string.IsNullOrEmpty(tileId))
                return;

            SpatialTileManifestEntry tile = _manifest?.FindTile(tileId);
            if (tile?.Subcells == null)
                return;

            string proxyKey = SpatialStreamingHlodEvaluator.BuildProxyKey(tileId);
            if (_loaded.ContainsKey(proxyKey))
                UnloadBlock(proxyKey);

            foreach (SpatialSubcellManifestEntry subcell in tile.Subcells)
            {
                if (subcell == null || string.IsNullOrEmpty(subcell.SubcellId))
                    continue;

                UnloadSubcellProxy(tileId, subcell.SubcellId);
            }
        }

        bool IsTileDetailComplete(string tileId)
        {
            if (string.IsNullOrEmpty(tileId))
                return false;

            if (_loadedStateIndex != null)
                return _loadedStateIndex.IsTileDetailComplete(tileId);

            if (_manifest == null)
                return false;

            SpatialTileManifestEntry tile = _manifest.FindTile(tileId);
            if (tile == null)
                return false;

            if (!tile.UsesSubcells)
            {
                string coarseKey = SpatialStreamingHlodEvaluator.BuildDetailKey(tileId, "tile_coarse");
                return _loaded.ContainsKey(coarseKey);
            }

            bool requiresAny = false;
            foreach (SpatialSubcellManifestEntry subcell in tile.Subcells)
            {
                if (subcell == null || string.IsNullOrEmpty(subcell.BundleRel))
                    continue;

                requiresAny = true;
                string key = SpatialStreamingHlodEvaluator.BuildDetailKey(tileId, subcell.SubcellId);
                if (!_loaded.ContainsKey(key))
                    return false;
            }

            return requiresAny;
        }

        int CountLoadedDetailSubcellsForTile(string tileId)
        {
            if (string.IsNullOrEmpty(tileId))
                return 0;

            if (_loadedStateIndex != null)
                return _loadedStateIndex.CountLoadedDetailSubcells(tileId);

            int count = 0;
            foreach (SpatialLoadedSubcellRecord record in _loaded.Values)
            {
                if (record.LodLevel == SpatialStreamingLodLevel.Detail && record.TileId == tileId)
                    count++;
            }

            return count;
        }

        int CountRequiredDetailSubcellsForTile(string tileId)
        {
            SpatialTileManifestEntry tile = _manifest?.FindTile(tileId);
            if (tile == null)
                return 0;

            if (!tile.UsesSubcells)
                return string.IsNullOrEmpty(tile.CoarseBundleRel) ? 0 : 1;

            int count = 0;
            foreach (SpatialSubcellManifestEntry subcell in tile.Subcells)
            {
                if (subcell != null && !string.IsNullOrEmpty(subcell.BundleRel))
                    count++;
            }

            return count;
        }

        public IReadOnlyList<string> BuildRuntimeHudLines()
        {
            SpatialStreamingStats stats = _stats;
            var lines = new List<string>(24)
            {
                $"HLOD: {(_enableHlod ? "on" : "off")}",
                $"Manifest tiles: {_manifest?.Tiles?.Count ?? 0}  supertiles: {_manifest?.Supertiles?.Count ?? 0}",
                $"Index tiles: {_runtimeIndex?.TileCount ?? 0}  supertiles: {_runtimeIndex?.SupertileCount ?? 0}",
            };

            SpatialStreamingHudFormatting.AppendLodTable(lines, stats);

            lines.Add($"Hidden (substituted): {stats.HiddenBySubstitution}");
            lines.Add($"Instantiated (lifetime): {stats.TotalInstantiated}");
            lines.Add($"Load failures: {stats.TotalLoadFailures}");

            if (!_datasetBundlesAvailable)
                lines.Add("Dataset: bundles_spatial MISSING — run Bake");

            if (stats.BlockedByLoadFailure > 0)
                lines.Add($"Load retry blocked: {stats.BlockedByLoadFailure}");

            if (!string.IsNullOrEmpty(stats.LastLoadError))
                lines.Add($"Last error: {stats.LastLoadError}");

            lines.Add($"Visible mesh renderers: {stats.TotalMeshRenderers}");
            lines.Add($"Detail rings: {_tileRings.detailRings}  subcell proxy: {_tileRings.subcellProxyRings}  tile proxy: {_tileRings.tileProxyRings}");
            lines.Add($"HLOD2 rings: {_tileRings.hlod2x2Rings}  HLOD4 rings: {_tileRings.hlod4x4Rings}");

            if (_debugLodTint)
            {
                lines.Add("LOD tint: detail=green  subcell=cyan  tile=yellow  HLOD2=orange  HLOD4=red");
            }

            if (_manifest != null &&
                _cam != null &&
                SpatialStreamingTileRingUtility.TryResolveCameraTileGrid(
                    _manifest, _cam.position, out var cameraTile))
            {
                lines.Add($"Camera tile: {SpatialTileIdUtility.Format(cameraTile.Left, cameraTile.Bottom)}");
            }

            if (_enablePerformanceProfiling)
            {
                SpatialStreamingPerformanceStats perf = _performanceTracker.Stats;
                lines.Add(string.Empty);
                lines.Add("— Performance —");
                lines.Add($"FPS: {perf.SmoothedFps:F0}  frame: {perf.LastFrameMs:F1} ms  min(1s): {perf.MinFps1s:F0}");
                lines.Add(
                    $"Load starts: {perf.LoadStartsThisFrame}/frame  {perf.LoadStartsPerSecond}/s");
                lines.Add(
                    $"Spawn: {perf.SpawnMsThisFrame:F1} ms this frame  last: {perf.LastSpawnMs:F1} ms");
                if (!string.IsNullOrEmpty(perf.LastSpawnKey))
                    lines.Add($"  last key: {perf.LastSpawnKey}");
                if (_logSpikesToCsv)
                {
                    lines.Add($"Spikes (>{_spikeFrameMsThreshold:F0} ms): {perf.SpikeCount}");
                    if (!string.IsNullOrEmpty(perf.SpikeCsvPath))
                        lines.Add($"CSV: {perf.SpikeCsvPath}");
                }
            }

            return lines;
        }

        void EnsureDebugHud()
        {
            if (!_showDebugHud)
            {
                if (_debugHud != null)
                    _debugHud.SetVisible(false);
                return;
            }

            if (_debugHud == null)
            {
                _debugHud = GetComponent<SpatialStreamingDebugHud>();
                if (_debugHud == null)
                {
                    var hudGo = new GameObject("Spatial Streaming HUD");
                    hudGo.transform.SetParent(transform, false);
                    _debugHud = hudGo.AddComponent<SpatialStreamingDebugHud>();
                }

                _debugHud.Initialize(this);
            }

            SyncDebugHudSettings();
            _debugHud.SetVisible(true);
        }

        void SyncDebugHudSettings()
        {
            if (_debugHud == null)
                return;

            _debugHud.ApplySettings(_debugHudScale);
        }

        string ResolveMaterialTileId(SpatialStreamingHlodEvaluator.LoadRequest request)
        {
            if (!string.IsNullOrEmpty(request.TileId))
                return request.TileId;

            if (!string.IsNullOrEmpty(request.SupertileId))
            {
                SpatialSupertileManifestEntry supertile = _manifest?.FindSupertile(request.SupertileId);
                if (supertile?.ChildTileIds != null && supertile.ChildTileIds.Count > 0)
                    return supertile.ChildTileIds[0];
            }

            if (request.BlockLeft != 0 || request.BlockBottom != 0)
                return $"{request.BlockLeft}_{request.BlockBottom}";

            return "spatial";
        }

        Vector3 ResolveMaterialTileOrigin(
            SpatialStreamingHlodEvaluator.LoadRequest request,
            Vector3 instanceWorldPos)
        {
            if (!string.IsNullOrEmpty(request.TileId))
            {
                SpatialTileManifestEntry tile = _manifest?.FindTile(request.TileId);
                if (tile != null)
                    return tile.GetUnityPosition();
            }

            if (!string.IsNullOrEmpty(request.SupertileId))
            {
                SpatialSupertileManifestEntry supertile = _manifest?.FindSupertile(request.SupertileId);
                if (supertile != null)
                    return supertile.GetUnityPosition();
            }

            return instanceWorldPos;
        }

        void RefreshStats()
        {
            MarkStatsRefreshed();

            SpatialStreamingLodBandStats detail = default;
            SpatialStreamingLodBandStats subcellProxy = default;
            SpatialStreamingLodBandStats tileProxy = default;
            SpatialStreamingLodBandStats hlod2 = default;
            SpatialStreamingLodBandStats hlod4 = default;
            int renderers = 0;

            foreach (SpatialStreamingHlodEvaluator.LoadRequest request in _pending)
                IncrementBandPending(ref BandForLod(ref detail, ref subcellProxy, ref tileProxy, ref hlod2, ref hlod4, request.LodLevel));

            foreach (string key in _loading)
            {
                if (!SpatialStreamingHlodEvaluator.TryInferLodLevelFromKey(key, _manifest, out SpatialStreamingLodLevel lodLevel))
                    continue;

                IncrementBandLoading(ref BandForLod(ref detail, ref subcellProxy, ref tileProxy, ref hlod2, ref hlod4, lodLevel));
            }

            foreach (SpatialLoadedSubcellRecord record in _loaded.Values)
            {
                ref SpatialStreamingLodBandStats band =
                    ref BandForLod(ref detail, ref subcellProxy, ref tileProxy, ref hlod2, ref hlod4, record.LodLevel);
                band.Loaded++;

                if (record.Root != null && record.Root.activeSelf)
                {
                    band.Shown++;
                    renderers += record.MeshRendererCount;
                }
            }

            int loadedTotal = detail.Loaded + subcellProxy.Loaded + tileProxy.Loaded + hlod2.Loaded + hlod4.Loaded;
            int visible = detail.Shown + subcellProxy.Shown + tileProxy.Shown + hlod2.Shown + hlod4.Shown;
            int pendingTotal = detail.Pending + subcellProxy.Pending + tileProxy.Pending + hlod2.Pending + hlod4.Pending;
            int loadingTotal = detail.Loading + subcellProxy.Loading + tileProxy.Loading + hlod2.Loading + hlod4.Loading;

            _stats = new SpatialStreamingStats
            {
                Pending = pendingTotal,
                Loading = loadingTotal,
                LoadedTotal = loadedTotal,
                LoadedDetail = detail.Loaded,
                LoadedSubcellProxy = subcellProxy.Loaded,
                LoadedTileProxy = tileProxy.Loaded,
                LoadedHlod2x2 = hlod2.Loaded,
                LoadedHlod4x4 = hlod4.Loaded,
                VisibleBlocks = visible,
                HiddenBySubstitution = loadedTotal - visible,
                TotalInstantiated = _totalInstantiated,
                TotalLoadFailures = _totalLoadFailures,
                TotalMeshRenderers = renderers,
                BlockedByLoadFailure = _loadBlockedUntil.Count,
                LastLoadError = _lastLoadError,
                Detail = detail,
                SubcellProxy = subcellProxy,
                TileProxy = tileProxy,
                Hlod2x2 = hlod2,
                Hlod4x4 = hlod4,
            };
        }

        static ref SpatialStreamingLodBandStats BandForLod(
            ref SpatialStreamingLodBandStats detail,
            ref SpatialStreamingLodBandStats subcellProxy,
            ref SpatialStreamingLodBandStats tileProxy,
            ref SpatialStreamingLodBandStats hlod2,
            ref SpatialStreamingLodBandStats hlod4,
            SpatialStreamingLodLevel lodLevel)
        {
            switch (lodLevel)
            {
                case SpatialStreamingLodLevel.SubcellProxy:
                    return ref subcellProxy;
                case SpatialStreamingLodLevel.TileProxy:
                    return ref tileProxy;
                case SpatialStreamingLodLevel.Hlod2x2:
                    return ref hlod2;
                case SpatialStreamingLodLevel.Hlod4x4:
                    return ref hlod4;
                default:
                    return ref detail;
            }
        }

        static void IncrementBandPending(ref SpatialStreamingLodBandStats band) => band.Pending++;

        static void IncrementBandLoading(ref SpatialStreamingLodBandStats band) => band.Loading++;

        void PruneExpiredLoadBlocks()
        {
            if (_loadBlockedUntil.Count == 0)
                return;

            float now = Time.time;
            var expired = new List<string>();
            foreach (KeyValuePair<string, float> blocked in _loadBlockedUntil)
            {
                if (now >= blocked.Value)
                    expired.Add(blocked.Key);
            }

            foreach (string key in expired)
                _loadBlockedUntil.Remove(key);
        }

        void UnloadBlock(string key)
        {
            if (!_loaded.TryGetValue(key, out SpatialLoadedSubcellRecord record))
                return;

            if (record.Root != null)
                SpatialObjectUtility.Destroy(record.Root);

            if (record.Bundle != null && !string.IsNullOrEmpty(record.BundleFullPath))
                SpatialBundleLoader.Release(record.BundleFullPath, unloadAllLoadedObjects: false);

            UnregisterLoadedRecordIndexes(record);
            _loaded.Remove(key);
            MarkLoadedStateDirty();

            if (_logStreaming)
                Debug.Log($"[ZGConnect.Spatial] Unloaded '{key}'");
        }

        void PrunePendingNotWanted(HashSet<string> want)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (!want.Contains(_pending[i].Key))
                    _pending.RemoveAt(i);
            }
        }

        Vector3 ResolveInstanceWorldPosition(SpatialStreamingHlodEvaluator.LoadRequest request)
        {
            if (request.LodLevel == SpatialStreamingLodLevel.Hlod2x2 ||
                request.LodLevel == SpatialStreamingLodLevel.Hlod4x4)
            {
                SpatialSupertileManifestEntry supertile = _manifest?.FindSupertile(request.SupertileId);
                if (supertile != null)
                    return supertile.GetUnityPosition();
            }

            if (!string.IsNullOrEmpty(request.TileId))
            {
                SpatialTileManifestEntry tile = _manifest?.FindTile(request.TileId);
                if (tile != null)
                    return tile.GetUnityPosition();

                if (_logStreaming)
                {
                    Debug.LogWarning(
                        $"[ZGConnect.Spatial] Tile '{request.TileId}' missing from manifest — placing at origin.");
                }
            }

            return Vector3.zero;
        }

        static string BuildInstanceName(SpatialStreamingHlodEvaluator.LoadRequest request)
        {
            if (request.LodLevel == SpatialStreamingLodLevel.Hlod2x2 ||
                request.LodLevel == SpatialStreamingLodLevel.Hlod4x4)
            {
                return $"Spatial_{request.SupertileId ?? request.SubcellId}";
            }

            return $"Spatial_{request.TileId}_{request.SubcellId}";
        }

        void OnDestroy()
        {
            foreach (string key in _pendingUnloadKeys.ToList())
            {
                if (_loaded.ContainsKey(key))
                    UnloadBlock(key);
            }

            _pendingUnloadKeys.Clear();

            foreach (string key in _loaded.Keys.ToList())
                UnloadBlock(key);
        }
    }
}
