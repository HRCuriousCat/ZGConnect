using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using ZGConnect;
using ZGConnect.Editor;
using ZGConnect.RealtimeStreaming;

namespace ZGConnect.RealtimeStreaming.Editor
{
    public class ZGConnectRealtimeStreamerWindow : EditorWindow
    {
        const string PrefsPrefix = "ZGConnect.RealtimeImporter.";

        string _rootFolder = "";
        bool _hasScanned;
        string _scanError;
        DatasetFolderScanner.ScanResult _scan = new();

        int _selectedHeightmapIdx;
        bool[] _basemapSelected = System.Array.Empty<bool>();

        bool _packTerrain = true;
        bool _packTerrainBundles = true;
        bool _packOrtho = true;
        bool _packTiled = true;
        bool _packFacadeBuildings = true;
        bool _packOrthoRoofBuildings = true;
        bool _packVegetation = true;
        bool _packBuildingLod = true;
        bool _packBuildingBundles = true;
        bool _packCopyBuildingMetadataBinary = true;
        bool _bakeVegetationBundles = true;
        int _packBuildingColliderMode = 1;
        bool _packUncompressedAssetBundles;
        bool _skipExisting = true;

        BuildingSurfaceSettings _buildingSurfaceSettings;
        Material _roofOrthophotoMaterialTemplate;
        VegetationRuleSet _vegetationRuleSet;

        int _heightmapResolution;
        int _orthoMaxRes = 2048;
        bool _orthoCrunch = true;
        int _orthoCrunchQuality = 75;

        int _regionMinE;
        int _regionMaxE;
        int _regionMinN;
        int _regionMaxN;
        bool _useRegionFilter;

        bool _isPacking;
        bool _parametersExpanded = true;
        bool _progressExpanded;
        EditorCoroutineRunner _packRunner;
        readonly RealtimePackProgressTracker _packProgress = new();

        Texture2D _logo;
        Vector2 _scroll;

        [MenuItem("ZG Connect/RealTime Asset Importer")]
        public static void Open()
        {
            var window = GetWindow<ZGConnectRealtimeStreamerWindow>("RealTime Asset Importer");
            window.minSize = new Vector2(460, 560);
        }

        void OnEnable()
        {
            _logo = ZGConnectEditorBranding.LoadLogo();
            if (_scan == null)
            {
                _scan = new DatasetFolderScanner.ScanResult();
                _hasScanned = false;
            }

            LoadSettings();

            if (_buildingSurfaceSettings == null)
                _buildingSurfaceSettings = LoadDefaultBuildingSurfaceSettings();
            if (_vegetationRuleSet == null)
                _vegetationRuleSet = LoadDefaultVegetationRuleSet();

            if (!string.IsNullOrWhiteSpace(_rootFolder) && Directory.Exists(_rootFolder))
                ScanRoot(restoreBasemapSelection: true);
            else
                RegisterRegionBridge();
        }

        void OnDisable()
        {
            SaveSettings();
            _packRunner?.Stop();
            if (ZGConnectImportRegionBridge.RepaintImporter == (System.Action)Repaint)
                ZGConnectImportRegionBridge.Clear();
        }

        void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            ZGConnectEditorBranding.DrawWindowBackground(this);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            ZGConnectEditorBranding.DrawWindowHeader(_logo, position.width, "RealTime Asset Importer");

            DrawParametersAccordion();
            DrawProgressAccordion();
            EditorGUILayout.EndScrollView();

            if (EditorGUI.EndChangeCheck())
                SaveSettings();
        }

        void DrawParametersAccordion()
        {
            DrawAccordionSection("Parameters", ref _parametersExpanded, () =>
            {
                DrawDatasetRoot();
                DrawDiscoveredSources();
                DrawPackOptions();
                DrawRegionFilter();
                EditorGUILayout.Space(6);
                DrawPackActions();
            });
        }

        void DrawProgressAccordion()
        {
            DrawAccordionSection("Progress", ref _progressExpanded, DrawPackProgressContent);
        }

        static void DrawAccordionSection(string title, ref bool expanded, System.Action drawContent)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            expanded = EditorGUILayout.Foldout(expanded, title, true, EditorStyles.foldoutHeader);
            if (expanded)
            {
                EditorGUILayout.Space(4);
                drawContent?.Invoke();
            }

            EditorGUILayout.EndVertical();
        }

        void DrawDatasetRoot()
        {
            EditorGUILayout.LabelField("External dataset root", EditorStyles.miniBoldLabel);
            EditorGUILayout.BeginHorizontal();
            _rootFolder = EditorGUILayout.TextField(_rootFolder);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                string picked = EditorUtility.OpenFolderPanel("Dataset root", _rootFolder, "");
                if (!string.IsNullOrEmpty(picked))
                    _rootFolder = picked;
            }
            if (GUILayout.Button("Scan", GUILayout.Width(60)))
                ScanRoot(restoreBasemapSelection: true);
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_scanError))
                EditorGUILayout.HelpBox(_scanError, MessageType.Warning);
        }

        void DrawDiscoveredSources()
        {
            if (!_hasScanned || _scan == null)
                return;

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Discovered sources", EditorStyles.miniBoldLabel);

            if (_scan.HeightmapSources != null && _scan.HeightmapSources.Count > 0)
            {
                var names = _scan.HeightmapSources.ConvertAll(s => s.DisplayName).ToArray();
                _selectedHeightmapIdx = EditorGUILayout.Popup("Heightmap", _selectedHeightmapIdx, names);
            }

            if (_scan.BasemapSources == null)
                return;

            for (int i = 0; i < _scan.BasemapSources.Count; i++)
            {
                if (_basemapSelected.Length != _scan.BasemapSources.Count)
                    _basemapSelected = Enumerable.Repeat(true, _scan.BasemapSources.Count).ToArray();
                _basemapSelected[i] = EditorGUILayout.ToggleLeft(
                    _scan.BasemapSources[i].DisplayName, _basemapSelected[i]);
            }

            if (!string.IsNullOrEmpty(_scan.VegetationMasksFolder))
                EditorGUILayout.LabelField("Vegetation masks", _scan.VegetationMasksFolder);
        }

        void DrawPackOptions()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Pack options", EditorStyles.miniBoldLabel);
            _packTerrain = EditorGUILayout.ToggleLeft("Terrain (heightmap)", _packTerrain);
            using (new EditorGUI.DisabledScope(!_packTerrain))
                _packTerrainBundles = EditorGUILayout.ToggleLeft(
                    "Pre-bake TerrainData AssetBundles (fast runtime)", _packTerrainBundles);
            _packOrtho = EditorGUILayout.ToggleLeft("Ortho basemaps", _packOrtho);
            if (_packTerrain && _packTerrainBundles && !_packOrtho)
            {
                EditorGUILayout.HelpBox(
                    "Terrain bundles need ortho basemaps for satellite textures. " +
                    "Ortho will be auto-enabled during pack if an ortho source is selected below.",
                    MessageType.Warning);
            }
            _packTiled = EditorGUILayout.ToggleLeft("Tiled basemaps", _packTiled);
            _packFacadeBuildings = EditorGUILayout.ToggleLeft("Facade buildings", _packFacadeBuildings);
            _packOrthoRoofBuildings = EditorGUILayout.ToggleLeft("Ortho-roof buildings", _packOrthoRoofBuildings);
            using (new EditorGUI.DisabledScope(!_packFacadeBuildings && !_packOrthoRoofBuildings))
            {
                _packCopyBuildingMetadataBinary = EditorGUILayout.ToggleLeft(
                    "Pack building metadata binary (.bytes)", _packCopyBuildingMetadataBinary);
            }

            if (_packCopyBuildingMetadataBinary && (_packFacadeBuildings || _packOrthoRoofBuildings))
            {
                EditorGUILayout.HelpBox(
                    "Copies existing building_meshes_bin / building_meshes_ortho_bin when present; " +
                    "otherwise generates .bytes from buildings_{tileId}.json during pack. " +
                    "Use ZG Connect → Convert Building Metadata to Binary to pre-build binaries in the source dataset.",
                    MessageType.None);
            }

            _packVegetation = EditorGUILayout.ToggleLeft("Vegetation masks", _packVegetation);
            _packBuildingLod = EditorGUILayout.ToggleLeft("Building LOD1 boxes (pack-time)", _packBuildingLod);
            _packBuildingBundles = EditorGUILayout.ToggleLeft(
                "Bake building prefab bundles (materials, combine, colliders)", _packBuildingBundles);
            using (new EditorGUI.DisabledScope(!_packBuildingBundles))
            {
                _packBuildingColliderMode = EditorGUILayout.Popup(
                    "Building colliders (pack bake)",
                    _packBuildingColliderMode,
                    new[] { "Box (fast pack)", "Convex mesh (accurate physics)" });
                _packUncompressedAssetBundles = EditorGUILayout.ToggleLeft(
                    "Uncompressed asset bundles (faster archive, larger files)",
                    _packUncompressedAssetBundles);
            }

            using (new EditorGUI.DisabledScope(!_packVegetation))
                _bakeVegetationBundles = EditorGUILayout.ToggleLeft(
                    "Bake vegetation instance bundles", _bakeVegetationBundles);

            if (_packBuildingBundles || _bakeVegetationBundles)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Bake assets", EditorStyles.miniBoldLabel);
            }

            using (new EditorGUI.DisabledScope(!_packBuildingBundles))
            {
                _buildingSurfaceSettings = (BuildingSurfaceSettings)EditorGUILayout.ObjectField(
                    "Building Surface Settings",
                    _buildingSurfaceSettings,
                    typeof(BuildingSurfaceSettings),
                    false);

                if (_packOrthoRoofBuildings)
                {
                    _roofOrthophotoMaterialTemplate = (Material)EditorGUILayout.ObjectField(
                        "Roof orthophoto material",
                        _roofOrthophotoMaterialTemplate,
                        typeof(Material),
                        false);
                }

                if (_packBuildingBundles &&
                    (_packFacadeBuildings || _packOrthoRoofBuildings) &&
                    _buildingSurfaceSettings == null)
                {
                    EditorGUILayout.HelpBox(
                        "Assign Building Surface Settings so pack bake can apply facade/roof materials " +
                        "to combined meshes. Create one via ZG Connect → Buildings → Create Default Building Surface Settings.",
                        MessageType.Warning);
                }
            }

            using (new EditorGUI.DisabledScope(!_bakeVegetationBundles))
            {
                _vegetationRuleSet = (VegetationRuleSet)EditorGUILayout.ObjectField(
                    "Vegetation Rule Set",
                    _vegetationRuleSet,
                    typeof(VegetationRuleSet),
                    false);

                if (_bakeVegetationBundles && _vegetationRuleSet == null)
                {
                    EditorGUILayout.HelpBox(
                        "Assign a Vegetation Rule Set to bake vegetation instance bundles.",
                        MessageType.Warning);
                }
            }

            if (_packBuildingBundles || _bakeVegetationBundles)
            {
                EditorGUILayout.HelpBox(
                    "Bake-at-pack moves mesh combine, colliders, and vegetation into the pack step. " +
                    "Box colliders and stripped LOD geometry greatly reduce bundle size and archive time.",
                    MessageType.Info);
            }

            _heightmapResolution = EditorGUILayout.IntPopup(
                "Heightmap resolution",
                _heightmapResolution > 0 ? _heightmapResolution : 1025,
                new[] { "513", "1025" },
                new[] { 513, 1025 });
            _orthoMaxRes = EditorGUILayout.IntPopup(
                "Ortho max resolution",
                _orthoMaxRes,
                new[] { "512", "1024", "2048" },
                new[] { 512, 1024, 2048 });

            using (new EditorGUI.DisabledScope(!_packTerrainBundles))
            {
                _orthoCrunch = EditorGUILayout.ToggleLeft(
                    "Ortho Crunch compression (bundle staging)", _orthoCrunch);
                using (new EditorGUI.DisabledScope(!_orthoCrunch))
                {
                    _orthoCrunchQuality = EditorGUILayout.IntSlider(
                        "Crunch quality", _orthoCrunchQuality, 0, 100);
                }

                if (!_packTerrainBundles)
                {
                    EditorGUILayout.HelpBox(
                        "Crunch applies when pre-baking TerrainData AssetBundles.",
                        MessageType.None);
                }
                else if (_orthoCrunch)
                {
                    EditorGUILayout.HelpBox(
                        "Higher quality improves ortho detail but slows bundle staging import. " +
                        "Disable Crunch for faster packs (slightly larger bundles).",
                        MessageType.None);
                }
            }

            EditorGUILayout.Space(4);
            _skipExisting = EditorGUILayout.ToggleLeft("Skip existing packed tiles", _skipExisting);
            EditorGUILayout.HelpBox(
                "Incremental pack: expand the EPSG region and pack again. Already packed tiles, " +
                "supertiles, and bundles are reused; only new or changed tiles are processed.",
                MessageType.None);
        }

        void DrawRegionFilter()
        {
            EditorGUILayout.Space(6);
            _useRegionFilter = EditorGUILayout.ToggleLeft("Filter tiles by EPSG region", _useRegionFilter);
            if (_useRegionFilter)
            {
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.BeginHorizontal();
                _regionMinE = EditorGUILayout.IntField("Min E", _regionMinE);
                _regionMaxE = EditorGUILayout.IntField("Max E", _regionMaxE);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.BeginHorizontal();
                _regionMinN = EditorGUILayout.IntField("Min N", _regionMinN);
                _regionMaxN = EditorGUILayout.IntField("Max N", _regionMaxN);
                EditorGUILayout.EndHorizontal();
                if (EditorGUI.EndChangeCheck())
                    AlignRegionToHlodGrid();
                EditorGUILayout.HelpBox(
                    "Region bounds snap to the 4×4 tile grid so pack borders align with HLOD supertiles.",
                    MessageType.None);
                if (GUILayout.Button("Open tile map picker"))
                    ZGConnectTileMapWindow.OpenFromImporter();
            }
        }

        void DrawPackProgressContent()
        {
            if (!_packProgress.HasStarted)
            {
                EditorGUILayout.HelpBox("Pack progress will appear here when you start packing.", MessageType.None);
                return;
            }

            Rect masterRect = GUILayoutUtility.GetRect(22f, 24f, GUILayout.ExpandWidth(true));
            EditorGUI.ProgressBar(masterRect, _packProgress.Master, $"Overall  {_packProgress.Master * 100f:0}%");

            if (!string.IsNullOrEmpty(_packProgress.ResultMessage))
            {
                var messageType = _packProgress.IsFailed ? MessageType.Error : MessageType.Info;
                EditorGUILayout.HelpBox(_packProgress.ResultMessage, messageType);
            }

            EditorGUILayout.Space(4);
            foreach (RealtimePackProgressTracker.StageSlot stage in _packProgress.Stages)
                DrawStageProgressBar(stage);

            if (_packProgress.IsComplete && !_isPacking)
            {
                EditorGUILayout.Space(4);
                if (GUILayout.Button("Clear progress"))
                {
                    _packProgress.Clear();
                    _parametersExpanded = true;
                    _progressExpanded = false;
                }
            }
        }

        static void DrawStageProgressBar(RealtimePackProgressTracker.StageSlot stage)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(stage.Label, GUILayout.Width(110));
            Rect barRect = GUILayoutUtility.GetRect(14f, 16f, GUILayout.ExpandWidth(true));
            string barText = !stage.Enabled
                ? "Skipped"
                : !string.IsNullOrEmpty(stage.Detail)
                    ? stage.Detail
                    : $"{stage.Progress * 100f:0}%";
            EditorGUI.ProgressBar(barRect, stage.Enabled ? stage.Progress : 0f, barText);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(2);
        }

        void DrawPackActions()
        {
            GUI.enabled = _hasScanned && _scan != null && _scan.Success && !_isPacking;
            if (GUILayout.Button(_isPacking ? "Packing..." : "Pack Dataset", GUILayout.Height(28)))
                PackDataset();
            if (GUILayout.Button("Create Streamer in Scene"))
                CreateStreamerInScene();
            GUI.enabled = true;
        }

        void ScanRoot(bool restoreBasemapSelection = false)
        {
            _scan = DatasetFolderScanner.Scan(_rootFolder);
            _hasScanned = true;
            _scanError = _scan.Error;
            _selectedHeightmapIdx = Mathf.Clamp(
                _selectedHeightmapIdx, 0, Mathf.Max(0, _scan.HeightmapSources.Count - 1));

            if (_scan.BasemapSources.Count > 0)
            {
                _basemapSelected = new bool[_scan.BasemapSources.Count];
                if (restoreBasemapSelection)
                    RestoreBasemapSelection();
                else
                {
                    for (int i = 0; i < _basemapSelected.Length; i++)
                        _basemapSelected[i] = true;
                }
            }
            else
            {
                _basemapSelected = System.Array.Empty<bool>();
            }

            RegisterRegionBridge();
            RealtimeStreamingMapGeorefUtility.InvalidateCaches();
            RefreshOpenTileMaps();
            SaveSettings();
        }

        void RestoreBasemapSelection()
        {
            if (_basemapSelected == null || _scan?.BasemapSources == null)
                return;

            string savedPaths = EditorPrefs.GetString(PrefsPrefix + "SelectedBasemaps", "");
            if (string.IsNullOrEmpty(savedPaths))
            {
                for (int i = 0; i < _basemapSelected.Length; i++)
                    _basemapSelected[i] = true;
                return;
            }

            var saved = new HashSet<string>(
                savedPaths.Split(new[] { '|' }, System.StringSplitOptions.RemoveEmptyEntries));
            for (int i = 0; i < _scan.BasemapSources.Count; i++)
                _basemapSelected[i] = saved.Contains(_scan.BasemapSources[i].FolderPath);
        }

        void LoadSettings()
        {
            _rootFolder = EditorPrefs.GetString(PrefsPrefix + "RootFolder", _rootFolder);
            _selectedHeightmapIdx = EditorPrefs.GetInt(PrefsPrefix + "HeightmapIdx", _selectedHeightmapIdx);

            _packTerrain = EditorPrefs.GetBool(PrefsPrefix + "PackTerrain", _packTerrain);
            _packTerrainBundles = EditorPrefs.GetBool(PrefsPrefix + "PackTerrainBundles", _packTerrainBundles);
            _packOrtho = EditorPrefs.GetBool(PrefsPrefix + "PackOrtho", _packOrtho);
            _packTiled = EditorPrefs.GetBool(PrefsPrefix + "PackTiled", _packTiled);
            _packFacadeBuildings = EditorPrefs.GetBool(PrefsPrefix + "PackFacadeBuildings", _packFacadeBuildings);
            _packOrthoRoofBuildings = EditorPrefs.GetBool(PrefsPrefix + "PackOrthoRoofBuildings", _packOrthoRoofBuildings);
            _packVegetation = EditorPrefs.GetBool(PrefsPrefix + "PackVegetation", _packVegetation);
            _packBuildingLod = EditorPrefs.GetBool(PrefsPrefix + "PackBuildingLod", _packBuildingLod);
            _packBuildingBundles = EditorPrefs.GetBool(PrefsPrefix + "PackBuildingBundles", _packBuildingBundles);
            _packCopyBuildingMetadataBinary = EditorPrefs.GetBool(
                PrefsPrefix + "PackCopyBuildingMetadataBinary", _packCopyBuildingMetadataBinary);
            _bakeVegetationBundles = EditorPrefs.GetBool(PrefsPrefix + "BakeVegetationBundles", _bakeVegetationBundles);
            _packBuildingColliderMode = EditorPrefs.GetInt(PrefsPrefix + "PackBuildingColliderMode", _packBuildingColliderMode);
            _packUncompressedAssetBundles = EditorPrefs.GetBool(
                PrefsPrefix + "PackUncompressedAssetBundles", _packUncompressedAssetBundles);
            _skipExisting = EditorPrefs.GetBool(PrefsPrefix + "SkipExisting", _skipExisting);

            _heightmapResolution = EditorPrefs.GetInt(PrefsPrefix + "HeightmapResolution", _heightmapResolution);
            _orthoMaxRes = EditorPrefs.GetInt(PrefsPrefix + "OrthoMaxRes", _orthoMaxRes);
            _orthoCrunch = EditorPrefs.GetBool(PrefsPrefix + "OrthoCrunch", _orthoCrunch);
            _orthoCrunchQuality = EditorPrefs.GetInt(PrefsPrefix + "OrthoCrunchQuality", _orthoCrunchQuality);

            _useRegionFilter = EditorPrefs.GetBool(PrefsPrefix + "UseRegionFilter", _useRegionFilter);
            _regionMinE = EditorPrefs.GetInt(PrefsPrefix + "RegionMinE", _regionMinE);
            _regionMaxE = EditorPrefs.GetInt(PrefsPrefix + "RegionMaxE", _regionMaxE);
            _regionMinN = EditorPrefs.GetInt(PrefsPrefix + "RegionMinN", _regionMinN);
            _regionMaxN = EditorPrefs.GetInt(PrefsPrefix + "RegionMaxN", _regionMaxN);

            _parametersExpanded = EditorPrefs.GetBool(PrefsPrefix + "ParametersExpanded", _parametersExpanded);
            _progressExpanded = EditorPrefs.GetBool(PrefsPrefix + "ProgressExpanded", _progressExpanded);

            _buildingSurfaceSettings = LoadAssetFromPrefs<BuildingSurfaceSettings>(
                PrefsPrefix + "BuildingSurfaceSettings");
            _roofOrthophotoMaterialTemplate = LoadAssetFromPrefs<Material>(
                PrefsPrefix + "RoofOrthophotoMaterial");
            _vegetationRuleSet = LoadAssetFromPrefs<VegetationRuleSet>(
                PrefsPrefix + "VegetationRuleSet");
        }

        void SaveSettings()
        {
            EditorPrefs.SetString(PrefsPrefix + "RootFolder", _rootFolder ?? "");
            EditorPrefs.SetInt(PrefsPrefix + "HeightmapIdx", _selectedHeightmapIdx);

            EditorPrefs.SetBool(PrefsPrefix + "PackTerrain", _packTerrain);
            EditorPrefs.SetBool(PrefsPrefix + "PackTerrainBundles", _packTerrainBundles);
            EditorPrefs.SetBool(PrefsPrefix + "PackOrtho", _packOrtho);
            EditorPrefs.SetBool(PrefsPrefix + "PackTiled", _packTiled);
            EditorPrefs.SetBool(PrefsPrefix + "PackFacadeBuildings", _packFacadeBuildings);
            EditorPrefs.SetBool(PrefsPrefix + "PackOrthoRoofBuildings", _packOrthoRoofBuildings);
            EditorPrefs.SetBool(PrefsPrefix + "PackVegetation", _packVegetation);
            EditorPrefs.SetBool(PrefsPrefix + "PackBuildingLod", _packBuildingLod);
            EditorPrefs.SetBool(PrefsPrefix + "PackBuildingBundles", _packBuildingBundles);
            EditorPrefs.SetBool(PrefsPrefix + "PackCopyBuildingMetadataBinary", _packCopyBuildingMetadataBinary);
            EditorPrefs.SetBool(PrefsPrefix + "BakeVegetationBundles", _bakeVegetationBundles);
            EditorPrefs.SetInt(PrefsPrefix + "PackBuildingColliderMode", _packBuildingColliderMode);
            EditorPrefs.SetBool(PrefsPrefix + "PackUncompressedAssetBundles", _packUncompressedAssetBundles);
            EditorPrefs.SetBool(PrefsPrefix + "SkipExisting", _skipExisting);

            EditorPrefs.SetInt(PrefsPrefix + "HeightmapResolution", _heightmapResolution);
            EditorPrefs.SetInt(PrefsPrefix + "OrthoMaxRes", _orthoMaxRes);
            EditorPrefs.SetBool(PrefsPrefix + "OrthoCrunch", _orthoCrunch);
            EditorPrefs.SetInt(PrefsPrefix + "OrthoCrunchQuality", _orthoCrunchQuality);

            EditorPrefs.SetBool(PrefsPrefix + "UseRegionFilter", _useRegionFilter);
            EditorPrefs.SetInt(PrefsPrefix + "RegionMinE", _regionMinE);
            EditorPrefs.SetInt(PrefsPrefix + "RegionMaxE", _regionMaxE);
            EditorPrefs.SetInt(PrefsPrefix + "RegionMinN", _regionMinN);
            EditorPrefs.SetInt(PrefsPrefix + "RegionMaxN", _regionMaxN);

            EditorPrefs.SetBool(PrefsPrefix + "ParametersExpanded", _parametersExpanded);
            EditorPrefs.SetBool(PrefsPrefix + "ProgressExpanded", _progressExpanded);

            SaveAssetToPrefs(PrefsPrefix + "BuildingSurfaceSettings", _buildingSurfaceSettings);
            SaveAssetToPrefs(PrefsPrefix + "RoofOrthophotoMaterial", _roofOrthophotoMaterialTemplate);
            SaveAssetToPrefs(PrefsPrefix + "VegetationRuleSet", _vegetationRuleSet);

            if (_scan?.BasemapSources != null && _basemapSelected != null &&
                _basemapSelected.Length == _scan.BasemapSources.Count)
            {
                var selectedPaths = new List<string>();
                for (int i = 0; i < _scan.BasemapSources.Count; i++)
                {
                    if (_basemapSelected[i] && !string.IsNullOrEmpty(_scan.BasemapSources[i].FolderPath))
                        selectedPaths.Add(_scan.BasemapSources[i].FolderPath);
                }

                EditorPrefs.SetString(PrefsPrefix + "SelectedBasemaps", string.Join("|", selectedPaths));
            }
        }

        static T LoadAssetFromPrefs<T>(string key) where T : Object
        {
            string guid = EditorPrefs.GetString(key, "");
            if (string.IsNullOrEmpty(guid))
                return null;

            string path = AssetDatabase.GUIDToAssetPath(guid);
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<T>(path);
        }

        static void SaveAssetToPrefs(string key, Object asset)
        {
            string guid = asset != null
                ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset))
                : "";
            EditorPrefs.SetString(key, guid);
        }

        void RegisterRegionBridge()
        {
            ZGConnectImportRegionBridge.AlignSelectionToPackGrid = true;
            ZGConnectImportRegionBridge.ApplyTargetLabel = "Import Manager";
            ZGConnectImportRegionBridge.GetTiles = () =>
            {
                if (_scan?.HeightmapSources == null || _scan.HeightmapSources.Count == 0)
                    return new List<HeightmapTileJson>();
                return _scan.HeightmapSources[Mathf.Clamp(_selectedHeightmapIdx, 0, _scan.HeightmapSources.Count - 1)]
                    .Metadata.Tiles;
            };
            ZGConnectImportRegionBridge.GetOverviewGeorefBounds = () =>
            {
                if (RealtimeStreamingMapGeorefUtility.TryGetTileUnion(
                        ZGConnectImportRegionBridge.GetTiles?.Invoke(),
                        out ZGConnectMapGeorefBounds bounds))
                {
                    return (bounds.MinE, bounds.MaxE, bounds.MinN, bounds.MaxN);
                }

                return (0, 0, 0, 0);
            };
            ZGConnectImportRegionBridge.GetBackgroundMapGeorefBounds = () =>
            {
                if (RealtimeStreamingMapGeorefUtility.TryGetBackgroundMapGeoref(out ZGConnectMapGeorefBounds bounds))
                    return (bounds.MinE, bounds.MaxE, bounds.MinN, bounds.MaxN);

                if (RealtimeStreamingMapGeorefUtility.TryGetTileUnion(
                        ZGConnectImportRegionBridge.GetTiles?.Invoke(),
                        out bounds))
                {
                    return (bounds.MinE, bounds.MaxE, bounds.MinN, bounds.MaxN);
                }

                return (0, 0, 0, 0);
            };
            ZGConnectImportRegionBridge.GetRegionEpsg = () => (_regionMinE, _regionMaxE, _regionMinN, _regionMaxN);
            ZGConnectImportRegionBridge.ApplyRegionEpsg = (minE, maxE, minN, maxN) =>
            {
                _regionMinE = minE;
                _regionMaxE = maxE;
                _regionMinN = minN;
                _regionMaxN = maxN;
                _useRegionFilter = true;
                AlignRegionToHlodGrid();
                SaveSettings();
                Repaint();
            };
            ZGConnectImportRegionBridge.RepaintImporter = Repaint;
            ZGConnectImportRegionBridge.GetPackedTileIds = GetPackedTileIds;
        }

        static void RepaintOpenTileMap()
        {
            ZGConnectTileMapWindow[] maps = Resources.FindObjectsOfTypeAll<ZGConnectTileMapWindow>();
            foreach (ZGConnectTileMapWindow map in maps)
                map.Repaint();
        }

        static void RefreshOpenTileMaps()
        {
            ZGConnectTileMapWindow[] maps = Resources.FindObjectsOfTypeAll<ZGConnectTileMapWindow>();
            foreach (ZGConnectTileMapWindow map in maps)
                map.RefreshFromImporter();
        }

        static HashSet<string> GetPackedTileIds()
        {
            string outputRoot = ZGConnectPathUtils.AssetPathToFullPath(RuntimeStreamingPaths.PackedDatasetAssetRoot);
            StreamingDatasetManifest manifest = RealtimePackReuseUtility.TryLoadExistingManifest(outputRoot);
            if (manifest?.Tiles == null || manifest.Tiles.Count == 0)
                return new HashSet<string>();

            var ids = new HashSet<string>();
            foreach (StreamingTileEntry tile in manifest.Tiles)
            {
                if (string.IsNullOrEmpty(tile.TileId))
                    continue;

                if (RealtimePackReuseUtility.HasStreamableTerrain(tile, outputRoot))
                    ids.Add(tile.TileId);
            }

            return ids;
        }

        static BuildingSurfaceSettings LoadDefaultBuildingSurfaceSettings()
        {
            string[] guids = AssetDatabase.FindAssets("t:BuildingSurfaceSettings");
            if (guids.Length == 0)
                return null;
            return AssetDatabase.LoadAssetAtPath<BuildingSurfaceSettings>(
                AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        static VegetationRuleSet LoadDefaultVegetationRuleSet()
        {
            string[] guids = AssetDatabase.FindAssets("t:VegetationRuleSet");
            if (guids.Length == 0)
                return null;
            return AssetDatabase.LoadAssetAtPath<VegetationRuleSet>(
                AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        static VegetationPrototype[] LoadDefaultVegetationPrototypes()
        {
            string[] guids = AssetDatabase.FindAssets("t:VegetationPrototype");
            if (guids.Length == 0)
                return null;

            var prototypes = new List<VegetationPrototype>();
            foreach (string guid in guids)
            {
                var prototype = AssetDatabase.LoadAssetAtPath<VegetationPrototype>(
                    AssetDatabase.GUIDToAssetPath(guid));
                if (prototype != null)
                    prototypes.Add(prototype);
            }

            return prototypes.Count > 0 ? prototypes.ToArray() : null;
        }

        /// <summary>
        /// Must match runtime <see cref="RealtimeStreamingController.ResolveVegetationPrototypes"/>
        /// so baked vegetation bundle prototype indices stay valid.
        /// </summary>
        static VegetationPrototype[] ResolveVegetationPrototypesForPack(VegetationRuleSet ruleSet)
        {
            VegetationPrototype[] fromRuleSet = VegetationPrototypeUtility.CollectFromRuleSet(ruleSet);
            if (fromRuleSet != null && fromRuleSet.Length > 0)
                return fromRuleSet;

            return LoadDefaultVegetationPrototypes();
        }

        void AlignRegionToHlodGrid()
        {
            if (!_useRegionFilter || _regionMaxE <= _regionMinE || _regionMaxN <= _regionMinN)
                return;
            if (_scan?.HeightmapSources == null || _scan.HeightmapSources.Count == 0)
                return;

            int tileSize = _scan.HeightmapSources[Mathf.Clamp(_selectedHeightmapIdx, 0, _scan.HeightmapSources.Count - 1)]
                .Metadata.Settings.TileSizeMeters;
            HlodGridZones.AlignRegionEpsg(
                _regionMinE, _regionMaxE, _regionMinN, _regionMaxN,
                tileSize, HlodGridZones.PackRegionAlignFactor,
                out _regionMinE, out _regionMaxE, out _regionMinN, out _regionMaxN);
        }

        void PackDataset()
        {
            if (_scan?.HeightmapSources == null || _scan.HeightmapSources.Count == 0)
            {
                EditorUtility.DisplayDialog("RealTime Asset Importer", "Scan a dataset with a heightmap source first.", "OK");
                return;
            }

            if (_useRegionFilter)
                AlignRegionToHlodGrid();

            if (_packBuildingBundles &&
                (_packFacadeBuildings || _packOrthoRoofBuildings) &&
                _buildingSurfaceSettings == null)
            {
                EditorUtility.DisplayDialog(
                    "RealTime Asset Importer",
                    "Assign Building Surface Settings before baking building bundles. " +
                    "Materials are applied to combined meshes during pack bake.",
                    "OK");
                return;
            }

            if (_bakeVegetationBundles && _packVegetation && _vegetationRuleSet == null)
            {
                EditorUtility.DisplayDialog(
                    "RealTime Asset Importer",
                    "Assign a Vegetation Rule Set before baking vegetation bundles.",
                    "OK");
                return;
            }

            var hm = _scan.HeightmapSources[_selectedHeightmapIdx];
            var selectedBasemaps = new List<DatasetFolderScanner.BasemapSource>();
            for (int i = 0; i < _scan.BasemapSources.Count; i++)
            {
                if (_basemapSelected[i])
                    selectedBasemaps.Add(_scan.BasemapSources[i]);
            }

            var tileIds = new HashSet<string>();
            foreach (HeightmapTileJson tile in hm.Metadata.Tiles)
            {
                if (_useRegionFilter)
                {
                    if (tile.Right < _regionMinE || tile.Left > _regionMaxE ||
                        tile.Top < _regionMinN || tile.Bottom > _regionMaxN)
                        continue;
                }
                tileIds.Add($"{tile.Left}_{tile.Bottom}");
            }

            var options = new RealtimePackOptions
            {
                DatasetRoot = _rootFolder,
                OutputRoot = ZGConnectPathUtils.AssetPathToFullPath(RuntimeStreamingPaths.PackedDatasetAssetRoot),
                Heightmap = hm,
                Basemaps = selectedBasemaps,
                VegetationMasksFolder = _scan.VegetationMasksFolder,
                SelectedTileIds = tileIds,
                RegionBounds = _useRegionFilter
                    ? StreamingRegionBoundsEpsg.FromFields(
                        true, _regionMinE, _regionMaxE, _regionMinN, _regionMaxN)
                    : default,
                PackTerrain = _packTerrain,
                PackTerrainBundles = _packTerrainBundles,
                PackOrtho = _packOrtho,
                PackTiled = _packTiled,
                PackFacadeBuildings = _packFacadeBuildings,
                PackOrthoRoofBuildings = _packOrthoRoofBuildings,
                PackCopyBuildingMetadataBinary = _packCopyBuildingMetadataBinary,
                PackVegetation = _packVegetation,
                PackBuildingLod = _packBuildingLod,
                PackBuildingBundles = _packBuildingBundles,
                PackColliderMode = _packBuildingColliderMode == 0
                    ? RealtimeBuildingColliderMode.Box
                    : RealtimeBuildingColliderMode.ConvexMesh,
                PackUncompressedAssetBundles = _packUncompressedAssetBundles,
                BakeVegetationBundles = _bakeVegetationBundles,
                BuildingSurfaceSettings = _buildingSurfaceSettings,
                RoofOrthophotoMaterialTemplate = _roofOrthophotoMaterialTemplate,
                VegetationRuleSet = _vegetationRuleSet,
                VegetationPrototypes = ResolveVegetationPrototypesForPack(_vegetationRuleSet),
                SkipExisting = _skipExisting,
                HeightmapResolution = _heightmapResolution,
                OrthoMaxResolution = _orthoMaxRes,
                OrthoCrunchCompression = _orthoCrunch,
                OrthoCrunchQuality = _orthoCrunchQuality,
            };

            _packRunner?.Stop();
            _isPacking = true;
            _parametersExpanded = false;
            _progressExpanded = true;
            _packProgress.Reset(options);
            Repaint();

            _packRunner = EditorCoroutineRunner.Start(
                RealtimeDatasetPackager.PackCoroutine(
                    options,
                    OnPackProgress,
                    _ => { }),
                onComplete: () =>
                {
                    _isPacking = false;
                    RealtimePackStagingUtility.CleanupAll();
                    _packProgress.MarkComplete(
                        $"Packed {tileIds.Count} tile(s) to StreamingAssets/ZGConnect/");
                    Repaint();
                    RepaintOpenTileMap();
                },
                onError: ex =>
                {
                    _isPacking = false;
                    Debug.LogWarning(
                        "[ZGConnect.Realtime] Pack failed — _pack_staging prefabs are kept for inspection under " +
                        $"{RealtimePackStagingUtility.StagingAssetRoot}");
                    _packProgress.MarkFailed(ex.Message, _packProgress.Master);
                    Debug.LogException(ex);
                    Repaint();
                });
        }

        void OnPackProgress(RealtimePackProgress progress)
        {
            _packProgress.Apply(progress);
            Repaint();
        }

        void CreateStreamerInScene()
        {
            var go = new GameObject("ZG Connect Realtime Streamer");
            var streamer = go.AddComponent<RealtimeStreamingController>();
            var so = new SerializedObject(streamer);
            so.FindProperty("manifestRelativePath").stringValue = RuntimeStreamingPaths.DefaultManifestRelativePath;
            so.ApplyModifiedPropertiesWithoutUndo();
            Selection.activeGameObject = go;
        }
    }
}
