using UnityEditor;
using UnityEngine;

namespace ZGConnect.PhotogrammetryStreaming.Editor
{
    [InitializeOnLoad]
    static class PhotogrammetryDefaultAssetsBootstrap
    {
        const string SettingsDir = "Assets/ZGConnect/PhotogrammetryStreaming/Settings";

        static PhotogrammetryDefaultAssetsBootstrap()
        {
            EditorApplication.delayCall += EnsureDefaultAssets;
        }

        static void EnsureDefaultAssets()
        {
            if (Application.isPlaying)
                return;

            if (!AssetDatabase.IsValidFolder("Assets/ZGConnect/PhotogrammetryStreaming"))
                return;

            if (!AssetDatabase.IsValidFolder(SettingsDir))
                AssetDatabase.CreateFolder("Assets/ZGConnect/PhotogrammetryStreaming", "Settings");

            LoadOrCreate<PhotogrammetryApiSettings>($"{SettingsDir}/PhotogrammetryApiSettings.asset");
            var region = LoadOrCreate<PhotogrammetryRegionPreset>($"{SettingsDir}/PhotogrammetryRegion_Zagreb.asset");
            if (region.kind != PhotogrammetryRegionKind.ZagrebCityBounds)
            {
                region.kind = PhotogrammetryRegionKind.ZagrebCityBounds;
                EditorUtility.SetDirty(region);
            }

            LoadOrCreate<PhotogrammetryLodProfile>($"{SettingsDir}/PhotogrammetryLodProfile_Default.asset");

            const string scenePath = "Assets/ZGConnect/PhotogrammetryStreaming/Scenes/Photogrammetry_Zagreb_Streaming.unity";
            if (!System.IO.File.Exists(scenePath))
                PhotogrammetrySceneBuilder.CreateOrUpdateScene();

            AssetDatabase.SaveAssets();
        }

        static T LoadOrCreate<T>(string path) where T : ScriptableObject
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null)
                return existing;
            var so = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(so, path);
            return so;
        }
    }
}
