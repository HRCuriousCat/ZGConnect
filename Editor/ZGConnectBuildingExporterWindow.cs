using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ZGConnect.Editor
{
    /// <summary>
    /// Exports per-tile building groups from the current scene to GLB files suitable
    /// for inclusion in a ZG Connect dataset folder.
    ///
    /// Prerequisites:
    ///   1. Buildings must already be associated with tiles via the Building Associator
    ///      (Tools → ZG Connect → Building Associator).
    ///   2. UnityGLTF package must be installed:
    ///      Package Manager → Add by name → com.khronos.unitygltf
    ///
    /// Output per wired tile (written to the chosen Output Folder):
    ///   buildings_{tileId}.glb    — full mesh hierarchy; each building is a named child node
    ///   buildings_{tileId}.json   — building names + local transforms (optional; for enrichment)
    ///
    /// Building names are preserved verbatim from the scene GameObject names.
    /// The numeric ID embedded in each name (e.g. "building_12345") is the key used later
    /// to join per-building attribute data (address, floors, surface area, etc.).
    ///
    /// ── Why a Buildings Root field exists ────────────────────────────────────────
    /// Unity cannot serialize references from a ScriptableObject asset to a scene
    /// GameObject — the reference is valid in memory but is lost on every domain reload
    /// (i.e. every script recompilation). The exporter therefore resolves building groups
    /// by name (TileBuildings_{tileId}) under the supplied Buildings Root rather than
    /// relying on CityTileRecord.buildingsSceneObject, which may be null after reload.
    /// </summary>
    public class ZGConnectBuildingExporterWindow : EditorWindow
    {
        // ── Inputs ─────────────────────────────────────────────────────────────
        private CityDataset _dataset;
        private GameObject  _buildingsRoot;       // fallback for stale dataset references
        private string      _exportFolder  = "";
        private bool        _writeMetaJson = true;
        private bool        _skipExisting  = true;

        // ── UI state ───────────────────────────────────────────────────────────
        private Vector2 _tileListScroll;

        // Cached existing-file state — recomputed when the folder path changes or
        // after a completed export run. Avoids File.Exists spam every OnGUI frame.
        private string          _cachedExistingFolder;
        private int             _cachedExistingCount;
        private HashSet<string> _cachedExistingIds = new HashSet<string>();

        // ── UnityGLTF reflection cache ──────────────────────────────────────────
        // The exporter is an optional package (com.khronos.unitygltf).
        // We detect it at runtime so the rest of ZG Connect compiles without it.
        private static bool                      _exporterChecked;
        private static System.Type               _exporterType;
        private static ConstructorInfo           _exporterCtor;
        private static MethodInfo                _saveGlbMethod;
        private static System.Type               _exportContextType;

        // ──────────────────────────────────────────────────────────────────────

        [MenuItem("ZG Connect/Building Exporter")]
        public static void ShowWindow()
        {
            var w = GetWindow<ZGConnectBuildingExporterWindow>("ZG Connect — Exporter");
            w.minSize = new Vector2(440, 540);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("ZG Connect — Building Exporter", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            DrawInputs();
            if (_dataset == null) return;

            var wiredTiles = CollectWiredTiles();

            EditorGUILayout.Space(6);
            DrawStatus(wiredTiles);
            EditorGUILayout.Space(6);
            DrawOutputFolder();
            EditorGUILayout.Space(4);
            DrawOptions();
            EditorGUILayout.Space(8);
            DrawExportButton(wiredTiles);

            if (wiredTiles.Count > 0)
            {
                EditorGUILayout.Space(6);
                DrawTileList(wiredTiles);
            }
        }

        // ── Sections ───────────────────────────────────────────────────────────

        private void DrawInputs()
        {
            EditorGUILayout.LabelField("Inputs", EditorStyles.boldLabel);

            _dataset = (CityDataset)EditorGUILayout.ObjectField(
                "City Dataset", _dataset, typeof(CityDataset), false);

            _buildingsRoot = (GameObject)EditorGUILayout.ObjectField(
                "Buildings Root", _buildingsRoot, typeof(GameObject), true);

            if (_dataset == null)
            {
                EditorGUILayout.HelpBox(
                    "Assign the CityDataset asset. Buildings must already be associated " +
                    "via the Building Associator before exporting.",
                    MessageType.Info);
                return;
            }

            if (_buildingsRoot == null)
            {
                EditorGUILayout.HelpBox(
                    "Assign the Buildings Root — the same parent GameObject used in the " +
                    "Building Associator (e.g. 'ZG Connect Buildings').\n\n" +
                    "Unity cannot save scene-object references inside .asset files, so " +
                    "dataset references are lost on every script recompilation. The exporter " +
                    "finds tile groups by name (TileBuildings_{tileId}) under this root instead.",
                    MessageType.Warning);
            }
        }

        private void DrawStatus(List<CityTileRecord> wiredTiles)
        {
            EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);

            int total      = _dataset.tiles.Count;
            int wired      = wiredTiles.Count;
            int unwired    = total - wired;
            int totalBldgs = 0;

            foreach (var rec in wiredTiles)
            {
                GameObject g = ResolveGroup(rec);
                if (g != null) totalBldgs += g.transform.childCount;
            }

            EditorGUILayout.LabelField(
                $"Tiles with buildings (ready to export):  {wired}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField(
                $"Total buildings across all tiles:  {totalBldgs:N0}", EditorStyles.miniLabel);

            if (unwired > 0)
                EditorGUILayout.LabelField(
                    $"Tiles without buildings (will be skipped):  {unwired}",
                    EditorStyles.miniLabel);

            // Show already-exported count when a folder is selected
            if (!string.IsNullOrEmpty(_exportFolder) && wired > 0)
            {
                int existing = CountExistingExports(wiredTiles);
                if (existing > 0)
                    EditorGUILayout.LabelField(
                        $"Already exported in output folder:  {existing}  " +
                        $"({wired - existing} remaining)",
                        EditorStyles.miniLabel);
            }

            if (wired == 0)
            {
                string hint = _buildingsRoot == null
                    ? "Assign the Buildings Root above so the exporter can locate tile groups by name."
                    : $"No children named 'TileBuildings_{{tileId}}' found under '{_buildingsRoot.name}'. " +
                      "Run the Building Associator first (Tools → ZG Connect → Building Associator).";

                EditorGUILayout.HelpBox(hint, MessageType.Warning);
            }
        }

        private void DrawOutputFolder()
        {
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                _exportFolder = EditorGUILayout.TextField("Output Folder", _exportFolder);

                if (GUILayout.Button("…", GUILayout.Width(28)))
                {
                    string start = string.IsNullOrEmpty(_exportFolder)
                        ? Application.dataPath
                        : _exportFolder;

                    string picked = EditorUtility.OpenFolderPanel(
                        "Select Export Folder", start, "");

                    if (!string.IsNullOrEmpty(picked))
                    {
                        _exportFolder = picked;
                        GUI.FocusControl(null);
                        Repaint();
                    }
                }
            }

            if (!string.IsNullOrEmpty(_exportFolder))
                EditorGUILayout.LabelField(
                    "One .glb (and optional .json) will be written per tile.",
                    EditorStyles.miniLabel);
        }

        private void DrawOptions()
        {
            EditorGUILayout.LabelField("Options", EditorStyles.boldLabel);

            _writeMetaJson = EditorGUILayout.Toggle(
                new GUIContent(
                    "Write metadata JSON per tile",
                    "Saves a companion .json alongside each .glb listing every building's " +
                    "name and local transform. The JSON is the bridge for later per-building " +
                    "attribute enrichment (address, floors, surface area, etc.)."),
                _writeMetaJson);

            _skipExisting = EditorGUILayout.Toggle(
                new GUIContent(
                    "Skip already exported tiles",
                    "If buildings_{tileId}.glb already exists in the output folder, " +
                    "that tile is skipped entirely. Use this to resume an interrupted " +
                    "export run without re-processing tiles that completed successfully."),
                _skipExisting);
        }

        private void DrawExportButton(List<CityTileRecord> wiredTiles)
        {
            EnsureExporterChecked();

            if (_saveGlbMethod == null)
            {
                EditorGUILayout.HelpBox(
                    "UnityGLTF package not found.\n" +
                    "Install it via Package Manager → Add package by name:\n" +
                    "com.khronos.unitygltf",
                    MessageType.Error);
                return;
            }

            bool canExport = wiredTiles.Count > 0 && !string.IsNullOrEmpty(_exportFolder);

            int toExport = canExport && _skipExisting
                ? wiredTiles.Count - CountExistingExports(wiredTiles)
                : wiredTiles.Count;

            Color prev = GUI.backgroundColor;
            if (canExport && toExport > 0) GUI.backgroundColor = new Color(0.3f, 0.75f, 0.3f);

            using (new EditorGUI.DisabledScope(!canExport || toExport == 0))
            {
                string label;
                if (!canExport)
                    label = "Export  (assign inputs and output folder first)";
                else if (toExport == 0)
                    label = "All tiles already exported  ✓";
                else
                    label = $"Export {toExport} Tile(s) to GLB";

                if (GUILayout.Button(label, GUILayout.Height(36)))
                    RunExport(wiredTiles);
            }

            GUI.backgroundColor = prev;
        }

        private void DrawTileList(List<CityTileRecord> wiredTiles)
        {
            EditorGUILayout.LabelField(
                $"Tiles queued for export ({wiredTiles.Count})", EditorStyles.boldLabel);

            _tileListScroll = EditorGUILayout.BeginScrollView(
                _tileListScroll, GUILayout.MaxHeight(180));

            // Warm the cache before iterating so File.Exists runs once, not per row
            bool checkExisting = _skipExisting && !string.IsNullOrEmpty(_exportFolder);
            if (checkExisting) CountExistingExports(wiredTiles);

            var doneStyle = new GUIStyle(EditorStyles.miniLabel)
                { normal = { textColor = new Color(0.45f, 0.75f, 0.45f) } };

            foreach (var rec in wiredTiles)
            {
                bool   exists = checkExisting && TileAlreadyExported(rec.tileId);
                int    count  = 0;

                if (!exists)
                {
                    GameObject g = ResolveGroup(rec);
                    if (g != null) count = g.transform.childCount;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"  {rec.tileId}", GUILayout.Width(220));

                    if (exists)
                        EditorGUILayout.LabelField("✓ done", doneStyle);
                    else
                        EditorGUILayout.LabelField($"{count:N0} buildings", EditorStyles.miniLabel);
                }
            }

            EditorGUILayout.EndScrollView();
        }

        // ── Export ─────────────────────────────────────────────────────────────

        private void RunExport(List<CityTileRecord> wiredTiles)
        {
            if (!Directory.Exists(_exportFolder))
                Directory.CreateDirectory(_exportFolder);

            int total    = wiredTiles.Count;
            int exported = 0;
            int skipped  = 0;
            int failed   = 0;

            try
            {
                for (int i = 0; i < total; i++)
                {
                    CityTileRecord rec     = wiredTiles[i];
                    string         glbPath = Path.Combine(
                        _exportFolder, $"buildings_{rec.tileId}.glb");

                    // Skip tiles whose GLB already exists (resume after crash / interruption)
                    if (_skipExisting && File.Exists(glbPath))
                    {
                        skipped++;
                        continue;
                    }

                    bool cancelled = EditorUtility.DisplayCancelableProgressBar(
                        "ZG Connect — Building Exporter",
                        $"Exporting tile {rec.tileId}   ({i + 1} / {total})",
                        (float)(i + 1) / total);

                    if (cancelled) break;

                    // Resolve the group: prefer stored reference, fall back to name lookup
                    GameObject group = ResolveGroup(rec);
                    if (group == null)
                    {
                        Debug.LogWarning(
                            $"[ZGConnect] Could not find group for tile {rec.tileId} — skipping. " +
                            "Ensure Buildings Root is assigned and the Building Associator has run.");
                        failed++;
                        continue;
                    }

                    try
                    {
                        ExportTileGlb(group, glbPath);

                        if (_writeMetaJson)
                            WriteMetadataJson(rec, group, glbPath);

                        exported++;
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogError(
                            $"[ZGConnect] Export failed for tile {rec.tileId}: {ex.Message}\n{ex.StackTrace}");
                        failed++;
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                // Invalidate the existing-file cache so the tile list refreshes
                _cachedExistingFolder = null;
            }

            // Refresh Project window if the output is inside Assets/
            if (_exportFolder.Replace('\\', '/').StartsWith(
                    Application.dataPath.Replace('\\', '/')))
                AssetDatabase.Refresh();

            string msg = $"Exported {exported} tile GLB file(s) to:\n{_exportFolder}";
            if (skipped > 0)
                msg += $"\nSkipped {skipped} tile(s) — GLB already present.";
            if (failed > 0)
                msg += $"\n\n⚠ {failed} tile(s) failed — see Console for details.";

            EditorUtility.DisplayDialog("ZG Connect — Export Complete", msg, "OK");
        }

        /// <summary>
        /// Invokes GLTFSceneExporter.SaveGLB for one tile group.
        /// Temporarily activates the group if it was disabled after wiring,
        /// because some exporters skip renderers on inactive objects.
        /// </summary>
        private static void ExportTileGlb(GameObject group, string glbPath) =>
            ZGConnectGlbExportUtility.ExportRoot(group, glbPath);

        /// <summary>
        /// Writes a companion JSON file alongside the exported GLB.
        /// Contains building names and local transforms for later attribute enrichment.
        /// </summary>
        private static void WriteMetadataJson(CityTileRecord rec, GameObject group, string glbPath)
        {
            string    jsonPath   = Path.ChangeExtension(glbPath, ".json");
            Transform tileTf     = group.transform;
            int       childCount = tileTf.childCount;
            Vector3   orig       = rec.unityPosition;

            // Use invariant culture so decimal separator is always '.' regardless of OS locale.
            var ic = System.Globalization.CultureInfo.InvariantCulture;

            var sb = new StringBuilder(childCount * 128);
            sb.AppendLine("{");
            sb.AppendLine($"  \"tileId\": \"{rec.tileId}\",");
            sb.AppendLine($"  \"tileSizeMeters\": {rec.right - rec.left},");
            sb.AppendLine(
                $"  \"tileOriginUnity\": " +
                $"{{ \"x\": {orig.x.ToString("F3", ic)}, \"y\": {orig.y.ToString("F3", ic)}, \"z\": {orig.z.ToString("F3", ic)} }},");
            sb.AppendLine($"  \"buildingCount\": {childCount},");
            sb.AppendLine("  \"buildings\": [");

            for (int i = 0; i < childCount; i++)
            {
                Transform child = tileTf.GetChild(i);
                Vector3   lp    = child.localPosition;
                Vector3   lr    = child.localEulerAngles;
                Vector3   ls    = child.localScale;

                sb.Append("    {");
                sb.Append($" \"name\": \"{EscapeJson(child.name)}\",");
                sb.Append(
                    $" \"localPosition\": " +
                    $"{{ \"x\": {lp.x.ToString("F4", ic)}, \"y\": {lp.y.ToString("F4", ic)}, \"z\": {lp.z.ToString("F4", ic)} }},");
                sb.Append(
                    $" \"localRotation\": " +
                    $"{{ \"x\": {lr.x.ToString("F4", ic)}, \"y\": {lr.y.ToString("F4", ic)}, \"z\": {lr.z.ToString("F4", ic)} }},");
                sb.Append(
                    $" \"localScale\": " +
                    $"{{ \"x\": {ls.x.ToString("F4", ic)}, \"y\": {ls.y.ToString("F4", ic)}, \"z\": {ls.z.ToString("F4", ic)} }}");
                sb.Append(" }");
                if (i < childCount - 1) sb.Append(",");
                sb.AppendLine();
            }

            sb.AppendLine("  ]");
            sb.Append("}");

            File.WriteAllText(jsonPath, sb.ToString(), Encoding.UTF8);
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves the building group GameObject for a tile record using a two-step strategy:
        /// <list type="number">
        ///   <item>Use <c>rec.buildingsSceneObject</c> if it is still a valid in-memory reference.</item>
        ///   <item>Fall back to <c>Transform.Find("TileBuildings_{tileId}")</c> under
        ///         <see cref="_buildingsRoot"/>. This is the reliable path after domain reload,
        ///         since Unity cannot serialize scene-object references in .asset files.</item>
        /// </list>
        /// </summary>
        private GameObject ResolveGroup(CityTileRecord rec)
        {
            if (rec.buildingsSceneObject != null)
                return rec.buildingsSceneObject;

            if (_buildingsRoot != null)
                return _buildingsRoot.transform
                    .Find($"TileBuildings_{rec.tileId}")?.gameObject;

            return null;
        }

        /// <summary>
        /// Returns all tiles for which a building group can be resolved —
        /// either via the stored reference or by name under the buildings root.
        /// </summary>
        private List<CityTileRecord> CollectWiredTiles()
        {
            var list = new List<CityTileRecord>();
            if (_dataset == null) return list;

            foreach (var rec in _dataset.tiles)
                if (ResolveGroup(rec) != null)
                    list.Add(rec);

            return list;
        }

        /// <summary>
        /// Returns how many wired tiles already have a GLB file in the output folder,
        /// and repopulates <see cref="_cachedExistingIds"/> for per-tile queries in the
        /// tile list. The filesystem scan runs only when the folder path changes or after
        /// a completed export run — never on every OnGUI frame.
        /// </summary>
        private int CountExistingExports(List<CityTileRecord> wiredTiles)
        {
            if (string.IsNullOrEmpty(_exportFolder)) return 0;

            if (_exportFolder == _cachedExistingFolder)
                return _cachedExistingCount;

            _cachedExistingFolder = _exportFolder;
            _cachedExistingCount  = 0;
            _cachedExistingIds.Clear();

            foreach (var rec in wiredTiles)
            {
                if (File.Exists(Path.Combine(_exportFolder, $"buildings_{rec.tileId}.glb")))
                {
                    _cachedExistingIds.Add(rec.tileId);
                    _cachedExistingCount++;
                }
            }

            return _cachedExistingCount;
        }

        private bool TileAlreadyExported(string tileId) =>
            _cachedExistingIds.Contains(tileId);

        /// <summary>
        /// Scans loaded assemblies for the UnityGLTF package and caches references to
        /// <c>GLTFSceneExporter</c> and its <c>SaveGLB</c> method.
        /// Safe to call every frame — the actual scan runs only once.
        /// </summary>
        private static void EnsureExporterChecked()
        {
            if (_exporterChecked) return;
            _exporterChecked = true;

            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!asm.GetName().Name.StartsWith("UnityGLTF")) continue;

                System.Type exporterType = asm.GetType("UnityGLTF.GLTFSceneExporter");
                if (exporterType == null) continue;

                // Require: SaveGLB(string path, string fileName)
                MethodInfo saveMethod = exporterType.GetMethod(
                    "SaveGLB",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(string), typeof(string) }, null);
                if (saveMethod == null) continue;

                // Preferred constructor — UnityGLTF 2.x:
                //   GLTFSceneExporter(Transform[] transforms, ExportContext context)
                System.Type contextType = asm.GetType("UnityGLTF.ExportContext");
                ConstructorInfo ctor = contextType != null
                    ? exporterType.GetConstructor(new[] { typeof(Transform[]), contextType })
                    : null;

                // Fallback — older versions:
                //   GLTFSceneExporter(Transform[] transforms)
                if (ctor == null)
                    ctor = exporterType.GetConstructor(new[] { typeof(Transform[]) });

                if (ctor == null) continue;

                _exporterType      = exporterType;
                _exporterCtor      = ctor;
                _saveGlbMethod     = saveMethod;
                _exportContextType = contextType;
                return;
            }
        }

        private static string EscapeJson(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
    }
}
