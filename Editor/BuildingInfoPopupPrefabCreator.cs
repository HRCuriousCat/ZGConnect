#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace ZGConnect.Editor
{
    public static class BuildingInfoPopupPrefabCreator
    {
        const string PrefabPath = "Assets/ZGConnect/Prefabs/UI/BuildingInfoPopup.prefab";

        [MenuItem("ZG Connect/Create Building Info Popup Prefab")]
        public static void CreatePrefab()
        {
            var tempRoot = new GameObject("BuildingInfoPopupTemplate");
            try
            {
                BuildingInfoPopup template = BuildingInfoPopup.CreateTemplate(tempRoot.transform);
                template.gameObject.SetActive(true);

                string directory = System.IO.Path.GetDirectoryName(PrefabPath);
                if (!string.IsNullOrEmpty(directory) && !AssetDatabase.IsValidFolder(directory))
                {
                    if (!AssetDatabase.IsValidFolder("Assets/ZGConnect/Prefabs"))
                        AssetDatabase.CreateFolder("Assets/ZGConnect", "Prefabs");
                    if (!AssetDatabase.IsValidFolder("Assets/ZGConnect/Prefabs/UI"))
                        AssetDatabase.CreateFolder("Assets/ZGConnect/Prefabs", "UI");
                }

                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(template.gameObject, PrefabPath);
                Object.DestroyImmediate(tempRoot);
                Selection.activeObject = prefab;
                Debug.Log($"[ZGConnect] Created building info popup prefab at {PrefabPath}");
            }
            finally
            {
                if (tempRoot != null)
                    Object.DestroyImmediate(tempRoot);
            }
        }
    }
}
#endif

