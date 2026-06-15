using UnityEditor;
using UnityEngine;

namespace ZGConnect.Editor
{
    public static class BuildingSurfaceReprocessMenu
    {
        [MenuItem("ZG Connect/Buildings/Reprocess Surfaces On Selected Prefabs")]
        public static void ReprocessSelected()
        {
            BuildingSurfaceSettings settings = GetSettings();
            if (settings == null)
            {
                EditorUtility.DisplayDialog(
                    "ZG Connect",
                    "No BuildingSurfaceSettings asset found.\n\n" +
                    "Use ZG Connect → Buildings → Create Default Building Surface Settings first.",
                    "OK");
                return;
            }

            int count = 0;
            foreach (Object obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab"))
                    continue;

                GameObject root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    bool any = false;
                    foreach (Transform child in root.transform)
                    {
                        BuildingData bd = child.GetComponent<BuildingData>();
                        string id = bd != null ? bd.buildingId : child.name;
                        BuildingSurfaceMeshProcessor.ProcessBuilding(child.gameObject, settings, id);
                        any = true;
                    }

                    if (any)
                    {
                        PrefabUtility.SaveAsPrefabAsset(root, path);
                        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                        BuildingSurfaceMeshProcessor.PersistEphemeralMeshes(root, path);
                        AssetDatabase.SaveAssets();
                        PrefabUtility.SaveAsPrefabAsset(root, path);
                        count++;
                    }
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }

            Debug.Log($"[ZGConnect] Reprocessed building surfaces on {count} prefab(s).");
        }

        [MenuItem("ZG Connect/Buildings/Reprocess Surfaces On Selected Prefabs", true)]
        public static bool ReprocessSelectedValidate() => Selection.objects.Length > 0;

        [MenuItem("ZG Connect/Buildings/Reprocess Roof Orthophoto On Selected Prefabs")]
        public static void ReprocessRoofOrthoSelected()
        {
            BuildingSurfaceSettings settings = GetSettings();
            if (settings == null)
            {
                EditorUtility.DisplayDialog(
                    "ZG Connect",
                    "No BuildingSurfaceSettings asset found.",
                    "OK");
                return;
            }

            CityDataset dataset = GetCityDataset();
            if (dataset == null)
            {
                EditorUtility.DisplayDialog(
                    "ZG Connect",
                    "No CityDataset asset found in project.",
                    "OK");
                return;
            }

            var importSettings = new BuildingsImportSettings
            {
                OutputFolder = EditorPrefs.GetString(
                    "ZGConnect.Importer.OutputFolder", "Assets/Generated/ZGConnect"),
                ProcessRoofOrthophotoUv = true,
                RoofOrthophotoBasemapId = "ortho",
            };

            int count = 0;
            foreach (Object obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab"))
                    continue;

                GameObject root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    BuildingData anyBd = root.GetComponentInChildren<BuildingData>(true);
                    string tileId = anyBd != null ? anyBd.tileId : null;
                    if (string.IsNullOrEmpty(tileId))
                    {
                        string n = root.name;
                        const string prefix = "TileBuildings_";
                        if (n.StartsWith(prefix))
                            tileId = n.Substring(prefix.Length);
                    }

                    CityTileRecord rec = null;
                    if (!string.IsNullOrEmpty(tileId))
                    {
                        foreach (CityTileRecord t in dataset.tiles)
                        {
                            if (t.tileId == tileId)
                            {
                                rec = t;
                                break;
                            }
                        }
                    }

                    if (rec == null)
                    {
                        Debug.LogWarning($"[ZGConnect] Roof ortho reprocess: no tile record for '{path}'.");
                        continue;
                    }

                    if (!BuildingRoofOrthophotoResolver.TryEnsureTileRoofMaterial(
                            rec, dataset, importSettings, out Material roofMat))
                        continue;

                    BuildingMeshProcessingUtility.MakeInstanceMeshesReadable(root);
                    BuildingRoofOrthophotoProcessor.ProcessTile(root, settings, roofMat, rec, dataset);

                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    BuildingSurfaceMeshProcessor.PersistEphemeralMeshes(root, path);
                    AssetDatabase.SaveAssets();
                    count++;
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }

            Debug.Log($"[ZGConnect] Reprocessed roof orthophoto on {count} prefab(s).");
        }

        [MenuItem("ZG Connect/Buildings/Reprocess Roof Orthophoto On Selected Prefabs", true)]
        public static bool ReprocessRoofOrthoSelectedValidate() => Selection.objects.Length > 0;

        private static CityDataset GetCityDataset()
        {
            string[] guids = AssetDatabase.FindAssets("t:CityDataset");
            if (guids.Length == 0) return null;
            return AssetDatabase.LoadAssetAtPath<CityDataset>(
                AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        private static BuildingSurfaceSettings GetSettings()
        {
            string[] guids = AssetDatabase.FindAssets("t:BuildingSurfaceSettings");
            if (guids.Length == 0) return null;
            return AssetDatabase.LoadAssetAtPath<BuildingSurfaceSettings>(
                AssetDatabase.GUIDToAssetPath(guids[0]));
        }
    }
}
