using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace ZGConnect.Editor
{
    /// <summary>
    /// Exports <see cref="CityDataset"/> tiles to tiles.json for the OSM vegetation mask
    /// Python pipeline (EPSG:3765 bounds per tile).
    /// </summary>
    public static class ZGConnectVegetationTilesExporter
    {
        public enum TileFilter
        {
            AllTiles,
            WithTerrainDataOnly
        }

        public struct ExportOptions
        {
            public int       MaskResolution;
            public TileFilter Filter;
            public bool      UseRegionFilter;
            public int       RegionMinE;
            public int       RegionMaxE;
            public int       RegionMinN;
            public int       RegionMaxN;
        }

        [Serializable]
        public class VegetationTilesExportRoot
        {
            [JsonProperty("crs")] public string crs = "EPSG:3765";

            [JsonProperty("tile_size_meters")]
            public int tile_size_meters = 1000;

            [JsonProperty("resolution")]
            public int resolution = 1024;

            [JsonProperty("tiles")]
            public List<VegetationTileExportEntry> tiles = new List<VegetationTileExportEntry>();
        }

        [Serializable]
        public class VegetationTileExportEntry
        {
            [JsonProperty("tile_id")] public string tile_id;
            [JsonProperty("left")]    public int left;
            [JsonProperty("bottom")]  public int bottom;
            [JsonProperty("right")]   public int right;
            [JsonProperty("top")]     public int top;
        }

        public static int CountExportableTiles(CityDataset dataset, ExportOptions options)
        {
            if (dataset?.tiles == null) return 0;
            int n = 0;
            foreach (var rec in dataset.tiles)
            {
                if (ShouldInclude(rec, dataset, options))
                    n++;
            }
            return n;
        }

        public static bool Export(CityDataset dataset, string outputPath, ExportOptions options, out string error)
        {
            error = null;
            if (dataset == null)
            {
                error = "CityDataset is null.";
                return false;
            }

            if (dataset.tiles == null || dataset.tiles.Count == 0)
            {
                error = "Dataset has no tiles.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                error = "Output path is empty.";
                return false;
            }

            options.MaskResolution = Mathf.Clamp(options.MaskResolution, 64, 8192);

            var root = new VegetationTilesExportRoot
            {
                crs               = ResolveCrs(dataset),
                tile_size_meters  = Mathf.Max(1, dataset.tileSizeMeters),
                resolution        = options.MaskResolution
            };

            foreach (CityTileRecord rec in dataset.tiles)
            {
                if (!ShouldInclude(rec, dataset, options))
                    continue;

                if (!TryGetTileBounds(rec, root.tile_size_meters,
                        out int left, out int bottom, out int right, out int top))
                {
                    Debug.LogWarning(
                        $"[ZGConnect] Skipping tile '{rec?.tileId}': cannot resolve EPSG bounds.");
                    continue;
                }

                root.tiles.Add(new VegetationTileExportEntry
                {
                    tile_id = string.IsNullOrEmpty(rec.tileId)
                        ? $"{left}_{bottom}"
                        : rec.tileId,
                    left    = left,
                    bottom  = bottom,
                    right   = right,
                    top     = top
                });
            }

            if (root.tiles.Count == 0)
            {
                error = "No tiles matched the current filter.";
                return false;
            }

            root.tiles.Sort((a, b) =>
            {
                int c = a.bottom.CompareTo(b.bottom);
                return c != 0 ? c : a.left.CompareTo(b.left);
            });

            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string json = JsonConvert.SerializeObject(root, Formatting.Indented);
            File.WriteAllText(outputPath, json, Encoding.UTF8);
            AssetDatabase.Refresh();
            return true;
        }

        public static bool ShouldInclude(CityTileRecord rec, CityDataset dataset, ExportOptions options)
        {
            if (rec == null || string.IsNullOrEmpty(rec.tileId))
                return false;

            if (options.Filter == TileFilter.WithTerrainDataOnly && !HasTerrainData(rec))
                return false;

            if (!TryGetTileBounds(rec, dataset.tileSizeMeters,
                    out int left, out int bottom, out int right, out int top))
                return false;

            if (options.UseRegionFilter)
            {
                bool overlaps =
                    left   < options.RegionMaxE &&
                    right  > options.RegionMinE &&
                    bottom < options.RegionMaxN &&
                    top    > options.RegionMinN;
                if (!overlaps)
                    return false;
            }

            return true;
        }

        public static bool HasTerrainData(CityTileRecord rec) =>
            rec.sceneObject != null
            || rec.terrainData != null
            || (rec.terrainDataRef != null && rec.terrainDataRef.RuntimeKeyIsValid());

        public static string ResolveCrs(CityDataset dataset) =>
            string.IsNullOrWhiteSpace(dataset?.crs) ? "EPSG:3765" : dataset.crs.Trim();

        public static bool TryGetTileBounds(CityTileRecord rec, int tileSize,
            out int left, out int bottom, out int right, out int top)
        {
            left = bottom = right = top = 0;

            if (rec.right > rec.left && rec.top > rec.bottom)
            {
                left   = rec.left;
                bottom = rec.bottom;
                right  = rec.right;
                top    = rec.top;
                return true;
            }

            return TryParseTileId(rec.tileId, tileSize, out left, out bottom, out right, out top);
        }

        /// <summary>tileId format: "{left}_{bottom}" in EPSG metres.</summary>
        public static bool TryParseTileId(string tileId, int tileSize,
            out int left, out int bottom, out int right, out int top)
        {
            left = bottom = right = top = 0;
            if (string.IsNullOrEmpty(tileId)) return false;

            string[] parts = tileId.Split('_');
            if (parts.Length != 2) return false;
            if (!int.TryParse(parts[0], out left) || !int.TryParse(parts[1], out bottom))
                return false;

            right = left + tileSize;
            top   = bottom + tileSize;
            return true;
        }
    }

    public class ZGConnectVegetationTilesExporterWindow : EditorWindow
    {
        private CityDataset _dataset;
        private string      _outputPath = "";
        private int         _resolution = 1024;
        private ZGConnectVegetationTilesExporter.TileFilter _filter =
            ZGConnectVegetationTilesExporter.TileFilter.WithTerrainDataOnly;

        private bool _useRegionFilter;
        private int  _regionMinE = 455000;
        private int  _regionMaxE = 457000;
        private int  _regionMinN = 5071000;
        private int  _regionMaxN = 5073000;

        private Vector2 _scroll;

        [MenuItem("ZG Connect/Vegetation Tiles Exporter")]
        public static void ShowWindow()
        {
            var w = GetWindow<ZGConnectVegetationTilesExporterWindow>("Vegetation Tiles JSON");
            w.minSize = new Vector2(420, 420);
        }

        [MenuItem("Assets/ZG Connect/Export Vegetation tiles.json", false, 2100)]
        private static void ExportFromSelection()
        {
            var dataset = Selection.activeObject as CityDataset;
            if (dataset == null) return;

            string dir  = Path.GetDirectoryName(AssetDatabase.GetAssetPath(dataset));
            string path = EditorUtility.SaveFilePanel(
                "Export vegetation tiles.json",
                string.IsNullOrEmpty(dir) ? "Assets" : dir,
                "vegetation_tiles.json",
                "json");

            if (string.IsNullOrEmpty(path)) return;

            var options = new ZGConnectVegetationTilesExporter.ExportOptions
            {
                MaskResolution  = 1024,
                Filter          = ZGConnectVegetationTilesExporter.TileFilter.WithTerrainDataOnly,
                UseRegionFilter = false
            };

            if (ZGConnectVegetationTilesExporter.Export(dataset, path, options, out string err))
            {
                EditorUtility.DisplayDialog(
                    "Vegetation tiles exported",
                    $"Wrote {ZGConnectVegetationTilesExporter.CountExportableTiles(dataset, options)} tile(s) to:\n{path}",
                    "OK");
                EditorUtility.RevealInFinder(path);
            }
            else
                EditorUtility.DisplayDialog("Export failed", err, "OK");
        }

        [MenuItem("Assets/ZG Connect/Export Vegetation tiles.json", true, 2100)]
        private static bool ExportFromSelectionValidate() => Selection.activeObject is CityDataset;

        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("ZG Connect — Vegetation Tiles Exporter", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Optional: export a slim tiles.json for the Python script.\n" +
                "Preferred input for Python is the dataset heightmap metadata.json " +
                "(e.g. _dataset/hightmaps_raw_1km_1025/metadata.json) — same file as Import Manager.\n" +
                "python Tools/osm_vegetation_masks.py --metadata <path> --out <folder>\n" +
                "One regional OSM download; writes masks + ../road_segments/{tile}_roads.json",
                MessageType.Info);

            EditorGUILayout.Space(4);
            _dataset = (CityDataset)EditorGUILayout.ObjectField(
                "City Dataset", _dataset, typeof(CityDataset), false);

            if (_dataset == null)
            {
                EditorGUILayout.HelpBox("Assign the CityDataset asset.", MessageType.Warning);
                return;
            }

            _resolution = EditorGUILayout.IntSlider(
                new GUIContent("Mask resolution", "Pixel size (width = height) for each exported PNG."),
                _resolution, 256, 4096);

            _filter = (ZGConnectVegetationTilesExporter.TileFilter)EditorGUILayout.EnumPopup(
                "Tile filter", _filter);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("EPSG region filter (optional)", EditorStyles.boldLabel);
            _useRegionFilter = EditorGUILayout.Toggle("Limit to region", _useRegionFilter);

            using (new EditorGUI.DisabledScope(!_useRegionFilter))
            {
                _regionMinE = EditorGUILayout.IntField("Min E (left)", _regionMinE);
                _regionMaxE = EditorGUILayout.IntField("Max E (right)", _regionMaxE);
                _regionMinN = EditorGUILayout.IntField("Min N (bottom)", _regionMinN);
                _regionMaxN = EditorGUILayout.IntField("Max N (top)", _regionMaxN);
            }

            if (GUILayout.Button("Sync region from Import Manager"))
                SyncRegionFromBridge();

            EditorGUILayout.Space(4);
            DrawOutputPath();
            EditorGUILayout.Space(6);

            var options = BuildOptions();
            int count     = ZGConnectVegetationTilesExporter.CountExportableTiles(_dataset, options);

            EditorGUILayout.LabelField(
                $"Tiles to export: {count}  ·  CRS: {ZGConnectVegetationTilesExporter.ResolveCrs(_dataset)}  ·  " +
                $"size: {_dataset.tileSizeMeters} m",
                EditorStyles.miniLabel);

            using (new EditorGUI.DisabledScope(count == 0 || string.IsNullOrEmpty(_outputPath)))
            {
                if (GUILayout.Button($"Export tiles.json ({count} tiles)", GUILayout.Height(32)))
                    RunExport(options, count);
            }

            if (count > 0 && !string.IsNullOrEmpty(_outputPath))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Python (after export)", EditorStyles.boldLabel);
                string py = GetSuggestedPythonCommand();
                EditorGUILayout.SelectableLabel(py, EditorStyles.textField, GUILayout.Height(36));
            }

            if (count > 0)
            {
                EditorGUILayout.Space(4);
                DrawTilePreview(options);
            }
        }

        private ZGConnectVegetationTilesExporter.ExportOptions BuildOptions() =>
            new ZGConnectVegetationTilesExporter.ExportOptions
            {
                MaskResolution  = _resolution,
                Filter          = _filter,
                UseRegionFilter = _useRegionFilter,
                RegionMinE      = _regionMinE,
                RegionMaxE      = _regionMaxE,
                RegionMinN      = _regionMinN,
                RegionMaxN      = _regionMaxN
            };

        private void DrawOutputPath()
        {
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _outputPath = EditorGUILayout.TextField("tiles.json path", _outputPath);
                if (GUILayout.Button("…", GUILayout.Width(28)))
                    BrowseOutputPath();
            }

            if (string.IsNullOrEmpty(_outputPath) && _dataset != null)
            {
                string assetPath = AssetDatabase.GetAssetPath(_dataset);
                string dir       = string.IsNullOrEmpty(assetPath)
                    ? "Assets/Generated/ZGConnect"
                    : Path.Combine(Path.GetDirectoryName(assetPath) ?? "Assets", "vegetation_tiles.json");
                _outputPath = dir.Replace('\\', '/');
            }
        }

        private void BrowseOutputPath()
        {
            string start = string.IsNullOrEmpty(_outputPath)
                ? Application.dataPath
                : Path.GetDirectoryName(_outputPath);

            string picked = EditorUtility.SaveFilePanel(
                "Save vegetation tiles.json",
                start ?? Application.dataPath,
                "vegetation_tiles.json",
                "json");

            if (!string.IsNullOrEmpty(picked))
                _outputPath = picked;
        }

        private void SyncRegionFromBridge()
        {
            if (ZGConnectImportRegionBridge.GetRegionEpsg == null)
            {
                EditorUtility.DisplayDialog(
                    "Import Manager not open",
                    "Open Tools → ZG Connect → Dataset Import Manager and set a region, " +
                    "or enter EPSG bounds manually.",
                    "OK");
                return;
            }

            var (minE, maxE, minN, maxN) = ZGConnectImportRegionBridge.GetRegionEpsg();
            if (maxE <= minE || maxN <= minN)
            {
                EditorUtility.DisplayDialog("No region", "Import Manager has no valid region.", "OK");
                return;
            }

            _regionMinE = minE;
            _regionMaxE = maxE;
            _regionMinN = minN;
            _regionMaxN = maxN;
            _useRegionFilter = true;
            Repaint();
        }

        private void RunExport(ZGConnectVegetationTilesExporter.ExportOptions options, int count)
        {
            if (ZGConnectVegetationTilesExporter.Export(_dataset, _outputPath, options, out string err))
            {
                EditorUtility.DisplayDialog(
                    "Export complete",
                    $"Exported {count} tile(s) to:\n{_outputPath}",
                    "OK");
                EditorUtility.RevealInFinder(_outputPath);
            }
            else
                EditorUtility.DisplayDialog("Export failed", err, "OK");
        }

        private string GetSuggestedPythonCommand()
        {
            string outDir = Path.Combine(
                Path.GetDirectoryName(_outputPath) ?? "Assets/Generated/ZGConnect",
                "VegetationMasks").Replace('\\', '/');

            // Typical dataset layout next to the Unity project
            string hmMeta = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "..", "_dataset",
                    "hightmaps_raw_1km_1025", "metadata.json"))
                .Replace('\\', '/');

            return $"python Tools/osm_vegetation_masks.py --metadata \"{hmMeta}\" --out \"{outDir}\"";
        }

        private void DrawTilePreview(ZGConnectVegetationTilesExporter.ExportOptions options)
        {
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MaxHeight(140));

            int shown = 0;
            foreach (CityTileRecord rec in _dataset.tiles)
            {
                if (!ZGConnectVegetationTilesExporter.ShouldInclude(rec, _dataset, options))
                    continue;

                ZGConnectVegetationTilesExporter.TryGetTileBounds(
                    rec, _dataset.tileSizeMeters,
                    out int left, out int bottom, out int right, out int top);

                bool hasMask = rec.vegetationMask != null;
                EditorGUILayout.LabelField(
                    $"  {rec.tileId}  [{left},{bottom}]–[{right},{top}]" +
                    (hasMask ? "  ✓ mask" : ""),
                    EditorStyles.miniLabel);

                if (++shown >= 50)
                {
                    EditorGUILayout.LabelField("  …", EditorStyles.miniLabel);
                    break;
                }
            }

            EditorGUILayout.EndScrollView();
        }
    }
}
