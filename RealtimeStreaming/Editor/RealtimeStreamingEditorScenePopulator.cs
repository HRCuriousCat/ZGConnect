using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using ZGConnect;

namespace ZGConnect.RealtimeStreaming.Editor
{
    public static class RealtimeStreamingEditorScenePopulator
    {
        const string kTerrainRootName = "ZG Connect Terrains (Realtime)";
        const string kBuildingsRootName = "ZG Connect Buildings (Realtime)";
        const int kLargeTerrainPopulateWarningThreshold = 20;

        public static void Populate(RealtimeStreamingController controller, Vector3 cameraPosition)
        {
            if (controller == null)
                return;

            if (Application.isPlaying)
            {
                EditorUtility.DisplayDialog(
                    "Populate Scene",
                    "Exit Play mode before populating the scene in the editor.",
                    "OK");
                return;
            }

            if (!controller.EditorTryBuildPopulateRequest(
                    cameraPosition, out RealtimeStreamingEditorPopulateRequest request, out string error))
            {
                EditorUtility.DisplayDialog("Populate Scene", error ?? "Failed to build populate request.", "OK");
                return;
            }

            if (request.TerrainTiles.Count == 0 && request.BuildingLeaves.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "Populate Scene",
                    "No terrain or building tiles match the current camera, radii, HLOD, and region settings.",
                    "OK");
                return;
            }

            if (request.TerrainTiles.Count > kLargeTerrainPopulateWarningThreshold &&
                !ConfirmLargeTerrainPopulate(request.TerrainTiles.Count))
            {
                return;
            }

            if (request.StreamTerrain && request.TerrainTiles.Count > 0)
            {
                Debug.LogWarning(
                    "[ZGConnect.Realtime] Editor populate: Stream Terrain is enabled on the controller — " +
                    "runtime will spawn additional terrains at play time, duplicating populated tiles.");
            }

            Clear(controller, registerUndo: false);

            Transform terrainRoot = EnsureSceneStreamRoot(kTerrainRootName);
            Transform buildingsRoot = EnsureSceneStreamRoot(kBuildingsRootName);

            Undo.RegisterFullObjectHierarchyUndo(terrainRoot.gameObject, "Populate ZG Connect Scene");
            Undo.RegisterFullObjectHierarchyUndo(buildingsRoot.gameObject, "Populate ZG Connect Scene");

            var terrainEntries = new List<RuntimeTerrainFactory.TerrainBoundsEntry>();
            int terrainLoaded = 0;
            int terrainFailed = 0;
            int buildingLoaded = 0;
            int buildingFailed = 0;
            var roofMaterialCache = new Dictionary<string, Material>();

            if (request.StreamBuildings && request.BuildingSurfaceSettings == null)
            {
                Debug.LogWarning(
                    "[ZGConnect.Realtime] Editor populate: Building Surface Settings not assigned — " +
                    "building meshes will load but CombinedRender materials cannot be applied.");
            }

            try
            {
                if (request.TerrainTiles.Count > 0)
                {
                    int total = request.TerrainTiles.Count;
                    for (int i = 0; i < total; i++)
                    {
                        RuntimeTileRecord template = request.TerrainTiles[i];
                        EditorUtility.DisplayProgressBar(
                            "Populate Scene",
                            $"Terrain {template.Key} ({i + 1}/{total})",
                            total > 0 ? (float)(i + 1) / total : 1f);

                        if (TryCreateEditorTerrain(controller, request, template, terrainRoot, out Terrain terrain))
                        {
                            terrainEntries.Add(new RuntimeTerrainFactory.TerrainBoundsEntry(
                                terrain, template.Left, template.Bottom, template.TileSizeMeters));
                            terrainLoaded++;
                        }
                        else
                        {
                            terrainFailed++;
                        }
                    }
                }

                foreach (RuntimeTerrainFactory.TerrainBoundsEntry entry in terrainEntries)
                {
                    RuntimeTerrainFactory.SetNeighborsByBounds(
                        entry.Terrain, entry.Left, entry.Bottom, entry.SizeMeters, terrainEntries);
                }

                if (request.StreamBuildings)
                {
                    int total = request.BuildingLeaves.Count;
                    for (int i = 0; i < total; i++)
                    {
                        StreamingTileEntry leaf = request.BuildingLeaves[i];
                        EditorUtility.DisplayProgressBar(
                            "Populate Scene",
                            $"Buildings {leaf.TileId} ({i + 1}/{total})",
                            total > 0 ? (float)(i + 1) / total : 1f);

                        if (TryCreateEditorBuilding(controller, leaf, buildingsRoot, request, roofMaterialCache))
                            buildingLoaded++;
                        else
                            buildingFailed++;
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            EditorSceneManager.MarkSceneDirty(controller.gameObject.scene);
            Debug.Log(
                $"[ZGConnect.Realtime] Editor populate complete: terrain {terrainLoaded} loaded" +
                (terrainFailed > 0 ? $", {terrainFailed} failed" : string.Empty) +
                $"; buildings {buildingLoaded} loaded" +
                (buildingFailed > 0 ? $", {buildingFailed} failed" : string.Empty) +
                $". Camera position {cameraPosition}.");
        }

        public static void Clear(RealtimeStreamingController controller, bool registerUndo = true)
        {
            if (controller == null || Application.isPlaying)
                return;

            RealtimeStreamingEditorPopulatedTile[] tiles = Object.FindObjectsByType<RealtimeStreamingEditorPopulatedTile>(
                FindObjectsInactive.Include);
            foreach (RealtimeStreamingEditorPopulatedTile tile in tiles)
            {
                if (tile == null)
                    continue;

                if (registerUndo)
                    Undo.DestroyObjectImmediate(tile.gameObject);
                else
                    Object.DestroyImmediate(tile.gameObject);
            }
        }

        public static Vector3 ResolveCameraPosition(RealtimeStreamingController controller)
        {
            if (controller?.AssignedCameraTransform != null)
                return controller.AssignedCameraTransform.position;

            SceneView sceneView = SceneView.lastActiveSceneView;
            if (sceneView?.camera != null)
                return sceneView.camera.transform.position;

            return Vector3.zero;
        }

        /// <summary>Same scene-root lookup as RealtimeStreamingController.EnsureRoot.</summary>
        static Transform EnsureSceneStreamRoot(string name)
        {
            GameObject go = GameObject.Find(name);
            if (go == null)
                go = new GameObject(name);
            return go.transform;
        }

        static bool TryCreateEditorTerrain(
            RealtimeStreamingController controller,
            RealtimeStreamingEditorPopulateRequest request,
            RuntimeTileRecord template,
            Transform terrainRoot,
            out Terrain terrain)
        {
            terrain = null;
            if (template == null || terrainRoot == null)
                return false;

            TerrainData terrainData = null;
            GameObject terrainPrefab = null;
            AssetBundle bundle = null;
            string bundlePath = null;
            UnityEngine.Object[] bundleAssets = null;

            if (request.PreferTerrainBundles &&
                controller.EditorTryResolveTerrainBundlePath(template, out bundlePath))
            {
                var loadResult = new RuntimeTerrainBundleLoader.LoadResult();
                if (!RuntimeTerrainBundleLoader.TryLoadTerrainDataSync(bundlePath, loadResult))
                {
                    Debug.LogWarning(
                        $"[ZGConnect.Realtime] Editor terrain bundle failed for '{template.Key}': {loadResult.Error}");
                    if (request.TerrainBundleOnlyMode)
                        return false;
                }
                else
                {
                    bundle = loadResult.Bundle;
                    bundleAssets = loadResult.AllAssets;
                    terrainPrefab = loadResult.TerrainPrefab;
                    terrainData = loadResult.TerrainData ??
                                  terrainPrefab?.GetComponent<Terrain>()?.terrainData;
                }
            }

            GameObject terrainObject;
            if (terrainPrefab != null)
            {
                // Mirrors InstantiateTerrainFromBundle: Instantiate(prefab, terrainRoot) + world position.
                terrainObject = Object.Instantiate(terrainPrefab, terrainRoot);
                terrainObject.transform.position = template.UnityPosition;

                terrain = terrainObject.GetComponent<Terrain>();
                if (terrain?.terrainData != null)
                {
                    TerrainData cloned = RealtimeStreamingEditorBundleAssetCloner.CloneTerrainDataForScene(
                        terrain.terrainData, bundleAssets);
                    terrain.terrainData = cloned;
                    TerrainCollider collider = terrainObject.GetComponent<TerrainCollider>();
                    if (collider != null)
                        collider.terrainData = cloned;
                }
            }
            else
            {
                if (terrainData == null && !request.TerrainBundleOnlyMode &&
                    controller.EditorTryResolveHeightmapPath(template, out string hmPath))
                {
                    Vector3 size = template.IsSupertile
                        ? template.Supertile.GetTerrainSize()
                        : template.Leaf.GetTerrainSize();
                    bool flip = controller.EditorResolveHeightmapFlip(template);
                    terrainData = RuntimeTerrainFactory.CreateTerrainData(
                        hmPath, template.HeightmapRes, size, flipHeightmapVertically: flip);
                }

                if (terrainData == null)
                {
                    RuntimeTerrainBundleLoader.ReleaseEditorBundle(
                        bundlePath, bundle, unloadAllLoadedObjects: false);
                    return false;
                }

                if (bundleAssets != null)
                {
                    terrainData = RealtimeStreamingEditorBundleAssetCloner.CloneTerrainDataForScene(
                        terrainData, bundleAssets);
                }

                // Mirrors InstantiateTerrainFromData / CreateTerrainGameObject path.
                terrainObject = RuntimeTerrainFactory.CreateTerrainGameObject(
                    terrainData,
                    template.UnityPosition,
                    terrainRoot,
                    request.TerrainMaterial,
                    request.DrawInstanced,
                    request.BasemapDistance);
                terrain = terrainObject.GetComponent<Terrain>();
            }

            terrainObject.name = template.IsSupertile
                ? $"Supertile_{template.Key}"
                : $"Tile_{template.Key}";

            ApplyEditorTerrainRenderingSettings(terrain, request);

            var marker = terrainObject.AddComponent<RealtimeStreamingEditorPopulatedTile>();
            marker.Initialize(template.Key, RealtimeStreamingEditorPopulatedTile.ContentType.Terrain);

            RuntimeTerrainBundleLoader.ReleaseEditorBundle(bundlePath, bundle, unloadAllLoadedObjects: false);
            return terrain != null;
        }

        static bool TryCreateEditorBuilding(
            RealtimeStreamingController controller,
            StreamingTileEntry leaf,
            Transform buildingsRoot,
            RealtimeStreamingEditorPopulateRequest request,
            Dictionary<string, Material> roofMaterialCache)
        {
            if (leaf == null || !request.StreamBuildings || buildingsRoot == null)
                return false;

            if (!controller.EditorTryResolveBuildingBundlePath(leaf, out string bundlePath))
            {
                Debug.LogWarning($"[ZGConnect.Realtime] Editor building bundle missing for '{leaf.TileId}'.");
                return false;
            }

            var loadResult = new RuntimeBuildingBundleLoader.LoadResult();
            if (!RuntimeBuildingBundleLoader.TryLoadBuildingVisualSync(bundlePath, loadResult) ||
                loadResult.Prefab == null)
            {
                Debug.LogWarning(
                    $"[ZGConnect.Realtime] Editor building bundle failed for '{leaf.TileId}': {loadResult.Error}");
                return false;
            }

            // Identical to LoadBuildingFromBundleCoroutine — no extra position offset.
            GameObject instance = Object.Instantiate(loadResult.Prefab, buildingsRoot);
            instance.name = $"TileBuildings_{leaf.TileId}";

            GameObject physicsInstance = null;
            if (loadResult.HasSplitPhysicsPrefab)
            {
                GameObject physicsPrefab = RuntimeBuildingBundleLoader.TryLoadBuildingPhysicsSync(
                    loadResult.Bundle, loadResult.PhysicsPrefabAssetName);
                if (physicsPrefab != null)
                {
                    physicsInstance = Object.Instantiate(physicsPrefab, instance.transform);
                    physicsInstance.name = $"TileBuildings_{leaf.TileId}_Physics";
                }
                else
                {
                    Debug.LogWarning(
                        $"[ZGConnect.Realtime] Editor populate: failed to load physics prefab " +
                        $"'{loadResult.PhysicsPrefabAssetName}' for '{leaf.TileId}'.");
                }
            }
            else
            {
                string[] bundleAssets = loadResult.Bundle != null ? loadResult.Bundle.GetAllAssetNames() : null;
                Debug.LogWarning(
                    $"[ZGConnect.Realtime] Editor populate: no split physics prefab detected for '{leaf.TileId}'. " +
                    $"Bundle assets: {(bundleAssets != null ? string.Join(", ", bundleAssets) : "none")}. " +
                    "Repack with building bake — physics prefab must be included in the bundle.");
            }

            RealtimeStreamingEditorBundleAssetCloner.CloneRenderableAssetsInHierarchy(instance);
            if (physicsInstance != null)
                RealtimeStreamingEditorBundleAssetCloner.CloneRenderableAssetsInHierarchy(physicsInstance);

            RealtimeStreamingBuildBundlePrepareUtility.PrepareVisualInstance(instance);
            if (physicsInstance != null)
                RealtimeStreamingBuildBundlePrepareUtility.PreparePhysicsInstance(physicsInstance);

            RealtimeStreamingEditorBundleAssetCloner.ApplyBuildingMaterials(
                instance, leaf, request, roofMaterialCache);

            var marker = instance.AddComponent<RealtimeStreamingEditorPopulatedTile>();
            marker.Initialize(leaf.TileId, RealtimeStreamingEditorPopulatedTile.ContentType.Buildings);
            RealtimeStreamingBuildBundlePrepareUtility.FinalizeEditorPopulatedBuildingTile(instance);

            // Keep scene instances alive — Unload(true) destroys instantiated bundle objects in edit mode.
            RuntimeBuildingBundleLoader.ReleaseEditorBundle(bundlePath, loadResult.Bundle, unloadAllLoadedObjects: false);
            return true;
        }

        static void ApplyEditorTerrainRenderingSettings(
            Terrain terrain,
            RealtimeStreamingEditorPopulateRequest request)
        {
            if (terrain == null)
                return;

            if (request.TerrainMaterial != null)
                terrain.materialTemplate = request.TerrainMaterial;

            terrain.drawInstanced = request.DrawInstanced;
            terrain.basemapDistance = request.BasemapDistance;
            terrain.allowAutoConnect = false;
            RuntimeBasemapFactory.ApplyMatteTerrainShading(terrain);
            terrain.Flush();
        }

        static bool ConfirmLargeTerrainPopulate(int terrainTileCount)
        {
            string message =
                $"You are about to spawn {terrainTileCount} terrain tiles in the editor.\n\n" +
                "Loading this large an area can drastically slow down Unity or temporarily freeze the editor.\n\n" +
                "Consider narrowing the load region or populating a smaller subset before continuing.";

            Debug.LogWarning(
                $"[ZGConnect.Realtime] Editor populate: {terrainTileCount} terrain tiles requested " +
                $"(threshold {kLargeTerrainPopulateWarningThreshold}).");

            return EditorUtility.DisplayDialog(
                "Populate Scene — large area",
                message,
                "Continue",
                "Cancel");
        }
    }
}
