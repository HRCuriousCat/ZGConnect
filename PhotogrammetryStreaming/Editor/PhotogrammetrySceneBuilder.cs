using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ZGConnect.PhotogrammetryStreaming.Editor
{
    public static class PhotogrammetrySceneBuilder
    {
        const string ScenePath = "Assets/ZGConnect/PhotogrammetryStreaming/Scenes/Photogrammetry_Zagreb_Streaming.unity";
        const string SettingsDir = "Assets/ZGConnect/PhotogrammetryStreaming/Settings";

        [MenuItem("ZG Connect/Photogrammetry/Create Zagreb Streaming Scene")]
        public static void CreateOrUpdateScene()
        {
            EnsureFolders();

            var api = LoadOrCreate<PhotogrammetryApiSettings>($"{SettingsDir}/PhotogrammetryApiSettings.asset");
            var region = LoadOrCreate<PhotogrammetryRegionPreset>($"{SettingsDir}/PhotogrammetryRegion_Zagreb.asset");
            var lod = LoadOrCreate<PhotogrammetryLodProfile>($"{SettingsDir}/PhotogrammetryLodProfile_Default.asset");

            Scene scene;
            if (System.IO.File.Exists(ScenePath))
            {
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }
            else
            {
                scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            }

            var streamerGo = GameObject.Find("PhotogrammetryStreamer");
            if (streamerGo == null)
                streamerGo = new GameObject("PhotogrammetryStreamer");

            var controller = streamerGo.GetComponent<PhotogrammetryStreamingController>();
            if (controller == null)
                controller = streamerGo.AddComponent<PhotogrammetryStreamingController>();

            var attribution = streamerGo.GetComponent<PhotogrammetryAttributionUI>();
            if (attribution == null)
                attribution = streamerGo.AddComponent<PhotogrammetryAttributionUI>();

            var cam = Camera.main;

            var so = new SerializedObject(controller);
            so.FindProperty("_apiSettings").objectReferenceValue = api;
            so.FindProperty("_regionPreset").objectReferenceValue = region;
            so.FindProperty("_lodProfile").objectReferenceValue = lod;
            so.FindProperty("_attributionUi").objectReferenceValue = attribution;
            if (cam != null)
                so.FindProperty("_targetCamera").objectReferenceValue = cam;
            so.ApplyModifiedPropertiesWithoutUndo();

            if (cam != null)
            {
                cam.transform.position = new Vector3(0f, 200f, -300f);
                cam.transform.rotation = Quaternion.Euler(25f, 0f, 0f);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Photogrammetry] Scene saved: {ScenePath}");
        }

        static void EnsureFolders()
        {
            if (!AssetDatabase.IsValidFolder("Assets/ZGConnect/PhotogrammetryStreaming/Scenes"))
                AssetDatabase.CreateFolder("Assets/ZGConnect/PhotogrammetryStreaming", "Scenes");
            if (!AssetDatabase.IsValidFolder(SettingsDir))
                AssetDatabase.CreateFolder("Assets/ZGConnect/PhotogrammetryStreaming", "Settings");
        }

        static T LoadOrCreate<T>(string path) where T : ScriptableObject
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null)
                return existing;
            var asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }
    }
}
