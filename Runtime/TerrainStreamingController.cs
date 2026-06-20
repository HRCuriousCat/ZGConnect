using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace ZGConnect
{
    public enum TerrainQualityPreset
    {
        /// <summary>Integrated GPU / laptop — up to 4 tiles, aggressive LOD.</summary>
        Low    = 0,
        /// <summary>Mid-range discrete GPU (4–8 GB VRAM) — up to 9 tiles.</summary>
        Medium = 1,
        /// <summary>High-end GPU (12+ GB VRAM) — up to 16 tiles, fine LOD.</summary>
        High   = 2,
        /// <summary>All settings taken from the manual inspector fields below.</summary>
        Custom = 3,
    }

    /// <summary>
    /// Streams terrain tiles in and out based on camera proximity.
    ///
    /// Two modes, selected automatically per tile:
    ///
    ///   SetActive mode  — tile has a pre-placed scene object (record.sceneObject != null).
    ///                     The existing GameObject is simply shown/hidden.
    ///                     Used when you import with "Create Scene Objects" enabled,
    ///                     or after clicking "Instantiate All Terrains" in the inspector.
    ///
    ///   Runtime mode    — tile has no scene object (record.sceneObject == null).
    ///                     A terrain GameObject is instantiated from record.terrainData
    ///                     when the tile enters the load radius, and destroyed when it
    ///                     leaves the unload radius.  No scene setup required.
    ///
    /// Both modes can coexist in the same dataset.
    ///
    /// Setup (runtime mode):
    ///   1. Import terrain — "Create Scene Objects" can be OFF.
    ///   2. Add this component to any persistent GameObject.
    ///   3. Assign the CityDataset asset.
    ///   4. Press Play → streaming begins automatically (or call StartStreaming()).
    ///
    /// Setup (SetActive mode):
    ///   1. Import terrain with "Create Scene Objects" ON, OR click
    ///      "Instantiate All Terrains" in this component's inspector.
    ///   2. Same steps 2–4 above.
    /// </summary>
    [DisallowMultipleComponent]
    public class TerrainStreamingController : MonoBehaviour
    {
        [Header("Dataset")]
        [SerializeField] private CityDataset dataset;
        [SerializeField] private Transform   cameraTransform;

        /// <summary>The CityDataset asset driving this controller (read-only).</summary>
        public CityDataset Dataset => dataset;

        [Header("Terrain Rendering")]
        [Tooltip("URP Terrain/Lit (or compatible) material applied to runtime-instantiated tiles. " +
                 "Required for correct rendering in builds, especially when Draw Instanced is enabled.")]
        [SerializeField] private Material terrainMaterial;

        [Header("Radii (meters)")]
        [Tooltip("Tiles whose nearest edge is within this distance will be loaded.")]
        [SerializeField] private float loadRadius   = 3000f;
        [Tooltip("Tiles whose nearest edge exceeds this distance will be unloaded. " +
                 "Must be greater than Load Radius to create a hysteresis band.")]
        [SerializeField] private float unloadRadius = 5000f;

        [Header("Performance")]
        [Tooltip("Seconds between streaming evaluation passes.")]
        [SerializeField] private float checkInterval = 0.5f;

        [Tooltip("Low = integrated GPU (4 tiles, coarse LOD).  Medium = mid-range discrete GPU (9 tiles).  " +
                 "High = 12+ GB VRAM (16 tiles, fine LOD).  Custom = use the manual fields below.")]
        [SerializeField] private TerrainQualityPreset qualityPreset = TerrainQualityPreset.Medium;

        [Tooltip("Maximum number of terrain tiles that may be loaded (active + pending) at once. " +
                 "Ignored unless Quality Preset is Custom. " +
                 "Each tile can consume 15–80 MB of VRAM depending on basemap type and resolution.")]
        [SerializeField] private int maxLoadedTiles = 9;

        [Header("Runtime Terrain Root")]
        [Tooltip("Parent for runtime-instantiated terrain tiles. " +
                 "Auto-created as 'ZG Connect Terrains (Runtime)' if left empty.")]
        [SerializeField] private Transform terrainRoot;

        [Header("Terrain Quality (Runtime Mode)")]
        [Tooltip("drawInstanced setting applied to runtime-instantiated terrains.")]
        [SerializeField] private bool  drawInstanced   = true;
        [Tooltip("Unity Terrain.basemapDistance — distance at which full splat detail fades to the baked basemap.")]
        [SerializeField] private int   basemapDistance  = 2000;
        [Tooltip("When disabled, heightmapPixelError Near/Mid/Far are ignored and Pixel Error is used for all distances.")]
        [SerializeField] private bool  dynamicHeightmapLod = true;
        [Tooltip("heightmapPixelError when the camera is inside or very close to the tile (see Basemap LOD distances).")]
        [SerializeField] private float pixelErrorNear     = 2f;
        [SerializeField] private float pixelErrorMid      = 5f;
        [SerializeField] private float pixelErrorFar      = 10f;
        [Tooltip("Fallback heightmapPixelError when Dynamic Heightmap LOD is off.")]
        [SerializeField] private float pixelError         = 5f;

        [Header("Terrain LOD")]
        [Tooltip("Master switch. When off, no runtime LOD runs — only Basemap Distance and Pixel Error from Terrain Quality are applied.")]
        [SerializeField] private bool terrainLodEnabled = true;

        [Header("Terrain LOD Distances")]
        [Tooltip("Max horizontal distance (m) from tile bounds for the Near tier. 0 m when standing on the tile.")]
        [SerializeField] private float terrainLodNearDistance = 800f;
        [Tooltip("Max distance (m) for Mid tier. Beyond this, Far tier is used.")]
        [SerializeField] private float terrainLodMidDistance  = 3000f;

        [Header("Terrain Texture LOD (close-up quality)")]
        [Tooltip("Forces full-resolution mips on terrain layer textures when near. This is what you see up close — not baseMapResolution.")]
        [SerializeField] private bool  dynamicTerrainTextureLod = true;
        [Tooltip("Unity global texture streaming. Disable to test whether blurry terrain is caused by mip budget (uses more VRAM).")]
        [SerializeField] private bool  enableGlobalTextureStreaming = true;
        [Tooltip("requestedMipmapLevel at Near / Mid / Far (0 = sharpest). Only affects textures imported with Streaming Mipmaps.")]
        [SerializeField] private int   terrainTextureMipNear = 0;
        [SerializeField] private int   terrainTextureMipMid  = 1;
        [SerializeField] private int   terrainTextureMipFar  = 3;
        [Tooltip("While inside the Near distance band, Terrain.basemapDistance is raised so Unity keeps splat/layer detail instead of the far basemap.")]
        [SerializeField] private int   nearTerrainBasemapDistance = 20000;

        [Header("Far Basemap LOD (VRAM only)")]
        [Tooltip("TerrainData.baseMapResolution only affects far-patch rendering. Does not change close-up splat quality.")]
        [SerializeField] private bool  dynamicFarBasemapLod = true;
        [SerializeField] private int   farBasemapResolutionNear = 2048;
        [SerializeField] private int   farBasemapResolutionMid  = 1024;
        [SerializeField] private int   farBasemapResolutionFar  = 512;

        [Header("Buildings")]
        [Tooltip("When enabled, building groups are streamed alongside their terrain tile.\n" +
                 "Prefab-based buildings (buildingsLod0Prefab set) are Instantiated on load " +
                 "and Destroyed on unload. Legacy scene-object buildings (buildingsSceneObject) " +
                 "fall back to SetActive toggling.")]
        [SerializeField] private bool streamBuildings = true;

        [Tooltip("Parent transform for instantiated building groups. " +
                 "Auto-created as 'ZG Connect Buildings (Runtime)' if not assigned.")]
        [SerializeField] private Transform buildingsRoot;

        [Tooltip("Seconds an unloaded building group stays in memory (inactive) before being destroyed. " +
                 "Allows instant re-activation if the camera returns within this window.")]
        [SerializeField] private float buildingDestroyDelay = 30f;

        [Tooltip("Maximum number of inactive building groups kept in memory during the grace period. " +
                 "When the cap is reached the oldest group is destroyed immediately regardless of delay.")]
        [SerializeField] private int maxInactiveBuildingGroups = 10;

        [Tooltip("When enabled, each tile's building group is instantiated with every direct child " +
                 "building deactivated, then revealed by an X sweep from the tile's west edge to its east edge.")]
        [SerializeField] private bool staggerBuildingActivation = false;

        [Tooltip("West-to-east sweep speed (m/s) along world X while Stagger Building Activation is on.")]
        [SerializeField] private float buildingSweepSpeed = 100f;

        [Tooltip("Seconds to lerp each building's localScale.z from 0 to full when the sweep activates it.")]
        [SerializeField] private float buildingRevealDuration = 0.35f;

        [Tooltip("Replaces embedded GLB materials with shared project materials when a tile is instantiated.")]
        [SerializeField] private bool applySharedBuildingMaterials = true;

        [Tooltip("Facade/roof material profiles (same asset as building import).")]
        [SerializeField] private BuildingSurfaceSettings buildingSurfaceSettings;

        [Tooltip("How materials are assigned — match your building import mode.")]
        [SerializeField] private BuildingMaterialApplyStyle buildingMaterialStyle =
            BuildingMaterialApplyStyle.FacadeAndRoofVariants;

        [Tooltip("Basemap id on each CityTileRecord for roof orthophoto mode (e.g. ortho).")]
        [SerializeField] private string roofOrthophotoBasemapId = "ortho";

        [Tooltip("Optional URP Lit template for per-tile runtime roof materials. " +
                 "If empty, a material is created from Building Surface Settings.")]
        [SerializeField] private Material roofOrthophotoMaterialTemplate;

        [Header("Vegetation")]
        [Tooltip("When enabled, vegetation chunks are generated and destroyed alongside terrain tiles.")]
        [SerializeField] private bool streamVegetation = true;

        [Tooltip("Global vegetation rule set shared across all tiles.")]
        [SerializeField] private VegetationRuleSet vegetationRuleSet;

        [Tooltip("Optional render registry. When empty, prototypes are collected automatically " +
                 "from VegetationRuleSet species sets. When set, only prototypes in this list " +
                 "can spawn (allow-list for the scene).")]
        [SerializeField] private VegetationPrototype[] vegetationPrototypes;

        [Header("Vegetation Spawn Animation")]
        [Tooltip("How vegetation appears when a tile streams in. None = full size immediately.")]
        [SerializeField] private VegetationSpawnAnimationMode vegetationSpawnAnimation =
            VegetationSpawnAnimationMode.ChunkPopIn;

        [Tooltip("Whole-tile scale-in duration when mode = Chunk Pop In.")]
        [SerializeField] private float vegetationChunkPopInDuration = 0.35f;

        [Tooltip("Grow duration per tree after stagger when mode = Per Tree.")]
        [SerializeField] private float vegetationPerTreeGrowDuration = 1.2f;

        [Tooltip("Max random delay before each tree starts growing (Per Tree mode).")]
        [SerializeField] private float vegetationPerTreeMaxStagger = 0.8f;

        [Header("Vegetation Draw Distance")]
        [Tooltip("No vegetation is drawn beyond this distance (m). Set to 0 to disable.")]
        [SerializeField] private float vegetationMaxDrawDistance = 1500f;

        [Tooltip("Full density up to this horizontal distance from the camera per instance (m). " +
                 "Beyond it, density linearly decreases until Max Draw Distance.")]
        [SerializeField] private float vegetationDensityFalloffStart = 800f;

        [Tooltip("Skip drawing vegetation tiles whose bounds are outside the camera frustum.")]
        [SerializeField] private bool vegetationFrustumCullChunks = true;

        [Tooltip("Skip individual trees outside the camera frustum (within visible tiles).")]
        [SerializeField] private bool vegetationFrustumCullInstances = true;

        [Header("Start Position")]
        [Tooltip("When enabled, the camera is teleported to the centre of the dataset on Start.\n" +
                 "Useful when your scene camera starts far from the tile grid.")]
        [SerializeField] private bool  teleportCameraOnStart  = false;
        [Tooltip("Camera height above the dataset's maximum terrain height after teleport.")]
        [SerializeField] private float teleportHeightOffset   = 300f;

        [Header("HUD")]
        [SerializeField] private bool showHUD = true;

        // ── Private state ──────────────────────────────────────────────────────
        private Transform _cam;
        private Camera    _camComponent;
        private float     _nextCheckTime;
        private bool      _isStreaming;
        private bool      _initialized;

        private int _loadedCount;
        private int _pendingCount;   // tiles currently being loaded asynchronously
        private int _totalCount;
        private int _visibleBuildingCount;

        private readonly Dictionary<string, Material> _tileRoofMaterials =
            new Dictionary<string, Material>();

        // GPS readout — updated every frame
        private (double lat, double lon) _cameraGPS;
        private (double lat, double lon) _mouseGPS;
        private float _cameraAlt;
        private float _mouseAlt;
        private bool  _mouseOnTerrain;

        // Runtime-mode: terrain GameObjects instantiated by this controller.
        // Separate from record.sceneObject (the importer's static reference).
        private readonly Dictionary<string, GameObject> _runtimeTerrainObjects =
            new Dictionary<string, GameObject>();

        // Addressables load handles — keyed by tileId.
        // _pendingLoads : async load started, TerrainData not yet available.
        // _loadHandles  : load complete; handle MUST be Released when tile unloads to free memory.
        private readonly Dictionary<string, AsyncOperationHandle<TerrainData>> _pendingLoads =
            new Dictionary<string, AsyncOperationHandle<TerrainData>>();
        private readonly Dictionary<string, AsyncOperationHandle<TerrainData>> _loadHandles =
            new Dictionary<string, AsyncOperationHandle<TerrainData>>();

        // Used by RewireNeighbors — rebuilt each pass.
        private readonly Dictionary<string, Terrain> _activeTerrainsById =
            new Dictionary<string, Terrain>();

        // Prefab-based building instances keyed by tileId.
        private readonly Dictionary<string, GameObject> _activeBuildingObjects =
            new Dictionary<string, GameObject>();

        // Building groups deactivated but not yet destroyed (grace period).
        private readonly Dictionary<string, GameObject> _inactiveBuildingObjects =
            new Dictionary<string, GameObject>();
        private readonly Dictionary<string, float> _buildingDeactivationTimes =
            new Dictionary<string, float>();

        private sealed class BuildingStaggerState
        {
            public GameObject tileRoot;
            public float      sweepPosition;
            public float      sweepEndX;
            public int        nextChildIndex;
            public readonly List<Transform> childrenByX = new List<Transform>(256);
        }

        private readonly Dictionary<string, BuildingStaggerState> _buildingStaggerByTileId =
            new Dictionary<string, BuildingStaggerState>();

        private readonly List<string> _buildingStaggerCompletedScratch = new List<string>(8);

        private readonly Dictionary<string, VegetationChunkRenderer> _activeVegetation =
            new Dictionary<string, VegetationChunkRenderer>();

        // Last-applied per-tile settings — avoids redundant Flush / texture mip writes.
        private readonly Dictionary<string, (int basemapRes, float pixelErr, int basemapDist)> _appliedLodByTile =
            new Dictionary<string, (int, float, int)>();

        // Sharpest mip requested per terrain layer texture (shared layers: min mip wins).
        private readonly Dictionary<EntityId, int> _terrainTextureMipById = new Dictionary<EntityId, int>();
        private readonly List<Texture2D> _terrainTextureScratch = new List<Texture2D>(16);

        private VegetationInstanceGenerator _vegetationGenerator;

        private int _streamingMipmapsBudgetMB = 512;
        private int _streamingMipmapsMaxReduction = 3;

        private GUIStyle _hudStyle;
        private GUIStyle _hudShadowStyle;

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>Whether the streaming loop is currently running.</summary>
        public bool IsStreaming => _isStreaming;

        /// <summary>Number of terrain tiles currently active.</summary>
        public int LoadedCount => _loadedCount;

        /// <summary>Number of building GameObjects currently visible across all active tiles.</summary>
        public int VisibleBuildingCount => _visibleBuildingCount;

        /// <summary>
        /// Begins streaming. Runs an immediate evaluation pass then continues
        /// checking every <c>checkInterval</c> seconds.
        /// Safe to call multiple times — subsequent calls only reset the timer.
        /// </summary>
        public void StartStreaming()
        {
            if (!EnsureInitialized()) return;

            _isStreaming   = true;
            _nextCheckTime = 0f;   // force an immediate pass on the next Update

            Debug.Log("[ZGConnect] Terrain streaming started.");
        }

        /// <summary>
        /// Stops the streaming loop.
        /// Runtime-instantiated terrain tiles are destroyed.
        /// SetActive-mode tiles remain in their current state.
        /// Call StartStreaming() to resume.
        /// </summary>
        public void StopStreaming()
        {
            _isStreaming = false;

            // Cancel all pending Addressable loads
            foreach (var kvp in _pendingLoads)
                if (kvp.Value.IsValid()) Addressables.Release(kvp.Value);
            _pendingLoads.Clear();

            // Destroy all runtime-instantiated terrain tiles
            foreach (var kvp in _runtimeTerrainObjects)
                if (kvp.Value != null) Destroy(kvp.Value);
            _runtimeTerrainObjects.Clear();

            // Release all loaded Addressable TerrainData handles (frees TerrainData + textures)
            foreach (var kvp in _loadHandles)
                if (kvp.Value.IsValid()) Addressables.Release(kvp.Value);
            _loadHandles.Clear();

            // Destroy runtime building instances (active and grace-period inactive)
            foreach (var kvp in _activeBuildingObjects)
                if (kvp.Value != null) Destroy(kvp.Value);
            _activeBuildingObjects.Clear();

            foreach (var kvp in _inactiveBuildingObjects)
                if (kvp.Value != null) Destroy(kvp.Value);
            _inactiveBuildingObjects.Clear();
            _buildingDeactivationTimes.Clear();
            _buildingStaggerByTileId.Clear();

            foreach (var kvp in _activeVegetation)
                if (kvp.Value != null) kvp.Value.Clear();
            _activeVegetation.Clear();
            ResetTerrainTextureMipOverrides();

            _loadedCount = 0;

            Debug.Log("[ZGConnect] Terrain streaming stopped.");
        }

        // ──────────────────────────────────────────────────────────────────────

        private void Start()
        {
            // Eagerly resolve all scene references so inspector fields are
            // populated from frame one — no lazy surprises mid-session.
            EnsureTerrainRoot();
            EnsureBuildingsRoot();

            if (EnsureInitialized())
            {
                if (teleportCameraOnStart)
                    TeleportCameraToDatasetCenter();

                StartStreaming();
            }
        }

        private void Update()
        {
            // GPS readout — every frame regardless of tile check interval
            if (_initialized && _cam != null)
                UpdateGPS();

            if (!_isStreaming) return;

            if (staggerBuildingActivation && _buildingStaggerByTileId.Count > 0)
                TickStaggeredBuildingActivation();

            if (Time.time < _nextCheckTime) return;

            _nextCheckTime = Time.time + checkInterval;
            EvaluateTiles();
        }

        private void UpdateGPS()
        {
            _cameraGPS = ZGConnectCoordinates.UnityToWGS84(_cam.position);
            _cameraAlt = ZGConnectCoordinates.UnityYToElevation(_cam.position.y);

            // Mouse → terrain raycast
            Camera cam = _camComponent != null ? _camComponent : Camera.main;
            if (cam == null) { _mouseOnTerrain = false; return; }

#if ENABLE_INPUT_SYSTEM
            Vector2 mouseScreen = UnityEngine.InputSystem.Mouse.current?.position.ReadValue() ?? Vector2.zero;
#else
            Vector2 mouseScreen = Input.mousePosition;
#endif
            Ray ray = cam.ScreenPointToRay(mouseScreen);
            if (Physics.Raycast(ray, out RaycastHit hit, float.PositiveInfinity))
            {
                if (hit.collider is TerrainCollider)
                {
                    _mouseGPS      = ZGConnectCoordinates.UnityToWGS84(hit.point);
                    _mouseAlt      = ZGConnectCoordinates.UnityYToElevation(hit.point.y);
                    _mouseOnTerrain = true;
                    return;
                }
            }
            _mouseOnTerrain = false;
        }

        // ── Initialisation ─────────────────────────────────────────────────────

        private void ApplyQualityPreset()
        {
            switch (qualityPreset)
            {
                case TerrainQualityPreset.Low:
                    maxLoadedTiles            = 16;
                    basemapDistance           = 800;
                    pixelErrorNear            = 8f;
                    pixelErrorMid             = 12f;
                    pixelErrorFar             = 18f;
                    pixelError                = 12f;
                    terrainLodNearDistance    = 500f;
                    terrainLodMidDistance     = 2000f;
                    terrainTextureMipFar      = 4;
                    farBasemapResolutionNear  = 1024;
                    farBasemapResolutionMid   = 512;
                    farBasemapResolutionFar   = 256;
                    _streamingMipmapsBudgetMB = 512;
                    _streamingMipmapsMaxReduction = 4;
                    break;
                case TerrainQualityPreset.Medium:
                    maxLoadedTiles            = 48;
                    basemapDistance           = 2000;
                    pixelErrorNear            = 3f;
                    pixelErrorMid             = 6f;
                    pixelErrorFar             = 10f;
                    pixelError                = 6f;
                    terrainLodNearDistance    = 800f;
                    terrainLodMidDistance     = 3000f;
                    terrainTextureMipFar      = 3;
                    farBasemapResolutionNear  = 2048;
                    farBasemapResolutionMid   = 1024;
                    farBasemapResolutionFar   = 512;
                    _streamingMipmapsBudgetMB = 1024;
                    _streamingMipmapsMaxReduction = 3;
                    break;
                case TerrainQualityPreset.High:
                    maxLoadedTiles            = 128;
                    basemapDistance           = 4000;
                    pixelErrorNear            = 1.5f;
                    pixelErrorMid             = 3f;
                    pixelErrorFar             = 6f;
                    pixelError                = 3f;
                    terrainLodNearDistance    = 1200f;
                    terrainLodMidDistance     = 5000f;
                    terrainTextureMipFar      = 2;
                    farBasemapResolutionNear  = 2048;
                    farBasemapResolutionMid   = 1024;
                    farBasemapResolutionFar   = 512;
                    _streamingMipmapsBudgetMB = 2048;
                    _streamingMipmapsMaxReduction = 2;
                    break;
                case TerrainQualityPreset.Custom:
                    break;  // leave all fields as-is
            }

            if (terrainLodEnabled)
            {
                QualitySettings.streamingMipmapsActive             = enableGlobalTextureStreaming;
                QualitySettings.streamingMipmapsMemoryBudget        = _streamingMipmapsBudgetMB;
                QualitySettings.streamingMipmapsMaxLevelReduction  = _streamingMipmapsMaxReduction;
            }
        }

        private bool EnsureInitialized()
        {
            if (_initialized) return true;

            // Resolve and persist camera reference so it shows in the inspector.
            if (cameraTransform == null)
                cameraTransform = Camera.main?.transform;

            _cam = cameraTransform;

            if (_cam == null)
            {
                Debug.LogError("[ZGConnect] TerrainStreamingController: no camera found. " +
                               "Assign Camera Transform or ensure Camera.main exists.");
                return false;
            }

            if (dataset == null)
            {
                Debug.LogError("[ZGConnect] TerrainStreamingController: CityDataset not assigned.");
                return false;
            }

            ApplyQualityPreset();

            _totalCount    = dataset.tiles.Count;
            _camComponent  = cameraTransform?.GetComponent<Camera>();

            // Configure coordinate converter.
            // Prefer dataset.unityOrigin; if it is zero (dataset imported before
            // the field existed) derive the origin from the first tile instead:
            //   originX = tile.left   − tile.unityPosition.x
            //   originY = tile.bottom − tile.unityPosition.z
            int originX = dataset.unityOrigin.x;
            int originY = dataset.unityOrigin.y;

            if (originX == 0 && originY == 0 &&
                dataset.tiles != null && dataset.tiles.Count > 0)
            {
                CityTileRecord t0 = dataset.tiles[0];
                originX = Mathf.RoundToInt(t0.left   - t0.unityPosition.x);
                originY = Mathf.RoundToInt(t0.bottom - t0.unityPosition.z);
                Debug.Log($"[ZGConnect] Derived coordinate origin from tile data: ({originX}, {originY}). " +
                          "Re-import the dataset to persist this value in dataset.unityOrigin.");
            }

            ZGConnectCoordinates.Configure(originX, originY, dataset.minHeight);

            _initialized = true;
            return true;
        }

        // ── Core evaluation ────────────────────────────────────────────────────

        private void EvaluateTiles()
        {
            Vector3 camPos   = _cam.position;
            int     tileSize = dataset.tileSizeMeters;
            bool    anyChanged = false;
            int     loaded     = 0;
            int     buildings  = 0;

            // ── Pass 1: unload anything beyond unload radius ───────────────────
            foreach (CityTileRecord record in dataset.tiles)
            {
                bool hasData = record.sceneObject != null
                    || (record.terrainDataRef != null && record.terrainDataRef.RuntimeKeyIsValid())
                    || record.terrainData != null;
                if (!hasData) continue;

                float dist     = TileBoundaryDistance(camPos, record.unityPosition, tileSize);
                bool  isLoaded = IsTileLoaded(record);
                bool  isPending = IsTilePendingLoad(record);

                if ((isLoaded || isPending) && dist > unloadRadius)
                {
                    UnloadTile(record);
                    if (streamBuildings)  UnloadBuildingGroup(record);
                    if (streamVegetation) DestroyVegetation(record);
                    anyChanged = true;
                }
            }

            // ── Pass 2: collect candidates inside load radius, sorted by distance ─
            // Tiles currently loading count against the cap to prevent memory spikes.
            int slotsUsed = _runtimeTerrainObjects.Count + _pendingLoads.Count;

            // SetActive-mode tiles don't count against the runtime cap (no VRAM allocation here).
            var candidates = new System.Collections.Generic.List<(float dist, CityTileRecord record)>();

            foreach (CityTileRecord record in dataset.tiles)
            {
                bool hasData = record.sceneObject != null
                    || (record.terrainDataRef != null && record.terrainDataRef.RuntimeKeyIsValid())
                    || record.terrainData != null;
                if (!hasData) continue;

                float dist      = TileBoundaryDistance(camPos, record.unityPosition, tileSize);
                bool  isLoaded  = IsTileLoaded(record);
                bool  isPending = IsTilePendingLoad(record);

                if (!isLoaded && !isPending && dist <= loadRadius)
                    candidates.Add((dist, record));
            }

            candidates.Sort((a, b) => a.dist.CompareTo(b.dist));

            foreach (var (dist, record) in candidates)
            {
                if (slotsUsed >= maxLoadedTiles) break;

                LoadTile(record);
                // Note: buildings and vegetation are triggered from OnTerrainDataLoaded
                // for the Addressables path to keep them in sync with terrain appearance.
                // For SetActive and legacy sync paths they are still triggered here.
                slotsUsed++;
                anyChanged = true;
            }

            // ── Pass 3: tally HUD counters ────────────────────────────────────
            foreach (CityTileRecord record in dataset.tiles)
            {
                if (!IsTileLoaded(record)) continue;
                loaded++;

                if (streamBuildings)
                {
                    if (record.buildingsLod0Prefab != null)
                    {
                        if (_activeBuildingObjects.TryGetValue(record.tileId, out GameObject bgo))
                            buildings += CountVisibleBuildingChildren(bgo);
                    }
                    else if (record.buildingsSceneObject != null &&
                             record.buildingsSceneObject.activeSelf)
                    {
                        buildings += CountVisibleBuildingChildren(record.buildingsSceneObject);
                    }
                }
            }

            _loadedCount          = loaded;
            _pendingCount         = _pendingLoads.Count;
            _visibleBuildingCount = buildings;

            if (anyChanged)
                RewireNeighbors();

            if (streamBuildings)
                PruneInactiveBuildings();

            if (terrainLodEnabled)
                UpdateActiveTerrainLod(camPos, tileSize);
        }

        // ── Tile load / unload ─────────────────────────────────────────────────

        /// <summary>Returns true if the tile terrain is currently active in the scene.</summary>
        private bool IsTileLoaded(CityTileRecord record)
        {
            if (record.sceneObject != null)
                return record.sceneObject.activeSelf;
            return _runtimeTerrainObjects.ContainsKey(record.tileId);
        }

        /// <summary>Returns true if an async Addressables load is in progress for this tile.</summary>
        private bool IsTilePendingLoad(CityTileRecord record) =>
            _pendingLoads.ContainsKey(record.tileId);

        /// <summary>
        /// Returns the active terrain GameObject for a tile, or null if not loaded.
        /// Works for both SetActive and runtime modes.
        /// </summary>
        private GameObject GetTileGameObject(CityTileRecord record)
        {
            if (record.sceneObject != null)
                return record.sceneObject.activeSelf ? record.sceneObject : null;

            _runtimeTerrainObjects.TryGetValue(record.tileId, out GameObject go);
            return go;
        }

        private void LoadTile(CityTileRecord record)
        {
            // SetActive mode: tile was pre-placed in the scene
            if (record.sceneObject != null)
            {
                record.sceneObject.SetActive(true);
                if (streamBuildings)  LoadBuildingGroup(record);
                if (streamVegetation) GenerateVegetation(record);
                return;
            }

            // Guard against double-load
            if (_pendingLoads.ContainsKey(record.tileId)) return;
            if (_runtimeTerrainObjects.ContainsKey(record.tileId)) return;

            // ── Addressables path (preferred — deferred, memory-managed) ──────────
            if (record.terrainDataRef != null && record.terrainDataRef.RuntimeKeyIsValid())
            {
                AsyncOperationHandle<TerrainData> handle =
                    Addressables.LoadAssetAsync<TerrainData>(record.terrainDataRef);

                _pendingLoads[record.tileId] = handle;
                handle.Completed += h => OnTerrainDataLoaded(record, h);
                return;
            }

            // ── Legacy synchronous fallback (pre-migration tiles) ─────────────────
            if (record.terrainData != null)
            {
                CreateTerrainGameObject(record, record.terrainData);
                if (streamBuildings)  LoadBuildingGroup(record);
                if (streamVegetation) GenerateVegetation(record);
                return;
            }

            Debug.LogWarning(
                $"[ZGConnect] Tile '{record.tileId}' has no TerrainData reference. " +
                "Run Addressables migration in the ZG Connect Dataset Import Manager.");
        }

        private void OnTerrainDataLoaded(
            CityTileRecord record, AsyncOperationHandle<TerrainData> handle)
        {
            // Remove from pending regardless of outcome
            _pendingLoads.Remove(record.tileId);

            if (handle.Status != AsyncOperationStatus.Succeeded)
            {
                Debug.LogError(
                    $"[ZGConnect] Failed to load TerrainData for tile '{record.tileId}': " +
                    $"{handle.OperationException?.Message}");
                if (handle.IsValid()) Addressables.Release(handle);
                return;
            }

            // Tile might have left the load radius or streaming stopped while loading
            if (!_isStreaming || _cam == null)
            {
                Addressables.Release(handle);
                return;
            }

            float dist = TileBoundaryDistance(_cam.position, record.unityPosition, dataset.tileSizeMeters);
            if (dist > unloadRadius)
            {
                Addressables.Release(handle);
                return;
            }

            // Store handle — MUST be released in UnloadTile or StopStreaming to free memory
            _loadHandles[record.tileId] = handle;

            CreateTerrainGameObject(record, handle.Result);

            // Trigger buildings and vegetation now that terrain exists
            if (streamBuildings)  LoadBuildingGroup(record);
            if (streamVegetation) GenerateVegetation(record);
        }

        /// <summary>
        /// Creates and registers the terrain GameObject for a tile.
        /// Called both from the Addressables async callback and the legacy sync path.
        /// </summary>
        private void CreateTerrainGameObject(CityTileRecord record, TerrainData td)
        {
            EnsureTerrainRoot();

            GameObject go = Terrain.CreateTerrainGameObject(td);
            go.name = $"Tile_{record.tileId}";
            go.transform.SetParent(terrainRoot);
            go.transform.position = record.unityPosition;

            Terrain terrain = go.GetComponent<Terrain>();

            if (terrainMaterial != null)
                terrain.materialTemplate = terrainMaterial;
            else
                Debug.LogError("[ZGConnect] No terrain material assigned. Runtime terrain may render pink or invisible in build.");

            terrain.drawInstanced   = drawInstanced;
            terrain.basemapDistance = basemapDistance;
            terrain.allowAutoConnect = false;

            if (terrainLodEnabled)
            {
                float dist = TileBoundaryDistance(_cam != null ? _cam.position : Vector3.zero,
                                                  record.unityPosition, dataset.tileSizeMeters);
                ApplyTerrainLod(record, terrain, dist);
            }
            else
                ApplyStaticTerrainQuality(terrain);

            TerrainCollider col = go.GetComponent<TerrainCollider>();
            if (col != null) col.terrainData = td;

            _runtimeTerrainObjects[record.tileId] = go;

            // Stitch seams with any already-active neighbours
            RewireNeighbors();
        }

        // ── Terrain LOD (layer textures, heightmap mesh, far basemap) ───────────

        private void ApplyStaticTerrainQuality(Terrain terrain)
        {
            if (terrain == null) return;
            terrain.heightmapPixelError = pixelError;
            terrain.basemapDistance     = basemapDistance;
        }

        private void ResetTerrainTextureMipOverrides()
        {
            if (dataset?.tiles == null)
            {
                _terrainTextureMipById.Clear();
                _appliedLodByTile.Clear();
                return;
            }

            foreach (CityTileRecord record in dataset.tiles)
            {
                if (!IsTileLoaded(record)) continue;
                GameObject go = GetTileGameObject(record);
                if (go == null) continue;
                Terrain terrain = go.GetComponent<Terrain>();
                if (terrain?.terrainData?.terrainLayers == null) continue;

                foreach (TerrainLayer layer in terrain.terrainData.terrainLayers)
                {
                    if (layer == null) continue;
                    ResetTextureMipOverride(layer.diffuseTexture);
                    ResetTextureMipOverride(layer.normalMapTexture);
                    ResetTextureMipOverride(layer.maskMapTexture);
                }
            }

            _terrainTextureMipById.Clear();
            _appliedLodByTile.Clear();
        }

        private static void ResetTextureMipOverride(Texture2D tex)
        {
            if (tex != null && tex.streamingMipmaps && tex.requestedMipmapLevel != -1)
                tex.requestedMipmapLevel = -1;
        }

        private void UpdateActiveTerrainLod(Vector3 camPos, int tileSize)
        {
            if (!terrainLodEnabled)
            {
                if (_appliedLodByTile.Count > 0 || _terrainTextureMipById.Count > 0)
                    ResetTerrainTextureMipOverrides();
                return;
            }

            _terrainTextureMipById.Clear();

            foreach (CityTileRecord record in dataset.tiles)
            {
                if (!IsTileLoaded(record)) continue;

                GameObject go = GetTileGameObject(record);
                if (go == null) continue;

                Terrain terrain = go.GetComponent<Terrain>();
                if (terrain == null || terrain.terrainData == null) continue;

                float dist = TileBoundaryDistance(camPos, record.unityPosition, tileSize);

                if (dynamicTerrainTextureLod)
                    AccumulateTerrainTextureMips(terrain.terrainData, dist);

                ApplyTerrainComponentLod(record, terrain, dist);
            }

            if (dynamicTerrainTextureLod)
                ApplyAccumulatedTerrainTextureMips();
        }

        private void AccumulateTerrainTextureMips(TerrainData td, float distMeters)
        {
            if (td.terrainLayers == null) return;

            int desiredMip = ResolveTerrainTextureMip(distMeters);

            foreach (TerrainLayer layer in td.terrainLayers)
            {
                if (layer == null) continue;
                AccumulateTextureMip(layer.diffuseTexture, desiredMip);
                AccumulateTextureMip(layer.normalMapTexture, desiredMip);
                AccumulateTextureMip(layer.maskMapTexture, desiredMip);
            }
        }

        private void AccumulateTextureMip(Texture2D tex, int desiredMip)
        {
            if (tex == null || !tex.streamingMipmaps) return;

            EntityId id = tex.GetEntityId();
            if (_terrainTextureMipById.TryGetValue(id, out int current))
                _terrainTextureMipById[id] = Mathf.Min(current, desiredMip);
            else
                _terrainTextureMipById[id] = desiredMip;
        }

        private void ApplyAccumulatedTerrainTextureMips()
        {
            _terrainTextureScratch.Clear();

            foreach (CityTileRecord record in dataset.tiles)
            {
                if (!IsTileLoaded(record)) continue;
                GameObject go = GetTileGameObject(record);
                if (go == null) continue;
                Terrain terrain = go.GetComponent<Terrain>();
                if (terrain?.terrainData?.terrainLayers == null) continue;

                foreach (TerrainLayer layer in terrain.terrainData.terrainLayers)
                {
                    if (layer == null) continue;
                    if (layer.diffuseTexture != null) _terrainTextureScratch.Add(layer.diffuseTexture);
                    if (layer.normalMapTexture != null) _terrainTextureScratch.Add(layer.normalMapTexture);
                    if (layer.maskMapTexture != null) _terrainTextureScratch.Add(layer.maskMapTexture);
                }
            }

            for (int i = 0; i < _terrainTextureScratch.Count; i++)
            {
                Texture2D tex = _terrainTextureScratch[i];
                if (tex == null || !_terrainTextureMipById.TryGetValue(tex.GetEntityId(), out int mip))
                    continue;

                mip = Mathf.Clamp(mip, 0, Mathf.Max(0, tex.mipmapCount - 1));
                if (tex.requestedMipmapLevel != mip)
                    tex.requestedMipmapLevel = mip;
            }
        }

        private int ResolveTerrainTextureMip(float distMeters)
        {
            if (distMeters <= terrainLodNearDistance)
                return Mathf.Max(0, terrainTextureMipNear);
            if (distMeters <= terrainLodMidDistance)
                return Mathf.Max(0, terrainTextureMipMid);
            return Mathf.Max(0, terrainTextureMipFar);
        }

        private int ResolveFarBasemapResolution(float distMeters)
        {
            if (distMeters <= terrainLodNearDistance)
                return ClampBasemapResolution(farBasemapResolutionNear);
            if (distMeters <= terrainLodMidDistance)
                return ClampBasemapResolution(farBasemapResolutionMid);
            return ClampBasemapResolution(farBasemapResolutionFar);
        }

        private int ResolveTerrainBasemapDistance(float distMeters)
        {
            if (distMeters <= terrainLodNearDistance)
                return Mathf.Max(basemapDistance, nearTerrainBasemapDistance);
            return basemapDistance;
        }

        private float ResolvePixelError(float distMeters)
        {
            if (!dynamicHeightmapLod)
                return pixelError;
            if (distMeters <= terrainLodNearDistance)
                return pixelErrorNear;
            if (distMeters <= terrainLodMidDistance)
                return pixelErrorMid;
            return pixelErrorFar;
        }

        private static int ClampBasemapResolution(int resolution)
        {
            resolution = Mathf.Clamp(resolution, 16, 4096);
            return Mathf.ClosestPowerOfTwo(resolution);
        }

        private void ApplyTerrainComponentLod(CityTileRecord record, Terrain terrain, float distMeters)
        {
            TerrainData td = terrain.terrainData;
            if (td == null) return;

            int   targetRes  = dynamicFarBasemapLod ? ResolveFarBasemapResolution(distMeters) : td.baseMapResolution;
            float targetErr  = ResolvePixelError(distMeters);
            int   targetBmap = ResolveTerrainBasemapDistance(distMeters);

            if (_appliedLodByTile.TryGetValue(record.tileId, out var applied)
                && applied.basemapRes == targetRes
                && Mathf.Approximately(applied.pixelErr, targetErr)
                && applied.basemapDist == targetBmap)
                return;

            bool changed = false;

            if (dynamicFarBasemapLod && td.baseMapResolution != targetRes)
            {
                td.baseMapResolution = targetRes;
                td.SetBaseMapDirty();
                changed = true;
            }

            if (!Mathf.Approximately(terrain.heightmapPixelError, targetErr))
            {
                terrain.heightmapPixelError = targetErr;
                changed = true;
            }

            if (terrain.basemapDistance != targetBmap)
            {
                terrain.basemapDistance = targetBmap;
                changed = true;
            }

            if (changed)
                terrain.Flush();

            _appliedLodByTile[record.tileId] = (targetRes, targetErr, targetBmap);
        }

        private void ApplyTerrainLod(CityTileRecord record, Terrain terrain, float distMeters)
        {
            if (dynamicTerrainTextureLod)
            {
                AccumulateTerrainTextureMips(terrain.terrainData, distMeters);
                ApplyAccumulatedTerrainTextureMips();
            }

            ApplyTerrainComponentLod(record, terrain, distMeters);
        }

        private void UnloadTile(CityTileRecord record)
        {
            // SetActive mode
            if (record.sceneObject != null)
            {
                record.sceneObject.SetActive(false);
                _appliedLodByTile.Remove(record.tileId);
                return;
            }

            // Cancel any in-flight async load
            if (_pendingLoads.TryGetValue(record.tileId,
                out AsyncOperationHandle<TerrainData> pendingHandle))
            {
                _pendingLoads.Remove(record.tileId);
                if (pendingHandle.IsValid()) Addressables.Release(pendingHandle);
            }

            // Destroy the scene GameObject
            if (_runtimeTerrainObjects.TryGetValue(record.tileId, out GameObject go))
            {
                _runtimeTerrainObjects.Remove(record.tileId);
                _appliedLodByTile.Remove(record.tileId);
                Destroy(go);
            }

            // Release Addressable handle → TerrainData + embedded layers + textures freed
            if (_loadHandles.TryGetValue(record.tileId,
                out AsyncOperationHandle<TerrainData> loadHandle))
            {
                _loadHandles.Remove(record.tileId);
                if (loadHandle.IsValid()) Addressables.Release(loadHandle);
            }
        }

        // ── Neighbour rewiring ─────────────────────────────────────────────────

        private void RewireNeighbors()
        {
            _activeTerrainsById.Clear();

            foreach (CityTileRecord record in dataset.tiles)
            {
                GameObject go = GetTileGameObject(record);
                if (go == null) continue;

                Terrain t = go.GetComponent<Terrain>();
                if (t != null)
                    _activeTerrainsById[$"{record.left}_{record.bottom}"] = t;
            }

            int tileSize = dataset.tileSizeMeters;

            foreach (CityTileRecord record in dataset.tiles)
            {
                GameObject go = GetTileGameObject(record);
                if (go == null) continue;

                Terrain t = go.GetComponent<Terrain>();
                if (t == null) continue;

                _activeTerrainsById.TryGetValue($"{record.left - tileSize}_{record.bottom}", out Terrain left);
                _activeTerrainsById.TryGetValue($"{record.left + tileSize}_{record.bottom}", out Terrain right);
                _activeTerrainsById.TryGetValue($"{record.left}_{record.bottom + tileSize}",  out Terrain top);
                _activeTerrainsById.TryGetValue($"{record.left}_{record.bottom - tileSize}", out Terrain bottom);

                t.SetNeighbors(left, top, right, bottom);
                t.Flush();
            }
        }

        // ── Distance helper ────────────────────────────────────────────────────

        private static float TileBoundaryDistance(Vector3 camPos, Vector3 tileOrigin, int tileSize)
        {
            float dx = Mathf.Max(0f, tileOrigin.x - camPos.x, camPos.x - (tileOrigin.x + tileSize));
            float dz = Mathf.Max(0f, tileOrigin.z - camPos.z, camPos.z - (tileOrigin.z + tileSize));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // ── Building streaming helpers ─────────────────────────────────────────

        private void LoadBuildingGroup(CityTileRecord record)
        {
            if (record.buildingsLod0Prefab != null)
            {
                if (_activeBuildingObjects.ContainsKey(record.tileId))
                    return;

                // Reactivate from grace period — no Instantiate needed.
                if (_inactiveBuildingObjects.TryGetValue(record.tileId, out GameObject cached))
                {
                    _inactiveBuildingObjects.Remove(record.tileId);
                    _buildingDeactivationTimes.Remove(record.tileId);
                    _activeBuildingObjects[record.tileId] = cached;
                    ActivateBuildingGroup(cached, record);
                    return;
                }

                EnsureBuildingsRoot();
                GameObject go = Instantiate(record.buildingsLod0Prefab, buildingsRoot);
                go.name = $"TileBuildings_{record.tileId}";
                _activeBuildingObjects[record.tileId] = go;
                ActivateBuildingGroup(go, record);
                ScheduleApplySharedBuildingMaterials(go, record);
                return;
            }

            if (record.buildingsSceneObject != null)
            {
                record.buildingsSceneObject.SetActive(true);
                ActivateBuildingGroup(record.buildingsSceneObject, record);
                ScheduleApplySharedBuildingMaterials(record.buildingsSceneObject, record);
            }
        }

        private void ScheduleApplySharedBuildingMaterials(GameObject tileRoot, CityTileRecord record)
        {
            if (!applySharedBuildingMaterials || tileRoot == null || record == null)
                return;

            StartCoroutine(ApplySharedBuildingMaterialsDeferred(tileRoot, record));
        }

        private System.Collections.IEnumerator ApplySharedBuildingMaterialsDeferred(
            GameObject tileRoot,
            CityTileRecord record)
        {
            yield return null;
            if (tileRoot == null || !tileRoot.activeInHierarchy)
                yield break;

            ApplySharedBuildingMaterials(tileRoot, record);
        }

        private void ActivateBuildingGroup(GameObject tileRoot, CityTileRecord record)
        {
            if (tileRoot == null || record == null) return;

            if (staggerBuildingActivation)
            {
                PrepareStaggeredBuildingActivation(tileRoot, record);
                return;
            }

            tileRoot.SetActive(true);
            CancelStaggeredBuildingActivation(record.tileId);
        }

        private void PrepareStaggeredBuildingActivation(GameObject tileRoot, CityTileRecord record)
        {
            tileRoot.SetActive(true);
            DeactivateAllBuildingChildren(tileRoot);

            float startX = record.unityPosition.x;
            float endX   = startX + dataset.tileSizeMeters;

            if (!_buildingStaggerByTileId.TryGetValue(record.tileId, out BuildingStaggerState state))
            {
                state = new BuildingStaggerState { tileRoot = tileRoot };
                _buildingStaggerByTileId[record.tileId] = state;
            }
            else
            {
                state.tileRoot = tileRoot;
            }

            state.sweepPosition  = startX;
            state.sweepEndX      = endX;
            state.nextChildIndex = 0;
            BuildSortedBuildingChildren(tileRoot.transform, state.childrenByX);
        }

        private void CancelStaggeredBuildingActivation(string tileId)
        {
            if (!string.IsNullOrEmpty(tileId))
                _buildingStaggerByTileId.Remove(tileId);
        }

        private void TickStaggeredBuildingActivation()
        {
            float speed = buildingSweepSpeed;
            if (speed <= 0f) return;

            _buildingStaggerCompletedScratch.Clear();

            foreach (var kvp in _buildingStaggerByTileId)
            {
                BuildingStaggerState state = kvp.Value;
                if (state.tileRoot == null)
                {
                    _buildingStaggerCompletedScratch.Add(kvp.Key);
                    continue;
                }

                state.sweepPosition += speed * Time.deltaTime;
                float sweep = state.sweepPosition;

                List<Transform> children = state.childrenByX;
                while (state.nextChildIndex < children.Count)
                {
                    Transform child = children[state.nextChildIndex];
                    if (child == null)
                    {
                        state.nextChildIndex++;
                        continue;
                    }

                    if (child.position.x > sweep)
                        break;

                    RevealBuildingChild(child);
                    state.nextChildIndex++;
                }

                if (state.sweepPosition >= state.sweepEndX)
                {
                    while (state.nextChildIndex < children.Count)
                    {
                        Transform child = children[state.nextChildIndex];
                        if (child != null)
                            RevealBuildingChild(child);
                        state.nextChildIndex++;
                    }

                    _buildingStaggerCompletedScratch.Add(kvp.Key);
                }
            }

            for (int i = 0; i < _buildingStaggerCompletedScratch.Count; i++)
                _buildingStaggerByTileId.Remove(_buildingStaggerCompletedScratch[i]);
        }

        private static void BuildSortedBuildingChildren(Transform tileRoot, List<Transform> into)
        {
            into.Clear();
            int childCount = tileRoot.childCount;
            for (int i = 0; i < childCount; i++)
                into.Add(tileRoot.GetChild(i));

            into.Sort((a, b) => a.position.x.CompareTo(b.position.x));
        }

        private void RevealBuildingChild(Transform child)
        {
            BuildingHeightReveal reveal = child.GetComponent<BuildingHeightReveal>();
            if (reveal == null)
                reveal = child.gameObject.AddComponent<BuildingHeightReveal>();

            reveal.BeginReveal(buildingRevealDuration);
        }

        private static void DeactivateAllBuildingChildren(GameObject tileRoot)
        {
            Transform root = tileRoot.transform;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                BuildingHeightReveal reveal = child.GetComponent<BuildingHeightReveal>();
                if (reveal != null)
                    reveal.PrepareHidden();
                child.gameObject.SetActive(false);
            }
        }

        private static int CountVisibleBuildingChildren(GameObject tileRoot)
        {
            if (tileRoot == null || !tileRoot.activeInHierarchy) return 0;

            Transform root = tileRoot.transform;
            int count = 0;
            for (int i = 0; i < root.childCount; i++)
            {
                if (root.GetChild(i).gameObject.activeSelf)
                    count++;
            }

            return count;
        }

        /// <summary>
        /// Replaces GLB-embedded materials with shared facade/roof or tile orthophoto materials.
        /// Safe to call multiple times on the same hierarchy.
        /// </summary>
        public void ApplySharedBuildingMaterials(GameObject tileRoot, CityTileRecord record)
        {
            if (!applySharedBuildingMaterials || tileRoot == null || record == null)
                return;

            if (buildingSurfaceSettings == null)
            {
                Debug.LogWarning(
                    "[ZGConnect] Apply Shared Building Materials is enabled but Building Surface Settings is not assigned on TerrainStreamingController.");
                return;
            }

            Material roofTemplate = roofOrthophotoMaterialTemplate
                                    ?? buildingSurfaceSettings.GetFlatRoofMaterial(BuildingCategory.House, 0);

            BuildingSharedMaterialApplier.ApplyTile(
                tileRoot,
                record,
                buildingSurfaceSettings,
                buildingMaterialStyle,
                roofOrthophotoBasemapId,
                roofTemplate,
                _tileRoofMaterials);
        }

        private void UnloadBuildingGroup(CityTileRecord record)
        {
            if (record.buildingsLod0Prefab != null)
            {
                if (_activeBuildingObjects.TryGetValue(record.tileId, out GameObject go))
                {
                    _activeBuildingObjects.Remove(record.tileId);
                    CancelStaggeredBuildingActivation(record.tileId);
                    go.SetActive(false);
                    _inactiveBuildingObjects[record.tileId] = go;
                    _buildingDeactivationTimes[record.tileId] = Time.time;
                    EnforceInactiveBuildingCap();
                }
                return;
            }

            if (record.buildingsSceneObject != null)
            {
                CancelStaggeredBuildingActivation(record.tileId);
                record.buildingsSceneObject.SetActive(false);
            }
        }

        private void PruneInactiveBuildings()
        {
            if (_buildingDeactivationTimes.Count == 0) return;

            float now = Time.time;
            var toDestroy = new System.Collections.Generic.List<string>();

            foreach (var kvp in _buildingDeactivationTimes)
                if (now - kvp.Value >= buildingDestroyDelay)
                    toDestroy.Add(kvp.Key);

            foreach (string tileId in toDestroy)
                DestroyInactiveBuilding(tileId);
        }

        private void EnforceInactiveBuildingCap()
        {
            while (_inactiveBuildingObjects.Count > maxInactiveBuildingGroups)
            {
                string oldest = null;
                float  oldestTime = float.MaxValue;

                foreach (var kvp in _buildingDeactivationTimes)
                {
                    if (kvp.Value < oldestTime)
                    {
                        oldestTime = kvp.Value;
                        oldest     = kvp.Key;
                    }
                }

                if (oldest == null) break;
                DestroyInactiveBuilding(oldest);
            }
        }

        private void DestroyInactiveBuilding(string tileId)
        {
            if (_inactiveBuildingObjects.TryGetValue(tileId, out GameObject go))
            {
                _inactiveBuildingObjects.Remove(tileId);
                if (go != null) Destroy(go);
            }
            _buildingDeactivationTimes.Remove(tileId);
            ReleaseTileRoofMaterial(tileId);
        }

        private void ReleaseTileRoofMaterial(string tileId)
        {
            if (string.IsNullOrEmpty(tileId))
                return;

            if (_tileRoofMaterials.TryGetValue(tileId, out Material mat))
            {
                if (mat != null)
                    Destroy(mat);
                _tileRoofMaterials.Remove(tileId);
            }
        }

        // ── Vegetation streaming helpers ───────────────────────────────────────

        /// <summary>
        /// Manual vegetationPrototypes if assigned; otherwise all prototypes from the rule set.
        /// </summary>
        public VegetationPrototype[] ResolveVegetationPrototypes()
        {
            if (vegetationPrototypes != null && vegetationPrototypes.Length > 0)
                return vegetationPrototypes;
            return VegetationPrototypeUtility.CollectFromRuleSet(vegetationRuleSet);
        }

        public VegetationSpawnAnimationSettings GetVegetationSpawnAnimationSettings() =>
            new VegetationSpawnAnimationSettings
            {
                mode                 = vegetationSpawnAnimation,
                chunkPopInDuration   = vegetationChunkPopInDuration,
                perTreeGrowDuration  = vegetationPerTreeGrowDuration,
                perTreeMaxStagger    = vegetationPerTreeMaxStagger
            };

        public VegetationDrawDistanceSettings GetVegetationDrawDistanceSettings() =>
            new VegetationDrawDistanceSettings
            {
                densityFalloffStart = vegetationDensityFalloffStart,
                maxDrawDistance     = vegetationMaxDrawDistance
            };

        private void GenerateVegetation(CityTileRecord record)
        {
            if (!streamVegetation) return;
            if (vegetationRuleSet == null) return;

            VegetationPrototype[] prototypes = ResolveVegetationPrototypes();
            if (prototypes == null || prototypes.Length == 0) return;
            if (record.vegetationMask == null) return;
            if (_activeVegetation.ContainsKey(record.tileId)) return;

            GameObject tileGo = GetTileGameObject(record);
            if (tileGo == null) return;

            VegetationChunkRenderer existingRenderer = tileGo.GetComponent<VegetationChunkRenderer>();
            if (existingRenderer != null && existingRenderer.TotalInstances > 0)
            {
                _activeVegetation[record.tileId] = existingRenderer;
                return;
            }

            Terrain terrain = tileGo.GetComponent<Terrain>();

            int   size  = dataset.tileSizeMeters;
            float halfY = (dataset.maxHeight - dataset.minHeight) * 0.5f;
            float midY  = dataset.minHeight + halfY;

            var bounds = new Bounds(
                new Vector3(record.unityPosition.x + size * 0.5f, midY, record.unityPosition.z + size * 0.5f),
                new Vector3(size, halfY * 2f, size)
            );

            if (_vegetationGenerator == null)
                _vegetationGenerator = new VegetationInstanceGenerator();

            VegetationChunk vchunk = _vegetationGenerator.GenerateChunk(
                record.tileId,
                bounds,
                record.vegetationMask,
                vegetationRuleSet,
                prototypes,
                terrain
            );

            VegetationChunkRenderer renderer = existingRenderer;
            if (renderer == null)
                renderer = tileGo.AddComponent<VegetationChunkRenderer>();

            renderer.SetChunk(
                vchunk,
                prototypes,
                GetVegetationSpawnAnimationSettings(),
                playSpawnAnimation: Application.isPlaying,
                GetVegetationDrawDistanceSettings(),
                cameraTransform,
                vegetationFrustumCullChunks,
                vegetationFrustumCullInstances);

            _activeVegetation[record.tileId] = renderer;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Editor / SetActive pre-bake: generate vegetation on every scene terrain tile that has a mask.
        /// </summary>
        public int EditorBakeAllVegetation(bool forceRegenerate = false)
        {
            if (dataset?.tiles == null || vegetationRuleSet == null)
                return 0;

            VegetationPrototype[] prototypes = ResolveVegetationPrototypes();
            if (prototypes == null || prototypes.Length == 0)
                return 0;

            if (_vegetationGenerator == null)
                _vegetationGenerator = new VegetationInstanceGenerator();

            int size  = dataset.tileSizeMeters;
            float halfY = (dataset.maxHeight - dataset.minHeight) * 0.5f;
            float midY  = dataset.minHeight + halfY;
            int baked = 0;
            int skipped = 0;

            foreach (CityTileRecord record in dataset.tiles)
            {
                if (record.vegetationMask == null) { skipped++; continue; }

                GameObject tileGo = record.sceneObject;
                if (tileGo == null)
                {
                    skipped++;
                    continue;
                }

                VegetationChunkRenderer renderer = tileGo.GetComponent<VegetationChunkRenderer>();
                if (!forceRegenerate && renderer != null && renderer.TotalInstances > 0)
                {
                    skipped++;
                    continue;
                }

                Terrain terrain = tileGo.GetComponent<Terrain>();
                var bounds = new Bounds(
                    new Vector3(record.unityPosition.x + size * 0.5f, midY, record.unityPosition.z + size * 0.5f),
                    new Vector3(size, halfY * 2f, size)
                );

                VegetationChunk vchunk = _vegetationGenerator.GenerateChunk(
                    record.tileId,
                    bounds,
                    record.vegetationMask,
                    vegetationRuleSet,
                    prototypes,
                    terrain
                );

                if (renderer == null)
                    renderer = tileGo.AddComponent<VegetationChunkRenderer>();

                renderer.SetChunk(
                    vchunk,
                    prototypes,
                    default,
                    playSpawnAnimation: false,
                    VegetationDrawDistanceSettings.Disabled,
                    null);
                UnityEditor.EditorUtility.SetDirty(tileGo);
                baked++;
            }

            return baked;
        }
#endif

        private void DestroyVegetation(CityTileRecord record)
        {
            if (_activeVegetation.TryGetValue(record.tileId, out VegetationChunkRenderer renderer))
            {
                if (renderer != null) renderer.Clear();
                _activeVegetation.Remove(record.tileId);
            }
        }

        // ── Camera positioning ─────────────────────────────────────────────────

        /// <summary>
        /// Returns the world-space centre of the dataset tile grid (XZ midpoint,
        /// Y = <c>dataset.maxHeight + teleportHeightOffset</c>).
        /// Returns <c>Vector3.zero</c> if the dataset has no valid tiles.
        /// </summary>
        public Vector3 GetDatasetCenter()
        {
            if (dataset?.tiles == null || dataset.tiles.Count == 0) return Vector3.zero;

            float minX = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxZ = float.MinValue;

            foreach (CityTileRecord tile in dataset.tiles)
            {
                // Accept tiles with either Addressable ref (post-migration) or legacy direct ref
                bool hasTd = (tile.terrainDataRef != null && tile.terrainDataRef.RuntimeKeyIsValid())
                          || tile.terrainData != null
                          || tile.sceneObject != null;
                if (!hasTd) continue;
                float x1 = tile.unityPosition.x;
                float z1 = tile.unityPosition.z;
                float x2 = x1 + dataset.tileSizeMeters;
                float z2 = z1 + dataset.tileSizeMeters;
                if (x1 < minX) minX = x1;
                if (z1 < minZ) minZ = z1;
                if (x2 > maxX) maxX = x2;
                if (z2 > maxZ) maxZ = z2;
            }

            if (minX == float.MaxValue) return Vector3.zero;

            return new Vector3(
                (minX + maxX) * 0.5f,
                dataset.maxHeight + teleportHeightOffset,
                (minZ + maxZ) * 0.5f
            );
        }

        /// <summary>
        /// Moves the camera to <see cref="GetDatasetCenter"/> and angles it 45° downward.
        /// Safe to call from scripts or editor buttons at any time.
        /// </summary>
        public void TeleportCameraToDatasetCenter()
        {
            if (_cam == null)
            {
                Debug.LogWarning("[ZGConnect] TeleportCameraToDatasetCenter: no camera assigned.");
                return;
            }

            Vector3 centre = GetDatasetCenter();
            if (centre == Vector3.zero)
            {
                Debug.LogWarning("[ZGConnect] TeleportCameraToDatasetCenter: dataset has no tiles with TerrainData.");
                return;
            }

            _cam.position = centre;
            _cam.rotation = Quaternion.Euler(45f, 0f, 0f);

            Debug.Log($"[ZGConnect] Camera teleported to dataset centre " +
                      $"({centre.x:F0}, {centre.y:F0}, {centre.z:F0}).");
        }

        // ── Root helpers ───────────────────────────────────────────────────────

        private void EnsureTerrainRoot()
        {
            if (terrainRoot != null) return;
            terrainRoot = new GameObject("ZG Connect Terrains (Runtime)").transform;
        }

        private void EnsureBuildingsRoot()
        {
            if (buildingsRoot != null) return;
            buildingsRoot = new GameObject("ZG Connect Buildings (Runtime)").transform;
        }

        // ── Lifecycle ──────────────────────────────────────────────────────────

        private void OnDestroy()
        {
            // Cancel pending Addressable loads
            foreach (var kvp in _pendingLoads)
                if (kvp.Value.IsValid()) Addressables.Release(kvp.Value);
            _pendingLoads.Clear();

            foreach (var kvp in _runtimeTerrainObjects)
                if (kvp.Value != null) Destroy(kvp.Value);
            _runtimeTerrainObjects.Clear();

            // Release Addressable handles — frees TerrainData + embedded textures
            foreach (var kvp in _loadHandles)
                if (kvp.Value.IsValid()) Addressables.Release(kvp.Value);
            _loadHandles.Clear();

            foreach (var kvp in _activeBuildingObjects)
                if (kvp.Value != null) Destroy(kvp.Value);
            _activeBuildingObjects.Clear();

            foreach (var kvp in _inactiveBuildingObjects)
                if (kvp.Value != null) Destroy(kvp.Value);
            _inactiveBuildingObjects.Clear();
            _buildingDeactivationTimes.Clear();
            _buildingStaggerByTileId.Clear();

            BuildingSharedMaterialApplier.ReleaseCachedRoofMaterials(_tileRoofMaterials);

            foreach (var kvp in _activeVegetation)
                if (kvp.Value != null) kvp.Value.Clear();
            _activeVegetation.Clear();
            ResetTerrainTextureMipOverrides();
        }

        private void OnValidate()
        {
            if (unloadRadius <= loadRadius)
                unloadRadius = loadRadius + 500f;

            buildingSweepSpeed      = Mathf.Max(0.1f, buildingSweepSpeed);
            buildingRevealDuration  = Mathf.Max(0.01f, buildingRevealDuration);

            farBasemapResolutionNear = ClampBasemapResolution(farBasemapResolutionNear);
            farBasemapResolutionMid  = ClampBasemapResolution(farBasemapResolutionMid);
            farBasemapResolutionFar  = ClampBasemapResolution(farBasemapResolutionFar);

            if (terrainLodNearDistance < 0f)
                terrainLodNearDistance = 0f;
            if (terrainLodMidDistance < terrainLodNearDistance)
                terrainLodMidDistance = terrainLodNearDistance + 100f;

            terrainTextureMipNear = Mathf.Max(0, terrainTextureMipNear);
            terrainTextureMipMid  = Mathf.Max(terrainTextureMipNear, terrainTextureMipMid);
            terrainTextureMipFar  = Mathf.Max(terrainTextureMipMid, terrainTextureMipFar);
            nearTerrainBasemapDistance = Mathf.Max(basemapDistance, nearTerrainBasemapDistance);

            pixelErrorNear = Mathf.Max(1f, pixelErrorNear);
            pixelErrorMid  = Mathf.Max(pixelErrorNear, pixelErrorMid);
            pixelErrorFar  = Mathf.Max(pixelErrorMid, pixelErrorFar);
            pixelError     = Mathf.Max(1f, pixelError);

#if UNITY_EDITOR
            // Auto-assign Camera.main if cameraTransform is not set.
            // Runs whenever the component is first added or any inspector field changes.
            if (cameraTransform == null && Camera.main != null)
            {
                cameraTransform = Camera.main.transform;
                UnityEditor.EditorUtility.SetDirty(this);
            }
#endif
        }

        // ── HUD ────────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            if (!showHUD) return;
            EnsureHUDStyles();

            // Build lines in bottom-to-top order
            var lines = new System.Collections.Generic.List<string>(6);

            // Row 1 (bottom): tile streaming status
            string status     = _isStreaming ? "streaming" : "stopped";
            string loadingStr = _pendingCount > 0 ? $"  ({_pendingCount} loading…)" : "";
            lines.Add(_isStreaming
                ? $"Tiles  {_loadedCount} / {_totalCount}  loaded{loadingStr}  (cap {maxLoadedTiles})   |   load {loadRadius:F0} m  ·  unload {unloadRadius:F0} m"
                : $"Tiles  {_loadedCount} / {_totalCount}  loaded   |   {status}");

            // Row 2: buildings (optional)
            if (streamBuildings && _visibleBuildingCount > 0)
                lines.Add($"Buildings  {_visibleBuildingCount:N0}  visible");

            // Rows 3–4: GPS coordinates
            if (ZGConnectCoordinates.IsConfigured)
            {
                if (_mouseOnTerrain)
                    lines.Add($"Cursor   {ZGConnectCoordinates.Format(_mouseGPS.lat, _mouseGPS.lon)}   {_mouseAlt:F0} m");

                lines.Add($"Camera   {ZGConnectCoordinates.Format(_cameraGPS.lat, _cameraGPS.lon)}   {_cameraAlt:F0} m");
            }

            // Render bottom-up
            float w = Screen.width - 20f;
            float h = 22f;
            float y = Screen.height - 14f - h;

            foreach (string line in lines)
            {
                var r = new Rect(10f, y, w, h);
                GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), line, _hudShadowStyle);
                GUI.Label(r, line, _hudStyle);
                y -= h;
            }
        }

        private void EnsureHUDStyles()
        {
            if (_hudStyle != null) return;

            _hudStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize  = 12,
                fontStyle = FontStyle.Bold,
                normal    = { textColor = Color.white }
            };
            _hudShadowStyle = new GUIStyle(_hudStyle)
            {
                normal = { textColor = new Color(0f, 0f, 0f, 0.8f) }
            };
        }
    }
}
