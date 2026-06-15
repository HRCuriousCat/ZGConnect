using UnityEngine;

namespace ZGConnect.PhotogrammetryStreaming
{
    /// <summary>Loads default ScriptableObject settings when scene references are missing.</summary>
    public static class PhotogrammetryDefaults
    {
        public const string SettingsFolder = "Assets/ZGConnect/PhotogrammetryStreaming/Settings/";

        public static PhotogrammetryApiSettings LoadApiSettings() =>
            LoadAsset<PhotogrammetryApiSettings>("PhotogrammetryApiSettings.asset");

        public static PhotogrammetryRegionPreset LoadRegionPreset() =>
            LoadAsset<PhotogrammetryRegionPreset>("PhotogrammetryRegion_Zagreb.asset");

        public static PhotogrammetryLodProfile LoadLodProfile() =>
            LoadAsset<PhotogrammetryLodProfile>("PhotogrammetryLodProfile_Default.asset");

        static T LoadAsset<T>(string fileName) where T : ScriptableObject
        {
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<T>(SettingsFolder + fileName);
#else
            string resourceName = "Photogrammetry/" + System.IO.Path.GetFileNameWithoutExtension(fileName);
            return Resources.Load<T>(resourceName);
#endif
        }
    }
}
