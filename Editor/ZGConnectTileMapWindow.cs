using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using ZGConnect;
using ZGConnect.RealtimeStreaming;

namespace ZGConnect.Editor
{
  /// <summary>
  /// Map-based tile region picker. Drag a rectangle to select heightmap tiles for import.
  /// </summary>
  public class ZGConnectTileMapWindow : EditorWindow
  {
    private Texture2D _mapTexture;
    private Texture2D _customMap;

    private double _viewMinE;
    private double _viewMaxE;
    private double _viewMinN;
    private double _viewMaxN;

    private bool   _isDragging;
    private Vector2 _dragStartGui;
    private Vector2 _dragCurrentGui;

    private int _selectedTileCount;
    private int _totalTileCount;

    private List<HeightmapTileJson> _cachedTiles;
    private HashSet<string> _cachedPackedTileIds;
    private bool _dataCacheDirty = true;

    private static readonly Color kTileOutline         = new Color(1f, 1f, 1f, 0.35f);
    private static readonly Color kTileInvalid         = new Color(0.9f, 0.25f, 0.2f, 0.25f);
    private static readonly Color kTilePacked          = new Color(0.15f, 0.55f, 0.82f, 0.42f);
    private static readonly Color kTilePackedInSelection = new Color(0.25f, 0.68f, 0.95f, 0.52f);
    private static readonly Color kTileInSelection     = new Color(0.2f, 0.85f, 0.35f, 0.45f);
    private static readonly Color kSelectionOutline    = new Color(1f, 0.92f, 0.2f, 0.95f);
    private static readonly Color kSelectionFill       = new Color(1f, 0.92f, 0.2f, 0.18f);

    const string kCustomMapGuidPrefKey = "ZGConnect.TileMap.CustomMapGuid";
    const float kFooterReservedHeight = 220f;
    const float kToolbarHeight = 22f;
    const float kMapMinHeight = 160f;
    const double kMinViewSpanMeters = 300.0;
    const float kWheelZoomStep = 0.12f;

    bool _isPanning;

    int _footerSelMinE;
    int _footerSelMaxE;
    int _footerSelMinN;
    int _footerSelMaxN;
    int _footerViewMinE;
    int _footerViewMaxE;
    int _footerViewMinN;
    int _footerViewMaxN;
    int _footerSelectedCount = -1;
    int _footerPackedInView = -1;

    static GUIStyle s_mapLabelStyle;
    static GUIStyle s_scaleBarStyle;
    static GUIStyle s_footerLegendStyle;

    [MenuItem("ZG Connect/Tile Map Selector")]
    public static void ShowWindow()
    {
      var w = GetWindow<ZGConnectTileMapWindow>("ZG Connect — Map");
      w.minSize = new Vector2(520, 560);
      w.ResetView();
    }

    public static void OpenFromImporter()
    {
      var w = GetWindow<ZGConnectTileMapWindow>("ZG Connect — Map");
      w.minSize = new Vector2(520, 560);
      w.RefreshFromImporter();
      w.Show();
      w.Focus();
    }

    public void RefreshFromImporter()
    {
      InvalidateDataCache();
      _mapTexture = ZGConnectMapPlaceholderUtility.EnsurePlaceholder();
      SyncSelectionFromBridge();
      FrameViewAfterImporterSync();
      Repaint();
    }

    void FrameViewAfterImporterSync()
    {
      GetSelectionEpsg(out int minE, out int maxE, out int minN, out int maxN);
      if (maxE > minE && maxN > minN)
      {
        FrameSelectionExtent(0.08f);
        return;
      }

      if (TryGetDatasetTileBounds(out _, out _, out _, out _))
        FrameDatasetExtent();
      else
        FrameFullMapExtent();
    }

    private void OnEnable()
    {
      InvalidateDataCache();
      _customMap = LoadCustomMapFromPrefs();
      _mapTexture = ZGConnectMapPlaceholderUtility.EnsurePlaceholder();
      SyncSelectionFromBridge();
      GetSelectionEpsg(out int minE, out int maxE, out int minN, out int maxN);
      if (maxE <= minE || maxN <= minN)
      {
        if (TryGetDatasetTileBounds(out int extentMinE, out int extentMaxE, out int extentMinN, out int extentMaxN))
          SetSelectionEpsg(extentMinE, extentMaxE, extentMinN, extentMaxN);
      }

      FrameViewAfterImporterSync();
    }

    private void ResetView() => FrameFullMapExtent();

    /// <summary>Frames the authored overview background (full source dataset georef).</summary>
    private void FrameFullMapExtent()
    {
      GetBackgroundTextureGeorefBounds(out int minE, out int maxE, out int minN, out int maxN);
      SetViewEpsg(minE, maxE, minN, maxN);
    }

    private void FrameDatasetExtent(float paddingFraction = 0.08f)
    {
      if (!TryGetDatasetTileBounds(out int minE, out int maxE, out int minN, out int maxN))
      {
        FrameFullMapExtent();
        return;
      }

      ExpandBounds(ref minE, ref maxE, ref minN, ref maxN, paddingFraction);
      SetViewEpsg(minE, maxE, minN, maxN);
    }

    private void FrameSelectionExtent(float paddingFraction = 0.15f)
    {
      GetSelectionEpsg(out int minE, out int maxE, out int minN, out int maxN);
      if (maxE <= minE || maxN <= minN)
      {
        FrameDatasetExtent();
        return;
      }

      ExpandBounds(ref minE, ref maxE, ref minN, ref maxN, paddingFraction);
      SetViewEpsg(minE, maxE, minN, maxN);
    }

    static void ExpandBounds(ref int minE, ref int maxE, ref int minN, ref int maxN, float paddingFraction)
    {
      int width = maxE - minE;
      int height = maxN - minN;
      int padE = Mathf.Max(1, Mathf.RoundToInt(width * paddingFraction));
      int padN = Mathf.Max(1, Mathf.RoundToInt(height * paddingFraction));
      minE -= padE;
      maxE += padE;
      minN -= padN;
      maxN += padN;
    }

    private void SetViewEpsg(int minE, int maxE, int minN, int maxN)
    {
      if (maxE <= minE || maxN <= minN)
        return;

      ClampViewEpsg(ref minE, ref maxE, ref minN, ref maxN);

      if (_viewMinE == minE && _viewMaxE == maxE && _viewMinN == minN && _viewMaxN == maxN)
        return;

      _viewMinE = minE;
      _viewMaxE = maxE;
      _viewMinN = minN;
      _viewMaxN = maxN;
      Repaint();
    }

    void ClampViewEpsg(ref int minE, ref int maxE, ref int minN, ref int maxN)
    {
      GetViewLimits(out int limitMinE, out int limitMaxE, out int limitMinN, out int limitMaxN);

      double width = maxE - minE;
      double height = maxN - minN;
      width = System.Math.Max(width, kMinViewSpanMeters);
      height = System.Math.Max(height, kMinViewSpanMeters);

      double maxWidth = limitMaxE - limitMinE;
      double maxHeight = limitMaxN - limitMinN;
      width = System.Math.Min(width, maxWidth);
      height = System.Math.Min(height, maxHeight);

      double centerE = (minE + maxE) * 0.5;
      double centerN = (minN + maxN) * 0.5;
      minE = Mathf.RoundToInt((float)(centerE - width * 0.5));
      maxE = Mathf.RoundToInt((float)(centerE + width * 0.5));
      minN = Mathf.RoundToInt((float)(centerN - height * 0.5));
      maxN = Mathf.RoundToInt((float)(centerN + height * 0.5));

      if (minE < limitMinE)
      {
        maxE += limitMinE - minE;
        minE = limitMinE;
      }

      if (maxE > limitMaxE)
      {
        minE -= maxE - limitMaxE;
        maxE = limitMaxE;
      }

      if (minN < limitMinN)
      {
        maxN += limitMinN - minN;
        minN = limitMinN;
      }

      if (maxN > limitMaxN)
      {
        minN -= maxN - limitMaxN;
        maxN = limitMaxN;
      }

      minE = Mathf.Max(minE, limitMinE);
      maxE = Mathf.Min(maxE, limitMaxE);
      minN = Mathf.Max(minN, limitMinN);
      maxN = Mathf.Min(maxN, limitMaxN);
    }

    void GetViewLimits(out int minE, out int maxE, out int minN, out int maxN)
    {
      GetBackgroundTextureGeorefBounds(out minE, out maxE, out minN, out maxN);

      if (TryGetDatasetTileBounds(out int dsMinE, out int dsMaxE, out int dsMinN, out int dsMaxN))
      {
        minE = System.Math.Min(minE, dsMinE);
        maxE = System.Math.Max(maxE, dsMaxE);
        minN = System.Math.Min(minN, dsMinN);
        maxN = System.Math.Max(maxN, dsMaxN);
      }
    }

    bool TryGetDatasetTileBounds(out int minE, out int maxE, out int minN, out int maxN)
    {
      EnsureDataCache();
      GetDatasetTileExtentFromTiles(_cachedTiles, out minE, out maxE, out minN, out maxN);
      return maxE > minE && maxN > minN;
    }

    private void OnGUI()
    {
      if (!ZGConnectImportRegionBridge.IsImporterReady)
      {
        EditorGUILayout.HelpBox(
          "Open Spatial Streaming, RealTime Asset Importer, or Dataset Import Manager and scan a dataset first.",
          MessageType.Warning);
        if (GUILayout.Button("Open Spatial Streaming"))
          ZGConnect.SpatialStreaming.Editor.SpatialStreamingWindow.Open();
        if (GUILayout.Button("Open RealTime Asset Importer"))
          ZGConnect.RealtimeStreaming.Editor.ZGConnectRealtimeStreamerWindow.Open();
        if (GUILayout.Button("Open Dataset Import Manager"))
          ZGConnectDatasetManagerWindow.ShowWindow();
        return;
      }

      EnsureDataCache();
      _totalTileCount = _cachedTiles?.Count ?? 0;

      DrawMapToolbar();
      EditorGUILayout.Space(2);
      DrawMapArea(_cachedTiles, _cachedPackedTileIds);
      EditorGUILayout.Space(4);
      DrawFooter(_cachedTiles, _cachedPackedTileIds);
    }

    void DrawMapToolbar()
    {
      EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
      if (GUILayout.Button("Full map", EditorStyles.toolbarButton, GUILayout.Width(72)))
        FrameFullMapExtent();
      if (GUILayout.Button("Dataset tiles", EditorStyles.toolbarButton, GUILayout.Width(88)))
        FrameDatasetExtent();
      if (GUILayout.Button("Selection", EditorStyles.toolbarButton, GUILayout.Width(72)))
        FrameSelectionExtent();

      GUILayout.FlexibleSpace();

      GetViewEpsg(out int viewMinE, out int viewMaxE, out int viewMinN, out int viewMaxN);
      double viewWidthKm = (viewMaxE - viewMinE) / 1000.0;
      double viewHeightKm = (viewMaxN - viewMinN) / 1000.0;
      GUILayout.Label(
        $"View {viewWidthKm:0.0} × {viewHeightKm:0.0} km",
        EditorStyles.miniLabel);

      EditorGUILayout.EndHorizontal();
    }

    void GetViewEpsg(out int minE, out int maxE, out int minN, out int maxN)
    {
      minE = Mathf.RoundToInt((float)_viewMinE);
      maxE = Mathf.RoundToInt((float)_viewMaxE);
      minN = Mathf.RoundToInt((float)_viewMinN);
      maxN = Mathf.RoundToInt((float)_viewMaxN);
    }

    void InvalidateDataCache() => _dataCacheDirty = true;

    void EnsureDataCache()
    {
      if (!_dataCacheDirty)
        return;

      _cachedTiles = ZGConnectImportRegionBridge.GetTiles?.Invoke() ?? new List<HeightmapTileJson>();
      _cachedPackedTileIds = ZGConnectImportRegionBridge.GetPackedTileIds?.Invoke();
      _dataCacheDirty = false;
    }

    private Texture2D ActiveMap => _customMap != null ? _customMap : _mapTexture;

    private void DrawMapArea(List<HeightmapTileJson> tiles, HashSet<string> packedTileIds)
    {
      float mapHeight = Mathf.Max(
        kMapMinHeight,
        position.height - kFooterReservedHeight - kToolbarHeight - 6f);
      var hostRect = GUILayoutUtility.GetRect(
        GUIContent.none, GUIStyle.none,
        GUILayout.ExpandWidth(true), GUILayout.Height(mapHeight));

      EditorGUI.DrawRect(hostRect, new Color(0.12f, 0.12f, 0.14f));
      var mapRect = PixelAlignRect(FitRectToViewAspect(hostRect));

      HandleMapInput(mapRect);

      Rect localMapRect = ToLocalRect(mapRect, hostRect);
      GUI.BeginClip(hostRect);
      try
      {
        var tex = ActiveMap;
        if (tex != null)
          DrawMapTexture(localMapRect, tex);

        if (tiles != null)
          DrawTiles(localMapRect, tiles, packedTileIds);

        DrawActiveSelection(localMapRect);
        DrawDragRect(mapRect, hostRect, localMapRect);
      }
      finally
      {
        GUI.EndClip();
      }

      DrawMapLabels(mapRect);
      DrawScaleBar(mapRect);
    }

    private Rect FitRectToViewAspect(Rect hostRect)
    {
      double viewWidth = _viewMaxE - _viewMinE;
      double viewHeight = _viewMaxN - _viewMinN;
      if (viewWidth <= 0.0 || viewHeight <= 0.0)
        return hostRect;

      float viewAspect = (float)(viewWidth / viewHeight);
      float hostAspect = hostRect.width / Mathf.Max(hostRect.height, 0.0001f);

      // Keep equal metres-per-pixel on both axes to preserve 1x1 km tile shape.
      if (hostAspect > viewAspect)
      {
        float width = hostRect.height * viewAspect;
        float x = hostRect.x + (hostRect.width - width) * 0.5f;
        return new Rect(x, hostRect.y, width, hostRect.height);
      }
      else
      {
        float height = hostRect.width / Mathf.Max(viewAspect, 0.0001f);
        float y = hostRect.y + (hostRect.height - height) * 0.5f;
        return new Rect(hostRect.x, y, hostRect.width, height);
      }
    }

    /// <summary>
    /// Draws the overview texture for the portion of the view that overlaps authored map georef.
    /// Areas outside the map image (e.g. dataset tiles north of the placeholder) stay on the dark host fill.
    /// </summary>
    private void DrawMapTexture(Rect mapRect, Texture2D tex)
    {
      EditorGUI.DrawRect(mapRect, new Color(0.10f, 0.10f, 0.11f));

      GetBackgroundTextureGeorefBounds(out int texMinE, out int texMaxE, out int texMinN, out int texMaxN);

      double intersectMinE = System.Math.Max(_viewMinE, texMinE);
      double intersectMaxE = System.Math.Min(_viewMaxE, texMaxE);
      double intersectMinN = System.Math.Max(_viewMinN, texMinN);
      double intersectMaxN = System.Math.Min(_viewMaxN, texMaxN);
      if (intersectMaxE <= intersectMinE || intersectMaxN <= intersectMinN)
        return;

      Rect imageRect = EpsgBoxToGuiRect(
        Mathf.FloorToInt((float)intersectMinE),
        Mathf.CeilToInt((float)intersectMaxE),
        Mathf.FloorToInt((float)intersectMinN),
        Mathf.CeilToInt((float)intersectMaxN),
        mapRect);

      Rect uv = ViewToMapUvRect(
        intersectMinE, intersectMaxE, intersectMinN, intersectMaxN,
        texMinE, texMaxE, texMinN, texMaxN);
      if (uv.width <= 0f || uv.height <= 0f)
        return;

      GUI.DrawTextureWithTexCoords(imageRect, tex, uv, true);
    }

    /// <summary>
    /// EPSG bounds covered by the overview background image (full source dataset when configured).
    /// </summary>
    void GetBackgroundTextureGeorefBounds(out int minE, out int maxE, out int minN, out int maxN)
    {
      if (ZGConnectImportRegionBridge.GetBackgroundMapGeorefBounds != null)
      {
        var (bgMinE, bgMaxE, bgMinN, bgMaxN) = ZGConnectImportRegionBridge.GetBackgroundMapGeorefBounds();
        if (bgMaxE > bgMinE && bgMaxN > bgMinN)
        {
          minE = bgMinE;
          maxE = bgMaxE;
          minN = bgMinN;
          maxN = bgMaxN;
          return;
        }
      }

      ZGConnectMapExtent.GetCityBoundingBox(out minE, out maxE, out minN, out maxN);
    }

    static bool BoundsDiffer(int aMinE, int aMaxE, int aMinN, int aMaxN, int bMinE, int bMaxE, int bMinN, int bMaxN) =>
      aMinE != bMinE || aMaxE != bMaxE || aMinN != bMinN || aMaxN != bMaxN;

    static Rect PixelAlignRect(Rect rect)
    {
      float xMin = Mathf.Floor(rect.xMin);
      float yMin = Mathf.Floor(rect.yMin);
      float xMax = Mathf.Ceil(rect.xMax);
      float yMax = Mathf.Ceil(rect.yMax);
      return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
    }

    static Rect ToLocalRect(Rect rect, Rect clipHost)
    {
      return new Rect(
        rect.x - clipHost.x,
        rect.y - clipHost.y,
        rect.width,
        rect.height);
    }

    static Rect IntersectRect(Rect a, Rect b)
    {
      float xMin = Mathf.Max(a.xMin, b.xMin);
      float yMin = Mathf.Max(a.yMin, b.yMin);
      float xMax = Mathf.Min(a.xMax, b.xMax);
      float yMax = Mathf.Min(a.yMax, b.yMax);
      if (xMax <= xMin || yMax <= yMin)
        return Rect.zero;

      return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
    }

    private static Rect ViewToMapUvRect(
      double viewMinE, double viewMaxE, double viewMinN, double viewMaxN,
      double extentMinE, double extentMaxE, double extentMinN, double extentMaxN)
    {
      double extentWidth = extentMaxE - extentMinE;
      double extentHeight = extentMaxN - extentMinN;
      if (extentWidth <= 0.0 || extentHeight <= 0.0)
        return new Rect(0f, 0f, 1f, 1f);

      float u0 = Mathf.Clamp01((float)((viewMinE - extentMinE) / extentWidth));
      float u1 = Mathf.Clamp01((float)((viewMaxE - extentMinE) / extentWidth));
      float v0 = Mathf.Clamp01((float)((viewMinN - extentMinN) / extentHeight));
      float v1 = Mathf.Clamp01((float)((viewMaxN - extentMinN) / extentHeight));
      return new Rect(u0, v0, Mathf.Max(u1 - u0, 0.0001f), Mathf.Max(v1 - v0, 0.0001f));
    }

    private void DrawMapLabels(Rect mapRect)
    {
      EnsureMapStyles();
      GUI.Label(new Rect(mapRect.x + 4, mapRect.y + 4, 120, 18), "N ↑", s_mapLabelStyle);
      GUI.Label(new Rect(mapRect.xMax - 130, mapRect.yMax - 20, 126, 18), "EPSG:3765", s_mapLabelStyle);
    }

    static void EnsureMapStyles()
    {
      if (s_mapLabelStyle == null)
      {
        s_mapLabelStyle = new GUIStyle(EditorStyles.miniLabel)
        {
          normal = { textColor = new Color(1f, 1f, 1f, 0.85f) },
          padding = new RectOffset(4, 4, 2, 2),
        };
      }

      if (s_scaleBarStyle == null)
      {
        s_scaleBarStyle = new GUIStyle(EditorStyles.miniLabel)
        {
          normal = { textColor = new Color(1f, 1f, 1f, 0.9f) },
          alignment = TextAnchor.LowerCenter,
        };
      }

      if (s_footerLegendStyle == null)
      {
        s_footerLegendStyle = new GUIStyle(EditorStyles.wordWrappedMiniLabel)
        {
          wordWrap = true,
          padding = new RectOffset(2, 2, 2, 4),
        };
      }
    }

    private void HandleMapInput(Rect mapRect)
    {
      var e = Event.current;
      if (!mapRect.Contains(e.mousePosition))
        return;

      if (e.type == EventType.ScrollWheel)
      {
        EpsgFromGui(e.mousePosition, mapRect, out double anchorE, out double anchorN);
        float direction = Mathf.Sign(e.delta.y);
        double factor = 1.0 + direction * kWheelZoomStep;
        ZoomViewAround(anchorE, anchorN, factor);
        e.Use();
        Repaint();
        return;
      }

      if (e.type == EventType.MouseDown && e.button == 2)
      {
        _isPanning = true;
        e.Use();
        return;
      }

      if (e.type == EventType.MouseDrag && e.button == 2 && _isPanning)
      {
        PanViewByGuiDelta(e.delta, mapRect);
        e.Use();
        Repaint();
        return;
      }

      if (e.type == EventType.MouseUp && e.button == 2)
      {
        _isPanning = false;
        e.Use();
        return;
      }

      if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
      {
        _isDragging     = true;
        _dragStartGui   = e.mousePosition;
        _dragCurrentGui = e.mousePosition;
        e.Use();
      }

      if (e.type == EventType.MouseDrag && e.button == 0 && _isDragging)
      {
        _dragCurrentGui = e.mousePosition;
        e.Use();
        Repaint();
      }

      if (e.type == EventType.MouseUp && e.button == 0 && _isDragging)
      {
        _isDragging     = false;
        _dragCurrentGui = e.mousePosition;
        CommitDragSelection(mapRect);
        e.Use();
        Repaint();
      }
    }

    void PanViewByGuiDelta(Vector2 guiDelta, Rect mapRect)
    {
      if (mapRect.width <= 0.01f || mapRect.height <= 0.01f)
        return;

      double viewWidth = _viewMaxE - _viewMinE;
      double viewHeight = _viewMaxN - _viewMinN;
      double deltaE = -guiDelta.x / mapRect.width * viewWidth;
      double deltaN = guiDelta.y / mapRect.height * viewHeight;
      SetViewEpsg(
        Mathf.RoundToInt((float)(_viewMinE + deltaE)),
        Mathf.RoundToInt((float)(_viewMaxE + deltaE)),
        Mathf.RoundToInt((float)(_viewMinN + deltaN)),
        Mathf.RoundToInt((float)(_viewMaxN + deltaN)));
    }

    void ZoomViewAround(double anchorE, double anchorN, double factor)
    {
      factor = System.Math.Max(0.05, System.Math.Min(factor, 20.0));
      double width = (_viewMaxE - _viewMinE) * factor;
      double height = (_viewMaxN - _viewMinN) * factor;

      double relU = (_viewMaxE - _viewMinE) > 0.0
        ? (anchorE - _viewMinE) / (_viewMaxE - _viewMinE)
        : 0.5;
      double relV = (_viewMaxN - _viewMinN) > 0.0
        ? (anchorN - _viewMinN) / (_viewMaxN - _viewMinN)
        : 0.5;

      int minE = Mathf.RoundToInt((float)(anchorE - relU * width));
      int maxE = Mathf.RoundToInt((float)(minE + width));
      int minN = Mathf.RoundToInt((float)(anchorN - relV * height));
      int maxN = Mathf.RoundToInt((float)(minN + height));
      SetViewEpsg(minE, maxE, minN, maxN);
    }

    void DrawScaleBar(Rect mapRect)
    {
      double viewWidth = _viewMaxE - _viewMinE;
      if (viewWidth <= 0.0 || mapRect.width <= 40f)
        return;

      double[] candidatesMeters = { 100, 250, 500, 1000, 2000, 5000, 10000, 20000 };
      double targetMeters = viewWidth * 0.22;
      double barMeters = candidatesMeters[0];
      foreach (double candidate in candidatesMeters)
      {
        barMeters = candidate;
        if (candidate >= targetMeters)
          break;
      }

      float barPixels = (float)(barMeters / viewWidth * mapRect.width);
      barPixels = Mathf.Clamp(barPixels, 36f, mapRect.width * 0.45f);
      barMeters = barPixels / mapRect.width * viewWidth;

      var style = s_scaleBarStyle;
      EnsureMapStyles();

      float y = mapRect.yMax - 22f;
      float x = mapRect.x + 12f;
      var barRect = new Rect(x, y, barPixels, 3f);
      EditorGUI.DrawRect(barRect, new Color(1f, 1f, 1f, 0.85f));
      EditorGUI.DrawRect(new Rect(x, y - 4f, 1f, 11f), new Color(1f, 1f, 1f, 0.85f));
      EditorGUI.DrawRect(new Rect(x + barPixels - 1f, y - 4f, 1f, 11f), new Color(1f, 1f, 1f, 0.85f));

      string label = barMeters >= 1000.0
        ? $"{barMeters / 1000.0:0.#} km"
        : $"{barMeters:0} m";
      GUI.Label(new Rect(x, y - 18f, barPixels, 16f), label, style);
    }

    private void GetDatasetTileExtent(out int minE, out int maxE, out int minN, out int maxN)
    {
      minE = ZGConnectMapExtent.MinE;
      maxE = ZGConnectMapExtent.MaxE;
      minN = ZGConnectMapExtent.MinN;
      maxN = ZGConnectMapExtent.MaxN;

      var tiles = ZGConnectImportRegionBridge.GetTiles?.Invoke();
      if (tiles == null || tiles.Count == 0)
        return;

      int tMinE = int.MaxValue;
      int tMaxE = int.MinValue;
      int tMinN = int.MaxValue;
      int tMaxN = int.MinValue;

      foreach (var t in tiles)
      {
        if (t.Left < tMinE) tMinE = t.Left;
        if (t.Right > tMaxE) tMaxE = t.Right;
        if (t.Bottom < tMinN) tMinN = t.Bottom;
        if (t.Top > tMaxN) tMaxN = t.Top;
      }

      if (tMaxE > tMinE && tMaxN > tMinN)
      {
        minE = tMinE;
        maxE = tMaxE;
        minN = tMinN;
        maxN = tMaxN;
      }
    }

    private void GetActiveExtent(out int minE, out int maxE, out int minN, out int maxN) =>
      GetDatasetTileExtent(out minE, out maxE, out minN, out maxN);

    private void DrawTiles(Rect mapRect, List<HeightmapTileJson> tiles, HashSet<string> packedTileIds)
    {
      if (tiles == null || tiles.Count == 0)
        return;

      GetSelectionEpsg(out int selMinE, out int selMaxE, out int selMinN, out int selMaxN);
      GetViewEpsg(out int viewMinE, out int viewMaxE, out int viewMinN, out int viewMaxN);

      foreach (var t in tiles)
      {
        if (t.Right <= viewMinE || t.Left >= viewMaxE || t.Top <= viewMinN || t.Bottom >= viewMaxN)
          continue;

        Rect tr = TileToGuiRect(t, mapRect);
        if (tr.width < 0.5f || tr.height < 0.5f)
          continue;

        bool inSel = t.Left < selMaxE && t.Right > selMinE &&
                     t.Bottom < selMaxN && t.Top > selMinN;
        bool packed = packedTileIds != null &&
                      packedTileIds.Contains($"{t.Left}_{t.Bottom}");
        bool invalid = t.InvalidRatio > 0.95f;

        Color fill = ResolveTileFill(inSel, packed, invalid);
        if (fill.a > 0.01f)
          EditorGUI.DrawRect(tr, fill);

        if (tr.width >= 3f && tr.height >= 3f)
          DrawRectOutline(tr, packed ? new Color(0.35f, 0.8f, 1f, 0.7f) : kTileOutline, packed ? 1.5f : 1f);
      }
    }

    private static Color ResolveTileFill(bool inSelection, bool packed, bool invalid)
    {
      if (packed)
        return inSelection ? kTilePackedInSelection : kTilePacked;
      if (inSelection)
        return kTileInSelection;
      if (invalid)
        return kTileInvalid;
      return Color.clear;
    }

    private void DrawActiveSelection(Rect mapRect)
    {
      GetSelectionEpsg(out int minE, out int maxE, out int minN, out int maxN);
      if (minE >= maxE || minN >= maxN) return;

      Rect r = IntersectRect(EpsgBoxToGuiRect(minE, maxE, minN, maxN, mapRect), mapRect);
      if (r.width <= 0f || r.height <= 0f)
        return;

      EditorGUI.DrawRect(r, kSelectionFill);
      DrawRectOutline(r, kSelectionOutline, 2f);
    }

    private void DrawDragRect(Rect mapRectGlobal, Rect hostRect, Rect mapRectLocal)
    {
      if (!_isDragging) return;

      Rect r = ToLocalRect(GuiDragRect(), hostRect);
      r = IntersectRect(r, mapRectLocal);
      if (r.width <= 0f || r.height <= 0f)
        return;

      EditorGUI.DrawRect(r, kSelectionFill);
      DrawRectOutline(r, kSelectionOutline, 2f);
    }

    private Rect GuiDragRect()
    {
      float xMin = Mathf.Min(_dragStartGui.x, _dragCurrentGui.x);
      float xMax = Mathf.Max(_dragStartGui.x, _dragCurrentGui.x);
      float yMin = Mathf.Min(_dragStartGui.y, _dragCurrentGui.y);
      float yMax = Mathf.Max(_dragStartGui.y, _dragCurrentGui.y);
      return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
    }

    private void CommitDragSelection(Rect mapRect)
    {
      Rect r = GuiDragRect();
      if (r.width < 4f || r.height < 4f) return;

      var corners = new[]
      {
        new Vector2(r.xMin, r.yMin),
        new Vector2(r.xMax, r.yMin),
        new Vector2(r.xMin, r.yMax),
        new Vector2(r.xMax, r.yMax)
      };

      double minE = double.MaxValue, maxE = double.MinValue;
      double minN = double.MaxValue, maxN = double.MinValue;

      foreach (var p in corners)
      {
        if (!mapRect.Contains(p)) continue;
        EpsgFromGui(p, mapRect, out double e, out double n);
        minE = System.Math.Min(minE, e);
        maxE = System.Math.Max(maxE, e);
        minN = System.Math.Min(minN, n);
        maxN = System.Math.Max(maxN, n);
      }

      if (minE >= maxE || minN >= maxN) return;

      SetSelectionEpsg(
        Mathf.FloorToInt((float)minE),
        Mathf.CeilToInt((float)maxE),
        Mathf.FloorToInt((float)minN),
        Mathf.CeilToInt((float)maxN));
    }

    private void DrawFooter(List<HeightmapTileJson> tiles, HashSet<string> packedTileIds)
    {
      GetSelectionEpsg(out int minE, out int maxE, out int minN, out int maxN);
      GetViewEpsg(out int viewMinE, out int viewMaxE, out int viewMinN, out int viewMaxN);

      if (minE != _footerSelMinE || maxE != _footerSelMaxE ||
          minN != _footerSelMinN || maxN != _footerSelMaxN ||
          viewMinE != _footerViewMinE || viewMaxE != _footerViewMaxE ||
          viewMinN != _footerViewMinN || viewMaxN != _footerViewMaxN ||
          _footerSelectedCount < 0)
      {
        _footerSelMinE = minE;
        _footerSelMaxE = maxE;
        _footerSelMinN = minN;
        _footerSelMaxN = maxN;
        _footerViewMinE = viewMinE;
        _footerViewMaxE = viewMaxE;
        _footerViewMinN = viewMinN;
        _footerViewMaxN = viewMaxN;
        _footerSelectedCount = CountTilesInRegion(tiles, minE, maxE, minN, maxN);
        _footerPackedInView = CountPackedTilesInView(tiles, packedTileIds, viewMinE, viewMaxE, viewMinN, viewMaxN);
      }

      _selectedTileCount = _footerSelectedCount;

      TryGetDatasetTileBounds(out int geoMinE, out int geoMaxE, out int geoMinN, out int geoMaxN);
      GetBackgroundTextureGeorefBounds(out int bgMinE, out int bgMaxE, out int bgMinN, out int bgMaxN);
      bool backgroundDiffersFromGrid = BoundsDiffer(bgMinE, bgMaxE, bgMinN, bgMaxN, geoMinE, geoMaxE, geoMinN, geoMaxN);

      EditorGUILayout.LabelField(
        $"Selection: E {minE}–{maxE}   N {minN}–{maxN}",
        EditorStyles.miniLabel);
      EditorGUILayout.LabelField(
        $"Loaded tiles (grid): E {geoMinE}–{geoMaxE}, N {geoMinN}–{geoMaxN}",
        EditorStyles.miniLabel);
      if (backgroundDiffersFromGrid)
      {
        EditorGUILayout.LabelField(
          $"Background image: E {bgMinE}–{bgMaxE}, N {bgMinN}–{bgMaxN}",
          EditorStyles.miniLabel);
      }
      EditorGUILayout.LabelField(
        $"View: E {viewMinE}–{viewMaxE}, N {viewMinN}–{viewMaxN}",
        EditorStyles.miniLabel);

      int packedInView = _footerPackedInView;
      EditorGUILayout.LabelField(
        $"{_selectedTileCount} / {_totalTileCount} tiles in selection",
        EditorStyles.boldLabel);
      if (ZGConnectImportRegionBridge.GetPackedTileIds != null)
      {
        string packedLabel = ZGConnectImportRegionBridge.ApplyTargetLabel == "Spatial Streaming"
          ? $"{packedInView} already baked in spatial_manifest.json"
          : $"{packedInView} with streamable terrain in dataset";
        EditorGUILayout.LabelField(packedLabel, EditorStyles.miniLabel);
      }

      EditorGUILayout.BeginHorizontal();

      string applyLabel = string.IsNullOrEmpty(ZGConnectImportRegionBridge.ApplyTargetLabel)
        ? "Apply"
        : $"Apply to {ZGConnectImportRegionBridge.ApplyTargetLabel}";
      using (new EditorGUI.DisabledScope(_selectedTileCount == 0))
      {
        if (GUILayout.Button(applyLabel, GUILayout.Height(28)))
          ApplyToImporter();
      }

      if (GUILayout.Button("Cancel", GUILayout.Width(72), GUILayout.Height(28)))
        Close();

      EditorGUILayout.EndHorizontal();

      string legend = ZGConnectImportRegionBridge.AlignSelectionToPackGrid
        ? "LMB drag — select region (snaps to 4×4 tile grid). "
        : "LMB drag — select region (free rectangle). ";
      legend += "Wheel — zoom. MMB drag — pan. ";
      legend += "Dataset tiles frames the loaded tile grid; Full map frames the background image extent. ";
      if (backgroundDiffersFromGrid)
        legend += "Background = full source dataset; white grid = tiles in StreamingAssets; selection applies to loaded tiles only. ";
      if (ZGConnectImportRegionBridge.GetPackedTileIds != null)
      {
        legend += ZGConnectImportRegionBridge.ApplyTargetLabel == "Spatial Streaming"
          ? " Blue tiles are already in spatial_manifest.json."
          : " Blue tiles have streamable terrain on disk.";
      }

      EnsureMapStyles();
      EditorGUILayout.LabelField(legend, s_footerLegendStyle);
    }

    private int CountPackedTilesInView(
      List<HeightmapTileJson> tiles,
      HashSet<string> packedTileIds,
      int viewMinE,
      int viewMaxE,
      int viewMinN,
      int viewMaxN)
    {
      if (tiles == null || packedTileIds == null || packedTileIds.Count == 0)
        return 0;

      int count = 0;
      foreach (HeightmapTileJson t in tiles)
      {
        if (t.Right <= viewMinE || t.Left >= viewMaxE || t.Top <= viewMinN || t.Bottom >= viewMaxN)
          continue;

        if (packedTileIds.Contains($"{t.Left}_{t.Bottom}"))
          count++;
      }

      return count;
    }

    private void ApplyToImporter()
    {
      GetSelectionEpsg(out int minE, out int maxE, out int minN, out int maxN);
      ZGConnectImportRegionBridge.ApplyRegionEpsg?.Invoke(minE, maxE, minN, maxN);
      ZGConnectImportRegionBridge.RepaintImporter?.Invoke();
      string target = ZGConnectImportRegionBridge.ApplyTargetLabel ?? "target";
      ShowNotification(new GUIContent($"Region applied to {target}"));
    }

    private void SetSelectionEpsg(int minE, int maxE, int minN, int maxN)
    {
      if (maxE <= minE || maxN <= minN)
        return;

      if (ZGConnectImportRegionBridge.AlignSelectionToPackGrid)
      {
        EnsureDataCache();
        int tileSize = ResolveTileSizeMeters(_cachedTiles);
        HlodGridZones.AlignRegionEpsg(
          minE, maxE, minN, maxN, tileSize, HlodGridZones.PackRegionAlignFactor,
          out minE, out maxE, out minN, out maxN);
      }

      GetActiveExtent(out int extentMinE, out int extentMaxE, out int extentMinN, out int extentMaxN);
      minE = Mathf.Max(minE, extentMinE);
      maxE = Mathf.Min(maxE, extentMaxE);
      minN = Mathf.Max(minN, extentMinN);
      maxN = Mathf.Min(maxN, extentMaxN);

      if (minE >= maxE || minN >= maxN)
        return;

      _selMinE = minE;
      _selMaxE = maxE;
      _selMinN = minN;
      _selMaxN = maxN;
      _footerSelectedCount = -1;
      Repaint();
    }

    private static int ResolveTileSizeMeters(List<HeightmapTileJson> tiles)
    {
      if (tiles == null || tiles.Count == 0)
        return 1000;

      foreach (HeightmapTileJson tile in tiles)
      {
        int size = tile.Right - tile.Left;
        if (size > 0)
          return size;
      }

      return 1000;
    }

    private int _selMinE, _selMaxE, _selMinN, _selMaxN;

    private void SyncSelectionFromBridge()
    {
      if (ZGConnectImportRegionBridge.GetRegionEpsg == null) return;
      var (minE, maxE, minN, maxN) = ZGConnectImportRegionBridge.GetRegionEpsg();
      if (maxE <= minE || maxN <= minN) return;
      SetSelectionEpsg(minE, maxE, minN, maxN);
    }

    private void GetSelectionEpsg(out int minE, out int maxE, out int minN, out int maxN)
    {
      minE = _selMinE;
      maxE = _selMaxE;
      minN = _selMinN;
      maxN = _selMaxN;
    }

    private static int CountTilesInRegion(List<HeightmapTileJson> tiles,
      int minE, int maxE, int minN, int maxN)
    {
      if (tiles == null || maxE <= minE || maxN <= minN) return 0;
      int count = 0;
      foreach (var t in tiles)
      {
        if (t.Left < maxE && t.Right > minE &&
            t.Bottom < maxN && t.Top > minN)
          count++;
      }
      return count;
    }

    private Rect TileToGuiRect(HeightmapTileJson t, Rect mapRect) =>
      EpsgBoxToGuiRect(t.Left, t.Right, t.Bottom, t.Top, mapRect);

    private Rect EpsgBoxToGuiRect(int minE, int maxE, int minN, int maxN, Rect mapRect)
    {
      float xMin = EToGuiX(minE, mapRect);
      float xMax = EToGuiX(maxE, mapRect);
      float yMin = NToGuiY(maxN, mapRect);
      float yMax = NToGuiY(minN, mapRect);
      return Rect.MinMaxRect(
        Mathf.Round(Mathf.Min(xMin, xMax)),
        Mathf.Round(Mathf.Min(yMin, yMax)),
        Mathf.Round(Mathf.Max(xMin, xMax)),
        Mathf.Round(Mathf.Max(yMin, yMax)));
    }

    private float EToGuiX(double e, Rect mapRect)
    {
      double viewWidth = _viewMaxE - _viewMinE;
      if (viewWidth <= 0.0)
        return mapRect.x;

      double t = (e - _viewMinE) / viewWidth;
      return mapRect.x + (float)t * mapRect.width;
    }

    private float NToGuiY(double n, Rect mapRect)
    {
      double viewHeight = _viewMaxN - _viewMinN;
      if (viewHeight <= 0.0)
        return mapRect.y;

      double t = (n - _viewMinN) / viewHeight;
      return mapRect.yMax - (float)t * mapRect.height;
    }

    private void EpsgFromGui(Vector2 gui, Rect mapRect, out double e, out double n)
    {
      float u = (gui.x - mapRect.x) / mapRect.width;
      float v = (mapRect.yMax - gui.y) / mapRect.height;
      e = _viewMinE + u * (_viewMaxE - _viewMinE);
      n = _viewMinN + v * (_viewMaxN - _viewMinN);
    }

    static Texture2D LoadCustomMapFromPrefs()
    {
      string guid = EditorPrefs.GetString(kCustomMapGuidPrefKey, string.Empty);
      if (string.IsNullOrEmpty(guid))
        return null;

      string path = AssetDatabase.GUIDToAssetPath(guid);
      return string.IsNullOrEmpty(path)
        ? null
        : AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    static void SaveCustomMapToPrefs(Texture2D texture)
    {
      if (texture == null)
      {
        EditorPrefs.DeleteKey(kCustomMapGuidPrefKey);
        return;
      }

      string path = AssetDatabase.GetAssetPath(texture);
      string guid = string.IsNullOrEmpty(path) ? string.Empty : AssetDatabase.AssetPathToGUID(path);
      if (string.IsNullOrEmpty(guid))
        EditorPrefs.DeleteKey(kCustomMapGuidPrefKey);
      else
        EditorPrefs.SetString(kCustomMapGuidPrefKey, guid);
    }

    static void GetDatasetTileExtentFromTiles(
      List<HeightmapTileJson> tiles,
      out int minE,
      out int maxE,
      out int minN,
      out int maxN)
    {
      minE = ZGConnectMapExtent.MinE;
      maxE = ZGConnectMapExtent.MaxE;
      minN = ZGConnectMapExtent.MinN;
      maxN = ZGConnectMapExtent.MaxN;

      if (tiles == null || tiles.Count == 0)
        return;

      int tMinE = int.MaxValue;
      int tMaxE = int.MinValue;
      int tMinN = int.MaxValue;
      int tMaxN = int.MinValue;

      foreach (HeightmapTileJson t in tiles)
      {
        if (t.Left < tMinE) tMinE = t.Left;
        if (t.Right > tMaxE) tMaxE = t.Right;
        if (t.Bottom < tMinN) tMinN = t.Bottom;
        if (t.Top > tMaxN) tMaxN = t.Top;
      }

      if (tMaxE > tMinE && tMaxN > tMinN)
      {
        minE = tMinE;
        maxE = tMaxE;
        minN = tMinN;
        maxN = tMaxN;
      }
    }

    private static void DrawRectOutline(Rect r, Color col, float thickness)
    {
      EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, thickness), col);
      EditorGUI.DrawRect(new Rect(r.x, r.yMax - thickness, r.width, thickness), col);
      EditorGUI.DrawRect(new Rect(r.x, r.y, thickness, r.height), col);
      EditorGUI.DrawRect(new Rect(r.xMax - thickness, r.y, thickness, r.height), col);
    }
  }
}
