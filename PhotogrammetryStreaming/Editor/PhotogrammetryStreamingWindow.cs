using UnityEditor;
using UnityEngine;

namespace ZGConnect.PhotogrammetryStreaming.Editor
{
    public class PhotogrammetryStreamingWindow : EditorWindow
    {
        int _tab;
        string _apiKey = "";
        string _verifyLog = "";
        Vector2 _scroll;

        PhotogrammetryApiSettings _apiSettings;
        PhotogrammetryRegionPreset _regionPreset;
        PhotogrammetryLodProfile _lodProfile;

        [MenuItem("ZG Connect/Photogrammetry/Streaming Settings")]
        public static void ShowWindow() => Open(0);

        public static void Open(int selectTab = 0)
        {
            var w = GetWindow<PhotogrammetryStreamingWindow>("Photogrammetry Streaming");
            w._tab = selectTab;
            w._apiKey = PhotogrammetryApiVerifier.GetSavedApiKey();
            w.Show();
        }

        void OnGUI()
        {
            _tab = GUILayout.Toolbar(_tab, new[] { "API Verify", "Assets", "Scene" });
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            switch (_tab)
            {
                case 0: DrawApiTab(); break;
                case 1: DrawAssetsTab(); break;
                case 2: DrawSceneTab(); break;
            }

            EditorGUILayout.EndScrollView();
        }

        void DrawApiTab()
        {
            EditorGUILayout.LabelField("Step 0 — Google Map Tiles API verification", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "GET https://tile.googleapis.com/v1/3dtiles/root.json?key=YOUR_KEY\n\n" +
                "200 + tileset JSON → live streaming OK\n" +
                "403 + EEA message → Croatia projects may be blocked (Jul 2025+)\n" +
                "401/400 → fix API key, enable Map Tiles API, billing",
                MessageType.Info);

            _apiKey = EditorGUILayout.PasswordField("API Key", _apiKey);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Verify API"))
            {
                PhotogrammetryApiVerifier.SaveApiKey(_apiKey);
                _verifyLog = "Verifying...";
                PhotogrammetryApiVerifier.RunVerification(_apiKey, r =>
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"HTTP {r.HttpCode}");
                    if (!string.IsNullOrEmpty(r.Transport))
                        sb.AppendLine($"Transport: {r.Transport}");
                    if (!string.IsNullOrEmpty(r.RequestUrlMasked))
                        sb.AppendLine($"URL: {r.RequestUrlMasked}");
                    sb.AppendLine();
                    sb.AppendLine(r.Summary);
                    if (!string.IsNullOrEmpty(r.BodyPreview))
                    {
                        sb.AppendLine();
                        sb.AppendLine("Body preview:");
                        sb.AppendLine(r.BodyPreview);
                    }
                    _verifyLog = sb.ToString();
                    Debug.Log("[Photogrammetry] API verify:\n" + _verifyLog);
                    Repaint();
                });
            }

            if (GUILayout.Button("Apply key to settings asset"))
            {
                EnsureDefaultAssets();
                if (_apiSettings != null)
                {
                    Undo.RecordObject(_apiSettings, "Set API Key");
                    _apiSettings.apiKey = _apiKey;
                    EditorUtility.SetDirty(_apiSettings);
                    PhotogrammetryApiVerifier.SaveApiKey(_apiKey);
                }
            }
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_verifyLog))
                EditorGUILayout.TextArea(_verifyLog, GUILayout.MinHeight(120));
        }

        void DrawAssetsTab()
        {
            EditorGUILayout.LabelField("ScriptableObject assets", EditorStyles.boldLabel);
            _apiSettings = (PhotogrammetryApiSettings)EditorGUILayout.ObjectField("API Settings", _apiSettings, typeof(PhotogrammetryApiSettings), false);
            _regionPreset = (PhotogrammetryRegionPreset)EditorGUILayout.ObjectField("Region Preset", _regionPreset, typeof(PhotogrammetryRegionPreset), false);
            _lodProfile = (PhotogrammetryLodProfile)EditorGUILayout.ObjectField("LOD Profile", _lodProfile, typeof(PhotogrammetryLodProfile), false);

            if (GUILayout.Button("Create default assets (if missing)"))
                EnsureDefaultAssets();

            EditorGUILayout.HelpBox(
                "Store API keys only in local settings assets. Do not commit real keys to git.",
                MessageType.Warning);
        }

        void DrawSceneTab()
        {
            EditorGUILayout.LabelField("Scene setup", EditorStyles.boldLabel);
            if (GUILayout.Button("Create / Update Photogrammetry_Zagreb_Streaming scene"))
                PhotogrammetrySceneBuilder.CreateOrUpdateScene();
        }

        void EnsureDefaultAssets()
        {
            const string dir = "Assets/ZGConnect/PhotogrammetryStreaming/Settings";
            if (!AssetDatabase.IsValidFolder("Assets/ZGConnect/PhotogrammetryStreaming/Settings"))
            {
                AssetDatabase.CreateFolder("Assets/ZGConnect/PhotogrammetryStreaming", "Settings");
            }

            _apiSettings = _apiSettings ?? LoadOrCreate<PhotogrammetryApiSettings>($"{dir}/PhotogrammetryApiSettings.asset");
            _regionPreset = _regionPreset ?? LoadOrCreate<PhotogrammetryRegionPreset>($"{dir}/PhotogrammetryRegion_Zagreb.asset");
            _lodProfile = _lodProfile ?? LoadOrCreate<PhotogrammetryLodProfile>($"{dir}/PhotogrammetryLodProfile_Default.asset");

            if (_regionPreset != null && _regionPreset.kind == PhotogrammetryRegionKind.ZagrebCityBounds)
            {
                EditorUtility.SetDirty(_regionPreset);
            }
        }

        static T LoadOrCreate<T>(string path) where T : ScriptableObject
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null)
                return existing;
            var so = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(so, path);
            AssetDatabase.SaveAssets();
            return so;
        }
    }
}
