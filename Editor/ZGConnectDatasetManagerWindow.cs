using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using ZGConnect;
using ZGConnect.RealtimeStreaming;
using ZGConnect.RealtimeStreaming.Editor;

namespace ZGConnect.Editor
{
    public class ZGConnectDatasetManagerWindow : EditorWindow
    {
        // ── Enums ──────────────────────────────────────────────────────────────
        private enum HeightmapResOption { Low_513 = 513, Medium_1025 = 1025 }
        private enum BasemapResOption   { R512 = 512, R1024 = 1024, R2048 = 2048, R4096 = 4096 }
        private enum TileSelectionMode  { All, FirstN, Region }
        private enum RegionInputMode    { GPS, EPSG3765 }
        private enum RegionPreset       { Custom, ZagrebCityBounds, ZagrebCityCenter }

        private static readonly string[] kRegionPresetLabels =
        {
            "Prilagođeno",
            "Granice grada Zagreba",
            "Centar grada"
        };

        private static float kZagrebBoundsMinLat => ZGConnectMapExtent.MinLat;
        private static float kZagrebBoundsMinLon => ZGConnectMapExtent.MinLon;
        private static float kZagrebBoundsMaxLat => ZGConnectMapExtent.MaxLat;
        private static float kZagrebBoundsMaxLon => ZGConnectMapExtent.MaxLon;

        // Urban core: Donji grad, Gornji grad, Cvijetni, okolica Glavnog kolodvora
        private const float kZagrebCenterMinLat = 45.75f;
        private const float kZagrebCenterMinLon = 15.85f;
        private const float kZagrebCenterMaxLat = 45.87f;
        private const float kZagrebCenterMaxLon = 16.10f;

        // ── Discovered source types ────────────────────────────────────────────
        private class HeightmapSource
        {
            public string                FolderPath;
            public string                DisplayName;
            public HeightmapMetadataJson Metadata;
        }

        private class BasemapSource
        {
            public string            FolderPath;
            public string            DisplayName;
            public BasemapType       Type;
            public OrthoMetadataJson Metadata;       // non-null for Ortho
            public TiledMetadataJson TiledMetadata;  // non-null for Tiled
        }

        // ── Root folder state ──────────────────────────────────────────────────
        private string _rootFolder = "";
        private bool   _hasScanned = false;
        private string _scanError;

        private List<HeightmapSource> _heightmapSources = new List<HeightmapSource>();
        private List<BasemapSource>   _basemapSources   = new List<BasemapSource>();

        private int    _selectedHeightmapIdx = 0;
        private bool[] _basemapSelected      = Array.Empty<bool>();

        // ── Import settings ────────────────────────────────────────────────────
        private string             _outputFolder    = "Assets/Generated/ZGConnect";
        private HeightmapResOption _heightmapRes    = HeightmapResOption.Medium_1025;
        private BasemapResOption   _basemapRes      = BasemapResOption.R2048;
        private TileSelectionMode  _tileMode        = TileSelectionMode.All;
        private int                _maxTiles        = 25;
        private float              _maxInvalidRatio = 0.10f;

        // ── Region filter ──────────────────────────────────────────────────────
        private RegionPreset    _regionPreset    = RegionPreset.Custom;
        private RegionInputMode _regionInputMode = RegionInputMode.GPS;
        // GPS inputs
        private float _regionMinLat = 45.75f;
        private float _regionMaxLat = 45.87f;
        private float _regionMinLon = 15.85f;
        private float _regionMaxLon = 16.10f;
        // EPSG:3765 inputs (direct or computed from GPS)
        private int   _regionMinE = 450000;
        private int   _regionMaxE = 475000;
        private int   _regionMinN = 5060000;
        private int   _regionMaxN = 5090000;
        private bool               _skipExisting        = true;
        private bool               _createSceneObjects  = true;
        private bool               _addCityTileComp     = true;
        private bool               _createDatasetAsset  = true;
        private bool               _setNeighbors        = true;

        // ── Logo ──────────────────────────────────────────────────────────────
        private Texture2D _logo;

        // ── Advanced ───────────────────────────────────────────────────────────
        private bool  _showAdvanced    = false;
        private bool  _flipVertically  = true;
        private bool  _drawInstanced   = true;
        private float _pixelError      = 5f;
        private int   _basemapDistance = 1000;

        // ── Buildings import ───────────────────────────────────────────────────
        private bool _buildingsSkipExisting   = true;
        private bool _buildingsAttachData     = true;
        private bool _buildingsFilterByRegion = true;  // mirrors terrain region when enabled
        private bool _buildingsProcessSurfaces = true;
        private bool _buildingsRoofOrthophotoUv = false;
        private bool _buildingsFastImport      = true;
        private bool _buildingsUseProcessed    = false;
        private BuildingSurfaceSettings _buildingsSurfaceSettings;

        // ── Vegetation masks import ────────────────────────────────────────────
        private string _vegSourceFolder    = "";
        private string _vegAssetFolder     = "Assets/Generated/ZGConnect/VegetationMasks";
        private bool   _vegSkipExisting    = true;
        private bool   _vegFilterByRegion  = true;

        // ── Tab state ──────────────────────────────────────────────────────────
        private int _selectedTab = 0;
        private static readonly string[] kTabNames = { "Terrain", "Buildings", "Vegetation" };

        public const int VegetationTabIndex = 2;

        public static void ShowWindowOnTab(int tabIndex)
        {
            var w = GetWindow<ZGConnectDatasetManagerWindow>("ZG Connect Import");
            w.minSize = new Vector2(460, 560);
            w._selectedTab = tabIndex;
            w.Repaint();
            w.Focus();
        }

        private Vector2 _scroll;

        // ── Persistence ────────────────────────────────────────────────────────
        private const string kPP = "ZGConnect.Importer.";   // EditorPrefs key prefix

        // ──────────────────────────────────────────────────────────────────────

        [MenuItem("ZG Connect/Dataset Import Manager")]
        public static void ShowWindow()
        {
            var w = GetWindow<ZGConnectDatasetManagerWindow>("ZG Connect Import");
            w.minSize = new Vector2(460, 560);
        }

        private void OnEnable()
        {
            _logo = ZGConnectEditorBranding.LoadLogo();
            LoadSettings();
            // Auto-rescan if root folder is still valid — window reopens fully ready
            if (!string.IsNullOrWhiteSpace(_rootFolder) && Directory.Exists(_rootFolder))
                ScanRootFolder();
            else
                RegisterMapBridge();

            if (string.IsNullOrWhiteSpace(_vegSourceFolder) && !string.IsNullOrWhiteSpace(_rootFolder))
            {
                string vegDefault = Path.Combine(_rootFolder, "vegetation_masks");
                if (Directory.Exists(vegDefault))
                    _vegSourceFolder = vegDefault;
            }
        }

        private void OnDisable()
        {
            SaveSettings();
            ZGConnectImportRegionBridge.Clear();
        }

        private void RegisterMapBridge()
        {
            ZGConnectImportRegionBridge.AlignSelectionToPackGrid = true;
            ZGConnectImportRegionBridge.ApplyTargetLabel = "Import Manager";
            ZGConnectImportRegionBridge.GetTiles = () =>
                ActiveHeightmapSource?.Metadata?.Tiles;
            ZGConnectImportRegionBridge.GetOverviewGeorefBounds = () =>
            {
                if (RealtimeStreamingMapGeorefUtility.TryGetTileUnion(
                        ActiveHeightmapSource?.Metadata?.Tiles,
                        out ZGConnectMapGeorefBounds bounds))
                {
                    return (bounds.MinE, bounds.MaxE, bounds.MinN, bounds.MaxN);
                }

                return (0, 0, 0, 0);
            };
            ZGConnectImportRegionBridge.GetBackgroundMapGeorefBounds = () =>
            {
                if (RealtimeStreamingMapGeorefUtility.TryGetTileUnion(
                        ActiveHeightmapSource?.Metadata?.Tiles,
                        out ZGConnectMapGeorefBounds bounds))
                {
                    return (bounds.MinE, bounds.MaxE, bounds.MinN, bounds.MaxN);
                }

                if (RealtimeStreamingMapGeorefUtility.TryGetBackgroundMapGeoref(out bounds))
                    return (bounds.MinE, bounds.MaxE, bounds.MinN, bounds.MaxN);

                return (0, 0, 0, 0);
            };
            ZGConnectImportRegionBridge.GetRegionEpsg = () =>
                (_regionMinE, _regionMaxE, _regionMinN, _regionMaxN);
            ZGConnectImportRegionBridge.ApplyRegionEpsg = ApplyRegionFromMap;
            ZGConnectImportRegionBridge.RepaintImporter = Repaint;
        }

        private void ApplyRegionFromMap(int minE, int maxE, int minN, int maxN)
        {
            _tileMode        = TileSelectionMode.Region;
            _regionPreset    = RegionPreset.Custom;
            _regionInputMode = RegionInputMode.EPSG3765;
            _regionMinE      = minE;
            _regionMaxE      = maxE;
            _regionMinN      = minN;
            _regionMaxN      = maxN;
            AlignRegionToHlodGrid();
            SyncGpsFromEpsgRegion();
            SaveSettings();
            Repaint();
        }

        private void AlignRegionToHlodGrid()
        {
            if (_regionMaxE <= _regionMinE || _regionMaxN <= _regionMinN)
                return;

            int tileSize = ActiveHeightmapSource?.Metadata?.Settings?.TileSizeMeters ?? 1000;
            HlodGridZones.AlignRegionEpsg(
                _regionMinE, _regionMaxE, _regionMinN, _regionMaxN,
                tileSize, HlodGridZones.PackRegionAlignFactor,
                out _regionMinE, out _regionMaxE, out _regionMinN, out _regionMaxN);
        }

        private void SyncGpsFromEpsgRegion()
        {
            try
            {
                var (minLat, minLon) = ZGConnectCoordinates.EPSG3765ToWGS84(_regionMinE, _regionMinN);
                var (maxLat, maxLon) = ZGConnectCoordinates.EPSG3765ToWGS84(_regionMaxE, _regionMaxN);
                _regionMinLat = (float)System.Math.Min(minLat, maxLat);
                _regionMaxLat = (float)System.Math.Max(minLat, maxLat);
                _regionMinLon = (float)System.Math.Min(minLon, maxLon);
                _regionMaxLon = (float)System.Math.Max(minLon, maxLon);
            }
            catch { /* ignore */ }
        }

        // ── Settings persistence ───────────────────────────────────────────────

        private void SaveSettings()
        {
            // Paths & output
            EditorPrefs.SetString(kPP + "RootFolder",         _rootFolder);
            EditorPrefs.SetString(kPP + "OutputFolder",       _outputFolder);
            // Resolutions
            EditorPrefs.SetInt   (kPP + "HeightmapRes",       (int)_heightmapRes);
            EditorPrefs.SetInt   (kPP + "BasemapRes",         (int)_basemapRes);
            // Tile selection
            EditorPrefs.SetInt   (kPP + "TileMode",           (int)_tileMode);
            EditorPrefs.SetInt   (kPP + "MaxTiles",           _maxTiles);
            EditorPrefs.SetFloat (kPP + "MaxInvalidRatio",    _maxInvalidRatio);
            // Region
            EditorPrefs.SetInt   (kPP + "RegionPreset",       (int)_regionPreset);
            EditorPrefs.SetInt   (kPP + "RegionInputMode",    (int)_regionInputMode);
            EditorPrefs.SetFloat (kPP + "RegionMinLat",       _regionMinLat);
            EditorPrefs.SetFloat (kPP + "RegionMaxLat",       _regionMaxLat);
            EditorPrefs.SetFloat (kPP + "RegionMinLon",       _regionMinLon);
            EditorPrefs.SetFloat (kPP + "RegionMaxLon",       _regionMaxLon);
            EditorPrefs.SetInt   (kPP + "RegionMinE",         _regionMinE);
            EditorPrefs.SetInt   (kPP + "RegionMaxE",         _regionMaxE);
            EditorPrefs.SetInt   (kPP + "RegionMinN",         _regionMinN);
            EditorPrefs.SetInt   (kPP + "RegionMaxN",         _regionMaxN);
            // Output options
            EditorPrefs.SetBool  (kPP + "SkipExisting",       _skipExisting);
            EditorPrefs.SetBool  (kPP + "CreateSceneObjs",    _createSceneObjects);
            EditorPrefs.SetBool  (kPP + "AddCityTileComp",    _addCityTileComp);
            EditorPrefs.SetBool  (kPP + "CreateDatasetAsset", _createDatasetAsset);
            EditorPrefs.SetBool  (kPP + "SetNeighbors",       _setNeighbors);
            // Advanced
            EditorPrefs.SetBool  (kPP + "ShowAdvanced",       _showAdvanced);
            EditorPrefs.SetBool  (kPP + "FlipVertically",     _flipVertically);
            EditorPrefs.SetBool  (kPP + "DrawInstanced",      _drawInstanced);
            EditorPrefs.SetFloat (kPP + "PixelError",         _pixelError);
            EditorPrefs.SetInt   (kPP + "BasemapDistance",    _basemapDistance);
            // Buildings
            EditorPrefs.SetBool  (kPP + "BldSkipExisting",    _buildingsSkipExisting);
            EditorPrefs.SetBool  (kPP + "BldAttachData",      _buildingsAttachData);
            EditorPrefs.SetBool  (kPP + "BldFilterByRegion",  _buildingsFilterByRegion);
            EditorPrefs.SetBool  (kPP + "BldProcessSurfaces", _buildingsProcessSurfaces);
            EditorPrefs.SetBool  (kPP + "BldRoofOrthophoto",  _buildingsRoofOrthophotoUv);
            EditorPrefs.SetBool  (kPP + "BldFastImport",      _buildingsFastImport);
            EditorPrefs.SetBool  (kPP + "BldUseProcessed",    _buildingsUseProcessed);
            EditorPrefs.SetString(kPP + "BldSurfaceSettings",
                _buildingsSurfaceSettings != null
                    ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(_buildingsSurfaceSettings))
                    : "");
            // Vegetation
            EditorPrefs.SetString(kPP + "VegSourceFolder",    _vegSourceFolder);
            EditorPrefs.SetString(kPP + "VegAssetFolder",     _vegAssetFolder);
            EditorPrefs.SetBool  (kPP + "VegSkipExisting",    _vegSkipExisting);
            EditorPrefs.SetBool  (kPP + "VegFilterByRegion",  _vegFilterByRegion);
            // UI state
            EditorPrefs.SetInt   (kPP + "SelectedTab",        _selectedTab);
            EditorPrefs.SetInt   (kPP + "HeightmapIdx",       _selectedHeightmapIdx);

            // Basemap selection — save folder paths so they can be restored after rescan
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _basemapSources.Count; i++)
                if (_basemapSelected[i])
                    sb.Append(_basemapSources[i].FolderPath).Append('|');
            EditorPrefs.SetString(kPP + "SelectedBasemaps", sb.ToString());
        }

        private void LoadSettings()
        {
            _rootFolder         = EditorPrefs.GetString(kPP + "RootFolder",         _rootFolder);
            _outputFolder       = EditorPrefs.GetString(kPP + "OutputFolder",       _outputFolder);
            _heightmapRes       = (HeightmapResOption)EditorPrefs.GetInt(kPP + "HeightmapRes",  (int)_heightmapRes);
            _basemapRes         = (BasemapResOption)  EditorPrefs.GetInt(kPP + "BasemapRes",    (int)_basemapRes);
            _tileMode           = (TileSelectionMode) EditorPrefs.GetInt(kPP + "TileMode",      (int)_tileMode);
            _maxTiles           = EditorPrefs.GetInt  (kPP + "MaxTiles",            _maxTiles);
            _maxInvalidRatio    = EditorPrefs.GetFloat(kPP + "MaxInvalidRatio",     _maxInvalidRatio);
            _regionPreset       = (RegionPreset)      EditorPrefs.GetInt(kPP + "RegionPreset",    (int)_regionPreset);
            _regionInputMode    = (RegionInputMode)   EditorPrefs.GetInt(kPP + "RegionInputMode", (int)_regionInputMode);
            if (_regionPreset != RegionPreset.Custom)
                ApplyRegionPreset(_regionPreset);
            _regionMinLat       = EditorPrefs.GetFloat(kPP + "RegionMinLat",        _regionMinLat);
            _regionMaxLat       = EditorPrefs.GetFloat(kPP + "RegionMaxLat",        _regionMaxLat);
            _regionMinLon       = EditorPrefs.GetFloat(kPP + "RegionMinLon",        _regionMinLon);
            _regionMaxLon       = EditorPrefs.GetFloat(kPP + "RegionMaxLon",        _regionMaxLon);
            _regionMinE         = EditorPrefs.GetInt  (kPP + "RegionMinE",          _regionMinE);
            _regionMaxE         = EditorPrefs.GetInt  (kPP + "RegionMaxE",          _regionMaxE);
            _regionMinN         = EditorPrefs.GetInt  (kPP + "RegionMinN",          _regionMinN);
            _regionMaxN         = EditorPrefs.GetInt  (kPP + "RegionMaxN",          _regionMaxN);
            _skipExisting       = EditorPrefs.GetBool (kPP + "SkipExisting",        _skipExisting);
            _createSceneObjects = EditorPrefs.GetBool (kPP + "CreateSceneObjs",     _createSceneObjects);
            _addCityTileComp    = EditorPrefs.GetBool (kPP + "AddCityTileComp",     _addCityTileComp);
            _createDatasetAsset = EditorPrefs.GetBool (kPP + "CreateDatasetAsset",  _createDatasetAsset);
            _setNeighbors       = EditorPrefs.GetBool (kPP + "SetNeighbors",        _setNeighbors);
            _showAdvanced       = EditorPrefs.GetBool (kPP + "ShowAdvanced",        _showAdvanced);
            _flipVertically     = EditorPrefs.GetBool (kPP + "FlipVertically",      _flipVertically);
            _drawInstanced      = EditorPrefs.GetBool (kPP + "DrawInstanced",       _drawInstanced);
            _pixelError         = EditorPrefs.GetFloat(kPP + "PixelError",          _pixelError);
            _basemapDistance    = EditorPrefs.GetInt  (kPP + "BasemapDistance",     _basemapDistance);
            _buildingsSkipExisting   = EditorPrefs.GetBool(kPP + "BldSkipExisting",    _buildingsSkipExisting);
            _buildingsAttachData     = EditorPrefs.GetBool(kPP + "BldAttachData",      _buildingsAttachData);
            _buildingsFilterByRegion = EditorPrefs.GetBool(kPP + "BldFilterByRegion",  _buildingsFilterByRegion);
            _buildingsProcessSurfaces = EditorPrefs.GetBool(kPP + "BldProcessSurfaces", _buildingsProcessSurfaces);
            _buildingsRoofOrthophotoUv = EditorPrefs.GetBool(kPP + "BldRoofOrthophoto", _buildingsRoofOrthophotoUv);
            _buildingsFastImport      = EditorPrefs.GetBool(kPP + "BldFastImport",      _buildingsFastImport);
            _buildingsUseProcessed    = EditorPrefs.GetBool(kPP + "BldUseProcessed",    _buildingsUseProcessed);
            if (!_buildingsUseProcessed
                && (_buildingsProcessSurfaces || _buildingsRoofOrthophotoUv))
                _buildingsFastImport = false;
            string bldSettingsGuid = EditorPrefs.GetString(kPP + "BldSurfaceSettings", "");
            if (!string.IsNullOrEmpty(bldSettingsGuid))
            {
                string p = AssetDatabase.GUIDToAssetPath(bldSettingsGuid);
                if (!string.IsNullOrEmpty(p))
                    _buildingsSurfaceSettings = AssetDatabase.LoadAssetAtPath<BuildingSurfaceSettings>(p);
            }
            _vegSourceFolder    = EditorPrefs.GetString(kPP + "VegSourceFolder",   _vegSourceFolder);
            _vegAssetFolder     = EditorPrefs.GetString(kPP + "VegAssetFolder",    _vegAssetFolder);
            _vegSkipExisting    = EditorPrefs.GetBool  (kPP + "VegSkipExisting",   _vegSkipExisting);
            _vegFilterByRegion  = EditorPrefs.GetBool  (kPP + "VegFilterByRegion", _vegFilterByRegion);
            _selectedTab        = EditorPrefs.GetInt  (kPP + "SelectedTab",         _selectedTab);
            _selectedHeightmapIdx = EditorPrefs.GetInt(kPP + "HeightmapIdx",        _selectedHeightmapIdx);
        }

        private void OnGUI()
        {
            ZGConnectEditorBranding.DrawWindowBackground(this);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            ZGConnectEditorBranding.DrawWindowHeader(_logo, position.width, "ZG Connect — Dataset Import Manager");
            DrawRootFolderSection();

            EditorGUILayout.Space(4);
            _selectedTab = GUILayout.Toolbar(_selectedTab, kTabNames);
            EditorGUILayout.Space(6);

            switch (_selectedTab)
            {
                case 0:
                    DrawTerrainTab();
                    break;
                case 1:
                    DrawBuildingsSection();
                    break;
                default:
                    DrawVegetationSection();
                    break;
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawTerrainTab()
        {
            if (!_hasScanned) return;

            DrawDiscoveredSources();

            if (ActiveHeightmapSource != null)
            {
                EditorGUILayout.Space(4);
                DrawImportOptions();
                EditorGUILayout.Space(4);
                DrawOutputOptions();
                EditorGUILayout.Space(4);
                DrawAdvancedOptions();
                EditorGUILayout.Space(8);
                DrawImportButton();
            }

            EditorGUILayout.Space(6);
            DrawHorizontalRule();
            DrawSceneSetupSection();
            EditorGUILayout.Space(6);
            DrawHorizontalRule();
            DrawAddressablesMigrationSection();
        }

        // ── Accessors ──────────────────────────────────────────────────────────

        private HeightmapSource ActiveHeightmapSource =>
            _heightmapSources.Count > 0 ? _heightmapSources[_selectedHeightmapIdx] : null;

        private bool AnyBasemapSelected
        {
            get
            {
                foreach (bool b in _basemapSelected) if (b) return true;
                return false;
            }
        }

        // ── Sections ───────────────────────────────────────────────────────────

        private void DrawRootFolderSection()
        {
            EditorGUILayout.LabelField("Dataset Root Folder", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                _rootFolder = EditorGUILayout.TextField(_rootFolder);
                bool rootChanged = EditorGUI.EndChangeCheck();

                if (GUILayout.Button("Browse", GUILayout.Width(70)))
                {
                    string picked = EditorUtility.OpenFolderPanel(
                        "Select Dataset Root Folder", _rootFolder, "");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        _rootFolder = picked;
                        rootChanged = true;
                        ResetScan();
                    }
                }

                if (rootChanged)
                    SaveSettings();
            }

            EditorGUILayout.Space(2);
            if (GUILayout.Button("Scan for Data Sources"))
                ScanRootFolder();

            if (!string.IsNullOrEmpty(_scanError))
                EditorGUILayout.HelpBox(_scanError, MessageType.Error);

            EditorGUILayout.Space(4);
        }

        private void DrawDiscoveredSources()
        {
            EditorGUILayout.LabelField("Discovered Sources", EditorStyles.boldLabel);

            // ── Heightmap ──────────────────────────────────────────────────────
            if (_heightmapSources.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No heightmap source found. Expected a subfolder with a metadata.json " +
                    "containing settings.resolution.",
                    MessageType.Error);
            }
            else
            {
                if (_heightmapSources.Count == 1)
                {
                    EditorGUILayout.LabelField("Heightmap", _heightmapSources[0].DisplayName);
                }
                else
                {
                    string[] options = BuildDisplayNames(_heightmapSources, s => s.DisplayName);
                    _selectedHeightmapIdx = EditorGUILayout.Popup(
                        "Heightmap", _selectedHeightmapIdx, options);
                }

                if (ActiveHeightmapSource != null)
                {
                    var s = ActiveHeightmapSource.Metadata.Settings;
                    using (new EditorGUI.IndentLevelScope())
                    {
                        EditorGUILayout.LabelField("Tiles",        ActiveHeightmapSource.Metadata.Tiles.Count.ToString());
                        EditorGUILayout.LabelField("Resolution",   $"{s.Resolution} px");
                        EditorGUILayout.LabelField("Height range", $"{s.MinHeight} – {s.MaxHeight} m");
                        EditorGUILayout.LabelField("Unity origin", $"({s.UnityOriginX}, {s.UnityOriginY})");
                    }
                }
            }

            EditorGUILayout.Space(4);

            // ── Basemaps (multi-select checkbox list) ──────────────────────────
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Basemaps to Import", EditorStyles.boldLabel);

                if (_basemapSources.Count > 0)
                {
                    if (GUILayout.Button("All",  GUILayout.Width(38)))
                        for (int i = 0; i < _basemapSelected.Length; i++) _basemapSelected[i] = true;
                    if (GUILayout.Button("None", GUILayout.Width(44)))
                        for (int i = 0; i < _basemapSelected.Length; i++) _basemapSelected[i] = false;
                }
            }

            if (_basemapSources.Count == 0)
            {
                EditorGUILayout.LabelField("  None found — terrains will import without texture.",
                    EditorStyles.miniLabel);
            }
            else
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    for (int i = 0; i < _basemapSources.Count; i++)
                    {
                        _basemapSelected[i] = EditorGUILayout.ToggleLeft(
                            _basemapSources[i].DisplayName, _basemapSelected[i]);
                    }
                }
            }

            EditorGUILayout.Space(2);
        }

        private void DrawImportOptions()
        {
            EditorGUILayout.LabelField("Import Options", EditorStyles.boldLabel);

            // Heightmap resolution
            _heightmapRes = (HeightmapResOption)EditorGUILayout.EnumPopup("Heightmap Resolution", _heightmapRes);
            if (_heightmapRes == HeightmapResOption.Low_513)
                EditorGUILayout.HelpBox("513 is downsampled in code from the native 1025 source.", MessageType.None);

            EditorGUILayout.Space(2);

            // Basemap max texture size — disabled when nothing is selected
            using (new EditorGUI.DisabledScope(!AnyBasemapSelected))
            {
                _basemapRes = (BasemapResOption)EditorGUILayout.EnumPopup("Basemap Max Resolution", _basemapRes);

                // Warn for each selected ortho source whose native resolution is lower than chosen limit
                for (int i = 0; i < _basemapSources.Count; i++)
                {
                    if (!_basemapSelected[i]) continue;
                    if (_basemapSources[i].Type != BasemapType.Ortho) continue;
                    int srcRes = _basemapSources[i].Metadata.Settings.TextureResolution;
                    if ((int)_basemapRes > srcRes)
                    {
                        EditorGUILayout.HelpBox(
                            $"{_basemapSources[i].DisplayName}: source is {srcRes} px — " +
                            "Unity will not upscale beyond that.",
                            MessageType.Warning);
                    }
                }
            }

            EditorGUILayout.Space(4);

            _tileMode = (TileSelectionMode)EditorGUILayout.EnumPopup("Tile Selection", _tileMode);

            if (_tileMode == TileSelectionMode.FirstN)
            {
                using (new EditorGUI.IndentLevelScope())
                    _maxTiles = EditorGUILayout.IntField("Max Tiles", _maxTiles);
            }
            else if (_tileMode == TileSelectionMode.Region)
            {
                DrawRegionFilter();
            }

            _maxInvalidRatio = EditorGUILayout.Slider("Max Invalid Ratio", _maxInvalidRatio, 0f, 0.95f);
        }

        private void DrawRegionFilter()
        {
            EditorGUILayout.Space(4);

            using (new EditorGUI.IndentLevelScope())
            {
                int presetIdx = EditorGUILayout.Popup("Područje", (int)_regionPreset, kRegionPresetLabels);
                if ((RegionPreset)presetIdx != _regionPreset)
                {
                    _regionPreset = (RegionPreset)presetIdx;
                    if (_regionPreset != RegionPreset.Custom)
                        ApplyRegionPreset(_regionPreset);
                }

                bool customBounds = _regionPreset == RegionPreset.Custom;

                using (new EditorGUI.DisabledScope(!customBounds))
                {
                    _regionInputMode = (RegionInputMode)EditorGUILayout.EnumPopup("Input Mode", _regionInputMode);
                }
                EditorGUILayout.Space(2);

                if (_regionInputMode == RegionInputMode.GPS)
                {
                    // GPS input → convert to EPSG:3765
                    EditorGUILayout.LabelField("Latitude range", EditorStyles.miniLabel);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("Min", GUILayout.Width(28));
                        using (new EditorGUI.DisabledScope(!customBounds))
                        {
                            EditorGUI.BeginChangeCheck();
                            _regionMinLat = EditorGUILayout.FloatField(_regionMinLat);
                            if (EditorGUI.EndChangeCheck() && !customBounds)
                                _regionPreset = RegionPreset.Custom;
                        }
                        EditorGUILayout.LabelField("Max", GUILayout.Width(28));
                        using (new EditorGUI.DisabledScope(!customBounds))
                        {
                            EditorGUI.BeginChangeCheck();
                            _regionMaxLat = EditorGUILayout.FloatField(_regionMaxLat);
                            if (EditorGUI.EndChangeCheck() && !customBounds)
                                _regionPreset = RegionPreset.Custom;
                        }
                    }

                    EditorGUILayout.LabelField("Longitude range", EditorStyles.miniLabel);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("Min", GUILayout.Width(28));
                        using (new EditorGUI.DisabledScope(!customBounds))
                        {
                            EditorGUI.BeginChangeCheck();
                            _regionMinLon = EditorGUILayout.FloatField(_regionMinLon);
                            if (EditorGUI.EndChangeCheck() && !customBounds)
                                _regionPreset = RegionPreset.Custom;
                        }
                        EditorGUILayout.LabelField("Max", GUILayout.Width(28));
                        using (new EditorGUI.DisabledScope(!customBounds))
                        {
                            EditorGUI.BeginChangeCheck();
                            _regionMaxLon = EditorGUILayout.FloatField(_regionMaxLon);
                            if (EditorGUI.EndChangeCheck() && !customBounds)
                                _regionPreset = RegionPreset.Custom;
                        }
                    }

                    // Convert GPS → EPSG:3765 and cache
                    ComputeRegionFromGPS();

                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField(
                        $"EPSG:3765   E {_regionMinE}–{_regionMaxE}   N {_regionMinN}–{_regionMaxN}",
                        EditorStyles.miniLabel);
                }
                else
                {
                    // Direct EPSG:3765 input
                    EditorGUILayout.LabelField("Easting range (E)", EditorStyles.miniLabel);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("Min", GUILayout.Width(28));
                        using (new EditorGUI.DisabledScope(!customBounds))
                        {
                            EditorGUI.BeginChangeCheck();
                            _regionMinE = EditorGUILayout.IntField(_regionMinE);
                            if (EditorGUI.EndChangeCheck())
                            {
                                if (!customBounds)
                                _regionPreset = RegionPreset.Custom;
                                AlignRegionToHlodGrid();
                            }
                        }
                        EditorGUILayout.LabelField("Max", GUILayout.Width(28));
                        using (new EditorGUI.DisabledScope(!customBounds))
                        {
                            EditorGUI.BeginChangeCheck();
                            _regionMaxE = EditorGUILayout.IntField(_regionMaxE);
                            if (EditorGUI.EndChangeCheck())
                            {
                                if (!customBounds)
                                _regionPreset = RegionPreset.Custom;
                                AlignRegionToHlodGrid();
                            }
                        }
                    }

                    EditorGUILayout.LabelField("Northing range (N)", EditorStyles.miniLabel);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("Min", GUILayout.Width(28));
                        using (new EditorGUI.DisabledScope(!customBounds))
                        {
                            EditorGUI.BeginChangeCheck();
                            _regionMinN = EditorGUILayout.IntField(_regionMinN);
                            if (EditorGUI.EndChangeCheck())
                            {
                                if (!customBounds)
                                _regionPreset = RegionPreset.Custom;
                                AlignRegionToHlodGrid();
                            }
                        }
                        EditorGUILayout.LabelField("Max", GUILayout.Width(28));
                        using (new EditorGUI.DisabledScope(!customBounds))
                        {
                            EditorGUI.BeginChangeCheck();
                            _regionMaxN = EditorGUILayout.IntField(_regionMaxN);
                            if (EditorGUI.EndChangeCheck())
                            {
                                if (!customBounds)
                                _regionPreset = RegionPreset.Custom;
                                AlignRegionToHlodGrid();
                            }
                        }
                    }
                }

                EditorGUILayout.Space(3);
                if (GUILayout.Button("Odaberi na karti…", GUILayout.Height(22)))
                    ZGConnectTileMapWindow.OpenFromImporter();

                // Live tile count preview
                EditorGUILayout.Space(3);
                int matchCount  = CountTilesInRegion();
                int totalCount  = ActiveHeightmapSource?.Metadata?.Tiles?.Count ?? 0;
                MessageType msgType = matchCount == 0 ? MessageType.Warning : MessageType.None;
                EditorGUILayout.HelpBox(
                    matchCount == 0
                        ? "No tiles in this region — adjust the bounds."
                        : $"{matchCount} / {totalCount} tiles in region",
                    msgType);
            }
        }

        private void ApplyRegionPreset(RegionPreset preset)
        {
            switch (preset)
            {
                case RegionPreset.ZagrebCityBounds:
                    _regionMinLat = kZagrebBoundsMinLat;
                    _regionMinLon = kZagrebBoundsMinLon;
                    _regionMaxLat = kZagrebBoundsMaxLat;
                    _regionMaxLon = kZagrebBoundsMaxLon;
                    break;
                case RegionPreset.ZagrebCityCenter:
                    _regionMinLat = kZagrebCenterMinLat;
                    _regionMinLon = kZagrebCenterMinLon;
                    _regionMaxLat = kZagrebCenterMaxLat;
                    _regionMaxLon = kZagrebCenterMaxLon;
                    break;
                default:
                    return;
            }

            _regionInputMode = RegionInputMode.GPS;
            ComputeRegionFromGPS();
        }

        private void ComputeRegionFromGPS()
        {
            try
            {
                var (minE, minN) = ZGConnectCoordinates.WGS84ToEPSG3765(_regionMinLat, _regionMinLon);
                var (maxE, maxN) = ZGConnectCoordinates.WGS84ToEPSG3765(_regionMaxLat, _regionMaxLon);
                _regionMinE = Mathf.FloorToInt((float)System.Math.Min(minE, maxE));
                _regionMaxE = Mathf.CeilToInt ((float)System.Math.Max(minE, maxE));
                _regionMinN = Mathf.FloorToInt((float)System.Math.Min(minN, maxN));
                _regionMaxN = Mathf.CeilToInt ((float)System.Math.Max(minN, maxN));
                AlignRegionToHlodGrid();
            }
            catch { /* ignore invalid intermediate values while user is typing */ }
        }

        private int CountTilesInRegion()
        {
            var tiles = ActiveHeightmapSource?.Metadata?.Tiles;
            if (tiles == null) return 0;

            int count = 0;
            foreach (var t in tiles)
            {
                if (t.Left   < _regionMaxE && t.Right > _regionMinE &&
                    t.Bottom < _regionMaxN && t.Top   > _regionMinN)
                    count++;
            }
            return count;
        }

        private int CountBuildingTilesInRegion(CityDataset dataset)
        {
            if (dataset?.tiles == null) return 0;

            int count = 0;
            foreach (var rec in dataset.tiles)
            {
                if (rec.left   < _regionMaxE && rec.right > _regionMinE &&
                    rec.bottom < _regionMaxN && rec.top   > _regionMinN)
                    count++;
            }
            return count;
        }

        private void DrawOutputOptions()
        {
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            _outputFolder       = EditorGUILayout.TextField("Output Folder",         _outputFolder);
            _skipExisting       = EditorGUILayout.Toggle("Skip Existing",            _skipExisting);
            _createDatasetAsset = EditorGUILayout.Toggle("Create CityDataset Asset", _createDatasetAsset);

            EditorGUILayout.Space(2);
            _createSceneObjects = EditorGUILayout.Toggle("Create Scene Objects",     _createSceneObjects);

            using (new EditorGUI.DisabledScope(!_createSceneObjects))
            using (new EditorGUI.IndentLevelScope())
            {
                _addCityTileComp = EditorGUILayout.Toggle("Add CityTile Component",  _addCityTileComp);
                _setNeighbors    = EditorGUILayout.Toggle("Set Terrain Neighbors",   _setNeighbors);
            }
        }

        private void DrawAdvancedOptions()
        {
            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "Advanced", true);
            if (!_showAdvanced) return;

            using (new EditorGUI.IndentLevelScope())
            {
                _flipVertically  = EditorGUILayout.Toggle("Flip Heightmap Vertically", _flipVertically);
                _drawInstanced   = EditorGUILayout.Toggle("Draw Instanced",             _drawInstanced);
                _pixelError      = EditorGUILayout.FloatField("Pixel Error",            _pixelError);
                _basemapDistance = EditorGUILayout.IntField("Basemap Distance",         _basemapDistance);
            }
        }

        private void DrawImportButton()
        {
            if (GUILayout.Button("Import", GUILayout.Height(36)))
                RunImport();
        }

        // ── Scan ───────────────────────────────────────────────────────────────

        private void ScanRootFolder()
        {
            ResetScan();

            if (string.IsNullOrWhiteSpace(_rootFolder) || !Directory.Exists(_rootFolder))
            {
                _scanError = "Root folder not found.";
                return;
            }

            string[] subFolders = Directory.GetDirectories(_rootFolder);

            foreach (string folder in subFolders)
            {
                string metaPath = Path.Combine(folder, "metadata.json");
                if (!File.Exists(metaPath)) continue;

                string json       = File.ReadAllText(metaPath);
                string folderName = Path.GetFileName(folder);

                if (TryParseHeightmap(json, folderName, out HeightmapSource hm))
                {
                    _heightmapSources.Add(hm);
                    hm.FolderPath = folder;
                    continue;
                }

                if (TryParseOrthoBasemap(json, folderName, out BasemapSource bm))
                {
                    _basemapSources.Add(bm);
                    bm.FolderPath = folder;
                    continue;
                }

                if (TryParseTiledBasemap(json, folderName, out BasemapSource tiledBm))
                {
                    _basemapSources.Add(tiledBm);
                    tiledBm.FolderPath = folder;
                }
            }

            // Restore saved basemap selection, or default all to checked on first scan
            _basemapSelected = new bool[_basemapSources.Count];
            string savedPaths = EditorPrefs.GetString(kPP + "SelectedBasemaps", "");

            if (string.IsNullOrEmpty(savedPaths))
            {
                // First time — select all
                for (int i = 0; i < _basemapSelected.Length; i++)
                    _basemapSelected[i] = true;
            }
            else
            {
                // Restore by matching folder paths
                var saved = new System.Collections.Generic.HashSet<string>(
                    savedPaths.Split(new[] { '|' }, System.StringSplitOptions.RemoveEmptyEntries));
                for (int i = 0; i < _basemapSources.Count; i++)
                    _basemapSelected[i] = saved.Contains(_basemapSources[i].FolderPath);
            }

            // Restore heightmap index (clamp to valid range)
            _selectedHeightmapIdx = Mathf.Clamp(_selectedHeightmapIdx, 0,
                Mathf.Max(0, _heightmapSources.Count - 1));

            string vegCandidate = Path.Combine(_rootFolder, "vegetation_masks");
            if (Directory.Exists(vegCandidate))
                _vegSourceFolder = vegCandidate;

            _hasScanned = true;

            if (_heightmapSources.Count == 0)
                _scanError = "No heightmap source found in subfolders.";
            else
                Debug.Log($"[ZGConnect] Scan complete: {_heightmapSources.Count} heightmap source(s), " +
                          $"{_basemapSources.Count} basemap source(s).");

            RegisterMapBridge();
            Repaint();
        }

        private static bool TryParseHeightmap(string json, string folderName, out HeightmapSource result)
        {
            result = null;
            try
            {
                var meta = JsonConvert.DeserializeObject<HeightmapMetadataJson>(json);
                if (meta?.Settings?.Resolution > 0 && meta.Tiles != null)
                {
                    result = new HeightmapSource
                    {
                        DisplayName = $"{folderName}  ({meta.Settings.Resolution} px · {meta.Tiles.Count} tiles)",
                        Metadata    = meta
                    };
                    return true;
                }
            }
            catch { /* not a heightmap metadata */ }
            return false;
        }

        private static bool TryParseOrthoBasemap(string json, string folderName, out BasemapSource result)
        {
            result = null;
            try
            {
                var meta = JsonConvert.DeserializeObject<OrthoMetadataJson>(json);
                if (meta?.Settings?.TextureResolution > 0 && meta.Tiles != null)
                {
                    result = new BasemapSource
                    {
                        DisplayName = $"{folderName}  [Ortho · {meta.Settings.TextureResolution} px · {meta.Tiles.Count} tiles]",
                        Type        = BasemapType.Ortho,
                        Metadata    = meta
                    };
                    return true;
                }
            }
            catch { /* not ortho metadata */ }
            return false;
        }

        private static bool TryParseTiledBasemap(string json, string folderName, out BasemapSource result)
        {
            result = null;
            try
            {
                var meta = JsonConvert.DeserializeObject<TiledMetadataJson>(json);
                if (meta?.Layers != null && meta.Layers.Count > 0 && meta.Tiles != null)
                {
                    result = new BasemapSource
                    {
                        DisplayName  = $"{folderName}  [Tiled · {meta.Layers.Count} layers · {meta.Tiles.Count} tiles]",
                        Type         = BasemapType.Tiled,
                        TiledMetadata = meta
                    };
                    return true;
                }
            }
            catch { /* not tiled metadata */ }
            return false;
        }

        private void ResetScan()
        {
            _heightmapSources.Clear();
            _basemapSources.Clear();
            _selectedHeightmapIdx = 0;
            _basemapSelected      = Array.Empty<bool>();
            _scanError            = null;
            _hasScanned           = false;
        }

        // ── Import ─────────────────────────────────────────────────────────────

        private void RunImport()
        {
            HeightmapSource hm = ActiveHeightmapSource;

            if (hm == null)
            {
                EditorUtility.DisplayDialog("ZG Connect", "No heightmap source selected.", "OK");
                return;
            }

            // Build the list of selected basemap sources
            var selectedBasemaps = new List<BasemapImportSource>();
            for (int i = 0; i < _basemapSources.Count; i++)
            {
                if (!_basemapSelected[i]) continue;
                var bm = _basemapSources[i];
                selectedBasemaps.Add(new BasemapImportSource
                {
                    BasemapId     = ZGConnectPathUtils.DeriveBasemapId(bm.FolderPath),
                    DisplayName   = bm.DisplayName,
                    Type          = bm.Type,
                    Metadata      = bm.Metadata,
                    TiledMetadata = bm.TiledMetadata,
                    FolderPath    = bm.FolderPath
                });
            }

            // Ensure EPSG:3765 bounds are current when GPS mode is used
            if (_tileMode == TileSelectionMode.Region &&
                _regionInputMode == RegionInputMode.GPS)
                ComputeRegionFromGPS();

            var settings = new ZGConnectImportSettings
            {
                OutputFolder            = _outputFolder,
                HeightmapResolution     = (int)_heightmapRes,
                BasemapMaxTextureSize   = (int)_basemapRes,
                ImportAllTiles          = _tileMode == TileSelectionMode.All,
                MaxTiles                = _tileMode == TileSelectionMode.FirstN ? _maxTiles : int.MaxValue,
                MaxInvalidRatio         = _maxInvalidRatio,
                FilterByRegion          = _tileMode == TileSelectionMode.Region,
                RegionMinE              = _regionMinE,
                RegionMaxE              = _regionMaxE,
                RegionMinN              = _regionMinN,
                RegionMaxN              = _regionMaxN,
                SkipExisting            = _skipExisting,
                CreateSceneObjects      = _createSceneObjects,
                AddCityTileComponents   = _createSceneObjects && _addCityTileComp,
                CreateCityDatasetAsset  = _createDatasetAsset,
                SetTerrainNeighbors     = _createSceneObjects && _setNeighbors,
                FlipHeightmapVertically = _flipVertically,
                DrawInstanced           = _drawInstanced,
                PixelError              = _pixelError,
                BasemapDistance         = _basemapDistance,
            };

            try
            {
                ZGConnectImporter.Import(
                    hm.Metadata, hm.FolderPath,
                    selectedBasemaps,
                    settings,
                    (p, msg) => EditorUtility.DisplayProgressBar("ZG Connect Import", msg, p));
            }
            catch (Exception ex)
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("Import Error", ex.Message, "OK");
                Debug.LogError($"[ZGConnect] Import failed: {ex}");
            }
        }

        // ── Scene setup ────────────────────────────────────────────────────────

        private void DrawSceneSetupSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Scene Setup", EditorStyles.boldLabel);

            TerrainStreamingController existing =
                UnityEngine.Object.FindAnyObjectByType<TerrainStreamingController>();

            if (existing != null)
            {
                EditorGUILayout.HelpBox(
                    $"Streamer already in scene: '{existing.gameObject.name}'",
                    MessageType.None);

                if (GUILayout.Button("Select in Hierarchy", GUILayout.Height(24)))
                    Selection.activeGameObject = existing.gameObject;
            }
            else
            {
                if (GUILayout.Button("Create ZGConnect Streamer", GUILayout.Height(30)))
                    CreateStreamer();
            }
        }

        private void DrawAddressablesMigrationSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Addressables", EditorStyles.boldLabel);

            // Find the dataset in the current output folder
            string datasetPath   = $"{_outputFolder}/Config/ZGConnectDataset.asset";
            CityDataset dataset  = AssetDatabase.LoadAssetAtPath<CityDataset>(datasetPath);

            if (dataset == null)
            {
                EditorGUILayout.HelpBox(
                    "Import terrain first to generate a CityDataset, " +
                    "then run the migration here.",
                    MessageType.None);
                return;
            }

            var (migrated, total) = ZGConnectAddressablesMigrator.MigrationStatus(dataset);

            if (migrated < total)
            {
                EditorGUILayout.HelpBox(
                    $"Addressables: {migrated} / {total} tiles migrated.\n\n" +
                    "Click 'Migrate to Addressables' to:\n" +
                    "  • Eliminate the editor / runtime freeze\n" +
                    "  • Enable per-tile memory release during streaming\n" +
                    "  • Prepare the dataset for remote content delivery",
                    MessageType.Warning);

                if (GUILayout.Button("⚡  Migrate to Addressables", GUILayout.Height(32)))
                {
                    int done = ZGConnectAddressablesMigrator.MigrateDataset(dataset);
                    EditorUtility.DisplayDialog(
                        "ZGConnect Addressables",
                        $"Migration complete.\n\n" +
                        $"{done} tile(s) newly registered as Addressable.\n\n" +
                        "Next step: Window → Asset Management → Addressables → Groups\n" +
                        "→ Build → New Build → Default Build Script",
                        "OK");
                    GUIUtility.ExitGUI();
                }
            }
            else
            {
                EditorGUILayout.HelpBox(
                    $"✓  All {total} tiles are registered as Addressables.\n" +
                    "Build content: Window → Asset Management → Addressables → Groups " +
                    "→ Build → New Build → Default Build Script",
                    MessageType.None);
            }
        }

        private void CreateStreamer()
        {
            var go      = new GameObject("ZGConnect Streamer");
            var streamer = go.AddComponent<TerrainStreamingController>();

            // Auto-assign dataset if already imported
            string datasetPath = $"{_outputFolder}/Config/ZGConnectDataset.asset";
            CityDataset dataset = AssetDatabase.LoadAssetAtPath<CityDataset>(datasetPath);
            if (dataset != null)
            {
                var so   = new SerializedObject(streamer);
                var prop = so.FindProperty("dataset");
                if (prop != null)
                {
                    prop.objectReferenceValue = dataset;
                    so.ApplyModifiedProperties();
                }
            }

            Undo.RegisterCreatedObjectUndo(go, "Create ZGConnect Streamer");
            Selection.activeGameObject = go;

            Debug.Log($"[ZGConnect] Created 'ZGConnect Streamer'" +
                      (dataset != null ? " — CityDataset auto-assigned." : " — assign CityDataset manually."));
        }

        // ── Buildings section ──────────────────────────────────────────────────

        private void DrawBuildingsSection()
        {
            EditorGUILayout.LabelField("Buildings Import", EditorStyles.boldLabel);
            EditorGUILayout.Space(2);

            // Both paths are derived — no user input needed beyond what is already set above.
            //   Dataset:          {OutputFolder}/Config/ZGConnectDataset.asset
            //   Buildings folder: {RootFolder}/building_meshes/
            string datasetAssetPath   = $"{_outputFolder}/Config/ZGConnectDataset.asset";
            string rawBuildingsFolder = Path.Combine(_rootFolder, "building_meshes");
            string processedFolder    = BuildingSurfacePipeline.GetProcessedFolder(rawBuildingsFolder);
            string buildingsFolder    = _buildingsUseProcessed ? processedFolder : rawBuildingsFolder;

            CityDataset dataset       = AssetDatabase.LoadAssetAtPath<CityDataset>(datasetAssetPath);
            bool        folderExists  = Directory.Exists(buildingsFolder);
            bool        rawExists       = Directory.Exists(rawBuildingsFolder);

            // ── Status ────────────────────────────────────────────────────────
            bool rootReady = !string.IsNullOrWhiteSpace(_rootFolder) && Directory.Exists(_rootFolder);

            if (!rootReady)
            {
                EditorGUILayout.HelpBox(
                    "Set the Dataset Root Folder at the top of the window to enable buildings import.",
                    MessageType.Info);
                return;
            }

            if (dataset == null)
            {
                EditorGUILayout.HelpBox(
                    "No dataset found — run the terrain import first.\n" +
                    $"Expected: {datasetAssetPath}",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.LabelField(
                    $"Dataset  ·  {dataset.datasetName}  ({dataset.tiles.Count} tiles)",
                    EditorStyles.miniLabel);
            }

            if (!rawExists)
            {
                EditorGUILayout.HelpBox(
                    $"Raw buildings folder not found:\n{rawBuildingsFolder}",
                    MessageType.Warning);
            }
            else
            {
                int rawCount = Directory.GetFiles(rawBuildingsFolder, "buildings_*.glb").Length;
                int procCount = Directory.Exists(processedFolder)
                    ? Directory.GetFiles(processedFolder, "buildings_*.glb").Length
                    : 0;
                EditorGUILayout.LabelField(
                    $"Raw GLBs: {rawCount}  ·  {rawBuildingsFolder}",
                    EditorStyles.miniLabel);
                EditorGUILayout.LabelField(
                    $"Processed: {procCount}  ·  {processedFolder}",
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(4);
            _buildingsUseProcessed = EditorGUILayout.Toggle(
                new GUIContent(
                    "Import from Processed folder",
                    "Uses building_meshes/Processed/ (baked UVs). Skips surface remesh on import."),
                _buildingsUseProcessed);

            if (_buildingsUseProcessed)
            {
                if (!Directory.Exists(processedFolder))
                {
                    EditorGUILayout.HelpBox(
                        "Processed folder does not exist yet. Run Bake Processed GLBs first.",
                        MessageType.Warning);
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        "Import reads baked GLBs only — Fast Import / surface processing are disabled.",
                        MessageType.Info);
                }
            }

            if (_buildingsUseProcessed && folderExists)
            {
                using (new EditorGUI.DisabledScope(_buildingsSurfaceSettings == null))
                {
                    if (GUILayout.Button("Bake Processed GLBs (raw → Processed/)", GUILayout.Height(26)))
                        RunBakeProcessedBuildings(dataset, rawBuildingsFolder);
                }
            }
            else if (rawExists && _buildingsSurfaceSettings != null)
            {
                if (GUILayout.Button("Bake Processed GLBs (raw → Processed/)", GUILayout.Height(26)))
                    RunBakeProcessedBuildings(dataset, rawBuildingsFolder);
            }

            if (_buildingsUseProcessed && _buildingsSurfaceSettings == null)
            {
                EditorGUILayout.HelpBox(
                    "Assign Surface Settings to bake processed GLBs.",
                    MessageType.Warning);
            }

            EditorGUILayout.Space(2);
            _buildingsSkipExisting = EditorGUILayout.Toggle("Skip Existing",                _buildingsSkipExisting);
            _buildingsAttachData   = EditorGUILayout.Toggle("Attach BuildingData Component", _buildingsAttachData);

            EditorGUILayout.Space(4);
            using (new EditorGUI.DisabledScope(_buildingsUseProcessed))
            {
                _buildingsFastImport = EditorGUILayout.Toggle(
                    new GUIContent(
                        "Fast Import",
                        "Skips facade/roof mesh processing for quicker bulk import (~50 tiles). " +
                        "Re-run surfaces later from the Buildings menu."),
                    _buildingsFastImport);
            }

            if (_buildingsUseProcessed)
                _buildingsFastImport = true;

            if (_buildingsFastImport && !_buildingsUseProcessed)
            {
                EditorGUILayout.HelpBox(
                    "Fast import: GLB → prefab with BuildingData only. " +
                    "After import, select prefabs and use " +
                    "ZG Connect → Buildings → Reprocess Surfaces On Selected Prefabs.",
                    MessageType.Info);
            }

            using (new EditorGUI.DisabledScope(_buildingsFastImport))
            {
                EditorGUI.BeginChangeCheck();
                using (new EditorGUI.DisabledScope(_buildingsUseProcessed))
                {
                    _buildingsProcessSurfaces = EditorGUILayout.Toggle(
                    new GUIContent(
                        "Process Facade / Roof Surfaces",
                        "Full surface pass: facade UV remap + repeating roof textures."),
                    _buildingsProcessSurfaces);
                }
                if (EditorGUI.EndChangeCheck() && _buildingsProcessSurfaces)
                {
                    _buildingsRoofOrthophotoUv = false;
                    _buildingsFastImport = false;
                }

                EditorGUI.BeginChangeCheck();
                _buildingsRoofOrthophotoUv = EditorGUILayout.Toggle(
                    new GUIContent(
                        "Roof Orthophoto UV (roofs only)",
                        "Maps roof UVs to the full tile (0–1). Used for import and Bake Processed GLBs. " +
                        "Facades keep original GLB UVs."),
                    _buildingsRoofOrthophotoUv);
                if (EditorGUI.EndChangeCheck() && _buildingsRoofOrthophotoUv)
                {
                    _buildingsProcessSurfaces = false;
                    _buildingsFastImport = false;
                }
            }

            if (_buildingsRoofOrthophotoUv && !_buildingsFastImport)
            {
                EditorGUILayout.HelpBox(
                    "Roof orthophoto mode needs tile ortofoto PNGs (import terrain ortho first) " +
                    "or a selected ortho basemap source from the Terrain tab scan.",
                    MessageType.Info);
            }

            _buildingsSurfaceSettings = (BuildingSurfaceSettings)EditorGUILayout.ObjectField(
                "Surface Settings",
                _buildingsSurfaceSettings,
                typeof(BuildingSurfaceSettings),
                false);

            if (!_buildingsFastImport
                && (_buildingsProcessSurfaces || _buildingsRoofOrthophotoUv)
                && _buildingsSurfaceSettings == null)
            {
                EditorGUILayout.HelpBox(
                    "Assign BuildingSurfaceSettings or run:\n" +
                    "ZG Connect → Buildings → Create Default Building Surface Settings",
                    MessageType.Warning);
            }

            if (GUILayout.Button("Generate Grid Placeholder Textures"))
            {
                BuildingSurfaceTextureGenerator.GenerateAll();
                string[] guids = AssetDatabase.FindAssets("t:BuildingSurfaceSettings");
                if (guids.Length > 0)
                    _buildingsSurfaceSettings = AssetDatabase.LoadAssetAtPath<BuildingSurfaceSettings>(
                        AssetDatabase.GUIDToAssetPath(guids[0]));
            }

            // ── Region filter ──────────────────────────────────────────────────
            EditorGUILayout.Space(4);
            _buildingsFilterByRegion = EditorGUILayout.Toggle("Filter by Region", _buildingsFilterByRegion);

            if (_buildingsFilterByRegion)
            {
                bool regionDefined = _tileMode == TileSelectionMode.Region
                                  || (_regionMinE != 0 || _regionMaxE != 0);

                if (!regionDefined)
                {
                    EditorGUILayout.HelpBox(
                        "No region defined — set a region in the Terrain tab first " +
                        "(Tile Selection → Region), then return here.",
                        MessageType.Warning);
                }
                else
                {
                    int matchCount = CountBuildingTilesInRegion(dataset);
                    EditorGUILayout.HelpBox(
                        $"E {_regionMinE}–{_regionMaxE}   N {_regionMinN}–{_regionMaxN}\n" +
                        (dataset != null
                            ? $"{matchCount} / {dataset.tiles.Count} tiles in region"
                            : "Assign dataset to see tile count."),
                        MessageType.None);
                }
            }

            EditorGUILayout.Space(4);

            bool canImport = dataset != null && folderExists;

            Color prev = GUI.backgroundColor;
            if (canImport) GUI.backgroundColor = new Color(0.3f, 0.75f, 0.3f);

            using (new EditorGUI.DisabledScope(!canImport))
            {
                if (GUILayout.Button("Import Buildings", GUILayout.Height(32)))
                    RunBuildingsImport(dataset, buildingsFolder);
            }

            bool canImportUnprocessed = canImport && !_buildingsUseProcessed && rawExists;
            using (new EditorGUI.DisabledScope(!canImportUnprocessed))
            {
                if (GUILayout.Button(
                        new GUIContent(
                            "Import Buildings (skip processed)",
                            "Import only raw tiles that do not yet have a GLB in Processed/."),
                        GUILayout.Height(26)))
                {
                    RunBuildingsImport(dataset, rawBuildingsFolder, skipProcessed: true);
                }
            }

            GUI.backgroundColor = prev;
        }

        private void RunBakeProcessedBuildings(CityDataset dataset, string rawBuildingsFolder)
        {
            if (_buildingsFilterByRegion && _regionInputMode == RegionInputMode.GPS)
                ComputeRegionFromGPS();

            if (_buildingsSurfaceSettings == null)
            {
                EditorUtility.DisplayDialog(
                    "ZG Connect — Bake",
                    "Assign BuildingSurfaceSettings before baking.",
                    "OK");
                return;
            }

            BasemapImportSource orthoSource = null;
            for (int i = 0; i < _basemapSources.Count; i++)
            {
                if (!_basemapSelected[i] || _basemapSources[i].Type != BasemapType.Ortho)
                    continue;
                var bm = _basemapSources[i];
                orthoSource = new BasemapImportSource
                {
                    BasemapId   = ZGConnectPathUtils.DeriveBasemapId(bm.FolderPath),
                    DisplayName = bm.DisplayName,
                    Type        = bm.Type,
                    Metadata    = bm.Metadata,
                    FolderPath  = bm.FolderPath,
                };
                break;
            }

            var bake = new BuildingsBakeSettings
            {
                OutputFolder            = _outputFolder,
                SurfaceSettings         = _buildingsSurfaceSettings,
                SkipExisting            = _buildingsSkipExisting,
                FilterByRegion          = _buildingsFilterByRegion,
                RegionMinE              = _regionMinE,
                RegionMaxE              = _regionMaxE,
                RegionMinN              = _regionMinN,
                RegionMaxN              = _regionMaxN,
                ProcessBuildingSurfaces = _buildingsProcessSurfaces,
                ProcessRoofOrthophotoUv = _buildingsRoofOrthophotoUv,
                RoofOrthophotoBasemapId = orthoSource?.BasemapId ?? "ortho",
                RoofOrthophotoSource    = orthoSource,
                BasemapMaxTextureSize   = (int)_basemapRes,
            };

            try
            {
                ZGConnectImporter.BakeProcessedBuildings(dataset, rawBuildingsFolder, bake);
            }
            catch (System.Exception ex)
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("Bake Error", ex.Message, "OK");
            }
        }

        private void RunBuildingsImport(
            CityDataset dataset,
            string      buildingsFolder,
            bool        skipProcessed = false)
        {
            if (_buildingsFilterByRegion && _regionInputMode == RegionInputMode.GPS)
                ComputeRegionFromGPS();

            bool fromProcessed = _buildingsUseProcessed && !skipProcessed;

            BasemapImportSource orthoSource = null;
            for (int i = 0; i < _basemapSources.Count; i++)
            {
                if (!_basemapSelected[i] || _basemapSources[i].Type != BasemapType.Ortho)
                    continue;
                var bm = _basemapSources[i];
                orthoSource = new BasemapImportSource
                {
                    BasemapId   = ZGConnectPathUtils.DeriveBasemapId(bm.FolderPath),
                    DisplayName = bm.DisplayName,
                    Type        = bm.Type,
                    Metadata    = bm.Metadata,
                    FolderPath  = bm.FolderPath,
                };
                break;
            }

            bool wantsMeshProcessing = !fromProcessed
                && (_buildingsProcessSurfaces || _buildingsRoofOrthophotoUv);

            var settings = new BuildingsImportSettings
            {
                OutputFolder       = _outputFolder,
                SkipExisting       = _buildingsSkipExisting,
                SkipProcessed      = skipProcessed,
                AttachBuildingData = _buildingsAttachData,
                FastImport         = (fromProcessed || _buildingsFastImport) && !wantsMeshProcessing,
                ProcessBuildingSurfaces = fromProcessed ? false : _buildingsProcessSurfaces,
                ProcessRoofOrthophotoUv = fromProcessed ? false : _buildingsRoofOrthophotoUv,
                RoofOrthophotoBasemapId = orthoSource?.BasemapId ?? "ortho",
                RoofOrthophotoSource = orthoSource,
                BasemapMaxTextureSize = (int)_basemapRes,
                SurfaceSettings    = _buildingsSurfaceSettings,
                FilterByRegion     = _buildingsFilterByRegion,
                RegionMinE         = _regionMinE,
                RegionMaxE         = _regionMaxE,
                RegionMinN         = _regionMinN,
                RegionMaxN         = _regionMaxN,
            };

            try
            {
                ZGConnectImporter.ImportBuildings(
                    dataset,
                    buildingsFolder,
                    settings,
                    (p, msg) => EditorUtility.DisplayProgressBar(
                        "ZG Connect — Buildings Import", msg, p));
            }
            catch (Exception ex)
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("Buildings Import Error", ex.Message, "OK");
                Debug.LogError($"[ZGConnect] Buildings import failed: {ex}");
            }
        }

        // ── Vegetation section ─────────────────────────────────────────────────

        private void DrawVegetationSection()
        {
            EditorGUILayout.LabelField("Vegetation Masks Import", EditorStyles.boldLabel);
            EditorGUILayout.Space(2);

            EditorGUILayout.HelpBox(
                "Imports {tileId}_vegetation.png from the Python OSM script into Unity and assigns " +
                "CityTileRecord.vegetationMask.\n" +
                "Textures: linear, Read/Write, uncompressed (required for runtime placement).",
                MessageType.Info);

            string datasetAssetPath = $"{_outputFolder}/Config/ZGConnectDataset.asset";
            CityDataset dataset     = AssetDatabase.LoadAssetAtPath<CityDataset>(datasetAssetPath);

            bool rootReady = !string.IsNullOrWhiteSpace(_rootFolder) && Directory.Exists(_rootFolder);

            if (!rootReady)
            {
                EditorGUILayout.HelpBox(
                    "Set the Dataset Root Folder at the top of the window.",
                    MessageType.Info);
                return;
            }

            if (dataset == null)
            {
                EditorGUILayout.HelpBox(
                    "No dataset found — run terrain import first.\n" +
                    $"Expected: {datasetAssetPath}",
                    MessageType.Info);
            }
            else
            {
                int withMask = ZGConnectVegetationMaskImporter.CountDatasetTilesWithMask(dataset);
                EditorGUILayout.LabelField(
                    $"Dataset  ·  {dataset.tiles.Count} tiles  ·  {withMask} with vegetation mask",
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Source & output", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                _vegSourceFolder = EditorGUILayout.TextField("Mask PNG folder", _vegSourceFolder);
                if (GUILayout.Button("…", GUILayout.Width(28)))
                {
                    string picked = EditorUtility.OpenFolderPanel(
                        "Vegetation masks folder",
                        string.IsNullOrEmpty(_vegSourceFolder) ? _rootFolder : _vegSourceFolder,
                        "");
                    if (!string.IsNullOrEmpty(picked))
                        _vegSourceFolder = picked;
                }
            }

            _vegAssetFolder = EditorGUILayout.TextField("Unity asset folder", _vegAssetFolder);

            int pngCount = ZGConnectVegetationMaskImporter.CountMaskFiles(_vegSourceFolder);
            if (string.IsNullOrWhiteSpace(_vegSourceFolder) || !Directory.Exists(_vegSourceFolder))
            {
                EditorGUILayout.HelpBox(
                    $"Expected folder under dataset root:\n{Path.Combine(_rootFolder, "vegetation_masks")}",
                    MessageType.Warning);
            }
            else
            {
                EditorGUILayout.LabelField(
                    $"Found {pngCount} *_vegetation.png file(s)",
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Options", EditorStyles.boldLabel);

            _vegSkipExisting = EditorGUILayout.Toggle(
                new GUIContent(
                    "Skip tiles that already have a mask",
                    "Leaves existing vegetationMask references unchanged."),
                _vegSkipExisting);

            _vegFilterByRegion = EditorGUILayout.Toggle("Filter by region", _vegFilterByRegion);

            if (_vegFilterByRegion)
            {
                bool regionDefined = _tileMode == TileSelectionMode.Region
                                  || (_regionMinE != 0 || _regionMaxE != 0);

                if (!regionDefined)
                {
                    EditorGUILayout.HelpBox(
                        "No region defined — set a region on the Terrain tab first.",
                        MessageType.Warning);
                }
                else if (dataset != null)
                {
                    int tilesInRegion = CountBuildingTilesInRegion(dataset);
                    EditorGUILayout.HelpBox(
                        $"E {_regionMinE}–{_regionMaxE}   N {_regionMinN}–{_regionMaxN}\n" +
                        $"{tilesInRegion} dataset tiles in region (masks outside region are ignored).",
                        MessageType.None);
                }
            }

            EditorGUILayout.Space(4);

            bool canImport = dataset != null
                          && pngCount > 0
                          && Directory.Exists(_vegSourceFolder);

            Color prev = GUI.backgroundColor;
            if (canImport) GUI.backgroundColor = new Color(0.3f, 0.75f, 0.3f);

            using (new EditorGUI.DisabledScope(!canImport))
            {
                if (GUILayout.Button("Import Vegetation Masks", GUILayout.Height(32)))
                    RunVegetationImport(dataset);
            }

            GUI.backgroundColor = prev;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Runtime (after import)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "On Terrain Streaming Controller: enable Stream Vegetation, assign VegetationRuleSet " +
                "and VegetationPrototypes (mesh + materials on drvo_* assets).",
                MessageType.None);
        }

        private void RunVegetationImport(CityDataset dataset)
        {
            if (_vegFilterByRegion && _regionInputMode == RegionInputMode.GPS)
                ComputeRegionFromGPS();

            var settings = new VegetationMaskImportSettings
            {
                Dataset         = dataset,
                SourceFolder    = _vegSourceFolder,
                AssetFolder     = _vegAssetFolder,
                SkipExisting    = _vegSkipExisting,
                FilterByRegion  = _vegFilterByRegion,
                RegionMinE      = _regionMinE,
                RegionMaxE      = _regionMaxE,
                RegionMinN      = _regionMinN,
                RegionMaxN      = _regionMaxN,
            };

            VegetationMaskImportResult result = ZGConnectVegetationMaskImporter.Import(settings);

            string msg =
                $"Assigned: {result.Assigned}\n" +
                $"Skipped (already set): {result.Skipped}\n" +
                $"Outside region: {result.RegionSkipped}\n" +
                $"PNG with no dataset tile: {result.NoDatasetTile}\n" +
                $"Unrecognized filenames: {result.UnrecognizedFiles}" +
                (result.Cancelled ? "\n\nCancelled by user." : "") +
                $"\n\nAssets: {_vegAssetFolder}";

            Debug.Log($"[ZGConnect] Vegetation mask import — {msg.Replace("\n", ", ")}");
            EditorUtility.DisplayDialog(
                result.Cancelled ? "Import cancelled" : "Vegetation masks imported",
                msg,
                "OK");
        }

        // ── Utilities ──────────────────────────────────────────────────────────

        private static string[] BuildDisplayNames<T>(List<T> list, Func<T, string> selector)
        {
            var names = new string[list.Count];
            for (int i = 0; i < list.Count; i++)
                names[i] = selector(list[i]);
            return names;
        }

        private static void DrawHorizontalRule()
        {
            Rect r = EditorGUILayout.GetControlRect(false, 1f);
            EditorGUI.DrawRect(r, new Color(0.4f, 0.4f, 0.4f, 0.6f));
        }
    }
}
