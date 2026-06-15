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
    private ZGConnectMapGeorefBounds _cachedOverviewBounds;
    private bool _hasCachedOverviewBounds;
    private bool _dataCacheDirty = true;

    private static readonly Color kTileOutline         = new Color(1f, 1f, 1f, 0.35f);
    private static readonly Color kTileInvalid         = new Color(0.9f, 0.25f, 0.2f, 0.25f);
    private static readonly Color kTilePacked          = new Color(0.15f, 0.55f, 0.82f, 0.42f);
    private static readonly Color kTilePackedInSelection = new Color(0.25f, 0.68f, 0.95f, 0.52f);
    private static readonly Color kTileInSelection     = new Color(0.2f, 0.85f, 0.35f, 0.45f);
    private static readonly Color kSelectionOutline    = new Color(1f, 0.92f, 0.2f, 0.95f);
    private static readonly Color kSelectionFill       = new Color(1f, 0.92f, 0.2f, 0.18f);

    const string kCustomMapGuidPrefKey = "ZGConnect.TileMap.CustomMapGuid";
    const float kFooterReservedHeight = 200f;
    const float kMapMinHeight = 160f;

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
      FrameFullMapExtent();
      Repaint();
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
        GetOverviewGeorefBounds(out int extentMinE, out int extentMaxE, out int extentMinN, out int extentMaxN);
        SetSelectionEpsg(extentMinE, extentMaxE, extentMinN, extentMaxN);
      }

      FrameFullMapExtent();
    }

    private void ResetView() => FrameFullMapExtent();

    private void FrameFullMapExtent()
    {
      GetOverviewGeorefBounds(out int minE, out int maxE, out int minN, out int maxN);
      SetViewEpsg(minE, maxE, minN, maxN);
    }

    private void SetViewEpsg(int minE, int maxE, int minN, int maxN)
    {
      if (maxE <= minE || maxN <= minN)
        return;

      if (_viewMinE == minE && _viewMaxE == maxE && _viewMinN == minN && _viewMaxN == maxN)
        return;

      _viewMinE = minE;
      _viewMaxE = maxE;
      _viewMinN = minN;
      _viewMaxN = maxN;
      Repaint();
    }

    private void OnGUI()
    {
      if (!ZGConnectImportRegionBridge.IsImporterReady)
      {
        EditorGUILayout.HelpBox(
          "Open an importer (RealTime Asset Importer or Dataset Import Manager) and scan a dataset first.",
          MessageType.Warning);
        if (GUILayout.Button("Open RealTime Asset Importer"))
          ZGConnect.RealtimeStreaming.Editor.ZGConnectRealtimeStreamerWindow.Open();
        if (GUILayout.Button("Open Dataset Import Manager"))
          ZGConnectDatasetManagerWindow.ShowWindow();
        return;
      }

      EnsureDataCache();
      _totalTileCount = _cachedTiles?.Count ?? 0;

      EditorGUILayout.Space(2);
      DrawMapArea(_cachedTiles, _cachedPackedTileIds);
      EditorGUILayout.Space(4);
      DrawFooter(_cachedTiles, _cachedPackedTileIds);
    }

    void InvalidateDataCache() => _dataCacheDirty = true;

    void EnsureDataCache()
    {
      if (!_dataCacheDirty)
        return;

      _cachedTiles = ZGConnectImportRegionBridge.GetTiles?.Invoke() ?? new List<HeightmapTileJson>();
      _cachedPackedTileIds = ZGConnectImportRegionBridge.GetPackedTileIds?.Invoke();
      _hasCachedOverviewBounds = TryResolveOverviewGeorefBounds(out _cachedOverviewBounds);
      _dataCacheDirty = false;
    }

    private Texture2D ActiveMap => _customMap != null ? _customMap : _mapTexture;

    private void DrawMapArea(List<HeightmapTileJson> tiles, HashSet<string> packedTileIds)
    {
      float mapHeight = Mathf.Max(kMapMinHeight, position.height - kFooterReservedHeight);
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
    /// Draws the overview texture cropped to the current EPSG view.
    /// UV mapping uses the texture's authored georef (city overview extent), not the
    /// heightmap tile union — otherwise the tile grid drifts from the map image.
    /// North-up: v=0 south, v=1 north.
    /// </summary>
    private void DrawMapTexture(Rect mapRect, Texture2D tex)
    {
      GetTextureGeorefBounds(out int texMinE, out int texMaxE, out int texMinN, out int texMaxN);

      Rect uv = ViewToMapUvRect(
        _viewMinE, _viewMaxE, _viewMinN, _viewMaxN,
        texMinE, texMaxE, texMinN, texMaxN);
      if (uv.width <= 0f || uv.height <= 0f)
        return;

      GUI.DrawTextureWithTexCoords(mapRect, tex, uv, true);
    }

    /// <summary>
    /// EPSG bounds covered by the overview image pixels (placeholder + default custom maps).
    /// </summary>
    static void GetTextureGeorefBounds(out int minE, out int maxE, out int minN, out int maxN) =>
      ZGConnectMapExtent.GetCityBoundingBox(out minE, out maxE, out minN, out maxN);

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
      var style = new GUIStyle(EditorStyles.miniLabel)
      {
        normal = { textColor = new Color(1f, 1f, 1f, 0.85f) },
        padding = new RectOffset(4, 4, 2, 2)
      };

      GUI.Label(new Rect(mapRect.x + 4, mapRect.y + 4, 120, 18), "N ↑", style);
      GUI.Label(new Rect(mapRect.xMax - 130, mapRect.yMax - 20, 126, 18),
        "EPSG:3765", style);
    }

    private void HandleMapInput(Rect mapRect)
    {
      var e = Event.current;
      if (!mapRect.Contains(e.mousePosition))
        return;

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
      GetSelectionEpsg(out int selMinE, out int selMaxE, out int selMinN, out int selMaxN);

      foreach (var t in tiles)
      {
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
      _selectedTileCount = CountTilesInRegion(tiles, minE, maxE, minN, maxN);

      GetOverviewGeorefBounds(out int geoMinE, out int geoMaxE, out int geoMinN, out int geoMaxN);

      EditorGUILayout.LabelField(
        $"Selection: E {minE}–{maxE}   N {minN}–{maxN}",
        EditorStyles.miniLabel);
      EditorGUILayout.LabelField(
        $"Map: E {geoMinE}–{geoMaxE}, N {geoMinN}–{geoMaxN} (heightmap coverage)",
        EditorStyles.miniLabel);

      int packedInView = CountPackedTilesInView(tiles, packedTileIds);
      EditorGUILayout.LabelField(
        $"{_selectedTileCount} / {_totalTileCount} tiles in selection",
        EditorStyles.boldLabel);
      if (ZGConnectImportRegionBridge.GetPackedTileIds != null)
        EditorGUILayout.LabelField($"{packedInView} with streamable terrain in dataset", EditorStyles.miniLabel);

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
        ? "LMB drag — select region (snaps to 4×4 tile grid). View shows the full overview map."
        : "LMB drag — select region (free rectangle). View shows the full overview map.";
      if (ZGConnectImportRegionBridge.GetPackedTileIds != null)
        legend += " Blue tiles have streamable terrain on disk (heightmap or bundle).";

      var legendStyle = new GUIStyle(EditorStyles.wordWrappedMiniLabel)
      {
        wordWrap = true,
        padding = new RectOffset(2, 2, 2, 4),
      };
      EditorGUILayout.LabelField(legend, legendStyle);
    }

    private int CountPackedTilesInView(List<HeightmapTileJson> tiles, HashSet<string> packedTileIds)
    {
      if (tiles == null || packedTileIds == null || packedTileIds.Count == 0)
        return 0;

      int count = 0;
      foreach (HeightmapTileJson t in tiles)
      {
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

    void GetOverviewGeorefBounds(out int minE, out int maxE, out int minN, out int maxN)
    {
      EnsureDataCache();
      ZGConnectMapExtent.GetHeightmapCoverageGeoref(
        _hasCachedOverviewBounds ? _cachedOverviewBounds : default,
        out minE, out maxE, out minN, out maxN);
    }

    bool TryResolveOverviewGeorefBounds(out ZGConnectMapGeorefBounds bounds)
    {
      bounds = default;
      if (ZGConnectImportRegionBridge.GetOverviewGeorefBounds != null)
      {
        var (minE, maxE, minN, maxN) = ZGConnectImportRegionBridge.GetOverviewGeorefBounds();
        if (maxE > minE && maxN > minN)
        {
          bounds = new ZGConnectMapGeorefBounds
          {
            MinE = minE,
            MaxE = maxE,
            MinN = minN,
            MaxN = maxN,
          };
          return true;
        }
      }

      GetDatasetTileExtentFromTiles(_cachedTiles, out int fallbackMinE, out int fallbackMaxE, out int fallbackMinN, out int fallbackMaxN);
      if (fallbackMaxE <= fallbackMinE || fallbackMaxN <= fallbackMinN)
        return false;

      bounds = new ZGConnectMapGeorefBounds
      {
        MinE = fallbackMinE,
        MaxE = fallbackMaxE,
        MinN = fallbackMinN,
        MaxN = fallbackMaxN,
      };
      return true;
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
