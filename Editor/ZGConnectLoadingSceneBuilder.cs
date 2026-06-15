using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

namespace ZGConnect.Editor
{
    /// <summary>
    /// Builds <c>Assets/ZGConnect/Scenes/LoadingScene.unity</c> with title + progress bar UI.
    /// </summary>
    public static class ZGConnectLoadingSceneBuilder
    {
        public const string LoadingScenePath = "Assets/ZGConnect/Scenes/LoadingScene.unity";
        public const string DefaultTargetScenePath = "Assets/ZGConnect/Scenes/Runtime_Streaming.unity";

        [MenuItem("ZG Connect/Scenes/Create Or Update Loading Scene")]
        public static void CreateOrUpdateLoadingScene()
        {
            EnsureFolder("Assets/ZGConnect/Scenes");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            CreateEventSystem();
            RectTransform canvasRt = CreateCanvas();
            Slider progressSlider = CreateLoadingUi(canvasRt, out Text titleText, out Text percentText);
            SceneLoadingController loader = CreateLoader(progressSlider, titleText, percentText);

            string targetPath = DefaultTargetScenePath;
            if (!File.Exists(targetPath))
            {
                string[] candidates = { "Assets/Scenes/SampleScene.unity", "Assets/synched.unity" };
                foreach (string c in candidates)
                {
                    if (File.Exists(c)) { targetPath = c; break; }
                }
            }

            ConfigureLoaderTarget(loader, targetPath);
            ConfigureLoaderPresentation(loader, "ZG Connect");

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, LoadingScenePath);
            UpdateBuildSettings(LoadingScenePath, targetPath);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log(
                $"[ZGConnect] Loading scene saved to {LoadingScenePath}. " +
                $"Build order: 0 = loading, 1 = {targetPath}.");
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            string leaf = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        private static void CreateEventSystem()
        {
            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
            go.AddComponent<InputSystemUIInputModule>();
#else
            go.AddComponent<StandaloneInputModule>();
#endif
        }

        private static RectTransform CreateCanvas()
        {
            var canvasGo = new GameObject("Canvas");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 0;

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            canvasGo.AddComponent<GraphicRaycaster>();
            return canvasGo.GetComponent<RectTransform>();
        }

        private static Slider CreateLoadingUi(RectTransform canvas, out Text titleText, out Text percentText)
        {
            Sprite uiSprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");

            var bg = CreatePanel(canvas, "Background", new Color(0.07f, 0.08f, 0.10f, 1f), uiSprite);
            StretchFull(bg.GetComponent<RectTransform>());

            Texture2D logo = ZGConnectEditorBranding.LoadUiLogo();
            if (logo != null)
            {
                var logoGo = CreatePanel(canvas, "Logo", Color.white, uiSprite);
                var logoRt = logoGo.GetComponent<RectTransform>();
                logoRt.anchorMin = new Vector2(0.5f, 0.62f);
                logoRt.anchorMax = new Vector2(0.5f, 0.62f);
                logoRt.pivot = new Vector2(0.5f, 0.5f);
                logoRt.sizeDelta = new Vector2(420f, 140f);
                logoRt.anchoredPosition = Vector2.zero;
                var logoImg = logoGo.GetComponent<Image>();
                logoImg.sprite = Sprite.Create(
                    logo, new Rect(0, 0, logo.width, logo.height), new Vector2(0.5f, 0.5f), 100f);
                logoImg.preserveAspect = true;
                logoImg.color = Color.white;
            }

            var titleGo = CreateText(canvas, "Title", "ZG Connect", 64, FontStyle.Bold);
            titleText = titleGo.GetComponent<Text>();
            var titleRt = titleGo.GetComponent<RectTransform>();
            titleRt.anchorMin = new Vector2(0.5f, 0.48f);
            titleRt.anchorMax = new Vector2(0.5f, 0.48f);
            titleRt.pivot = new Vector2(0.5f, 0.5f);
            titleRt.sizeDelta = new Vector2(1200f, 100f);
            titleRt.anchoredPosition = Vector2.zero;
            titleText.alignment = TextAnchor.MiddleCenter;
            titleText.color = new Color(0.95f, 0.96f, 0.98f, 1f);

            Slider progressSlider = CreateProgressSlider(
                canvas,
                new Color(0.15f, 0.17f, 0.20f, 1f),
                new Color(0.25f, 0.72f, 0.55f, 1f),
                uiSprite,
                new Vector2(0.5f, 0.32f),
                new Vector2(720f, 14f));

            var pctGo = CreateText(canvas, "ProgressPercent", "0%", 22, FontStyle.Normal);
            percentText = pctGo.GetComponent<Text>();
            var pctRt = pctGo.GetComponent<RectTransform>();
            pctRt.anchorMin = new Vector2(0.5f, 0.26f);
            pctRt.anchorMax = new Vector2(0.5f, 0.26f);
            pctRt.pivot = new Vector2(0.5f, 0.5f);
            pctRt.sizeDelta = new Vector2(200f, 40f);
            pctRt.anchoredPosition = Vector2.zero;
            percentText.alignment = TextAnchor.MiddleCenter;
            percentText.color = new Color(0.65f, 0.70f, 0.75f, 1f);

            return progressSlider;
        }

        private static Slider CreateProgressSlider(
            Transform parent,
            Color backgroundColor,
            Color fillColor,
            Sprite sprite,
            Vector2 anchorY,
            Vector2 size)
        {
            var sliderGo = new GameObject("ProgressSlider", typeof(RectTransform), typeof(Slider));
            sliderGo.transform.SetParent(parent, false);

            var sliderRt = sliderGo.GetComponent<RectTransform>();
            sliderRt.anchorMin = anchorY;
            sliderRt.anchorMax = anchorY;
            sliderRt.pivot = new Vector2(0.5f, 0.5f);
            sliderRt.sizeDelta = size;
            sliderRt.anchoredPosition = Vector2.zero;

            var bgGo = CreatePanel(sliderGo.transform, "Background", backgroundColor, sprite);
            StretchFull(bgGo.GetComponent<RectTransform>());

            var fillAreaGo = new GameObject("Fill Area", typeof(RectTransform));
            fillAreaGo.transform.SetParent(sliderGo.transform, false);
            StretchFull(fillAreaGo.GetComponent<RectTransform>());

            var fillGo = CreatePanel(fillAreaGo.transform, "Fill", fillColor, sprite);
            StretchFull(fillGo.GetComponent<RectTransform>());

            Slider slider = sliderGo.GetComponent<Slider>();
            slider.fillRect = fillGo.GetComponent<RectTransform>();
            slider.targetGraphic = fillGo.GetComponent<Image>();
            slider.handleRect = null;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = 0f;
            slider.interactable = false;
            return slider;
        }

        private static SceneLoadingController CreateLoader(
            Slider progressSlider, Text titleText, Text percentText)
        {
            var loaderGo = new GameObject("Scene Loader");
            var loader = loaderGo.AddComponent<SceneLoadingController>();

            var so = new SerializedObject(loader);
            so.FindProperty("progressSlider").objectReferenceValue = progressSlider;
            so.FindProperty("titleText").objectReferenceValue = titleText;
            so.FindProperty("progressPercentText").objectReferenceValue = percentText;
            so.ApplyModifiedPropertiesWithoutUndo();
            return loader;
        }

        private static void ConfigureLoaderTarget(SceneLoadingController loader, string targetScenePath)
        {
            string sceneName = Path.GetFileNameWithoutExtension(targetScenePath);
            var so = new SerializedObject(loader);
            so.FindProperty("nextSceneName").stringValue = sceneName;
            so.FindProperty("loadByBuildIndex").boolValue = false;
            so.FindProperty("additiveLoadWithStreamingProgress").boolValue = true;
            so.FindProperty("sceneLoadProgressWeight").floatValue = 0.15f;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureLoaderPresentation(SceneLoadingController loader, string title)
        {
            var so = new SerializedObject(loader);
            so.FindProperty("projectTitle").stringValue = title;
            so.FindProperty("minDisplaySeconds").floatValue = 0.75f;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void UpdateBuildSettings(string loadingScenePath, string targetScenePath)
        {
            var list = new List<EditorBuildSettingsScene>
            {
                new EditorBuildSettingsScene(loadingScenePath, true),
                new EditorBuildSettingsScene(targetScenePath, true)
            };

            foreach (EditorBuildSettingsScene existing in EditorBuildSettings.scenes)
            {
                if (existing.path == loadingScenePath || existing.path == targetScenePath)
                    continue;
                list.Add(existing);
            }

            EditorBuildSettings.scenes = list.ToArray();
        }

        private static GameObject CreatePanel(Transform parent, string name, Color color, Sprite sprite)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.sprite = sprite;
            img.type = Image.Type.Sliced;
            img.color = color;
            return go;
        }

        private static GameObject CreateText(
            Transform parent, string name, string text, int fontSize, FontStyle style)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.text = text;
            t.fontSize = fontSize;
            t.fontStyle = style;
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.supportRichText = false;
            return go;
        }

        private static void StretchFull(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}

