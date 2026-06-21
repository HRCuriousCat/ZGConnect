using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.UI;
#endif

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// Runtime Canvas HUD for <see cref="SpatialStreamingController"/>.
    /// Created automatically when <c>_showDebugHud</c> is enabled on the controller.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SpatialStreamingDebugHud : MonoBehaviour
    {
        [SerializeField] SpatialStreamingController _controller;
        [SerializeField] bool _visible = true;
        [SerializeField] KeyCode _toggleKey = KeyCode.F3;
        [SerializeField] Vector2 _margin = new(12f, 12f);
        [SerializeField] float _width = 430f;
        [SerializeField] float _scale = 1f;

        Canvas _canvas;
        RectTransform _panelRt;
        Text _statsText;
        float _appliedScale = float.NaN;

        public void Initialize(SpatialStreamingController controller)
        {
            _controller = controller;
            if (_canvas == null)
                BuildUi();
            ApplySettings(_scale);
            SetVisible(_visible);
        }

        public void ApplySettings(float scale)
        {
            _scale = Mathf.Max(0.25f, scale);
            if (_panelRt == null)
                return;

            if (Mathf.Approximately(_appliedScale, _scale))
                return;

            _appliedScale = _scale;
            _panelRt.localScale = Vector3.one * _scale;
        }

        public void SetVisible(bool visible)
        {
            _visible = visible;
            if (_canvas != null)
                _canvas.gameObject.SetActive(visible);
        }

        void Awake()
        {
            if (_controller == null)
                _controller = GetComponent<SpatialStreamingController>();

            if (_controller != null && _canvas == null)
                Initialize(_controller);
        }

        void Update()
        {
            if (WasTogglePressed())
                SetVisible(!_visible);
        }

        void LateUpdate()
        {
            if (!_visible || _controller == null || _statsText == null)
                return;

            if (_controller.Manifest == null)
            {
                _statsText.text = "Spatial Streaming\n(manifest not loaded)";
                return;
            }

            IReadOnlyList<string> lines = _controller.BuildRuntimeHudLines();
            _statsText.text = "Spatial Streaming\n" + string.Join("\n", lines);
        }

        bool WasTogglePressed()
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null || !TryMapToggleKey(_toggleKey, out Key key))
                return false;

            KeyControl control = keyboard[key];
            return control != null && control.wasPressedThisFrame;
#else
            return Input.GetKeyDown(_toggleKey);
#endif
        }

#if ENABLE_INPUT_SYSTEM
        static bool TryMapToggleKey(KeyCode keyCode, out Key key)
        {
            return System.Enum.TryParse(keyCode.ToString(), ignoreCase: true, out key);
        }
#endif

        static Sprite _whiteSprite;

        static Sprite GetUiSprite()
        {
            if (_whiteSprite != null)
                return _whiteSprite;

            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            _whiteSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 100f);
            return _whiteSprite;
        }

        void BuildUi()
        {
            EnsureEventSystem();

            var canvasGo = new GameObject(
                "Canvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);

            _canvas = canvasGo.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            Sprite uiSprite = GetUiSprite();
            Font font = ResolveMonospaceFont();

            var panel = CreatePanel(canvasGo.transform, "Panel", new Color(0.04f, 0.16f, 0.2f, 0.88f), uiSprite);
            _panelRt = panel.GetComponent<RectTransform>();
            _panelRt.anchorMin = new Vector2(0f, 1f);
            _panelRt.anchorMax = new Vector2(0f, 1f);
            _panelRt.pivot = new Vector2(0f, 1f);
            _panelRt.anchoredPosition = new Vector2(_margin.x, -_margin.y);
            _panelRt.sizeDelta = new Vector2(_width, 560f);
            _panelRt.localScale = Vector3.one * _scale;
            _appliedScale = _scale;

            var layout = panel.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(12, 12, 10, 10);
            layout.spacing = 4f;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var fitter = panel.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _statsText = CreateStatsText(panel.transform, font);
            CreateHintText(panel.transform, font);
        }

        void EnsureEventSystem()
        {
            if (FindAnyObjectByType<EventSystem>() != null)
                return;

            var eventSystemGo = new GameObject("EventSystem");
            eventSystemGo.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
            eventSystemGo.AddComponent<InputSystemUIInputModule>();
#else
            eventSystemGo.AddComponent<StandaloneInputModule>();
#endif
        }

        static Font ResolveMonospaceFont()
        {
            Font font = Font.CreateDynamicFontFromOSFont(
                new[] { "Consolas", "Cascadia Mono", "Courier New", "Lucida Console", "Menlo" },
                13);
            if (font != null)
                return font;

            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font != null)
                return font;

            return Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Arial" }, 13);
        }

        static Text CreateStatsText(Transform parent, Font font)
        {
            var go = CreateText(parent, "Stats", string.Empty, 13, FontStyle.Normal, font);
            var text = go.GetComponent<Text>();
            text.color = new Color(0.92f, 0.95f, 0.98f, 1f);
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.alignment = TextAnchor.UpperLeft;

            var layout = go.AddComponent<LayoutElement>();
            layout.minHeight = 460f;
            layout.flexibleHeight = 1f;
            return text;
        }

        static void CreateHintText(Transform parent, Font font)
        {
            var go = CreateText(parent, "Hint", "F3 — toggle HUD", 11, FontStyle.Italic, font);
            var text = go.GetComponent<Text>();
            text.color = new Color(0.56f, 0.78f, 0.85f, 1f);

            var layout = go.AddComponent<LayoutElement>();
            layout.minHeight = 18f;
        }

        static GameObject CreatePanel(Transform parent, string name, Color color, Sprite sprite)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.sprite = sprite;
            image.type = Image.Type.Simple;
            image.color = color;
            return go;
        }

        static GameObject CreateText(Transform parent, string name, string content, int fontSize, FontStyle fontStyle, Font font)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<Text>();
            text.font = font;
            text.fontSize = fontSize;
            text.fontStyle = fontStyle;
            text.text = content;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return go;
        }
    }
}
