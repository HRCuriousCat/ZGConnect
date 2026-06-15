using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ZGConnect.Editor
{
    /// <summary>
    /// Editor tool that groups individually imported building meshes into per-tile
    /// parent GameObjects and wires them into CityDataset for streaming.
    ///
    /// Workflow:
    ///   1. Align your building hierarchy manually in the Scene view so footprints
    ///      match the terrain tiles.
    ///   2. Open this window (Tools > ZG Connect > Building Associator).
    ///   3. Assign the buildings root GameObject and the CityDataset asset.
    ///   4. Click Preview to see how many buildings fall in each tile.
    ///   5. Click Associate Buildings to restructure the hierarchy and update the dataset.
    ///
    /// Each DIRECT CHILD of the buildings root is treated as one building unit.
    /// Its entire internal hierarchy (sub-meshes, LOD children, etc.) moves as a whole.
    /// </summary>
    public class ZGConnectBuildingAssociatorWindow : EditorWindow
    {
        // ── Inputs ─────────────────────────────────────────────────────────────
        private GameObject  _buildingsRoot;
        private CityDataset _dataset;
        private bool        _snapToTerrain      = false;
        private bool        _disableAfterWiring = true;

        // ── Preview state ──────────────────────────────────────────────────────
        private bool                    _hasPreviewed     = false;
        private Dictionary<string, int> _previewCounts;
        private int                     _previewUnassigned;
        private int                     _previewTotal;
        private Vector2                 _previewScroll;

        // ── Tile lookup ────────────────────────────────────────────────────────
        // Built once per Preview / Association run for O(1) tile classification.
        // Key: (gridX, gridZ) = tile origin divided by tileSizeMeters (integer grid coords).
        // Tile origins are always exact multiples of tileSizeMeters after ZG Connect import,
        // so FloorToInt(cx / size) == RoundToInt(origin.x / size) for any point inside the tile.
        private Dictionary<(int, int), CityTileRecord> _tileLookup;

        // ──────────────────────────────────────────────────────────────────────

        [MenuItem("ZG Connect/Building Associator")]
        public static void ShowWindow()
        {
            var w = GetWindow<ZGConnectBuildingAssociatorWindow>("ZG Connect — Buildings");
            w.minSize = new Vector2(420, 500);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("ZG Connect — Building Associator", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            DrawInputs();
            EditorGUILayout.Space(6);
            DrawOptions();
            EditorGUILayout.Space(6);
            DrawPreviewButton();

            if (_hasPreviewed)
            {
                EditorGUILayout.Space(4);
                DrawPreviewResults();
                EditorGUILayout.Space(8);
                DrawAssociateButton();
            }
        }

        // ── Sections ───────────────────────────────────────────────────────────

        private void DrawInputs()
        {
            EditorGUILayout.LabelField("Inputs", EditorStyles.boldLabel);

            var newRoot = (GameObject)EditorGUILayout.ObjectField(
                "Buildings Root", _buildingsRoot, typeof(GameObject), true);
            if (newRoot != _buildingsRoot) { _buildingsRoot = newRoot; _hasPreviewed = false; }

            var newDataset = (CityDataset)EditorGUILayout.ObjectField(
                "Dataset", _dataset, typeof(CityDataset), false);
            if (newDataset != _dataset) { _dataset = newDataset; _hasPreviewed = false; }

            if (_buildingsRoot == null)
            {
                EditorGUILayout.HelpBox(
                    "Assign the root GameObject whose direct children are the individual buildings. " +
                    "Align it to the terrain in the Scene view before running Preview.",
                    MessageType.Info);
                return;
            }

            int childCount = _buildingsRoot.transform.childCount;
            EditorGUILayout.LabelField(
                $"Direct children (buildings): {childCount}",
                EditorStyles.miniLabel);
        }

        private void DrawOptions()
        {
            EditorGUILayout.LabelField("Options", EditorStyles.boldLabel);

            _snapToTerrain = EditorGUILayout.Toggle(
                new GUIContent("Snap to terrain surface",
                    "After grouping, raycasts each building downward and sets its Y position " +
                    "to the terrain surface hit point. Requires terrain colliders to be active."),
                _snapToTerrain);

            _disableAfterWiring = EditorGUILayout.Toggle(
                new GUIContent("Disable groups after wiring",
                    "Sets all per-tile building groups to inactive after association. " +
                    "Recommended: streaming expects everything to start disabled."),
                _disableAfterWiring);
        }

        private void DrawPreviewButton()
        {
            using (new EditorGUI.DisabledScope(_buildingsRoot == null || _dataset == null))
            {
                if (GUILayout.Button("Preview  (no scene changes)", GUILayout.Height(28)))
                    RunPreview();
            }
        }

        private void DrawPreviewResults()
        {
            EditorGUILayout.LabelField(
                $"Analysis — {_previewTotal} buildings total, {_previewUnassigned} unassigned",
                EditorStyles.boldLabel);

            _previewScroll = EditorGUILayout.BeginScrollView(
                _previewScroll, GUILayout.MaxHeight(200));

            var sortedTiles = new List<CityTileRecord>(_dataset.tiles);
            sortedTiles.Sort((a, b) => string.Compare(
                a.tileId, b.tileId, System.StringComparison.Ordinal));

            foreach (var rec in sortedTiles)
            {
                _previewCounts.TryGetValue(rec.tileId, out int count);
                if (count == 0) continue;

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"  Tile {rec.tileId}", GUILayout.Width(220));
                    EditorGUILayout.LabelField($"{count} buildings", EditorStyles.miniLabel);
                }
            }

            if (_previewUnassigned > 0)
            {
                var warnStyle = new GUIStyle(EditorStyles.miniLabel)
                    { normal = { textColor = new Color(0.9f, 0.5f, 0.1f) } };
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(
                        "  Unassigned (outside all tile bounds)", GUILayout.Width(220));
                    EditorGUILayout.LabelField(
                        $"{_previewUnassigned} buildings", warnStyle);
                }
                EditorGUILayout.HelpBox(
                    "Unassigned buildings have their XZ centre outside every tile. " +
                    "Check that the buildings root is correctly aligned with the terrain.",
                    MessageType.Warning);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawAssociateButton()
        {
            Color prev = GUI.backgroundColor;

            GUI.backgroundColor = new Color(0.3f, 0.75f, 0.3f);
            if (GUILayout.Button("Associate Buildings", GUILayout.Height(36)))
                RunAssociation(firstTileOnly: false);

            EditorGUILayout.Space(4);

            GUI.backgroundColor = new Color(0.3f, 0.6f, 0.9f);
            if (GUILayout.Button("Associate First Tile Only  (test)", GUILayout.Height(26)))
                RunAssociation(firstTileOnly: true);

            GUI.backgroundColor = prev;
        }

        // ── Preview (read-only) ────────────────────────────────────────────────

        private void RunPreview()
        {
            _previewCounts     = new Dictionary<string, int>();
            _previewUnassigned = 0;

            foreach (var rec in _dataset.tiles)
                _previewCounts[rec.tileId] = 0;

            // Collect DIRECT CHILDREN only — each is one building unit
            var buildings = GetDirectChildren(_buildingsRoot);
            _previewTotal = buildings.Count;

            // Build O(1) lookup once; far faster than linear search per building
            BuildTileLookup();

            const int kInterval = 500;   // update bar every N buildings to amortise overhead
            bool      cancelled = false;

            try
            {
                for (int i = 0; i < buildings.Count; i++)
                {
                    if (i % kInterval == 0)
                    {
                        float pct = _previewTotal > 0 ? (float)i / _previewTotal : 1f;
                        cancelled = EditorUtility.DisplayCancelableProgressBar(
                            "ZG Connect — Building Associator",
                            $"Classifying…  {i:N0} / {_previewTotal:N0} buildings",
                            pct);
                        if (cancelled) break;
                    }

                    string tileId = FindTileId(buildings[i]);
                    if (tileId != null)
                        _previewCounts[tileId]++;
                    else
                        _previewUnassigned++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (cancelled) return;   // don't display partial results

            _hasPreviewed = true;
            Repaint();
        }

        // ── Association ────────────────────────────────────────────────────────

        private void RunAssociation(bool firstTileOnly = false)
        {
            // Snapshot the direct children BEFORE any reparenting
            var buildings = GetDirectChildren(_buildingsRoot);
            if (buildings.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "ZG Connect",
                    "No direct children found under the buildings root.",
                    "OK");
                return;
            }

            // Build O(1) lookup once
            BuildTileLookup();

            // ── Phase 1 — Classify each building by tile (cancellable) ──────────
            var tileGroups = new Dictionary<string, List<GameObject>>();
            var unassigned = new List<GameObject>();
            bool cancelled = false;
            const int kInterval = 500;
            int       total     = buildings.Count;

            try
            {
                for (int i = 0; i < total; i++)
                {
                    if (i % kInterval == 0)
                    {
                        // First half of progress bar (0 → 0.5) covers classification
                        float pct = total > 0 ? (float)i / total * 0.5f : 0f;
                        cancelled = EditorUtility.DisplayCancelableProgressBar(
                            "ZG Connect — Building Associator",
                            $"Classifying…  {i:N0} / {total:N0} buildings",
                            pct);
                        if (cancelled) break;
                    }

                    string tileId = FindTileId(buildings[i]);
                    if (tileId == null) { unassigned.Add(buildings[i]); continue; }
                    if (!tileGroups.ContainsKey(tileId))
                        tileGroups[tileId] = new List<GameObject>();
                    tileGroups[tileId].Add(buildings[i]);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (cancelled) return;

            // ── Phase 2 — Create per-tile parents and reparent (non-cancellable) ─
            // Undo operations are already partially registered — do not allow early exit.
            // Never move or replace _buildingsRoot — its transform defines alignment.
            Undo.RecordObject(_buildingsRoot.transform, "Associate Buildings");

            // Determine how many tiles will actually be wired (progress denominator)
            int tilesWithBuildings = 0;
            foreach (var rec in _dataset.tiles)
                if (tileGroups.TryGetValue(rec.tileId, out var g) && g.Count > 0)
                    tilesWithBuildings++;
            if (firstTileOnly) tilesWithBuildings = Mathf.Min(tilesWithBuildings, 1);

            int wiredTiles = 0;

            try
            {
                foreach (var rec in _dataset.tiles)
                {
                    if (!tileGroups.TryGetValue(rec.tileId, out var group) || group.Count == 0)
                        continue;

                    if (firstTileOnly && wiredTiles >= 1) break;

                    // Second half of progress bar (0.5 → 1.0) covers reparenting
                    float wirePct = tilesWithBuildings > 0
                        ? 0.5f + (float)(wiredTiles + 1) / tilesWithBuildings * 0.5f
                        : 1f;
                    EditorUtility.DisplayProgressBar(
                        "ZG Connect — Building Associator",
                        $"Wiring tile {rec.tileId}  ({wiredTiles + 1} / {tilesWithBuildings})",
                        wirePct);

                    string    groupName = $"TileBuildings_{rec.tileId}";
                    Transform existing  = _buildingsRoot.transform.Find(groupName);

                    GameObject tileParent;
                    if (existing != null)
                    {
                        tileParent = existing.gameObject;
                    }
                    else
                    {
                        tileParent = new GameObject(groupName);
                        Undo.RegisterCreatedObjectUndo(tileParent, "Associate Buildings");
                        // Parent under root, keeping world position (worldPositionStays = true)
                        Undo.RecordObject(tileParent.transform, "Associate Buildings");
                        tileParent.transform.SetParent(_buildingsRoot.transform, true);
                    }

                    // Reparent each building unit under this tile parent.
                    // worldPositionStays = true preserves world position exactly.
                    foreach (GameObject bldg in group)
                    {
                        Undo.RecordObject(bldg.transform, "Associate Buildings");
                        bldg.transform.SetParent(tileParent.transform, true);
                    }

                    // Wire into dataset
                    Undo.RecordObject(_dataset, "Associate Buildings");
                    rec.buildingsSceneObject = tileParent;

                    if (_disableAfterWiring)
                        tileParent.SetActive(false);

                    wiredTiles++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            // ── Handle unassigned buildings (skipped in first-tile-only mode) ──
            if (unassigned.Count > 0 && !firstTileOnly)
            {
                const string unassignedName = "TileBuildings_Unassigned";
                Transform existingUnassigned = _buildingsRoot.transform.Find(unassignedName);
                GameObject unassignedParent  = existingUnassigned != null
                    ? existingUnassigned.gameObject
                    : new GameObject(unassignedName);

                if (existingUnassigned == null)
                {
                    Undo.RegisterCreatedObjectUndo(unassignedParent, "Associate Buildings");
                    unassignedParent.transform.SetParent(_buildingsRoot.transform, true);
                }

                foreach (GameObject bldg in unassigned)
                {
                    Undo.RecordObject(bldg.transform, "Associate Buildings");
                    bldg.transform.SetParent(unassignedParent.transform, true);
                }

                Debug.LogWarning(
                    $"[ZGConnect] {unassigned.Count} building(s) fell outside all tile bounds " +
                    "and were placed under 'TileBuildings_Unassigned'. " +
                    "Verify the buildings root is correctly aligned with the terrain.");
            }

            // ── Optional terrain snap ──────────────────────────────────────────
            if (_snapToTerrain && !firstTileOnly)
                SnapBuildingsToTerrain(tileGroups);

            EditorUtility.SetDirty(_dataset);
            AssetDatabase.SaveAssets();

            _hasPreviewed = false;

            string title   = firstTileOnly ? "ZG Connect — Test Run" : "ZG Connect — Association Complete";
            string message = firstTileOnly
                ? "First tile wired successfully.\n\nCheck positions and hierarchy in the Scene view, " +
                  "then Undo (Ctrl+Z) before running the full association."
                : $"Wired {wiredTiles} tile group(s).\n" +
                  (unassigned.Count > 0
                      ? $"Warning: {unassigned.Count} building(s) are unassigned.\n"
                      : "") +
                  "\nInspect ZGConnectDataset.asset to verify buildingsSceneObject references.";

            EditorUtility.DisplayDialog(title, message, "OK");
        }

        // ── Terrain snap ───────────────────────────────────────────────────────

        private static void SnapBuildingsToTerrain(Dictionary<string, List<GameObject>> tileGroups)
        {
            int snapped = 0;
            int missed  = 0;

            foreach (var group in tileGroups.Values)
            {
                foreach (GameObject bldg in group)
                {
                    Bounds  bounds = GetCompoundWorldBounds(bldg);
                    Vector3 origin = new Vector3(bounds.center.x,
                                                 bounds.max.y + 10f,
                                                 bounds.center.z);

                    if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit,
                        bounds.size.y + 200f,
                        Physics.DefaultRaycastLayers,
                        QueryTriggerInteraction.Ignore))
                    {
                        if (hit.collider is TerrainCollider)
                        {
                            Undo.RecordObject(bldg.transform, "Snap to Terrain");
                            float pivotToBottom  = bldg.transform.position.y - bounds.min.y;
                            Vector3 pos          = bldg.transform.position;
                            pos.y                = hit.point.y + pivotToBottom;
                            bldg.transform.position = pos;
                            snapped++;
                        }
                    }
                    else
                    {
                        missed++;
                    }
                }
            }

            if (missed > 0)
                Debug.LogWarning(
                    $"[ZGConnect] Terrain snap: {snapped} snapped, {missed} missed " +
                    "(no TerrainCollider hit — ensure terrain colliders are active).");
            else
                Debug.Log($"[ZGConnect] Terrain snap: {snapped} buildings snapped.");
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds an O(1) lookup from integer grid coordinates to <see cref="CityTileRecord"/>.
        /// Must be called once before classifying buildings in a Preview or Association run.
        ///
        /// Key: <c>(RoundToInt(origin.x / tileSize), RoundToInt(origin.z / tileSize))</c>.
        /// Lookup: <c>(FloorToInt(cx / tileSize), FloorToInt(cz / tileSize))</c>.
        ///
        /// Both resolve to the same integer k whenever tile origins are exact multiples
        /// of <c>tileSizeMeters</c>, which is guaranteed by the ZG Connect importer.
        /// </summary>
        private void BuildTileLookup()
        {
            int tileSize = _dataset.tileSizeMeters;
            _tileLookup  = new Dictionary<(int, int), CityTileRecord>(_dataset.tiles.Count);

            foreach (CityTileRecord rec in _dataset.tiles)
            {
                int gx = Mathf.RoundToInt(rec.unityPosition.x / tileSize);
                int gz = Mathf.RoundToInt(rec.unityPosition.z / tileSize);
                _tileLookup[(gx, gz)] = rec;
            }
        }

        /// <summary>
        /// Returns the direct children of root as a snapshot list.
        /// Each direct child is treated as one indivisible building unit —
        /// its entire internal hierarchy (sub-meshes, LOD nodes, etc.) moves as a whole.
        /// </summary>
        private static List<GameObject> GetDirectChildren(GameObject root)
        {
            var result = new List<GameObject>(root.transform.childCount);
            for (int i = 0; i < root.transform.childCount; i++)
                result.Add(root.transform.GetChild(i).gameObject);
            return result;
        }

        /// <summary>
        /// Returns the tileId whose XZ bounds contain the building's world-space centre,
        /// or <c>null</c> if no tile contains it.
        ///
        /// Uses the O(1) <see cref="_tileLookup"/> when it has been built by
        /// <see cref="BuildTileLookup"/>, falling back to a linear search otherwise.
        /// Uses compound bounds (all renderers in children) for accurate placement.
        /// </summary>
        private string FindTileId(GameObject bldg)
        {
            Bounds bounds = GetCompoundWorldBounds(bldg);
            float  cx     = bounds.center.x;
            float  cz     = bounds.center.z;
            int    size   = _dataset.tileSizeMeters;

            // ── O(1) path (preferred) ──────────────────────────────────────────
            if (_tileLookup != null)
            {
                int gx = Mathf.FloorToInt(cx / size);
                int gz = Mathf.FloorToInt(cz / size);
                return _tileLookup.TryGetValue((gx, gz), out CityTileRecord fast)
                    ? fast.tileId
                    : null;
            }

            // ── Linear fallback (when called without BuildTileLookup) ──────────
            foreach (CityTileRecord rec in _dataset.tiles)
            {
                float left   = rec.unityPosition.x;
                float bottom = rec.unityPosition.z;

                if (cx >= left && cx < left + size &&
                    cz >= bottom && cz < bottom + size)
                    return rec.tileId;
            }

            return null;
        }

        /// <summary>
        /// Returns a world-space AABB that encloses the GameObject and all of its
        /// children by unioning every Renderer.bounds found in the hierarchy.
        /// Falls back to the pivot position if no renderers exist.
        /// </summary>
        private static Bounds GetCompoundWorldBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>(includeInactive: true);

            if (renderers.Length == 0)
                return new Bounds(go.transform.position, Vector3.zero);

            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                b.Encapsulate(renderers[i].bounds);

            return b;
        }
    }
}
