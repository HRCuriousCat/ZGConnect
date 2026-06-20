using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using ZGConnect;
using ZGConnect.Editor;
using ZGConnect.RealtimeStreaming.Editor;
using ZGConnect.SpatialStreaming;

namespace ZGConnect.SpatialStreaming.Editor
{
    public sealed class SpatialStreamingWindow : EditorWindow
    {
        enum SpatialBakeTileMode
        {
            All,
            Region,
        }

        const string PrefsPrefix = "ZGConnect.SpatialStreaming.";
        const string BakeProfilePrefsKey = PrefsPrefix + "BakeProfileGuid";

        bool _enableGpuResidentDrawer = true;
        bool _enableGpuOcclusionCulling = true;
        float _smallMeshScreenPercentage;
        bool _applyToPcPipelineAsset = true;

        SpatialBakeProfile _bakeProfile;
        SpatialBakeTileMode _bakeTileMode = SpatialBakeTileMode.All;
        int _regionMinE;
        int _regionMaxE;
        int _regionMinN;
        int _regionMaxN;
        bool _bakeRunning;
        bool _rebuildManifestFromStaging = true;
        string _bakeStatus = "Idle";
        float _bakeProgress;
        SpatialBakePipeline.BakeReport _bakeReport;

        SpatialStreamingSourceScanResult _scan;
        Vector2 _scroll;
        IEnumerator _bakeRoutine;
        int _cachedTileSizeMeters = 1000;

        [MenuItem("ZG Connect/Spatial Streaming")]
        public static void Open()
        {
            var window = GetWindow<SpatialStreamingWindow>("Spatial Streaming");
            window.minSize = new Vector2(440f, 640f);
            window.Show();
        }

        void OnEnable()
        {
            _enableGpuResidentDrawer = EditorPrefs.GetBool(PrefsPrefix + "EnableGrd", true);
            _enableGpuOcclusionCulling = EditorPrefs.GetBool(PrefsPrefix + "EnableOcclusion", true);
            _smallMeshScreenPercentage = EditorPrefs.GetFloat(PrefsPrefix + "SmallMeshPct", 0f);
            _applyToPcPipelineAsset = EditorPrefs.GetBool(PrefsPrefix + "ApplyPcUrp", true);
            LoadBakeProfileFromPrefs();
            LoadRegionSettings();
            RefreshSourceScan();
            RegisterRegionBridge();
        }

        void OnDisable()
        {
            EditorPrefs.SetBool(PrefsPrefix + "EnableGrd", _enableGpuResidentDrawer);
            EditorPrefs.SetBool(PrefsPrefix + "EnableOcclusion", _enableGpuOcclusionCulling);
            EditorPrefs.SetFloat(PrefsPrefix + "SmallMeshPct", _smallMeshScreenPercentage);
            EditorPrefs.SetBool(PrefsPrefix + "ApplyPcUrp", _applyToPcPipelineAsset);
            SaveBakeProfileToPrefs();
            SaveRegionSettings();
            StopBakeRoutine();
            if (ZGConnectImportRegionBridge.RepaintImporter == (Action)Repaint)
                ZGConnectImportRegionBridge.Clear();
        }

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("Spatial Streaming", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Independent workflow from RealtimeStreaming. Sources: StreamingAssets/ZGConnect/*. " +
                "Bake output: StreamingAssets/ZGConnect/bundles_spatial/.",
                MessageType.Info);

            DrawGpuResidentDrawerSection();
            EditorGUILayout.Space(8f);
            DrawBakeSection();
            EditorGUILayout.Space(8f);
            DrawSourceDatasetSection();
            EditorGUILayout.Space(8f);
            DrawSceneSetupSection();

            EditorGUILayout.EndScrollView();
        }

        void DrawGpuResidentDrawerSection()
        {
            EditorGUILayout.LabelField("GPU Resident Drawer", EditorStyles.boldLabel);

            _enableGpuResidentDrawer = EditorGUILayout.ToggleLeft(
                "Instanced Drawing (GPU Resident Drawer)", _enableGpuResidentDrawer);
            using (new EditorGUI.DisabledScope(!_enableGpuResidentDrawer))
            {
                _enableGpuOcclusionCulling = EditorGUILayout.ToggleLeft(
                    "GPU occlusion culling in cameras", _enableGpuOcclusionCulling);
                _smallMeshScreenPercentage = EditorGUILayout.Slider(
                    "Small mesh screen % cull", _smallMeshScreenPercentage, 0f, 20f);
            }

            _applyToPcPipelineAsset = EditorGUILayout.ToggleLeft(
                $"Apply to {SpatialGpuResidentDrawerProjectUtility.PcPipelineAssetPath}", _applyToPcPipelineAsset);

            DrawCurrentPipelineStatus();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply Project Settings"))
                ApplyProjectSettings();
            if (GUILayout.Button("Reinitialize Drawer"))
            {
                IGPUResidentRenderPipeline.ReinitializeGPUResidentDrawer();
                SpatialGpuResidentDrawerStatus status = SpatialGpuResidentDrawerStatus.Query();
                Debug.Log($"[ZGConnect.Spatial] {status.Message}");
                Repaint();
            }
            EditorGUILayout.EndHorizontal();

            List<string> rendererIssues =
                SpatialGpuResidentDrawerProjectUtility.ValidateRendererCompatibility(
                    SpatialGpuResidentDrawerProjectUtility.GetActiveUrpAsset());
            if (rendererIssues.Count > 0 && GUILayout.Button("Upgrade renderers to Forward+/Deferred+"))
            {
                int upgraded = SpatialGpuResidentDrawerProjectUtility.UpgradeIncompatibleRenderersInProject(
                    out List<string> paths);
                IGPUResidentRenderPipeline.ReinitializeGPUResidentDrawer();
                Debug.Log(
                    upgraded > 0
                        ? $"[ZGConnect.Spatial] Upgraded {upgraded} renderer(s):\n- {string.Join("\n- ", paths)}"
                        : "[ZGConnect.Spatial] No incompatible renderers found.");
                Repaint();
            }
        }

        void DrawBakeSection()
        {
            EditorGUILayout.LabelField("Spatial bake → bundles_spatial/", EditorStyles.boldLabel);

            _bakeProfile = (SpatialBakeProfile)EditorGUILayout.ObjectField(
                "Bake profile", _bakeProfile, typeof(SpatialBakeProfile), false);

            if (_bakeProfile == null)
            {
                EditorGUILayout.HelpBox(
                    "Assign or create a SpatialBakeProfile (subcell size, split threshold).",
                    MessageType.Warning);
                if (GUILayout.Button("Create Default Bake Profile"))
                    CreateDefaultBakeProfile();
            }
            else
            {
                EditorGUILayout.LabelField("Source", SpatialStreamingPaths.BuildingMeshesSourceFolder);
                EditorGUILayout.LabelField("Subcell size (m)", _bakeProfile.subcellSizeMeters.ToString());
                EditorGUILayout.LabelField("Split threshold (buildings)", _bakeProfile.minBuildingsForSubcellSplit.ToString());
                _bakeProfile.buildingSurfaceSettings = (BuildingSurfaceSettings)EditorGUILayout.ObjectField(
                    "Building surface settings",
                    _bakeProfile.buildingSurfaceSettings,
                    typeof(BuildingSurfaceSettings),
                    false);
                if (_bakeProfile.buildingSurfaceSettings == null)
                {
                    EditorGUILayout.HelpBox(
                        "Assign BuildingSurfaceSettings for facade/roof remap by GLB material name (no UV remap).",
                        MessageType.Warning);
                }

                _bakeProfile.verboseBakeLogging = EditorGUILayout.Toggle(
                    "Verbose bake logging", _bakeProfile.verboseBakeLogging);

                EditorGUILayout.Space(4f);
                DrawBakeScopeSection();
            }

            if (_bakeRunning)
            {
                EditorGUI.ProgressBar(
                    EditorGUILayout.GetControlRect(false, 20f),
                    _bakeProgress,
                    _bakeStatus);
            }
            else
            {
                EditorGUILayout.LabelField("Status", _bakeStatus);
            }

            using (new EditorGUI.DisabledScope(_bakeRunning || _bakeProfile == null || !CanStartBake()))
            {
                if (GUILayout.Button(GetBakeButtonLabel()))
                    StartBake();
            }

            if (_bakeRunning && GUILayout.Button("Cancel Bake"))
                StopBakeRoutine();

            DrawStagingRecoverySection();
        }

        void DrawStagingRecoverySection()
        {
            int stagedPrefabs = _scan?.StagedPrefabCount ?? 0;
            if (stagedPrefabs <= 0)
                return;

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Staging recovery", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                $"{stagedPrefabs} prefab(s) are staged under _spatial_bake_staging but bundles_spatial " +
                $"has {_scan?.SpatialBundleFileCount ?? 0} file(s). Finish from staging without re-baking GLBs.",
                MessageType.Warning);

            _rebuildManifestFromStaging = EditorGUILayout.Toggle(
                "Rebuild spatial_manifest.json",
                _rebuildManifestFromStaging);

            using (new EditorGUI.DisabledScope(_bakeRunning || _bakeProfile == null))
            {
                if (GUILayout.Button("Build bundles from staging"))
                    StartBuildFromStaging();
            }
        }

        void DrawCurrentPipelineStatus()
        {
            SpatialGpuResidentDrawerStatus status = SpatialGpuResidentDrawerStatus.Query();
            MessageType messageType = status.IsReadyForStreaming ? MessageType.Info : MessageType.Warning;
            EditorGUILayout.HelpBox(status.Message, messageType);

            UniversalRenderPipelineAsset active = SpatialGpuResidentDrawerProjectUtility.GetActiveUrpAsset();
            if (active != null)
            {
                EditorGUILayout.LabelField("Active URP asset", active.name);
                EditorGUILayout.LabelField("Drawer mode", status.DrawerMode.ToString());
                EditorGUILayout.LabelField("Occlusion culling", status.OcclusionCullingInCameras ? "On" : "Off");
            }

            List<string> rendererIssues =
                SpatialGpuResidentDrawerProjectUtility.ValidateRendererCompatibility(active);
            foreach (string issue in rendererIssues)
                EditorGUILayout.HelpBox(issue, MessageType.Warning);

            if (GUILayout.Button("List all incompatible URP renderers in project"))
            {
                List<string> projectIssues =
                    SpatialGpuResidentDrawerProjectUtility.FindAllIncompatibleRendererAssetsInProject();
                if (projectIssues.Count == 0)
                {
                    Debug.Log("[ZGConnect.Spatial] All Universal Renderer Data assets use Forward+ or Deferred+.");
                }
                else
                {
                    Debug.LogWarning(
                        "[ZGConnect.Spatial] Incompatible URP renderers:\n- " +
                        string.Join("\n- ", projectIssues));
                }
            }
        }

        void DrawSourceDatasetSection()
        {
            EditorGUILayout.LabelField("Source dataset", EditorStyles.boldLabel);

            EditorGUILayout.LabelField("Dataset root", _scan?.DatasetRoot ?? SpatialStreamingPaths.DatasetRoot);
            EditorGUILayout.LabelField(
                "Parent manifest",
                _scan?.ParentManifestExists == true ? "manifest.json" : "missing");
            EditorGUILayout.LabelField(
                "Spatial manifest",
                _scan?.SpatialManifestExists == true ? "spatial_manifest.json" : "missing");

            if (_scan != null)
            {
                EditorGUILayout.LabelField("Facade GLBs", _scan.BuildingGlbCount.ToString());
                EditorGUILayout.LabelField("Ortho roof GLBs", _scan.BuildingOrthoGlbCount.ToString());
                EditorGUILayout.LabelField("bundles_spatial files", _scan.SpatialBundleFileCount.ToString());
                EditorGUILayout.LabelField("Staged prefabs", _scan.StagedPrefabCount.ToString());

                foreach (string warning in _scan.Warnings)
                    EditorGUILayout.HelpBox(warning, MessageType.Warning);
            }

            if (GUILayout.Button("Rescan dataset"))
                RefreshSourceScan();
        }

        void DrawSceneSetupSection()
        {
            EditorGUILayout.LabelField("Scene setup", EditorStyles.boldLabel);

            if (GUILayout.Button("Add Spatial Streaming Rig to Scene"))
            {
                var gpuSettings = AssetDatabase.LoadAssetAtPath<SpatialGpuResidentRenderingSettings>(
                    "Assets/ZGConnect/SpatialStreaming/Settings/SpatialGpuResidentRenderingSettings_Default.asset");
                BuildingSurfaceSettings buildingSettings = TryLoadDefaultBuildingSurfaceSettings();
                SpatialSceneSetupUtility.EnsureStreamingRigInOpenScenes(gpuSettings, buildingSettings);
            }

            if (GUILayout.Button("Create GPU Resident Settings Asset"))
                CreateDefaultSettingsAsset();
        }

        void DrawBakeScopeSection()
        {
            EditorGUILayout.LabelField("Bake scope", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            _bakeTileMode = (SpatialBakeTileMode)EditorGUILayout.EnumPopup("Tiles to bake", _bakeTileMode);
            if (EditorGUI.EndChangeCheck())
                SaveRegionSettings();

            if (_bakeTileMode == SpatialBakeTileMode.All)
            {
                EditorGUILayout.HelpBox(
                    "All tiles with source GLBs in manifest.json will be baked.",
                    MessageType.None);
                return;
            }

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
            {
                AlignRegionToHlodGrid();
                SaveRegionSettings();
            }

            EditorGUILayout.HelpBox(
                "Region snaps to the 4×4 tile grid so bake borders align with HLOD supertiles. " +
                "Blue tiles on the map are already in spatial_manifest.json. " +
                "Region bakes merge into the existing manifest (other tiles are kept).",
                MessageType.None);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Open area selection map"))
                ZGConnectTileMapWindow.OpenFromImporter();
            if (GUILayout.Button("Reset to full dataset"))
                ResetRegionToFullExtent();
            EditorGUILayout.EndHorizontal();

            if (_regionMaxE > _regionMinE && _regionMaxN > _regionMinN)
            {
                List<string> bakeIds = SpatialBakeRegionUtility.ResolveBakeTileIdsInRegion(
                    _regionMinE,
                    _regionMaxE,
                    _regionMinN,
                    _regionMaxN,
                    _bakeProfile?.GetSourceFolder(),
                    out int tilesInRegion,
                    out int tilesWithSourceGlb);

                EditorGUILayout.LabelField(
                    $"Region: {tilesWithSourceGlb} tile(s) with source GLBs " +
                    $"({tilesInRegion} manifest tile(s) intersect region)",
                    EditorStyles.miniLabel);

                if (tilesWithSourceGlb == 0)
                {
                    EditorGUILayout.HelpBox(
                        "No source building GLBs intersect the current region. " +
                        "Drag a rectangle on the map or adjust EPSG bounds.",
                        MessageType.Warning);
                }
                else if (bakeIds.Count <= 12)
                {
                    EditorGUILayout.LabelField(string.Join(", ", bakeIds), EditorStyles.wordWrappedMiniLabel);
                }
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Define a valid EPSG region or pick one on the area selection map.",
                    MessageType.Warning);
            }
        }

        string GetBakeButtonLabel()
        {
            if (_bakeTileMode == SpatialBakeTileMode.All)
                return "Bake All Tiles";

            if (_regionMaxE <= _regionMinE || _regionMaxN <= _regionMinN)
                return "Bake Selected Region";

            SpatialBakeRegionUtility.ResolveBakeTileIdsInRegion(
                _regionMinE,
                _regionMaxE,
                _regionMinN,
                _regionMaxN,
                _bakeProfile?.GetSourceFolder(),
                out _,
                out int tilesWithSourceGlb);

            return tilesWithSourceGlb > 0
                ? $"Bake Selected Region ({tilesWithSourceGlb} tiles)"
                : "Bake Selected Region";
        }

        bool CanStartBake()
        {
            if (_bakeTileMode == SpatialBakeTileMode.All)
                return true;

            if (_regionMaxE <= _regionMinE || _regionMaxN <= _regionMinN)
                return false;

            SpatialBakeRegionUtility.ResolveBakeTileIdsInRegion(
                _regionMinE,
                _regionMaxE,
                _regionMinN,
                _regionMaxN,
                _bakeProfile?.GetSourceFolder(),
                out _,
                out int tilesWithSourceGlb);

            return tilesWithSourceGlb > 0;
        }

        void RegisterRegionBridge()
        {
            ZGConnectImportRegionBridge.AlignSelectionToPackGrid = true;
            ZGConnectImportRegionBridge.ApplyTargetLabel = "Spatial Streaming";
            ZGConnectImportRegionBridge.GetTiles = () =>
            {
                if (SpatialBakeRegionUtility.TryLoadMapTiles(
                        out List<HeightmapTileJson> tiles,
                        out int tileSizeMeters,
                        out _))
                {
                    _cachedTileSizeMeters = tileSizeMeters;
                    return tiles;
                }

                return new List<HeightmapTileJson>();
            };
            ZGConnectImportRegionBridge.GetOverviewGeorefBounds = () =>
            {
                if (SpatialBakeRegionUtility.TryGetOverviewBounds(out ZGConnectMapGeorefBounds bounds))
                    return (bounds.MinE, bounds.MaxE, bounds.MinN, bounds.MaxN);

                ZGConnectMapExtent.GetCityBoundingBox(out int minE, out int maxE, out int minN, out int maxN);
                return (minE, maxE, minN, maxN);
            };
            ZGConnectImportRegionBridge.GetBackgroundMapGeorefBounds = () =>
            {
                if (RealtimeStreamingMapGeorefUtility.TryGetBackgroundMapGeoref(out ZGConnectMapGeorefBounds bounds))
                    return (bounds.MinE, bounds.MaxE, bounds.MinN, bounds.MaxN);

                ZGConnectMapExtent.GetCityBoundingBox(out int minE, out int maxE, out int minN, out int maxN);
                return (minE, maxE, minN, maxN);
            };
            ZGConnectImportRegionBridge.GetRegionEpsg = () =>
                (_regionMinE, _regionMaxE, _regionMinN, _regionMaxN);
            ZGConnectImportRegionBridge.ApplyRegionEpsg = (minE, maxE, minN, maxN) =>
            {
                _regionMinE = minE;
                _regionMaxE = maxE;
                _regionMinN = minN;
                _regionMaxN = maxN;
                _bakeTileMode = SpatialBakeTileMode.Region;
                AlignRegionToHlodGrid();
                SaveRegionSettings();
                Repaint();
            };
            ZGConnectImportRegionBridge.RepaintImporter = Repaint;
            ZGConnectImportRegionBridge.GetPackedTileIds = SpatialBakeRegionUtility.GetSpatialBakedTileIds;
        }

        void AlignRegionToHlodGrid()
        {
            if (_regionMaxE <= _regionMinE || _regionMaxN <= _regionMinN)
                return;

            SpatialBakeRegionUtility.AlignRegionToHlodGrid(
                ref _regionMinE,
                ref _regionMaxE,
                ref _regionMinN,
                ref _regionMaxN,
                _cachedTileSizeMeters);
        }

        void ResetRegionToFullExtent()
        {
            if (SpatialBakeRegionUtility.TryGetOverviewBounds(out ZGConnectMapGeorefBounds bounds))
            {
                _regionMinE = bounds.MinE;
                _regionMaxE = bounds.MaxE;
                _regionMinN = bounds.MinN;
                _regionMaxN = bounds.MaxN;
                SaveRegionSettings();
                RepaintOpenTileMaps();
            }
        }

        static void RepaintOpenTileMaps()
        {
            ZGConnectTileMapWindow[] maps = Resources.FindObjectsOfTypeAll<ZGConnectTileMapWindow>();
            foreach (ZGConnectTileMapWindow map in maps)
                map.Repaint();
        }

        void LoadRegionSettings()
        {
            _bakeTileMode = (SpatialBakeTileMode)EditorPrefs.GetInt(
                PrefsPrefix + "BakeTileMode",
                (int)SpatialBakeTileMode.All);
            _regionMinE = EditorPrefs.GetInt(PrefsPrefix + "RegionMinE", 0);
            _regionMaxE = EditorPrefs.GetInt(PrefsPrefix + "RegionMaxE", 0);
            _regionMinN = EditorPrefs.GetInt(PrefsPrefix + "RegionMinN", 0);
            _regionMaxN = EditorPrefs.GetInt(PrefsPrefix + "RegionMaxN", 0);

            if (SpatialBakeRegionUtility.TryLoadMapTiles(out _, out int tileSizeMeters, out _))
                _cachedTileSizeMeters = tileSizeMeters;

            if (_regionMaxE <= _regionMinE || _regionMaxN <= _regionMinN)
            {
                if (SpatialBakeRegionUtility.TryGetOverviewBounds(out ZGConnectMapGeorefBounds bounds))
                {
                    _regionMinE = bounds.MinE;
                    _regionMaxE = bounds.MaxE;
                    _regionMinN = bounds.MinN;
                    _regionMaxN = bounds.MaxN;
                }
            }
        }

        void SaveRegionSettings()
        {
            EditorPrefs.SetInt(PrefsPrefix + "BakeTileMode", (int)_bakeTileMode);
            EditorPrefs.SetInt(PrefsPrefix + "RegionMinE", _regionMinE);
            EditorPrefs.SetInt(PrefsPrefix + "RegionMaxE", _regionMaxE);
            EditorPrefs.SetInt(PrefsPrefix + "RegionMinN", _regionMinN);
            EditorPrefs.SetInt(PrefsPrefix + "RegionMaxN", _regionMaxN);
        }

        void StartBake()
        {
            StopBakeRoutine();

            IReadOnlyList<string> runtimeTileFilter = null;
            if (_bakeTileMode == SpatialBakeTileMode.Region)
            {
                runtimeTileFilter = SpatialBakeRegionUtility.ResolveBakeTileIdsInRegion(
                    _regionMinE,
                    _regionMaxE,
                    _regionMinN,
                    _regionMaxN,
                    _bakeProfile.GetSourceFolder(),
                    out _,
                    out int tilesWithSourceGlb);

                if (tilesWithSourceGlb == 0)
                {
                    EditorUtility.DisplayDialog(
                        "Spatial Streaming",
                        "No source building GLBs intersect the selected region.",
                        "OK");
                    return;
                }
            }

            _bakeRunning = true;
            _bakeProgress = 0f;
            _bakeStatus = "Starting bake...";

            _bakeReport = new SpatialBakePipeline.BakeReport();
            _bakeRoutine = SpatialBakePipeline.BakeCoroutine(
                _bakeProfile,
                (progress, detail) =>
                {
                    _bakeProgress = progress;
                    _bakeStatus = detail ?? "Baking...";
                    Repaint();
                },
                _bakeReport,
                runtimeTileFilter);

            EditorApplication.update += BakeEditorUpdate;
        }

        void StartBuildFromStaging()
        {
            if (_bakeProfile == null)
            {
                EditorUtility.DisplayDialog("Spatial Streaming", "Assign a bake profile first.", "OK");
                return;
            }

            _bakeRunning = true;
            _bakeProgress = 0f;
            _bakeStatus = "Building bundles from staging...";

            _bakeReport = new SpatialBakePipeline.BakeReport();
            _bakeRoutine = SpatialBakePipeline.BuildBundlesFromStagingCoroutine(
                _bakeProfile,
                _rebuildManifestFromStaging,
                (progress, detail) =>
                {
                    _bakeProgress = progress;
                    _bakeStatus = detail ?? "Building bundles...";
                    Repaint();
                },
                _bakeReport);

            EditorApplication.update += BakeEditorUpdate;
        }

        void BakeEditorUpdate()
        {
            if (_bakeRoutine == null)
            {
                StopBakeRoutine();
                return;
            }

            try
            {
                if (_bakeRoutine.MoveNext())
                {
                    Repaint();
                    return;
                }
            }
            catch (Exception ex)
            {
                _bakeStatus = "Bake failed: " + ex.Message;
                Debug.LogException(ex);
                StopBakeRoutine();
                return;
            }

            _bakeStatus = !string.IsNullOrEmpty(_bakeReport?.Message)
                ? _bakeReport.Message
                : "Bake finished.";
            _bakeProgress = 1f;
            if (!string.IsNullOrEmpty(_bakeReport?.Message))
                Debug.Log($"[ZGConnect.Spatial] {_bakeReport.Message}");
            StopBakeRoutine();
            RefreshSourceScan();
            Repaint();
        }

        void StopBakeRoutine()
        {
            _bakeRunning = false;
            _bakeRoutine = null;
            EditorApplication.update -= BakeEditorUpdate;
        }

        void ApplyProjectSettings()
        {
            SpatialGpuResidentDrawerProjectUtility.EnsureBrgShaderStrippingKeepAll(out string brgMessage);
            if (!string.IsNullOrEmpty(brgMessage))
                Debug.Log($"[ZGConnect.Spatial] {brgMessage}");

            UniversalRenderPipelineAsset target = _applyToPcPipelineAsset
                ? SpatialGpuResidentDrawerProjectUtility.LoadPcPipelineAsset()
                : SpatialGpuResidentDrawerProjectUtility.GetActiveUrpAsset();

            SpatialGpuResidentDrawerProjectUtility.ApplyResult result =
                SpatialGpuResidentDrawerProjectUtility.EnsureDrawerActive(
                    target,
                    _enableGpuResidentDrawer,
                    _enableGpuOcclusionCulling,
                    _smallMeshScreenPercentage);

            if (result.Success)
                Debug.Log($"[ZGConnect.Spatial] {result.Message} Asset: {target?.name}");
            else
                Debug.LogWarning($"[ZGConnect.Spatial] {result.Message}");

            Repaint();
        }

        void RefreshSourceScan()
        {
            SpatialBakeRegionUtility.InvalidateMapCaches();
            _scan = SpatialStreamingSourceScanner.Scan();
            if (SpatialBakeRegionUtility.TryLoadMapTiles(out _, out int tileSizeMeters, out _))
                _cachedTileSizeMeters = tileSizeMeters;
            RegisterRegionBridge();
            Repaint();
        }

        void LoadBakeProfileFromPrefs()
        {
            string guid = EditorPrefs.GetString(BakeProfilePrefsKey, string.Empty);
            if (string.IsNullOrEmpty(guid))
                return;

            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!string.IsNullOrEmpty(path))
                _bakeProfile = AssetDatabase.LoadAssetAtPath<SpatialBakeProfile>(path);
        }

        void SaveBakeProfileToPrefs()
        {
            if (_bakeProfile == null)
            {
                EditorPrefs.DeleteKey(BakeProfilePrefsKey);
                return;
            }

            string path = AssetDatabase.GetAssetPath(_bakeProfile);
            string guid = AssetDatabase.AssetPathToGUID(path);
            EditorPrefs.SetString(BakeProfilePrefsKey, guid);
        }

        static void CreateDefaultSettingsAsset()
        {
            const string folder = "Assets/ZGConnect/SpatialStreaming/Settings";
            const string path = folder + "/SpatialGpuResidentRenderingSettings_Default.asset";
            if (AssetDatabase.LoadAssetAtPath<SpatialGpuResidentRenderingSettings>(path) != null)
            {
                Selection.activeObject = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                return;
            }

            EnsureSettingsFolder();
            var asset = CreateInstance<SpatialGpuResidentRenderingSettings>();
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            Selection.activeObject = asset;
        }

        static void CreateDefaultBakeProfile()
        {
            const string path = "Assets/ZGConnect/SpatialStreaming/Settings/SpatialBakeProfile_Default.asset";
            if (AssetDatabase.LoadAssetAtPath<SpatialBakeProfile>(path) != null)
            {
                Selection.activeObject = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                return;
            }

            EnsureSettingsFolder();
            var asset = CreateInstance<SpatialBakeProfile>();
            asset.buildingSurfaceSettings = TryLoadDefaultBuildingSurfaceSettings();
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            Selection.activeObject = asset;
        }

        static BuildingSurfaceSettings TryLoadDefaultBuildingSurfaceSettings()
        {
            string[] guids = AssetDatabase.FindAssets("t:BuildingSurfaceSettings");
            if (guids.Length == 0)
                return null;

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.Contains("BuildingSurfaceSettings Flat"))
                    continue;

                BuildingSurfaceSettings settings =
                    AssetDatabase.LoadAssetAtPath<BuildingSurfaceSettings>(path);
                if (settings != null)
                    return settings;
            }

            return AssetDatabase.LoadAssetAtPath<BuildingSurfaceSettings>(
                AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        static void EnsureSettingsFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/ZGConnect/SpatialStreaming/Settings"))
            {
                if (!AssetDatabase.IsValidFolder("Assets/ZGConnect/SpatialStreaming"))
                    AssetDatabase.CreateFolder("Assets/ZGConnect", "SpatialStreaming");
                AssetDatabase.CreateFolder("Assets/ZGConnect/SpatialStreaming", "Settings");
            }
        }
    }
}
