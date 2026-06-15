using System.Collections.Generic;
using UnityEngine;
using ZGConnect.RealtimeStreaming;

namespace ZGConnect
{
    /// <summary>
    /// Developer toolbox for the ZGConnect city plugin.
    ///
    /// Drop this component on any GameObject in a ZGConnect scene and use
    /// <c>ZGConnectToolkit.Instance</c> from your scripts. It bundles the most common
    /// helper operations behind one API:
    ///
    ///  - GPS / EPSG:3765 / Unity coordinate conversions, distances, bearings, geofencing
    ///  - terrain ground height, slope, normals, snap-to-ground, random points
    ///  - building search (by id, address, radius, predicate), metadata access, bounds/rooftops
    ///  - programmatic building highlighting and click/hover events
    ///  - camera teleport / fly-to navigation (world, GPS or address targets)
    ///  - streaming info (loaded tiles, progress) and area prewarming
    ///
    /// All references resolve automatically — assign them only to override the defaults.
    /// The custom inspector shows a live dashboard in play mode.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("ZG Connect/ZG Connect Toolkit")]
    public sealed partial class ZGConnectToolkit : MonoBehaviour
    {
        // ── Inspector ──────────────────────────────────────────────────────────

        [Header("References (auto-resolved when empty)")]
        [Tooltip("Camera used for cursor raycasts and as the default navigation target. Defaults to Camera.main.")]
        [SerializeField] Camera _referenceCamera;

        [Tooltip("Runtime streaming controller. Auto-found in the scene.")]
        [SerializeField] RealtimeStreamingController _realtimeStreamer;

        [Tooltip("Editor/dataset streaming controller. Auto-found in the scene.")]
        [SerializeField] TerrainStreamingController _terrainStreamer;

        [Tooltip("Building interaction controller used to relay click/hover events. Auto-found in the scene.")]
        [SerializeField] BuildingInteractionController _buildingInteraction;

        [Header("Navigation")]
        [Tooltip("Transform moved by TeleportTo / FlyTo. Defaults to the reference camera.")]
        [SerializeField] Transform _navigationTarget;

        [Header("Highlight")]
        [Tooltip("Default color used by HighlightBuilding when no color is passed.")]
        [SerializeField] Color _defaultHighlightColor = new(1f, 0.72f, 0.3f, 1f);

        [Tooltip("Outline width for the fallback ZGConnect/BuildingOutline shader.")]
        [SerializeField] float _highlightOutlineWidth = 0.02f;

        [Header("Scene Gizmos")]
        [Tooltip("Draw the dataset bounds as a wire box in the Scene view.")]
        public bool drawDatasetBounds = true;

        [Tooltip("Draw the 1 km tile grid inside the dataset bounds.")]
        public bool drawTileGrid;

        [Tooltip("Mark the dataset origin (Unity 0,0,0) in the Scene view.")]
        public bool drawOrigin = true;

        // ── Singleton ──────────────────────────────────────────────────────────

        static ZGConnectToolkit _instance;

        /// <summary>
        /// Scene singleton. Finds an existing toolkit, or creates one on the fly so that
        /// <c>ZGConnectToolkit.Instance</c> always works in play mode.
        /// </summary>
        public static ZGConnectToolkit Instance
        {
            get
            {
                if (_instance != null)
                    return _instance;

                _instance = FindAnyObjectByType<ZGConnectToolkit>();
                if (_instance == null && Application.isPlaying)
                    _instance = new GameObject("ZGConnectToolkit").AddComponent<ZGConnectToolkit>();

                return _instance;
            }
        }

        // ── Resolved references ────────────────────────────────────────────────

        bool _streamerEventsHooked;
        bool _interactionEventsHooked;

        /// <summary>Camera used for cursor raycasts and navigation. Never null while a camera exists.</summary>
        public Camera ReferenceCamera
        {
            get
            {
                if (_referenceCamera == null)
                    _referenceCamera = Camera.main;
                return _referenceCamera;
            }
        }

        /// <summary>Runtime streaming controller, or null when the scene does not use it.</summary>
        public RealtimeStreamingController RealtimeStreamer
        {
            get
            {
                if (_realtimeStreamer == null)
                    _realtimeStreamer = FindAnyObjectByType<RealtimeStreamingController>();
                return _realtimeStreamer;
            }
        }

        /// <summary>Editor/dataset streaming controller, or null when the scene does not use it.</summary>
        public TerrainStreamingController TerrainStreamer
        {
            get
            {
                if (_terrainStreamer == null)
                    _terrainStreamer = FindAnyObjectByType<TerrainStreamingController>();
                return _terrainStreamer;
            }
        }

        /// <summary>Building interaction controller, or null when none exists in the scene.</summary>
        public BuildingInteractionController BuildingInteraction
        {
            get
            {
                if (_buildingInteraction == null)
                    _buildingInteraction = FindAnyObjectByType<BuildingInteractionController>();
                return _buildingInteraction;
            }
        }

        // ── Lifecycle ──────────────────────────────────────────────────────────

        void Awake()
        {
            if (_instance == null)
                _instance = this;
        }

        float _nextResolveCheck;

        void Update()
        {
            // Controller lookup involves FindAnyObjectByType — throttle the retries.
            if (Time.unscaledTime >= _nextResolveCheck)
            {
                _nextResolveCheck = Time.unscaledTime + 2f;
                EnsureCoordinatesConfigured();
                HookStreamerEvents();
                HookInteractionEvents();
            }

            TickHighlights();
        }

        void OnDestroy()
        {
            UnhookStreamerEvents();
            UnhookInteractionEvents();
            DisposeHighlights();

            if (_instance == this)
                _instance = null;
        }

        /// <summary>
        /// Makes sure ZGConnectCoordinates is configured even before the streaming
        /// controllers finish their own setup (uses the realtime manifest when present).
        /// </summary>
        void EnsureCoordinatesConfigured()
        {
            if (ZGConnectCoordinates.IsConfigured)
                return;

            StreamingDatasetManifest manifest = RealtimeStreamer != null ? RealtimeStreamer.Manifest : null;
            if (manifest != null)
            {
                Vector2Int origin = manifest.GetUnityOrigin();
                ZGConnectCoordinates.Configure(origin.x, origin.y, manifest.MinHeight);
                return;
            }

            CityDataset dataset = TerrainStreamer != null ? TerrainStreamer.Dataset : null;
            if (dataset != null && dataset.unityOrigin != Vector2Int.zero)
                ZGConnectCoordinates.Configure(dataset.unityOrigin.x, dataset.unityOrigin.y, dataset.minHeight);
        }

        // ── Dataset info ───────────────────────────────────────────────────────

        Bounds _datasetBounds;
        bool _datasetBoundsValid;

        /// <summary>Size of one terrain tile in metres (1000 by default).</summary>
        public int TileSizeMeters
        {
            get
            {
                if (RealtimeStreamer != null && RealtimeStreamer.Manifest != null)
                    return RealtimeStreamer.Manifest.TileSizeMeters;
                if (TerrainStreamer != null && TerrainStreamer.Dataset != null)
                    return TerrainStreamer.Dataset.tileSizeMeters;
                return 1000;
            }
        }

        /// <summary>
        /// World-space bounds of the whole dataset (all tiles, min terrain height to max).
        /// Returns false when no dataset/manifest is available yet.
        /// </summary>
        public bool TryGetDatasetBounds(out Bounds bounds)
        {
            if (_datasetBoundsValid)
            {
                bounds = _datasetBounds;
                return true;
            }

            bounds = default;

            float minX = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxZ = float.MinValue;
            float heightSpan = 0f;
            bool any = false;

            StreamingDatasetManifest manifest = RealtimeStreamer != null ? RealtimeStreamer.Manifest : null;
            if (manifest?.Tiles != null && manifest.Tiles.Count > 0)
            {
                foreach (StreamingTileEntry tile in manifest.Tiles)
                {
                    Vector3 p = tile.GetUnityPosition();
                    minX = Mathf.Min(minX, p.x);
                    minZ = Mathf.Min(minZ, p.z);
                    maxX = Mathf.Max(maxX, p.x + manifest.TileSizeMeters);
                    maxZ = Mathf.Max(maxZ, p.z + manifest.TileSizeMeters);
                    any = true;
                }

                heightSpan = Mathf.Max(0f, manifest.MaxHeight - manifest.MinHeight);
            }
            else
            {
                CityDataset dataset = TerrainStreamer != null ? TerrainStreamer.Dataset : null;
                if (dataset?.tiles != null && dataset.tiles.Count > 0)
                {
                    foreach (CityTileRecord tile in dataset.tiles)
                    {
                        minX = Mathf.Min(minX, tile.unityPosition.x);
                        minZ = Mathf.Min(minZ, tile.unityPosition.z);
                        maxX = Mathf.Max(maxX, tile.unityPosition.x + dataset.tileSizeMeters);
                        maxZ = Mathf.Max(maxZ, tile.unityPosition.z + dataset.tileSizeMeters);
                        any = true;
                    }

                    heightSpan = Mathf.Max(0f, dataset.maxHeight - dataset.minHeight);
                }
            }

            if (!any)
                return false;

            var min = new Vector3(minX, 0f, minZ);
            var max = new Vector3(maxX, heightSpan, maxZ);
            _datasetBounds = new Bounds((min + max) * 0.5f, max - min);
            _datasetBoundsValid = true;
            bounds = _datasetBounds;
            return true;
        }

        /// <summary>True when the world position lies inside the dataset's XZ extent.</summary>
        public bool IsInsideDataset(Vector3 worldPos)
        {
            if (!TryGetDatasetBounds(out Bounds b))
                return false;

            return worldPos.x >= b.min.x && worldPos.x <= b.max.x &&
                   worldPos.z >= b.min.z && worldPos.z <= b.max.z;
        }

        /// <summary>True when the GPS coordinate lies inside the dataset's XZ extent.</summary>
        public bool IsInsideDatasetGps(double latitude, double longitude) =>
            IsInsideDataset(ZGConnectCoordinates.WGS84ToUnity(latitude, longitude));

        // ── Gizmos ─────────────────────────────────────────────────────────────

        void OnDrawGizmos()
        {
            if (!drawDatasetBounds && !drawTileGrid && !drawOrigin)
                return;

            bool hasBounds = TryGetDatasetBounds(out Bounds bounds);

            if (drawDatasetBounds && hasBounds)
            {
                Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.9f);
                Gizmos.DrawWireCube(bounds.center, bounds.size);
            }

            if (drawTileGrid && hasBounds)
            {
                Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.25f);
                int tileSize = TileSizeMeters;
                for (float x = bounds.min.x; x <= bounds.max.x + 0.5f; x += tileSize)
                    Gizmos.DrawLine(new Vector3(x, 0f, bounds.min.z), new Vector3(x, 0f, bounds.max.z));
                for (float z = bounds.min.z; z <= bounds.max.z + 0.5f; z += tileSize)
                    Gizmos.DrawLine(new Vector3(bounds.min.x, 0f, z), new Vector3(bounds.max.x, 0f, z));
            }

            if (drawOrigin)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawSphere(Vector3.zero, 8f);
                Gizmos.DrawLine(Vector3.zero, Vector3.up * 200f);
            }
        }

        // ── Snapshot cache (shared with ZGBuildingHandle) ──────────────────────

        readonly Dictionary<string, BuildingInfoSnapshot> _snapshotCache = new();

        /// <summary>
        /// Resolves the metadata snapshot for a building root. Reads the BuildingData
        /// component when present, otherwise falls back to the lazy tile-metadata resolver.
        /// Results are cached per building.
        /// </summary>
        internal BuildingInfoSnapshot ResolveBuildingSnapshot(Transform buildingRoot, string tileId)
        {
            if (buildingRoot == null)
                return null;

            string cacheKey = $"{tileId}|{buildingRoot.name}";
            if (_snapshotCache.TryGetValue(cacheKey, out BuildingInfoSnapshot cached))
                return cached;

            BuildingInfoSnapshot snapshot = null;

            BuildingData data = buildingRoot.GetComponent<BuildingData>();
            if (data != null)
            {
                snapshot = SnapshotFromBuildingData(data);
            }
            else if (RealtimeStreamer != null)
            {
                BuildingLazyMetadataResolver resolver = RealtimeStreamer.GetBuildingLazyMetadataResolver();
                if (resolver != null && resolver.IsConfigured)
                    resolver.TryLoadBuildingInfo(buildingRoot, tileId, out snapshot);
            }

            if (snapshot != null)
                _snapshotCache[cacheKey] = snapshot;

            return snapshot;
        }

        static BuildingInfoSnapshot SnapshotFromBuildingData(BuildingData data)
        {
            var snapshot = new BuildingInfoSnapshot
            {
                buildingId = data.buildingId,
                tileId = data.tileId,
                sourceName = data.sourceName,
                gps = data.gps,
                osm = data.osm,
                address = data.address,
                floors = data.floors,
                grossFloorArea = data.grossFloorArea,
                useClassification = data.useClassification,
                constructionYear = data.constructionYear,
            };

            BuildingOsmMetadataUtility.EnrichDisplayFields(snapshot);
            return snapshot;
        }
    }
}
