using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect.Roads
{
    public sealed class RoadStreamingController : MonoBehaviour
    {
        [Header("Input")]
        [SerializeField] string _manifestPath;
        [SerializeField] Transform _viewer;

        [Header("Streaming")]
        [SerializeField] int _tileRingRadius = 1;
        [SerializeField] float _evaluateInterval = 0.35f;
        [SerializeField] int _maxGenerationsPerEvaluate = 2;
        [SerializeField] int _maxUnloadsPerEvaluate = 4;

        [Header("Rendering")]
        [SerializeField] Material _asphaltMaterial;
        [SerializeField] RoadStyleProfile _styleProfile;
        [SerializeField] float _surfaceOffset = 0.05f;

        RoadTopologyManifestLoader _loader;
        readonly Dictionary<string, RoadTileInstance> _loaded = new();
        readonly Queue<RoadTopologyManifestTile> _generateQueue = new();
        readonly Queue<string> _unloadQueue = new();
        readonly HashSet<string> _queuedGenerations = new();
        readonly HashSet<string> _queuedUnloads = new();
        float _nextEvaluateTime;

        public int LoadedTileCount => _loaded.Count;
        public int PendingGenerationCount => _generateQueue.Count;
        public int PendingUnloadCount => _unloadQueue.Count;

        void Awake()
        {
            if (!string.IsNullOrWhiteSpace(_manifestPath))
                _loader = new RoadTopologyManifestLoader(_manifestPath);
            if (_viewer == null && Camera.main != null)
                _viewer = Camera.main.transform;
        }

        void Update()
        {
            if (_loader == null || _viewer == null)
                return;

            if (Time.time >= _nextEvaluateTime)
            {
                _nextEvaluateTime = Time.time + Mathf.Max(0.05f, _evaluateInterval);
                EvaluateWantedTiles();
            }

            ProcessQueues();
        }

        void OnDestroy()
        {
            var loadedInstances = new List<RoadTileInstance>(_loaded.Values);
            foreach (RoadTileInstance instance in loadedInstances)
                instance.Dispose();

            _loaded.Clear();
            _generateQueue.Clear();
            _unloadQueue.Clear();
            _queuedGenerations.Clear();
            _queuedUnloads.Clear();
        }

        void EvaluateWantedTiles()
        {
            if (!_loader.TryResolveCameraTile(_viewer.position, out RoadTopologyManifestTile center))
                return;

            int tileSize = _loader.Manifest.TileSizeMeters > 0 ? _loader.Manifest.TileSizeMeters : 1000;
            Vector3 centerOrigin = center.UnityOrigin;
            int centerGX = FloorDiv(centerOrigin.x, tileSize);
            int centerGZ = FloorDiv(centerOrigin.z, tileSize);
            var wanted = new HashSet<string>();

            for (int dz = -_tileRingRadius; dz <= _tileRingRadius; dz++)
            {
                for (int dx = -_tileRingRadius; dx <= _tileRingRadius; dx++)
                {
                    if (!_loader.TryGetTileAtGrid(centerGX + dx, centerGZ + dz, out RoadTopologyManifestTile tile))
                        continue;
                    wanted.Add(tile.TileId);
                    if (!_loaded.ContainsKey(tile.TileId) && _queuedGenerations.Add(tile.TileId))
                        _generateQueue.Enqueue(tile);
                }
            }

            foreach (string tileId in _loaded.Keys)
            {
                if (!wanted.Contains(tileId) && _queuedUnloads.Add(tileId))
                    _unloadQueue.Enqueue(tileId);
            }
        }

        void ProcessQueues()
        {
            int generated = 0;
            int maxGenerations = Mathf.Max(1, _maxGenerationsPerEvaluate);
            while (_generateQueue.Count > 0 && generated < maxGenerations)
            {
                RoadTopologyManifestTile manifestTile = _generateQueue.Dequeue();
                _queuedGenerations.Remove(manifestTile.TileId);
                if (_loaded.ContainsKey(manifestTile.TileId))
                    continue;
                RoadTopologyTile tile = _loader.LoadTile(manifestTile);
                if (tile == null)
                    continue;
                _loaded[manifestTile.TileId] = BuildTile(tile);
                generated++;
            }

            int unloaded = 0;
            int maxUnloads = Mathf.Max(1, _maxUnloadsPerEvaluate);
            while (_unloadQueue.Count > 0 && unloaded < maxUnloads)
            {
                string tileId = _unloadQueue.Dequeue();
                _queuedUnloads.Remove(tileId);
                if (_loaded.TryGetValue(tileId, out RoadTileInstance instance))
                {
                    instance.Dispose();
                    _loaded.Remove(tileId);
                    unloaded++;
                }
            }
        }

        RoadTileInstance BuildTile(RoadTopologyTile tile)
        {
            var root = new GameObject($"RoadTile_{tile.TileId}");
            root.transform.SetParent(transform, worldPositionStays: true);

            var heightSampler = new RoadHeightSampler(Terrain.activeTerrain, _surfaceOffset);
            Mesh asphaltMesh = RoadMeshBuilder.BuildAsphaltMesh(tile, heightSampler);
            CreateMeshChildIfValid(root.transform, "Asphalt", asphaltMesh, _styleProfile?.asphaltMaterial != null ? _styleProfile.asphaltMaterial : _asphaltMaterial);

            float sidewalkOffset = _styleProfile != null ? _styleProfile.sidewalkHeightOffset : 0.08f;
            Mesh sidewalkMesh = RoadMeshBuilder.BuildPolygonMesh($"Road_Sidewalk_{tile.TileId}", tile.SidewalkPolygons, heightSampler, sidewalkOffset);
            CreateMeshChildIfValid(root.transform, "Sidewalk", sidewalkMesh, _styleProfile?.sidewalkMaterial != null ? _styleProfile.sidewalkMaterial : _asphaltMaterial);

            return new RoadTileInstance(tile.TileId, root, asphaltMesh, sidewalkMesh);
        }

        static void CreateMeshChildIfValid(Transform parent, string name, Mesh mesh, Material material)
        {
            if (mesh == null || mesh.vertexCount == 0 || mesh.triangles == null || mesh.triangles.Length == 0)
                return;

            var child = new GameObject(name);
            child.transform.SetParent(parent, worldPositionStays: false);

            var filter = child.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            var renderer = child.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
        }

        static int FloorDiv(float value, int divisor) =>
            Mathf.FloorToInt(value / divisor);
    }
}
