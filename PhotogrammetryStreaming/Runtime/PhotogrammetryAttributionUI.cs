using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace ZGConnect.PhotogrammetryStreaming
{
    [DisallowMultipleComponent]
    public class PhotogrammetryAttributionUI : MonoBehaviour
    {
        [SerializeField] Canvas _canvas;
        [SerializeField] Text _copyrightText;
        [SerializeField] Image _logoImage;
        [SerializeField] Sprite _googleMapsLogo;
        [SerializeField] float _refreshInterval = 0.5f;

        readonly HashSet<string> _activeCopyrights = new();
        float _nextRefresh;

        public bool AttributionEnabled { get; set; } = true;

        void Awake()
        {
            EnsureUi();
        }

        void EnsureUi()
        {
            if (_canvas != null)
                return;

            var go = new GameObject("PhotogrammetryAttributionCanvas");
            go.transform.SetParent(transform, false);
            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 32000;
            go.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            go.AddComponent<GraphicRaycaster>();

            var bar = new GameObject("AttributionBar");
            bar.transform.SetParent(go.transform, false);
            var barRect = bar.AddComponent<RectTransform>();
            barRect.anchorMin = new Vector2(0f, 0f);
            barRect.anchorMax = new Vector2(1f, 0f);
            barRect.pivot = new Vector2(0.5f, 0f);
            barRect.sizeDelta = new Vector2(0f, 36f);
            barRect.anchoredPosition = Vector2.zero;
            var barImg = bar.AddComponent<Image>();
            barImg.color = new Color(1f, 1f, 1f, 0.75f);

            var logoGo = new GameObject("GoogleMapsLogo");
            logoGo.transform.SetParent(bar.transform, false);
            var logoRect = logoGo.AddComponent<RectTransform>();
            logoRect.anchorMin = new Vector2(0f, 0f);
            logoRect.anchorMax = new Vector2(0f, 1f);
            logoRect.pivot = new Vector2(0f, 0.5f);
            logoRect.sizeDelta = new Vector2(96f, 28f);
            logoRect.anchoredPosition = new Vector2(8f, 0f);
            _logoImage = logoGo.AddComponent<Image>();
            if (_googleMapsLogo != null)
                _logoImage.sprite = _googleMapsLogo;
            else
            {
                _logoImage.color = new Color(0.2f, 0.2f, 0.2f, 0.01f);
            }

            var textGo = new GameObject("CopyrightLine");
            textGo.transform.SetParent(bar.transform, false);
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0f, 0f);
            textRect.anchorMax = new Vector2(1f, 1f);
            textRect.offsetMin = new Vector2(112f, 4f);
            textRect.offsetMax = new Vector2(-8f, -4f);
            _copyrightText = textGo.AddComponent<Text>();
            _copyrightText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _copyrightText.fontSize = 11;
            _copyrightText.color = Color.black;
            _copyrightText.alignment = TextAnchor.MiddleLeft;
            _copyrightText.horizontalOverflow = HorizontalWrapMode.Overflow;
            _copyrightText.text = "Google Maps";
        }

        public void RegisterCopyright(string copyright)
        {
            if (!string.IsNullOrWhiteSpace(copyright))
                _activeCopyrights.Add(copyright);
        }

        public void UnregisterCopyright(string copyright)
        {
            if (!string.IsNullOrWhiteSpace(copyright))
                _activeCopyrights.Remove(copyright);
        }

        public void ClearCopyrights() => _activeCopyrights.Clear();

        void Update()
        {
            if (!AttributionEnabled)
            {
                if (_canvas != null)
                    _canvas.enabled = false;
                return;
            }

            if (_canvas != null)
                _canvas.enabled = true;

            if (Time.unscaledTime < _nextRefresh)
                return;
            _nextRefresh = Time.unscaledTime + _refreshInterval;
            RefreshLine();
        }

        void RefreshLine()
        {
            if (_copyrightText == null)
                return;
            string line = PhotogrammetryAttributionCollector.Aggregate(_activeCopyrights);
            if (string.IsNullOrWhiteSpace(line))
                line = "Google Maps";
            _copyrightText.text = line;
        }

        void OnValidate()
        {
            if (!Application.isPlaying && _googleMapsLogo != null && _logoImage != null)
                _logoImage.sprite = _googleMapsLogo;
        }
    }
}
