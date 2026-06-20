using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Serialization;
using ZGConnect;

namespace ZGConnect.SpatialStreaming
{
    [DisallowMultipleComponent]
    public sealed class SpatialStreamingController : MonoBehaviour
    {
        [Header("Dataset")]
        [SerializeField] string _manifestRelativePath = SpatialStreamingPaths.SpatialManifestRelativePath;
        [SerializeField] Transform _contentRoot;
        [SerializeField] Camera _targetCamera;

        [Header("HLOD")]
        [SerializeField] bool _enableHlod = true;

        [Header("Detail spawn (whole tile triggers all subcells)")]
        [Tooltip("When the camera is within this distance of a subcell edge, that subcell's proxy/detail begin loading.")]
        [FormerlySerializedAs("_loadDistanceMeters")]
        [FormerlySerializedAs("_detailLoadDistanceMeters")]
        [SerializeField] float _detailTileLoadDistanceMeters = 1200f;
        [Tooltip("When the camera exceeds this distance from the tile, all detail subcells for that tile unload.")]
        [FormerlySerializedAs("_unloadDistanceMeters")]
        [FormerlySerializedAs("_detailUnloadDistanceMeters")]
        [SerializeField] float _detailTileUnloadDistanceMeters = 1600f;

        [Header("Tile proxy")]
        [SerializeField] float _proxyLoadDistanceMeters = 2800f;
        [SerializeField] float _proxyUnloadDistanceMeters = 3200f;

        [Header("HLOD supertiles")]
        [SerializeField] float _hlod2LoadDistanceMeters = 5000f;
        [SerializeField] float _hlod2UnloadDistanceMeters = 6000f;
        [SerializeField] float _hlod4LoadDistanceMeters = 15000f;
        [SerializeField] float _hlod4UnloadDistanceMeters = 17000f;

        [Header("Streaming")]
        [SerializeField] float _checkInterval = 0.35f;
        [SerializeField] SpatialStreamingLoadBudget _loadBudget = SpatialStreamingLoadBudget.Default;

        SpatialStreamingHlodDistances _hlodDistances;

        [Header("Rendering")]
        [SerializeField] SpatialGpuResidentRenderingSettings _gpuSettings;
        [SerializeField] BuildingSurfaceSettings _buildingSurfaceSettings;
        [Tooltip("When enabled, remaps combined mesh materials on each detail subcell after load.")]
        [SerializeField] bool _applyMaterialsOnLoad = true;

        [Header("Debug")]
        [SerializeField] bool _showDebugHud = true;
        [SerializeField] bool _logStreaming;

        [Header("Performance profiling")]
        [SerializeField] bool _enablePerformanceProfiling = true;
        [Tooltip("Log a CSV row when frame time exceeds this threshold during active streaming.")]
        [SerializeField] float _spikeFrameMsThreshold = 33.33f;
        [SerializeField] bool _logSpikesToCsv = true;
        [SerializeField] bool _logSpikesToConsole;

        SpatialDatasetManifest _manifest;
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
        const float MaxSaneLoadDistanceMeters = 50000f;

        readonly Dictionary<string, SpatialLoadedSubcellRecord> _loaded = new();
        readonly HashSet<string> _loading = new();
        readonly List<SpatialStreamingHlodEvaluator.LoadRequest> _pending = new();

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
            SyncLegacyDistanceFields();
            LoadManifest();
        }

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
            Debug.LogError(
                $"[ZGConnect.Spatial] bundles_spatial/ exists but manifest bundle '{sampleRel}' was not found. " +
                "Re-run Spatial Streaming Bake for the current manifest.");
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

            if (Time.time < _nextCheck)
                return;

            _nextCheck = Time.time + Mathf.Max(0.05f, _checkInterval);
            SyncLegacyDistanceFields();

            Profiler.BeginSample("SpatialStreaming.Evaluate");
            EvaluateStreaming();
            Profiler.EndSample();

            Profiler.BeginSample("SpatialStreaming.ProcessPendingLoads");
            ProcessPendingLoads();
            Profiler.EndSample();

            UpdateLodVisibility();
            RefreshStats();
        }

        void LateUpdate()
        {
            if (HasAnyDetailSubstitutionInProgress() && _manifest != null && _cam != null)
                UpdateLodVisibility();

            if (!_enablePerformanceProfiling)
                return;

            _performanceTracker.EndFrame(
                Time.unscaledDeltaTime,
                _stats,
                _logSpikesToCsv,
                _logSpikesToConsole,
                _spikeFrameMsThreshold);
        }

        bool HasAnyDetailSubstitutionInProgress()
        {
            foreach (SpatialLoadedSubcellRecord record in _loaded.Values)
            {
                if (record?.Root == null ||
                    string.IsNullOrEmpty(record.TileId))
                {
                    continue;
                }

                if (record.LodLevel == SpatialStreamingLodLevel.TileProxy &&
                    ShouldHideTileProxyForTile(record.TileId))
                {
                    return true;
                }

                if (record.LodLevel != SpatialStreamingLodLevel.SubcellProxy ||
                    string.IsNullOrEmpty(record.SubcellId))
                {
                    continue;
                }

                if (_loading.Contains(SpatialStreamingHlodEvaluator.BuildDetailKey(record.TileId, record.SubcellId)))
                    return true;
            }

            return false;
        }

        HashSet<string> BuildDetailCompleteTileIds()
        {
            var complete = new HashSet<string>();
            if (_manifest?.Tiles == null)
                return complete;

            foreach (SpatialTileManifestEntry tile in _manifest.Tiles)
            {
                if (tile != null && IsTileDetailComplete(tile.TileId))
                    complete.Add(tile.TileId);
            }

            return complete;
        }

        void SyncLegacyDistanceFields()
        {
            SanitizeDistancePair(
                ref _detailTileLoadDistanceMeters,
                ref _detailTileUnloadDistanceMeters,
                SpatialStreamingHlodDistances.Default.detailLoadMeters,
                SpatialStreamingHlodDistances.Default.detailUnloadMeters,
                "Detail tile");
            SanitizeDistancePair(
                ref _proxyLoadDistanceMeters,
                ref _proxyUnloadDistanceMeters,
                SpatialStreamingHlodDistances.Default.proxyLoadMeters,
                SpatialStreamingHlodDistances.Default.proxyUnloadMeters,
                "Proxy");
            SanitizeDistancePair(
                ref _hlod2LoadDistanceMeters,
                ref _hlod2UnloadDistanceMeters,
                SpatialStreamingHlodDistances.Default.hlod2LoadMeters,
                SpatialStreamingHlodDistances.Default.hlod2UnloadMeters,
                "HLOD2");
            SanitizeDistancePair(
                ref _hlod4LoadDistanceMeters,
                ref _hlod4UnloadDistanceMeters,
                SpatialStreamingHlodDistances.Default.hlod4LoadMeters,
                SpatialStreamingHlodDistances.Default.hlod4UnloadMeters,
                "HLOD4");

            _hlodDistances = new SpatialStreamingHlodDistances
            {
                detailLoadMeters = _detailTileLoadDistanceMeters,
                detailUnloadMeters = _detailTileUnloadDistanceMeters,
                proxyLoadMeters = _proxyLoadDistanceMeters,
                proxyUnloadMeters = _proxyUnloadDistanceMeters,
                hlod2LoadMeters = _hlod2LoadDistanceMeters,
                hlod2UnloadMeters = _hlod2UnloadDistanceMeters,
                hlod4LoadMeters = _hlod4LoadDistanceMeters,
                hlod4UnloadMeters = _hlod4UnloadDistanceMeters,
            };
        }

        void SanitizeDistancePair(
            ref float loadMeters,
            ref float unloadMeters,
            float defaultLoad,
            float defaultUnload,
            string label)
        {
            if (loadMeters <= 0f ||
                unloadMeters <= 0f ||
                loadMeters > MaxSaneLoadDistanceMeters ||
                loadMeters > unloadMeters)
            {
                Debug.LogWarning(
                    $"[ZGConnect.Spatial] {label} distances invalid ({loadMeters:F0}/{unloadMeters:F0} m) — " +
                    $"using defaults ({defaultLoad:F0}/{defaultUnload:F0} m).");
                loadMeters = defaultLoad;
                unloadMeters = defaultUnload;
            }
        }

        public void LoadManifest()
        {
            string path = string.IsNullOrEmpty(_manifestRelativePath)
                ? SpatialStreamingPaths.SpatialManifestPath()
                : Path.Combine(
                    SpatialStreamingPaths.StreamingAssetsRoot,
                    _manifestRelativePath.Replace('/', Path.DirectorySeparatorChar));

            _manifest = SpatialDatasetManifest.LoadFromFile(path);
        }

        void EvaluateStreaming()
        {
            Vector3 camPos = _cam.position;
            var want = new HashSet<string>();
            var pendingKeys = new HashSet<string>();
            foreach (SpatialStreamingHlodEvaluator.LoadRequest request in _pending)
                pendingKeys.Add(request.Key);

            PruneExpiredLoadBlocks();
            foreach (KeyValuePair<string, float> blocked in _loadBlockedUntil)
                pendingKeys.Add(blocked.Key);

            _pending.Clear();

            var loadedKeys = new HashSet<string>(_loaded.Keys);
            HashSet<string> detailCompleteTileIds = BuildDetailCompleteTileIds();
            SpatialStreamingHlodEvaluator.Evaluate(
                _manifest,
                camPos,
                _hlodDistances,
                _enableHlod,
                detailCompleteTileIds,
                want,
                _pending,
                _loading,
                loadedKeys,
                pendingKeys);

            foreach (string key in _loaded.Keys.ToList())
            {
                if (!want.Contains(key))
                    UnloadBlock(key);
            }

            foreach (string key in _loading.ToList())
            {
                if (!want.Contains(key))
                    _loading.Remove(key);
            }

            PrunePendingNotWanted(want);
        }

        void ProcessPendingLoads()
        {
            if (_pending.Count == 0 || !_datasetBundlesAvailable)
                return;

            _pending.Sort(ComparePendingLoads);

            int active = _loading.Count;
            int budget = Mathf.Max(1, _loadBudget.maxConcurrentLoads);
            float frameStart = Time.realtimeSinceStartup;
            int started = 0;

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

                SpatialStreamingHlodEvaluator.LoadRequest request = _pending[0];
                _pending.RemoveAt(0);

                if (_loaded.ContainsKey(request.Key) || _loading.Contains(request.Key))
                    continue;

                _loading.Add(request.Key);
                active++;
                started++;
                if (_enablePerformanceProfiling)
                    _performanceTracker.RecordLoadStart();
                StartCoroutine(LoadBlockCoroutine(request));
            }
        }

        static int ComparePendingLoads(
            SpatialStreamingHlodEvaluator.LoadRequest a,
            SpatialStreamingHlodEvaluator.LoadRequest b)
        {
            // Coarsest LOD first (HLOD4 → HLOD2 → tile proxy → subcell proxy → detail).
            int lodCmp = b.LodLevel.CompareTo(a.LodLevel);
            if (lodCmp != 0)
                return lodCmp;

            return a.PriorityDistance.CompareTo(b.PriorityDistance);
        }

        IEnumerator LoadBlockCoroutine(SpatialStreamingHlodEvaluator.LoadRequest request)
        {
            Profiler.BeginSample("SpatialStreaming.LoadBlock");
            float spawnStartMs = Time.realtimeSinceStartup * 1000f;

            string fullPath = SpatialStreamingPaths.ResolveDatasetRelativePath(request.BundleRel);
            var loadResult = new SpatialBundleLoader.LoadResult();
            bool useMeshDetail = ShouldLoadMeshDetail(request);

            Profiler.BeginSample("SpatialStreaming.LoadBundle");
            yield return SpatialBundleLoader.LoadVisualAsync(fullPath, useMeshDetail, loadResult);
            Profiler.EndSample();

            _loading.Remove(request.Key);

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

                Profiler.EndSample();
                yield break;
            }

            _loadBlockedUntil.Remove(request.Key);

            if (_loaded.ContainsKey(request.Key))
            {
                SpatialBundleLoader.Release(loadResult.BundleFullPath, unloadAllLoadedObjects: false);
                Profiler.EndSample();
                yield break;
            }

            Vector3 worldPos = ResolveInstanceWorldPosition(request);
            bool isProxyLod = IsProxyLod(request.LodLevel);
            GameObject instance = null;

            Profiler.BeginSample("SpatialStreaming.Instantiate");
            if (loadResult.MeshDetail != null)
            {
                instance = InstantiateMeshDetail(loadResult.MeshDetail, worldPos, _contentRoot);
                yield return null;
            }
            else
            {
                yield return InstantiateLoadedPrefab(loadResult.Prefab, worldPos, _contentRoot, created => instance = created);
            }
            Profiler.EndSample();

            if (instance == null)
            {
                SpatialBundleLoader.Release(loadResult.BundleFullPath, unloadAllLoadedObjects: false);
                Profiler.EndSample();
                yield break;
            }

            instance.name = BuildInstanceName(request);

            bool isCoarseProxy = request.LodLevel == SpatialStreamingLodLevel.TileProxy ||
                                 request.LodLevel == SpatialStreamingLodLevel.SubcellProxy;

            if (!isProxyLod || isCoarseProxy)
            {
                if (!isCoarseProxy)
                {
                    yield return null;

                    Profiler.BeginSample("SpatialStreaming.GpuAttach");
                    SpatialStreamedMeshRoot.Attach(instance, _gpuSettings);
                    Profiler.EndSample();
                }

                if (_applyMaterialsOnLoad && _buildingSurfaceSettings != null && !loadResult.IsMeshDetail)
                {
                    Profiler.BeginSample("SpatialStreaming.ApplyMaterials");
                    string materialTileId = ResolveMaterialTileId(request);
                    Vector3 materialOrigin = ResolveMaterialTileOrigin(request, worldPos);
                    int remapped = SpatialBuildingMaterialApplier.ApplyFacadeMaterialsToCombinedInstance(
                        instance,
                        materialTileId,
                        materialOrigin,
                        _manifest?.TileSizeMeters ?? 1000,
                        _buildingSurfaceSettings);
                    Profiler.EndSample();

                    if (_logStreaming && remapped > 0)
                    {
                        Debug.Log(
                            $"[ZGConnect.Spatial] Remapped {remapped} material slot(s) on '{request.Key}'");
                    }
                }
            }

            float spawnMs = Time.realtimeSinceStartup * 1000f - spawnStartMs;
            if (_enablePerformanceProfiling)
                _performanceTracker.RecordSpawn(request.Key, spawnMs);

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
            };

            _totalInstantiated++;

            if (request.LodLevel == SpatialStreamingLodLevel.Detail &&
                !string.IsNullOrEmpty(request.TileId) &&
                !string.IsNullOrEmpty(request.SubcellId))
            {
                UnloadSubcellProxy(request.TileId, request.SubcellId);
                UnloadTileProxyIfProgressiveDetail(request.TileId);
            }

            if (!string.IsNullOrEmpty(request.TileId) && IsTileDetailComplete(request.TileId))
                UnloadCoarseLodForTile(request.TileId);

            UpdateLodVisibility();

            if (_logStreaming)
            {
                int renderers = instance.GetComponentsInChildren<MeshRenderer>(true).Length;
                string detailProgress = request.LodLevel == SpatialStreamingLodLevel.Detail &&
                                        !string.IsNullOrEmpty(request.TileId)
                    ? $" detail={CountLoadedDetailSubcellsForTile(request.TileId)}/" +
                      $"{CountRequiredDetailSubcellsForTile(request.TileId)}"
                    : string.Empty;
                Debug.Log(
                    $"[ZGConnect.Spatial] Loaded '{request.Key}' lod={request.LodLevel} at {worldPos} " +
                    $"renderers={renderers}{detailProgress} spawnMs={spawnMs:F1} from {request.BundleRel}");
            }

            Profiler.EndSample();
        }

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
            return subcell != null && subcell.UsesMeshDetail;
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

        static GameObject InstantiateMeshDetail(
            SpatialMeshDetailAsset detail,
            Vector3 worldPos,
            Transform parent)
        {
            if (detail == null)
                return null;

            var root = new GameObject("SpatialMeshDetail");
            if (parent != null)
                root.transform.SetParent(parent, false);
            root.transform.SetPositionAndRotation(worldPos, Quaternion.identity);
            root.transform.localScale = Vector3.one;

            SpatialMeshDetailAsset.RendererEntry[] entries = detail.Renderers;
            if (entries == null || entries.Length == 0)
                return root;

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
            }

            return root;
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
            }

            onComplete(instance);
#else
            onComplete(Object.Instantiate(prefab, worldPos, Quaternion.identity, parent));
            yield break;
#endif
        }

        void UpdateLodVisibility()
        {
            if (_cam == null)
                return;

            foreach (SpatialLoadedSubcellRecord record in _loaded.Values)
            {
                if (record?.Root == null)
                    continue;

                bool visible = ShouldBlockBeVisible(record);
                if (record.Root.activeSelf != visible)
                    record.Root.SetActive(visible);
            }
        }

        bool ShouldBlockBeVisible(SpatialLoadedSubcellRecord record)
        {
            switch (record.LodLevel)
            {
                case SpatialStreamingLodLevel.Detail:
                    return true;

                case SpatialStreamingLodLevel.SubcellProxy:
                    return !IsSubcellDetailLoaded(record.TileId, record.SubcellId);

                case SpatialStreamingLodLevel.TileProxy:
                    return !ShouldHideTileProxyForTile(record.TileId);

                case SpatialStreamingLodLevel.Hlod2x2:
                case SpatialStreamingLodLevel.Hlod4x4:
                    return !IsSupertileFullySuperseded(record);

                default:
                    return true;
            }
        }

        /// <summary>
        /// Hide a supertile when every child tile is covered by finer LOD, or when any child
        /// has fully swapped to detail (monolithic mesh cannot mask per-tile).
        /// </summary>
        bool IsSupertileFullySuperseded(SpatialLoadedSubcellRecord supertileRecord)
        {
            if (_manifest == null || _cam == null)
                return false;

            SpatialSupertileManifestEntry entry = _manifest.FindSupertile(supertileRecord.SubcellId);
            if (entry?.ChildTileIds == null || entry.ChildTileIds.Count == 0)
                return false;

            Vector3 camPos = _cam.position;
            int tileSize = _manifest.TileSizeMeters;

            foreach (string tileId in entry.ChildTileIds)
            {
                if (StillNeedsSupertileCoverageForTile(tileId, camPos, tileSize, supertileRecord))
                    return false;
            }

            return true;
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

        bool ShouldHideTileProxyForTile(string tileId)
        {
            if (string.IsNullOrEmpty(tileId))
                return false;

            SpatialTileManifestEntry tile = _manifest?.FindTile(tileId);
            if (tile == null)
                return IsTileDetailComplete(tileId);

            if (tile.UsesSubcells)
                return CountLoadedDetailSubcellsForTile(tileId) > 0;

            return IsTileDetailComplete(tileId);
        }

        void UnloadCoarseLodForTile(string tileId)
        {
            if (string.IsNullOrEmpty(tileId))
                return;

            SpatialTileManifestEntry tile = _manifest?.FindTile(tileId);

            string proxyKey = SpatialStreamingHlodEvaluator.BuildProxyKey(tileId);
            if (_loaded.ContainsKey(proxyKey))
                UnloadBlock(proxyKey);

            foreach (SpatialSubcellManifestEntry subcell in tile?.Subcells ?? new List<SpatialSubcellManifestEntry>())
            {
                if (subcell == null || string.IsNullOrEmpty(subcell.SubcellId))
                    continue;

                UnloadSubcellProxy(tileId, subcell.SubcellId);
            }

            foreach (string key in _loaded.Keys.ToList())
            {
                if (!_loaded.TryGetValue(key, out SpatialLoadedSubcellRecord record))
                    continue;

                if (record.LodLevel != SpatialStreamingLodLevel.Hlod2x2 &&
                    record.LodLevel != SpatialStreamingLodLevel.Hlod4x4)
                {
                    continue;
                }

                SpatialSupertileManifestEntry entry = _manifest?.FindSupertile(record.SubcellId);
                if (entry?.ChildTileIds != null && entry.ChildTileIds.Contains(tileId))
                    UnloadBlock(key);
            }
        }

        bool StillNeedsSupertileCoverageForTile(
            string tileId,
            Vector3 camPos,
            int tileSize,
            SpatialLoadedSubcellRecord contextSupertile)
        {
            SpatialTileManifestEntry tile = _manifest.FindTile(tileId);
            if (tile == null)
                return true;

            float tileDist = SpatialTileDistanceUtility.TileBoundaryDistance(
                camPos, tile.GetUnityPosition(), tileSize);

            if (IsTileDetailComplete(tileId))
                return false;

            if (IsTileDetailLoading(tileId, tileDist) && HasProxyForTile(tileId))
                return false;

            if (IsTileCoveredByFinerLod(tileId, tileDist))
                return false;

            if (contextSupertile.LodLevel == SpatialStreamingLodLevel.Hlod4x4 &&
                IsTileCoveredByVisibleHlod2Supertile(tileId, camPos, tileSize))
            {
                return false;
            }

            if (!_enableHlod || tileDist > _hlodDistances.proxyUnloadMeters)
                return true;

            return true;
        }

        bool IsTileDetailLoading(string tileId, float tileDist)
        {
            if (tileDist > _hlodDistances.detailLoadMeters)
                return false;

            return CountLoadedDetailSubcellsForTile(tileId) > 0 && !IsTileDetailComplete(tileId);
        }

        bool IsTileCoveredByFinerLod(string tileId, float tileDist)
        {
            if (IsTileDetailComplete(tileId))
                return true;

            if (IsTileDetailLoading(tileId, tileDist))
                return HasProxyForTile(tileId);

            if (tileDist <= _hlodDistances.proxyUnloadMeters && HasProxyForTile(tileId))
                return true;

            return false;
        }

        bool HasProxyForTile(string tileId)
        {
            if (string.IsNullOrEmpty(tileId))
                return false;

            if (_loaded.ContainsKey(SpatialStreamingHlodEvaluator.BuildProxyKey(tileId)))
                return true;

            SpatialTileManifestEntry tile = _manifest?.FindTile(tileId);
            if (tile?.Subcells == null)
                return false;

            foreach (SpatialSubcellManifestEntry subcell in tile.Subcells)
            {
                if (subcell == null || string.IsNullOrEmpty(subcell.SubcellId))
                    continue;

                if (_loaded.ContainsKey(SpatialStreamingHlodEvaluator.BuildSubcellProxyKey(tileId, subcell.SubcellId)))
                    return true;
            }

            return false;
        }

        bool IsTileCoveredByVisibleHlod2Supertile(string tileId, Vector3 camPos, int tileSize)
        {
            if (!SpatialTileIdUtility.TryParse(tileId, out int left, out int bottom))
                return false;

            int tileSpan = tileSize;
            foreach (SpatialLoadedSubcellRecord record in _loaded.Values)
            {
                if (record.LodLevel != SpatialStreamingLodLevel.Hlod2x2 ||
                    record.Root == null ||
                    !record.Root.activeSelf)
                {
                    continue;
                }

                int blockSpan = tileSize * record.HlodFactor;
                bool insideBlock = left >= record.BlockLeft &&
                                   left + tileSpan <= record.BlockLeft + blockSpan &&
                                   bottom >= record.BlockBottom &&
                                   bottom + tileSpan <= record.BlockBottom + blockSpan;
                if (!insideBlock)
                    continue;

                if (!StillNeedsSupertileCoverageForTile(tileId, camPos, tileSize, record))
                    return true;
            }

            return false;
        }

        bool IsSubcellDetailLoaded(string tileId, string subcellId)
        {
            if (string.IsNullOrEmpty(tileId) || string.IsNullOrEmpty(subcellId))
                return false;

            if (subcellId == "tile_coarse")
                return IsTileDetailComplete(tileId);

            return _loaded.ContainsKey(SpatialStreamingHlodEvaluator.BuildDetailKey(tileId, subcellId));
        }

        bool IsTileDetailComplete(string tileId)
        {
            if (string.IsNullOrEmpty(tileId) || _manifest == null)
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
            var lines = new List<string>(16)
            {
                $"HLOD: {(_enableHlod ? "on" : "off")}",
                $"Manifest tiles: {_manifest?.Tiles?.Count ?? 0}  supertiles: {_manifest?.Supertiles?.Count ?? 0}",
                $"Pending: {stats.Pending}  Loading: {stats.Loading}",
                $"Loaded total: {stats.LoadedTotal}",
                $"  detail: {stats.LoadedDetail}  subcell proxy: {stats.LoadedSubcellProxy}  tile proxy: {stats.LoadedTileProxy}",
                $"  hlod2x2: {stats.LoadedHlod2x2}  hlod4x4: {stats.LoadedHlod4x4}",
                $"Visible blocks: {stats.VisibleBlocks}",
                $"Hidden (substituted): {stats.HiddenBySubstitution}",
                $"Instantiated (lifetime): {stats.TotalInstantiated}",
                $"Load failures: {stats.TotalLoadFailures}",
            };

            if (!_datasetBundlesAvailable)
                lines.Add("Dataset: bundles_spatial MISSING — run Bake");

            if (stats.BlockedByLoadFailure > 0)
                lines.Add($"Load retry blocked: {stats.BlockedByLoadFailure}");

            if (!string.IsNullOrEmpty(stats.LastLoadError))
                lines.Add($"Last error: {stats.LastLoadError}");

            lines.Add($"Visible mesh renderers: {stats.TotalMeshRenderers}");
            lines.Add($"Detail tile: {_detailTileLoadDistanceMeters:F0} / {_detailTileUnloadDistanceMeters:F0} m");
            lines.Add($"Proxy: {_proxyLoadDistanceMeters:F0} / {_proxyUnloadDistanceMeters:F0} m");
            lines.Add($"HLOD2: {_hlod2LoadDistanceMeters:F0} / {_hlod2UnloadDistanceMeters:F0} m");
            lines.Add($"HLOD4: {_hlod4LoadDistanceMeters:F0} / {_hlod4UnloadDistanceMeters:F0} m");

            if (_cam != null)
                lines.Add($"Camera: {_cam.position.x:F0}, {_cam.position.y:F0}, {_cam.position.z:F0}");

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

            _debugHud.SetVisible(true);
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
            int detail = 0;
            int subcellProxy = 0;
            int proxy = 0;
            int hlod2 = 0;
            int hlod4 = 0;
            int visible = 0;
            int renderers = 0;

            foreach (SpatialLoadedSubcellRecord record in _loaded.Values)
            {
                switch (record.LodLevel)
                {
                    case SpatialStreamingLodLevel.Detail:
                        detail++;
                        break;
                    case SpatialStreamingLodLevel.SubcellProxy:
                        subcellProxy++;
                        break;
                    case SpatialStreamingLodLevel.TileProxy:
                        proxy++;
                        break;
                    case SpatialStreamingLodLevel.Hlod2x2:
                        hlod2++;
                        break;
                    case SpatialStreamingLodLevel.Hlod4x4:
                        hlod4++;
                        break;
                }

                if (record.Root != null && record.Root.activeSelf)
                {
                    visible++;
                    renderers += record.Root.GetComponentsInChildren<MeshRenderer>(true).Length;
                }
            }

            _stats = new SpatialStreamingStats
            {
                Pending = _pending.Count,
                Loading = _loading.Count,
                LoadedTotal = _loaded.Count,
                LoadedDetail = detail,
                LoadedSubcellProxy = subcellProxy,
                LoadedTileProxy = proxy,
                LoadedHlod2x2 = hlod2,
                LoadedHlod4x4 = hlod4,
                VisibleBlocks = visible,
                HiddenBySubstitution = _loaded.Count - visible,
                TotalInstantiated = _totalInstantiated,
                TotalLoadFailures = _totalLoadFailures,
                TotalMeshRenderers = renderers,
                BlockedByLoadFailure = _loadBlockedUntil.Count,
                LastLoadError = _lastLoadError,
            };
        }

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

            if (!string.IsNullOrEmpty(record.BundleFullPath))
                SpatialBundleLoader.Release(record.BundleFullPath, unloadAllLoadedObjects: false);

            _loaded.Remove(key);
            UpdateLodVisibility();

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
            foreach (string key in _loaded.Keys.ToList())
                UnloadBlock(key);
        }
    }
}
