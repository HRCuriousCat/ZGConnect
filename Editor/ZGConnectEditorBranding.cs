using UnityEditor;
using UnityEngine;

namespace ZGConnect.Editor
{
    public static class ZGConnectEditorBranding
    {
        public static readonly Color WindowBackground = new Color(0.0353f, 0.2431f, 0.3216f);

        static readonly string[] LogoPaths =
        {
            "Assets/ZGConnect/Assets/UI/zg_connect_logo editor.jpg",
            "Assets/ZGConnect/Assets/UI/zg_connect_logo UI.jpg",
        };

        static readonly string[] UiLogoPaths =
        {
            "Assets/ZGConnect/Assets/UI/zg_connect_logo UI.jpg",
            "Assets/ZGConnect/Assets/UI/zg_connect_logo editor.jpg",
        };

        static Texture2D _backgroundTexture;
        static GUIStyle _inspectorPanelStyle;

        public static Texture2D LoadLogo() => LoadFirstAvailable(LogoPaths);

        public static Texture2D LoadUiLogo() => LoadFirstAvailable(UiLogoPaths);

        static Texture2D LoadFirstAvailable(string[] paths)
        {
            foreach (string path in paths)
            {
                Texture2D logo = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (logo != null)
                    return logo;
            }

            return null;
        }

        public static void DrawWindowBackground(EditorWindow window)
        {
            EditorGUI.DrawRect(new Rect(0, 0, window.position.width, window.position.height), WindowBackground);
        }

        public static void DrawWindowHeader(Texture2D logo, float viewWidth, string fallbackTitle)
        {
            if (logo != null)
            {
                float aspect = (float)logo.width / Mathf.Max(logo.height, 1);
                float logoHeight = 156f;
                float logoWidth = Mathf.Min(logoHeight * aspect, viewWidth - 16f);

                Rect logoRect = GUILayoutUtility.GetRect(logoWidth, logoHeight, GUILayout.ExpandWidth(true));
                GUI.DrawTexture(logoRect, logo, ScaleMode.ScaleToFit, alphaBlend: true);
                EditorGUILayout.Space(4);
            }
            else
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField(fallbackTitle, EditorStyles.boldLabel);
                EditorGUILayout.Space(6);
            }
        }

        public static GUIStyle InspectorPanelStyle
        {
            get
            {
                if (_inspectorPanelStyle == null)
                {
                    _inspectorPanelStyle = new GUIStyle
                    {
                        normal = { background = GetBackgroundTexture() },
                        border = new RectOffset(0, 0, 0, 0),
                        padding = new RectOffset(6, 6, 4, 8),
                        margin = new RectOffset(-18, -4, 0, 0),
                    };
                }

                return _inspectorPanelStyle;
            }
        }

        public static void BeginInspectorPanel() =>
            EditorGUILayout.BeginVertical(InspectorPanelStyle);

        public static void EndInspectorPanel() =>
            EditorGUILayout.EndVertical();

        static Texture2D GetBackgroundTexture()
        {
            if (_backgroundTexture == null)
            {
                _backgroundTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                _backgroundTexture.SetPixel(0, 0, WindowBackground);
                _backgroundTexture.Apply();
            }

            return _backgroundTexture;
        }

        public static void DrawInspectorHeader(
            Texture2D logo,
            string fallbackTitle,
            bool drawHeaderBackground = true)
        {
            const float headerHeight = 132f;
            Rect headerRect = GUILayoutUtility.GetRect(0f, headerHeight, GUILayout.ExpandWidth(true));
            if (drawHeaderBackground)
                EditorGUI.DrawRect(headerRect, WindowBackground);

            if (logo != null)
            {
                float aspect = (float)logo.width / Mathf.Max(logo.height, 1);
                float logoHeight = headerHeight - 12f;
                float logoWidth = Mathf.Min(logoHeight * aspect, headerRect.width - 16f);
                float x = headerRect.x + (headerRect.width - logoWidth) * 0.5f;
                float y = headerRect.y + (headerRect.height - logoHeight) * 0.5f;
                GUI.DrawTexture(
                    new Rect(x, y, logoWidth, logoHeight),
                    logo,
                    ScaleMode.ScaleToFit,
                    alphaBlend: true);
            }
            else
            {
                var titleStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = Color.white },
                };
                GUI.Label(headerRect, fallbackTitle, titleStyle);
            }

            EditorGUILayout.Space(4);
        }
    }
}
