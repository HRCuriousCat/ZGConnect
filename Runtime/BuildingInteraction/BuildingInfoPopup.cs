using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ZGConnect
{
    /// <summary>
    /// Screen-anchored popup showing metadata for a single building.
    /// </summary>
    public sealed class BuildingInfoPopup : MonoBehaviour
    {
        [SerializeField] RectTransform _panel;
        [SerializeField] TextMeshProUGUI _buildingIdText;
        [SerializeField] TextMeshProUGUI _addressText;
        [SerializeField] TextMeshProUGUI _floorsText;
        [SerializeField] TextMeshProUGUI _areaText;
        [SerializeField] TextMeshProUGUI _classificationText;
        [SerializeField] TextMeshProUGUI _yearText;
        [SerializeField] TextMeshProUGUI _osmLabelText;

        [Header("Optional detail fields (value only, no label prefix)")]
        [SerializeField] TextMeshProUGUI _gpsText;
        [SerializeField] TextMeshProUGUI _streetText;
        [SerializeField] TextMeshProUGUI _houseNumberText;
        [SerializeField] TextMeshProUGUI _cityText;
        [SerializeField] TextMeshProUGUI _postcodeText;
        [SerializeField] TextMeshProUGUI _buildingTagText;
        [SerializeField] TextMeshProUGUI _roleText;
        [SerializeField] TextMeshProUGUI _osmFeatureText;

        [Header("Inspector (Play mode)")]
        [SerializeField] BuildingInfoSnapshotInspectorData _inspectorData = new();

        Transform _linkedBuilding;
        BuildingInfoSnapshot _currentSnapshot;
        Vector2 _screenOffset = new(0f, 40f);
        Vector2 _screenPadding = new(12f, 12f);

        public Transform LinkedBuilding => _linkedBuilding;
        public BuildingInfoSnapshot CurrentSnapshot => _currentSnapshot;
        public BuildingInfoSnapshotInspectorData InspectorData => _inspectorData;

        public void Configure(Vector2 screenOffset)
        {
            _screenOffset = screenOffset;
        }

        public void SetLinkedBuilding(Transform building)
        {
            _linkedBuilding = building;
        }

        public void Populate(BuildingInfoSnapshot data)
        {
            if (data == null)
                return;

            _currentSnapshot = data;
            data.CopyToInspectorData(_inspectorData);
            SetRow(_buildingIdText, "ID", data.buildingId);
            SetRow(_addressText, "Address", data.address);
            SetValue(_gpsText, data.gpsText);
            SetValue(_streetText, data.street);
            SetValue(_houseNumberText, data.houseNumber);
            SetValue(_cityText, data.city);
            SetValue(_postcodeText, data.postcode);
            SetValue(_buildingTagText, data.buildingTag);
            SetValue(_roleText, data.osmRole);
            SetValue(_osmFeatureText, data.osmFeature);

            if (data.floors > 0)
                SetRow(_floorsText, "Floors", data.floors.ToString(CultureInfo.InvariantCulture));
            else
                SetRowActive(_floorsText, false);

            if (data.grossFloorArea > 0f)
            {
                string area = string.Format(CultureInfo.InvariantCulture, "{0:0.#} m²", data.grossFloorArea);
                SetRow(_areaText, "GFA", area);
            }
            else
            {
                SetRowActive(_areaText, false);
            }

            SetRow(_classificationText, "Use", data.useClassification);

            if (data.constructionYear > 0)
                SetRow(_yearText, "Built", data.constructionYear.ToString(CultureInfo.InvariantCulture));
            else
                SetRowActive(_yearText, false);

            SetOsmRow(data);
        }

        void SetOsmRow(BuildingInfoSnapshot data)
        {
            if (_osmLabelText == null)
                return;

            string osmDetails = BuildOsmDisplayText(data);
            if (string.IsNullOrWhiteSpace(osmDetails))
            {
                SetRowActive(_osmLabelText, false);
                return;
            }

            SetRowActive(_osmLabelText, true);
            _osmLabelText.textWrappingMode = TextWrappingModes.Normal;
            _osmLabelText.text = $"OSM:\n{osmDetails}";

            if (_osmLabelText.TryGetComponent(out LayoutElement layout))
            {
                layout.minHeight = 18f;
                layout.preferredHeight = -1f;
                layout.flexibleHeight = 0f;
            }
        }

        static string BuildOsmDisplayText(BuildingInfoSnapshot data)
        {
            if (data == null || !data.osm.hasData)
                return null;

            var sb = new StringBuilder();
            BuildingOsmData osm = data.osm;

            AppendField(sb, "Query", FormatCoordinates(osm.queryLatitude, osm.queryLongitude));
            AppendField(sb, "Coord source", osm.coordSource);

            if (osm.buildingMatch.hasMatch)
            {
                BuildingOsmMatchData match = osm.buildingMatch;
                AppendField(sb, "Feature", FormatOsmFeature(match.osmType, match.osmId));
                AppendField(sb, "Role", match.role);
                AppendField(sb, "Label", match.label);
                AppendField(sb, "Match method", match.matchMethod);
                AppendField(sb, "Match score", match.matchScore.ToString(CultureInfo.InvariantCulture));
                AppendField(sb, "Distance", $"{match.distanceM.ToString("0.##", CultureInfo.InvariantCulture)} m");
                AppendTagBlock(sb, match.tags);
            }

            if (osm.atPoint != null && osm.atPoint.Count > 0)
            {
                for (int i = 0; i < osm.atPoint.Count; i++)
                {
                    BuildingOsmAtPointData point = osm.atPoint[i];
                    sb.AppendLine();
                    sb.AppendLine($"At point [{i + 1}]: {FormatOsmFeature(point.osmType, point.osmId)}");
                    AppendField(sb, "  Role", point.role);
                    AppendField(sb, "  Label", point.label);
                    if (point.areaM2 > 0f)
                    {
                        sb.Append("  Area: ");
                        sb.Append(point.areaM2.ToString("0.##", CultureInfo.InvariantCulture));
                        sb.AppendLine(" m²");
                    }

                    AppendTagBlock(sb, point.tags, "  ");
                }
            }

            string text = sb.ToString().Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        static void AppendField(StringBuilder sb, string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            sb.Append(label).Append(": ").AppendLine(value.Trim());
        }

        static void AppendTagBlock(StringBuilder sb, List<BuildingOsmTagEntry> tags, string linePrefix = "")
        {
            if (tags == null || tags.Count == 0)
                return;

            var sorted = new List<BuildingOsmTagEntry>(tags);
            sorted.Sort((a, b) => string.Compare(a.key, b.key, StringComparison.OrdinalIgnoreCase));

            sb.Append(linePrefix);
            sb.AppendLine("Tags:");
            foreach (BuildingOsmTagEntry tag in sorted)
            {
                if (string.IsNullOrWhiteSpace(tag.key))
                    continue;

                sb.Append(linePrefix);
                sb.Append("  ");
                sb.Append(tag.key);
                if (!string.IsNullOrWhiteSpace(tag.value))
                {
                    sb.Append('=');
                    sb.Append(tag.value);
                }

                sb.AppendLine();
            }
        }

        static string FormatOsmFeature(string osmType, long osmId)
        {
            string type = string.IsNullOrWhiteSpace(osmType) ? "feature" : osmType.Trim();
            return osmId > 0 ? $"{type}/{osmId.ToString(CultureInfo.InvariantCulture)}" : type;
        }

        static string FormatCoordinates(double latitude, double longitude)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:0.######}, {1:0.######}",
                latitude,
                longitude);
        }

        public void UpdateScreenAnchor(Canvas canvas, Camera camera, Bounds worldBounds)
        {
            RectTransform root = (RectTransform)transform;
            RectTransform panel = _panel != null ? _panel : root;
            if (panel == null || camera == null || canvas == null)
                return;

            Vector3 screenPoint3 = camera.WorldToScreenPoint(worldBounds.center);
            if (screenPoint3.z < 0f)
            {
                gameObject.SetActive(false);
                return;
            }

            if (!gameObject.activeSelf)
                gameObject.SetActive(true);

            Vector2 screenPoint = new Vector2(screenPoint3.x, screenPoint3.y) + _screenOffset;
            float scale = Mathf.Max(canvas.scaleFactor, 0.0001f);
            Vector2 panelSizeScreen = panel.rect.size * scale;

            float minX = _screenPadding.x + panelSizeScreen.x * panel.pivot.x;
            float maxX = Screen.width - _screenPadding.x - panelSizeScreen.x * (1f - panel.pivot.x);
            float minY = _screenPadding.y + panelSizeScreen.y * panel.pivot.y;
            float maxY = Screen.height - _screenPadding.y - panelSizeScreen.y * (1f - panel.pivot.y);

            screenPoint.x = Mathf.Clamp(screenPoint.x, minX, maxX);
            screenPoint.y = Mathf.Clamp(screenPoint.y, minY, maxY);

            RectTransform canvasRect = canvas.transform as RectTransform;
            Camera canvasCamera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    canvasRect, screenPoint, canvasCamera, out Vector2 localPoint))
            {
                return;
            }

            root.anchoredPosition = localPoint;
        }

        static void SetRow(TextMeshProUGUI text, string label, string value)
        {
            if (text == null)
                return;

            if (string.IsNullOrWhiteSpace(value))
            {
                SetRowActive(text, false);
                return;
            }

            SetRowActive(text, true);
            text.text = $"{label}: {value}";
        }

        /// <summary>Sets a TMP field to the raw value (for custom prefab labels).</summary>
        static void SetValue(TextMeshProUGUI text, string value)
        {
            if (text == null)
                return;

            if (string.IsNullOrWhiteSpace(value))
            {
                SetRowActive(text, false);
                return;
            }

            SetRowActive(text, true);
            text.text = value.Trim();
        }

        static void SetRowActive(TextMeshProUGUI text, bool active)
        {
            if (text == null)
                return;

            text.gameObject.SetActive(active);
        }

        public static BuildingInfoPopup CreateTemplate(Transform parent)
        {
            var root = new GameObject("BuildingInfoPopup", typeof(RectTransform));
            root.transform.SetParent(parent, false);

            var rootRt = (RectTransform)root.transform;
            rootRt.anchorMin = new Vector2(0.5f, 0.5f);
            rootRt.anchorMax = new Vector2(0.5f, 0.5f);
            rootRt.pivot = new Vector2(0.5f, 0f);
            rootRt.sizeDelta = Vector2.zero;

            var popup = root.AddComponent<BuildingInfoPopup>();
            var panel = CreatePanel(root.transform);
            popup._panel = panel;

            popup._buildingIdText = CreateRow(panel, "BuildingId", 13, FontStyles.Bold);
            popup._addressText = CreateRow(panel, "Address");
            popup._gpsText = CreateRow(panel, "Gps");
            popup._streetText = CreateRow(panel, "Street");
            popup._houseNumberText = CreateRow(panel, "HouseNumber");
            popup._cityText = CreateRow(panel, "City");
            popup._postcodeText = CreateRow(panel, "Postcode");
            popup._buildingTagText = CreateRow(panel, "BuildingTag");
            popup._roleText = CreateRow(panel, "Role");
            popup._osmFeatureText = CreateRow(panel, "OsmFeature");
            popup._floorsText = CreateRow(panel, "Floors");
            popup._areaText = CreateRow(panel, "Area");
            popup._classificationText = CreateRow(panel, "Classification");
            popup._yearText = CreateRow(panel, "Year");
            popup._osmLabelText = CreateRow(panel, "OsmLabel", wrap: true);

            root.SetActive(false);
            return popup;
        }

        static RectTransform CreatePanel(Transform parent)
        {
            var panelGo = new GameObject("Panel", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            panelGo.transform.SetParent(parent, false);

            var panelRt = panelGo.GetComponent<RectTransform>();
            panelRt.sizeDelta = new Vector2(280f, 0f);
            panelRt.pivot = new Vector2(0.5f, 0f);

            var image = panelGo.GetComponent<Image>();
            image.color = new Color(0.04f, 0.1f, 0.14f, 0.92f);
            image.raycastTarget = true;

            var layout = panelGo.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(12, 12, 10, 10);
            layout.spacing = 4f;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var fitter = panelGo.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            return panelRt;
        }

        static TextMeshProUGUI CreateRow(
            RectTransform panel,
            string name,
            float fontSize = 12f,
            FontStyles style = FontStyles.Normal,
            bool wrap = false)
        {
            var rowGo = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
            rowGo.transform.SetParent(panel, false);

            var layout = rowGo.GetComponent<LayoutElement>();
            layout.minHeight = 18f;
            layout.preferredHeight = wrap ? -1f : 18f;

            var text = rowGo.GetComponent<TextMeshProUGUI>();
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.color = new Color(0.92f, 0.95f, 0.98f, 1f);
            text.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            text.richText = false;
            text.raycastTarget = false;
            return text;
        }
    }
}

