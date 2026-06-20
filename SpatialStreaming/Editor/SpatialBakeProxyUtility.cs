using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using ZGConnect;
using ZGConnect.Editor;
using ZGConnect.SpatialStreaming;

namespace ZGConnect.SpatialStreaming.Editor
{
    public static class SpatialBakeProxyUtility
    {
        public static Material ResolveProxyMaterial(SpatialBakeProfile profile)
        {
            if (profile?.proxyMaterialOverride != null)
                return profile.proxyMaterialOverride;

            if (profile?.buildingSurfaceSettings != null)
            {
                Material facade = profile.buildingSurfaceSettings.GetFacadeMaterial(BuildingCategory.House, 0);
                if (facade != null)
                    return facade;
            }

            return AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");
        }

        public static bool TryBakeTileProxy(
            SpatialBakeProfile profile,
            string tileId,
            Vector3 tileUnityPosition,
            int tileSizeMeters,
            Transform tileRoot,
            List<Transform> buildings,
            string stagingRoot,
            out string prefabAssetPath,
            out string bundleName,
            out string error)
        {
            prefabAssetPath = null;
            bundleName = null;
            error = null;

            if (profile == null || !profile.bakeFootprintProxies || buildings == null || buildings.Count == 0)
            {
                error = "proxy bake disabled or no buildings";
                return false;
            }

            if (!TryBuildProxyCombinedRoot(
                    profile,
                    tileId,
                    tileUnityPosition,
                    tileSizeMeters,
                    tileRoot,
                    buildings,
                    out GameObject combinedRoot,
                    out List<Mesh> ownedMeshes,
                    out error))
            {
                return false;
            }

            string stagingFolder = $"{stagingRoot}/{tileId}";
            ZGConnectPathUtils.EnsureAssetFolder(stagingFolder);
            prefabAssetPath = $"{stagingFolder}/Spatial_{tileId}_tile_proxy.prefab";
            bundleName = $"{tileId}/tile_proxy";

            return SaveDetachedPrefab(combinedRoot, prefabAssetPath, ownedMeshes, out error);
        }

        public static bool TryBakeSubcellProxy(
            SpatialBakeProfile profile,
            string tileId,
            string subcellId,
            Vector3 tileUnityPosition,
            int tileSizeMeters,
            Transform subcellRoot,
            List<Transform> buildings,
            string stagingRoot,
            out string prefabAssetPath,
            out string bundleName,
            out string error)
        {
            prefabAssetPath = null;
            bundleName = null;
            error = null;

            if (profile == null ||
                !profile.bakeFootprintProxies ||
                !profile.bakeSubcellFootprintProxies ||
                buildings == null ||
                buildings.Count == 0)
            {
                error = "subcell proxy bake disabled or no buildings";
                return false;
            }

            if (!TryBuildProxyCombinedRoot(
                    profile,
                    tileId,
                    tileUnityPosition,
                    tileSizeMeters,
                    subcellRoot,
                    buildings,
                    out GameObject combinedRoot,
                    out List<Mesh> ownedMeshes,
                    out error))
            {
                return false;
            }

            string stagingFolder = $"{stagingRoot}/{tileId}";
            ZGConnectPathUtils.EnsureAssetFolder(stagingFolder);
            prefabAssetPath = $"{stagingFolder}/Spatial_{tileId}_{subcellId}_proxy.prefab";
            bundleName = $"{tileId}/{subcellId}_proxy";

            return SaveDetachedPrefab(combinedRoot, prefabAssetPath, ownedMeshes, out error);
        }

        static bool TryBuildProxyCombinedRoot(
            SpatialBakeProfile profile,
            string tileId,
            Vector3 tileUnityPosition,
            int tileSizeMeters,
            Transform bakeRoot,
            List<Transform> buildings,
            out GameObject combinedRoot,
            out List<Mesh> ownedMeshes,
            out string error)
        {
            combinedRoot = null;
            ownedMeshes = new List<Mesh>();
            error = null;

            Material proxyMaterial = ResolveProxyMaterial(profile);
            if (proxyMaterial == null)
            {
                error = "no proxy material";
                return false;
            }

            combinedRoot = profile.buildingSurfaceSettings != null
                ? SpatialFootprintBoxUtility.BuildCombinedFootprintRoot(
                    bakeRoot,
                    buildings,
                    tileId,
                    profile.buildingSurfaceSettings,
                    proxyMaterial,
                    ownedMeshes)
                : SpatialFootprintBoxUtility.BuildCombinedFootprintRoot(
                    bakeRoot,
                    buildings,
                    proxyMaterial,
                    ownedMeshes);

            if (combinedRoot == null || combinedRoot.transform.childCount == 0)
            {
                error = "footprint combine produced no geometry";
                combinedRoot = null;
                return false;
            }

            if (profile.buildingSurfaceSettings != null)
            {
                SpatialBuildingMaterialApplier.ApplyFacadeMaterialsToCombinedInstance(
                    combinedRoot,
                    tileId,
                    tileUnityPosition,
                    tileSizeMeters,
                    profile.buildingSurfaceSettings);
            }

            return true;
        }

        public static bool SaveDetachedPrefab(
            GameObject combinedRoot,
            string prefabAssetPath,
            List<Mesh> ownedMeshes,
            out string error)
        {
            error = null;
            if (combinedRoot == null)
            {
                error = "combinedRoot is null";
                return false;
            }

            Transform originalParent = combinedRoot.transform.parent;
            combinedRoot.transform.SetParent(null, false);
            combinedRoot.transform.localPosition = Vector3.zero;
            combinedRoot.transform.localRotation = Quaternion.identity;
            combinedRoot.transform.localScale = Vector3.one;

            try
            {
                SpatialBakePipeline.PrepareHierarchyForPrefabSavePublic(combinedRoot);
                foreach (Mesh mesh in ownedMeshes)
                {
                    if (mesh != null)
                        mesh.hideFlags = HideFlags.None;
                }

                GameObject pass1 = PrefabUtility.SaveAsPrefabAsset(combinedRoot, prefabAssetPath);
                if (pass1 == null && !AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath))
                {
                    error = "prefab save failed";
                    return false;
                }

                int embeddedMeshes = SpatialBakePipeline.PersistEphemeralMeshesPublic(
                    combinedRoot, prefabAssetPath, out _);
                if (embeddedMeshes > 0)
                {
                    AssetDatabase.SaveAssets();
                    PrefabUtility.SaveAsPrefabAsset(combinedRoot, prefabAssetPath);
                }
            }
            finally
            {
                if (originalParent != null)
                    combinedRoot.transform.SetParent(originalParent, false);
            }

            return true;
        }
    }
}
