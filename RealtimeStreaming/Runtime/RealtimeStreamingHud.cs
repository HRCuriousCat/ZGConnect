using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

namespace ZGConnect.RealtimeStreaming
{
    /// <summary>
    /// Runtime-built Canvas HUD for <see cref="RealtimeStreamingController"/>.
    /// </summary>
    public sealed class RealtimeStreamingHud : MonoBehaviour
    {
        RealtimeStreamingController _controller;
        Canvas _canvas;
        Text _statsText;
        Toggle _buildingsToggle;
        InputField _cullDistanceInput;
        bool _suppressBuildingToggleCallback;
        bool _suppressCullInputCallback;

        public void Initialize(RealtimeStreamingController controller)
        {
            _controller = controller;
            BuildUi();
            SyncControlsFromController();
        }

        public void SetVisible(bool visible)
        {
            if (_canvas != null)
                _canvas.gameObject.SetActive(visible);
        }

        void LateUpdate()
        {
            if (_controller == null || _statsText == null)
                return;

            if (!_controller.IsStreamingActive || _controller.StreamingCamera == null)
            {
                _statsText.text = string.Empty;
                return;
            }

            _statsText.text = string.Join("\n", _controller.BuildRuntimeHudLines());
        }

        void SyncControlsFromController()
        {
            if (_controller == null)
                return;

            _suppressBuildingToggleCallback = true;
            if (_buildingsToggle != null)
                _buildingsToggle.isOn = _controller.StreamBuildingsEnabled;
            _suppressBuildingToggleCallback = false;

            _suppressCullInputCallback = true;
            if (_cullDistanceInput != null)
            {
                _cullDistanceInput.text = _controller.BuildingCullDistanceMeters.ToString("F0", CultureInfo.InvariantCulture);
                _cullDistanceInput.interactable = _controller.StreamBuildingsEnabled;
            }
            _suppressCullInputCallback = false;
        }

        void OnBuildingsToggleChanged(bool enabled)
        {
            if (_suppressBuildingToggleCallback || _controller == null)
                return;

            _controller.SetStreamBuildingsEnabled(enabled);

            if (_cullDistanceInput != null)
                _cullDistanceInput.interactable = enabled;
        }

        void OnCullDistanceEndEdit(string text)
        {
            if (_suppressCullInputCallback || _controller == null)
                return;

            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float meters) &&
                !float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out meters))
            {
                SyncControlsFromController();
                return;
            }

            _controller.SetBuildingCullDistanceMeters(meters);
            SyncControlsFromController();
        }

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

            var canvasGo = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);

            _canvas = canvasGo.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            Sprite uiSprite = GetUiSprite();
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null)
                font = Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Arial" }, 14);

            var panel = CreatePanel(canvasGo.transform, "Panel", new Color(0.04f, 0.16f, 0.2f, 0.88f), uiSprite);
            var panelRt = panel.GetComponent<RectTransform>();
            panelRt.anchorMin = new Vector2(0f, 1f);
            panelRt.anchorMax = new Vector2(0f, 1f);
            panelRt.pivot = new Vector2(0f, 1f);
            panelRt.anchoredPosition = new Vector2(10f, -10f);
            panelRt.sizeDelta = new Vector2(320f, 360f);

            var layout = panel.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(12, 12, 10, 10);
            layout.spacing = 6f;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var fitter = panel.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            CreateHeader(panel.transform, font);
            _statsText = CreateStatsText(panel.transform, font);
            CreateControlsRow(panel.transform, font, uiSprite);
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

        static void CreateHeader(Transform parent, Font font)
        {
            var go = CreateText(parent, "Header", "ZGConnect Realtime", 14, FontStyle.Bold, font);
            var text = go.GetComponent<Text>();
            text.color = Color.white;

            var layout = go.AddComponent<LayoutElement>();
            layout.minHeight = 22f;
            layout.preferredHeight = 22f;
        }

        static Text CreateStatsText(Transform parent, Font font)
        {
            var go = CreateText(parent, "Stats", string.Empty, 12, FontStyle.Normal, font);
            var text = go.GetComponent<Text>();
            text.color = new Color(0.92f, 0.95f, 0.98f, 1f);
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.alignment = TextAnchor.UpperLeft;

            var layout = go.AddComponent<LayoutElement>();
            layout.minHeight = 180f;
            layout.flexibleHeight = 1f;
            return text;
        }

        void CreateControlsRow(Transform parent, Font font, Sprite uiSprite)
        {
            var row = new GameObject("Controls", typeof(RectTransform));
            row.transform.SetParent(parent, false);

            var rowLayout = row.AddComponent<HorizontalLayoutGroup>();
            rowLayout.spacing = 8f;
            rowLayout.childAlignment = TextAnchor.MiddleLeft;
            rowLayout.childControlWidth = true;
            rowLayout.childControlHeight = true;
            rowLayout.childForceExpandWidth = false;
            rowLayout.childForceExpandHeight = false;

            var rowElement = row.AddComponent<LayoutElement>();
            rowElement.minHeight = 30f;
            rowElement.preferredHeight = 30f;

            _buildingsToggle = CreateToggle(row.transform, "Show buildings", font, uiSprite, 150f);
            _buildingsToggle.onValueChanged.AddListener(OnBuildingsToggleChanged);

            _cullDistanceInput = CreateLabeledInputField(row.transform, "Cull (m)", font, uiSprite, 130f);
            _cullDistanceInput.onEndEdit.AddListener(OnCullDistanceEndEdit);
        }

        static void CreateHintText(Transform parent, Font font)
        {
            var go = CreateText(parent, "Hint", "RMB — fly camera", 11, FontStyle.Italic, font);
            var text = go.GetComponent<Text>();
            text.color = new Color(0.56f, 0.78f, 0.85f, 1f);

            var layout = go.AddComponent<LayoutElement>();
            layout.minHeight = 18f;
        }

        static Toggle CreateToggle(Transform parent, string label, Font font, Sprite sprite, float width)
        {
            var toggleGo = new GameObject("BuildingsToggle", typeof(RectTransform), typeof(Toggle));
            toggleGo.transform.SetParent(parent, false);

            var toggleLayout = toggleGo.AddComponent<LayoutElement>();
            toggleLayout.minWidth = width;
            toggleLayout.preferredWidth = width;

            var toggle = toggleGo.GetComponent<Toggle>();

            var backgroundGo = CreatePanel(toggleGo.transform, "Background", new Color(0.12f, 0.14f, 0.16f, 1f), sprite);
            var backgroundRt = backgroundGo.GetComponent<RectTransform>();
            backgroundRt.anchorMin = new Vector2(0f, 0.5f);
            backgroundRt.anchorMax = new Vector2(0f, 0.5f);
            backgroundRt.pivot = new Vector2(0f, 0.5f);
            backgroundRt.sizeDelta = new Vector2(18f, 18f);
            backgroundRt.anchoredPosition = Vector2.zero;

            var checkmarkGo = CreatePanel(backgroundGo.transform, "Checkmark", new Color(0.25f, 0.72f, 0.55f, 1f), sprite);
            StretchFull(checkmarkGo.GetComponent<RectTransform>());

            var labelGo = CreateText(toggleGo.transform, "Label", label, 12, FontStyle.Normal, font);
            var labelRt = labelGo.GetComponent<RectTransform>();
            labelRt.anchorMin = new Vector2(0f, 0.5f);
            labelRt.anchorMax = new Vector2(1f, 0.5f);
            labelRt.pivot = new Vector2(0f, 0.5f);
            labelRt.offsetMin = new Vector2(24f, -12f);
            labelRt.offsetMax = new Vector2(0f, 12f);
            var labelText = labelGo.GetComponent<Text>();
            labelText.color = new Color(0.92f, 0.95f, 0.98f, 1f);
            labelText.alignment = TextAnchor.MiddleLeft;

            toggle.targetGraphic = backgroundGo.GetComponent<Image>();
            toggle.graphic = checkmarkGo.GetComponent<Image>();
            toggle.isOn = true;
            return toggle;
        }

        static InputField CreateLabeledInputField(Transform parent, string label, Font font, Sprite sprite, float width)
        {
            var fieldGo = new GameObject("CullDistanceField", typeof(RectTransform));
            fieldGo.transform.SetParent(parent, false);

            var fieldLayout = fieldGo.AddComponent<LayoutElement>();
            fieldLayout.minWidth = width;
            fieldLayout.preferredWidth = width;

            var layout = fieldGo.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 6f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            var labelGo = CreateText(fieldGo.transform, "Label", label, 12, FontStyle.Normal, font);
            var labelElement = labelGo.AddComponent<LayoutElement>();
            labelElement.minWidth = 56f;
            labelElement.preferredWidth = 56f;
            var labelText = labelGo.GetComponent<Text>();
            labelText.color = new Color(0.92f, 0.95f, 0.98f, 1f);
            labelText.alignment = TextAnchor.MiddleLeft;

            var inputGo = new GameObject("Input", typeof(RectTransform), typeof(Image), typeof(InputField));
            inputGo.transform.SetParent(fieldGo.transform, false);
            var inputElement = inputGo.AddComponent<LayoutElement>();
            inputElement.minWidth = 68f;
            inputElement.preferredWidth = 68f;
            inputElement.minHeight = 24f;
            inputElement.preferredHeight = 24f;

            var inputImage = inputGo.GetComponent<Image>();
            inputImage.sprite = sprite;
            inputImage.type = Image.Type.Simple;
            inputImage.color = new Color(0.10f, 0.12f, 0.14f, 1f);

            var textGo = CreateText(inputGo.transform, "Text", string.Empty, 12, FontStyle.Normal, font);
            StretchFull(textGo.GetComponent<RectTransform>());
            var text = textGo.GetComponent<Text>();
            text.color = Color.white;
            text.supportRichText = false;
            text.alignment = TextAnchor.MiddleLeft;
            var textRt = textGo.GetComponent<RectTransform>();
            textRt.offsetMin = new Vector2(6f, 2f);
            textRt.offsetMax = new Vector2(-6f, -2f);

            var placeholderGo = CreateText(inputGo.transform, "Placeholder", "400", 12, FontStyle.Italic, font);
            StretchFull(placeholderGo.GetComponent<RectTransform>());
            var placeholder = placeholderGo.GetComponent<Text>();
            placeholder.color = new Color(0.55f, 0.58f, 0.62f, 0.75f);
            placeholder.fontStyle = FontStyle.Italic;
            placeholder.alignment = TextAnchor.MiddleLeft;
            var placeholderRt = placeholderGo.GetComponent<RectTransform>();
            placeholderRt.offsetMin = new Vector2(6f, 2f);
            placeholderRt.offsetMax = new Vector2(-6f, -2f);

            var inputField = inputGo.GetComponent<InputField>();
            inputField.textComponent = text;
            inputField.placeholder = placeholder;
            inputField.contentType = InputField.ContentType.DecimalNumber;
            inputField.lineType = InputField.LineType.SingleLine;
            return inputField;
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
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return go;
        }

        static void StretchFull(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
