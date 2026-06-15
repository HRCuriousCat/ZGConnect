using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;
using ZGConnect;

namespace ZGConnect.RealtimeStreaming
{
    /// <summary>Initial terrain and optional building load state for loading screens.</summary>
    public readonly struct StreamingLoadProgress
    {
        public readonly int DesiredTerrainTileCount;
        public readonly int ReadyTerrainTileCount;
        public readonly int LoadingTerrainTileCount;
        public readonly int DesiredBuildingTileCount;
        public readonly int ReadyBuildingTileCount;
        public readonly int LoadingBuildingTileCount;
        public readonly bool TrackBuildings;
        public readonly bool IsStreamingActive;
        public readonly bool IsInitialPassPending;

        public StreamingLoadProgress(
            int desiredTerrainTileCount,
            int readyTerrainTileCount,
            int loadingTerrainTileCount,
            bool isStreamingActive,
            bool isInitialPassPending,
            int desiredBuildingTileCount = 0,
            int readyBuildingTileCount = 0,
            int loadingBuildingTileCount = 0,
            bool trackBuildings = false)
        {
            DesiredTerrainTileCount = desiredTerrainTileCount;
            ReadyTerrainTileCount = readyTerrainTileCount;
            LoadingTerrainTileCount = loadingTerrainTileCount;
            DesiredBuildingTileCount = desiredBuildingTileCount;
            ReadyBuildingTileCount = readyBuildingTileCount;
            LoadingBuildingTileCount = loadingBuildingTileCount;
            TrackBuildings = trackBuildings;
            IsStreamingActive = isStreamingActive;
            IsInitialPassPending = isInitialPassPending;
        }

        public int TotalDesired =>
            DesiredTerrainTileCount + (TrackBuildings ? DesiredBuildingTileCount : 0);

        public int TotalReady =>
            ReadyTerrainTileCount + (TrackBuildings ? ReadyBuildingTileCount : 0);

        public int TotalLoading =>
            LoadingTerrainTileCount + (TrackBuildings ? LoadingBuildingTileCount : 0);

        public float NormalizedProgress
        {
            get
            {
                if (!IsStreamingActive || IsInitialPassPending)
                    return 0f;

                if (TotalDesired <= 0)
                    return 1f;

                return Mathf.Clamp01((float)TotalReady / TotalDesired);
            }
        }

        /// <summary>Terrain and (when tracked) building tiles in the initial camera radius are ready.</summary>
        public bool IsInitialLoadComplete
        {
            get
            {
                if (!IsStreamingActive || IsInitialPassPending)
                    return false;

                bool terrainComplete = DesiredTerrainTileCount <= 0 ||
                    (ReadyTerrainTileCount >= DesiredTerrainTileCount &&
                     LoadingTerrainTileCount == 0);

                if (!TrackBuildings)
                    return terrainComplete;

                bool buildingsComplete = DesiredBuildingTileCount <= 0 ||
                    (ReadyBuildingTileCount >= DesiredBuildingTileCount &&
                     LoadingBuildingTileCount == 0);

                return terrainComplete && buildingsComplete;
            }
        }

        public bool IsInitialTerrainLoadComplete
        {
            get
            {
                if (!IsStreamingActive || IsInitialPassPending)
                    return false;

                if (DesiredTerrainTileCount <= 0)
                    return true;

                return ReadyTerrainTileCount >= DesiredTerrainTileCount && LoadingTerrainTileCount == 0;
            }
        }
    }

    /// <summary>Human-readable loading stage for intro / loading screens.</summary>
    public readonly struct StreamingLoadStatus
    {
        public readonly string StageLabel;
        public readonly string StageDetail;

        public StreamingLoadStatus(string stageLabel, string stageDetail = null)
        {
            StageLabel = stageLabel ?? string.Empty;
            StageDetail = stageDetail;
        }

        public static StreamingLoadStatus WithRatio(string stageLabel, int ready, int desired)
        {
            if (desired <= 0)
                return new StreamingLoadStatus(stageLabel);

            float percent = Mathf.Clamp01((float)ready / desired) * 100f;
            return new StreamingLoadStatus(stageLabel, $"{ready}/{desired} - {percent:F0}%");
        }
    }

    [DisallowMultipleComponent]
    public class RealtimeStreamingController : MonoBehaviour
    {
        [Header("Dataset")]
        [SerializeField] string manifestRelativePath = RuntimeStreamingPaths.DefaultManifestRelativePath;
        [SerializeField] Transform cameraTransform;
        [Tooltip("On Start, frame the camera on the dataset bounds when it is far from the terrain.")]
        [SerializeField] bool autoFocusCameraOnDataset = true;
        [Tooltip("Camera distance as a fraction of the dataset span (max of width/depth).")]
        [SerializeField] float autoFocusDistanceScale = 0.65f;
        [Tooltip("Hard cap on auto-focus camera Y (Unity world space, m). 0 = no limit. Does not affect flying.")]
        [SerializeField] float autoFocusMaxCameraHeightY;
        [Tooltip("Elevation above the horizon in degrees (0 = horizontal, 90 = top-down).")]
        [SerializeField] float autoFocusElevationDegrees = 38f;
        [Tooltip("Show loaded tile count and camera position in the player build.")]
        [SerializeField] bool showRuntimeHud = true;

        [Header("Radii (meters)")]
        [SerializeField] float loadRadius = 15000f;
        [SerializeField] float unloadRadius = 16000f;
        [Tooltip("Max tile-boundary distance for 1×1 HLOD ring.")]
        [SerializeField] float hlod1x1LoadDistance = 1500f;
        [SerializeField] float hlod1x1UnloadDistance = 2000f;
        [Tooltip("Max tile-boundary distance for 2×2 HLOD ring.")]
        [SerializeField] float hlod2x2LoadDistance = 5000f;
        [SerializeField] float hlod2x2UnloadDistance = 6000f;
        [Tooltip("Max tile-boundary distance for 4×4 HLOD ring.")]
        [SerializeField] float hlod4x4LoadDistance = 15000f;
        [SerializeField] float hlod4x4UnloadDistance = 16000f;
        [Tooltip("When off, cells that would use 2×2 HLOD are filled with 1×1 tiles instead.")]
        [SerializeField] bool enableHlod2x2 = true;
        [Tooltip("When off, 4×4 cells use 2×2 instead — or 1×1 when 2×2 is also off.")]
        [SerializeField] bool enableHlod4x4 = true;
        [SerializeField] float checkInterval = 0.5f;
        [SerializeField] int maxLoadedTiles = 32;
        [Tooltip("Hard cap: only tiles intersecting the EPSG region below are eligible for streaming " +
                 "(after radius/HLOD selection). Independent of manifest contents.")]
        [SerializeField] bool limitLoadRegion;
        [SerializeField] int loadRegionMinE;
        [SerializeField] int loadRegionMaxE;
        [SerializeField] int loadRegionMinN;
        [SerializeField] int loadRegionMaxN;

        [Tooltip("Max tiles preparing on background threads (disk I/O + heightmap decode).")]
        [SerializeField] int maxConcurrentBackgroundPrepares = 2;
        [Tooltip("Max tiles finalizing on the main thread at once (Terrain/Texture creation).")]
        [SerializeField] int maxConcurrentTileFinalizes = 1;
        [Tooltip("When false, all work runs synchronously on the main thread (legacy path).")]
        [SerializeField] bool useBackgroundTilePrepare = true;
        [Tooltip("Load pre-baked TerrainData AssetBundles from pack (manifest v3) when available.")]
        [SerializeField] bool preferTerrainBundles = true;
        [Tooltip("Terrain from bundles only — no RAW heightmap prepare/fallback. Requires terrain bundles on disk.")]
        [SerializeField] bool terrainBundleOnlyMode;
        [Tooltip("Load pre-baked building tile prefab bundles from pack (manifest v5) when available.")]
        [SerializeField] bool preferBuildingBundles = true;
        [Tooltip("Load pre-baked vegetation instance bundles from pack when available.")]
        [SerializeField] bool preferBakedVegetation = true;

        [Header("Terrain")]
        [SerializeField] Material terrainMaterial;
        [Tooltip("GDAL RAW rows are top→bottom; Unity terrain expects bottom→top (same as Dataset Manager import).")]
        [SerializeField] bool flipHeightmapVertically = true;
        [SerializeField] bool drawInstanced = false;
        [SerializeField] int basemapDistance = 2000;
        [SerializeField] Transform terrainRoot;

        [Header("Heightmap LOD (Pixel Error)")]
        [Tooltip("When disabled, Pixel Error is used at all distances.")]
        [SerializeField] bool dynamicHeightmapLod = true;
        [Tooltip("heightmapPixelError when the camera is near the tile.")]
        [SerializeField] float pixelErrorNear = 2f;
        [SerializeField] float pixelErrorMid = 5f;
        [SerializeField] float pixelErrorFar = 10f;
        [Tooltip("Fallback heightmapPixelError when Dynamic Heightmap LOD is off.")]
        [SerializeField] float pixelError = 5f;
        [Tooltip("Max horizontal distance (m) from tile bounds for the Near tier.")]
        [SerializeField] float terrainLodNearDistance = 800f;
        [Tooltip("Max distance (m) for Mid tier. Beyond this, Far tier is used.")]
        [SerializeField] float terrainLodMidDistance = 3000f;

        [Header("Streaming toggles")]
        [SerializeField] bool streamTerrain = true;
        [SerializeField] bool streamBuildings = true;
        [SerializeField] bool streamVegetation = true;
        [Tooltip("First pass: load all terrains in range, then all buildings, then vegetation and dynamic streaming.")]
        [SerializeField] bool sequentialInitialLoadOrder;
        [SerializeField] bool streamOrthoBasemaps = true;
        [SerializeField] bool streamTiledBasemaps = true;

        [Header("Basemap")]
        [SerializeField] string activeBasemapId = "ortho";
        [Tooltip("Multiply terrain material color by HLOD factor to visualize which ring each tile uses.")]
        [SerializeField] bool tintBasemapsByHlod;
        [SerializeField] Color hlod1x1BasemapTint = Color.white;
        [SerializeField] Color hlod2x2BasemapTint = new Color(0.45f, 0.75f, 1f, 1f);
        [SerializeField] Color hlod4x4BasemapTint = new Color(1f, 0.45f, 0.45f, 1f);
        [Tooltip("Draw a colored border on ortho basemap textures to show tile edges.")]
        [SerializeField] bool debugBasemapBorders;
        [SerializeField] Color debugBasemapBorderColor = new Color(1f, 0.1f, 0.9f, 1f);
        [SerializeField] int debugBasemapBorderPixels = 6;
        [Tooltip("Use square (L∞) rings for HLOD factor selection so supertiles stack on the grid. " +
                 "Circular rings leave distant periphery tiles on 1×1.")]
        [SerializeField] bool useRectangularHlodDistances = true;
        [Tooltip("Per-cell HLOD: ideal LOD per 1×1 cell, then 4×4 → 2×2 → 1×1 coarse-to-fine cover.")]
        [SerializeField] bool useGridAlignedHlodZones = true;

        [Header("Buildings")]
        [Tooltip("Facade or ortho-roof building bundles from the packed dataset. Mesh combine and colliders are baked at pack time.")]
        [SerializeField] RealtimeBuildingStyle buildingStyle = RealtimeBuildingStyle.Facade;
        [Tooltip("Assigns facade/roof materials to CombinedRender meshes at runtime (from object names like Combined_building_facade_grid).")]
        [SerializeField] BuildingSurfaceSettings buildingSurfaceSettings;
        [Tooltip("Optional URP Lit template for per-tile ortho roof materials when building style is Ortho Roof.")]
        [SerializeField] Material roofOrthophotoMaterialTemplate;
        [SerializeField] float buildingCullDistanceMeters = 400f;
        [Tooltip("When > 0, building bundles load CombinedRender visuals first and stream the physics prefab " +
                 "only within this boundary distance (m). 0 = load physics with the visual tile immediately.")]
        [SerializeField] float buildingColliderLoadDistanceMeters;
        [SerializeField] float buildingLod0Screen = 0.25f;
        [SerializeField] float buildingLod1Screen = 0.08f;
        [SerializeField] float buildingDestroyDelay = 30f;
        [SerializeField] int maxInactiveBuildingGroups = 10;
        [Tooltip("How often to re-evaluate building tile visibility (seconds).")]
        [SerializeField] float buildingCheckInterval = 0.25f;
        [Tooltip("Max building tiles reading GLB/JSON on background threads.")]
        [SerializeField] int maxConcurrentBuildingPrepares = 2;
        [Tooltip("Max building GLB instantiations on the main thread at once.")]
        [SerializeField] int maxConcurrentBuildingLoads = 1;
        [Header("Building interaction")]
        [SerializeField] bool enableBuildingInteraction = true;
        [Tooltip("Material applied to every submesh on the hovered building highlight copy.")]
        [FormerlySerializedAs("buildingOutlineMaterial")]
        [FormerlySerializedAs("buildingHighlightMaterial")]
        [FormerlySerializedAs("buildingHighlightMaterial0")]
        [SerializeField] Material buildingHighlightMaterial;
        [SerializeField] Material buildingSelectionMaterial;
        [SerializeField] BuildingInfoPopup buildingInfoPopupPrefab;

        [Header("Diagnostics")]
        [Tooltip("Log console errors when tiles in range have no terrain data or when terrain load fails.")]
        [SerializeField] bool logStreamingDiagnostics = true;

        [Header("Frame-budget spreading")]
        [Tooltip("Building metadata components attached per frame after legacy GLB instantiation.")]
        [SerializeField] int glbPostProcessBuildingsPerFrame = 12;
        [Tooltip("Max ms per frame spent on legacy GLB post-instantiate setup (0 = building count only).")]
        [SerializeField] float glbPostProcessMsBudget = 4f;
        [Tooltip("Vegetation placement rules processed per frame when a tile spawns vegetation.")]
        [SerializeField] int vegetationRulesPerFrame = 1;
        [Tooltip("Max ms per frame spent generating vegetation for a tile (0 = rule count only).")]
        [SerializeField] float vegetationSpreadMsBudget = 4f;
        [Tooltip("When false, building file reads run on the main thread during load.")]
        [SerializeField] bool useBackgroundBuildingPrepare = true;
        [Tooltip("Prefer buildings_{tileId}.bytes from building_meshes_bin; JSON is fallback when Prefer Binary.")]
        [SerializeField] BuildingMetadataSourceMode buildingMetadataSourceMode = BuildingMetadataSourceMode.PreferBinary;
        [SerializeField] Transform buildingsRoot;

        [Header("Vegetation")]
        [SerializeField] VegetationRuleSet vegetationRuleSet;
        [SerializeField] VegetationPrototype[] vegetationPrototypes;

        [Tooltip("When enabled, vegetation is generated on streamed 2×2 HLOD terrain tiles " +
                 "by combining child 1×1 vegetation masks.")]
        [SerializeField] bool streamVegetationOn2x2Supertiles = true;

        [Header("Vegetation Spawn Animation")]
        [Tooltip("How vegetation appears when a tile streams in. None = full size immediately.")]
        [SerializeField] VegetationSpawnAnimationMode vegetationSpawnAnimation =
            VegetationSpawnAnimationMode.ChunkPopIn;

        [Tooltip("Whole-tile scale-in duration when mode = Chunk Pop In.")]
        [SerializeField] float vegetationChunkPopInDuration = 0.35f;

        [Tooltip("Grow duration per tree after stagger when mode = Per Tree.")]
        [SerializeField] float vegetationPerTreeGrowDuration = 1.2f;

        [Tooltip("Max random delay before each tree starts growing (Per Tree mode).")]
        [SerializeField] float vegetationPerTreeMaxStagger = 0.8f;

        [Header("Vegetation Draw Distance")]
        [Tooltip("No vegetation is drawn beyond this distance (m). Set to 0 to disable.")]
        [SerializeField] float vegetationMaxDrawDistance = 1500f;

        [Tooltip("Full density up to this horizontal distance from the camera per instance (m). " +
                 "Beyond it, density linearly decreases until Max Draw Distance.")]
        [SerializeField] float vegetationDensityFalloffStart = 800f;

        [Tooltip("Skip drawing vegetation tiles whose bounds are outside the camera frustum.")]
        [SerializeField] bool vegetationFrustumCullChunks = true;

        [Tooltip("Skip individual trees outside the camera frustum (within visible tiles).")]
        [SerializeField] bool vegetationFrustumCullInstances = true;

        StreamingDatasetManifest _manifest;
        RuntimeBasemapFactory _basemapFactory;
        BuildingLodController _buildingLodController;
        VegetationInstanceGenerator _vegetationGenerator;

        readonly Dictionary<string, RuntimeTileRecord> _loaded = new();
        readonly Dictionary<string, RuntimeTileRecord> _loading = new();
        readonly Dictionary<string, Terrain> _terrainByCoord = new();
        readonly Dictionary<string, GameObject> _inactiveBuildings = new();
        readonly Dictionary<string, float> _buildingDeactivationTimes = new();
        readonly Dictionary<string, StreamingTileEntry> _leafTilesById = new();
        readonly Dictionary<string, RuntimeTileRecord> _buildingTilesLoaded = new();
        readonly Dictionary<string, RuntimeTileRecord> _buildingTileRecords = new();
        readonly HashSet<string> _buildingTilesLoading = new();
        readonly List<BuildingStreamRequest> _buildingPendingLoads = new();
        readonly List<BuildingBundleRequest> _buildingBundlePendingLoads = new();
        readonly Dictionary<string, Task<RuntimeBuildingPrepareResult>> _buildingPrepareTasks = new();
        readonly Dictionary<string, RuntimeBuildingPrepareResult> _buildingPrepared = new();
        readonly HashSet<string> _buildingInstantiating = new();
        readonly HashSet<string> _buildingCancelledLoads = new();
        readonly HashSet<string> _buildingBundlePathsInFlight = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> _buildingBundlePathByTileId = new();
        readonly Dictionary<string, Material> _buildingRoofMaterialCache = new();
        float _nextBuildingCheck;

        Transform _cam;
        float _nextCheck;
        bool _isStreaming;
        string _datasetRoot;
        RealtimeBuildingStyle _lastBuildingStyle;
        string _lastBasemapId;
        bool _lastTintBasemapsByHlod;
        Color _lastHlod1x1BasemapTint;
        Color _lastHlod2x2BasemapTint;
        Color _lastHlod4x4BasemapTint;
        bool _lastDebugBasemapBorders;
        Color _lastDebugBasemapBorderColor;
        int _lastDebugBasemapBorderPixels;
        HashSet<int> _packedHlodFactors = new() { 1 };
        HashSet<int> _effectiveHlodFactors = new() { 1 };
        readonly List<StreamingHlodLevel> _hlodLevels = new(3);
        int _lastDesiredCount = -1;
        readonly Dictionary<string, float> _appliedPixelErrorByTile = new();
        bool _loggedTerrainShader;
        string _terrainShaderHud = "?";
        string _terrainHitHud = "?";
        float _nextTerrainHitCheck;
        readonly List<RuntimeTileRecord> _pendingLoads = new();
        readonly Dictionary<string, Task<RuntimeTilePrepareResult>> _prepareTasks = new();
        readonly Queue<(RuntimeTileRecord record, RuntimeTilePrepareResult result)> _readyToFinalize = new();
        readonly HashSet<string> _cancelledLoads = new();
        readonly HashSet<string> _currentDesiredKeys = new();
        int _activeFinalizes;
        bool _awaitingInitialHlodPass;
        bool _awaitingInitialTerrainLoad;
        bool _awaitingInitialBuildingLoad;
        bool _awaitingInitialVegetationLoad;
        bool _runtimeRenderingSuppressed;

        RealtimeStreamingHud _runtimeHud;
        BuildingInteractionController _buildingInteraction;
        readonly BuildingLazyMetadataResolver _buildingLazyMetadata = new();
        readonly Queue<RuntimeTileRecord> _vegetationLoadQueue = new();
        bool _vegetationSpreadActive;
        string _vegetationSpreadTileKey;
        readonly Dictionary<string, GameObject> _editorPopulatedTerrainByKey = new();
        readonly RealtimeStreamingTerrainDiagnostics _terrainDiagnostics = new();

        public StreamingDatasetManifest Manifest => _manifest;
        public string ActiveBasemapId => activeBasemapId;
        public bool PreferTerrainBundles => preferTerrainBundles;
        public bool TerrainBundleOnlyMode => terrainBundleOnlyMode;
        public int LoadedTileCount => _loaded.Count;
        public bool IsStreamingActive => _isStreaming;
        public Transform StreamingCamera => _cam;
        public bool StreamBuildingsEnabled => streamBuildings;
        public float BuildingCullDistanceMeters => buildingCullDistanceMeters;
        public string DatasetRoot => _datasetRoot;
        public RealtimeBuildingStyle ActiveBuildingStyle => buildingStyle;
        public BuildingMetadataSourceMode BuildingMetadataSourceMode => buildingMetadataSourceMode;

        /// <summary>Currently loaded terrain tile records (leaf tiles and HLOD supertiles).</summary>
        public IReadOnlyCollection<RuntimeTileRecord> LoadedTerrainTiles => _loaded.Values;

        /// <summary>Currently loaded (active) building tile records.</summary>
        public IReadOnlyCollection<RuntimeTileRecord> LoadedBuildingTiles => _buildingTilesLoaded.Values;

        /// <summary>Raised when a terrain tile (leaf or supertile) finishes loading.</summary>
        public event Action<RuntimeTileRecord> TerrainTileLoaded;

        /// <summary>Raised when a terrain tile (leaf or supertile) is unloaded.</summary>
        public event Action<RuntimeTileRecord> TerrainTileUnloaded;

        /// <summary>Raised when a building tile becomes active.</summary>
        public event Action<RuntimeTileRecord> BuildingTileLoaded;

        /// <summary>Raised when a building tile is deactivated or destroyed.</summary>
        public event Action<RuntimeTileRecord> BuildingTileUnloaded;

        public bool TryGetLoadedTerrainTile(string tileKey, out RuntimeTileRecord record) =>
            _loaded.TryGetValue(tileKey, out record);

        public bool IsTerrainTileLoaded(string tileKey) =>
            !string.IsNullOrEmpty(tileKey) && _loaded.ContainsKey(tileKey);

        public bool IsBuildingTileLoaded(string tileId) =>
            !string.IsNullOrEmpty(tileId) && _buildingTilesLoaded.ContainsKey(tileId);

        /// <summary>
        /// Redirects the streaming focus to another transform (e.g. to prewarm a remote area).
        /// Pass the previous <see cref="StreamingCamera"/> value to restore normal behaviour.
        /// </summary>
        public void SetStreamingCameraOverride(Transform target)
        {
            if (target != null)
                _cam = target;
        }

        public void SetStreamBuildingsEnabled(bool enabled)
        {
            if (streamBuildings == enabled)
                return;

            streamBuildings = enabled;
            if (!streamBuildings)
                UnloadAllBuildingTiles(destroyImmediately: true);
            else
                EvaluateBuildingStreaming();
        }

        public void SetBuildingCullDistanceMeters(float meters)
        {
            meters = Mathf.Max(0f, meters);
            if (Mathf.Approximately(buildingCullDistanceMeters, meters))
                return;

            buildingCullDistanceMeters = meters;
            _buildingLodController = new BuildingLodController(
                buildingCullDistanceMeters, buildingLod0Screen, buildingLod1Screen);
            EvaluateBuildingStreaming();
        }

        /// <summary>Progress of the initial terrain ring and optional building tiles (for loading screens).</summary>
        public StreamingLoadProgress GetInitialLoadProgress() => BuildInitialLoadProgress();

        /// <summary>Alias for <see cref="GetInitialLoadProgress"/>.</summary>
        public StreamingLoadProgress GetInitialTerrainLoadProgress() => GetInitialLoadProgress();

        /// <summary>True when intro/loading UI can dismiss (includes sequential vegetation pass when enabled).</summary>
        public bool IsIntroInitialLoadComplete()
        {
            StreamingLoadProgress progress = BuildInitialLoadProgress();
            return progress.IsInitialLoadComplete && !IsSequentialInitialLoadPending();
        }

        /// <summary>Current loading stage label and detail for intro UI.</summary>
        public StreamingLoadStatus GetInitialLoadStatus()
        {
            StreamingLoadProgress progress = BuildInitialLoadProgress();

            if (!progress.IsStreamingActive)
                return new StreamingLoadStatus("Initializing");

            if (progress.IsInitialPassPending)
                return new StreamingLoadStatus("Initializing", "preparing camera");

            if (progress.DesiredTerrainTileCount > 0 &&
                (progress.ReadyTerrainTileCount < progress.DesiredTerrainTileCount ||
                 progress.LoadingTerrainTileCount > 0))
            {
                string label = _prepareTasks.Count > 0
                    ? "Preparing terrains"
                    : "Loading terrains";
                return StreamingLoadStatus.WithRatio(
                    label,
                    progress.ReadyTerrainTileCount,
                    progress.DesiredTerrainTileCount);
            }

            if (progress.TrackBuildings &&
                (progress.ReadyBuildingTileCount < progress.DesiredBuildingTileCount ||
                 !IsInitialBuildingLoadComplete()))
            {
                return StreamingLoadStatus.WithRatio(
                    ResolveBuildingLoadStageLabel(),
                    progress.ReadyBuildingTileCount,
                    progress.DesiredBuildingTileCount);
            }

            if (sequentialInitialLoadOrder && _awaitingInitialVegetationLoad)
            {
                int vegetationPending = _vegetationLoadQueue.Count + (_vegetationSpreadActive ? 1 : 0);
                return StreamingLoadStatus.WithRatio(
                    "Loading vegetation",
                    0,
                    Mathf.Max(1, vegetationPending));
            }

            if (IsIntroInitialLoadComplete())
                return StreamingLoadStatus.WithRatio("Ready", progress.TotalReady, progress.TotalDesired);

            return StreamingLoadStatus.WithRatio(
                "Loading",
                progress.TotalReady,
                Mathf.Max(1, progress.TotalDesired));
        }

        string ResolveBuildingLoadStageLabel()
        {
            if (_buildingInstantiating.Count > 0)
                return "Loading building meshes";

            if (_buildingPrepareTasks.Count > 0)
                return "Reading building data";

            if (_buildingPrepared.Count > 0)
                return "Preparing buildings";

            if (_buildingPendingLoads.Count > 0 || _buildingBundlePendingLoads.Count > 0)
                return "Queueing buildings";

            return "Loading buildings";
        }

        StreamingLoadProgress BuildInitialLoadProgress()
        {
            if (!_isStreaming || _manifest == null)
            {
                return new StreamingLoadProgress(
                    0, 0, 0, isStreamingActive: false, isInitialPassPending: _awaitingInitialHlodPass);
            }

            if (_awaitingInitialHlodPass)
            {
                return new StreamingLoadProgress(
                    0, 0, 0, isStreamingActive: true, isInitialPassPending: true);
            }

            int terrainDesired = 0;
            int terrainReady = 0;
            int terrainLoading = 0;

            foreach (string key in _currentDesiredKeys)
            {
                terrainDesired++;

                if (_loaded.TryGetValue(key, out RuntimeTileRecord record))
                {
                    if (IsTerrainFullyReady(record))
                        terrainReady++;
                    else
                        terrainLoading++;
                }
                else if (_loading.ContainsKey(key) || IsLoadQueued(key))
                {
                    terrainLoading++;
                }
            }

            CountInitialBuildingLoadProgress(
                out int buildingDesired,
                out int buildingReady,
                out int buildingLoading);

            return new StreamingLoadProgress(
                terrainDesired,
                terrainReady,
                terrainLoading,
                isStreamingActive: true,
                isInitialPassPending: false,
                buildingDesired,
                buildingReady,
                buildingLoading,
                trackBuildings: streamBuildings);
        }

        void CountInitialBuildingLoadProgress(out int desired, out int ready, out int loading)
        {
            desired = 0;
            ready = 0;
            loading = 0;

            if (!streamBuildings || _cam == null || _manifest?.Tiles == null)
                return;

            Vector3 camPos = _cam.position;
            foreach (StreamingTileEntry leaf in _manifest.Tiles)
            {
                if (!ShouldStreamBuildingTile(leaf, camPos))
                    continue;

                desired++;
                string tileId = leaf.TileId;
                if (_buildingTilesLoaded.ContainsKey(tileId))
                    ready++;
                else if (IsBuildingTileInFlight(tileId))
                    loading++;
            }
        }

        bool IsInitialBuildingLoadComplete()
        {
            if (!streamBuildings || _cam == null || _manifest?.Tiles == null)
                return true;

            Vector3 camPos = _cam.position;
            foreach (StreamingTileEntry leaf in _manifest.Tiles)
            {
                if (!ShouldStreamBuildingTile(leaf, camPos))
                    continue;

                if (!_buildingTilesLoaded.ContainsKey(leaf.TileId))
                    return false;
            }

            return _buildingPendingLoads.Count == 0 &&
                   _buildingPrepareTasks.Count == 0 &&
                   _buildingPrepared.Count == 0 &&
                   _buildingInstantiating.Count == 0 &&
                   _buildingTilesLoading.Count == 0;
        }

        bool ShouldStreamBuildingTile(StreamingTileEntry leaf, Vector3 camPos)
        {
            if (leaf == null || !LeafTileHasBuildings(leaf))
                return false;
            if (!IsLeafInsideLoadRegion(leaf))
                return false;
            if (!IsBuildingTileInRange(leaf, camPos))
                return false;

            return BuildingTileHasLoadableData(leaf);
        }

        bool BuildingTileHasLoadableData(StreamingTileEntry leaf)
        {
            if (leaf == null)
                return false;

            if (preferBuildingBundles && TryGetBuildingBundleRelativePath(leaf, out _))
                return true;

            ResolveBuildingTilePaths(leaf.TileId, out string glbPath, out _, out _);
            return File.Exists(glbPath);
        }

        void Awake()
        {
            _buildingLodController = new BuildingLodController(
                buildingCullDistanceMeters, buildingLod0Screen, buildingLod1Screen);
            _lastBuildingStyle = buildingStyle;
            _lastBasemapId = activeBasemapId;
            CacheBasemapVisualSettings();
        }

        void Start()
        {
            StartStreaming();
        }

        void Update()
        {
            if (!_isStreaming || _manifest == null)
                return;

            if (!_awaitingInitialHlodPass)
                ProcessPendingLoads();

            AdvanceSequentialInitialLoadPhases();

            if (_cam != null)
            {
                UpdateActiveTerrainPixelError();
                UpdateTerrainHitHud();
            }

            bool buildingsAllowed = !sequentialInitialLoadOrder || !_awaitingInitialTerrainLoad;
            if (streamBuildings && buildingsAllowed)
            {
                bool driveInitialBuildingLoad =
                    _awaitingInitialBuildingLoad || SceneLoadingController.IntroLoadHandoffActive;

                if (driveInitialBuildingLoad)
                {
                    EvaluateBuildingStreaming();
                    UpdateBuildingColliderStates();
                }
                else if (Time.time >= _nextBuildingCheck)
                {
                    _nextBuildingCheck = Time.time + buildingCheckInterval;
                    EvaluateBuildingStreaming();
                    UpdateBuildingColliderStates();
                }

                ProcessBuildingLoadPipeline();
            }

            if (!IsSequentialInitialLoadPending() || _awaitingInitialVegetationLoad)
                TryStartNextVegetationSpread();

            if (_awaitingInitialHlodPass || IsSequentialInitialLoadPending() || Time.time < _nextCheck)
            {
                PruneInactiveBuildings();
                return;
            }

            _nextCheck = Time.time + checkInterval;
            EvaluateStreaming();

            if (buildingStyle != _lastBuildingStyle)
            {
                _lastBuildingStyle = buildingStyle;
                ConfigureBuildingLazyMetadata();
                ReloadAllBuildings();
            }

            if (activeBasemapId != _lastBasemapId)
            {
                _lastBasemapId = activeBasemapId;
                ApplyBasemapSwitchToLoadedTiles();
                if (buildingStyle == RealtimeBuildingStyle.OrthoRoof)
                    RefreshCombinedRenderMaterialsOnLoadedBuildings();
            }

            if (BasemapVisualSettingsChanged())
            {
                RefreshBasemapVisualsOnAllLoadedTiles();
                CacheBasemapVisualSettings();
            }

            PruneInactiveBuildings();
        }

        public bool IsRuntimeRenderingSuppressed => _runtimeRenderingSuppressed;

        /// <summary>
        /// Hides the streaming camera, HUD, and all streamed content while the intro loading screen is visible.
        /// Loading continues in the background; only presentation is suppressed.
        /// </summary>
        public void SetRuntimeRenderingSuppressed(bool suppressed)
        {
            _runtimeRenderingSuppressed = suppressed;
            ApplyRuntimePresentationSuppression();

            if (suppressed)
                ApplyStreamingContentRenderingEnabled(false);
            else
                RestoreAllStreamedVisuals();
        }

        /// <summary>Re-enables cameras, listeners, and all streamed visuals after the intro handoff.</summary>
        public void ReleaseStreamingPresentation()
        {
            SetRuntimeRenderingSuppressed(false);
        }

        void RestoreAllStreamedVisuals()
        {
            if (terrainRoot != null)
                terrainRoot.gameObject.SetActive(true);

            if (buildingsRoot != null)
                buildingsRoot.gameObject.SetActive(true);

            foreach (RuntimeTileRecord record in _loaded.Values)
            {
                if (record?.TerrainObject != null)
                {
                    record.TerrainObject.SetActive(true);
                    SetGameObjectRenderingEnabled(record.TerrainObject, true);
                }
            }

            foreach (RuntimeTileRecord record in _buildingTileRecords.Values)
                RestoreBuildingVisuals(record);

            foreach (RuntimeTileRecord record in _buildingTilesLoaded.Values)
                RestoreBuildingVisuals(record);
        }

        static void RestoreBuildingVisuals(RuntimeTileRecord record)
        {
            ApplyBuildingTileRenderingEnabled(record, true);
        }

        void ApplyRuntimePresentationSuppression()
        {
            EnsureStreamingCameraAssigned();
            bool show = !_runtimeRenderingSuppressed;

            Scene scene = gameObject.scene;
            if (scene.IsValid() && scene.isLoaded)
            {
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (Camera camera in root.GetComponentsInChildren<Camera>(true))
                        camera.enabled = show;

                    foreach (AudioListener listener in root.GetComponentsInChildren<AudioListener>(true))
                        listener.enabled = show;
                }
            }

            if (_runtimeHud != null)
                _runtimeHud.SetVisible(show && showRuntimeHud);
        }

        void ApplyStreamingContentRenderingEnabled(bool enabled)
        {
            if (terrainRoot != null)
                SetGameObjectRenderingEnabled(terrainRoot.gameObject, enabled);

            if (buildingsRoot != null)
                buildingsRoot.gameObject.SetActive(enabled);

            foreach (RuntimeTileRecord record in _loaded.Values)
                ApplyTileContentRenderingEnabled(record, enabled);

            foreach (RuntimeTileRecord record in _buildingTileRecords.Values)
                ApplyBuildingTileRenderingEnabled(record, enabled);

            foreach (RuntimeTileRecord record in _buildingTilesLoaded.Values)
                ApplyBuildingTileRenderingEnabled(record, enabled);

            foreach (GameObject inactive in _inactiveBuildings.Values)
            {
                if (inactive == null)
                    continue;

                if (!enabled)
                    inactive.SetActive(false);
            }
        }

        void SuppressStreamingVisualIfNeeded(GameObject root)
        {
            if (!_runtimeRenderingSuppressed || root == null)
                return;

            RuntimeBuildingTileRuntimeState state = root.GetComponent<RuntimeBuildingTileRuntimeState>();
            if (state != null)
            {
                state.ApplyTileRenderingEnabled(false);
                return;
            }

            SetGameObjectRenderingEnabled(root, false);
        }

        bool IsBuildingTileInFlight(string tileId)
        {
            if (string.IsNullOrEmpty(tileId))
                return false;

            if (_buildingTilesLoading.Contains(tileId))
                return true;

            foreach (BuildingStreamRequest pending in _buildingPendingLoads)
            {
                if (pending.TileId == tileId)
                    return true;
            }

            foreach (BuildingBundleRequest pending in _buildingBundlePendingLoads)
            {
                if (pending.TileId == tileId)
                    return true;
            }

            if (_buildingPrepareTasks.ContainsKey(tileId) ||
                _buildingPrepared.ContainsKey(tileId) ||
                _buildingInstantiating.Contains(tileId))
                return true;

            return false;
        }

        static void ApplyTileContentRenderingEnabled(RuntimeTileRecord record, bool enabled)
        {
            if (record?.TerrainObject != null)
                SetGameObjectRenderingEnabled(record.TerrainObject, enabled);
        }

        static void ApplyBuildingTileRenderingEnabled(RuntimeTileRecord record, bool enabled)
        {
            if (record?.BuildingsObject == null)
                return;

            GameObject root = record.BuildingsObject;
            RuntimeBuildingTileRuntimeState state = root.GetComponent<RuntimeBuildingTileRuntimeState>();
            if (state != null && state.UsesCombinedRender)
            {
                if (enabled)
                {
                    root.SetActive(true);
                    state.ApplyTileRenderingEnabled(true);
                }
                else
                {
                    state.ApplyTileRenderingEnabled(false);
                }

                return;
            }

            SetGameObjectRenderingEnabled(root, enabled);
        }

        static void SetGameObjectRenderingEnabled(GameObject root, bool enabled)
        {
            if (root == null)
                return;

            if (enabled && !root.activeInHierarchy && root.transform.parent != null)
                root.SetActive(true);

            foreach (Terrain terrain in root.GetComponentsInChildren<Terrain>(true))
                terrain.enabled = enabled;

            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
                renderer.enabled = enabled;

            foreach (VegetationChunkRenderer vegetation in root.GetComponentsInChildren<VegetationChunkRenderer>(true))
                vegetation.enabled = enabled;

            foreach (Canvas canvas in root.GetComponentsInChildren<Canvas>(true))
                canvas.enabled = enabled;
        }

        public void ApplyInitialCameraFocus()
        {
            EnsureStreamingCameraAssigned();
            MaybeFocusCameraOnDataset(forceInitialFocus: true);
        }

        void EnsureStreamingCameraAssigned()
        {
            if (cameraTransform != null)
            {
                _cam = cameraTransform;
                return;
            }

            _cam = FindCameraTransformInScene(gameObject.scene);
            if (_cam == null)
                _cam = Camera.main?.transform;
        }

        static Transform FindCameraTransformInScene(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded)
                return null;

            Transform fallback = null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Camera camera in root.GetComponentsInChildren<Camera>(true))
                {
                    if (camera.CompareTag("MainCamera"))
                        return camera.transform;

                    if (fallback == null)
                        fallback = camera.transform;
                }
            }

            return fallback;
        }

        public void StartStreaming()
        {
            if (!LoadManifest())
                return;

            EnsureStreamingCameraAssigned();
            if (_cam == null)
            {
                Debug.LogError("[ZGConnect.Realtime] No camera assigned.");
                return;
            }

            if (terrainMaterial == null)
                Debug.LogWarning("[ZGConnect.Realtime] terrainMaterial is not assigned — terrain may render incorrectly.");
            else
                EnsureTerrainMaterialShader();

            if (streamVegetation)
            {
                if (vegetationRuleSet == null)
                {
                    Debug.LogWarning(
                        "[ZGConnect.Realtime] streamVegetation is enabled but Vegetation Rule Set is not assigned — " +
                        "vegetation will not load.");
                }
                else
                {
                    VegetationPrototype[] prototypes = ResolveVegetationPrototypes();
                    if (prototypes == null || prototypes.Length == 0)
                    {
                        Debug.LogWarning(
                            "[ZGConnect.Realtime] streamVegetation is enabled but no Vegetation Prototypes " +
                            "could be resolved from the rule set.");
                    }
                }
            }

            EnsureRoots();
            CacheEditorPopulatedTerrains();
            _isStreaming = true;
            _awaitingInitialHlodPass = false;
            _terrainDiagnostics.Enabled = logStreamingDiagnostics;
            _terrainDiagnostics.Reset();

            if (SceneLoadingController.IntroLoadHandoffActive)
                SetRuntimeRenderingSuppressed(true);

            if (autoFocusCameraOnDataset)
                MaybeFocusCameraOnDataset(forceInitialFocus: true);

            RunInitialHlodAndScheduleLoads();
            _nextCheck = Time.time + checkInterval;

            EnsureRuntimeHud();
            EnsureBuildingInteraction();
            _nextBuildingCheck = Time.time;
            Debug.Log($"[ZGConnect.Realtime] Streaming started. Camera at {_cam.position}.");
        }

        void OnDestroy()
        {
            BuildingSharedMaterialApplier.ReleaseCachedRoofMaterials(_buildingRoofMaterialCache);
            DestroyRuntimeHud();
        }

        void EnsureBuildingInteraction()
        {
            if (!enableBuildingInteraction)
                return;

            EnsureStreamingCameraAssigned();
            if (_cam == null)
                return;

            Camera camera = _cam.GetComponent<Camera>();
            if (camera == null)
            {
                Debug.LogWarning("[ZGConnect.Realtime] Building interaction requires a Camera on the streaming camera transform.");
                return;
            }

            _buildingInteraction = BuildingInteractionController.EnsureOnCamera(
                camera,
                buildingHighlightMaterial,
                buildingSelectionMaterial,
                buildingInfoPopupPrefab,
                transform);
            ConfigureBuildingLazyMetadata();
        }

        void ConfigureBuildingLazyMetadata()
        {
            _buildingLazyMetadata.Configure(
                _datasetRoot,
                buildingStyle == RealtimeBuildingStyle.OrthoRoof,
                buildingMetadataSourceMode);
            _buildingInteraction?.ConfigureLazyMetadataResolver(_buildingLazyMetadata);
        }

        public void StopStreaming()
        {
            _isStreaming = false;
            _awaitingInitialHlodPass = false;
            _awaitingInitialTerrainLoad = false;
            _awaitingInitialBuildingLoad = false;
            _awaitingInitialVegetationLoad = false;
            _runtimeRenderingSuppressed = false;
            foreach (string key in _loaded.Keys.ToList())
                UnloadRecord(_loaded[key]);
            _loaded.Clear();
            _loading.Clear();
            _currentDesiredKeys.Clear();
            _pendingLoads.Clear();
            _prepareTasks.Clear();
            _readyToFinalize.Clear();
            _cancelledLoads.Clear();
            _activeFinalizes = 0;
            ClearBuildingLoadPipeline();
            _buildingLazyMetadata.Clear();
            _terrainByCoord.Clear();
            _appliedPixelErrorByTile.Clear();
            UnloadAllBuildingTiles(destroyImmediately: true);
            if (_runtimeHud != null)
                _runtimeHud.SetVisible(false);
        }

        void EnsureRuntimeHud()
        {
            if (!showRuntimeHud)
            {
                if (_runtimeHud != null)
                    _runtimeHud.SetVisible(false);
                return;
            }

            if (_runtimeHud == null)
            {
                var hudGo = new GameObject("ZGConnect Runtime HUD");
                hudGo.transform.SetParent(transform, false);
                _runtimeHud = hudGo.AddComponent<RealtimeStreamingHud>();
                _runtimeHud.Initialize(this);
            }

            _runtimeHud.SetVisible(showRuntimeHud && !_runtimeRenderingSuppressed);
        }

        void DestroyRuntimeHud()
        {
            if (_runtimeHud == null)
                return;

            if (_runtimeHud.gameObject != null)
                Destroy(_runtimeHud.gameObject);
            _runtimeHud = null;
        }

        bool LoadManifest()
        {
            string path = RuntimeStreamingPaths.ManifestPath(manifestRelativePath);
            Debug.Log(
                $"[ZGConnect.Realtime] Build/Player startup. streamingAssetsPath={Application.streamingAssetsPath}\n" +
                $"manifest={path}");

            if (!File.Exists(path))
            {
                Debug.LogError(
                    $"[ZGConnect.Realtime] Manifest not found: {path}\n" +
                    "Ensure Assets/StreamingAssets/ZGConnect/... is present before building.");
                return false;
            }

            try
            {
                _manifest = StreamingDatasetManifest.LoadFromFile(path);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ZGConnect.Realtime] Manifest parse failed: {ex}");
                return false;
            }

            if (_manifest == null)
            {
                Debug.LogError("[ZGConnect.Realtime] Manifest deserialized to null.");
                return false;
            }

            _datasetRoot = RuntimeStreamingPaths.DatasetRoot();
            _basemapFactory = new RuntimeBasemapFactory(_datasetRoot);
            CacheAvailableHlodFactors();
            RebuildHlodLevels();
            CacheLeafTileLookup();
            ResolveActiveBasemapId();
            LogDatasetReadiness();
            ConfigureBuildingLazyMetadata();
            return true;
        }

        /// <summary>Ensures lazy metadata resolver is configured and returns the shared instance.</summary>
        public BuildingLazyMetadataResolver GetBuildingLazyMetadataResolver()
        {
            if (string.IsNullOrEmpty(_datasetRoot))
                LoadManifest();

            ConfigureBuildingLazyMetadata();
            return _buildingLazyMetadata;
        }

        void RebuildHlodLevels()
        {
            _hlodLevels.Clear();
            _hlodLevels.Add(new StreamingHlodLevel
            {
                Factor = 1,
                LoadDistance = hlod1x1LoadDistance,
                UnloadDistance = hlod1x1UnloadDistance,
            });
            _hlodLevels.Add(new StreamingHlodLevel
            {
                Factor = 2,
                LoadDistance = hlod2x2LoadDistance,
                UnloadDistance = hlod2x2UnloadDistance,
            });
            _hlodLevels.Add(new StreamingHlodLevel
            {
                Factor = 4,
                LoadDistance = hlod4x4LoadDistance,
                UnloadDistance = hlod4x4UnloadDistance,
            });
        }

        void CacheLeafTileLookup()
        {
            _leafTilesById.Clear();
            if (_manifest?.Tiles == null)
                return;

            foreach (StreamingTileEntry tile in _manifest.Tiles)
            {
                if (!string.IsNullOrEmpty(tile.TileId))
                    _leafTilesById[tile.TileId] = tile;
            }
        }

        void CacheAvailableHlodFactors()
        {
            _packedHlodFactors = new HashSet<int> { 1 };
            if (_manifest.Supertiles != null)
            {
                foreach (StreamingSupertileEntry st in _manifest.Supertiles)
                {
                    if (st.Factor == 2 || st.Factor == 4)
                        _packedHlodFactors.Add(st.Factor);
                }
            }

            RebuildEffectiveHlodFactors();
        }

        void RebuildEffectiveHlodFactors()
        {
            _effectiveHlodFactors = new HashSet<int> { 1 };
            if (enableHlod2x2 && _packedHlodFactors.Contains(2))
                _effectiveHlodFactors.Add(2);
            if (enableHlod4x4 && _packedHlodFactors.Contains(4))
                _effectiveHlodFactors.Add(4);
        }

        void ResolveActiveBasemapId()
        {
            if (_manifest.AvailableBasemaps == null || _manifest.AvailableBasemaps.Count == 0)
                return;

            bool found = _manifest.AvailableBasemaps.Exists(b => b.Id == activeBasemapId);
            if (found)
                return;

            string previous = activeBasemapId;
            activeBasemapId = _manifest.AvailableBasemaps[0].Id;
            _lastBasemapId = activeBasemapId;
            Debug.LogWarning(
                $"[ZGConnect.Realtime] activeBasemapId '{previous}' not in manifest. Using '{activeBasemapId}'.");
        }

        void LogDatasetReadiness()
        {
            int tilesWithHeightmap = 0;
            foreach (StreamingTileEntry tile in _manifest.Tiles ?? new List<StreamingTileEntry>())
            {
                var record = RuntimeTileRecord.FromLeaf(tile, _manifest.TileSizeMeters);
                if (TerrainDataAvailable(record))
                    tilesWithHeightmap++;
            }

            int manifestTileCount = _manifest.Tiles?.Count ?? 0;
            string terrainSource = terrainBundleOnlyMode ? "terrain bundle" : "heightmap or terrain bundle";
            Debug.Log(
                $"[ZGConnect.Realtime] Dataset ready: " +
                $"{manifestTileCount} tiles, {tilesWithHeightmap} with streamable {terrainSource} on disk, " +
                $"{_manifest.Supertiles?.Count ?? 0} supertiles. HLOD packed: {string.Join(",", _packedHlodFactors)}, " +
                $"active: {string.Join(",", _effectiveHlodFactors)}." +
                (terrainBundleOnlyMode ? " Terrain: bundle-only mode." : string.Empty));

            if (logStreamingDiagnostics && manifestTileCount > tilesWithHeightmap)
            {
                Debug.LogError(
                    $"[ZGConnect.Realtime] {manifestTileCount - tilesWithHeightmap} manifest tile(s) " +
                    $"have no {terrainSource} on disk — expect holes until they are re-packed. " +
                    "Fly the camera over gaps for per-tile details.");
            }
        }

        void RunInitialHlodAndScheduleLoads()
        {
            ApplyDesiredTilesAndScheduleLoads();
            ProcessPendingLoads();

            if (sequentialInitialLoadOrder)
            {
                _awaitingInitialTerrainLoad = true;
                _awaitingInitialBuildingLoad = false;
                _awaitingInitialVegetationLoad = false;
                return;
            }

            if (streamBuildings)
            {
                _awaitingInitialBuildingLoad = true;
                _nextBuildingCheck = 0f;
                EvaluateBuildingStreaming();
            }
        }

        bool IsSequentialInitialLoadPending()
        {
            if (!sequentialInitialLoadOrder)
                return false;

            return _awaitingInitialTerrainLoad ||
                   _awaitingInitialBuildingLoad ||
                   _awaitingInitialVegetationLoad;
        }

        bool IsInitialTerrainLoadComplete()
        {
            return BuildInitialLoadProgress().IsInitialTerrainLoadComplete;
        }

        void AdvanceSequentialInitialLoadPhases()
        {
            if (!sequentialInitialLoadOrder)
                return;

            if (_awaitingInitialTerrainLoad && IsInitialTerrainLoadComplete())
            {
                _awaitingInitialTerrainLoad = false;
                if (streamBuildings)
                {
                    _awaitingInitialBuildingLoad = true;
                    _nextBuildingCheck = 0f;
                    EvaluateBuildingStreaming();
                }
                else if (streamVegetation && HasPendingVegetationWork())
                {
                    _awaitingInitialVegetationLoad = true;
                }
            }

            if (_awaitingInitialBuildingLoad && IsInitialBuildingLoadComplete())
            {
                _awaitingInitialBuildingLoad = false;
                if (streamVegetation && HasPendingVegetationWork())
                    _awaitingInitialVegetationLoad = true;
            }

            if (_awaitingInitialVegetationLoad && !HasPendingVegetationWork())
                _awaitingInitialVegetationLoad = false;
        }

        bool HasPendingVegetationWork()
        {
            return _vegetationLoadQueue.Count > 0 || _vegetationSpreadActive;
        }

        void MaybeFocusCameraOnDataset(bool forceInitialFocus = false)
        {
            if (!autoFocusCameraOnDataset || _cam == null ||
                _manifest.Tiles == null || _manifest.Tiles.Count == 0)
                return;

            if (!TryGetDatasetBounds(out DatasetBounds bounds))
                return;

            if (!forceInitialFocus && !_awaitingInitialHlodPass)
            {
                Vector3 camPos = _cam.position;
                float horizontalDist = new Vector2(
                    camPos.x - bounds.Center.x,
                    camPos.z - bounds.Center.z).magnitude;

                bool farFromDataset = horizontalDist > bounds.Span * 0.2f;
                bool tooLow = camPos.y < bounds.AverageTerrainHeight * 0.25f;
                if (!farFromDataset && !tooLow)
                    return;
            }

            float lookY = bounds.AverageTerrainHeight * 0.4f;
            Vector3 lookTarget = new Vector3(bounds.Center.x, lookY, bounds.Center.z);

            float minDistance = _manifest.TileSizeMeters * 2.5f;
            float maxDistance = 25000f;
            if (loadRadius > 0f)
                maxDistance = Mathf.Min(maxDistance, loadRadius * 0.85f);

            float distance = Mathf.Clamp(bounds.Span * autoFocusDistanceScale, minDistance, maxDistance);

            float elevRad = autoFocusElevationDegrees * Mathf.Deg2Rad;
            const float azimuthDegrees = 225f;
            float azRad = azimuthDegrees * Mathf.Deg2Rad;

            Vector3 focusPos = ComputeAutoFocusPosition(lookTarget, distance, elevRad, azRad);

            if (autoFocusMaxCameraHeightY > 0f && focusPos.y > autoFocusMaxCameraHeightY)
            {
                focusPos = ComputeAutoFocusPositionWithHeightCap(
                    lookTarget,
                    autoFocusMaxCameraHeightY,
                    elevRad,
                    azRad,
                    minDistance);
            }

            Vector3 viewDir = lookTarget - focusPos;
            if (viewDir.sqrMagnitude < 0.001f)
                viewDir = Vector3.forward;

            _cam.position = focusPos;
            _cam.rotation = Quaternion.LookRotation(viewDir.normalized, Vector3.up);

            FlyCameraController fly = _cam.GetComponent<FlyCameraController>();
            if (fly != null)
                fly.SyncOrientationFromTransform();

            float actualDistance = Vector3.Distance(focusPos, lookTarget);
            Debug.Log(
                $"[ZGConnect.Realtime] Camera focused on dataset: pos={focusPos}, " +
                $"lookAt={lookTarget}, span={bounds.Span:F0} m, dist={actualDistance:F0} m " +
                $"(forced={forceInitialFocus}).");
        }

        static Vector3 ComputeAutoFocusPosition(
            Vector3 lookTarget,
            float distance,
            float elevRad,
            float azRad)
        {
            float horizontal = Mathf.Cos(elevRad) * distance;
            return lookTarget + new Vector3(
                horizontal * Mathf.Cos(azRad),
                Mathf.Sin(elevRad) * distance,
                horizontal * Mathf.Sin(azRad));
        }

        static Vector3 ComputeAutoFocusPositionWithHeightCap(
            Vector3 lookTarget,
            float maxCameraHeightY,
            float elevRad,
            float azRad,
            float minHorizontalDistance)
        {
            float vertical = maxCameraHeightY - lookTarget.y;
            if (vertical <= 0.01f)
            {
                return new Vector3(
                    lookTarget.x + minHorizontalDistance * Mathf.Cos(azRad),
                    maxCameraHeightY,
                    lookTarget.z + minHorizontalDistance * Mathf.Sin(azRad));
            }

            float tanElev = Mathf.Tan(elevRad);
            float horizontalDist = tanElev > 0.0001f
                ? vertical / tanElev
                : minHorizontalDistance;
            horizontalDist = Mathf.Max(horizontalDist, minHorizontalDistance);

            return new Vector3(
                lookTarget.x + horizontalDist * Mathf.Cos(azRad),
                maxCameraHeightY,
                lookTarget.z + horizontalDist * Mathf.Sin(azRad));
        }

        readonly struct DatasetBounds
        {
            public readonly Vector3 Center;
            public readonly float Span;
            public readonly float AverageTerrainHeight;

            public DatasetBounds(Vector3 center, float span, float averageTerrainHeight)
            {
                Center = center;
                Span = span;
                AverageTerrainHeight = averageTerrainHeight;
            }
        }

        bool TryGetDatasetBounds(out DatasetBounds bounds)
        {
            bounds = default;
            if (_manifest.Tiles == null || _manifest.Tiles.Count == 0)
                return false;

            float minX = float.MaxValue;
            float maxX = float.MinValue;
            float minZ = float.MaxValue;
            float maxZ = float.MinValue;
            float terrainHeightSum = 0f;
            int count = 0;

            foreach (StreamingTileEntry tile in _manifest.Tiles)
            {
                Vector3 origin = tile.GetUnityPosition();
                Vector3 size = tile.GetTerrainSize();
                minX = Mathf.Min(minX, origin.x);
                maxX = Mathf.Max(maxX, origin.x + size.x);
                minZ = Mathf.Min(minZ, origin.z);
                maxZ = Mathf.Max(maxZ, origin.z + size.z);
                terrainHeightSum += size.y;
                count++;
            }

            if (count == 0)
                return false;

            float centerX = (minX + maxX) * 0.5f;
            float centerZ = (minZ + maxZ) * 0.5f;
            float span = Mathf.Max(maxX - minX, maxZ - minZ);
            float avgTerrainHeight = terrainHeightSum / count;
            bounds = new DatasetBounds(
                new Vector3(centerX, 0f, centerZ),
                span,
                avgTerrainHeight);
            return true;
        }

        public IReadOnlyList<string> BuildRuntimeHudLines()
        {
            if (!_isStreaming || _cam == null)
                return System.Array.Empty<string>();

            CountTilesByHlod(_loaded.Values, out int loaded1, out int loaded2, out int loaded4);
            CountTilesByHlod(_loading.Values, out int loading1, out int loading2, out int loading4);

            int manifestLeaf = _manifest?.Tiles?.Count ?? 0;
            int manifestSupertiles = _manifest?.Supertiles?.Count ?? 0;
            int preparing = _prepareTasks.Count;
            Vector3 cam = _cam.position;

            var lines = new List<string>
            {
                $"Loaded  {_loaded.Count}  (leaf {manifestLeaf}, ST {manifestSupertiles})",
                $"  HLOD 1x1   {loaded1,3}",
                $"  HLOD 2x2   {loaded2,3}",
                $"  HLOD 4x4   {loaded4,3}",
                $"Loading  {_loading.Count}  (prep {preparing}, queue {_pendingLoads.Count})",
                $"  HLOD 1x1   {loading1,3}",
                $"  HLOD 2x2   {loading2,3}",
                $"  HLOD 4x4   {loading4,3}",
                $"Buildings  {_buildingTilesLoaded.Count} loaded, {CountBuildingPipelineInFlight()} in flight " +
                $"(prep {_buildingPrepareTasks.Count}, inst {_buildingInstantiating.Count})",
                $"  stream {(streamBuildings ? "on" : "off")}, cull {GetBuildingStreamRadius():F0} m" +
                (buildingColliderLoadDistanceMeters > 0f
                    ? $", colliders {buildingColliderLoadDistanceMeters:F0} m"
                    : string.Empty),
            };

            if (limitLoadRegion && loadRegionMinE < loadRegionMaxE && loadRegionMinN < loadRegionMaxN)
            {
                lines.Add(
                    $"Region  E {loadRegionMinE}–{loadRegionMaxE}, N {loadRegionMinN}–{loadRegionMaxN}");
            }

            lines.Add($"Camera  {cam.x:F0}, {cam.y:F0}, {cam.z:F0}");
            lines.Add($"Terrain  {_terrainShaderHud}");
            lines.Add($"Raycast  {_terrainHitHud}");
            lines.Add(BuildVegetationHudLine(this));
            return lines;
        }

        static string BuildVegetationHudLine(RealtimeStreamingController controller)
        {
            int registered = VegetationRenderStats.InstancesRegistered;
            int culled = VegetationRenderStats.InstancesFrustumCulled +
                         VegetationRenderStats.InstancesDistanceCulled;
            float cullPct = registered > 0 ? (culled * 100f / registered) : 0f;
            int chunks = controller != null ? controller.CountLoadedVegetationChunks() : 0;
            int queue = controller?._vegetationLoadQueue.Count ?? 0;
            string spread = controller != null && controller._vegetationSpreadActive ? " spread" : string.Empty;
            return $"Vegetation  draw {VegetationRenderStats.InstancesDrawn:N0} / {registered:N0}  " +
                   $"(~{VegetationRenderStats.TrianglesDrawn / 1_000_000f:F1}M tris, " +
                   $"culled {cullPct:F0}% dist {VegetationRenderStats.InstancesDistanceCulled:N0} " +
                   $"frust {VegetationRenderStats.InstancesFrustumCulled:N0}, " +
                   $"chunks {chunks}, queue {queue}{spread})";
        }

        int CountLoadedVegetationChunks()
        {
            int count = 0;
            foreach (RuntimeTileRecord record in _loaded.Values)
            {
                if (record?.VegetationRenderer != null && record.VegetationRenderer.TotalInstances > 0)
                    count++;
            }

            return count;
        }

        static void CountTilesByHlod(
            IEnumerable<RuntimeTileRecord> records,
            out int count1x1,
            out int count2x2,
            out int count4x4)
        {
            count1x1 = 0;
            count2x2 = 0;
            count4x4 = 0;

            if (records == null)
                return;

            foreach (RuntimeTileRecord record in records)
            {
                switch (record.HlodFactor)
                {
                    case 2: count2x2++; break;
                    case 4: count4x4++; break;
                    default: count1x1++; break;
                }
            }
        }

        void EnsureTerrainMaterialShader()
        {
            Shader shader = terrainMaterial.shader;
            if (shader != null && shader.isSupported)
            {
                _terrainShaderHud = shader.name;
                return;
            }

            Shader fallback = Shader.Find("Universal Render Pipeline/Terrain/Lit");
            if (fallback != null && fallback.isSupported)
            {
                terrainMaterial.shader = fallback;
                _terrainShaderHud = fallback.name;
                Debug.Log("[ZGConnect.Realtime] Terrain material shader reassigned via Shader.Find.");
                return;
            }

            _terrainShaderHud = shader != null ? $"{shader.name} (unsupported)" : "missing";
            Debug.LogError(
                $"[ZGConnect.Realtime] Terrain/Lit shader not supported in this build. " +
                $"Assigned shader: {shader?.name ?? "null"}");
        }

        void LogTerrainShaderOnce(Terrain terrain)
        {
            if (_loggedTerrainShader || terrain == null)
                return;

            _loggedTerrainShader = true;
            Material mat = terrain.materialTemplate;
            Shader shader = mat != null ? mat.shader : null;
            bool supported = shader != null && shader.isSupported;
            _terrainShaderHud = shader != null ? shader.name : "null";
            Debug.Log(
                $"[ZGConnect.Realtime] First terrain render check: shader={shader?.name ?? "null"}, " +
                $"supported={supported}, drawInstanced={terrain.drawInstanced}, layers={terrain.terrainData?.terrainLayers?.Length ?? 0}");
        }

        HlodDistanceMetric GetHlodDistanceMetric() =>
            useRectangularHlodDistances
                ? HlodDistanceMetric.Rectangular
                : HlodDistanceMetric.Circular;

        public HlodDistanceMetric GetHlodDistanceMetricPublic() => GetHlodDistanceMetric();

        public bool IsTerrainDataAvailable(RuntimeTileRecord record) => TerrainDataAvailable(record);

        float GetStreamDistance(Vector3 camPos, Vector3 tileOrigin, int tileSizeMeters) =>
            HlodRingEvaluator.TileBoundaryDistance(camPos, tileOrigin, tileSizeMeters, GetHlodDistanceMetric());

        float GetTileSortDistance(Vector3 camPos, RuntimeTileRecord record) =>
            GetTileSortDistance(camPos, record.UnityPosition, record.TileSizeMeters);

        float GetTileSortDistance(Vector3 camPos, Vector3 tileOrigin, int tileSizeMeters)
        {
            float centerX = tileOrigin.x + tileSizeMeters * 0.5f;
            float centerZ = tileOrigin.z + tileSizeMeters * 0.5f;
            float dx = Mathf.Abs(camPos.x - centerX);
            float dz = Mathf.Abs(camPos.z - centerZ);
            return useRectangularHlodDistances
                ? Mathf.Max(dx, dz)
                : Mathf.Sqrt(dx * dx + dz * dz);
        }

        float GetHlodLoadDistance(int factor) =>
            factor switch
            {
                1 => hlod1x1LoadDistance,
                2 => hlod2x2LoadDistance,
                4 => hlod4x4LoadDistance,
                _ => hlod4x4LoadDistance,
            };

        int GetCoarseReplacementUrgency(RuntimeTileRecord record)
        {
            if (!record.IsSupertile || record.Supertile?.ChildTileIds == null)
                return 0;
            if (!_currentDesiredKeys.Contains(record.Key))
                return 0;

            foreach (string childId in record.Supertile.ChildTileIds)
            {
                if (_currentDesiredKeys.Contains(childId))
                    continue;
                if (_loaded.ContainsKey(childId))
                    return -1;
            }

            return 0;
        }

        int CompareTileLoadPriority(RuntimeTileRecord a, RuntimeTileRecord b)
        {
            if (a == null || b == null)
                return 0;

            int urgencyCmp = GetCoarseReplacementUrgency(a).CompareTo(GetCoarseReplacementUrgency(b));
            if (urgencyCmp != 0)
                return urgencyCmp;

            Vector3 camPos = _cam != null ? _cam.position : Vector3.zero;
            float distA = GetTileSortDistance(camPos, a);
            float distB = GetTileSortDistance(camPos, b);
            int distCmp = distA.CompareTo(distB);
            if (distCmp != 0)
                return distCmp;

            float nearLoadDistance = GetHlodLoadDistance(1);
            bool coarseFirst = distA > nearLoadDistance;
            int factorCmp = coarseFirst
                ? b.HlodFactor.CompareTo(a.HlodFactor)
                : a.HlodFactor.CompareTo(b.HlodFactor);
            if (factorCmp != 0)
                return factorCmp;

            return string.CompareOrdinal(a.Key, b.Key);
        }

        void SortPendingLoadsByCameraPriority()
        {
            if (_pendingLoads.Count <= 1 || _cam == null)
                return;

            _pendingLoads.Sort(CompareTileLoadPriority);
        }

        bool TryDequeueHighestPriorityPending(out RuntimeTileRecord record)
        {
            _pendingLoads.RemoveAll(p => _cancelledLoads.Contains(p.Key));
            SortPendingLoadsByCameraPriority();
            if (_pendingLoads.Count == 0)
            {
                record = null;
                return false;
            }

            record = _pendingLoads[0];
            _pendingLoads.RemoveAt(0);
            return true;
        }

        static bool IsLeafCoveredByDesiredSupertile(
            string leafId,
            Dictionary<string, RuntimeTileRecord> desired)
        {
            foreach (RuntimeTileRecord d in desired.Values)
            {
                if (!d.IsSupertile || d.Supertile?.ChildTileIds == null)
                    continue;
                if (d.Supertile.ChildTileIds.Contains(leafId))
                    return true;
            }

            return false;
        }

        static bool FinerSupertileAlreadyInDesired(
            StreamingSupertileEntry coarse,
            Dictionary<string, RuntimeTileRecord> desired)
        {
            if (coarse.ChildTileIds == null || coarse.ChildTileIds.Count == 0)
                return false;

            foreach (RuntimeTileRecord d in desired.Values)
            {
                if (!d.IsSupertile || d.HlodFactor >= coarse.Factor || d.Supertile?.ChildTileIds == null)
                    continue;

                foreach (string childId in coarse.ChildTileIds)
                {
                    if (d.Supertile.ChildTileIds.Contains(childId))
                        return true;
                }
            }

            return false;
        }

        bool InStreamingLoadRange(float boundaryDistance) =>
            boundaryDistance <= loadRadius && boundaryDistance <= unloadRadius;

        float GetLeafBoundaryDistance(
            Vector3 camPos,
            StreamingTileEntry leaf,
            int tileSizeMeters,
            HlodDistanceMetric hlodMetric) =>
            HlodRingEvaluator.TileBoundaryDistance(
                camPos, leaf.GetUnityPosition(), tileSizeMeters, hlodMetric);

        float MinSupertileBoundaryDistance(
            StreamingSupertileEntry supertile,
            Dictionary<string, float> distanceByLeafId,
            Dictionary<string, StreamingTileEntry> leafById)
        {
            float min = float.MaxValue;
            if (supertile?.ChildTileIds == null)
                return min;

            bool any = false;
            foreach (string childId in supertile.ChildTileIds)
            {
                if (leafById != null &&
                    leafById.TryGetValue(childId, out StreamingTileEntry leaf) &&
                    !IsLeafInsideLoadRegion(leaf))
                {
                    continue;
                }

                if (distanceByLeafId.TryGetValue(childId, out float dist))
                {
                    min = Mathf.Min(min, dist);
                    any = true;
                }
            }

            return any ? min : float.MaxValue;
        }

        bool SupertileIntersectsLoadRegion(StreamingSupertileEntry supertile, int tileSizeMeters)
        {
            if (!limitLoadRegion || supertile == null)
                return true;

            int sizeMeters = supertile.Factor * tileSizeMeters;
            return GetLoadRegionBounds().IntersectsTile(
                supertile.Left,
                supertile.Left + sizeMeters,
                supertile.Bottom,
                supertile.Bottom + sizeMeters);
        }

        bool SupertileCanCoverCells(
            StreamingSupertileEntry supertile,
            int minIdealFactor,
            Dictionary<string, int> idealByLeafId,
            HashSet<string> coveredLeafIds,
            Dictionary<string, StreamingTileEntry> leafById)
        {
            if (supertile?.ChildTileIds == null || supertile.ChildTileIds.Count == 0)
                return false;

            if (!limitLoadRegion)
            {
                if (supertile.ChildTileIds.Count != supertile.Factor * supertile.Factor)
                    return false;

                foreach (string childId in supertile.ChildTileIds)
                {
                    if (!idealByLeafId.TryGetValue(childId, out int ideal))
                        return false;
                    if (ideal < minIdealFactor || coveredLeafIds.Contains(childId))
                        return false;
                }

                return true;
            }

            bool anyInRegion = false;
            foreach (string childId in supertile.ChildTileIds)
            {
                if (!leafById.TryGetValue(childId, out StreamingTileEntry leaf))
                    return false;

                if (!IsLeafInsideLoadRegion(leaf))
                    continue;

                anyInRegion = true;
                if (!idealByLeafId.TryGetValue(childId, out int ideal))
                    return false;
                if (ideal < minIdealFactor || coveredLeafIds.Contains(childId))
                    return false;
            }

            return anyInRegion;
        }

        void MarkSupertileChildrenCovered(
            StreamingSupertileEntry supertile,
            HashSet<string> coveredLeafIds,
            Dictionary<string, StreamingTileEntry> leafById)
        {
            if (supertile?.ChildTileIds == null)
                return;

            foreach (string childId in supertile.ChildTileIds)
            {
                if (limitLoadRegion &&
                    leafById.TryGetValue(childId, out StreamingTileEntry leaf) &&
                    !IsLeafInsideLoadRegion(leaf))
                {
                    continue;
                }

                coveredLeafIds.Add(childId);
            }
        }

        static bool SupertileInsideGridZone(
            StreamingSupertileEntry supertile,
            int tileSizeMeters,
            HlodGridRect zone)
        {
            HlodGridZones.TileToGridIndices(
                supertile.Left, supertile.Bottom, tileSizeMeters, out int gx, out int gz);
            return zone.ContainsBlock(gx, gz, supertile.Factor);
        }

        static bool LeafInsideFineZone(
            StreamingTileEntry leaf,
            int tileSizeMeters,
            HlodGridRect zone)
        {
            HlodGridZones.TileToGridIndices(leaf.Left, leaf.Bottom, tileSizeMeters, out int gx, out int gz);
            return zone.Contains(gx, gz);
        }

        static bool IsDatasetBorderLeaf(
            StreamingTileEntry leaf,
            IReadOnlyDictionary<string, StreamingTileEntry> leafById,
            int tileSizeMeters)
        {
            if (leaf == null || leafById == null)
                return true;

            int left = leaf.Left;
            int bottom = leaf.Bottom;
            return !leafById.ContainsKey($"{left - tileSizeMeters}_{bottom}") ||
                   !leafById.ContainsKey($"{left + tileSizeMeters}_{bottom}") ||
                   !leafById.ContainsKey($"{left}_{bottom - tileSizeMeters}") ||
                   !leafById.ContainsKey($"{left}_{bottom + tileSizeMeters}");
        }

        float Inner2x2BandMaxDistanceMeters()
        {
            float near = GetHlodLoadDistance(1);
            float mid = GetHlodLoadDistance(2);
            return (near + mid) * 0.5f;
        }

        /// <summary>
        /// Per-cell ideal LOD: 4×4 → 2×2 (2×2 zone, then 4×4 zone gap-fill) → 1×1 (1×1 zone or inner 2×2 alignment fill).
        /// 2×2 fills 4×4 grid holes when no 4×4 supertile covers that area. 1×1 never patches dataset border tiles.
        /// </summary>
        Dictionary<string, RuntimeTileRecord> BuildDesiredTilesPerCellCoarseToFine(
            Vector3 camPos,
            HlodDistanceMetric hlodMetric)
        {
            var desired = new Dictionary<string, RuntimeTileRecord>();
            int tileSize = _manifest.TileSizeMeters;
            var idealByLeafId = new Dictionary<string, int>();
            var distanceByLeafId = new Dictionary<string, float>();
            var leafById = new Dictionary<string, StreamingTileEntry>();

            foreach (StreamingTileEntry leaf in _manifest.Tiles ?? new List<StreamingTileEntry>())
            {
                if (!IsLeafInsideLoadRegion(leaf))
                    continue;

                float dist = GetLeafBoundaryDistance(camPos, leaf, tileSize, hlodMetric);
                if (!InStreamingLoadRange(dist))
                    continue;

                int ideal = HlodRingEvaluator.ResolveEffectiveFactor(
                    dist, _hlodLevels, _effectiveHlodFactors);
                idealByLeafId[leaf.TileId] = ideal;
                distanceByLeafId[leaf.TileId] = dist;
                leafById[leaf.TileId] = leaf;
            }

            if (idealByLeafId.Count == 0)
                return desired;

            float nearLoadDistance = GetHlodLoadDistance(1);
            float midLoadDistance = GetHlodLoadDistance(2);
            float farLoadDistance = GetHlodLoadDistance(4);
            float inner2x2MaxDistance = Inner2x2BandMaxDistanceMeters();
            Vector2 camGrid = HlodGridZones.CameraGridPosition(
                camPos, _manifest.GetUnityOrigin(), tileSize);
            HlodGridRect zone1x1 = HlodGridZones.BuildAlignedRect(
                camGrid.x, camGrid.y, nearLoadDistance / tileSize, alignCells: 2);
            HlodGridRect zone2x2 = HlodGridZones.BuildAlignedRect(
                camGrid.x, camGrid.y, midLoadDistance / tileSize, alignCells: 2);
            HlodGridRect zone4x4 = HlodGridZones.BuildAlignedRect(
                camGrid.x, camGrid.y, farLoadDistance / tileSize, alignCells: 4);

            var coveredLeafIds = new HashSet<string>();
            List<StreamingSupertileEntry> supertiles = _manifest.Supertiles ?? new List<StreamingSupertileEntry>();

            if (_effectiveHlodFactors.Contains(4))
            {
                foreach (StreamingSupertileEntry st in supertiles
                             .Where(s => s.Factor == 4)
                             .OrderByDescending(st => MinSupertileBoundaryDistance(st, distanceByLeafId, leafById)))
                {
                    if (!SupertileIntersectsLoadRegion(st, tileSize))
                        continue;
                    if (!SupertileCanCoverCells(st, minIdealFactor: 4, idealByLeafId, coveredLeafIds, leafById))
                        continue;

                    var record = RuntimeTileRecord.FromSupertile(st);
                    if (!TerrainDataAvailable(record))
                        continue;

                    desired[st.SupertileId] = record;
                    MarkSupertileChildrenCovered(st, coveredLeafIds, leafById);
                }
            }

            if (_effectiveHlodFactors.Contains(2))
            {
                IEnumerable<StreamingSupertileEntry> sorted2x2 = supertiles
                    .Where(s => s.Factor == 2)
                    .OrderByDescending(st => MinSupertileBoundaryDistance(st, distanceByLeafId, leafById));

                foreach (StreamingSupertileEntry st in sorted2x2)
                {
                    if (!SupertileIntersectsLoadRegion(st, tileSize))
                        continue;
                    if (!SupertileInsideGridZone(st, tileSize, zone2x2))
                        continue;
                    if (!SupertileCanCoverCells(st, minIdealFactor: 2, idealByLeafId, coveredLeafIds, leafById))
                        continue;

                    var record = RuntimeTileRecord.FromSupertile(st);
                    if (!TerrainDataAvailable(record))
                        continue;

                    desired[st.SupertileId] = record;
                    MarkSupertileChildrenCovered(st, coveredLeafIds, leafById);
                }

                int farZoneMinIdeal = _effectiveHlodFactors.Contains(4) ? 4 : 2;
                foreach (StreamingSupertileEntry st in sorted2x2)
                {
                    if (!SupertileIntersectsLoadRegion(st, tileSize))
                        continue;
                    if (!SupertileInsideGridZone(st, tileSize, zone4x4))
                        continue;
                    if (!SupertileCanCoverCells(st, minIdealFactor: farZoneMinIdeal, idealByLeafId, coveredLeafIds, leafById))
                        continue;

                    var record = RuntimeTileRecord.FromSupertile(st);
                    if (!TerrainDataAvailable(record))
                        continue;

                    desired[st.SupertileId] = record;
                    MarkSupertileChildrenCovered(st, coveredLeafIds, leafById);
                }
            }

            foreach (KeyValuePair<string, int> kvp in idealByLeafId)
            {
                if (coveredLeafIds.Contains(kvp.Key))
                    continue;
                if (!leafById.TryGetValue(kvp.Key, out StreamingTileEntry leaf))
                    continue;
                if (!distanceByLeafId.TryGetValue(kvp.Key, out float dist))
                    continue;

                int ideal = kvp.Value;
                bool use1x1 = false;
                bool only1x1Hlod = !_effectiveHlodFactors.Contains(2);

                if (ideal == 1)
                {
                    use1x1 = only1x1Hlod || LeafInsideFineZone(leaf, tileSize, zone1x1);
                }
                else if (ideal == 2)
                {
                    // Inner half of the 2×2 distance band: fill 2×2 grid alignment holes with 1×1, not dataset edges.
                    use1x1 = dist <= inner2x2MaxDistance &&
                               LeafInsideFineZone(leaf, tileSize, zone2x2) &&
                               !IsDatasetBorderLeaf(leaf, leafById, tileSize);
                }

                if (!use1x1)
                    continue;

                var record = RuntimeTileRecord.FromLeaf(leaf, tileSize);
                if (!TerrainDataAvailable(record))
                    continue;

                desired[leaf.TileId] = record;
                coveredLeafIds.Add(leaf.TileId);
            }

            AddUncoveredRegionLeafFallback(desired, idealByLeafId, coveredLeafIds, leafById, tileSize);
            return desired;
        }

        Dictionary<string, RuntimeTileRecord> BuildDesiredTilesPerTileDistance(
            Vector3 camPos,
            HlodDistanceMetric hlodMetric)
        {
            var desired = new Dictionary<string, RuntimeTileRecord>();
            int tileSize = _manifest.TileSizeMeters;

            var leafById = new Dictionary<string, StreamingTileEntry>();
            foreach (StreamingTileEntry leaf in _manifest.Tiles ?? new List<StreamingTileEntry>())
                leafById[leaf.TileId] = leaf;

            List<StreamingSupertileEntry> supertiles = _manifest.Supertiles ?? new List<StreamingSupertileEntry>();
            foreach (StreamingSupertileEntry st in supertiles.OrderByDescending(s => s.Factor))
            {
                if (!SupertileIntersectsLoadRegion(st, tileSize))
                    continue;

                int stSizeMeters = st.Factor * tileSize;
                float dist = HlodRingEvaluator.TileBoundaryDistance(
                    camPos, st.GetUnityPosition(), stSizeMeters, hlodMetric);
                if (dist > unloadRadius || dist > loadRadius)
                    continue;

                int effectiveFactor = HlodRingEvaluator.ResolveEffectiveFactor(
                    dist, _hlodLevels, _effectiveHlodFactors);
                if (!HlodRingEvaluator.SupertileSatisfiesFactor(st.Factor, effectiveFactor))
                    continue;

                if (st.Factor > effectiveFactor &&
                    FinerSupertileAlreadyInDesired(st, desired))
                    continue;

                if (st.Factor == 2 && effectiveFactor == 4 && st.ChildTileIds != null &&
                    st.ChildTileIds.Exists(id => IsLeafCoveredByDesiredSupertile(id, desired)))
                    continue;

                var record = RuntimeTileRecord.FromSupertile(st);
                if (!TerrainDataAvailable(record))
                    continue;

                desired[st.SupertileId] = record;
            }

            float inner2x2MaxDistance = Inner2x2BandMaxDistanceMeters();

            foreach (StreamingTileEntry leaf in _manifest.Tiles ?? new List<StreamingTileEntry>())
            {
                if (!IsLeafInsideLoadRegion(leaf))
                    continue;

                float dist = HlodRingEvaluator.TileBoundaryDistance(
                    camPos, leaf.GetUnityPosition(), tileSize, hlodMetric);
                if (dist > unloadRadius || dist > loadRadius)
                    continue;

                int effectiveFactor = HlodRingEvaluator.ResolveEffectiveFactor(
                    dist, _hlodLevels, _effectiveHlodFactors);

                if (IsLeafCoveredByDesiredSupertile(leaf.TileId, desired))
                    continue;

                bool use1x1 = effectiveFactor == 1 ||
                              (effectiveFactor == 2 &&
                               dist <= inner2x2MaxDistance &&
                               !IsDatasetBorderLeaf(leaf, leafById, tileSize));

                if (!use1x1)
                    continue;

                var record = RuntimeTileRecord.FromLeaf(leaf, _manifest.TileSizeMeters);
                if (!TerrainDataAvailable(record))
                    continue;

                desired[leaf.TileId] = record;
            }

            AddUncoveredRegionLeafFallbackPerTileDistance(desired, leafById, camPos, tileSize, hlodMetric);
            return desired;
        }

        /// <summary>
        /// Small load regions may not align to 2×2/4×4 HLOD grids. Ensure every in-range regional
        /// leaf still streams at 1×1 when no supertile could claim it.
        /// </summary>
        void AddUncoveredRegionLeafFallback(
            Dictionary<string, RuntimeTileRecord> desired,
            Dictionary<string, int> idealByLeafId,
            HashSet<string> coveredLeafIds,
            Dictionary<string, StreamingTileEntry> leafById,
            int tileSize)
        {
            if (!limitLoadRegion || idealByLeafId == null)
                return;

            foreach (KeyValuePair<string, int> kvp in idealByLeafId)
            {
                if (coveredLeafIds != null && coveredLeafIds.Contains(kvp.Key))
                    continue;
                if (desired.ContainsKey(kvp.Key))
                    continue;
                if (IsLeafCoveredByDesiredSupertile(kvp.Key, desired))
                    continue;
                if (!leafById.TryGetValue(kvp.Key, out StreamingTileEntry leaf))
                    continue;

                var record = RuntimeTileRecord.FromLeaf(leaf, tileSize);
                if (!TerrainDataAvailable(record))
                    continue;

                desired[leaf.TileId] = record;
                coveredLeafIds?.Add(leaf.TileId);
            }
        }

        void AddUncoveredRegionLeafFallbackPerTileDistance(
            Dictionary<string, RuntimeTileRecord> desired,
            Dictionary<string, StreamingTileEntry> leafById,
            Vector3 camPos,
            int tileSize,
            HlodDistanceMetric hlodMetric)
        {
            if (!limitLoadRegion)
                return;

            foreach (StreamingTileEntry leaf in _manifest.Tiles ?? new List<StreamingTileEntry>())
            {
                if (!IsLeafInsideLoadRegion(leaf))
                    continue;

                float dist = HlodRingEvaluator.TileBoundaryDistance(
                    camPos, leaf.GetUnityPosition(), tileSize, hlodMetric);
                if (dist > unloadRadius || dist > loadRadius)
                    continue;

                if (desired.ContainsKey(leaf.TileId))
                    continue;
                if (IsLeafCoveredByDesiredSupertile(leaf.TileId, desired))
                    continue;

                var record = RuntimeTileRecord.FromLeaf(leaf, tileSize);
                if (!TerrainDataAvailable(record))
                    continue;

                desired[leaf.TileId] = record;
            }
        }

        void EvaluateStreaming()
        {
            ApplyDesiredTilesAndScheduleLoads();
            ProcessPendingLoads();
        }

        Dictionary<string, RuntimeTileRecord> BuildDesiredTilesSnapshot(Vector3? cameraPosition = null)
        {
            Vector3 camPos = cameraPosition ?? (_cam != null ? _cam.position : Vector3.zero);
            HlodDistanceMetric hlodMetric = GetHlodDistanceMetric();
            Dictionary<string, RuntimeTileRecord> desired = useGridAlignedHlodZones
                ? BuildDesiredTilesPerCellCoarseToFine(camPos, hlodMetric)
                : BuildDesiredTilesPerTileDistance(camPos, hlodMetric);
            CullRecordsOutsideLoadRegion(desired);
            return desired;
        }

        StreamingRegionBoundsEpsg GetLoadRegionBounds() =>
            StreamingRegionBoundsEpsg.FromFields(
                limitLoadRegion, loadRegionMinE, loadRegionMaxE, loadRegionMinN, loadRegionMaxN);

        bool IsRecordInsideLoadRegion(RuntimeTileRecord record)
        {
            if (record == null || !limitLoadRegion)
                return !limitLoadRegion;

            int right = record.Left + record.TileSizeMeters;
            int top = record.Bottom + record.TileSizeMeters;
            return GetLoadRegionBounds().IntersectsTile(record.Left, right, record.Bottom, top);
        }

        bool IsLeafInsideLoadRegion(StreamingTileEntry leaf)
        {
            if (leaf == null || !limitLoadRegion)
                return !limitLoadRegion;

            return GetLoadRegionBounds().IntersectsTile(leaf.Left, leaf.Right, leaf.Bottom, leaf.Top);
        }

        void CullRecordsOutsideLoadRegion(Dictionary<string, RuntimeTileRecord> desired)
        {
            if (!limitLoadRegion || desired == null || desired.Count == 0)
                return;

            var remove = new List<string>();
            foreach (KeyValuePair<string, RuntimeTileRecord> kv in desired)
            {
                if (!IsRecordInsideLoadRegion(kv.Value))
                    remove.Add(kv.Key);
            }

            foreach (string key in remove)
                desired.Remove(key);
        }

        void ApplyDesiredTilesAndScheduleLoads()
        {
            Dictionary<string, RuntimeTileRecord> desired = BuildDesiredTilesSnapshot();

            if (desired.Count == 0 && _lastDesiredCount != 0)
            {
                Debug.LogWarning(
                    "[ZGConnect.Realtime] No streamable tiles near camera. " +
                    "Move camera toward dataset, increase load radius, or re-pack missing heightmaps.");
            }
            _lastDesiredCount = desired.Count;

            int candidateCount = desired.Count;
            var sorted = desired.Values
                .OrderBy(r => r, Comparer<RuntimeTileRecord>.Create(CompareTileLoadPriority))
                .ToList();

            if (candidateCount > maxLoadedTiles && _lastDesiredCount != candidateCount)
            {
                Debug.LogWarning(
                    $"[ZGConnect.Realtime] {candidateCount} tiles in range but maxLoadedTiles={maxLoadedTiles}. " +
                    $"{candidateCount - maxLoadedTiles} tile(s) may wait in queue — increase maxLoadedTiles on the streamer.");
            }

            _currentDesiredKeys.Clear();
            foreach (RuntimeTileRecord record in sorted)
                _currentDesiredKeys.Add(record.Key);

            foreach (string loadingKey in _loading.Keys.ToList())
            {
                if (!_currentDesiredKeys.Contains(loadingKey))
                    CancelLoad(loadingKey);
            }

            ScheduleDesiredTileLoads(sorted, desired);
            UpdateLeafHlodTransitionVisibility();
            EnsureVegetationQueuedForLoadedTiles();
            ProcessDeferredHlodUnloads();

            if (_cam != null)
            {
                _terrainDiagnostics.ReportPeriodicCoverage(
                    this,
                    _manifest,
                    _datasetRoot,
                    _cam.position,
                    loadRadius,
                    _currentDesiredKeys,
                    _loaded,
                    _loading,
                    IsLoadQueued);
            }
        }

        void ScheduleDesiredTileLoads(
            List<RuntimeTileRecord> sorted,
            Dictionary<string, RuntimeTileRecord> desired)
        {
            int desiredLoadBudgetUsed = CountDesiredLoadedTiles() + _loading.Count;

            void TrySchedule(RuntimeTileRecord record)
            {
                if (desiredLoadBudgetUsed >= maxLoadedTiles)
                    return;
                if (_loaded.ContainsKey(record.Key) || _loading.ContainsKey(record.Key))
                    return;
                if (IsLoadQueued(record.Key))
                    return;
                if (!record.IsSupertile && IsLeafCoveredByDesiredSupertile(record.Key, desired))
                    return;

                RequestLoad(record);
                desiredLoadBudgetUsed++;
            }

            foreach (RuntimeTileRecord record in sorted)
            {
                if (record.IsSupertile)
                    TrySchedule(record);
            }

            foreach (RuntimeTileRecord record in sorted)
            {
                if (!record.IsSupertile)
                    TrySchedule(record);
            }
        }

        void UpdateLeafHlodTransitionVisibility()
        {
            if (UsesEditorPopulatedVegetationTerrain())
                return;

            foreach (RuntimeTileRecord leaf in _loaded.Values)
            {
                if (leaf.IsSupertile || leaf.TerrainObject == null)
                    continue;

                bool shouldShow = ShouldShowVegetationTerrain(leaf);
                bool wasShowing = leaf.TerrainObject.activeSelf;
                if (shouldShow == wasShowing)
                    continue;

                leaf.TerrainObject.SetActive(shouldShow);

                if (shouldShow)
                {
                    if (ShouldLoadVegetation(leaf))
                        QueueVegetationLoad(leaf);
                }
                else
                {
                    DestroyVegetation(leaf);
                }
            }
        }

        bool ShouldShowVegetationTerrain(RuntimeTileRecord record)
        {
            if (ResolveVegetationTerrainObject(record) == null)
                return false;

            if (UsesEditorPopulatedVegetationTerrain())
                return true;

            if (_currentDesiredKeys.Contains(record.Key))
                return true;

            return !ShouldDeferLeafUnload(record);
        }

        int CountDesiredLoadedTiles()
        {
            int count = 0;
            foreach (RuntimeTileRecord record in _loaded.Values)
            {
                if (_currentDesiredKeys.Contains(record.Key))
                    count++;
            }

            return count;
        }

        bool IsLoadQueued(string key)
        {
            foreach (RuntimeTileRecord pending in _pendingLoads)
            {
                if (pending.Key == key)
                    return true;
            }

            return false;
        }

        void ProcessDeferredHlodUnloads()
        {
            foreach (RuntimeTileRecord loaded in _loaded.Values.ToList())
            {
                if (limitLoadRegion && !IsRecordInsideLoadRegion(loaded))
                {
                    UnloadRecord(loaded);
                    continue;
                }

                if (_currentDesiredKeys.Contains(loaded.Key))
                    continue;
                if (ShouldDeferHlodUnload(loaded))
                    continue;
                UnloadRecord(loaded);
            }
        }

        bool ShouldDeferHlodUnload(RuntimeTileRecord record)
        {
            if (record.IsSupertile)
                return ShouldDeferSupertileUnload(record);

            return ShouldDeferLeafUnload(record);
        }

        bool ShouldDeferLeafUnload(RuntimeTileRecord leaf)
        {
            string replacingSupertileId = FindDesiredSupertileKeyForLeaf(leaf.Key);
            if (string.IsNullOrEmpty(replacingSupertileId))
                return false;

            if (_loaded.TryGetValue(replacingSupertileId, out RuntimeTileRecord supertile) &&
                IsTerrainFullyReady(supertile))
                return false;

            return IsReplacementPending(replacingSupertileId);
        }

        bool ShouldDeferSupertileUnload(RuntimeTileRecord supertile)
        {
            if (supertile.Supertile == null)
                return false;

            if (supertile.Supertile.ChildTileIds != null)
            {
                bool anyReplacingLeafDesired = false;
                foreach (string childId in supertile.Supertile.ChildTileIds)
                {
                    if (!_currentDesiredKeys.Contains(childId))
                        continue;

                    anyReplacingLeafDesired = true;
                    if (!_loaded.TryGetValue(childId, out RuntimeTileRecord leaf) ||
                        !IsTerrainFullyReady(leaf))
                        return true;
                }

                if (anyReplacingLeafDesired)
                    return false;
            }

            List<string> finerSupertiles = FindDesiredFinerSupertilesReplacing(supertile);
            if (finerSupertiles.Count == 0)
                return false;

            foreach (string stKey in finerSupertiles)
            {
                if (!_loaded.TryGetValue(stKey, out RuntimeTileRecord st) || !IsTerrainFullyReady(st))
                    return true;
            }

            return false;
        }

        List<string> FindDesiredFinerSupertilesReplacing(RuntimeTileRecord coarse)
        {
            var result = new List<string>();
            if (!coarse.IsSupertile || coarse.Supertile == null)
                return result;

            int coarseSize = coarse.TileSizeMeters;
            int coarseLeft = coarse.Left;
            int coarseBottom = coarse.Bottom;
            int coarseRight = coarseLeft + coarseSize;
            int coarseTop = coarseBottom + coarseSize;

            foreach (StreamingSupertileEntry st in _manifest.Supertiles ?? new List<StreamingSupertileEntry>())
            {
                if (!_currentDesiredKeys.Contains(st.SupertileId))
                    continue;
                if (st.Factor >= coarse.HlodFactor)
                    continue;

                int stSize = st.Factor * _manifest.TileSizeMeters;
                if (st.Left >= coarseLeft && st.Bottom >= coarseBottom &&
                    st.Left + stSize <= coarseRight && st.Bottom + stSize <= coarseTop)
                    result.Add(st.SupertileId);
            }

            return result;
        }

        string FindDesiredSupertileKeyForLeaf(string leafId)
        {
            foreach (StreamingSupertileEntry st in _manifest.Supertiles ?? new List<StreamingSupertileEntry>())
            {
                if (!_currentDesiredKeys.Contains(st.SupertileId))
                    continue;
                if (st.ChildTileIds != null && st.ChildTileIds.Contains(leafId))
                    return st.SupertileId;
            }

            return null;
        }

        bool IsReplacementPending(string replacementKey)
        {
            if (_loaded.ContainsKey(replacementKey) || _loading.ContainsKey(replacementKey))
                return true;

            return IsLoadQueued(replacementKey) || _currentDesiredKeys.Contains(replacementKey);
        }

        bool IsTerrainFullyReady(RuntimeTileRecord record)
        {
            if (record.State != RealtimeTileLodState.Loaded)
                return false;

            if (!streamTerrain)
                return true;

            if (record.TerrainObject == null || record.TerrainData == null)
                return false;

            if (!streamOrthoBasemaps && !streamTiledBasemaps)
                return true;

            if (string.IsNullOrEmpty(activeBasemapId))
                return true;

            StreamingBasemapEntry bm = _manifest.AvailableBasemaps?
                .Find(b => b.Id == activeBasemapId);
            if (bm == null)
                return true;

            if (bm.GetBasemapType() == BasemapType.Ortho && streamOrthoBasemaps)
            {
                if (TryGetTerrainBundleRelativePath(record, out _))
                {
                    return record.LoadedFromTerrainBundle &&
                           record.LoadedTerrainBundleBasemapId == activeBasemapId &&
                           TerrainDataHasOrthoLayer(record.TerrainData);
                }

                return record.ActiveBasemapId == activeBasemapId &&
                       record.OrthoTextures.TryGetValue(activeBasemapId, out Texture2D ortho) &&
                       ortho != null;
            }

            if (bm.GetBasemapType() == BasemapType.Tiled && streamTiledBasemaps && !record.IsSupertile)
                return record.ActiveBasemapId == activeBasemapId;

            return true;
        }

        void RequestLoad(RuntimeTileRecord template)
        {
            if (_loaded.ContainsKey(template.Key) || _loading.ContainsKey(template.Key))
                return;

            foreach (RuntimeTileRecord pending in _pendingLoads)
            {
                if (pending.Key == template.Key)
                    return;
            }

            _cancelledLoads.Remove(template.Key);
            _pendingLoads.Add(template);
        }

        void CancelLoad(string key)
        {
            _cancelledLoads.Add(key);
            _loading.Remove(key);
            _pendingLoads.RemoveAll(p => p.Key == key);

            var retainedFinalize = new Queue<(RuntimeTileRecord record, RuntimeTilePrepareResult result)>();
            while (_readyToFinalize.Count > 0)
            {
                var item = _readyToFinalize.Dequeue();
                if (item.record.Key != key)
                    retainedFinalize.Enqueue(item);
            }

            while (retainedFinalize.Count > 0)
                _readyToFinalize.Enqueue(retainedFinalize.Dequeue());
        }

        void ProcessPendingLoads()
        {
            if (!useBackgroundTilePrepare)
            {
                while (_activeFinalizes < maxConcurrentTileFinalizes &&
                       TryDequeueHighestPriorityPending(out RuntimeTileRecord record))
                {
                    if (TryBeginTerrainLoad(record, out string bundleRel))
                        StartCoroutine(LoadTileFromBundleCoroutine(record, bundleRel));
                }

                return;
            }

            while (_loading.Count < maxConcurrentBackgroundPrepares &&
                   TryDequeueHighestPriorityPending(out RuntimeTileRecord record))
            {
                _loading[record.Key] = record;
                record.State = RealtimeTileLodState.Loading;

                if (TryBeginTerrainLoad(record, out string bundleRel))
                    StartCoroutine(LoadTileFromBundleCoroutine(record, bundleRel));
            }

            while (_activeFinalizes < maxConcurrentTileFinalizes && _readyToFinalize.Count > 0)
            {
                (RuntimeTileRecord record, RuntimeTilePrepareResult result) = _readyToFinalize.Dequeue();
                if (_cancelledLoads.Contains(record.Key))
                {
                    _loading.Remove(record.Key);
                    continue;
                }

                StartCoroutine(FinalizeTileCoroutine(record, result));
            }
        }

        bool TryBeginTerrainLoad(RuntimeTileRecord record, out string bundleRel)
        {
            if (TryGetTerrainBundleRelativePath(record, out bundleRel))
                return true;

            if (terrainBundleOnlyMode)
            {
                ReportTerrainBundleOnlyUnavailable(record);
                return false;
            }

            if (!useBackgroundTilePrepare)
                StartCoroutine(LoadTileCoroutineSync(record));
            else
                StartBackgroundPrepare(record);

            return false;
        }

        void ReportTerrainBundleOnlyUnavailable(RuntimeTileRecord record)
        {
            _loading.Remove(record.Key);
            _terrainDiagnostics.ReportLoadFailed(
                record,
                _datasetRoot,
                activeBasemapId,
                preferTerrainBundles,
                terrainBundleOnlyMode,
                $"Bundle-only mode: no terrain bundle for basemap '{activeBasemapId}'.");
            ProcessPendingLoads();
        }

        void StartBackgroundPrepare(RuntimeTileRecord record)
        {
            if (terrainBundleOnlyMode)
            {
                ReportTerrainBundleOnlyUnavailable(record);
                return;
            }

            string hmPath = ResolveHeightmapPath(record);
            Vector3 size = record.IsSupertile
                ? record.Supertile.GetTerrainSize()
                : record.Leaf.GetTerrainSize();
            bool flipHeightmap = ResolveHeightmapFlip(record);
            string orthoPath = ResolveOrthoDiskPath(record, activeBasemapId);

            Task<RuntimeTilePrepareResult> task = RuntimeTilePreparer.PrepareAsync(
                hmPath,
                record.HeightmapRes,
                size,
                flipHeightmap,
                streamOrthoBasemaps ? orthoPath : null);

            _prepareTasks[record.Key] = task;
            StartCoroutine(WaitForPrepareCoroutine(record, task));
        }

        IEnumerator WaitForPrepareCoroutine(RuntimeTileRecord record, Task<RuntimeTilePrepareResult> task)
        {
            while (!task.IsCompleted)
                yield return null;

            _prepareTasks.Remove(record.Key);

            if (_cancelledLoads.Contains(record.Key))
            {
                _loading.Remove(record.Key);
                ProcessPendingLoads();
                yield break;
            }

            RuntimeTilePrepareResult result = task.Result;
            if (!result.Success)
            {
                _terrainDiagnostics.ReportLoadFailed(
                    record,
                    _datasetRoot,
                    activeBasemapId,
                    preferTerrainBundles,
                    terrainBundleOnlyMode,
                    $"Background prepare failed: {result.Error}");
                _loading.Remove(record.Key);
                ProcessPendingLoads();
                yield break;
            }

            _readyToFinalize.Enqueue((record, result));
            ProcessPendingLoads();
        }

        IEnumerator LoadTileFromBundleCoroutine(RuntimeTileRecord record, string bundleRel)
        {
            _activeFinalizes++;
            yield return null;

            if (IsTerrainLoadCancelled(record.Key))
            {
                _activeFinalizes--;
                ProcessPendingLoads();
                yield break;
            }

            bool terrainLoaded = false;
            string bundleError = null;
            if (streamTerrain)
            {
                yield return LoadTerrainFromBundlesCoroutine(record, result =>
                {
                    terrainLoaded = result.Success;
                    bundleError = result.Error;
                });

                if (!terrainLoaded && !IsTerrainLoadCancelled(record.Key))
                {
                    if (!string.IsNullOrEmpty(bundleError))
                    {
                        _terrainDiagnostics.LogOnce(
                            $"bundle-fail:{record.Key}",
                            $"[ZGConnect.Realtime] Terrain bundle load failed for '{record.Key}' ({bundleError}).",
                            asError: false);
                    }

                    if (!terrainBundleOnlyMode && HeightmapFileExists(record))
                        terrainLoaded = TryLoadTerrain(record);
                }
            }

            yield return null;
            yield return CompleteTileLoadCoroutine(record, terrainLoaded, bundleError);
        }

        readonly struct TerrainBundleLoadAttempt
        {
            public readonly bool Success;
            public readonly string Error;

            public TerrainBundleLoadAttempt(bool success, string error)
            {
                Success = success;
                Error = error;
            }
        }

        IEnumerable<string> GetTerrainBundleBasemapOrder(RuntimeTileRecord record)
        {
            var ordered = new List<string>();
            if (!string.IsNullOrEmpty(activeBasemapId))
                ordered.Add(activeBasemapId);

            Dictionary<string, string> bundlePaths = record.IsSupertile
                ? record.Supertile?.TerrainBundlePaths
                : record.Leaf?.TerrainBundlePaths;
            if (bundlePaths != null)
            {
                foreach (string basemapId in bundlePaths.Keys)
                {
                    if (!ordered.Contains(basemapId))
                        ordered.Add(basemapId);
                }
            }

            return ordered;
        }

        bool TryResolveTerrainBundlePath(RuntimeTileRecord record, string basemapId, out string bundleRel)
        {
            bundleRel = null;
            if (string.IsNullOrEmpty(basemapId))
                return false;

            Dictionary<string, string> bundlePaths = record.IsSupertile
                ? record.Supertile?.TerrainBundlePaths
                : record.Leaf?.TerrainBundlePaths;
            if (bundlePaths == null ||
                !bundlePaths.TryGetValue(basemapId, out bundleRel) ||
                string.IsNullOrEmpty(bundleRel))
                return false;

            string fullPath = Path.Combine(_datasetRoot, bundleRel.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(fullPath);
        }

        IEnumerator LoadTerrainFromBundlesCoroutine(
            RuntimeTileRecord record,
            System.Action<TerrainBundleLoadAttempt> onComplete)
        {
            var errors = new List<string>();
            foreach (string basemapId in GetTerrainBundleBasemapOrder(record))
            {
                if (IsTerrainLoadCancelled(record.Key))
                {
                    onComplete?.Invoke(new TerrainBundleLoadAttempt(false, null));
                    yield break;
                }

                if (!TryResolveTerrainBundlePath(record, basemapId, out string bundleRel))
                    continue;

                string fullPath = Path.Combine(_datasetRoot, bundleRel.Replace('/', Path.DirectorySeparatorChar));
                var loadResult = new RuntimeTerrainBundleLoader.LoadResult();
                yield return RuntimeTerrainBundleLoader.LoadTerrainDataAsync(fullPath, loadResult);

                if (!loadResult.Success)
                {
                    errors.Add($"{basemapId}: {loadResult.Error}");
                    continue;
                }

                if (IsTerrainLoadCancelled(record.Key))
                {
                    ReleaseTerrainBundle(record, loadResult.BundleFullPath, unloadAllLoadedObjects: true);
                    onComplete?.Invoke(new TerrainBundleLoadAttempt(false, null));
                    yield break;
                }

                if (InstantiateTerrainFromBundle(
                        record,
                        loadResult.TerrainData,
                        loadResult.TerrainPrefab,
                        loadResult.Bundle,
                        loadResult.BundleFullPath,
                        basemapId))
                {
                    onComplete?.Invoke(new TerrainBundleLoadAttempt(true, null));
                    yield break;
                }

                ReleaseTerrainBundle(record, loadResult.BundleFullPath, unloadAllLoadedObjects: true);
                yield return null;
                errors.Add($"{basemapId}: bundle opened but terrain could not be instantiated.");
            }

            string combined = errors.Count > 0 ? string.Join(" | ", errors) : "no terrain bundle on disk.";
            onComplete?.Invoke(new TerrainBundleLoadAttempt(false, combined));
        }

        IEnumerator FinalizeTileCoroutine(RuntimeTileRecord record, RuntimeTilePrepareResult prepared)
        {
            _activeFinalizes++;
            yield return null;

            bool terrainLoaded = false;
            if (streamTerrain)
                terrainLoaded = FinalizeTerrainFromPrepared(record, prepared);

            yield return null;
            yield return CompleteTileLoadCoroutine(record, terrainLoaded);
        }

        IEnumerator CompleteTileLoadCoroutine(
            RuntimeTileRecord record,
            bool terrainLoaded,
            string bundleError = null)
        {
            if (streamTerrain && !terrainLoaded)
            {
                if (IsTerrainLoadCancelled(record.Key) || !IsTerrainLoadStillWanted(record.Key))
                {
                    _loading.Remove(record.Key);
                    _activeFinalizes--;
                    ProcessPendingLoads();
                    yield break;
                }

                string detail = string.IsNullOrEmpty(bundleError)
                    ? "Terrain could not be instantiated after load."
                    : $"Terrain bundle failed: {bundleError}";
                _terrainDiagnostics.ReportLoadFailed(
                    record,
                    _datasetRoot,
                    activeBasemapId,
                    preferTerrainBundles,
                    terrainBundleOnlyMode,
                    detail);
                _loading.Remove(record.Key);
                _activeFinalizes--;
                ProcessPendingLoads();
                yield break;
            }

            record.State = RealtimeTileLodState.Loaded;
            _loading.Remove(record.Key);
            _loaded[record.Key] = record;
            _activeFinalizes--;
            TerrainTileLoaded?.Invoke(record);

            if (ShouldLoadVegetation(record) && (record.IsSupertile || ShouldShowVegetationTerrain(record)))
                QueueVegetationLoad(record);

            UpdateLeafHlodTransitionVisibility();
            ProcessDeferredHlodUnloads();
            ProcessPendingLoads();
        }

        IEnumerator LoadTileCoroutineSync(RuntimeTileRecord template)
        {
            _activeFinalizes++;
            _loading[template.Key] = template;
            template.State = RealtimeTileLodState.Loading;
            yield return null;

            bool terrainLoaded = false;
            if (streamTerrain)
                terrainLoaded = TryLoadTerrain(template);

            yield return null;

            if (streamTerrain && !terrainLoaded)
            {
                _terrainDiagnostics.ReportLoadFailed(
                    template,
                    _datasetRoot,
                    activeBasemapId,
                    preferTerrainBundles,
                    terrainBundleOnlyMode,
                    "Synchronous terrain load failed.");
                _loading.Remove(template.Key);
                _activeFinalizes--;
                ProcessPendingLoads();
                yield break;
            }

            template.State = RealtimeTileLodState.Loaded;
            _loading.Remove(template.Key);
            _loaded[template.Key] = template;
            _activeFinalizes--;
            TerrainTileLoaded?.Invoke(template);

            if (ShouldLoadVegetation(template) && (template.IsSupertile || ShouldShowVegetationTerrain(template)))
                QueueVegetationLoad(template);

            UpdateLeafHlodTransitionVisibility();
            ProcessDeferredHlodUnloads();
            ProcessPendingLoads();
        }

        bool FinalizeTerrainFromPrepared(RuntimeTileRecord template, RuntimeTilePrepareResult prepared)
        {
            try
            {
                template.TerrainData = RuntimeTerrainFactory.CreateTerrainDataFromHeights(
                    prepared.Heights,
                    prepared.HeightmapRes,
                    prepared.TerrainSize);

                if (!InstantiateTerrainFromData(template, template.TerrainData, null, loadedFromBundle: false))
                    return false;

                ApplyBasemap(template, prepared.OrthoPngBytes);
                return true;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ZGConnect.Realtime] Failed to finalize tile '{template.Key}': {ex}");
                return false;
            }
        }

        bool IsTerrainLoadCancelled(string key) => _cancelledLoads.Contains(key);

        bool IsTerrainLoadStillWanted(string key) =>
            _currentDesiredKeys.Contains(key) || _loading.ContainsKey(key);

        static void ReleaseTerrainBundle(
            RuntimeTileRecord record,
            string bundleFullPath,
            bool unloadAllLoadedObjects)
        {
            string path = !string.IsNullOrEmpty(bundleFullPath)
                ? bundleFullPath
                : record?.TerrainBundleFullPath;
            if (!string.IsNullOrEmpty(path))
                RuntimeAssetBundleCache.Release(path, unloadAllLoadedObjects);

            if (record == null)
                return;

            record.TerrainBundle = null;
            record.TerrainBundleFullPath = null;
        }

        bool InstantiateTerrainFromBundle(
            RuntimeTileRecord template,
            TerrainData terrainData,
            GameObject terrainPrefab,
            AssetBundle bundle,
            string bundleFullPath,
            string loadedBasemapId = null)
        {
            if (terrainData == null && terrainPrefab == null)
                return false;

            if (!IsTerrainLoadStillWanted(template.Key))
            {
                ReleaseTerrainBundle(null, bundleFullPath, unloadAllLoadedObjects: true);
                return false;
            }

            template.TerrainData = terrainData ?? terrainPrefab?.GetComponent<Terrain>()?.terrainData;
            template.TerrainBundle = bundle;
            template.TerrainBundleFullPath = bundleFullPath;
            template.LoadedFromTerrainBundle = true;

            if (terrainPrefab != null)
            {
                template.TerrainObject = Instantiate(terrainPrefab, terrainRoot);
                template.TerrainObject.transform.position = template.UnityPosition;
            }
            else
            {
                bool useDrawInstanced = drawInstanced;
#if !UNITY_EDITOR
                useDrawInstanced = false;
#endif
                template.TerrainObject = RuntimeTerrainFactory.CreateTerrainGameObject(
                    template.TerrainData,
                    template.UnityPosition,
                    terrainRoot,
                    terrainMaterial,
                    useDrawInstanced,
                    basemapDistance);
            }

            template.TerrainObject.name = template.IsSupertile
                ? $"Supertile_{template.Key}"
                : $"Tile_{template.Key}";

            Terrain terrain = template.TerrainObject.GetComponent<Terrain>();
            if (terrain != null)
            {
                terrain.allowAutoConnect = false;
                string basemapId = loadedBasemapId ?? activeBasemapId;
                template.LoadedTerrainBundleBasemapId = basemapId;
                template.ActiveBasemapId = basemapId;
                RuntimeBasemapFactory.ApplyMatteTerrainShading(terrain);
                ApplyBasemapVisuals(template, terrain);
                terrain.Flush();
            }

            _terrainByCoord[$"{template.Left}_{template.Bottom}"] = terrain;
            LogTerrainShaderOnce(terrain);
            if (terrain != null && _cam != null)
            {
                float dist = HlodRingEvaluator.TileBoundaryDistance(
                    _cam.position, template.UnityPosition, template.TileSizeMeters);
                ApplyTerrainPixelError(template.Key, terrain, dist);
            }

            RewireNeighbors();
            SuppressStreamingVisualIfNeeded(template.TerrainObject);
            return true;
        }

        bool InstantiateTerrainFromData(
            RuntimeTileRecord template,
            TerrainData terrainData,
            AssetBundle bundle,
            bool loadedFromBundle)
        {
            if (terrainData == null)
                return false;

            if (!IsTerrainLoadStillWanted(template.Key))
                return false;

            template.TerrainData = terrainData;
            template.TerrainBundle = bundle;
            template.LoadedFromTerrainBundle = loadedFromBundle;

            bool useDrawInstanced = drawInstanced;
#if !UNITY_EDITOR
            useDrawInstanced = false;
#endif
            template.TerrainObject = RuntimeTerrainFactory.CreateTerrainGameObject(
                template.TerrainData,
                template.UnityPosition,
                terrainRoot,
                terrainMaterial,
                useDrawInstanced,
                basemapDistance);
            template.TerrainObject.name = template.IsSupertile
                ? $"Supertile_{template.Key}"
                : $"Tile_{template.Key}";

            Terrain terrain = template.TerrainObject.GetComponent<Terrain>();
            if (terrain != null)
            {
                terrain.allowAutoConnect = false;
                if (loadedFromBundle)
                {
                    template.LoadedTerrainBundleBasemapId = activeBasemapId;
                    template.ActiveBasemapId = activeBasemapId;
                    RuntimeBasemapFactory.ApplyMatteTerrainShading(terrain);
                }

                ApplyBasemapVisuals(template, terrain);
                terrain.Flush();
            }

            _terrainByCoord[$"{template.Left}_{template.Bottom}"] = terrain;
            LogTerrainShaderOnce(terrain);
            if (terrain != null && _cam != null)
            {
                float dist = HlodRingEvaluator.TileBoundaryDistance(
                    _cam.position, template.UnityPosition, template.TileSizeMeters);
                ApplyTerrainPixelError(template.Key, terrain, dist);
            }

            RewireNeighbors();
            SuppressStreamingVisualIfNeeded(template.TerrainObject);
            return true;
        }

        static bool TerrainDataHasOrthoLayer(TerrainData terrainData)
        {
            if (terrainData?.terrainLayers == null || terrainData.terrainLayers.Length == 0)
                return false;

            foreach (TerrainLayer layer in terrainData.terrainLayers)
            {
                if (layer?.diffuseTexture != null)
                    return true;
            }

            return false;
        }

        bool TryGetTerrainBundleRelativePath(RuntimeTileRecord record, out string bundleRel)
        {
            bundleRel = null;
            if (!preferTerrainBundles || string.IsNullOrEmpty(activeBasemapId))
                return false;

            StreamingBasemapEntry bm = _manifest.AvailableBasemaps?
                .Find(b => b.Id == activeBasemapId);
            if (bm != null && bm.GetBasemapType() != BasemapType.Ortho)
                return false;

            Dictionary<string, string> bundlePaths = record.IsSupertile
                ? record.Supertile?.TerrainBundlePaths
                : record.Leaf?.TerrainBundlePaths;

            if (bundlePaths == null ||
                !bundlePaths.TryGetValue(activeBasemapId, out bundleRel) ||
                string.IsNullOrEmpty(bundleRel))
            {
                return false;
            }

            string fullPath = Path.Combine(_datasetRoot, bundleRel.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(fullPath);
        }

        void UpdateTerrainHitHud()
        {
            if (Time.time < _nextTerrainHitCheck)
                return;

            _nextTerrainHitCheck = Time.time + 0.5f;
            if (Physics.Raycast(_cam.position, _cam.forward, out RaycastHit hit, 50000f))
                _terrainHitHud = $"hit {hit.collider.name} {hit.distance:F0}m";
            else
                _terrainHitHud = "hit none";
        }

        bool TryLoadTerrain(RuntimeTileRecord template)
        {
            if (terrainBundleOnlyMode)
            {
                _terrainDiagnostics.ReportLoadFailed(
                    template,
                    _datasetRoot,
                    activeBasemapId,
                    preferTerrainBundles,
                    terrainBundleOnlyMode,
                    "Bundle-only mode: RAW heightmap loading is disabled.");
                return false;
            }

            try
            {
                string hmPath = ResolveHeightmapPath(template);
                if (string.IsNullOrEmpty(hmPath) || !File.Exists(hmPath))
                {
                    _terrainDiagnostics.ReportLoadFailed(
                        template,
                        _datasetRoot,
                        activeBasemapId,
                        preferTerrainBundles,
                        terrainBundleOnlyMode,
                        string.IsNullOrEmpty(hmPath)
                            ? "No heightmap path in manifest."
                            : $"Heightmap file not found: {hmPath}");
                    return false;
                }

                Vector3 size = template.IsSupertile
                    ? template.Supertile.GetTerrainSize()
                    : template.Leaf.GetTerrainSize();

                bool flipHeightmap = ResolveHeightmapFlip(template);
                template.TerrainData = RuntimeTerrainFactory.CreateTerrainData(
                    hmPath, template.HeightmapRes, size, flipHeightmapVertically: flipHeightmap);

                if (!InstantiateTerrainFromData(template, template.TerrainData, null, loadedFromBundle: false))
                {
                    _terrainDiagnostics.ReportLoadFailed(
                        template,
                        _datasetRoot,
                        activeBasemapId,
                        preferTerrainBundles,
                        terrainBundleOnlyMode,
                        "InstantiateTerrainFromData returned false.");
                    return false;
                }

                ApplyBasemap(template);
                return true;
            }
            catch (System.Exception ex)
            {
                _terrainDiagnostics.ReportLoadFailed(
                    template,
                    _datasetRoot,
                    activeBasemapId,
                    preferTerrainBundles,
                    terrainBundleOnlyMode,
                    $"Exception: {ex.Message}");
                return false;
            }
        }

        bool LeafTileHasBuildings(StreamingTileEntry leaf)
        {
            if (leaf == null)
                return false;

            bool useOrtho = buildingStyle == RealtimeBuildingStyle.OrthoRoof;
            return useOrtho ? leaf.HasOrthoRoofBuildings : leaf.HasFacadeBuildings;
        }

        float GetBuildingStreamRadius() =>
            buildingCullDistanceMeters > 0f ? buildingCullDistanceMeters : loadRadius;

        bool ShouldEnableBuildingCollidersForTile(string tileId, Vector3 camPos)
        {
            if (buildingColliderLoadDistanceMeters <= 0f)
                return true;

            if (!_leafTilesById.TryGetValue(tileId, out StreamingTileEntry leaf))
                return false;

            float dist = GetLeafBoundaryDistance(
                camPos, leaf, _manifest.TileSizeMeters, GetBuildingDistanceMetric());
            return dist <= buildingColliderLoadDistanceMeters;
        }

        void ApplyBuildingTileColliderState(RuntimeTileRecord record)
        {
            if (record?.BuildingsObject == null || _cam == null)
                return;

            RuntimeBuildingTileRuntimeState state =
                record.BuildingsObject.GetComponent<RuntimeBuildingTileRuntimeState>();
            if (state == null)
                return;

            var bundleHolder = record.BuildingsObject.GetComponent<RuntimeBuildingBundleHolder>();
            bool splitPhysics = bundleHolder != null && bundleHolder.HasSplitPhysicsPrefab;
            bool wantPhysics = ShouldEnableBuildingCollidersForTile(record.Key, _cam.position);

            if (splitPhysics)
            {
                if (!wantPhysics)
                {
                    state.DetachPhysicsRoot();
                    return;
                }

                if (state.PhysicsRoot != null || state.PhysicsLoadInProgress)
                    return;

                StartCoroutine(LoadBuildingPhysicsFromBundleCoroutine(record, bundleHolder));
                return;
            }

            state.CacheBuildingColliders();
            state.SetCollidersEnabled(wantPhysics);
        }

        void UpdateBuildingColliderStates()
        {
            if (!streamBuildings || _cam == null)
                return;

            if (buildingColliderLoadDistanceMeters <= 0f)
                return;

            foreach (RuntimeTileRecord record in _buildingTilesLoaded.Values)
                ApplyBuildingTileColliderState(record);
        }

        IEnumerator LoadBuildingPhysicsFromBundleCoroutine(
            RuntimeTileRecord record,
            RuntimeBuildingBundleHolder bundleHolder)
        {
            if (record?.BuildingsObject == null || bundleHolder?.Bundle == null ||
                string.IsNullOrEmpty(bundleHolder.PhysicsPrefabAssetName))
                yield break;

            RuntimeBuildingTileRuntimeState state =
                record.BuildingsObject.GetComponent<RuntimeBuildingTileRuntimeState>();
            if (state == null)
                yield break;

            state.BeginPhysicsLoad();

            var loadResult = new RuntimeBuildingBundleLoader.LoadResult();
            yield return RuntimeBuildingBundleLoader.LoadBuildingPhysicsPrefabAsync(
                bundleHolder.Bundle,
                bundleHolder.PhysicsPrefabAssetName,
                loadResult);

            if (!loadResult.Success || loadResult.Prefab == null)
            {
                state.DetachPhysicsRoot();
                string[] bundleAssets = bundleHolder.Bundle != null
                    ? bundleHolder.Bundle.GetAllAssetNames()
                    : null;
                Debug.LogWarning(
                    $"[ZGConnect.Realtime] Building physics prefab failed for '{record.Key}': {loadResult.Error}. " +
                    $"Requested asset='{bundleHolder.PhysicsPrefabAssetName}'. " +
                    $"Bundle assets: {(bundleAssets != null ? string.Join(", ", bundleAssets) : "none")}");
                yield break;
            }

            if (!_buildingTilesLoaded.ContainsKey(record.Key) ||
                record.BuildingsObject == null ||
                !ShouldEnableBuildingCollidersForTile(record.Key, _cam.position))
            {
                state.DetachPhysicsRoot();
                yield break;
            }

            GameObject physicsInstance = Instantiate(loadResult.Prefab, record.BuildingsObject.transform);
            physicsInstance.name = $"TileBuildings_{record.Key}_Physics";
            state.AttachPhysicsRoot(physicsInstance);
        }

        HlodDistanceMetric GetBuildingDistanceMetric() =>
            useRectangularHlodDistances ? HlodDistanceMetric.Rectangular : HlodDistanceMetric.Circular;

        bool IsBuildingTileInRange(StreamingTileEntry leaf, Vector3 camPos)
        {
            if (leaf == null)
                return false;

            float dist = GetLeafBoundaryDistance(
                camPos, leaf, _manifest.TileSizeMeters, GetBuildingDistanceMetric());
            return dist <= GetBuildingStreamRadius();
        }

        void EvaluateBuildingStreaming()
        {
            if (!streamBuildings || _cam == null || _manifest?.Tiles == null)
                return;

            Vector3 camPos = _cam.position;
            var wantLoaded = new HashSet<string>();

            foreach (StreamingTileEntry leaf in _manifest.Tiles)
            {
                if (!ShouldStreamBuildingTile(leaf, camPos))
                    continue;

                wantLoaded.Add(leaf.TileId);
            }

            foreach (string tileId in _buildingTilesLoaded.Keys.ToList())
            {
                if (!wantLoaded.Contains(tileId))
                    UnloadBuildingTile(tileId);
            }

            foreach (string tileId in _buildingTilesLoading.ToList())
            {
                if (!wantLoaded.Contains(tileId) && !_buildingTilesLoaded.ContainsKey(tileId))
                    CancelBuildingLoad(tileId);
            }

            foreach (string tileId in wantLoaded)
            {
                if (_buildingTilesLoaded.ContainsKey(tileId) || IsBuildingTileInFlight(tileId))
                    continue;

                if (_inactiveBuildings.ContainsKey(tileId))
                {
                    ReactivateBuildingTile(tileId);
                    continue;
                }

                if (!_leafTilesById.TryGetValue(tileId, out StreamingTileEntry leaf))
                    continue;

                float dist = GetLeafBoundaryDistance(
                    camPos, leaf, _manifest.TileSizeMeters, GetBuildingDistanceMetric());
                RequestBuildingLoad(tileId, dist);
            }
        }

        void RequestBuildingLoad(string tileId, float priorityDistance)
        {
            foreach (BuildingStreamRequest pending in _buildingPendingLoads)
            {
                if (pending.TileId == tileId)
                    return;
            }

            foreach (BuildingBundleRequest pending in _buildingBundlePendingLoads)
            {
                if (pending.TileId == tileId)
                    return;
            }

            if (_buildingPrepareTasks.ContainsKey(tileId) ||
                _buildingPrepared.ContainsKey(tileId) ||
                _buildingInstantiating.Contains(tileId))
            {
                _buildingTilesLoading.Add(tileId);
                return;
            }

            if (!_leafTilesById.TryGetValue(tileId, out StreamingTileEntry leaf) ||
                !LeafTileHasBuildings(leaf))
                return;

            if (preferBuildingBundles && TryGetBuildingBundleRelativePath(leaf, out string bundleRel))
            {
                if (!TryReserveBuildingBundlePath(tileId, bundleRel))
                {
                    _buildingTilesLoading.Add(tileId);
                    return;
                }

                _buildingCancelledLoads.Remove(tileId);
                _buildingTilesLoading.Add(tileId);
                _buildingBundlePendingLoads.Add(new BuildingBundleRequest
                {
                    TileId = tileId,
                    PriorityDistance = priorityDistance,
                    BundleRel = bundleRel,
                });
                ProcessBuildingLoadPipeline();
                return;
            }

            ResolveBuildingTilePaths(tileId, out string glbPath, out string jsonPath, out string lod1Path);
            ResolveBuildingMetadataPaths(tileId, out _, out string metadataBytesPath);
            if (!File.Exists(glbPath))
            {
                Debug.LogWarning(
                    $"[ZGConnect.Realtime] Buildings GLB missing for tile '{tileId}': {glbPath}. " +
                    "Manifest marks hasOrthoRoofBuildings/hasFacadeBuildings but the packed file is absent. Re-pack the dataset.");
                return;
            }

            if (_manifest?.PackBake != null)
            {
                Debug.LogWarning(
                    $"[ZGConnect.Realtime] Tile '{tileId}': no pack-baked building bundle â€” loading raw GLB. " +
                    "Expect high batch counts and no colliders until you re-pack with Building Bundles enabled.");
            }

            _buildingCancelledLoads.Remove(tileId);
            _buildingTilesLoading.Add(tileId);
            _buildingPendingLoads.Add(new BuildingStreamRequest
            {
                TileId = tileId,
                PriorityDistance = priorityDistance,
                GlbPath = glbPath,
                JsonPath = jsonPath,
                Lod1Path = lod1Path,
                MetadataBytesPath = buildingMetadataSourceMode == BuildingMetadataSourceMode.JsonOnly
                    ? null
                    : metadataBytesPath,
            });
        }

        bool TryGetBuildingBundleRelativePath(StreamingTileEntry leaf, out string bundleRel)
        {
            bundleRel = null;
            if (!preferBuildingBundles || leaf == null || _manifest?.PackBake == null)
                return false;

            string styleKey = buildingStyle == RealtimeBuildingStyle.OrthoRoof
                ? StreamingPackBakeKeys.OrthoRoof
                : StreamingPackBakeKeys.Facade;

            if (leaf.BuildingBundlePaths == null ||
                !leaf.BuildingBundlePaths.TryGetValue(styleKey, out bundleRel) ||
                string.IsNullOrEmpty(bundleRel))
            {
                return false;
            }

            string fullPath = Path.Combine(_datasetRoot, bundleRel.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(fullPath);
        }

        bool TryGetVegetationBundleRelativePath(RuntimeTileRecord record, out string bundleRel)
        {
            bundleRel = null;
            if (!preferBakedVegetation || record == null || record.IsSupertile || record.Leaf == null)
                return false;

            bundleRel = record.Leaf.VegetationBundlePath;
            if (string.IsNullOrEmpty(bundleRel))
                return false;

            string fullPath = Path.Combine(_datasetRoot, bundleRel.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(fullPath);
        }

        void ResolveBuildingTilePaths(
            string tileId,
            out string glbPath,
            out string jsonPath,
            out string lod1Path)
        {
            bool useOrtho = buildingStyle == RealtimeBuildingStyle.OrthoRoof;
            string folder = BuildingMetadataPathUtility.GetJsonFolder(useOrtho);
            glbPath = Path.Combine(_datasetRoot,
                $"{folder}/buildings_{tileId}.glb".Replace('/', Path.DirectorySeparatorChar));
            jsonPath = Path.Combine(_datasetRoot,
                $"{folder}/{BuildingMetadataPathUtility.GetJsonFileName(tileId)}"
                    .Replace('/', Path.DirectorySeparatorChar));

            lod1Path = null;
            if (_manifest.GetLodStorageMode() == BuildingLodStorageMode.DualFile)
            {
                lod1Path = Path.Combine(_datasetRoot,
                    $"{folder}/buildings_{tileId}_lod1.glb".Replace('/', Path.DirectorySeparatorChar));
            }
        }

        void ResolveBuildingMetadataPaths(string tileId, out string jsonPath, out string bytesPath)
        {
            bool useOrtho = buildingStyle == RealtimeBuildingStyle.OrthoRoof;
            BuildingMetadataPathUtility.ResolveMetadataPaths(
                _datasetRoot, tileId, useOrtho, out jsonPath, out bytesPath);
        }

        void CancelBuildingLoad(string tileId)
        {
            _buildingCancelledLoads.Add(tileId);
            _buildingTilesLoading.Remove(tileId);
            _buildingPendingLoads.RemoveAll(p => p.TileId == tileId);
            _buildingBundlePendingLoads.RemoveAll(p => p.TileId == tileId);
            _buildingPrepareTasks.Remove(tileId);
            _buildingPrepared.Remove(tileId);
            ReleaseBuildingBundlePathForTile(tileId);

            if (_buildingCancelledLoads.Contains(tileId) &&
                _buildingTileRecords.TryGetValue(tileId, out RuntimeTileRecord cancelledRecord) &&
                cancelledRecord.BuildingsObject != null)
            {
                Destroy(cancelledRecord.BuildingsObject);
                cancelledRecord.BuildingsObject = null;
            }

        }

        void ClearBuildingLoadPipeline()
        {
            _buildingPendingLoads.Clear();
            _buildingBundlePendingLoads.Clear();
            _buildingPrepareTasks.Clear();
            _buildingPrepared.Clear();
            _buildingInstantiating.Clear();
            _buildingCancelledLoads.Clear();
            _buildingBundlePathsInFlight.Clear();
            _buildingBundlePathByTileId.Clear();
        }

        string ResolveBuildingBundleFullPath(string bundleRel)
        {
            if (string.IsNullOrEmpty(bundleRel) || string.IsNullOrEmpty(_datasetRoot))
                return string.Empty;

            return RuntimeAssetBundleCache.NormalizePath(
                Path.Combine(_datasetRoot, bundleRel.Replace('/', Path.DirectorySeparatorChar)));
        }

        bool TryReserveBuildingBundlePath(string tileId, string bundleRel)
        {
            string path = ResolveBuildingBundleFullPath(bundleRel);
            if (string.IsNullOrEmpty(path))
                return false;

            if (!_buildingBundlePathsInFlight.Add(path))
                return false;

            _buildingBundlePathByTileId[tileId] = path;
            return true;
        }

        void ReleaseBuildingBundlePathForTile(string tileId)
        {
            if (string.IsNullOrEmpty(tileId) ||
                !_buildingBundlePathByTileId.Remove(tileId, out string path))
            {
                return;
            }

            _buildingBundlePathsInFlight.Remove(path);
        }

        int CountBuildingPipelineInFlight() =>
            _buildingTilesLoading.Count;

        void ProcessBuildingLoadPipeline()
        {
            if (!streamBuildings)
                return;

            ProcessBuildingBundleQueue();
            ProcessBuildingPendingPrepares();
            ProcessBuildingInstantiateQueue();
        }

        void ProcessBuildingBundleQueue()
        {
            while (_buildingInstantiating.Count < maxConcurrentBuildingLoads &&
                   TrySelectHighestPriorityBuildingBundle(out int bestIndex, out BuildingBundleRequest request))
            {
                if (_buildingInstantiating.Contains(request.TileId))
                {
                    _buildingBundlePendingLoads.RemoveAt(bestIndex);
                    continue;
                }

                if (_buildingCancelledLoads.Contains(request.TileId) ||
                    _buildingTilesLoaded.ContainsKey(request.TileId) ||
                    _inactiveBuildings.ContainsKey(request.TileId))
                {
                    _buildingBundlePendingLoads.RemoveAt(bestIndex);
                    ReleaseBuildingBundlePathForTile(request.TileId);
                    continue;
                }

                _buildingBundlePendingLoads.RemoveAt(bestIndex);
                _buildingInstantiating.Add(request.TileId);
                StartCoroutine(LoadBuildingFromBundleCoroutine(request));
            }
        }

        bool TrySelectHighestPriorityBuildingBundle(out int bestIndex, out BuildingBundleRequest request)
        {
            bestIndex = -1;
            request = null;
            if (_buildingBundlePendingLoads.Count == 0)
                return false;

            bestIndex = 0;
            float bestDistance = _buildingBundlePendingLoads[0].PriorityDistance;
            for (int i = 1; i < _buildingBundlePendingLoads.Count; i++)
            {
                if (_buildingBundlePendingLoads[i].PriorityDistance < bestDistance)
                {
                    bestDistance = _buildingBundlePendingLoads[i].PriorityDistance;
                    bestIndex = i;
                }
            }

            request = _buildingBundlePendingLoads[bestIndex];
            return true;
        }

        IEnumerator LoadBuildingFromBundleCoroutine(BuildingBundleRequest request)
        {
            string tileId = request.TileId;
            try
            {
                if (_buildingCancelledLoads.Contains(tileId))
                    yield break;

                if (!_leafTilesById.TryGetValue(tileId, out StreamingTileEntry leaf) ||
                    !LeafTileHasBuildings(leaf))
                    yield break;

                EnsureBuildingsRoot();

                string fullPath = Path.Combine(
                    _datasetRoot, request.BundleRel.Replace('/', Path.DirectorySeparatorChar));
                if (_buildingTilesLoaded.ContainsKey(tileId) || _inactiveBuildings.ContainsKey(tileId))
                    yield break;

                var loadResult = new RuntimeBuildingBundleLoader.LoadResult();
                yield return RuntimeBuildingBundleLoader.LoadBuildingVisualPrefabAsync(fullPath, loadResult);

                if (_buildingCancelledLoads.Contains(tileId))
                {
                    if (!string.IsNullOrEmpty(loadResult.BundleFullPath))
                        RuntimeAssetBundleCache.Release(loadResult.BundleFullPath, unloadAllLoadedObjects: false);
                    yield break;
                }

                if (_buildingTilesLoaded.ContainsKey(tileId) || _inactiveBuildings.ContainsKey(tileId))
                {
                    if (!string.IsNullOrEmpty(loadResult.BundleFullPath))
                        RuntimeAssetBundleCache.Release(loadResult.BundleFullPath, unloadAllLoadedObjects: false);
                    yield break;
                }

                if (!loadResult.Success || loadResult.Prefab == null)
                {
                    Debug.LogWarning(
                        $"[ZGConnect.Realtime] Building bundle failed for '{tileId}' ({loadResult.Error}). " +
                        "Falling back to GLB path.");
                    ResolveBuildingTilePaths(tileId, out string glbPath, out string jsonPath, out string lod1Path);
                    ResolveBuildingMetadataPaths(tileId, out _, out string metadataBytesPath);
                    if (File.Exists(glbPath))
                    {
                        _buildingPendingLoads.Add(new BuildingStreamRequest
                        {
                            TileId = tileId,
                            PriorityDistance = request.PriorityDistance,
                            GlbPath = glbPath,
                            JsonPath = jsonPath,
                            Lod1Path = lod1Path,
                            MetadataBytesPath = buildingMetadataSourceMode == BuildingMetadataSourceMode.JsonOnly
                                ? null
                                : metadataBytesPath,
                        });
                    }
                    else
                    {
                        _buildingTilesLoading.Remove(tileId);
                    }

                    yield break;
                }

                var record = RuntimeTileRecord.FromLeaf(leaf, _manifest.TileSizeMeters);
                record.BuildingsObject = Instantiate(loadResult.Prefab, buildingsRoot);
                record.BuildingsObject.name = $"TileBuildings_{tileId}";
                RuntimeBuildingTilePostProcessor.StripEmptyBuildingShells(record.BuildingsObject.transform);
                var bundleHolder = record.BuildingsObject.GetComponent<RuntimeBuildingBundleHolder>() ??
                                   record.BuildingsObject.AddComponent<RuntimeBuildingBundleHolder>();
                bundleHolder.Bundle = loadResult.Bundle;
                bundleHolder.BundleFullPath = loadResult.BundleFullPath;
                bundleHolder.HasSplitPhysicsPrefab = loadResult.HasSplitPhysicsPrefab;
                bundleHolder.PhysicsPrefabAssetName = loadResult.PhysicsPrefabAssetName;

                RuntimeBuildingTileRuntimeState runtimeState =
                    record.BuildingsObject.GetComponent<RuntimeBuildingTileRuntimeState>();
                runtimeState?.ConfigureSplitPhysicsPrefab(loadResult.HasSplitPhysicsPrefab);

                SuppressStreamingVisualIfNeeded(record.BuildingsObject);
                _buildingTileRecords[tileId] = record;
                ActivateLoadedBuildingTile(record);
                CompleteBuildingTileLoad(record);

                if (loadResult.HasSplitPhysicsPrefab &&
                    (buildingColliderLoadDistanceMeters <= 0f ||
                     ShouldEnableBuildingCollidersForTile(tileId, _cam.position)))
                {
                    ApplyBuildingTileColliderState(record);
                }
            }
            finally
            {
                _buildingInstantiating.Remove(tileId);
                ReleaseBuildingBundlePathForTile(tileId);

                if (_buildingCancelledLoads.Contains(tileId) &&
                    _buildingTileRecords.TryGetValue(tileId, out RuntimeTileRecord cancelledRecord) &&
                    cancelledRecord.BuildingsObject != null)
                {
                    Destroy(cancelledRecord.BuildingsObject);
                    cancelledRecord.BuildingsObject = null;
                }

                ProcessBuildingLoadPipeline();
            }
        }

        void ProcessBuildingPendingPrepares()
        {
            while (_buildingPrepareTasks.Count < maxConcurrentBuildingPrepares &&
                   TryDequeueHighestPriorityBuildingPending(out BuildingStreamRequest request))
            {
                if (_buildingCancelledLoads.Contains(request.TileId))
                    continue;

                if (!useBackgroundBuildingPrepare)
                {
                    _buildingPrepared[request.TileId] = new RuntimeBuildingPrepareResult
                    {
                        Success = true,
                        TileId = request.TileId,
                        GlbPath = request.GlbPath,
                        JsonPath = request.JsonPath,
                        Lod1GlbPath = request.Lod1Path,
                        MetadataBytesPath = request.MetadataBytesPath,
                    };
                    continue;
                }

                Task<RuntimeBuildingPrepareResult> task = RuntimeBuildingPreparer.PrepareAsync(
                    request.TileId,
                    request.GlbPath,
                    request.JsonPath,
                    request.Lod1Path,
                    request.MetadataBytesPath);
                _buildingPrepareTasks[request.TileId] = task;
                StartCoroutine(WaitForBuildingPrepareCoroutine(request.TileId, task));
            }
        }

        IEnumerator WaitForBuildingPrepareCoroutine(string tileId, Task<RuntimeBuildingPrepareResult> task)
        {
            while (!task.IsCompleted)
                yield return null;

            _buildingPrepareTasks.Remove(tileId);

            if (_buildingCancelledLoads.Contains(tileId))
            {
                ProcessBuildingLoadPipeline();
                yield break;
            }

            RuntimeBuildingPrepareResult result = task.Result;
            if (!result.Success)
            {
                Debug.LogWarning(
                    $"[ZGConnect.Realtime] Building prepare failed for '{tileId}': {result.Error}");
                _buildingTilesLoading.Remove(tileId);
                ProcessBuildingLoadPipeline();
                yield break;
            }

            _buildingPrepared[tileId] = result;
            ProcessBuildingLoadPipeline();
        }

        void ProcessBuildingInstantiateQueue()
        {
            while (_buildingInstantiating.Count < maxConcurrentBuildingLoads &&
                   TryDequeueHighestPriorityBuildingPrepared(out string tileId, out RuntimeBuildingPrepareResult prepared))
            {
                if (_buildingCancelledLoads.Contains(tileId))
                {
                    _buildingPrepared.Remove(tileId);
                    continue;
                }

                _buildingInstantiating.Add(tileId);
                StartCoroutine(InstantiateBuildingTileCoroutine(tileId, prepared));
            }
        }

        IEnumerator InstantiateBuildingTileCoroutine(string tileId, RuntimeBuildingPrepareResult prepared)
        {
            try
            {
                if (_buildingCancelledLoads.Contains(tileId))
                    yield break;

                if (!_leafTilesById.TryGetValue(tileId, out StreamingTileEntry leaf) ||
                    !LeafTileHasBuildings(leaf))
                    yield break;

                EnsureBuildingsRoot();

                var record = RuntimeTileRecord.FromLeaf(leaf, _manifest.TileSizeMeters);
                Task<RuntimeGlbInstantiateResult> instantiateTask = RuntimeGlbLoader.InstantiatePreparedGlbAsync(
                    prepared,
                    buildingsRoot,
                    tileId,
                    _manifest.GetLodStorageMode());

                while (!instantiateTask.IsCompleted)
                    yield return null;

                RuntimeGlbInstantiateResult instantiateResult = instantiateTask.Result;
                if (instantiateResult?.Root == null)
                {
                    Debug.LogWarning(
                        $"[ZGConnect.Realtime] Buildings GLB failed to load for tile '{tileId}': {prepared.GlbPath}");
                    _buildingTilesLoading.Remove(tileId);
                    yield break;
                }

                yield return RuntimeGlbPostProcess.CompleteSetupSpread(
                    instantiateResult,
                    _buildingLodController,
                    glbPostProcessBuildingsPerFrame,
                    glbPostProcessMsBudget);

                record.BuildingsObject = instantiateResult.Root;
                record.BuildingsMetadata = instantiateResult.Metadata;
                SuppressStreamingVisualIfNeeded(record.BuildingsObject);
                _buildingTileRecords[tileId] = record;
                ActivateLoadedBuildingTile(record, warnIfNotPackBaked: true);
                CompleteBuildingTileLoad(record);
            }
            finally
            {
                _buildingInstantiating.Remove(tileId);
                _buildingPrepared.Remove(tileId);

                if (_buildingCancelledLoads.Contains(tileId) &&
                    _buildingTileRecords.TryGetValue(tileId, out RuntimeTileRecord cancelledRecord) &&
                    cancelledRecord.BuildingsObject != null)
                {
                    Destroy(cancelledRecord.BuildingsObject);
                    cancelledRecord.BuildingsObject = null;
                }

                ProcessBuildingLoadPipeline();
            }
        }

        void ActivateLoadedBuildingTile(RuntimeTileRecord record, bool warnIfNotPackBaked = false)
        {
            if (record?.BuildingsObject == null)
                return;

            RuntimeBuildingTileRuntimeState state =
                record.BuildingsObject.GetComponent<RuntimeBuildingTileRuntimeState>();
            record.BuildingsObject.SetActive(true);
            state?.EnsureCombinedRenderShown();
            ApplyCombinedRenderMaterials(record);
            ApplyBuildingTileColliderState(record);
            SuppressStreamingVisualIfNeeded(record.BuildingsObject);

            if (warnIfNotPackBaked &&
                (state == null || !state.WasBakedAtPack(_manifest?.PackBake)))
            {
                Debug.LogWarning(
                    $"[ZGConnect.Realtime] Tile '{record.Key}' loaded without pack-baked buildings. " +
                    "Repack with building bundle bake enabled for materials, combined meshes, and colliders.");
            }
        }

        void ApplyCombinedRenderMaterials(RuntimeTileRecord record)
        {
            if (buildingSurfaceSettings == null || record?.BuildingsObject == null)
                return;

            BuildingMaterialApplyStyle style = buildingStyle == RealtimeBuildingStyle.OrthoRoof
                ? BuildingMaterialApplyStyle.RoofOrthophoto
                : BuildingMaterialApplyStyle.FacadeAndRoofVariants;

            Material roofTemplate = roofOrthophotoMaterialTemplate
                ?? buildingSurfaceSettings.GetFlatRoofMaterial(BuildingCategory.House, 0);

            RuntimeCombinedRenderMaterialApplier.ApplyTile(
                record.BuildingsObject.transform,
                record.Key,
                BuildCityTileRecordForBuildings(record),
                buildingSurfaceSettings,
                style,
                activeBasemapId,
                roofTemplate,
                _buildingRoofMaterialCache);
        }

        CityTileRecord BuildCityTileRecordForBuildings(RuntimeTileRecord buildingRecord)
        {
            var city = new CityTileRecord
            {
                tileId = buildingRecord.Key,
                left = buildingRecord.Left,
                bottom = buildingRecord.Bottom,
                unityPosition = buildingRecord.UnityPosition,
            };

            if (buildingRecord.Leaf != null)
            {
                city.right = buildingRecord.Leaf.Right;
                city.top = buildingRecord.Leaf.Top;
            }

            RuntimeTileRecord orthoSource = buildingRecord;
            if ((orthoSource.OrthoTextures == null || orthoSource.OrthoTextures.Count == 0) &&
                _loaded.TryGetValue(buildingRecord.Key, out RuntimeTileRecord terrainRecord))
            {
                orthoSource = terrainRecord;
            }

            string basemapId = string.IsNullOrEmpty(activeBasemapId) ? "ortho" : activeBasemapId;
            if (orthoSource.OrthoTextures != null &&
                orthoSource.OrthoTextures.TryGetValue(basemapId, out Texture2D tex) &&
                tex != null)
            {
                var entry = new BasemapLayerEntry
                {
                    basemapId = basemapId,
                    texture = tex,
                };
                if (orthoSource.OrthoLayers != null &&
                    orthoSource.OrthoLayers.TryGetValue(basemapId, out TerrainLayer layer))
                {
                    entry.terrainLayer = layer;
                }

                city.basemapLayers.Add(entry);
            }

            return city;
        }

        void RefreshCombinedRenderMaterialsOnLoadedBuildings()
        {
            BuildingSharedMaterialApplier.ReleaseCachedRoofMaterials(_buildingRoofMaterialCache);

            foreach (KeyValuePair<string, RuntimeTileRecord> kvp in _buildingTilesLoaded)
                ApplyCombinedRenderMaterials(kvp.Value);

            foreach (KeyValuePair<string, GameObject> kvp in _inactiveBuildings)
            {
                if (kvp.Value == null ||
                    !_buildingTileRecords.TryGetValue(kvp.Key, out RuntimeTileRecord record))
                    continue;

                record.BuildingsObject = kvp.Value;
                ApplyCombinedRenderMaterials(record);
            }
        }

        void CompleteBuildingTileLoad(RuntimeTileRecord record)
        {
            if (record?.BuildingsObject == null)
                return;

            RuntimeBuildingTileRuntimeState state =
                record.BuildingsObject.GetComponent<RuntimeBuildingTileRuntimeState>();
            state?.EnsureCombinedRenderShown();

            _buildingCancelledLoads.Remove(record.Key);
            _buildingTilesLoading.Remove(record.Key);
            _buildingTilesLoaded[record.Key] = record;
            BuildingTileLoaded?.Invoke(record);
        }

        void ReactivateBuildingTile(string tileId)
        {
            if (!_inactiveBuildings.TryGetValue(tileId, out GameObject go) || go == null)
                return;

            _inactiveBuildings.Remove(tileId);
            _buildingDeactivationTimes.Remove(tileId);

            if (!_buildingTileRecords.TryGetValue(tileId, out RuntimeTileRecord record))
            {
                if (!_leafTilesById.TryGetValue(tileId, out StreamingTileEntry leaf))
                    return;

                record = RuntimeTileRecord.FromLeaf(leaf, _manifest.TileSizeMeters);
            }

            record.BuildingsObject = go;
            ActivateLoadedBuildingTile(record);
            _buildingTilesLoaded[tileId] = record;
            BuildingTileLoaded?.Invoke(record);
        }

        bool TryDequeueHighestPriorityBuildingPending(out BuildingStreamRequest request)
        {
            request = null;
            if (_buildingPendingLoads.Count == 0)
                return false;

            int bestIndex = 0;
            float bestDistance = _buildingPendingLoads[0].PriorityDistance;
            for (int i = 1; i < _buildingPendingLoads.Count; i++)
            {
                if (_buildingPendingLoads[i].PriorityDistance < bestDistance)
                {
                    bestDistance = _buildingPendingLoads[i].PriorityDistance;
                    bestIndex = i;
                }
            }

            request = _buildingPendingLoads[bestIndex];
            _buildingPendingLoads.RemoveAt(bestIndex);
            return true;
        }

        bool TryDequeueHighestPriorityBuildingPrepared(
            out string tileId,
            out RuntimeBuildingPrepareResult prepared)
        {
            tileId = null;
            prepared = null;
            if (_buildingPrepared.Count == 0)
                return false;

            string bestTileId = null;
            float bestDistance = float.MaxValue;
            foreach (KeyValuePair<string, RuntimeBuildingPrepareResult> kvp in _buildingPrepared)
            {
                float dist = GetBuildingPreparedPriorityDistance(kvp.Key);
                if (dist < bestDistance)
                {
                    bestDistance = dist;
                    bestTileId = kvp.Key;
                }
            }

            if (bestTileId == null)
                return false;

            tileId = bestTileId;
            prepared = _buildingPrepared[bestTileId];
            return true;
        }

        float GetBuildingPreparedPriorityDistance(string tileId)
        {
            if (_cam == null || !_leafTilesById.TryGetValue(tileId, out StreamingTileEntry leaf))
                return float.MaxValue;

            return GetLeafBoundaryDistance(
                _cam.position, leaf, _manifest.TileSizeMeters, GetBuildingDistanceMetric());
        }

        void UnloadBuildingTile(string tileId)
        {
            if (!_buildingTilesLoaded.TryGetValue(tileId, out RuntimeTileRecord record))
                return;

            if (record.BuildingsObject != null)
            {
                record.BuildingsObject.SetActive(false);
                _inactiveBuildings[tileId] = record.BuildingsObject;
                _buildingDeactivationTimes[tileId] = Time.time;
                record.BuildingsObject = null;
                EnforceInactiveBuildingCap();
            }

            ReleaseBuildingTileGpuTextures(record);
            _buildingTilesLoaded.Remove(tileId);
            _buildingTileRecords[tileId] = record;
            BuildingTileUnloaded?.Invoke(record);
        }

        void UnloadAllBuildingTiles(bool destroyImmediately)
        {
            foreach (string tileId in _buildingTilesLoaded.Keys.ToList())
            {
                if (_buildingTilesLoaded.TryGetValue(tileId, out RuntimeTileRecord record))
                {
                    if (record.BuildingsObject != null)
                    {
                        if (destroyImmediately)
                        {
                            Destroy(record.BuildingsObject);
                            BuildingTileUnloaded?.Invoke(record);
                        }
                        else
                        {
                            UnloadBuildingTile(tileId);
                        }
                    }
                }
            }

            _buildingTilesLoaded.Clear();
            _buildingTilesLoading.Clear();
            ClearBuildingLoadPipeline();

            if (destroyImmediately)
            {
                foreach (string tileId in _inactiveBuildings.Keys.ToList())
                    DestroyInactiveBuilding(tileId);

                foreach (RuntimeTileRecord record in _buildingTileRecords.Values)
                    ReleaseBuildingTileGpuTextures(record);

                _buildingTileRecords.Clear();
            }
        }

        void ReleaseBuildingTileGpuTextures(RuntimeTileRecord record)
        {
            if (record == null)
                return;

            foreach (Texture2D tex in record.GpuTextures)
            {
                if (tex != null)
                    Destroy(tex);
            }

            record.GpuTextures.Clear();
            record.OrthoTextures.Clear();
        }

        void CacheEditorPopulatedTerrains()
        {
            _editorPopulatedTerrainByKey.Clear();

            RealtimeStreamingEditorPopulatedTile[] tiles = FindObjectsByType<RealtimeStreamingEditorPopulatedTile>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            foreach (RealtimeStreamingEditorPopulatedTile tile in tiles)
            {
                if (tile == null ||
                    tile.PopulatedContentType != RealtimeStreamingEditorPopulatedTile.ContentType.Terrain)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(tile.TileKey))
                    continue;

                if (tile.GetComponent<Terrain>() == null)
                    continue;

                if (_editorPopulatedTerrainByKey.ContainsKey(tile.TileKey))
                {
                    Debug.LogWarning(
                        $"[ZGConnect.Realtime] Duplicate editor-populated terrain for tile '{tile.TileKey}'. " +
                        "Using the first marker found.");
                    continue;
                }

                _editorPopulatedTerrainByKey[tile.TileKey] = tile.gameObject;
            }

            if (!streamTerrain && streamVegetation)
            {
                if (_editorPopulatedTerrainByKey.Count == 0)
                {
                    Debug.LogWarning(
                        "[ZGConnect.Realtime] Stream Terrain is off but no editor-populated terrain tiles " +
                        "were found in the scene. Use Populate Scene in the editor, or enable Stream Terrain.");
                }
                else
                {
                    Debug.Log(
                        $"[ZGConnect.Realtime] Vegetation will attach to " +
                        $"{_editorPopulatedTerrainByKey.Count} editor-populated terrain tile(s).");
                }
            }
        }

        /// <summary>
        /// Terrain mesh used for vegetation placement. Streamed terrain when Stream Terrain is on;
        /// otherwise editor-populated 1×1 tiles matched by tile key.
        /// </summary>
        GameObject ResolveVegetationTerrainObject(RuntimeTileRecord record)
        {
            if (record == null)
                return null;

            if (streamTerrain)
                return record.TerrainObject;

            if (record.IsSupertile)
                return null;

            return _editorPopulatedTerrainByKey.TryGetValue(record.Key, out GameObject terrain)
                ? terrain
                : null;
        }

        bool TryGetVegetationTerrain(RuntimeTileRecord record, out Terrain terrain)
        {
            terrain = null;
            GameObject terrainObject = ResolveVegetationTerrainObject(record);
            if (terrainObject == null)
                return false;

            terrain = terrainObject.GetComponent<Terrain>();
            return terrain != null;
        }

        bool UsesEditorPopulatedVegetationTerrain() => !streamTerrain;

        /// <summary>
        /// Editor-populated terrains keep their VegetationChunkRenderer across streaming record unload/reload.
        /// </summary>
        bool TryRebindEditorPopulatedVegetation(RuntimeTileRecord record)
        {
            if (!UsesEditorPopulatedVegetationTerrain() || record == null)
                return false;

            GameObject terrainObject = ResolveVegetationTerrainObject(record);
            if (terrainObject == null)
                return false;

            VegetationChunkRenderer renderer = terrainObject.GetComponent<VegetationChunkRenderer>();
            if (renderer == null || renderer.TotalInstances <= 0)
                return false;

            record.VegetationRenderer = renderer;
            return true;
        }

        bool ShouldLoadVegetation(RuntimeTileRecord record)
        {
            if (!streamVegetation || vegetationRuleSet == null)
                return false;

            if (!streamTerrain && record.IsSupertile)
                return false;

            if (!record.IsSupertile)
                return record.Leaf?.HasVegetationMask == true;

            if (!streamVegetationOn2x2Supertiles || record.HlodFactor != 2)
                return false;

            return RuntimeVegetationMaskLoader.SupertileHasVegetationMask(
                record.Supertile, _manifest, _datasetRoot);
        }

        void QueueVegetationLoad(RuntimeTileRecord record)
        {
            if (record == null || ResolveVegetationTerrainObject(record) == null)
                return;

            if (UsesEditorPopulatedVegetationTerrain() && TryRebindEditorPopulatedVegetation(record))
                return;

            if (IsVegetationQueued(record.Key))
                return;

            _vegetationLoadQueue.Enqueue(record);
            TryStartNextVegetationSpread();
        }

        void PurgeVegetationQueue(string tileKey)
        {
            if (string.IsNullOrEmpty(tileKey) || _vegetationLoadQueue.Count == 0)
                return;

            int count = _vegetationLoadQueue.Count;
            for (int i = 0; i < count; i++)
            {
                RuntimeTileRecord queued = _vegetationLoadQueue.Dequeue();
                if (queued.Key != tileKey)
                    _vegetationLoadQueue.Enqueue(queued);
            }
        }

        bool IsVegetationLoadTargetValid(RuntimeTileRecord record)
        {
            if (record == null || ResolveVegetationTerrainObject(record) == null)
                return false;

            if (UsesEditorPopulatedVegetationTerrain())
                return true;

            return _loaded.ContainsKey(record.Key) || _loading.ContainsKey(record.Key);
        }

        void EnsureVegetationQueuedForLoadedTiles()
        {
            if (!streamVegetation || vegetationRuleSet == null)
                return;

            foreach (RuntimeTileRecord record in _loaded.Values)
            {
                if (!ShouldLoadVegetation(record))
                    continue;

                if (!record.IsSupertile && !ShouldShowVegetationTerrain(record))
                    continue;

                if (UsesEditorPopulatedVegetationTerrain())
                    TryRebindEditorPopulatedVegetation(record);

                if (record.VegetationRenderer != null && record.VegetationRenderer.TotalInstances > 0)
                    continue;

                if (IsVegetationQueued(record.Key))
                    continue;

                QueueVegetationLoad(record);
            }
        }

        bool IsVegetationQueued(string tileKey)
        {
            if (string.IsNullOrEmpty(tileKey))
                return false;

            foreach (RuntimeTileRecord queued in _vegetationLoadQueue)
            {
                if (queued.Key == tileKey)
                    return true;
            }

            return _vegetationSpreadActive &&
                   !string.IsNullOrEmpty(_vegetationSpreadTileKey) &&
                   _vegetationSpreadTileKey == tileKey;
        }

        void TryStartNextVegetationSpread()
        {
            if (_vegetationSpreadActive || _vegetationLoadQueue.Count == 0)
                return;

            StartCoroutine(LoadVegetationSpreadCoroutine(_vegetationLoadQueue.Dequeue()));
        }

        void AttachVegetationRenderer(
            RuntimeTileRecord record,
            VegetationChunk chunk,
            VegetationPrototype[] prototypes)
        {
            GameObject terrainObject = ResolveVegetationTerrainObject(record);
            if (terrainObject == null || chunk == null)
                return;

            VegetationChunkRenderer renderer = terrainObject.GetComponent<VegetationChunkRenderer>();
            if (renderer == null)
                renderer = terrainObject.AddComponent<VegetationChunkRenderer>();

            renderer.SetChunk(
                chunk,
                prototypes,
                GetVegetationSpawnAnimationSettings(),
                playSpawnAnimation: Application.isPlaying,
                GetVegetationDrawDistanceSettings(),
                _cam,
                vegetationFrustumCullChunks,
                vegetationFrustumCullInstances);

            record.VegetationRenderer = renderer;
            record.VegetationSettingsFingerprint = GetVegetationSettingsFingerprint();
            SuppressStreamingVisualIfNeeded(terrainObject);
        }

        IEnumerator LoadVegetationSpreadCoroutine(RuntimeTileRecord record)
        {
            _vegetationSpreadActive = true;
            _vegetationSpreadTileKey = record?.Key;

            try
            {
                if (!IsVegetationLoadTargetValid(record))
                    yield break;

                if (UsesEditorPopulatedVegetationTerrain() && TryRebindEditorPopulatedVegetation(record))
                    yield break;

                DestroyVegetation(record);
                if (!IsVegetationLoadTargetValid(record))
                    yield break;

                yield return null;

                if (!IsVegetationLoadTargetValid(record))
                    yield break;

                if (TryGetVegetationBundleRelativePath(record, out string vegBundleRel))
                {
                    string fullPath = Path.Combine(
                        _datasetRoot, vegBundleRel.Replace('/', Path.DirectorySeparatorChar));
                    var loadResult = new RuntimeVegetationBundleLoader.LoadResult();
                    yield return RuntimeVegetationBundleLoader.LoadVegetationAssetAsync(fullPath, loadResult);

                    if (!IsVegetationLoadTargetValid(record))
                        yield break;

                    if (loadResult.Success && loadResult.ChunkAsset != null)
                    {
                        VegetationPrototype[] prototypes = ResolveVegetationPrototypes();
                        VegetationChunk chunk = loadResult.ChunkAsset.ToRuntimeChunk(prototypes);
                        if (VegetationChunkHasRenderableContent(chunk))
                        {
                            AttachVegetationRenderer(record, chunk, prototypes);
                            loadResult.Bundle?.Unload(false);
                            yield break;
                        }

                        Debug.LogWarning(
                            $"[ZGConnect.Realtime] Baked vegetation for '{record.Key}' has no renderable " +
                            "instances (prototype index mismatch?). Falling back to runtime generation.");
                    }

                    Debug.LogWarning(
                        $"[ZGConnect.Realtime] Baked vegetation bundle failed for '{record.Key}' " +
                        $"({loadResult.Error}). Falling back to runtime generation.");
                }

                Texture2D mask = record.IsSupertile
                    ? RuntimeVegetationMaskLoader.ComposeSupertileVegetationMask(
                        record.Supertile, _manifest, _datasetRoot)
                    : RuntimeVegetationMaskLoader.LoadMask(GetVegetationMaskPath(record.Key));

                yield return null;

                if (!IsVegetationLoadTargetValid(record))
                    yield break;

                if (mask == null)
                    yield break;

                record.VegetationMask = mask;
                record.GpuTextures.Add(record.VegetationMask);

                float halfY = (_manifest.MaxHeight - _manifest.MinHeight) * 0.5f;
                float midY = _manifest.MinHeight + halfY;
                var bounds = new Bounds(
                    new Vector3(
                        record.UnityPosition.x + record.TileSizeMeters * 0.5f,
                        midY,
                        record.UnityPosition.z + record.TileSizeMeters * 0.5f),
                    new Vector3(record.TileSizeMeters, halfY * 2f, record.TileSizeMeters));

                if (!TryGetVegetationTerrain(record, out Terrain terrain))
                    yield break;

                VegetationPrototype[] runtimePrototypes = ResolveVegetationPrototypes();
                var generator = new VegetationInstanceGenerator();
                VegetationChunk chunkRuntime = null;

                yield return generator.GenerateChunkSpread(
                    record.Key,
                    bounds,
                    record.VegetationMask,
                    vegetationRuleSet,
                    runtimePrototypes,
                    terrain,
                    vegetationRulesPerFrame,
                    vegetationSpreadMsBudget,
                    generated => chunkRuntime = generated);

                if (!IsVegetationLoadTargetValid(record))
                    yield break;

                if (!VegetationChunkHasRenderableContent(chunkRuntime))
                {
                    if (logStreamingDiagnostics)
                    {
                        Debug.LogWarning(
                            $"[ZGConnect.Realtime] Vegetation mask loaded for '{record.Key}' but no " +
                            "instances were generated. Check Vegetation Rule Set, mask channels, and prototypes.");
                    }

                    yield break;
                }

                AttachVegetationRenderer(record, chunkRuntime, runtimePrototypes);
            }
            finally
            {
                _vegetationSpreadActive = false;
                _vegetationSpreadTileKey = null;
                TryStartNextVegetationSpread();
            }
        }

        string GetVegetationMaskPath(string tileKey) =>
            Path.Combine(_datasetRoot,
                $"vegetation_masks/{tileKey}_vegetation.png".Replace('/', Path.DirectorySeparatorChar));

        void ApplyBasemap(RuntimeTileRecord record, byte[] preloadedOrthoPng = null)
        {
            if (string.IsNullOrEmpty(activeBasemapId))
                return;

            StreamingBasemapEntry bm = _manifest.AvailableBasemaps?
                .Find(b => b.Id == activeBasemapId);
            if (bm == null)
                return;

            if (bm.GetBasemapType() == BasemapType.Ortho && streamOrthoBasemaps)
                ApplyOrthoBasemap(record, activeBasemapId, preloadedOrthoPng);
            else if (bm.GetBasemapType() == BasemapType.Tiled && streamTiledBasemaps && !record.IsSupertile)
                ApplyTiledBasemap(record, activeBasemapId);
        }

        void ApplyOrthoBasemap(RuntimeTileRecord record, string basemapId, byte[] preloadedPng = null)
        {
            Texture2D tex = null;
            if (preloadedPng != null && preloadedPng.Length > 0)
            {
                tex = _basemapFactory.LoadOrthoPngTextureFromBytes(preloadedPng);
            }
            else
            {
                string diskPath = ResolveOrthoDiskPath(record, basemapId);
                if (!string.IsNullOrEmpty(diskPath))
                {
                    byte[] bytes = File.ReadAllBytes(diskPath);
                    tex = _basemapFactory.LoadOrthoPngTextureFromBytes(bytes);
                }
                else
                {
                    Dictionary<string, string> paths = record.IsSupertile
                        ? record.Supertile.OrthoPaths
                        : record.Leaf.OrthoPaths;
                    if (paths != null && paths.TryGetValue(basemapId, out string rel))
                        tex = _basemapFactory.LoadOrthoPngTexture(rel);
                }
            }

            if (tex == null)
                return;

            if (debugBasemapBorders && debugBasemapBorderPixels > 0)
            {
                Texture2D bordered = RuntimeBasemapFactory.ApplyBasemapDebugBorder(
                    tex, debugBasemapBorderColor, debugBasemapBorderPixels);
                if (bordered != tex)
                {
                    record.GpuTextures.Add(tex);
                    tex = bordered;
                }
            }

            TerrainLayer layer = _basemapFactory.CreateOrthoLayer(
                tex, $"{basemapId}_{record.Key}", record.TileSizeMeters);
            record.OrthoTextures[basemapId] = tex;
            record.OrthoLayers[basemapId] = layer;
            record.GpuTextures.Add(tex);
            record.GpuTerrainLayers.Add(layer);
            record.ActiveBasemapId = basemapId;
            _basemapFactory.ApplyOrthoBasemap(record.TerrainData, layer);

            if (record.TerrainObject != null)
            {
                Terrain terrain = record.TerrainObject.GetComponent<Terrain>();
                if (terrain != null)
                {
                    RuntimeBasemapFactory.ApplyMatteTerrainShading(terrain);
                    ApplyBasemapVisuals(record, terrain);
                    terrain.Flush();
                }
            }

            if (buildingSurfaceSettings != null &&
                buildingStyle == RealtimeBuildingStyle.OrthoRoof &&
                _buildingTilesLoaded.TryGetValue(record.Key, out RuntimeTileRecord buildingRecord))
            {
                ApplyCombinedRenderMaterials(buildingRecord);
            }
        }

        void ApplyTiledBasemap(RuntimeTileRecord record, string basemapId)
        {
            if (record.Leaf?.TiledSplatPaths == null ||
                !record.Leaf.TiledSplatPaths.TryGetValue(basemapId, out List<string> splats))
                return;

            string metaRel = $"basemaps/{basemapId}/metadata.json";
            TerrainLayer[] shared = _basemapFactory.GetOrCreateSharedTiledLayers(basemapId, metaRel);
            if (shared == null)
                return;

            _basemapFactory.ApplyTiledSplatmaps(record.TerrainData, shared, splats);
            record.ActiveBasemapId = basemapId;

            if (record.TerrainObject != null)
            {
                Terrain terrain = record.TerrainObject.GetComponent<Terrain>();
                if (terrain != null)
                {
                    RuntimeBasemapFactory.ApplyMatteTerrainShading(terrain);
                    ApplyBasemapVisuals(record, terrain);
                    terrain.Flush();
                }
            }
        }

        void ApplyBasemapSwitchToLoadedTiles()
        {
            foreach (RuntimeTileRecord record in _loaded.Values)
            {
                if (record.TerrainObject == null && record.TerrainData == null)
                    continue;

                bool wantsBundle = TryGetTerrainBundleRelativePath(record, out _);
                bool hasBundleTerrain = record.LoadedFromTerrainBundle;

                if (wantsBundle)
                {
                    if (hasBundleTerrain && record.LoadedTerrainBundleBasemapId == activeBasemapId)
                        continue;

                    StartCoroutine(SwitchTerrainBundleBasemapCoroutine(record));
                    continue;
                }

                if (hasBundleTerrain)
                {
                    StartCoroutine(SwitchTerrainBundleBasemapCoroutine(record));
                    continue;
                }

                ApplyBasemap(record);
            }

        }

        IEnumerator SwitchTerrainBundleBasemapCoroutine(RuntimeTileRecord record)
        {
            UnloadTerrainOnly(record);
            yield return null;

            bool terrainLoaded = false;
            string bundleError = null;
            yield return LoadTerrainFromBundlesCoroutine(record, result =>
            {
                terrainLoaded = result.Success;
                bundleError = result.Error;
            });

            if (!terrainLoaded)
            {
                if (!string.IsNullOrEmpty(bundleError))
                {
                    Debug.LogWarning(
                        $"[ZGConnect.Realtime] Bundle switch failed for '{record.Key}' ({bundleError}).");
                }

                if (!terrainBundleOnlyMode && HeightmapFileExists(record))
                    terrainLoaded = TryLoadTerrain(record);
            }

            if (!terrainLoaded)
                yield break;

            RewireNeighbors();

            if (ShouldLoadVegetation(record) &&
                (record.IsSupertile || ShouldShowVegetationTerrain(record)))
            {
                QueueVegetationLoad(record);
            }

        }

        void UnloadTerrainOnly(RuntimeTileRecord record)
        {
            DestroyVegetation(record);

            if (record.TerrainObject != null)
            {
                _terrainByCoord.Remove($"{record.Left}_{record.Bottom}");
                Destroy(record.TerrainObject);
                record.TerrainObject = null;
            }

            if (record.LoadedFromTerrainBundle)
            {
                record.TerrainData = null;
                record.BundleSourceTerrainLayers = null;
                record.LoadedTerrainBundleBasemapId = null;
                ReleaseTerrainBundle(record, record.TerrainBundleFullPath, unloadAllLoadedObjects: true);
                record.LoadedFromTerrainBundle = false;
                DestroyBasemapVisualClones(record);
            }
            else
            {
                if (record.TerrainData != null)
                {
                    Destroy(record.TerrainData);
                    record.TerrainData = null;
                }

                foreach (Texture2D tex in record.GpuTextures)
                {
                    if (tex != null)
                        Destroy(tex);
                }
                record.GpuTextures.Clear();

                foreach (TerrainLayer layer in record.GpuTerrainLayers)
                {
                    if (layer != null)
                        Destroy(layer);
                }
                record.GpuTerrainLayers.Clear();
                record.OrthoTextures.Clear();
                record.OrthoLayers.Clear();
            }

            record.ActiveBasemapId = null;
        }

        void CacheBasemapVisualSettings()
        {
            _lastTintBasemapsByHlod = tintBasemapsByHlod;
            _lastHlod1x1BasemapTint = hlod1x1BasemapTint;
            _lastHlod2x2BasemapTint = hlod2x2BasemapTint;
            _lastHlod4x4BasemapTint = hlod4x4BasemapTint;
            _lastDebugBasemapBorders = debugBasemapBorders;
            _lastDebugBasemapBorderColor = debugBasemapBorderColor;
            _lastDebugBasemapBorderPixels = debugBasemapBorderPixels;
        }

        bool BasemapVisualSettingsChanged() =>
            tintBasemapsByHlod != _lastTintBasemapsByHlod ||
            hlod1x1BasemapTint != _lastHlod1x1BasemapTint ||
            hlod2x2BasemapTint != _lastHlod2x2BasemapTint ||
            hlod4x4BasemapTint != _lastHlod4x4BasemapTint ||
            debugBasemapBorders != _lastDebugBasemapBorders ||
            debugBasemapBorderColor != _lastDebugBasemapBorderColor ||
            debugBasemapBorderPixels != _lastDebugBasemapBorderPixels;

        Color ResolveHlodBasemapTint(int hlodFactor)
        {
            if (!tintBasemapsByHlod)
                return Color.white;

            return hlodFactor switch
            {
                2 => hlod2x2BasemapTint,
                4 => hlod4x4BasemapTint,
                _ => hlod1x1BasemapTint,
            };
        }

        RuntimeBasemapFactory.BasemapVisualOptions BuildBasemapVisualOptions(RuntimeTileRecord record) =>
            new RuntimeBasemapFactory.BasemapVisualOptions(
                tintBasemapsByHlod,
                ResolveHlodBasemapTint(record.HlodFactor),
                debugBasemapBorders,
                debugBasemapBorderColor,
                debugBasemapBorderPixels);

        bool NeedsBundleBasemapVisualClone(RuntimeTileRecord record, RuntimeBasemapFactory.BasemapVisualOptions options)
        {
            if (!record.LoadedFromTerrainBundle)
                return false;

            return options.TintByHlod || (options.DebugBorder && options.BorderPixels > 0);
        }

        void ApplyBasemapVisuals(RuntimeTileRecord record, Terrain terrain)
        {
            if (terrain == null || record.TerrainData == null)
                return;

            RuntimeBasemapFactory.BasemapVisualOptions options = BuildBasemapVisualOptions(record);

            if (record.LoadedFromTerrainBundle &&
                record.TerrainData.terrainLayers != null &&
                record.TerrainData.terrainLayers.Length > 0 &&
                record.BundleSourceTerrainLayers == null)
            {
                record.BundleSourceTerrainLayers = record.TerrainData.terrainLayers;
            }

            bool useBundleClonePath = NeedsBundleBasemapVisualClone(record, options) &&
                                      record.BundleSourceTerrainLayers != null &&
                                      record.BundleSourceTerrainLayers.Length > 0;

            if (useBundleClonePath)
            {
                DestroyBasemapVisualClones(record);
                TerrainLayer[] visualLayers = RuntimeBasemapFactory.CloneTerrainLayersWithVisuals(
                    record.BundleSourceTerrainLayers,
                    options,
                    record.GpuTerrainLayers,
                    record.GpuTextures);
                record.TerrainData.terrainLayers = visualLayers;
            }
            else if (options.TintByHlod)
            {
                RuntimeBasemapFactory.ApplyHlodTintToTerrainLayers(
                    record.TerrainData, options.HlodTint, true);
            }
            else
            {
                RuntimeBasemapFactory.ApplyHlodTintToTerrainLayers(
                    record.TerrainData, Color.white, false);
            }
        }

        static void DestroyBasemapVisualClones(RuntimeTileRecord record)
        {
            foreach (TerrainLayer layer in record.GpuTerrainLayers)
            {
                if (layer != null)
                    Destroy(layer);
            }

            record.GpuTerrainLayers.Clear();
        }

        void RefreshBasemapVisualsOnAllLoadedTiles()
        {
            foreach (RuntimeTileRecord record in _loaded.Values)
            {
                if (!record.LoadedFromTerrainBundle)
                    ReloadOrthoBasemapForVisuals(record);
                else if (record.TerrainObject != null)
                {
                    Terrain terrain = record.TerrainObject.GetComponent<Terrain>();
                    if (terrain != null)
                    {
                        ApplyBasemapVisuals(record, terrain);
                        terrain.Flush();
                    }
                }
            }
        }

        void ReloadOrthoBasemapForVisuals(RuntimeTileRecord record)
        {
            if (record.TerrainData == null)
                return;

            if (!string.IsNullOrEmpty(activeBasemapId) &&
                record.OrthoTextures.TryGetValue(activeBasemapId, out Texture2D oldTex))
            {
                record.GpuTextures.Remove(oldTex);
                if (oldTex != null)
                    Destroy(oldTex);
                record.OrthoTextures.Remove(activeBasemapId);
            }

            if (!string.IsNullOrEmpty(activeBasemapId) &&
                record.OrthoLayers.TryGetValue(activeBasemapId, out TerrainLayer oldLayer))
            {
                record.GpuTerrainLayers.Remove(oldLayer);
                if (oldLayer != null)
                    Destroy(oldLayer);
                record.OrthoLayers.Remove(activeBasemapId);
            }

            record.ActiveBasemapId = null;
            ApplyBasemap(record);

            if (record.TerrainObject != null)
            {
                Terrain terrain = record.TerrainObject.GetComponent<Terrain>();
                if (terrain != null)
                {
                    ApplyBasemapVisuals(record, terrain);
                    terrain.Flush();
                }
            }
        }

        void ReloadAllBuildings()
        {
            UnloadAllBuildingTiles(destroyImmediately: true);
            EvaluateBuildingStreaming();
        }

        void UnloadRecord(RuntimeTileRecord record)
        {
            PurgeVegetationQueue(record.Key);
            if (UsesEditorPopulatedVegetationTerrain())
                record.VegetationRenderer = null;
            else
                DestroyVegetation(record);

            if (record.TerrainObject != null)
            {
                _terrainByCoord.Remove($"{record.Left}_{record.Bottom}");
                Destroy(record.TerrainObject);
                record.TerrainObject = null;
            }

            if (record.LoadedFromTerrainBundle)
            {
                record.TerrainData = null;
                record.BundleSourceTerrainLayers = null;
                ReleaseTerrainBundle(record, record.TerrainBundleFullPath, unloadAllLoadedObjects: true);
                record.LoadedFromTerrainBundle = false;
                record.LoadedTerrainBundleBasemapId = null;

                foreach (Texture2D tex in record.GpuTextures)
                {
                    if (tex != null)
                        Destroy(tex);
                }
                record.GpuTextures.Clear();

                DestroyBasemapVisualClones(record);
                record.OrthoTextures.Clear();
                record.OrthoLayers.Clear();
            }
            else
            {
                if (record.TerrainData != null)
                {
                    Destroy(record.TerrainData);
                    record.TerrainData = null;
                }

                foreach (Texture2D tex in record.GpuTextures)
                {
                    if (tex != null)
                        Destroy(tex);
                }
                record.GpuTextures.Clear();

                foreach (TerrainLayer layer in record.GpuTerrainLayers)
                {
                    if (layer != null)
                        Destroy(layer);
                }
                record.GpuTerrainLayers.Clear();
            }

            record.OrthoTextures.Clear();
            record.OrthoLayers.Clear();
            record.State = RealtimeTileLodState.Unloaded;
            _appliedPixelErrorByTile.Remove(record.Key);
            _loaded.Remove(record.Key);
            TerrainTileUnloaded?.Invoke(record);
        }

        void UpdateActiveTerrainPixelError()
        {
            if (!streamTerrain)
                return;

            Vector3 camPos = _cam.position;
            foreach (RuntimeTileRecord record in _loaded.Values)
            {
                if (record.TerrainObject == null)
                    continue;

                Terrain terrain = record.TerrainObject.GetComponent<Terrain>();
                if (terrain == null)
                    continue;

                float dist = HlodRingEvaluator.TileBoundaryDistance(
                    camPos, record.UnityPosition, record.TileSizeMeters);
                ApplyTerrainPixelError(record.Key, terrain, dist);
            }
        }

        void ApplyTerrainPixelError(string tileKey, Terrain terrain, float distMeters)
        {
            float targetErr = ResolvePixelError(distMeters);
            if (_appliedPixelErrorByTile.TryGetValue(tileKey, out float applied)
                && Mathf.Approximately(applied, targetErr))
                return;

            if (!Mathf.Approximately(terrain.heightmapPixelError, targetErr))
            {
                terrain.heightmapPixelError = targetErr;
                terrain.Flush();
            }

            _appliedPixelErrorByTile[tileKey] = targetErr;
        }

        float ResolvePixelError(float distMeters)
        {
            if (!dynamicHeightmapLod)
                return pixelError;
            if (distMeters <= terrainLodNearDistance)
                return pixelErrorNear;
            if (distMeters <= terrainLodMidDistance)
                return pixelErrorMid;
            return pixelErrorFar;
        }

        void OnValidate()
        {
            if (terrainBundleOnlyMode)
                preferTerrainBundles = true;

            hlod1x1LoadDistance = Mathf.Max(0f, hlod1x1LoadDistance);
            hlod1x1UnloadDistance = Mathf.Max(hlod1x1LoadDistance, hlod1x1UnloadDistance);
            hlod2x2LoadDistance = Mathf.Max(hlod1x1LoadDistance, hlod2x2LoadDistance);
            hlod2x2UnloadDistance = Mathf.Max(hlod2x2LoadDistance, hlod2x2UnloadDistance);
            hlod4x4LoadDistance = Mathf.Max(hlod2x2LoadDistance, hlod4x4LoadDistance);
            hlod4x4UnloadDistance = Mathf.Max(hlod4x4LoadDistance, hlod4x4UnloadDistance);
            RebuildHlodLevels();
            RebuildEffectiveHlodFactors();

            pixelErrorNear = Mathf.Max(1f, pixelErrorNear);
            pixelErrorMid = Mathf.Max(pixelErrorNear, pixelErrorMid);
            pixelErrorFar = Mathf.Max(pixelErrorMid, pixelErrorFar);
            pixelError = Mathf.Max(1f, pixelError);
            terrainLodNearDistance = Mathf.Max(0f, terrainLodNearDistance);
            terrainLodMidDistance = Mathf.Max(terrainLodNearDistance, terrainLodMidDistance);
            autoFocusDistanceScale = Mathf.Clamp(autoFocusDistanceScale, 0.2f, 2f);
            autoFocusMaxCameraHeightY = Mathf.Max(0f, autoFocusMaxCameraHeightY);
            autoFocusElevationDegrees = Mathf.Clamp(autoFocusElevationDegrees, 10f, 80f);
            debugBasemapBorderPixels = Mathf.Clamp(debugBasemapBorderPixels, 1, 64);
            buildingCheckInterval = Mathf.Max(0.05f, buildingCheckInterval);
            buildingColliderLoadDistanceMeters = Mathf.Max(0f, buildingColliderLoadDistanceMeters);
            maxConcurrentBuildingPrepares = Mathf.Max(1, maxConcurrentBuildingPrepares);
            maxConcurrentBuildingLoads = Mathf.Max(1, maxConcurrentBuildingLoads);
            glbPostProcessBuildingsPerFrame = Mathf.Max(1, glbPostProcessBuildingsPerFrame);
            glbPostProcessMsBudget = Mathf.Max(0f, glbPostProcessMsBudget);
            vegetationRulesPerFrame = Mathf.Max(1, vegetationRulesPerFrame);
            vegetationSpreadMsBudget = Mathf.Max(0f, vegetationSpreadMsBudget);

            if (loadRegionMinE > loadRegionMaxE)
                (loadRegionMinE, loadRegionMaxE) = (loadRegionMaxE, loadRegionMinE);
            if (loadRegionMinN > loadRegionMaxN)
                (loadRegionMinN, loadRegionMaxN) = (loadRegionMaxN, loadRegionMinN);
        }

        void DestroyVegetation(RuntimeTileRecord record)
        {
            if (record.VegetationRenderer != null)
            {
                record.VegetationRenderer.Clear();
                Destroy(record.VegetationRenderer);
                record.VegetationRenderer = null;
            }

            if (record.VegetationMask != null)
            {
                record.GpuTextures.Remove(record.VegetationMask);
                Destroy(record.VegetationMask);
                record.VegetationMask = null;
            }

            record.VegetationSettingsFingerprint = 0;
        }

        void PruneInactiveBuildings()
        {
            if (_buildingDeactivationTimes.Count == 0)
                return;

            float now = Time.time;
            var toDestroy = new List<string>();
            foreach (var kvp in _buildingDeactivationTimes)
            {
                if (now - kvp.Value >= buildingDestroyDelay)
                    toDestroy.Add(kvp.Key);
            }

            foreach (string key in toDestroy)
                DestroyInactiveBuilding(key);
        }

        void EnforceInactiveBuildingCap()
        {
            while (_inactiveBuildings.Count > maxInactiveBuildingGroups)
            {
                string oldest = null;
                float oldestTime = float.MaxValue;
                foreach (var kvp in _buildingDeactivationTimes)
                {
                    if (kvp.Value < oldestTime)
                    {
                        oldestTime = kvp.Value;
                        oldest = kvp.Key;
                    }
                }

                if (oldest == null)
                    break;
                DestroyInactiveBuilding(oldest);
            }
        }

        void DestroyInactiveBuilding(string tileId)
        {
            if (_inactiveBuildings.TryGetValue(tileId, out GameObject go))
            {
                _inactiveBuildings.Remove(tileId);
                if (go != null)
                    Destroy(go);
            }

            if (_buildingTileRecords.TryGetValue(tileId, out RuntimeTileRecord record))
            {
                ReleaseBuildingTileGpuTextures(record);
                _buildingTileRecords.Remove(tileId);
            }

            _buildingDeactivationTimes.Remove(tileId);
        }

        void RewireNeighbors()
        {
            var entries = new List<RuntimeTerrainFactory.TerrainBoundsEntry>();
            foreach (RuntimeTileRecord record in _loaded.Values)
            {
                if (record.TerrainObject == null)
                    continue;

                Terrain terrain = record.TerrainObject.GetComponent<Terrain>();
                if (terrain == null)
                    continue;

                entries.Add(new RuntimeTerrainFactory.TerrainBoundsEntry(
                    terrain, record.Left, record.Bottom, record.TileSizeMeters));
            }

            foreach (RuntimeTerrainFactory.TerrainBoundsEntry entry in entries)
            {
                RuntimeTerrainFactory.SetNeighborsByBounds(
                    entry.Terrain, entry.Left, entry.Bottom, entry.SizeMeters, entries);
            }
        }

        bool ResolveHeightmapFlip(RuntimeTileRecord record)
        {
            if (!record.IsSupertile)
                return flipHeightmapVertically;

            // Legacy v1 packs stored merged supertile RAW in Unity row order (no GDAL unflip on write).
            if (_manifest != null && !_manifest.UsesGdalSupertileHeightmaps())
                return false;

            return flipHeightmapVertically;
        }

        string ResolveHeightmapPath(RuntimeTileRecord record)
        {
            string rel = record.IsSupertile
                ? record.Supertile.HeightmapPath
                : record.Leaf.HeightmapPath;
            if (string.IsNullOrEmpty(rel))
                return null;
            return Path.Combine(_datasetRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        }

        string ResolveOrthoDiskPath(RuntimeTileRecord record, string basemapId)
        {
            if (string.IsNullOrEmpty(basemapId))
                return null;

            StreamingBasemapEntry bm = _manifest.AvailableBasemaps?
                .Find(b => b.Id == basemapId);
            if (bm == null || bm.GetBasemapType() != BasemapType.Ortho)
                return null;

            Dictionary<string, string> paths = record.IsSupertile
                ? record.Supertile.OrthoPaths
                : record.Leaf.OrthoPaths;
            if (paths != null && paths.TryGetValue(basemapId, out string rel))
            {
                string fromManifest = ResolveOrthoPathOnDisk(_datasetRoot, rel);
                if (!string.IsNullOrEmpty(fromManifest))
                    return fromManifest;
            }

            string coordId = record.Key;
            int factor = record.IsSupertile ? record.Supertile.Factor : 1;
            string orthoFolder = factor == 1 ? "ortho_1x1" : $"ortho_{factor}x{factor}";
            foreach (string suffix in new[] { ".png", "png" })
            {
                string conventional = $"basemaps/{basemapId}/{orthoFolder}/{coordId}{suffix}";
                string fromConvention = ResolveOrthoPathOnDisk(_datasetRoot, conventional);
                if (!string.IsNullOrEmpty(fromConvention))
                    return fromConvention;
            }

            return null;
        }

        bool HeightmapFileExists(RuntimeTileRecord record)
        {
            string path = ResolveHeightmapPath(record);
            return !string.IsNullOrEmpty(path) && File.Exists(path);
        }

        bool TerrainDataAvailable(RuntimeTileRecord record)
        {
            if (terrainBundleOnlyMode)
                return preferTerrainBundles && TryGetTerrainBundleRelativePath(record, out _);

            if (HeightmapFileExists(record))
                return true;

            return preferTerrainBundles && TryGetTerrainBundleRelativePath(record, out _);
        }

        static string ResolveOrthoPathOnDisk(string datasetRoot, string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                return null;

            string primary = Path.Combine(datasetRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(primary))
                return primary;

            // Legacy pack bug: extension without dot (e.g. tileId + "png").
            int slash = relativePath.LastIndexOf('/');
            if (slash >= 0 && slash < relativePath.Length - 1)
            {
                string name = relativePath.Substring(slash + 1);
                if (name.EndsWith("png", System.StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".png"))
                {
                    string fixedRel = relativePath.Substring(0, slash + 1) + name.Insert(name.Length - 3, ".");
                    string fixedPath = Path.Combine(datasetRoot, fixedRel.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(fixedPath))
                        return fixedPath;
                }
            }

            return primary;
        }

        static Texture2D LoadPngFromPath(string fullPath, bool linear)
        {
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
                return null;

            byte[] bytes = File.ReadAllBytes(fullPath);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear);
            if (!tex.LoadImage(bytes))
            {
                UnityEngine.Object.Destroy(tex);
                return null;
            }
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.Apply(true, false);
            return tex;
        }

        VegetationPrototype[] ResolveVegetationPrototypes()
        {
            if (vegetationPrototypes != null && vegetationPrototypes.Length > 0)
                return vegetationPrototypes;
            return VegetationPrototypeUtility.CollectFromRuleSet(vegetationRuleSet);
        }

        static bool VegetationChunkHasRenderableContent(VegetationChunk chunk)
        {
            if (chunk?.batches == null)
                return false;

            foreach (VegetationBatch batch in chunk.batches)
            {
                if (batch.instances.Count == 0)
                    continue;

                VegetationPrototype proto = batch.prototype;
                if (proto?.mesh == null)
                    continue;

                if (proto.materials == null || proto.materials.Length == 0)
                    continue;

                return true;
            }

            return false;
        }

        VegetationSpawnAnimationSettings GetVegetationSpawnAnimationSettings() =>
            new VegetationSpawnAnimationSettings
            {
                mode                 = vegetationSpawnAnimation,
                chunkPopInDuration   = vegetationChunkPopInDuration,
                perTreeGrowDuration  = vegetationPerTreeGrowDuration,
                perTreeMaxStagger    = vegetationPerTreeMaxStagger
            };

        VegetationDrawDistanceSettings GetVegetationDrawDistanceSettings() =>
            new VegetationDrawDistanceSettings
            {
                densityFalloffStart = vegetationDensityFalloffStart,
                maxDrawDistance     = vegetationMaxDrawDistance
            };

        int GetVegetationSettingsFingerprint() =>
            System.HashCode.Combine(
                vegetationMaxDrawDistance,
                vegetationDensityFalloffStart,
                vegetationSpawnAnimation,
                vegetationChunkPopInDuration,
                vegetationPerTreeGrowDuration,
                vegetationPerTreeMaxStagger,
                vegetationFrustumCullChunks,
                vegetationFrustumCullInstances);

        void EnsureRoots()
        {
            EnsureRoot(ref terrainRoot, "ZG Connect Terrains (Realtime)");
            EnsureRoot(ref buildingsRoot, "ZG Connect Buildings (Realtime)");
        }

        static void EnsureRoot(ref Transform root, string name)
        {
            if (root != null)
                return;
            var go = GameObject.Find(name);
            if (go == null)
                go = new GameObject(name);
            root = go.transform;
        }

        void EnsureBuildingsRoot() => EnsureRoot(ref buildingsRoot, "ZG Connect Buildings (Realtime)");

        sealed class BuildingStreamRequest
        {
            public string TileId;
            public float PriorityDistance;
            public string GlbPath;
            public string JsonPath;
            public string Lod1Path;
            public string MetadataBytesPath;
        }

        sealed class BuildingBundleRequest
        {
            public string TileId;
            public float PriorityDistance;
            public string BundleRel;
        }

#if UNITY_EDITOR
        public Transform AssignedCameraTransform => cameraTransform;

        /// <summary>
        /// Editor populate: every 1×1 leaf in the load region with terrain data (ignores HLOD 2×2/4×4 and camera distance).
        /// </summary>
        Dictionary<string, RuntimeTileRecord> BuildEditorPopulateTerrainTiles()
        {
            var desired = new Dictionary<string, RuntimeTileRecord>();
            if (_manifest?.Tiles == null)
                return desired;

            int tileSize = _manifest.TileSizeMeters;
            foreach (StreamingTileEntry leaf in _manifest.Tiles)
            {
                if (leaf == null || !IsLeafInsideLoadRegion(leaf))
                    continue;

                var record = RuntimeTileRecord.FromLeaf(leaf, tileSize);
                if (!TerrainDataAvailable(record))
                    continue;

                desired[leaf.TileId] = record;
            }

            return desired;
        }

        public bool EditorTryBuildPopulateRequest(
            Vector3 cameraPosition,
            out RealtimeStreamingEditorPopulateRequest request,
            out string error)
        {
            request = null;
            error = null;

            if (!LoadManifest())
            {
                error = "Failed to load streaming manifest.";
                return false;
            }

            var terrainTiles = BuildEditorPopulateTerrainTiles()
                .Values
                .OrderBy(r => r, Comparer<RuntimeTileRecord>.Create(CompareTileLoadPriority))
                .ToList();

            var buildingLeaves = new List<StreamingTileEntry>();
            if (streamBuildings && _manifest.Tiles != null)
            {
                foreach (StreamingTileEntry leaf in _manifest.Tiles)
                {
                    if (!LeafTileHasBuildings(leaf) || !IsLeafInsideLoadRegion(leaf))
                        continue;
                    buildingLeaves.Add(leaf);
                }
            }

            request = new RealtimeStreamingEditorPopulateRequest
            {
                CameraPosition = cameraPosition,
                TerrainTiles = terrainTiles,
                BuildingLeaves = buildingLeaves,
                DatasetRoot = _datasetRoot,
                ActiveBasemapId = activeBasemapId,
                TerrainMaterial = terrainMaterial,
                StreamTerrain = streamTerrain,
                StreamBuildings = streamBuildings,
                PreferTerrainBundles = preferTerrainBundles,
                TerrainBundleOnlyMode = terrainBundleOnlyMode,
                FlipHeightmapVertically = flipHeightmapVertically,
                DrawInstanced = drawInstanced,
                BasemapDistance = basemapDistance,
                BuildingStyle = buildingStyle,
                BuildingSurfaceSettings = buildingSurfaceSettings,
                RoofOrthophotoMaterialTemplate = roofOrthophotoMaterialTemplate,
            };
            return true;
        }

        public bool EditorTryResolveTerrainBundlePath(RuntimeTileRecord record, out string fullPath)
        {
            fullPath = null;
            if (!TryGetTerrainBundleRelativePath(record, out string bundleRel))
                return false;

            fullPath = Path.Combine(_datasetRoot, bundleRel.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(fullPath);
        }

        public bool EditorTryResolveHeightmapPath(RuntimeTileRecord record, out string fullPath)
        {
            fullPath = ResolveHeightmapPath(record);
            return !string.IsNullOrEmpty(fullPath) && File.Exists(fullPath);
        }

        public bool EditorTryResolveBuildingBundlePath(StreamingTileEntry leaf, out string fullPath)
        {
            fullPath = null;
            if (!TryGetBuildingBundleRelativePath(leaf, out string bundleRel))
                return false;

            fullPath = Path.Combine(_datasetRoot, bundleRel.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(fullPath);
        }

        public bool EditorTryResolveBuildingJsonPath(string tileId, out string fullPath)
        {
            ResolveBuildingTilePaths(tileId, out _, out fullPath, out _);
            return !string.IsNullOrEmpty(fullPath) && File.Exists(fullPath);
        }

        public bool EditorResolveHeightmapFlip(RuntimeTileRecord record) => ResolveHeightmapFlip(record);
#endif
    }
}
