using System;
using System.Collections;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.IO;
using System.Threading.Tasks;
using GLTFast;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using ZGConnect;
using ZGConnect.Editor;
using ZGConnect.RealtimeStreaming;

namespace ZGConnect.RealtimeStreaming.Editor
{
    public sealed class BuildingBakeResult
    {
        public bool Success;
        public string PrefabAssetPath;
        public string PhysicsPrefabAssetPath;
    }

    public static class RealtimePackBuildingBakeUtility
    {
        const int MaterialApplyYieldInterval = 8;

        public static IEnumerator BakeBuildingTilePrefabCoroutine(
            RealtimePackOptions options,
            StreamingTileEntry tile,
            string styleKey,
            string folderName,
            BuildingLodStorageMode lodMode,
            string stagingFolder,
            string bakeFingerprint,
            BuildingBakeResult result)
        {
            result.Success = false;
            result.PrefabAssetPath = null;
            result.PhysicsPrefabAssetPath = null;

            if (tile == null || options == null)
            {
                BuildingBakeVerboseLog.Global("abort", "tile or options is null");
                yield break;
            }

            string tileId = tile.TileId;
            var tileSw = Stopwatch.StartNew();
            BuildingBakeVerboseLog.Tile(tileId, "start",
                $"style={styleKey} folder={folderName} lodMode={lodMode} staging={stagingFolder}");

            string glbRel = $"{folderName}/buildings_{tileId}.glb";
            string glbFull = Path.Combine(options.OutputRoot,
                glbRel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(glbFull))
            {
                BuildingBakeVerboseLog.TileWarning(tileId, "glb-missing", glbFull);
                yield break;
            }

            BuildingBakeVerboseLog.Tile(tileId, "glb-found",
                $"{glbFull} ({new FileInfo(glbFull).Length / 1024} KB)");

            ZGConnectPathUtils.EnsureAssetFolder(stagingFolder);
            BuildingBakeVerboseLog.Tile(tileId, "staging-ready", stagingFolder);

            var loadState = new GlbLoadState();
            IEnumerator loadRoutine = InstantiateBuildingGlbCoroutine(glbFull, tileId, loadState);
            while (loadRoutine.MoveNext())
                yield return loadRoutine.Current;

            GameObject instance = loadState.Instance;
            GltfImport gltfImport = loadState.Import;
            if (instance == null)
            {
                BuildingBakeVerboseLog.TileWarning(tileId, "glb-load-failed", loadState.Error);
                yield break;
            }

            BuildingBakeVerboseLog.Tile(tileId, "glb-load-ok",
                $"{BuildingBakeVerboseLog.HierarchyStats(instance.transform)} elapsedMs={tileSw.ElapsedMilliseconds}");

            try
            {
                BuildingBakeVerboseLog.Tile(tileId, "lod-setup-start", $"mode={lodMode}");
                SetupLod(instance.transform, tileId, folderName, lodMode, options);
                yield return null;
                BuildingBakeVerboseLog.Tile(tileId, "lod-setup-done",
                    BuildingBakeVerboseLog.HierarchyStats(instance.transform));

                BuildingsMetadataJson meta = LoadMetadata(tileId, folderName, options);
                BuildingBakeVerboseLog.Tile(tileId, "metadata",
                    meta?.Buildings != null ? $"buildings={meta.Buildings.Count}" : "none");
                StripBuildingDataComponents(instance.transform);
                RuntimeGlbLoader.ApplyOriginCorrection(instance.transform, tileId, meta);
                yield return null;

                BuildingMaterialApplyStyle materialStyle = styleKey == StreamingPackBakeKeys.OrthoRoof
                    ? BuildingMaterialApplyStyle.RoofOrthophoto
                    : BuildingMaterialApplyStyle.FacadeAndRoofVariants;

                if (options.BuildingSurfaceSettings != null)
                {
                    BuildingBakeVerboseLog.Tile(tileId, "materials-start", $"style={materialStyle}");
                    yield return ApplyMaterialsCoroutine(
                        instance, tile, meta, options, materialStyle, tileId);
                    BuildingBakeVerboseLog.Tile(tileId, "materials-done",
                        $"elapsedMs={tileSw.ElapsedMilliseconds}");
                }
                else
                {
                    BuildingBakeVerboseLog.TileWarning(tileId, "materials-skipped",
                        "BuildingSurfaceSettings not assigned");
                }

                RuntimeBuildingTilePostProcessSettings postSettings =
                    RealtimePackBakeFingerprint.CreateManifest(options).ToPostProcessSettings();
                BuildingBakeVerboseLog.Tile(tileId, "finalize-start",
                    $"combine={postSettings.CombineTileMeshes} perMaterial={postSettings.CombineMeshesPerMaterial} " +
                    $"collider={postSettings.ColliderMode}");

                var record = RuntimeTileRecord.FromLeaf(tile, options.Heightmap.Metadata.Settings.TileSizeMeters);
                if (!FinalizeBakedTile(instance, record, postSettings, tileId, out string finalizeError))
                {
                    BuildingBakeVerboseLog.TileWarning(tileId, "finalize-failed", finalizeError);
                    yield break;
                }

                RuntimeBuildingTileRuntimeState runtimeState =
                    instance.GetComponent<RuntimeBuildingTileRuntimeState>();
                string combinedInfo = runtimeState?.CombinedRenderRoot != null
                    ? $"combinedChildren={runtimeState.CombinedRenderRoot.transform.childCount} " +
                      $"ownedMeshes={runtimeState.OwnedMeshes.Count}"
                    : "no-combined-root";
                BuildingBakeVerboseLog.Tile(tileId, "finalize-ok", combinedInfo);

                RuntimeBuildingTileRuntimeState state =
                    runtimeState ?? instance.AddComponent<RuntimeBuildingTileRuntimeState>();
                BuildingBakeVerboseLog.Tile(tileId, "finalize-strip-sources-start", null);
                RuntimeBuildingTilePostProcessor.StripBakedSourceVisualGeometry(
                    instance.transform, state);
                BuildingBakeVerboseLog.Tile(tileId, "finalize-strip-sources-done",
                    BuildingBakeVerboseLog.HierarchyStats(instance.transform));
                yield return null;
                state.ResolveCombinedRenderRoot();
                state.IsFullyFinalized = true;
                state.FinalizedSettingsFingerprint =
                    RuntimeBuildingTileRuntimeState.ComputeSettingsFingerprint(postSettings);
                state.PackBakeFingerprint = bakeFingerprint;
                StripBuildingDataComponents(instance.transform);

                var marker = instance.GetComponent<RuntimeBuildingTileBakedMarker>() ??
                               instance.AddComponent<RuntimeBuildingTileBakedMarker>();
                marker.PackBakeFingerprint = bakeFingerprint;
                marker.BuildingStyleKey = styleKey;
                BuildingBakeVerboseLog.Tile(tileId, "markers-set", $"fingerprint={bakeFingerprint}");

                string visualPrefabPath = $"{stagingFolder}/TileBuildings_{tileId}.prefab";
                string physicsPrefabPath = $"{stagingFolder}/TileBuildings_{tileId}_Physics.prefab";
                BuildingBakeVerboseLog.Tile(tileId, "prefab-split-start", visualPrefabPath);

                GameObject physicsBranch = UnityEngine.Object.Instantiate(instance);
                physicsBranch.name = $"{instance.name}_Physics";
                RuntimeBuildingTilePhysicsSplitUtility.PrepareVisualOnlyInstance(instance);
                RuntimeBuildingTilePhysicsSplitUtility.PreparePhysicsOnlyInstance(physicsBranch);

                var physicsMarker = physicsBranch.GetComponent<RuntimeBuildingTilePhysicsMarker>() ??
                                    physicsBranch.AddComponent<RuntimeBuildingTilePhysicsMarker>();
                physicsMarker.PackBakeFingerprint = bakeFingerprint;
                physicsMarker.BuildingStyleKey = styleKey;

                BuildingBakeVerboseLog.Tile(tileId, "prefab-save-visual-start", visualPrefabPath);
                bool visualOk = SaveBakedBuildingPrefab(instance, visualPrefabPath, tileId, styleKey);
                bool physicsOk = false;
                if (visualOk)
                {
                    BuildingBakeVerboseLog.Tile(tileId, "prefab-save-physics-start", physicsPrefabPath);
                    physicsOk = SaveBakedBuildingPrefab(physicsBranch, physicsPrefabPath, tileId, styleKey);
                }

                UnityEngine.Object.DestroyImmediate(physicsBranch);

                result.PrefabAssetPath = visualPrefabPath;
                result.PhysicsPrefabAssetPath = physicsOk ? physicsPrefabPath : null;
                result.Success = visualOk;
                BuildingBakeVerboseLog.Tile(tileId, result.Success ? "prefab-save-ok" : "prefab-save-failed",
                    $"visual={visualOk} physics={physicsOk} elapsedMs={tileSw.ElapsedMilliseconds}");
                yield return null;

                if (result.Success)
                {
                    BuildingBakeVerboseLog.Tile(tileId, "complete",
                        $"totalMs={tileSw.ElapsedMilliseconds}");
                }
            }
            finally
            {
                BuildingBakeVerboseLog.Tile(tileId, "cleanup",
                    $"disposingGltfImport={(gltfImport != null)} destroyingInstance={(instance != null)}");
                gltfImport?.Dispose();
                if (instance != null)
                    UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        /// <summary>
        /// Saves a baked building instance as an embed-safe prefab (CombinedRender + collider meshes).
        /// Use when re-staging instances loaded from AssetBundles (e.g. player-build collider sanitize).
        /// </summary>
        public static bool SaveInstanceAsBakedBuildingPrefab(
            GameObject instance,
            string prefabAssetPath,
            string logLabel = "bundle-rebuild")
        {
            return SaveBakedBuildingPrefab(instance, prefabAssetPath, logLabel, "sanitize");
        }

        static bool SaveBakedBuildingPrefab(
            GameObject instance,
            string prefabAssetPath,
            string tileId,
            string styleKey)
        {
            if (instance == null || string.IsNullOrEmpty(prefabAssetPath))
            {
                BuildingBakeVerboseLog.TileWarning(tileId, "prefab-save-abort", "instance or path is null");
                return false;
            }

            string folder = Path.GetDirectoryName(prefabAssetPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(folder))
                ZGConnectPathUtils.EnsureAssetFolder(folder);

            HideFlags previousFlags = instance.hideFlags;
            PrepareInstanceForPrefabSave(instance);

            try
            {
                BuildingBakeVerboseLog.Tile(tileId, "prefab-pass1", "SaveAsPrefabAsset (initial)");
                GameObject pass1 = PrefabUtility.SaveAsPrefabAsset(instance, prefabAssetPath);
                if (pass1 == null && !TryLoadPrefabAsset(prefabAssetPath, out pass1))
                {
                    string fullPath = ZGConnectPathUtils.AssetPathToFullPath(prefabAssetPath);
                    bool onDisk = File.Exists(fullPath);
                    BuildingBakeVerboseLog.TileWarning(tileId, "prefab-pass1-result",
                        $"SaveAsPrefabAsset returned null, import failed, onDisk={onDisk} path={prefabAssetPath}");
                    return false;
                }

                BuildingBakeVerboseLog.Tile(tileId, "prefab-pass1-result", "ok");

                int filterMeshesEmbedded = CountEphemeralMeshFilters(instance);
                BuildingBakeVerboseLog.Tile(tileId, "prefab-embed-filters-start",
                    $"ephemeralMeshFilters={filterMeshesEmbedded}");
                bool embeddedFilters = BuildingSurfaceMeshProcessor.PersistEphemeralMeshes(instance, prefabAssetPath);

                int colliderMeshesEmbedded = CountEphemeralColliderMeshes(instance);
                BuildingBakeVerboseLog.Tile(tileId, "prefab-embed-colliders-start",
                    $"ephemeralColliderMeshes={colliderMeshesEmbedded}");
                bool embeddedColliders = PersistMeshColliderMeshes(instance, prefabAssetPath, tileId);

                if (embeddedFilters || embeddedColliders)
                {
                    BuildingBakeVerboseLog.Tile(tileId, "prefab-save-assets",
                        $"embeddedFilters={embeddedFilters} embeddedColliders={embeddedColliders}");
                    AssetDatabase.SaveAssets();
                }

                BuildingBakeVerboseLog.Tile(tileId, "prefab-pass2", "SaveAsPrefabAsset (after embed)");
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(instance, prefabAssetPath);
                if (prefab != null)
                {
                    BuildingBakeVerboseLog.Tile(tileId, "prefab-pass2-result", "ok");
                    return true;
                }

                if (TryLoadPrefabAsset(prefabAssetPath, out prefab))
                {
                    BuildingBakeVerboseLog.Tile(tileId, "prefab-load-after-refresh", "ok");
                    return true;
                }

                BuildingBakeVerboseLog.TileWarning(tileId, "prefab-save-failed",
                    $"AssetDatabase could not load prefab at {prefabAssetPath} " +
                    $"(children={instance.transform.childCount} style={styleKey})");
                return false;
            }
            finally
            {
                instance.hideFlags = previousFlags;
            }
        }

        static void PrepareInstanceForPrefabSave(GameObject instance)
        {
            if (instance == null)
                return;

            instance.hideFlags = HideFlags.None;
            foreach (Transform transform in instance.GetComponentsInChildren<Transform>(true))
                transform.gameObject.hideFlags = HideFlags.None;

            if (PrefabUtility.IsPartOfPrefabInstance(instance))
            {
                PrefabUtility.UnpackPrefabInstance(
                    instance,
                    PrefabUnpackMode.Completely,
                    InteractionMode.AutomatedAction);
            }

            MakeBakedMeshesReadableForPrefab(instance);
            StripBuildingDataComponents(instance.transform);
            RuntimeBuildingTilePostProcessor.SanitizeConvexMeshColliders(instance.transform);
        }

        static void MakeBakedMeshesReadableForPrefab(GameObject instance)
        {
            Transform combinedRoot = instance.transform.Find(
                RuntimeBuildingTilePostProcessor.CombinedRenderRootName);
            if (combinedRoot != null)
                BuildingMeshProcessingUtility.MakeInstanceMeshesReadable(combinedRoot.gameObject);

            foreach (MeshCollider collider in instance.GetComponentsInChildren<MeshCollider>(true))
            {
                Mesh mesh = collider.sharedMesh;
                if (mesh == null || mesh.isReadable)
                    continue;

                Mesh readable = BuildingMeshProcessingUtility.CloneMeshGeometry(mesh);
                if (readable == null)
                    continue;

                readable.name = mesh.name + "_Readable";
                collider.sharedMesh = readable;
            }
        }

        static bool TryLoadPrefabAsset(string prefabAssetPath, out GameObject prefab)
        {
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath);
            if (prefab != null)
                return true;

            string fullPath = ZGConnectPathUtils.AssetPathToFullPath(prefabAssetPath);
            if (!File.Exists(fullPath))
                return false;

            AssetDatabase.ImportAsset(prefabAssetPath, ImportAssetOptions.ForceUpdate);
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath);
            return prefab != null;
        }

        static int CountEphemeralMeshFilters(GameObject root)
        {
            int count = 0;
            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(mf.sharedMesh)))
                    count++;
            }

            return count;
        }

        static int CountEphemeralColliderMeshes(GameObject root)
        {
            int count = 0;
            foreach (MeshCollider collider in root.GetComponentsInChildren<MeshCollider>(true))
            {
                if (collider.sharedMesh != null &&
                    string.IsNullOrEmpty(AssetDatabase.GetAssetPath(collider.sharedMesh)))
                    count++;
            }

            return count;
        }

        static bool PersistMeshColliderMeshes(GameObject root, string prefabAssetPath, string tileId)
        {
            if (root == null || string.IsNullOrEmpty(prefabAssetPath))
                return false;

            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath) == null)
            {
                BuildingBakeVerboseLog.TileWarning(tileId, "embed-colliders-skip",
                    $"prefab asset not loaded: {prefabAssetPath}");
                return false;
            }

            int embedded = 0;
            foreach (MeshCollider collider in root.GetComponentsInChildren<MeshCollider>(true))
            {
                Mesh mesh = collider.sharedMesh;
                if (mesh == null || !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(mesh)))
                    continue;

                Mesh owned = UnityEngine.Object.Instantiate(mesh);
                owned.name = mesh.name;
                collider.sharedMesh = owned;
                owned.hideFlags = HideFlags.None;
                AssetDatabase.AddObjectToAsset(owned, prefabAssetPath);
                EditorUtility.SetDirty(owned);
                embedded++;
            }

            if (embedded > 0)
            {
                EditorUtility.SetDirty(AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath));
                BuildingBakeVerboseLog.Tile(tileId, "embed-colliders-done", $"count={embedded}");
            }

            return embedded > 0;
        }

        sealed class GlbLoadState
        {
            public GameObject Instance;
            public GltfImport Import;
            public string Error;
        }

        static IEnumerator InstantiateBuildingGlbCoroutine(
            string glbFullPath,
            string tileId,
            GlbLoadState state)
        {
            if (!File.Exists(glbFullPath))
            {
                state.Error = $"file not found: {glbFullPath}";
                BuildingBakeVerboseLog.TileWarning(tileId, "glb-read-failed", state.Error);
                yield break;
            }

            byte[] glbBytes;
            try
            {
                BuildingBakeVerboseLog.Tile(tileId, "glb-read-start", glbFullPath);
                glbBytes = File.ReadAllBytes(glbFullPath);
            }
            catch (Exception ex)
            {
                state.Error = $"read failed: {ex.Message}";
                BuildingBakeVerboseLog.TileWarning(tileId, "glb-read-failed", state.Error);
                yield break;
            }

            if (glbBytes == null || glbBytes.Length == 0)
            {
                state.Error = "file is empty";
                BuildingBakeVerboseLog.TileWarning(tileId, "glb-read-failed", state.Error);
                yield break;
            }

            BuildingBakeVerboseLog.Tile(tileId, "glb-read-ok", $"bytes={glbBytes.Length}");

            var deferAgent = new UninterruptedDeferAgent();
            GltfImport gltfImport = new GltfImport(deferAgent: deferAgent);
            var uri = new Uri(Path.GetFullPath(glbFullPath));

            BuildingBakeVerboseLog.Tile(tileId, "gltfast-load-start", uri.ToString());
            var loadSw = Stopwatch.StartNew();
            Task<bool> loadTask = gltfImport.Load(glbBytes, uri);
            while (!loadTask.IsCompleted)
                yield return null;

            if (!loadTask.Result)
            {
                state.Error = "GLTFast Load returned false";
                BuildingBakeVerboseLog.TileWarning(tileId, "gltfast-load-failed",
                    $"elapsedMs={loadSw.ElapsedMilliseconds}");
                gltfImport.Dispose();
                yield break;
            }

            BuildingBakeVerboseLog.Tile(tileId, "gltfast-load-ok", $"elapsedMs={loadSw.ElapsedMilliseconds}");
            yield return null;

            GameObject instance = new GameObject($"TileBuildings_{tileId}");
            BuildingBakeVerboseLog.Tile(tileId, "gltfast-instantiate-start", null);
            loadSw.Restart();
            Task<bool> instantiateTask = gltfImport.InstantiateMainSceneAsync(instance.transform);
            while (!instantiateTask.IsCompleted)
                yield return null;

            if (!instantiateTask.Result)
            {
                state.Error = "GLTFast InstantiateMainSceneAsync returned false";
                BuildingBakeVerboseLog.TileWarning(tileId, "gltfast-instantiate-failed",
                    $"elapsedMs={loadSw.ElapsedMilliseconds}");
                UnityEngine.Object.DestroyImmediate(instance);
                gltfImport.Dispose();
                yield break;
            }

            BuildingBakeVerboseLog.Tile(tileId, "gltfast-instantiate-ok",
                $"{BuildingBakeVerboseLog.HierarchyStats(instance.transform)} elapsedMs={loadSw.ElapsedMilliseconds}");

            RuntimeBuildingHierarchyUtility.FlattenRuntimeGltfWrappers(instance.transform);
            BuildingBakeVerboseLog.Tile(tileId, "gltfast-flatten-ok",
                BuildingBakeVerboseLog.HierarchyStats(instance.transform));
            state.Instance = instance;
            state.Import = gltfImport;
        }

        static void SetupLod(
            Transform root,
            string tileId,
            string folderName,
            BuildingLodStorageMode lodMode,
            RealtimePackOptions options)
        {
            var lodController = new BuildingLodController(
                cullDistanceMeters: 0f,
                lod0Screen: 0.5f,
                lod1Screen: 0.15f);

            if (lodMode == BuildingLodStorageMode.DualFile)
            {
                string lod1Rel = $"{folderName}/buildings_{tileId}_lod1.glb";
                string lod1Full = Path.Combine(options.OutputRoot,
                    lod1Rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(lod1Full))
                {
                    BuildingBakeVerboseLog.Tile(tileId, "lod-dual-fallback",
                        $"lod1 missing at {lod1Full}, using hierarchy LOD");
                    lodController.SetupLodGroupsFromHierarchy(root);
                    return;
                }

                BuildingBakeVerboseLog.Tile(tileId, "lod-dual-merge-start", lod1Full);
                string stagingDir = $"{RealtimePackStagingUtility.BuildingRoot}/_tmp_{tileId}";
                ZGConnectPathUtils.EnsureAssetFolder(stagingDir);
                string lod1Asset = $"{stagingDir}/lod1.glb";
                File.Copy(lod1Full, ZGConnectPathUtils.AssetPathToFullPath(lod1Asset), true);
                AssetDatabase.ImportAsset(lod1Asset, ImportAssetOptions.ForceUpdate);

                GameObject lod1AssetGo = AssetDatabase.LoadAssetAtPath<GameObject>(lod1Asset);
                if (lod1AssetGo == null)
                {
                    BuildingBakeVerboseLog.TileWarning(tileId, "lod-dual-import-failed",
                        $"could not load {lod1Asset}, using hierarchy LOD");
                    lodController.SetupLodGroupsFromHierarchy(root);
                    AssetDatabase.DeleteAsset(lod1Asset);
                    return;
                }

                GameObject lod1Instance = UnityEngine.Object.Instantiate(lod1AssetGo);
                lod1Instance.name = $"TileBuildings_{tileId}_lod1";
                lod1Instance.transform.SetParent(root.parent, false);
                try
                {
                    RuntimeBuildingHierarchyUtility.FlattenRuntimeGltfWrappers(lod1Instance.transform);
                    lodController.MergeDualGlb(root, lod1Instance.transform);
                    BuildingBakeVerboseLog.Tile(tileId, "lod-dual-merge-ok",
                        BuildingBakeVerboseLog.HierarchyStats(root));
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(lod1Instance);
                    AssetDatabase.DeleteAsset(lod1Asset);
                }
            }
            else
            {
                BuildingBakeVerboseLog.Tile(tileId, "lod-hierarchy-setup", null);
                lodController.SetupLodGroupsFromHierarchy(root);
            }
        }

        public static void StripBuildingDataComponents(Transform tileRoot)
        {
            if (tileRoot == null)
                return;

            BuildingLodController.ForEachBuildingTransform(tileRoot, building =>
            {
                foreach (BuildingData buildingData in building.GetComponentsInChildren<BuildingData>(true))
                {
                    if (buildingData != null)
                        UnityEngine.Object.DestroyImmediate(buildingData);
                }

#if UNITY_EDITOR
                foreach (Transform transform in building.GetComponentsInChildren<Transform>(true))
                    RealtimeStreamingEditorMissingScriptUtility.StripGameObject(transform.gameObject);
#endif
            });
        }

        static BuildingsMetadataJson LoadMetadata(string tileId, string folderName, RealtimePackOptions options)
        {
            string jsonFull = Path.Combine(options.OutputRoot,
                $"{folderName}/buildings_{tileId}.json".Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(jsonFull))
                return null;

            try
            {
                return JsonConvert.DeserializeObject<BuildingsMetadataJson>(File.ReadAllText(jsonFull));
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning(
                    $"[ZGConnect.Realtime] Failed to parse buildings JSON for '{tileId}': {ex.Message}");
                return null;
            }
        }

        static IEnumerator ApplyMaterialsCoroutine(
            GameObject buildingsRoot,
            StreamingTileEntry tile,
            BuildingsMetadataJson meta,
            RealtimePackOptions options,
            BuildingMaterialApplyStyle style,
            string tileId)
        {
            string basemapId = options.PackOrthoBasemapId ?? "ortho";
            Texture2D orthoTex = TryLoadPackedOrthoTexture(tile, basemapId, options, out TerrainLayer orthoLayer);
            BuildingBakeVerboseLog.Tile(tileId, "materials-ortho",
                orthoTex != null ? $"loaded {basemapId} {orthoTex.width}x{orthoTex.height}" : "no ortho texture");
            CityTileRecord cityRec = BuildCityTileRecord(tile, options, orthoTex, orthoLayer, basemapId);

            Material roofTemplate = options.RoofOrthophotoMaterialTemplate
                ?? options.BuildingSurfaceSettings.GetFlatRoofMaterial(BuildingCategory.House, 0);

            bool useSlotKeys = meta != null && BuildingMaterialSlotApplier.TileMetadataHasSlotKeys(meta);
            var roofCache = new Dictionary<string, Material>();
            int processed = 0;

            if (useSlotKeys)
            {
                BuildingMaterialSlotApplier.CollectMetadataBuildingEntries(
                    buildingsRoot, meta, out _, out List<(GameObject building, BuildingEntryJson entry)> pairs);
                BuildingBakeVerboseLog.Tile(tileId, "materials-slot-keys", $"buildings={pairs.Count}");
                foreach ((GameObject building, BuildingEntryJson entry) pair in pairs)
                {
                    BuildingMaterialSlotApplier.ApplyBuildingFromMetadata(
                        pair.building,
                        pair.entry,
                        cityRec,
                        options.BuildingSurfaceSettings,
                        style,
                        basemapId,
                        roofTemplate,
                        roofCache,
                        useOrthophotoBasemapForRoofs: true);

                    processed++;
                    if (processed % MaterialApplyYieldInterval == 0)
                        yield return null;
                }
            }
            else
            {
                var buildings = new List<Transform>();
                BuildingLodController.ForEachBuildingTransform(buildingsRoot.transform, building => buildings.Add(building));
                BuildingBakeVerboseLog.Tile(tileId, "materials-shared", $"buildings={buildings.Count}");
                foreach (Transform building in buildings)
                {
                    BuildingSharedMaterialApplier.ApplyBuilding(
                        building.gameObject,
                        cityRec,
                        options.BuildingSurfaceSettings,
                        style,
                        basemapId,
                        roofTemplate,
                        roofCache,
                        useOrthophotoBasemapForRoofs: true);

                    processed++;
                    if (processed % MaterialApplyYieldInterval == 0)
                        yield return null;
                }
            }
        }

        static CityTileRecord BuildCityTileRecord(
            StreamingTileEntry tile,
            RealtimePackOptions options,
            Texture2D orthoTex,
            TerrainLayer orthoLayer,
            string basemapId)
        {
            int tileSize = options.Heightmap.Metadata.Settings.TileSizeMeters;
            var rec = new CityTileRecord
            {
                tileId = tile.TileId,
                left = tile.Left,
                bottom = tile.Bottom,
                right = tile.Right,
                top = tile.Top,
                unityPosition = tile.GetUnityPosition(),
            };

            if (orthoTex == null)
                return rec;

            rec.basemapLayers = new List<BasemapLayerEntry>
            {
                new BasemapLayerEntry
                {
                    basemapId = basemapId,
                    texture = orthoTex,
                    terrainLayer = orthoLayer,
                },
            };
            rec.primaryBasemapTexture = orthoTex;
            rec.primaryTerrainLayer = orthoLayer;
            return rec;
        }

        static Texture2D TryLoadPackedOrthoTexture(
            StreamingTileEntry tile,
            string basemapId,
            RealtimePackOptions options,
            out TerrainLayer layer)
        {
            layer = null;
            if (tile?.OrthoPaths == null ||
                !tile.OrthoPaths.TryGetValue(basemapId, out string orthoRel) ||
                string.IsNullOrEmpty(orthoRel))
            {
                return null;
            }

            string orthoAsset = $"{RuntimeStreamingPaths.PackedDatasetAssetRoot}/{orthoRel.Replace('\\', '/')}";
            string orthoFull = ZGConnectPathUtils.AssetPathToFullPath(orthoAsset);
            if (!File.Exists(orthoFull))
                return null;

            Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(orthoAsset);
            if (tex == null)
            {
                AssetDatabase.ImportAsset(orthoAsset, ImportAssetOptions.ForceUpdate);
                tex = AssetDatabase.LoadAssetAtPath<Texture2D>(orthoAsset);
            }

            if (tex == null)
                return null;

            int tileSize = options.Heightmap.Metadata.Settings.TileSizeMeters;
            layer = new TerrainLayer
            {
                name = $"{basemapId}_{tile.TileId}",
                diffuseTexture = tex,
                tileSize = new Vector2(tileSize, tileSize),
                tileOffset = Vector2.zero,
                smoothnessSource = TerrainLayerSmoothnessSource.Constant,
                smoothness = 0f,
                metallic = 0f,
            };
            return tex;
        }

        static bool FinalizeBakedTile(
            GameObject rootGo,
            RuntimeTileRecord record,
            RuntimeBuildingTilePostProcessSettings postSettings,
            string tileId,
            out string error)
        {
            error = null;
            Transform tileRoot = rootGo.transform;
            RuntimeBuildingTileRuntimeState state =
                tileRoot.GetComponent<RuntimeBuildingTileRuntimeState>() ??
                tileRoot.gameObject.AddComponent<RuntimeBuildingTileRuntimeState>();

            BuildingBakeVerboseLog.Tile(tileId, "finalize-release-combined", null);
            state.ReleaseCombinedVisuals();
            RuntimeBuildingTilePostProcessor.DisableSourceRenderersForFinalize(tileRoot, state);
            BuildingBakeVerboseLog.Tile(tileId, "finalize-disabled-sources",
                $"lod0Renderers={CountLod0Renderers(tileRoot)}");

            if (postSettings.CombineTileMeshes)
            {
                BuildingBakeVerboseLog.Tile(tileId, "finalize-combine-start",
                    $"perMaterial={postSettings.CombineMeshesPerMaterial}");
                RuntimeBuildingTilePostProcessor.BuildCombinedRender(
                    tileRoot, state, postSettings.CombineMeshesPerMaterial);

                if (state.CombinedRenderRoot != null)
                {
                    state.CombinedRenderRoot.SetActive(true);
                    state.SetCombinedRenderVisible(true);
                    BuildingBakeVerboseLog.Tile(tileId, "finalize-combine-ok",
                        $"combinedChildren={state.CombinedRenderRoot.transform.childCount} " +
                        $"ownedMeshes={state.OwnedMeshes.Count}");
                }
                else
                {
                    int rendererCount = CountLod0Renderers(tileRoot);
                    error =
                        $"combined render bake produced no meshes (lod0 renderers={rendererCount}). " +
                        "Check GLB hierarchy flattening and mesh readability.";
                    BuildingBakeVerboseLog.TileWarning(tileId, "finalize-combine-failed", error);
                    state.RestoreSourceRenderers();
                    return false;
                }
            }
            else
            {
                BuildingBakeVerboseLog.Tile(tileId, "finalize-combine-skipped", null);
                state.RestoreSourceRenderers();
            }

            var buildings = new List<Transform>();
            BuildingLodController.ForEachBuildingTransform(tileRoot, building => buildings.Add(building));
            BuildingBakeVerboseLog.Tile(tileId, "finalize-buildings-found", $"count={buildings.Count}");
            if (buildings.Count == 0)
            {
                error = $"no building transforms under tile root (childCount={tileRoot.childCount})";
                BuildingBakeVerboseLog.TileWarning(tileId, "finalize-no-buildings", error);
                return false;
            }

            BuildingBakeVerboseLog.Tile(tileId, "finalize-colliders-start", $"mode={postSettings.ColliderMode}");
            RuntimeBuildingTilePostProcessor.ClearAllColliders(buildings);
            foreach (Transform building in buildings)
            {
                RuntimeBuildingTilePostProcessor.ApplyColliderForBuilding(
                    building,
                    tileRoot,
                    state,
                    postSettings.ColliderMode);
            }

            int colliderCount = 0;
            foreach (Transform building in buildings)
                colliderCount += building.GetComponentsInChildren<Collider>(true).Length;

            BuildingBakeVerboseLog.Tile(tileId, "finalize-colliders-done", $"count={colliderCount}");
            if (postSettings.ColliderMode != RealtimeBuildingColliderMode.None && colliderCount == 0)
            {
                error = $"collider bake produced zero colliders (mode={postSettings.ColliderMode})";
                BuildingBakeVerboseLog.TileWarning(tileId, "finalize-colliders-failed", error);
                return false;
            }

            return true;
        }

        static int CountLod0Renderers(Transform tileRoot)
        {
            int count = 0;
            BuildingLodController.ForEachBuildingTransform(tileRoot, building =>
            {
                foreach (MeshRenderer _ in RuntimeBuildingMeshUtility.GetLod0Renderers(building))
                    count++;
            });
            return count;
        }
    }
}
