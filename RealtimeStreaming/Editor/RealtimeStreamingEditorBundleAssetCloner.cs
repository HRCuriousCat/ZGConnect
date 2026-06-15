using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using ZGConnect;

namespace ZGConnect.RealtimeStreaming.Editor
{
    static class RealtimeStreamingEditorBundleAssetCloner
    {
        public static TerrainData CloneTerrainDataForScene(TerrainData source, UnityEngine.Object[] bundleAssets)
        {
            if (source == null)
                return null;

            RuntimeTerrainBundleLoader.RepairTerrainLayerTextures(source, bundleAssets);
            TerrainData copy = Object.Instantiate(source);
            if (copy.terrainLayers == null || copy.terrainLayers.Length == 0)
                return copy;

            var layers = new TerrainLayer[copy.terrainLayers.Length];
            for (int i = 0; i < copy.terrainLayers.Length; i++)
                layers[i] = CloneTerrainLayerForScene(copy.terrainLayers[i], bundleAssets);

            copy.terrainLayers = layers;
            return copy;
        }

        static TerrainLayer CloneTerrainLayerForScene(TerrainLayer source, UnityEngine.Object[] bundleAssets)
        {
            if (source == null)
                return null;

            Texture2D diffuse = source.diffuseTexture ?? FindFallbackTexture(bundleAssets);
            Texture2D normal = source.normalMapTexture;
            Texture2D mask = source.maskMapTexture;

            return new TerrainLayer
            {
                name = source.name,
                diffuseTexture = RuntimeBasemapFactory.CopyTextureReadable(diffuse),
                normalMapTexture = RuntimeBasemapFactory.CopyTextureReadable(normal),
                maskMapTexture = RuntimeBasemapFactory.CopyTextureReadable(mask),
                tileSize = source.tileSize,
                tileOffset = source.tileOffset,
                specular = source.specular,
                metallic = source.metallic,
                smoothness = source.smoothness,
                normalScale = source.normalScale,
                smoothnessSource = source.smoothnessSource,
                diffuseRemapMin = source.diffuseRemapMin,
                diffuseRemapMax = source.diffuseRemapMax,
                maskMapRemapMin = source.maskMapRemapMin,
                maskMapRemapMax = source.maskMapRemapMax,
            };
        }

        public static void CloneRenderableAssetsInHierarchy(GameObject root)
        {
            if (root == null)
                return;

            foreach (MeshFilter meshFilter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (meshFilter.sharedMesh != null)
                    meshFilter.sharedMesh = Object.Instantiate(meshFilter.sharedMesh);
            }

            foreach (SkinnedMeshRenderer skinnedMesh in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (skinnedMesh.sharedMesh != null)
                    skinnedMesh.sharedMesh = Object.Instantiate(skinnedMesh.sharedMesh);
            }

            foreach (MeshCollider meshCollider in root.GetComponentsInChildren<MeshCollider>(true))
            {
                if (meshCollider.sharedMesh != null)
                    meshCollider.sharedMesh = Object.Instantiate(meshCollider.sharedMesh);
            }
        }

        public static void AttachBuildingDataFromJson(GameObject buildingRoot, string tileId, string jsonPath)
        {
            if (buildingRoot == null || string.IsNullOrEmpty(jsonPath) || !File.Exists(jsonPath))
            {
                Debug.LogWarning(
                    $"[ZGConnect.Realtime] Editor populate: buildings_{tileId}.json not found at '{jsonPath}'.");
                return;
            }

            BuildingsMetadataJson meta;
            try
            {
                meta = JsonConvert.DeserializeObject<BuildingsMetadataJson>(File.ReadAllText(jsonPath));
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning(
                    $"[ZGConnect.Realtime] Editor populate: failed to read building metadata for '{tileId}': {ex.Message}");
                return;
            }

            if (meta?.Buildings == null || meta.Buildings.Count == 0)
            {
                Debug.LogWarning(
                    $"[ZGConnect.Realtime] Editor populate: buildings_{tileId}.json has no building entries.");
                return;
            }

            RealtimeStreamingEditorMissingScriptUtility.StripHierarchy(buildingRoot);

            int attached = 0;
            RuntimeBuildingHierarchyUtility.ForEachMetadataBuilding(
                buildingRoot.transform,
                meta,
                (buildingTransform, entry) =>
                {
                    if (AttachBuildingDataForEditor(buildingTransform, tileId, entry))
                        attached++;
                });

            if (attached == 0)
            {
                Debug.LogWarning(
                    $"[ZGConnect.Realtime] Editor populate: no building transforms matched metadata for '{tileId}'.");
            }
        }

        static bool AttachBuildingDataForEditor(
            Transform buildingTransform,
            string tileId,
            BuildingEntryJson entry)
        {
            if (buildingTransform == null)
                return false;

            GameObject gameObject = buildingTransform.gameObject;
            RealtimeStreamingEditorMissingScriptUtility.StripGameObject(gameObject);

            foreach (BuildingData existing in gameObject.GetComponents<BuildingData>())
                Undo.DestroyObjectImmediate(existing);

            if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(gameObject) > 0)
            {
                Debug.LogWarning(
                    $"[ZGConnect.Realtime] Editor populate: could not clear missing scripts on '{gameObject.name}'.");
                return false;
            }

            BuildingData buildingData = Undo.AddComponent<BuildingData>(gameObject);
            buildingData.tileId = tileId;
            buildingData.buildingId = BuildingSurfaceUtility.ExtractBuildingIdFromObjectName(buildingTransform.name);
            BuildingMetadataApplier.Apply(buildingData, entry);
            return true;
        }

        public static void ApplyBuildingMaterials(
            GameObject buildingRoot,
            StreamingTileEntry leaf,
            RealtimeStreamingEditorPopulateRequest request,
            Dictionary<string, Material> roofMaterialCache)
        {
            if (buildingRoot == null || leaf == null || request.BuildingSurfaceSettings == null)
                return;

            RuntimeBuildingTileRuntimeState state =
                buildingRoot.GetComponent<RuntimeBuildingTileRuntimeState>();
            state?.EnsureCombinedRenderShown();

            BuildingMaterialApplyStyle style = request.BuildingStyle == RealtimeBuildingStyle.OrthoRoof
                ? BuildingMaterialApplyStyle.RoofOrthophoto
                : BuildingMaterialApplyStyle.FacadeAndRoofVariants;

            Material roofTemplate = request.RoofOrthophotoMaterialTemplate
                ?? request.BuildingSurfaceSettings.GetFlatRoofMaterial(BuildingCategory.House, 0);

            var cityRecord = new CityTileRecord
            {
                tileId = leaf.TileId,
                left = leaf.Left,
                bottom = leaf.Bottom,
                right = leaf.Right,
                top = leaf.Top,
                unityPosition = leaf.GetUnityPosition(),
            };

            RuntimeCombinedRenderMaterialApplier.ApplyTile(
                buildingRoot.transform,
                leaf.TileId,
                cityRecord,
                request.BuildingSurfaceSettings,
                style,
                request.ActiveBasemapId,
                roofTemplate,
                roofMaterialCache);
        }

        static Texture2D FindFallbackTexture(UnityEngine.Object[] bundleAssets)
        {
            if (bundleAssets == null)
                return null;

            foreach (UnityEngine.Object asset in bundleAssets)
            {
                if (asset is Texture2D texture && texture != null)
                    return texture;
            }

            return null;
        }
    }
}
