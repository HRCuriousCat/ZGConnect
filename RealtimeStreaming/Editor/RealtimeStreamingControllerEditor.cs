using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using ZGConnect.Editor;
using ZGConnect.RealtimeStreaming;

namespace ZGConnect.RealtimeStreaming.Editor
{
    [CustomEditor(typeof(RealtimeStreamingController))]
    public class RealtimeStreamingControllerEditor : UnityEditor.Editor
    {
        static readonly string[] kLoadRegionPropertyNames =
        {
            "limitLoadRegion",
            "loadRegionMinE",
            "loadRegionMaxE",
            "loadRegionMinN",
            "loadRegionMaxN",
        };

        Texture2D _logo;

        void OnEnable()
        {
            _logo = ZGConnectEditorBranding.LoadLogo();
            RegisterRegionBridge();
        }

        void OnDisable()
        {
            if (ZGConnectImportRegionBridge.RepaintImporter == (System.Action)Repaint)
                ZGConnectImportRegionBridge.Clear();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            ZGConnectEditorBranding.BeginInspectorPanel();
            ZGConnectEditorBranding.DrawInspectorHeader(
                _logo, "ZG Connect — Realtime Streamer", drawHeaderBackground: false);

            RegisterRegionBridge();
            DrawLoadRegionSection();
            EditorGUILayout.Space(4);

            DrawPropertiesExcludingBasemapIds();

            DrawVegetationHelpBox();

            SerializedProperty bundleOnlyProp = serializedObject.FindProperty("terrainBundleOnlyMode");
            if (bundleOnlyProp != null && bundleOnlyProp.boolValue)
            {
                EditorGUILayout.HelpBox(
                    "Terrain bundle-only: tiles load exclusively from terrain AssetBundles. " +
                    "RAW heightmap prepare and fallback are disabled. Every streamed tile needs a bundle for the active basemap.",
                    MessageType.Info);
            }

            serializedObject.ApplyModifiedProperties();

            var controller = (RealtimeStreamingController)target;
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Runtime", EditorStyles.miniBoldLabel);
            EditorGUILayout.LabelField("Loaded tiles", controller.LoadedTileCount.ToString());

            if (Application.isPlaying && controller.Manifest != null)
            {
                EditorGUILayout.LabelField("Tiles in manifest", controller.Manifest.Tiles?.Count.ToString() ?? "0");
                EditorGUILayout.HelpBox(
                    "During Play: change Active Basemap Id, Building Style, or HLOD tint. " +
                    "Building materials, mesh combine, and colliders come from pack-baked bundles.",
                    MessageType.Info);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Editor Populate", EditorStyles.miniBoldLabel);
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Populate Scene"))
                {
                    Vector3 cameraPosition = RealtimeStreamingEditorScenePopulator.ResolveCameraPosition(controller);
                    RealtimeStreamingEditorScenePopulator.Populate(controller, cameraPosition);
                }

                if (GUILayout.Button("Clear Populated"))
                    RealtimeStreamingEditorScenePopulator.Clear(controller);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.HelpBox(
                    "Instantly places terrain and building tiles in the scene (no Play mode). " +
                    "Terrain: all 1×1 leaves in the load region (ignores HLOD 2×2/4×4 and camera distance). " +
                    "Buildings: all styled leaves in the load region. Colliders spawn on TileBuildings_*_Physics children. " +
                    "Assign Building Surface Settings for CombinedRender materials.",
                    MessageType.Info);
            }

            if (Application.isPlaying)
            {
                EditorGUILayout.Space(4);
                if (GUILayout.Button("Restart Streaming"))
                    controller.StartStreaming();
            }

            ZGConnectEditorBranding.EndInspectorPanel();
        }

        void DrawLoadRegionSection()
        {
            EditorGUILayout.LabelField("Load Region", EditorStyles.boldLabel);

            SerializedProperty limitProp = serializedObject.FindProperty("limitLoadRegion");
            SerializedProperty minEProp = serializedObject.FindProperty("loadRegionMinE");
            SerializedProperty maxEProp = serializedObject.FindProperty("loadRegionMaxE");
            SerializedProperty minNProp = serializedObject.FindProperty("loadRegionMinN");
            SerializedProperty maxNProp = serializedObject.FindProperty("loadRegionMaxN");

            EditorGUILayout.PropertyField(limitProp, new GUIContent("Limit Load Region"));
            if (limitProp.boolValue)
            {
                EditorGUILayout.HelpBox(
                    "Only tiles inside this EPSG rectangle are eligible for streaming (terrain, buildings, vegetation). " +
                    "Radius and HLOD rules still apply first; tiles outside the region are culled last.",
                    MessageType.Info);

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PrefixLabel("EPSG bounds");
                EditorGUILayout.LabelField(
                    $"E {minEProp.intValue} – {maxEProp.intValue}, N {minNProp.intValue} – {maxNProp.intValue}",
                    EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.PropertyField(minEProp, new GUIContent("Min E"));
                EditorGUILayout.PropertyField(maxEProp, new GUIContent("Max E"));
                EditorGUILayout.PropertyField(minNProp, new GUIContent("Min N"));
                EditorGUILayout.PropertyField(maxNProp, new GUIContent("Max N"));

                SerializedProperty manifestPathProp = serializedObject.FindProperty("manifestRelativePath");
                string manifestRelativePath = manifestPathProp?.stringValue;
                if (!ManifestExistsOnDisk(manifestRelativePath))
                {
                    EditorGUILayout.HelpBox(
                        "Manifest not found — assign a valid manifest path before using the tile map.",
                        MessageType.Warning);
                }

                if (GUILayout.Button("Open Tile Map"))
                {
                    RegisterRegionBridge();
                    ZGConnectTileMapWindow.OpenFromImporter();
                    GUIUtility.ExitGUI();
                }
            }
        }

        void DrawPropertiesExcludingBasemapIds()
        {
            var controller = (RealtimeStreamingController)target;
            SerializedProperty manifestPathProp = serializedObject.FindProperty("manifestRelativePath");
            string manifestRelativePath = manifestPathProp?.stringValue;
            StreamingDatasetManifest playModeManifest =
                Application.isPlaying ? controller.Manifest : null;

            SerializedProperty prop = serializedObject.GetIterator();
            bool enterChildren = true;
            while (prop.NextVisible(enterChildren))
            {
                enterChildren = false;

                if (IsLoadRegionProperty(prop.name))
                    continue;

                if (prop.name == "activeBasemapId")
                {
                    StreamingManifestEditorUtility.DrawBasemapIdPopup(
                        prop,
                        manifestRelativePath,
                        playModeManifest,
                        orthoOnly: false);
                    continue;
                }

                EditorGUILayout.PropertyField(prop, true);
            }
        }

        void DrawVegetationHelpBox()
        {
            SerializedProperty streamVegetationProp = serializedObject.FindProperty("streamVegetation");
            if (streamVegetationProp == null || !streamVegetationProp.boolValue)
                return;

            bool noRuleSet = serializedObject.FindProperty("vegetationRuleSet")?.objectReferenceValue == null;
            if (noRuleSet)
            {
                EditorGUILayout.HelpBox(
                    "Stream Vegetation is on but Vegetation Rule Set is missing. Assign " +
                    "Assets/ZGConnect/Assets/Vegetation Rule Set.asset on the streamer.",
                    MessageType.Warning);
                return;
            }

            SerializedProperty supertileProp = serializedObject.FindProperty("streamVegetationOn2x2Supertiles");
            if (supertileProp != null && !supertileProp.boolValue)
            {
                EditorGUILayout.HelpBox(
                    "Vegetation spawns only on 1×1 terrain tiles. With 2×2 HLOD mid-range tiles " +
                    "you will see terrain without trees until the camera is within hlod1x1LoadDistance. " +
                    "Enable Stream Vegetation On 2x2 Supertiles for trees on mid-range HLOD.",
                    MessageType.Info);
            }
        }

        static bool IsLoadRegionProperty(string name)
        {
            foreach (string regionProp in kLoadRegionPropertyNames)
            {
                if (regionProp == name)
                    return true;
            }

            return false;
        }

        void RegisterRegionBridge()
        {
            var controller = (RealtimeStreamingController)target;
            SerializedProperty manifestPathProp = serializedObject.FindProperty("manifestRelativePath");
            string manifestRelativePath = manifestPathProp?.stringValue;

            ZGConnectImportRegionBridge.AlignSelectionToPackGrid = false;
            ZGConnectImportRegionBridge.ApplyTargetLabel = "Realtime Streamer";
            ZGConnectImportRegionBridge.GetTiles = () =>
                RealtimeStreamingManifestMapCache.GetTiles(manifestRelativePath, LoadManifestTilesForMap);
            ZGConnectImportRegionBridge.GetOverviewGeorefBounds = () =>
            {
                if (RealtimeStreamingMapGeorefUtility.TryGetHeightmapCoverageFromImporterPrefs(
                        out ZGConnectMapGeorefBounds bounds))
                {
                    return (bounds.MinE, bounds.MaxE, bounds.MinN, bounds.MaxN);
                }

                if (RealtimeStreamingMapGeorefUtility.TryGetTileUnion(
                        RealtimeStreamingManifestMapCache.GetTiles(manifestRelativePath, LoadManifestTilesForMap),
                        out bounds))
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
                        RealtimeStreamingManifestMapCache.GetTiles(manifestRelativePath, LoadManifestTilesForMap),
                        out bounds))
                {
                    return (bounds.MinE, bounds.MaxE, bounds.MinN, bounds.MaxN);
                }

                return (0, 0, 0, 0);
            };
            ZGConnectImportRegionBridge.GetRegionEpsg = () =>
            {
                serializedObject.Update();
                return (
                    serializedObject.FindProperty("loadRegionMinE").intValue,
                    serializedObject.FindProperty("loadRegionMaxE").intValue,
                    serializedObject.FindProperty("loadRegionMinN").intValue,
                    serializedObject.FindProperty("loadRegionMaxN").intValue);
            };
            ZGConnectImportRegionBridge.ApplyRegionEpsg = ApplyRegionFromMap;
            ZGConnectImportRegionBridge.RepaintImporter = Repaint;
            ZGConnectImportRegionBridge.GetPackedTileIds = () =>
                RealtimeStreamingManifestMapCache.GetTileIds(
                    manifestRelativePath, LoadManifestTilesForMap, LoadManifestTileIds);
        }

        void ApplyRegionFromMap(int minE, int maxE, int minN, int maxN)
        {
            Undo.RecordObject(target, "Set streaming load region");
            serializedObject.Update();
            serializedObject.FindProperty("limitLoadRegion").boolValue = true;
            serializedObject.FindProperty("loadRegionMinE").intValue = minE;
            serializedObject.FindProperty("loadRegionMaxE").intValue = maxE;
            serializedObject.FindProperty("loadRegionMinN").intValue = minN;
            serializedObject.FindProperty("loadRegionMaxN").intValue = maxN;
            serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(target);
            Repaint();
        }

        static List<HeightmapTileJson> LoadManifestTilesForMap(string manifestRelativePath)
        {
            if (!TryLoadManifest(manifestRelativePath, out StreamingDatasetManifest manifest))
                return new List<HeightmapTileJson>();

            int tileSize = manifest.TileSizeMeters > 0 ? manifest.TileSizeMeters : 1000;
            var tiles = new List<HeightmapTileJson>(manifest.Tiles?.Count ?? 0);
            if (manifest.Tiles == null)
                return tiles;

            foreach (StreamingTileEntry entry in manifest.Tiles)
            {
                tiles.Add(new HeightmapTileJson
                {
                    Left = entry.Left,
                    Bottom = entry.Bottom,
                    Right = entry.Right > entry.Left ? entry.Right : entry.Left + tileSize,
                    Top = entry.Top > entry.Bottom ? entry.Top : entry.Bottom + tileSize,
                });
            }

            return tiles;
        }

        static HashSet<string> LoadManifestTileIds(string manifestRelativePath)
        {
            var ids = new HashSet<string>();
            if (!TryLoadManifest(manifestRelativePath, out StreamingDatasetManifest manifest) ||
                manifest.Tiles == null)
            {
                return ids;
            }

            foreach (StreamingTileEntry entry in manifest.Tiles)
                ids.Add(entry.TileId);

            return ids;
        }

        static bool ManifestExistsOnDisk(string manifestRelativePath)
        {
            string rel = string.IsNullOrEmpty(manifestRelativePath)
                ? RuntimeStreamingPaths.DefaultManifestRelativePath
                : manifestRelativePath;
            return File.Exists(RuntimeStreamingPaths.ManifestPath(rel));
        }

        static bool TryLoadManifest(string manifestRelativePath, out StreamingDatasetManifest manifest)
        {
            manifest = null;
            string rel = string.IsNullOrEmpty(manifestRelativePath)
                ? RuntimeStreamingPaths.DefaultManifestRelativePath
                : manifestRelativePath;

            string path = RuntimeStreamingPaths.ManifestPath(rel);
            if (!File.Exists(path))
                return false;

            try
            {
                manifest = StreamingDatasetManifest.LoadFromFile(path);
                return manifest != null;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[ZGConnect.Realtime] Failed to read manifest for region map: {ex.Message}");
                return false;
            }
        }
    }
}
