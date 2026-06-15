using UnityEditor;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace ZGConnect.Editor
{
    [CustomEditor(typeof(TerrainStreamingController))]
    public class ZGConnectStreamingControllerEditor : UnityEditor.Editor
    {
        // ── Foldout state (persisted per-session) ──────────────────────────────
        private static bool _showCameraSection = true;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var controller = (TerrainStreamingController)target;
            var so         = new SerializedObject(controller);

            // ── Auto-reference warnings ────────────────────────────────────────
            bool needsSetup = false;

            var camProp          = so.FindProperty("cameraTransform");
            var terrainRootProp  = so.FindProperty("terrainRoot");
            var buildingsRootProp = so.FindProperty("buildingsRoot");

            if (camProp?.objectReferenceValue == null ||
                terrainRootProp?.objectReferenceValue == null ||
                buildingsRootProp?.objectReferenceValue == null)
            {
                needsSetup = true;
                EditorGUILayout.Space(6);
                EditorGUILayout.HelpBox(
                    "Some scene references are not assigned. " +
                    "Click 'Auto Setup' to assign Camera.main and create root GameObjects.",
                    MessageType.Warning);

                if (GUILayout.Button("⚙  Auto Setup Scene References", GUILayout.Height(28)))
                    AutoSetup(controller, so);
            }

            if (!needsSetup)
                EditorGUILayout.Space(6);

            // ── Scene Setup (edit mode only) ───────────────────────────────────
            EditorGUILayout.LabelField("Scene Setup", EditorStyles.boldLabel);

            var datasetProp = so.FindProperty("dataset");
            var dataset     = datasetProp?.objectReferenceValue as CityDataset;

            if (dataset == null)
            {
                EditorGUILayout.HelpBox(
                    "Assign a CityDataset to enable terrain streaming.",
                    MessageType.Warning);
            }
            else
            {
                int total        = dataset.tiles?.Count ?? 0;
                int withSceneObj = 0;
                int withData     = 0;
                int withPrefab   = 0;
                int withSceneBld = 0;
                int withVegMask  = 0;

                if (dataset.tiles != null)
                {
                    foreach (var t in dataset.tiles)
                    {
                        if (t.terrainData != null
                            || (t.terrainDataRef != null && t.terrainDataRef.RuntimeKeyIsValid()))
                            withData++;
                        if (IsUnityObjectAlive(t.sceneObject))          withSceneObj++;
                        if (t.buildingsLod0Prefab != null)              withPrefab++;
                        if (IsUnityObjectAlive(t.buildingsSceneObject)) withSceneBld++;
                        if (t.vegetationMask       != null) withVegMask++;
                    }
                }

                // ── Terrain mode ───────────────────────────────────────────────────
                if (withSceneObj == total && total > 0)
                    EditorGUILayout.HelpBox(
                        $"SetActive mode — all {total} tiles have scene objects. " +
                        "Streaming will toggle visibility on existing GameObjects.",
                        MessageType.None);
                else if (withSceneObj == 0)
                    EditorGUILayout.HelpBox(
                        $"Runtime mode — {withData} / {total} tiles have TerrainData. " +
                        "Streaming will instantiate terrain GameObjects on demand.",
                        MessageType.None);
                else
                    EditorGUILayout.HelpBox(
                        $"Mixed mode — {withSceneObj} tiles use SetActive, " +
                        $"{total - withSceneObj} tiles use runtime instantiation.",
                        MessageType.None);

                // ── Building materials ─────────────────────────────────────────────
                var applyMatsProp = so.FindProperty("applySharedBuildingMaterials");
                var bldSettingsProp = so.FindProperty("buildingSurfaceSettings");
                if (applyMatsProp?.boolValue == true && bldSettingsProp?.objectReferenceValue == null)
                    EditorGUILayout.HelpBox(
                        "Apply Shared Building Materials is ON — assign Building Surface Settings " +
                        "(same asset as import).",
                        MessageType.Warning);

                // ── Buildings validation ───────────────────────────────────────────
                var staggerBldProp = so.FindProperty("staggerBuildingActivation");
                if (staggerBldProp?.boolValue == true)
                {
                    float sweep = so.FindProperty("buildingSweepSpeed")?.floatValue ?? 100f;
                    float reveal = so.FindProperty("buildingRevealDuration")?.floatValue ?? 0.35f;
                    EditorGUILayout.HelpBox(
                        $"Staggered building reveal: an X sweep from each tile's west edge to east edge " +
                        $"at {sweep:0.#} m/s; each building lerps scale Z 0→100% over {reveal:0.##} s.",
                        MessageType.Info);
                }

                var streamBldProp = so.FindProperty("streamBuildings");
                if (streamBldProp?.boolValue == true)
                {
                    int withAnyBuildings = Mathf.Max(withPrefab, withSceneBld);
                    if (withAnyBuildings == 0)
                        EditorGUILayout.HelpBox(
                            "Stream Buildings is ON — no tiles have building prefabs assigned.\n" +
                            "Run: Dataset Import Manager → Buildings tab.",
                            MessageType.Warning);
                    else if (total - withAnyBuildings > 0)
                        EditorGUILayout.HelpBox(
                            $"Buildings: {withAnyBuildings} / {total} tiles assigned" +
                            $" — {total - withAnyBuildings} tile(s) have no prefab.",
                            MessageType.Info);
                }

                // ── Vegetation validation ──────────────────────────────────────────
                var streamVegProp = so.FindProperty("streamVegetation");
                if (streamVegProp?.boolValue == true)
                {
                    bool noRuleSet = so.FindProperty("vegetationRuleSet")?.objectReferenceValue == null;
                    var resolved   = controller.ResolveVegetationPrototypes();
                    bool noProtos  = resolved == null || resolved.Length == 0;
                    bool manualList = (so.FindProperty("vegetationPrototypes")?.arraySize ?? 0) > 0;

                    if (noRuleSet)
                        EditorGUILayout.HelpBox(
                            "Stream Vegetation is ON — VegetationRuleSet is not assigned.",
                            MessageType.Warning);

                    if (!noRuleSet && noProtos)
                        EditorGUILayout.HelpBox(
                            "Stream Vegetation is ON — no prototypes found. Add prototypes to " +
                            "species sets in the rule set, or assign Vegetation Prototypes manually.",
                            MessageType.Warning);

                    if (!noRuleSet && !noProtos && manualList)
                        EditorGUILayout.HelpBox(
                            "Vegetation Prototypes list is an optional allow-list. Leave empty to use " +
                            "all prototypes from the rule set automatically.",
                            MessageType.Info);

                    var spawnMode = (VegetationSpawnAnimationMode)(so.FindProperty("vegetationSpawnAnimation")?.enumValueIndex ?? 0);
                    if (spawnMode == VegetationSpawnAnimationMode.ChunkPopIn)
                    {
                        float dur = so.FindProperty("vegetationChunkPopInDuration")?.floatValue ?? 0.35f;
                        EditorGUILayout.HelpBox(
                            $"Chunk Pop In — whole tile scales in over {dur:0.##} s (cheap, one factor per tile).",
                            MessageType.Info);
                    }
                    else if (spawnMode == VegetationSpawnAnimationMode.PerTree)
                    {
                        float grow = so.FindProperty("vegetationPerTreeGrowDuration")?.floatValue ?? 1.2f;
                        float stag = so.FindProperty("vegetationPerTreeMaxStagger")?.floatValue ?? 0.8f;
                        EditorGUILayout.HelpBox(
                            $"Per Tree — each instance grows over {grow:0.##} s with up to {stag:0.##} s random stagger " +
                            "(rebuilds instance matrices every frame while animating).",
                            MessageType.Info);
                    }

                    float vegMaxDist = so.FindProperty("vegetationMaxDrawDistance")?.floatValue ?? 0f;
                    if (vegMaxDist > 0f)
                    {
                        float vegFalloff = so.FindProperty("vegetationDensityFalloffStart")?.floatValue ?? 0f;
                        EditorGUILayout.HelpBox(
                            $"Draw distance â€” full density to {vegFalloff:0.#} m, linear falloff to 0 at {vegMaxDist:0.#} m " +
                            "(horizontal distance from camera to tile bounds).",
                            MessageType.Info);
                    }

                    if (!noRuleSet && !noProtos && withVegMask == 0)
                        EditorGUILayout.HelpBox(
                            "Stream Vegetation is ON — no tiles have a VegetationMask texture assigned.",
                            MessageType.Warning);
                }

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField(
                    "Optional: pre-place all tiles in the scene for SetActive mode.",
                    EditorStyles.miniLabel);
            }

            using (new EditorGUI.DisabledScope(dataset == null || Application.isPlaying))
            {
                if (GUILayout.Button("Instantiate All Terrains (SetActive mode)", GUILayout.Height(28)))
                {
                    ZGConnectImporter.InstantiateAllTerrains(dataset);
                    GUIUtility.ExitGUI();
                }
            }

            // ── Instantiate All Buildings ──────────────────────────────────────
            {
                int bldWithPrefab = 0;
                int bldInScene    = 0;
                if (dataset?.tiles != null)
                    foreach (var t in dataset.tiles)
                    {
                        if (t.buildingsLod0Prefab != null) bldWithPrefab++;
                        if (IsUnityObjectAlive(t.buildingsSceneObject)) bldInScene++;
                    }

                int bldRemaining = bldWithPrefab - bldInScene;
                bool canPlace    = dataset != null && !Application.isPlaying
                                   && bldWithPrefab > 0 && bldRemaining > 0;

                string bldLabel = bldWithPrefab == 0
                    ? "Instantiate All Buildings  (no prefabs imported)"
                    : bldRemaining == 0
                        ? $"Instantiate All Buildings  ✓ all {bldWithPrefab} already in scene"
                        : bldInScene == 0
                            ? $"Instantiate All Buildings  ({bldWithPrefab} tiles)"
                            : $"Instantiate All Buildings  ({bldRemaining} remaining / {bldWithPrefab} total)";

                using (new EditorGUI.DisabledScope(!canPlace))
                {
                    if (GUILayout.Button(bldLabel, GUILayout.Height(28)))
                    {
                        ZGConnectImporter.InstantiateAllBuildings(dataset);
                        GUIUtility.ExitGUI();
                    }
                }
            }

            // ── Bake All Vegetation ────────────────────────────────────────────
            {
                int vegWithMask = 0;
                int vegBaked    = 0;
                int vegTerrain  = 0;
                if (dataset?.tiles != null)
                {
                    foreach (var t in dataset.tiles)
                    {
                        if (t.vegetationMask == null) continue;
                        vegWithMask++;

                        GameObject sceneGo = t.sceneObject;
                        if (!IsUnityObjectAlive(sceneGo)) continue;

                        vegTerrain++;
                        VegetationChunkRenderer r = sceneGo.GetComponent<VegetationChunkRenderer>();
                        if (r != null && r.TotalInstances > 0) vegBaked++;
                    }
                }

                bool hasRuleSet = so.FindProperty("vegetationRuleSet")?.objectReferenceValue != null;
                var protos      = controller.ResolveVegetationPrototypes();
                bool hasProtos  = protos != null && protos.Length > 0;
                int vegRemaining = vegTerrain - vegBaked;

                bool canBakeVeg = dataset != null && !Application.isPlaying
                                  && hasRuleSet && hasProtos && vegTerrain > 0;

                string vegLabel = !hasRuleSet
                    ? "Bake All Vegetation  (assign VegetationRuleSet)"
                    : !hasProtos
                        ? "Bake All Vegetation  (no prototypes in rule set)"
                        : vegWithMask == 0
                            ? "Bake All Vegetation  (no masks on dataset)"
                            : vegTerrain == 0
                                ? "Bake All Vegetation  (instantiate terrains first)"
                                : vegRemaining == 0
                                    ? $"Bake All Vegetation  ✓ {vegBaked} tiles baked"
                                    : vegBaked == 0
                                        ? $"Bake All Vegetation  ({vegTerrain} tiles with mask)"
                                        : $"Bake All Vegetation  ({vegRemaining} remaining / {vegTerrain})";

                using (new EditorGUI.DisabledScope(!canBakeVeg))
                {
                    if (GUILayout.Button(vegLabel, GUILayout.Height(28)))
                    {
                        int n = controller.EditorBakeAllVegetation(forceRegenerate: false);
                        Debug.Log($"[ZGConnect] Baked vegetation on {n} tile(s).");
                        GUIUtility.ExitGUI();
                    }
                }

                if (canBakeVeg && vegBaked > 0)
                {
                    if (GUILayout.Button("Regenerate All Vegetation (force)", GUILayout.Height(22)))
                    {
                        int n = controller.EditorBakeAllVegetation(forceRegenerate: true);
                        Debug.Log($"[ZGConnect] Regenerated vegetation on {n} tile(s).");
                        GUIUtility.ExitGUI();
                    }
                }
            }

            // ── Camera Positioning ─────────────────────────────────────────────
            EditorGUILayout.Space(10);
            _showCameraSection = EditorGUILayout.Foldout(_showCameraSection,
                "Camera Positioning", true, EditorStyles.foldoutHeader);

            if (_showCameraSection)
            {
                if (dataset == null || dataset.tiles == null || dataset.tiles.Count == 0)
                {
                    EditorGUILayout.HelpBox(
                        "Assign a CityDataset to enable camera teleport.",
                        MessageType.None);
                }
                else
                {
                    // Compute and display dataset bounds for reference
                    Vector3 centre = controller.GetDatasetCenter();
                    if (centre != Vector3.zero)
                    {
                        EditorGUILayout.LabelField(
                            $"Dataset centre  ≈  ({centre.x:F0}, {centre.y:F0}, {centre.z:F0})",
                            EditorStyles.miniLabel);
                    }

                    EditorGUILayout.Space(2);

                    if (Application.isPlaying)
                    {
                        if (GUILayout.Button("📍  Teleport Camera to Dataset Centre",
                                             GUILayout.Height(28)))
                        {
                            controller.TeleportCameraToDatasetCenter();
                        }
                    }
                    else
                    {
                        if (GUILayout.Button("📍  Teleport Camera to Dataset Centre (Edit Mode)",
                                             GUILayout.Height(28)))
                        {
                            TeleportCameraEditMode(controller);
                            GUIUtility.ExitGUI();
                        }

                        EditorGUILayout.HelpBox(
                            "Tip: enable 'Teleport Camera On Start' in the component to " +
                            "jump there automatically every time you enter Play mode.",
                            MessageType.None);
                    }
                }
            }

            // ── Streaming Control (play mode only) ─────────────────────────────
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Streaming Control", EditorStyles.boldLabel);

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Streaming starts automatically when you enter Play mode.",
                    MessageType.Info);
                return;
            }

            string statusLabel = controller.IsStreaming ? "● Active" : "○ Stopped";
            Color  statusColor = controller.IsStreaming
                ? new Color(0.2f, 0.85f, 0.2f)
                : new Color(0.7f, 0.7f, 0.7f);

            GUIStyle statusStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                normal = { textColor = statusColor }
            };
            EditorGUILayout.LabelField("Status", statusLabel, statusStyle);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Tiles loaded",
                controller.LoadedCount.ToString(), EditorStyles.miniLabel);

            if (controller.VisibleBuildingCount > 0)
                EditorGUILayout.LabelField("Buildings visible",
                    controller.VisibleBuildingCount.ToString("N0"), EditorStyles.miniLabel);

            EditorGUILayout.Space(4);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(controller.IsStreaming))
                {
                    if (GUILayout.Button("▶  Start", GUILayout.Height(30)))
                        controller.StartStreaming();
                }

                using (new EditorGUI.DisabledScope(!controller.IsStreaming))
                {
                    if (GUILayout.Button("■  Stop", GUILayout.Height(30)))
                        controller.StopStreaming();
                }
            }

            if (controller.IsStreaming)
                Repaint();
        }

        // ── Edit-mode camera teleport ──────────────────────────────────────────

        private static void TeleportCameraEditMode(TerrainStreamingController controller)
        {
            Vector3 centre = controller.GetDatasetCenter();
            if (centre == Vector3.zero)
            {
                Debug.LogWarning("[ZGConnect] Could not compute dataset centre — " +
                                 "no tiles with TerrainData found.");
                return;
            }

            Camera cam = Camera.main;
            if (cam == null)
            {
                // Fallback: try the scene-view camera in edit mode
                var sceneView = UnityEditor.SceneView.lastActiveSceneView;
                if (sceneView != null)
                {
                    Undo.RecordObject(sceneView, "Teleport SceneView to Dataset");
                    sceneView.pivot = centre;
                    sceneView.Repaint();
                    Debug.Log($"[ZGConnect] Scene view pivoted to dataset centre {centre}.");
                }
                else
                {
                    Debug.LogWarning("[ZGConnect] No Camera.main found to teleport.");
                }
                return;
            }

            Undo.RecordObject(cam.transform, "Teleport Camera to Dataset");
            cam.transform.position = centre;
            cam.transform.rotation = Quaternion.Euler(45f, 0f, 0f);

            // Also snap the scene view so you can see it immediately
            var sv = UnityEditor.SceneView.lastActiveSceneView;
            if (sv != null)
            {
                sv.pivot  = centre;
                sv.Repaint();
            }

            Debug.Log($"[ZGConnect] Camera teleported to ({centre.x:F0}, {centre.y:F0}, {centre.z:F0}).");
        }

        // ── Auto Setup ─────────────────────────────────────────────────────────

        private void AutoSetup(TerrainStreamingController controller, SerializedObject so)
        {
            so.Update();
            bool dirty = false;

            // Camera
            var camProp = so.FindProperty("cameraTransform");
            if (camProp.objectReferenceValue == null && Camera.main != null)
            {
                camProp.objectReferenceValue = Camera.main.transform;
                dirty = true;
            }

            // Terrain Root — create as child if missing
            var terrainRootProp = so.FindProperty("terrainRoot");
            if (terrainRootProp.objectReferenceValue == null)
            {
                Transform existing = controller.transform.Find("Terrain Root");
                if (existing == null)
                {
                    var go = new GameObject("Terrain Root");
                    go.transform.SetParent(controller.transform, false);
                    Undo.RegisterCreatedObjectUndo(go, "Create Terrain Root");
                    terrainRootProp.objectReferenceValue = go.transform;
                }
                else
                {
                    terrainRootProp.objectReferenceValue = existing;
                }
                dirty = true;
            }

            // Buildings Root — create as child if missing
            var buildingsRootProp = so.FindProperty("buildingsRoot");
            if (buildingsRootProp.objectReferenceValue == null)
            {
                Transform existing = controller.transform.Find("Buildings Root");
                if (existing == null)
                {
                    var go = new GameObject("Buildings Root");
                    go.transform.SetParent(controller.transform, false);
                    Undo.RegisterCreatedObjectUndo(go, "Create Buildings Root");
                    buildingsRootProp.objectReferenceValue = go.transform;
                }
                else
                {
                    buildingsRootProp.objectReferenceValue = existing;
                }
                dirty = true;
            }

            if (dirty)
            {
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(controller);
            }
        }

        /// <summary>
        /// Unity fake-null check — required instead of C# ?. on destroyed scene references.
        /// </summary>
        private static bool IsUnityObjectAlive(Object obj) => obj != null;
    }
}
