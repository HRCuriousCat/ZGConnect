using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace ZGConnect.Editor
{
    /// <summary>
    /// Custom inspector for <see cref="ZGConnectToolkit"/>: live play-mode dashboard
    /// (camera GPS, tiles, buildings, streaming status) plus quick debug actions.
    /// </summary>
    [CustomEditor(typeof(ZGConnectToolkit))]
    public class ZGConnectToolkitEditor : UnityEditor.Editor
    {
        static double _teleportLat = 45.8131;   // Trg bana Jelačića
        static double _teleportLon = 15.9772;
        static float _logRadius = 250f;

        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var toolkit = (ZGConnectToolkit)target;

            EditorGUILayout.Space(10);
            DrawDashboard(toolkit);

            EditorGUILayout.Space(10);
            DrawDebugTools(toolkit);
        }

        // ── Live dashboard ─────────────────────────────────────────────────────

        void DrawDashboard(ZGConnectToolkit toolkit)
        {
            EditorGUILayout.LabelField("Live Dashboard", EditorStyles.boldLabel);

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play mode to see live camera, tile and building info.", MessageType.Info);
                return;
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                Camera cam = toolkit.ReferenceCamera;
                if (cam == null)
                {
                    EditorGUILayout.LabelField("No camera found.");
                    return;
                }

                Vector3 camPos = cam.transform.position;
                (double lat, double lon) = ZGConnectToolkit.WorldToGps(camPos);

                ReadOnlyRow("Camera GPS", ZGConnectToolkit.FormatGps(lat, lon));
                ReadOnlyRow("Elevation",
                    toolkit.TryGetGroundElevation(camPos, out float elev)
                        ? $"{elev:F1} m a.s.l. (ground)  |  camera {ZGConnectToolkit.UnityYToElevation(camPos.y):F1} m"
                        : $"camera {ZGConnectToolkit.UnityYToElevation(camPos.y):F1} m (no terrain below)");
                ReadOnlyRow("Tile", $"{toolkit.GetTileIdAt(camPos)}  ({(toolkit.IsTileLoadedAt(camPos) ? "loaded" : "not loaded")})");
                ReadOnlyRow("Inside dataset", toolkit.IsInsideDataset(camPos) ? "yes" : "no");

                EditorGUILayout.Space(4);
                ReadOnlyRow("Streaming", toolkit.IsStreaming ? "active" : "inactive");
                ReadOnlyRow("Terrain tiles", toolkit.LoadedTerrainTileCount.ToString());
                ReadOnlyRow("Building tiles", toolkit.LoadedBuildingTileCount.ToString());
                ReadOnlyRow("Initial load", $"{toolkit.GetInitialLoadProgress01() * 100f:F0}%  {toolkit.GetLoadStatusText()}");
                ReadOnlyRow("Highlights", toolkit.HighlightedBuildingCount.ToString());

                ZGBuildingHandle hovered = toolkit.GetBuildingUnderCursor();
                ReadOnlyRow("Under cursor", hovered != null ? hovered.GetSummary() : "—");
            }
        }

        static void ReadOnlyRow(string label, string value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(110));
                EditorGUILayout.SelectableLabel(value, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
        }

        // ── Debug tools ────────────────────────────────────────────────────────

        void DrawDebugTools(ZGConnectToolkit toolkit)
        {
            EditorGUILayout.LabelField("Debug Tools", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                // Teleport row
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Lat / Lon", GUILayout.Width(70));
                    _teleportLat = EditorGUILayout.DoubleField(_teleportLat);
                    _teleportLon = EditorGUILayout.DoubleField(_teleportLon);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Teleport"))
                        toolkit.TeleportToGps(_teleportLat, _teleportLon);
                    if (GUILayout.Button("Fly To"))
                        toolkit.FlyToGps(_teleportLat, _teleportLon);
                    if (GUILayout.Button("Prewarm"))
                        toolkit.PrewarmGps(_teleportLat, _teleportLon);
                }

                EditorGUILayout.Space(4);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Copy Camera GPS"))
                        CopyCameraGps(toolkit);
                    if (GUILayout.Button("Open in Google Maps"))
                        OpenCameraInMaps(toolkit);
                }

                EditorGUILayout.Space(4);

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Radius (m)", GUILayout.Width(70));
                    _logRadius = EditorGUILayout.FloatField(_logRadius);
                    if (GUILayout.Button("Log Buildings In Radius"))
                        LogBuildingsInRadius(toolkit, _logRadius);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Log Loaded Tiles"))
                        LogLoadedTiles(toolkit);
                    if (GUILayout.Button("Clear Highlights"))
                        toolkit.ClearAllHighlights();
                }
            }

            if (!Application.isPlaying)
                EditorGUILayout.HelpBox("Debug tools are available in Play mode.", MessageType.None);
        }

        static void CopyCameraGps(ZGConnectToolkit toolkit)
        {
            Camera cam = toolkit.ReferenceCamera;
            if (cam == null)
                return;

            (double lat, double lon) = ZGConnectToolkit.WorldToGps(cam.transform.position);
            string text = string.Format(CultureInfo.InvariantCulture, "{0:F7}, {1:F7}", lat, lon);
            EditorGUIUtility.systemCopyBuffer = text;
            Debug.Log($"[ZGConnect] Copied camera GPS to clipboard: {text}");
        }

        static void OpenCameraInMaps(ZGConnectToolkit toolkit)
        {
            Camera cam = toolkit.ReferenceCamera;
            if (cam != null)
                Application.OpenURL(ZGConnectToolkit.GetGoogleMapsUrl(cam.transform.position));
        }

        static void LogBuildingsInRadius(ZGConnectToolkit toolkit, float radius)
        {
            Camera cam = toolkit.ReferenceCamera;
            if (cam == null)
                return;

            List<ZGBuildingHandle> buildings = toolkit.GetBuildingsInRadius(cam.transform.position, radius);
            Debug.Log($"[ZGConnect] {buildings.Count} buildings within {radius:F0} m of the camera:");
            foreach (ZGBuildingHandle building in buildings)
                Debug.Log($"  {building.GetSummary()}", building.Transform);
        }

        static void LogLoadedTiles(ZGConnectToolkit toolkit)
        {
            List<string> ids = toolkit.GetLoadedTerrainTileIds();
            Debug.Log($"[ZGConnect] {ids.Count} loaded terrain tiles: {string.Join(", ", ids)}");
        }
    }
}
