using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using ZGConnect;
using ZGConnect.RealtimeStreaming;

namespace ZGConnect.PhotogrammetryStreaming
{
    [DisallowMultipleComponent]
    public class PhotogrammetryStreamingController : MonoBehaviour
    {
        [Header("Configuration")]
        [SerializeField] PhotogrammetryApiSettings _apiSettings;
        [SerializeField] PhotogrammetryRegionPreset _regionPreset;
        [SerializeField] PhotogrammetryLodProfile _lodProfile;
        [SerializeField] Camera _targetCamera;
        [SerializeField] PhotogrammetryAttributionUI _attributionUi;

        [Header("Runtime")]
        [SerializeField] Transform _tileRoot;
        [SerializeField] float _checkInterval = 0.25f;

        Google3DTilesSession _session;
        Tile3DNode _rootNode;
        Matrix4d _tilesetRootTransform = Matrix4d.Identity;
        EcefToUnityTransform _ecefTransform;
        ZagrebGeographicClipper _clipper;
        TilesetTraverser _traverser;
        TileMemoryCache _memoryCache;
        TileDiskCache _diskCache;

        readonly HashSet<string> _desiredUris = new();
        readonly HashSet<string> _preloadUris = new();
        readonly HashSet<string> _loadedUris = new();
        readonly Queue<string> _glbQueue = new();
        readonly Queue<string> _jsonQueue = new();
        readonly HashSet<string> _inFlight = new();
        readonly Dictionary<string, Tile3DNode> _contentUriToNode = new();

        float _nextCheck;
        float _nextHeartbeat;
        bool _initialized;
        bool _loadProcessorRunning;
        string _status = "Idle";

        public string Status => _status;

        [Header("Rotation tuning (Play mode)")]
        [SerializeField] PhotogrammetryRotationTuning _rotationTuning = new();
        [SerializeField] float _rotationReloadDebounceSec = 0.35f;

        [Header("Debug")]
        [SerializeField] bool _verboseLogging = true;
        [Tooltip("Load only the N nearest GLB tiles to the camera (for rotation/position experiments).")]
        [SerializeField] bool _debugLimitGlbLoads;
        [SerializeField, Range(1, 4)] int _debugMaxGlbTiles = 1;
        [SerializeField] bool _debugLogTilePlacement = true;

        readonly Dictionary<string, byte[]> _glbBytesByUri = new();
        int _debugCapFingerprint;
        int _rotationTuningFingerprint;
        float _rotationReloadScheduledAt = -1f;
        bool _rotationReloadRunning;
        bool _rotationReloadPending;
        Coroutine _rotationDebounceCoroutine;

        int _diagDesired;
        int _diagPreload;
        int _diagLoaded;
        bool _loggedClipDiag;

        void Awake()
        {
            ResolveSerializedReferences();

            if (_targetCamera == null)
                _targetCamera = Camera.main;
            if (_tileRoot == null)
            {
                var go = new GameObject("PhotogrammetryTiles");
                go.transform.SetParent(transform, false);
                _tileRoot = go.transform;
            }
        }

        void ResolveSerializedReferences()
        {
            _apiSettings ??= PhotogrammetryDefaults.LoadApiSettings();
            _regionPreset ??= PhotogrammetryDefaults.LoadRegionPreset();
            _lodProfile ??= PhotogrammetryDefaults.LoadLodProfile();
        }

        void Start()
        {
            ResolveSerializedReferences();

            if (_regionPreset == null || _lodProfile == null)
            {
                _status = "Missing region or LOD profile. Assign assets on PhotogrammetryStreamer or run ZG Connect > Photogrammetry > Create Zagreb Streaming Scene.";
                Debug.LogError("[Photogrammetry] " + _status);
                return;
            }

            if (_apiSettings == null)
            {
                _status = "Missing API settings asset.";
                Debug.LogError("[Photogrammetry] " + _status);
                return;
            }

            if (string.IsNullOrWhiteSpace(_apiSettings.apiKey) &&
                _apiSettings.tileSourceMode == TileSourceMode.LiveGoogle)
            {
                _status = "API key is empty. Set it in PhotogrammetryApiSettings or via ZG Connect > Photogrammetry > Streaming Settings.";
                Debug.LogError("[Photogrammetry] " + _status);
                return;
            }

            Log($"Starting ({_apiSettings.tileSourceMode})...");

            ZGConnectCoordinates.Configure(459479, 5074937, 95f);
            _ecefTransform = new EcefToUnityTransform(_regionPreset);
            _clipper = new ZagrebGeographicClipper(_regionPreset, _lodProfile.regionClipEnabled);
            _traverser = new TilesetTraverser(_lodProfile, _clipper, _ecefTransform);
            _memoryCache = new TileMemoryCache(_lodProfile.maxCachedBytes);
            _session = new Google3DTilesSession();

            _ecefTransform.ValidateLandmark("Zagreb Cathedral approx", 45.81444, 15.97889, 105);
            _rotationTuning ??= new PhotogrammetryRotationTuning();
            _rotationTuningFingerprint = _rotationTuning.ComputeFingerprint();
            _debugCapFingerprint = ComputeDebugCapFingerprint();
            StartCoroutine(InitializeCoroutine());
        }

        int ComputeDebugCapFingerprint() =>
            (_debugLimitGlbLoads ? 1 : 0) * 1000 + _debugMaxGlbTiles;

        IEnumerator InitializeCoroutine()
        {
            _status = "Initializing...";

            if (_apiSettings.tileSourceMode == TileSourceMode.LiveGoogle)
            {
                string err = null;
                yield return _session.StartSessionCoroutine(_apiSettings, e => err = e);
                if (!string.IsNullOrEmpty(err))
                {
                    _status = err;
                    Debug.LogError("[Photogrammetry] " + err);
                    yield break;
                }

                if (_lodProfile.enableDiskCache)
                {
                    string hash = _session.SessionToken ?? "nogoogle";
                    _diskCache = new TileDiskCache(hash, _lodProfile.maxDiskCacheBytes, _lodProfile.diskCacheTtlHours);
                }

                ParseRootTilesetFromSession();
            }
            else
            {
                string path = ResolveLocalTilesetPath(_apiSettings.localTilesetPath);
                if (!File.Exists(path))
                {
                    _status = $"Local tileset not found: {path}";
                    Debug.LogError("[Photogrammetry] " + _status);
                    yield break;
                }

                string json = File.ReadAllText(path);
                _rootNode = Tile3DParser.ParseTileset(json, "file://" + path, out _tilesetRootTransform);
                IndexContentNodes(_rootNode);
                _initialized = _rootNode != null;
                _status = _initialized ? "Local bake ready." : "Failed to parse local tileset.";
            }
        }

        void ParseRootTilesetFromSession()
        {
            if (string.IsNullOrEmpty(_session?.LastRootJson))
            {
                _status = "Root tileset JSON missing after session start.";
                Debug.LogError("[Photogrammetry] " + _status);
                return;
            }

            _rootNode = Tile3DParser.ParseTileset(
                _session.LastRootJson,
                PhotogrammetryApiSettings.DefaultRootUrl,
                out _tilesetRootTransform);
            IndexContentNodes(_rootNode);
            _initialized = _rootNode != null;
            _status = _initialized ? "Live session ready." : "Failed to parse root tileset.";
            if (_initialized)
            {
                Log($"Root tileset parsed. Root children={_rootNode.Children.Count}, content={_rootNode.ContentUri ?? "none"}");
                RunTraversalPass();
            }
            else
                Debug.LogError("[Photogrammetry] " + _status);
        }

        void RunTraversalPass()
        {
            if (!_initialized || _rootNode == null || _targetCamera == null || _traverser == null)
                return;

            _traverser.CollectDesiredTiles(
                _rootNode,
                _targetCamera,
                _desiredUris,
                _preloadUris,
                _targetCamera.transform.position,
                _loadedUris);

            ApplyDebugGlbTileCap();
            PruneGlbQueueToDesired();

            EnqueueLoads(_desiredUris, _glbQueue);
            EnqueueLoads(_preloadUris, _jsonQueue);

            if (_verboseLogging &&
                (_desiredUris.Count != _diagDesired || _preloadUris.Count != _diagPreload))
            {
                _diagDesired = _desiredUris.Count;
                _diagPreload = _preloadUris.Count;
                Log($"Traversal desired={_desiredUris.Count} preload={_preloadUris.Count} glbQ={_glbQueue.Count} jsonQ={_jsonQueue.Count}");
            }

            if (_verboseLogging && !_loggedClipDiag && _preloadUris.Count == 0 && _desiredUris.Count == 0 &&
                _rootNode?.Children.Count > 0)
            {
                _loggedClipDiag = true;
                LogClipDiagnostics();
                LogNearbyGlbTiles(_lodProfile.maxFocusRadiusM);
            }

            if (!_loadProcessorRunning && (_glbQueue.Count > 0 || _jsonQueue.Count > 0))
                StartCoroutine(ProcessLoadQueueCoroutine());
        }

        void IndexContentNodes(Tile3DNode node)
        {
            if (node == null)
                return;
            if (!string.IsNullOrEmpty(node.ContentUri))
                _contentUriToNode[node.ContentUri] = node;
            foreach (var child in node.Children)
                IndexContentNodes(child);
        }

        void Update()
        {
            if (!_initialized || _rootNode == null || _targetCamera == null)
                return;

            if (_session != null && _session.NeedsRefresh() && _apiSettings.tileSourceMode == TileSourceMode.LiveGoogle)
                StartCoroutine(RefreshSessionCoroutine());

            if (Time.unscaledTime < _nextCheck)
                return;
            _nextCheck = Time.unscaledTime + _checkInterval;

            RunTraversalPass();
            ProcessUnload();

            if (_verboseLogging && Time.unscaledTime >= _nextHeartbeat)
            {
                _nextHeartbeat = Time.unscaledTime + 2f;
                Log($"Status desired={_desiredUris.Count} preload={_preloadUris.Count} glbLoaded={_memoryCache.Count} urisTracked={_loadedUris.Count} glbQ={_glbQueue.Count} jsonQ={_jsonQueue.Count} inFlight={_inFlight.Count}");
            }

            if (!_loadProcessorRunning && (_glbQueue.Count > 0 || _jsonQueue.Count > 0 || _inFlight.Count > 0))
                StartCoroutine(ProcessLoadQueueCoroutine());

            PollRotationTuningChange();
            PollDebugCapChange();
        }

        void PollDebugCapChange()
        {
            if (!_initialized)
                return;

            int fp = ComputeDebugCapFingerprint();
            if (fp == _debugCapFingerprint)
                return;

            _debugCapFingerprint = fp;
            Log(_debugLimitGlbLoads
                ? $"Debug GLB cap enabled: max {_debugMaxGlbTiles} tile(s)."
                : "Debug GLB cap disabled — using LOD profile limit.");
            RunTraversalPass();
        }

        void ApplyDebugGlbTileCap()
        {
            if (!_debugLimitGlbLoads)
                return;

            int cap = Mathf.Max(1, _debugMaxGlbTiles);
            var glbUris = new List<string>();
            foreach (string uri in _desiredUris)
            {
                if (TileContentUri.IsGlb(uri))
                    glbUris.Add(uri);
            }

            if (glbUris.Count <= cap)
                return;

            glbUris.Sort((a, b) =>
                GetUriHorizontalDistanceToCamera(a).CompareTo(GetUriHorizontalDistanceToCamera(b)));

            var keep = new HashSet<string>();
            for (int i = 0; i < cap && i < glbUris.Count; i++)
                keep.Add(glbUris[i]);

            var remove = new List<string>();
            foreach (string uri in _desiredUris)
            {
                if (TileContentUri.IsGlb(uri) && !keep.Contains(uri))
                    remove.Add(uri);
            }

            foreach (string uri in remove)
                _desiredUris.Remove(uri);
        }

        void PruneGlbQueueToDesired()
        {
            if (!_debugLimitGlbLoads || _glbQueue.Count == 0)
                return;

            int count = _glbQueue.Count;
            for (int i = 0; i < count; i++)
            {
                string uri = _glbQueue.Dequeue();
                if (_desiredUris.Contains(uri))
                    _glbQueue.Enqueue(uri);
            }
        }

        float GetUriHorizontalDistanceToCamera(string uri)
        {
            if (_contentUriToNode.TryGetValue(uri, out var node) && node?.Bounds != null)
            {
                Vector3 center = _ecefTransform.EcefToUnity(Tile3DNodeTransform.GetWorldCenterEcef(node));
                var cam = _targetCamera.transform.position;
                return Vector3.Distance(new Vector3(cam.x, 0f, cam.z), new Vector3(center.x, 0f, center.z));
            }

            return float.MaxValue;
        }

        void PollRotationTuningChange()
        {
            if (!_initialized || _rotationTuning == null || _rotationReloadRunning)
                return;

            int fp = _rotationTuning.ComputeFingerprint();
            if (fp == _rotationTuningFingerprint)
                return;

            _rotationTuningFingerprint = fp;
            ScheduleRotationReload();
        }

        void ScheduleRotationReload()
        {
            if (_rotationReloadRunning)
            {
                _rotationReloadPending = true;
                return;
            }

            _rotationReloadScheduledAt = Time.unscaledTime + _rotationReloadDebounceSec;
            if (!isActiveAndEnabled)
                return;

            if (_rotationDebounceCoroutine != null)
                StopCoroutine(_rotationDebounceCoroutine);
            _rotationDebounceCoroutine = StartCoroutine(RotationReloadDebouncedCoroutine());
        }

        IEnumerator RotationReloadDebouncedCoroutine()
        {
            while (Time.unscaledTime < _rotationReloadScheduledAt)
                yield return null;

            yield return ReloadGlbTilesCoroutine();
        }

        IEnumerator ReloadGlbTilesCoroutine()
        {
            if (_rotationReloadRunning)
                yield break;

            var uris = new List<string>();
            foreach (string uri in _loadedUris)
            {
                if (TileContentUri.IsGlb(uri))
                    uris.Add(uri);
            }

            if (uris.Count == 0)
                yield break;

            _rotationReloadRunning = true;
            Log($"Rotation tuning changed — reloading {uris.Count} GLB tile(s)...");

            foreach (string uri in uris)
            {
                _loadedUris.Remove(uri);
                _memoryCache.Remove(uri);
                if (_attributionUi != null)
                    _attributionUi.UnregisterCopyright(GetCopyrightForUri(uri));
                _uriCopyright.Remove(uri);
            }

            int reloaded = 0;
            foreach (string uri in uris)
            {
                if (!_glbBytesByUri.TryGetValue(uri, out byte[] bytes) || bytes == null)
                    continue;

                var loadTask = LoadGlbTileAsync(uri, bytes);
                while (!loadTask.IsCompleted)
                    yield return null;

                if (loadTask.Result)
                {
                    _loadedUris.Add(uri);
                    reloaded++;
                }
            }

            _rotationReloadRunning = false;
            Log($"Rotation reload finished: {reloaded}/{uris.Count} GLB tile(s).");

            if (_rotationReloadPending)
            {
                _rotationReloadPending = false;
                _rotationTuningFingerprint = _rotationTuning.ComputeFingerprint();
                ScheduleRotationReload();
            }
        }

        void OnValidate()
        {
            _rotationTuning ??= new PhotogrammetryRotationTuning();
            if (!Application.isPlaying || !_initialized)
                return;

            int capFp = ComputeDebugCapFingerprint();
            if (capFp != _debugCapFingerprint)
            {
                _debugCapFingerprint = capFp;
                RunTraversalPass();
            }

            int fp = _rotationTuning.ComputeFingerprint();
            if (fp == _rotationTuningFingerprint)
                return;

            _rotationTuningFingerprint = fp;
            ScheduleRotationReload();
        }

        [ContextMenu("Reload GLB Tiles (rotation tuning)")]
        void ReloadGlbTilesFromContextMenu()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[Photogrammetry] Enter Play mode to reload GLB tiles.");
                return;
            }

            ScheduleRotationReload();
        }

        IEnumerator RefreshSessionCoroutine()
        {
            _status = "Refreshing session...";
            string err = null;
            yield return _session.StartSessionCoroutine(_apiSettings, e => err = e);
            _status = string.IsNullOrEmpty(err) ? "Session refreshed." : err;
        }

        void EnqueueLoads(HashSet<string> uris, Queue<string> queue)
        {
            foreach (string uri in uris)
            {
                if (string.IsNullOrEmpty(uri))
                    continue;
                if (_loadedUris.Contains(uri) || _inFlight.Contains(uri))
                    continue;
                if (!TileContentUri.IsTileContent(uri))
                    continue;
                if (QueueContains(queue, uri))
                    continue;

                queue.Enqueue(uri);
                Log($"Queued {(TileContentUri.IsGlb(uri) ? "GLB" : "tileset")} {TruncateUri(uri)}");
            }
        }

        static bool QueueContains(Queue<string> queue, string uri)
        {
            foreach (string item in queue)
            {
                if (item == uri)
                    return true;
            }
            return false;
        }

        void ProcessUnload()
        {
            var toRemove = new List<string>();
            foreach (string uri in _loadedUris)
            {
                if (!TileContentUri.IsGlb(uri))
                    continue;

                if (_desiredUris.Contains(uri))
                    continue;

                toRemove.Add(uri);
            }

            foreach (string uri in toRemove)
            {
                _loadedUris.Remove(uri);
                _memoryCache.Remove(uri);
                if (_attributionUi != null)
                    _attributionUi.UnregisterCopyright(GetCopyrightForUri(uri));
                _uriCopyright.Remove(uri);
            }
        }

        float EstimateUriDistance(string uri)
        {
            if (_memoryCache.TryGet(uri, out var go) && go != null)
            {
                return Vector3.Distance(
                    new Vector3(_targetCamera.transform.position.x, 0f, _targetCamera.transform.position.z),
                    new Vector3(go.transform.position.x, 0f, go.transform.position.z));
            }
            return float.MaxValue;
        }

        readonly Dictionary<string, string> _uriCopyright = new();

        string GetCopyrightForUri(string uri) =>
            _uriCopyright.TryGetValue(uri, out var c) ? c : null;

        IEnumerator ProcessLoadQueueCoroutine()
        {
            _loadProcessorRunning = true;
            int finalizedThisFrame = 0;
            float frameStart = Time.realtimeSinceStartup;
            int activeLoads = 0;

            int jsonLoadsThisBatch = 0;
            while ((_glbQueue.Count > 0 || _jsonQueue.Count > 0) &&
                   activeLoads < _lodProfile.maximumSimultaneousTileLoads)
            {
                if (RuntimeFrameBudget.ShouldYield(finalizedThisFrame, _lodProfile.maxTileFinalizesPerFrame,
                        frameStart, _lodProfile.tileFinalizeMsBudget))
                {
                    _loadProcessorRunning = false;
                    yield break;
                }

                string uri = DequeueNextUri(ref jsonLoadsThisBatch);
                if (uri == null)
                    break;

                if (_loadedUris.Contains(uri) || _inFlight.Contains(uri))
                    continue;

                _inFlight.Add(uri);
                activeLoads++;
                StartCoroutine(LoadUriCoroutine(uri, () =>
                {
                    _inFlight.Remove(uri);
                    activeLoads--;
                    finalizedThisFrame++;
                }));
            }

            while (_inFlight.Count > 0)
                yield return null;

            _loadProcessorRunning = false;
        }

        IEnumerator LoadUriCoroutine(string uri, Action onDone)
        {
            string fetchUrl = ResolveFetchUrl(uri);
            string cacheKey = TileDiskCache.NormalizeUrlForCache(fetchUrl);
            byte[] bytes = null;
            Log($"Downloading {TruncateUri(uri)}...");

            if (_apiSettings.tileSourceMode == TileSourceMode.LocalBaked &&
                !uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                string localPath = ResolveLocalContentPath(uri);
                if (File.Exists(localPath))
                    bytes = File.ReadAllBytes(localPath);
            }
            else if (_diskCache != null && _diskCache.TryRead(cacheKey, out bytes))
            {
                // cached
            }
            else
            {
                yield return DownloadBytesCoroutine(fetchUrl, b => bytes = b);
                if (bytes != null && _diskCache != null)
                    _diskCache.Write(cacheKey, bytes);
            }

            if (bytes == null)
            {
                onDone?.Invoke();
                yield break;
            }

            if (TileContentUri.IsExpandableTileset(uri))
            {
                yield return HandleChildTilesetJson(bytes, uri);
                _loadedUris.Add(uri);
                onDone?.Invoke();
                yield break;
            }

            var loadTask = LoadGlbTileAsync(uri, bytes);
            while (!loadTask.IsCompleted)
                yield return null;

            if (loadTask.Result)
            {
                _loadedUris.Add(uri);
                Log($"Loaded GLB {TruncateUri(uri)}");
                if (_debugLimitGlbLoads && _debugLogTilePlacement)
                    LogTilePlacementDiagnostics(uri, bytes);
            }
            else
                Debug.LogWarning($"[Photogrammetry] GLB instantiate failed: {TruncateUri(uri)}");

            onDone?.Invoke();
        }

        string DequeueNextUri(ref int jsonLoadsThisBatch)
        {
            if (_glbQueue.Count > 0)
                return _glbQueue.Dequeue();

            if (_jsonQueue.Count > 0 && jsonLoadsThisBatch < _lodProfile.maxJsonExpansionPerCollect)
            {
                jsonLoadsThisBatch++;
                return _jsonQueue.Dequeue();
            }

            return null;
        }

        static void MergeExternalTileset(Tile3DNode parent, Tile3DNode externalRoot)
        {
            parent.GeometricError = externalRoot.GeometricError;
            parent.Bounds = externalRoot.Bounds ?? parent.Bounds;
            parent.ContentUri = externalRoot.ContentUri;
            parent.Children.Clear();
            foreach (var child in externalRoot.Children)
            {
                child.Parent = parent;
                parent.Children.Add(child);
            }
        }

        IEnumerator HandleChildTilesetJson(byte[] bytes, string sourceUri)
        {
            string json = System.Text.Encoding.UTF8.GetString(bytes);
            var parsed = Tile3DParser.ParseTileset(json, PhotogrammetryApiSettings.DefaultRootUrl);
            if (parsed == null)
                yield break;

            if (_contentUriToNode.TryGetValue(sourceUri, out var parentNode))
            {
                MergeExternalTileset(parentNode, parsed);
                Tile3DParser.RecomputeTransforms(_rootNode, _tilesetRootTransform);
                _contentUriToNode.Clear();
                IndexContentNodes(_rootNode);
                Log($"Expanded tileset {TruncateUri(sourceUri)} → children={parentNode.Children.Count}, glb={TileContentUri.IsGlb(parentNode.ContentUri)}");
            }
            else if (_rootNode == null)
            {
                _rootNode = parsed;
                IndexContentNodes(_rootNode);
            }

            yield return null;
        }

        async Task<bool> LoadGlbTileAsync(string uri, byte[] bytes)
        {
            Vector3d parsedRtc = GltFastTileInstantiator.ParseRtcCenterEcef(bytes);
            Vector3d fallbackEcef = _ecefTransform.AnchorEcef;
            if (_contentUriToNode.TryGetValue(uri, out var fallbackNode) && fallbackNode?.Bounds != null)
                fallbackEcef = Tile3DNodeTransform.GetWorldCenterEcef(fallbackNode);
            Vector3d rtcEcef = CesiumStyleTilePlacement.ResolveRtcCenter(parsedRtc, fallbackEcef);

            Matrix4d tileMatrix = Matrix4d.Identity;
            if (_contentUriToNode.TryGetValue(uri, out var tileNode))
                tileMatrix = tileNode.TransformToEcef;

            var inst = await GltFastTileInstantiator.InstantiateAsync(
                bytes,
                _tileRoot,
                _ecefTransform,
                rtcEcef,
                Path.GetFileName(uri),
                _rotationTuning,
                tileMatrix);

            if (!inst.Success)
                return false;

            _glbBytesByUri[uri] = bytes;

            Vector3 placed = GetTilePlacementWorldPosition(inst.Root);
            if (!IsPlausibleTilePosition(placed, _ecefTransform))
            {
                Destroy(inst.Root);
                Debug.LogWarning(
                    $"[Photogrammetry] Discarded GLB with implausible position: {TruncateUri(uri)} " +
                    $"pos={placed} distFromAnchor={HorizontalErrorM(placed, _ecefTransform.UnityAnchor):F0}m");
                return false;
            }

            _memoryCache.Add(uri, inst.Root, inst.Holder, inst.EstimatedBytes);
            if (!string.IsNullOrEmpty(inst.Copyright))
            {
                _uriCopyright[uri] = inst.Copyright;
                _attributionUi?.RegisterCopyright(inst.Copyright);
            }

            return true;
        }

        void LogTilePlacementDiagnostics(string uri, byte[] bytes)
        {
            if (_memoryCache.TryGet(uri, out var root) == false || root == null)
                return;

            Vector3d rtcEcef = GltFastTileInstantiator.ParseRtcCenterEcef(bytes);
            Vector3d treeCenterEcef = Vector3d.Zero;
            if (_contentUriToNode.TryGetValue(uri, out var node) && node?.Bounds != null)
                treeCenterEcef = Tile3DNodeTransform.GetWorldCenterEcef(node);

            bool hasRtc = rtcEcef.Magnitude > 1_000_000.0;
            bool hasTree = treeCenterEcef.Magnitude > 1.0;
            Vector3 unityRtc = hasRtc ? _ecefTransform.EcefToUnity(rtcEcef) : Vector3.zero;
            Vector3 unityTree = hasTree ? _ecefTransform.EcefToUnity(treeCenterEcef) : Vector3.zero;
            Vector3 placed = GetTilePlacementWorldPosition(root);

            Transform content = root.transform.Find("Content");
            string pivotRot = content != null
                ? content.rotation.eulerAngles.ToString("F1")
                : "n/a";

            Matrix4d tileMatrix = Matrix4d.Identity;
            if (_contentUriToNode.TryGetValue(uri, out var tileNode))
                tileMatrix = tileNode.TransformToEcef;
            bool useTileTree = _rotationTuning != null && _rotationTuning.applyTileTreeTransform;
            Matrix4d positionToEcef = useTileTree ? tileMatrix : Matrix4d.Identity;
            if (rtcEcef.Magnitude >= 1_000_000.0)
                positionToEcef = Matrix4d.Multiply(positionToEcef, Matrix4d.Translation(rtcEcef));
            Matrix4d modelToEcef = positionToEcef;
            _ecefTransform.DecomposeModelToEcefToUnity(
                modelToEcef, out Vector3 expectedPos, out _, out _);

            double rtcTreeDeltaM = rtcEcef.Magnitude > 1_000_000.0 && treeCenterEcef.Magnitude > 1.0
                ? (rtcEcef - treeCenterEcef).Magnitude
                : 0.0;

            string mode = _rotationTuning?.placementMode.ToString() ?? "CesiumParity";
            Debug.Log(
                $"[Photogrammetry] Tile placement '{TruncateUri(uri)}' ({mode}):\n" +
                $"  rtc-tree center delta: {rtcTreeDeltaM:F1} m\n" +
                $"  expected (Cesium chain): {expectedPos}\n" +
                $"  unity from tree center: {unityTree}\n" +
                $"  unity from rtc: {unityRtc}\n" +
                $"  placed content position: {placed}\n" +
                $"  expected horizontal error: {HorizontalErrorM(placed, expectedPos):F2} m\n" +
                $"  content world euler: {pivotRot}");
        }

        static Vector3 GetTilePlacementWorldPosition(GameObject root)
        {
            if (root == null)
                return Vector3.zero;
            Transform content = root.transform.Find("Content");
            return content != null ? content.position : root.transform.position;
        }

        static float HorizontalErrorM(Vector3 a, Vector3 b) =>
            Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));

        IEnumerator DownloadBytesCoroutine(string url, Action<byte[]> onDone)
        {
            using var req = UnityWebRequest.Get(url);
            req.timeout = 60;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[Photogrammetry] Download failed ({req.responseCode}): {TruncateUrlForLog(url)} — {req.error}");
                onDone?.Invoke(null);
                yield break;
            }
            onDone?.Invoke(req.downloadHandler.data);
        }

        string ResolveFetchUrl(string uri)
        {
            if (_apiSettings.tileSourceMode == TileSourceMode.LocalBaked)
            {
                if (uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    return uri;
                string baseDir = Path.GetDirectoryName(ResolveLocalTilesetPath(_apiSettings.localTilesetPath));
                return Path.Combine(baseDir ?? "", uri).Replace('\\', '/');
            }

            return _session?.AppendSessionAndKey(uri) ?? uri;
        }

        static string ResolveLocalTilesetPath(string relativeOrAbsolute)
        {
            if (Path.IsPathRooted(relativeOrAbsolute))
                return relativeOrAbsolute;
            return Path.Combine(Application.streamingAssetsPath, relativeOrAbsolute);
        }

        string ResolveLocalContentPath(string relativeUri)
        {
            string baseDir = Path.GetDirectoryName(ResolveLocalTilesetPath(_apiSettings.localTilesetPath));
            string combined = Path.Combine(baseDir ?? Application.streamingAssetsPath, relativeUri);
            return Path.GetFullPath(combined);
        }

        void LogClipDiagnostics()
        {
            int n = Mathf.Min(_rootNode.Children.Count, 4);
            for (int i = 0; i < n; i++)
            {
                var child = _rootNode.Children[i];
                LogNodeDiag($"Root child[{i}]", child);

                int gc = Mathf.Min(child.Children.Count, 2);
                for (int g = 0; g < gc; g++)
                {
                    var grandchild = child.Children[g];
                    LogNodeDiag($"  child[{i}].child[{g}]", grandchild);

                    int ggc = Mathf.Min(grandchild.Children.Count, 2);
                    for (int gg = 0; gg < ggc; gg++)
                        LogNodeDiag($"    child[{i}].child[{g}].child[{gg}]", grandchild.Children[gg]);
                }
            }
        }

        void LogNearbyGlbTiles(float maxDistM)
        {
            if (_rootNode == null || _targetCamera == null || _ecefTransform == null)
                return;

            int count = 0;
            Walk(_rootNode);
            Log($"Nearby GLB within {maxDistM:F0}m: {count}");

            void Walk(Tile3DNode node)
            {
                if (node == null)
                    return;

                if (TileContentUri.IsGlb(node.ContentUri) && node.Bounds != null)
                {
                    var center = _ecefTransform.EcefToUnity(Tile3DNodeTransform.GetWorldCenterEcef(node));
                    float d = Vector3.Distance(
                        new Vector3(_targetCamera.transform.position.x, 0f, _targetCamera.transform.position.z),
                        new Vector3(center.x, 0f, center.z));
                    if (d <= maxDistM)
                    {
                        count++;
                        Log($"  Near GLB depth={node.Depth} dist={d:F0}m {TruncateUri(node.ContentUri)}");
                    }
                }

                foreach (var child in node.Children)
                    Walk(child);
            }
        }

        void LogNodeDiag(string label, Tile3DNode node)
        {
            bool clip = _clipper.IntersectsRegion(node);
            string bounds = node.Bounds == null ? "none" : node.Bounds.GetType().Name;
            string content = TileContentUri.IsExpandableTileset(node.ContentUri) ? "tileset" :
                TileContentUri.IsGlb(node.ContentUri) ? "glb" : "none";
            string dist = "";
            if (TileContentUri.IsGlb(node.ContentUri) && node.Bounds != null && _targetCamera != null)
            {
                var center = _ecefTransform.EcefToUnity(Tile3DNodeTransform.GetWorldCenterEcef(node));
                float d = Vector3.Distance(
                    new Vector3(_targetCamera.transform.position.x, 0f, _targetCamera.transform.position.z),
                    new Vector3(center.x, 0f, center.z));
                dist = $" dist={d:F0}m";
            }
            Log($"{label} clip={clip} bounds={bounds} content={content} children={node.Children.Count} depth={node.Depth}{dist}");
        }

        static bool IsPlausibleTilePosition(Vector3 worldPos, EcefToUnityTransform bridge)
        {
            if (!float.IsFinite(worldPos.x) || !float.IsFinite(worldPos.y) || !float.IsFinite(worldPos.z))
                return false;

            float distFromAnchor = HorizontalErrorM(worldPos, bridge.UnityAnchor);
            if (distFromAnchor > 15_000f)
                return false;
            if (worldPos.y < -500f || worldPos.y > 1_500f)
                return false;
            return true;
        }

        void Log(string message)
        {
            if (_verboseLogging)
                Debug.Log("[Photogrammetry] " + message);
        }

        static string TruncateUri(string uri)
        {
            if (string.IsNullOrEmpty(uri))
                return uri;
            int slash = uri.LastIndexOf('/');
            return slash >= 0 ? uri.Substring(slash + 1) : uri;
        }

        static string TruncateUrlForLog(string url)
        {
            if (string.IsNullOrEmpty(url))
                return url;
            int key = url.IndexOf("key=", StringComparison.OrdinalIgnoreCase);
            if (key < 0)
                return url.Length > 120 ? url.Substring(0, 120) + "..." : url;
            return url.Substring(0, key + 4) + "***";
        }

        void OnDestroy()
        {
            _memoryCache?.Clear();
            _glbBytesByUri.Clear();
        }
    }
}
