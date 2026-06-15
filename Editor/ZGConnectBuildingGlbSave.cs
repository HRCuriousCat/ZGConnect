using System.IO;
using UnityEditor;
using UnityEngine;

namespace ZGConnect.Editor
{
    /// <summary>
    /// Exports a TileBuildings hierarchy (with <see cref="BuildingRoofUvRotation"/> edits) back to GLB on disk.
    /// </summary>
    public static class ZGConnectBuildingGlbSave
    {
        private const string kDatasetRootPref = "ZGConnect.Importer.RootFolder";

        public static GameObject FindTileBuildingsRoot(Transform from)
        {
            if (from == null) return null;

            Transform t = from;
            while (t != null)
            {
                if (t.name.StartsWith("TileBuildings_"))
                    return t.gameObject;
                t = t.parent;
            }

            BuildingData bd = from.GetComponentInParent<BuildingData>();
            if (bd != null && !string.IsNullOrEmpty(bd.tileId))
            {
                string expected = $"TileBuildings_{bd.tileId}";
                foreach (Transform root in from.root.GetComponentsInChildren<Transform>(true))
                {
                    if (root.name == expected)
                        return root.gameObject;
                }
            }

            return null;
        }

        public static void ApplyAllRoofUvEdits(GameObject tileRoot)
        {
            if (tileRoot == null) return;

            foreach (BuildingRoofUvRotation rot in tileRoot.GetComponentsInChildren<BuildingRoofUvRotation>(true))
                rot.ApplyRoofUvs();
        }

        /// <summary>Resolves tile id from <see cref="BuildingData"/> or parent <c>TileBuildings_*</c> name.</summary>
        public static string ResolveTileId(Transform from)
        {
            if (from == null) return null;

            BuildingData bd = from.GetComponentInParent<BuildingData>();
            if (bd != null && !string.IsNullOrEmpty(bd.tileId))
                return bd.tileId;

            GameObject tileRoot = FindTileBuildingsRoot(from);
            if (tileRoot != null && tileRoot.name.StartsWith("TileBuildings_"))
                return tileRoot.name.Substring("TileBuildings_".Length);

            return null;
        }

        public static bool TryGetDefaultProcessedGlbPath(
            string tileId, out string path, out string error)
        {
            path = null;
            error = null;

            if (string.IsNullOrEmpty(tileId))
            {
                error = "Could not determine tile id. Add BuildingData with tileId, or parent named TileBuildings_{tileId}.";
                return false;
            }

            string root = EditorPrefs.GetString(kDatasetRootPref, "").Trim();
            if (string.IsNullOrEmpty(root))
            {
                error = "Dataset Root Folder is empty in EditorPrefs. Open Dataset Import Manager, set the folder " +
                        "(the path is saved when you change it or close the window).";
                return false;
            }

            if (!Directory.Exists(root))
            {
                error = $"Dataset Root Folder does not exist on disk:\n{root}";
                return false;
            }

            string folder = BuildingSurfacePipeline.GetProcessedFolder(
                Path.Combine(root, "building_meshes"));
            path = Path.Combine(folder, $"buildings_{tileId}.glb");
            return true;
        }

        public static string TryGetDefaultProcessedGlbPath(string tileId)
        {
            TryGetDefaultProcessedGlbPath(tileId, out string path, out _);
            return path;
        }

        public static bool TryGetDefaultRawGlbPath(string tileId, out string path, out string error)
        {
            path = null;
            error = null;

            if (string.IsNullOrEmpty(tileId))
            {
                error = "Could not determine tile id. Add BuildingData with tileId, or parent named TileBuildings_{tileId}.";
                return false;
            }

            string root = EditorPrefs.GetString(kDatasetRootPref, "").Trim();
            if (string.IsNullOrEmpty(root))
            {
                error = "Dataset Root Folder is not set. Configure it in Dataset Import Manager.";
                return false;
            }

            if (!Directory.Exists(root))
            {
                error = $"Dataset Root Folder does not exist on disk:\n{root}";
                return false;
            }

            path = Path.Combine(root, "building_meshes", $"buildings_{tileId}.glb");
            return true;
        }

        public static string TryGetDefaultRawGlbPath(string tileId)
        {
            TryGetDefaultRawGlbPath(tileId, out string path, out _);
            return path;
        }

        /// <param name="geometryOnly">When true, exports mesh/UV only (material slot keys written to JSON).</param>
        public static bool ExportTileToGlb(
            GameObject tileRoot,
            string     glbOsPath,
            bool       writeJson = true,
            bool       geometryOnly = true)
        {
            if (tileRoot == null)
            {
                Debug.LogError("[ZGConnect] Export GLB: tile root is null.");
                return false;
            }

            if (!ZGConnectGlbExportUtility.IsAvailable)
            {
                EditorUtility.DisplayDialog(
                    "ZG Connect — Export GLB",
                    "UnityGLTF is not installed (com.khronos.unitygltf).",
                    "OK");
                return false;
            }

            ApplyAllRoofUvEdits(tileRoot);

            try
            {
                string dir = Path.GetDirectoryName(glbOsPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                if (geometryOnly)
                    ZGConnectGlbExportUtility.ExportRootGeometryOnly(tileRoot, glbOsPath);
                else
                ZGConnectGlbExportUtility.ExportRoot(tileRoot, glbOsPath);

                if (writeJson)
                    BuildingTileMetadataWriter.WriteJson(tileRoot, glbOsPath);

                Debug.Log($"[ZGConnect] Exported tile GLB: {glbOsPath}");
                return true;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ZGConnect] Export GLB failed: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        public static bool ExportFromBuildingRoofUvComponent(
            BuildingRoofUvRotation component,
            string                   glbOsPath,
            bool                     geometryOnly = true)
        {
            GameObject tileRoot = FindTileBuildingsRoot(component.transform);
            if (tileRoot == null)
            {
                Debug.LogError(
                    "[ZGConnect] Could not find TileBuildings_* root. Export from the tile prefab root instead.");
                return false;
            }

            return ExportTileToGlb(tileRoot, glbOsPath, writeJson: true, geometryOnly: geometryOnly);
        }

        /// <summary>Embeds edited meshes into the nearest prefab asset so UVs survive without GLB export.</summary>
        public static void BakeUvMeshesIntoPrefab(GameObject tileRoot)
        {
            if (tileRoot == null) return;

            string prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(tileRoot);
            if (string.IsNullOrEmpty(prefabPath))
            {
                Debug.LogWarning("[ZGConnect] Not part of a prefab instance — save GLB or save as prefab manually.");
                return;
            }

            ApplyAllRoofUvEdits(tileRoot);

            PrefabUtility.SaveAsPrefabAsset(
                PrefabUtility.GetNearestPrefabInstanceRoot(tileRoot) ?? tileRoot, prefabPath);
            AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceUpdate);
            BuildingSurfaceMeshProcessor.PersistEphemeralMeshes(tileRoot, prefabPath);
            AssetDatabase.SaveAssets();

            GameObject instanceRoot = PrefabUtility.GetNearestPrefabInstanceRoot(tileRoot);
            if (instanceRoot == null)
            {
                Debug.LogWarning("[ZGConnect] No prefab instance root found.");
                return;
            }

            PrefabUtility.SavePrefabAsset(instanceRoot);
            AssetDatabase.SaveAssets();
            Debug.Log($"[ZGConnect] Baked roof UV meshes into prefab: {prefabPath}");
        }
    }
}
