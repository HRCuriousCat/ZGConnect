using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using ZGConnect.RealtimeStreaming;

namespace ZGConnect
{
    /// <summary>Building helpers: enumeration, search, raycasting and highlighting.</summary>
    public sealed partial class ZGConnectToolkit
    {
        const float SceneBuildingCacheLifetime = 5f;
        const float BuildingProbeRayHeight = 2000f;

        // ── Enumeration ────────────────────────────────────────────────────────

        /// <summary>
        /// Enumerates all currently loaded buildings (streamed building tiles plus any
        /// scene objects carrying BuildingData). Metadata is NOT loaded by this call —
        /// it stays lazy until you access it on a handle.
        /// </summary>
        public IEnumerable<ZGBuildingHandle> GetLoadedBuildings()
        {
            var seen = new HashSet<Transform>();

            if (RealtimeStreamer != null)
            {
                foreach (RuntimeTileRecord record in RealtimeStreamer.LoadedBuildingTiles)
                {
                    if (record?.BuildingsObject == null)
                        continue;

                    foreach (Transform building in EnumerateBuildingTransforms(record.BuildingsObject.transform))
                    {
                        if (seen.Add(building))
                            yield return new ZGBuildingHandle(this, building, record.Key);
                    }
                }
            }

            foreach (BuildingData data in GetSceneBuildingData())
            {
                if (data == null || !seen.Add(data.transform))
                    continue;

                yield return new ZGBuildingHandle(this, data.transform, data.tileId);
            }
        }

        /// <summary>Number of currently loaded buildings.</summary>
        public int GetLoadedBuildingCount()
        {
            int count = 0;
            foreach (ZGBuildingHandle _ in GetLoadedBuildings())
                count++;
            return count;
        }

        // ── Search ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Finds a loaded building by its numeric id (e.g. "1619") or full source name
        /// (e.g. "zagreb_Part_1619"). Only streamed-in buildings can be found.
        /// </summary>
        public ZGBuildingHandle FindBuildingById(string buildingId)
        {
            if (string.IsNullOrEmpty(buildingId))
                return null;

            foreach (ZGBuildingHandle building in GetLoadedBuildings())
            {
                if (string.Equals(building.BuildingId, buildingId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(building.Name, buildingId, StringComparison.OrdinalIgnoreCase))
                {
                    return building;
                }
            }

            return null;
        }

        /// <summary>
        /// Building standing on/above a world position (vertical probe). Null when the
        /// point is open ground or the building tile is not loaded.
        /// </summary>
        public ZGBuildingHandle GetBuildingAt(Vector3 worldPos)
        {
            var ray = new Ray(
                new Vector3(worldPos.x, worldPos.y + BuildingProbeRayHeight, worldPos.z),
                Vector3.down);
            return TryRaycastBuilding(ray, out ZGBuildingHandle building, out _, BuildingProbeRayHeight * 2f)
                ? building
                : null;
        }

        /// <summary>Building at a GPS coordinate, or null.</summary>
        public ZGBuildingHandle GetBuildingAtGps(double latitude, double longitude) =>
            GetBuildingAt(GpsToWorld(latitude, longitude));

        /// <summary>
        /// Nearest loaded building to a world position (horizontal distance), or null.
        /// Optionally limited to a maximum radius in metres.
        /// </summary>
        public ZGBuildingHandle GetNearestBuilding(Vector3 worldPos, float maxRadiusMeters = float.PositiveInfinity)
        {
            ZGBuildingHandle best = null;
            float bestSqr = float.IsPositiveInfinity(maxRadiusMeters)
                ? float.MaxValue
                : maxRadiusMeters * maxRadiusMeters;

            foreach (ZGBuildingHandle building in GetLoadedBuildings())
            {
                Vector3 p = building.Position;
                float dx = p.x - worldPos.x;
                float dz = p.z - worldPos.z;
                float sqr = dx * dx + dz * dz;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = building;
                }
            }

            return best;
        }

        /// <summary>
        /// All loaded buildings within a horizontal radius around a centre point.
        /// Prefer <see cref="GetBuildingsInRadius(Vector3, float)"/> to search the full dataset.
        /// </summary>
        public List<ZGBuildingHandle> GetLoadedBuildingsInRadius(Vector3 center, float radiusMeters) =>
            GetBuildingsInRadius(center, radiusMeters, includeUnloadedTiles: false);

        /// <summary>
        /// Fuzzy address search across loaded buildings ("Ilica 5", "trg bana", ...).
        /// Diacritic-insensitive (č/ć/š/đ/ž match c/s/d/z). NOTE: this loads metadata for
        /// every candidate building, so the first call can take a moment on large scenes —
        /// results are cached afterwards.
        /// </summary>
        public List<ZGBuildingHandle> FindBuildingsByAddress(string query, int maxResults = 10)
        {
            var results = new List<ZGBuildingHandle>();
            if (string.IsNullOrWhiteSpace(query))
                return results;

            string folded = FoldDiacritics(query.Trim());

            foreach (ZGBuildingHandle building in GetLoadedBuildings())
            {
                BuildingInfoSnapshot meta = building.Metadata;
                if (meta == null)
                    continue;

                bool match =
                    ContainsFolded(meta.address, folded) ||
                    ContainsFolded(meta.street, folded) ||
                    ContainsFolded(meta.osm.hasData ? meta.osm.buildingMatch.label : null, folded);

                if (!match)
                    continue;

                results.Add(building);
                if (results.Count >= maxResults)
                    break;
            }

            return results;
        }

        /// <summary>
        /// Generic building query — e.g. all buildings older than 1950, taller than
        /// 20 m, or classified Commercial. When <paramref name="requireMetadata"/> is true
        /// (default), buildings without metadata are skipped and metadata is loaded for
        /// every candidate (can be slow on first call).
        /// </summary>
        public List<ZGBuildingHandle> QueryBuildings(
            Func<ZGBuildingHandle, bool> predicate,
            bool requireMetadata = true)
        {
            var results = new List<ZGBuildingHandle>();
            if (predicate == null)
                return results;

            foreach (ZGBuildingHandle building in GetLoadedBuildings())
            {
                if (requireMetadata && !building.HasMetadata)
                    continue;

                if (predicate(building))
                    results.Add(building);
            }

            return results;
        }

        // ── Raycasting ─────────────────────────────────────────────────────────

        /// <summary>Building currently under the mouse cursor, or null.</summary>
        public ZGBuildingHandle GetBuildingUnderCursor()
        {
            Camera cam = ReferenceCamera;
            if (cam == null)
                return null;

            Vector3 mouse;
#if ENABLE_INPUT_SYSTEM
            mouse = UnityEngine.InputSystem.Mouse.current != null
                ? (Vector3)UnityEngine.InputSystem.Mouse.current.position.ReadValue()
                : Vector3.zero;
#else
            mouse = Input.mousePosition;
#endif
            Ray ray = cam.ScreenPointToRay(mouse);
            return TryRaycastBuilding(ray, out ZGBuildingHandle building, out _) ? building : null;
        }

        /// <summary>
        /// Raycasts an arbitrary ray (mouse, VR controller, NPC line of sight, ...) against
        /// the city and returns the first building hit.
        /// </summary>
        public bool TryRaycastBuilding(
            Ray ray,
            out ZGBuildingHandle building,
            out RaycastHit hit,
            float maxDistance = 50000f)
        {
            building = null;
            hit = default;

            RaycastHit[] hits = Physics.RaycastAll(ray, maxDistance, ~0, QueryTriggerInteraction.Ignore);
            if (hits == null || hits.Length == 0)
                return false;

            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            foreach (RaycastHit candidate in hits)
            {
                if (TryResolveBuildingFromCollider(candidate.collider, out building))
                {
                    hit = candidate;
                    return true;
                }

                // Terrain blocks the ray before any building behind it.
                if (candidate.collider is TerrainCollider)
                    return false;
            }

            return false;
        }

        bool TryResolveBuildingFromCollider(Collider collider, out ZGBuildingHandle building)
        {
            building = null;
            if (collider == null)
                return false;

            if (BuildingHitUtility.TryResolveBuildingRootFromCollider(
                    collider, out Transform root, out string tileId))
            {
                building = new ZGBuildingHandle(this, root, tileId);
                return true;
            }

            BuildingData data = collider.GetComponentInParent<BuildingData>();
            if (data != null)
            {
                building = new ZGBuildingHandle(this, data.transform, data.tileId);
                return true;
            }

            return false;
        }

        // ── Highlighting ───────────────────────────────────────────────────────

        Transform _highlightRoot;
        readonly Dictionary<Transform, GameObject> _highlightGhosts = new();
        readonly Dictionary<Color, Material> _highlightMaterials = new();
        readonly Dictionary<Mesh, Mesh> _highlightMeshCache = new();

        /// <summary>
        /// Highlights a building with the default highlight color until cleared —
        /// independent of hover/click selection (mark quest targets, search results, ...).
        /// </summary>
        public bool HighlightBuilding(ZGBuildingHandle building) =>
            HighlightBuilding(building, _defaultHighlightColor);

        /// <summary>Highlights a building with a custom color until cleared.</summary>
        public bool HighlightBuilding(ZGBuildingHandle building, Color color)
        {
            if (building?.Transform == null)
                return false;

            if (!BuildingInteractionLayers.IsValid)
            {
                Debug.LogWarning(
                    $"[ZGConnect] Toolkit highlight requires layer '{BuildingInteractionLayers.HighlightLayerName}' in the Tag Manager.");
                return false;
            }

            EnsureHighlightInfrastructure();

            if (_highlightGhosts.TryGetValue(building.Transform, out GameObject existing) && existing != null)
                Destroy(existing);

            Material material = GetHighlightMaterial(color);
            if (material == null)
                return false;

            GameObject ghost = BuildingGhostBuilder.Build(
                building.Transform,
                _highlightRoot,
                material,
                _highlightMeshCache,
                $"ToolkitHighlight_{building.Name}");

            if (ghost == null)
                return false;

            _highlightGhosts[building.Transform] = ghost;
            return true;
        }

        /// <summary>Removes the toolkit highlight from one building.</summary>
        public void ClearHighlight(ZGBuildingHandle building)
        {
            if (building?.Transform == null)
                return;

            if (_highlightGhosts.TryGetValue(building.Transform, out GameObject ghost))
            {
                if (ghost != null)
                    Destroy(ghost);
                _highlightGhosts.Remove(building.Transform);
            }
        }

        /// <summary>Removes all toolkit highlights.</summary>
        public void ClearAllHighlights()
        {
            foreach (GameObject ghost in _highlightGhosts.Values)
            {
                if (ghost != null)
                    Destroy(ghost);
            }

            _highlightGhosts.Clear();
        }

        /// <summary>Number of buildings currently highlighted through the toolkit.</summary>
        public int HighlightedBuildingCount => _highlightGhosts.Count;

        void EnsureHighlightInfrastructure()
        {
            if (_highlightRoot == null)
            {
                var rootGo = new GameObject("ToolkitHighlightRoot");
                rootGo.transform.SetParent(transform, false);
                _highlightRoot = rootGo.transform;
            }

            // The highlight layer is rendered by a BuildingHighlightCamera; reuse the
            // interaction controller's when present, otherwise attach one to our camera.
            if (FindAnyObjectByType<BuildingHighlightCamera>() == null && ReferenceCamera != null)
            {
                var highlightCamera = ReferenceCamera.gameObject.AddComponent<BuildingHighlightCamera>();
                highlightCamera.Initialize(ReferenceCamera);
            }
        }

        Material GetHighlightMaterial(Color color)
        {
            if (_highlightMaterials.TryGetValue(color, out Material cached) && cached != null)
                return cached;

            Shader shader = Shader.Find("ZGConnect/BuildingOutline");
            if (shader == null)
            {
                Debug.LogError("[ZGConnect] Shader 'ZGConnect/BuildingOutline' was not found.");
                return null;
            }

            var material = new Material(shader);
            if (material.HasProperty("_OutlineColor"))
                material.SetColor("_OutlineColor", color);
            if (material.HasProperty("_OutlineWidth"))
                material.SetFloat("_OutlineWidth", _highlightOutlineWidth);

            _highlightMaterials[color] = material;
            return material;
        }

        void TickHighlights()
        {
            if (_highlightGhosts.Count == 0)
                return;

            List<Transform> stale = null;
            foreach (KeyValuePair<Transform, GameObject> entry in _highlightGhosts)
            {
                if (entry.Key == null || !entry.Key.gameObject.activeInHierarchy)
                    (stale ??= new List<Transform>()).Add(entry.Key);
            }

            if (stale == null)
                return;

            foreach (Transform key in stale)
            {
                if (_highlightGhosts.TryGetValue(key, out GameObject ghost) && ghost != null)
                    Destroy(ghost);
                _highlightGhosts.Remove(key);
            }
        }

        void DisposeHighlights()
        {
            ClearAllHighlights();

            foreach (Material material in _highlightMaterials.Values)
            {
                if (material != null)
                    Destroy(material);
            }
            _highlightMaterials.Clear();

            BuildingGhostBuilder.DestroyMeshCloneCache(_highlightMeshCache);
        }

        // ── Internals ──────────────────────────────────────────────────────────

        BuildingData[] _sceneBuildingCache;
        float _sceneBuildingCacheTime = float.NegativeInfinity;

        BuildingData[] GetSceneBuildingData()
        {
            if (_sceneBuildingCache == null ||
                Time.unscaledTime - _sceneBuildingCacheTime > SceneBuildingCacheLifetime)
            {
                _sceneBuildingCache = FindObjectsByType<BuildingData>();
                _sceneBuildingCacheTime = Time.unscaledTime;
            }

            return _sceneBuildingCache;
        }

        /// <summary>Forces a refresh of the cached scene BuildingData list and tile metadata cache.</summary>
        public void InvalidateBuildingCache()
        {
            _sceneBuildingCache = null;
            _snapshotCache.Clear();
            _tileMetadataCache.Clear();
        }

        static IEnumerable<Transform> EnumerateBuildingTransforms(Transform tileRoot)
        {
            for (int i = 0; i < tileRoot.childCount; i++)
            {
                Transform child = tileRoot.GetChild(i);
                string childName = child.name;

                if (childName == RuntimeBuildingTilePostProcessor.CombinedRenderRootName)
                    continue;

                // Physics-split roots duplicate building nodes with collider-only copies.
                if (childName.StartsWith("TileBuildings_", StringComparison.Ordinal))
                {
                    if (childName.EndsWith("_Physics", StringComparison.Ordinal))
                        continue;

                    if (child.GetComponent<MeshFilter>() == null && child.childCount > 0)
                    {
                        foreach (Transform nested in EnumerateBuildingTransforms(child))
                            yield return nested;
                        continue;
                    }
                }

                yield return child;
            }
        }

        static bool ContainsFolded(string haystack, string foldedNeedle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(foldedNeedle))
                return false;

            return FoldDiacritics(haystack).Contains(foldedNeedle, StringComparison.OrdinalIgnoreCase);
        }

        static string FoldDiacritics(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                switch (c)
                {
                    case 'č': case 'ć': sb.Append('c'); break;
                    case 'Č': case 'Ć': sb.Append('C'); break;
                    case 'đ': sb.Append('d'); break;
                    case 'Đ': sb.Append('D'); break;
                    case 'š': sb.Append('s'); break;
                    case 'Š': sb.Append('S'); break;
                    case 'ž': sb.Append('z'); break;
                    case 'Ž': sb.Append('Z'); break;
                    default: sb.Append(c); break;
                }
            }

            return sb.ToString();
        }
    }
}
