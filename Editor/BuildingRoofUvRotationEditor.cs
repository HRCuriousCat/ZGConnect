using System.IO;
using UnityEditor;
using UnityEngine;

namespace ZGConnect.Editor
{
    [CustomEditor(typeof(BuildingRoofUvRotation))]
    public class BuildingRoofUvRotationEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var rot = (BuildingRoofUvRotation)target;

            EditorGUILayout.HelpBox(
                "Adjusts roof UVs only (submeshes 1+ after surface processing, or upward-facing triangles). " +
                "Facade UVs are not changed. Order: tiling → rotation → offset.",
                MessageType.Info);

            serializedObject.Update();

            EditorGUI.BeginChangeCheck();
            DrawSliderPair("Rotation", "roofUvRotationDegrees", "roofUvRotationFine");
            DrawSliderPair("Offset X", "roofUvOffsetX", "roofUvOffsetXFine");
            DrawSliderPair("Offset Y", "roofUvOffsetY", "roofUvOffsetYFine");
            DrawSliderPair("Tiling", "roofUvTiling", "roofUvTilingFine");
            if (EditorGUI.EndChangeCheck())
            {
                MeshFilter mf = rot.GetComponentInChildren<MeshFilter>(true);
                if (mf != null && mf.sharedMesh != null)
                    Undo.RecordObject(mf.sharedMesh, "Roof UV");
                Undo.RecordObject(rot, "Roof UV");
            }

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Save", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Processed export: mesh + UV in GLB; material slot keys (house_facade, roof_flat, â€¦) in JSON. " +
                "No textures embedded. Prefer Processed/ for import.",
                MessageType.None);

            string tileId = ZGConnectBuildingGlbSave.ResolveTileId(rot.transform);
            string savedRoot = EditorPrefs.GetString("ZGConnect.Importer.RootFolder", "").Trim();
            if (!string.IsNullOrEmpty(savedRoot))
            {
                EditorGUILayout.LabelField("Dataset root (saved)", savedRoot, EditorStyles.miniLabel);
                if (!string.IsNullOrEmpty(tileId))
                {
                    string preview = ZGConnectBuildingGlbSave.TryGetDefaultProcessedGlbPath(tileId);
                    if (!string.IsNullOrEmpty(preview))
                        EditorGUILayout.LabelField("Processed GLB path", preview, EditorStyles.miniLabel);
                }
            }

            if (string.IsNullOrEmpty(tileId))
            {
                EditorGUILayout.HelpBox(
                    "Tile id unknown — add BuildingData.tileId on this building, or place it under TileBuildings_{tileId}.",
                    MessageType.Warning);
            }

            using (new EditorGUI.DisabledScope(!ZGConnectGlbExportUtility.IsAvailable))
            {
                if (GUILayout.Button("Save tile GLB → Processed folder"))
                {
                    if (!ZGConnectBuildingGlbSave.TryGetDefaultProcessedGlbPath(tileId, out string path, out string err))
                    {
                        EditorUtility.DisplayDialog("ZG Connect — Cannot resolve path", err, "OK");
                    }
                    else if (EditorUtility.DisplayDialog(
                        "Overwrite Processed GLB?",
                        $"Write:\n{path}",
                        "Save",
                        "Cancel"))
                    {
                        ZGConnectBuildingGlbSave.ExportFromBuildingRoofUvComponent(rot, path);
                    }
                }

                if (GUILayout.Button("Save tile GLB â†’ raw building_meshes (with materials)"))
                {
                    if (!ZGConnectBuildingGlbSave.TryGetDefaultRawGlbPath(tileId, out string path, out string err))
                    {
                        EditorUtility.DisplayDialog("ZG Connect — Cannot resolve path", err, "OK");
                    }
                    else if (EditorUtility.DisplayDialog(
                        "Overwrite raw GLB?",
                        $"This replaces the original export:\n{path}",
                        "Overwrite",
                        "Cancel"))
                    {
                        ZGConnectBuildingGlbSave.ExportFromBuildingRoofUvComponent(rot, path, geometryOnly: false);
                    }
                }

                if (GUILayout.Button("Save tile GLB to file…"))
                {
                    string defaultName = string.IsNullOrEmpty(tileId)
                        ? "buildings_tile.glb"
                        : $"buildings_{tileId}.glb";
                    string path = EditorUtility.SaveFilePanel(
                        "Save tile GLB",
                        string.IsNullOrEmpty(tileId) ? "" : Path.GetDirectoryName(
                            ZGConnectBuildingGlbSave.TryGetDefaultProcessedGlbPath(tileId) ?? ""),
                        defaultName,
                        "glb");
                    if (!string.IsNullOrEmpty(path))
                        ZGConnectBuildingGlbSave.ExportFromBuildingRoofUvComponent(rot, path);
                }

                if (GUILayout.Button("Bake UV meshes into prefab asset"))
                {
                    GameObject tileRoot = ZGConnectBuildingGlbSave.FindTileBuildingsRoot(rot.transform);
                    if (tileRoot != null)
                        ZGConnectBuildingGlbSave.BakeUvMeshesIntoPrefab(tileRoot);
                }
            }

            EditorGUILayout.Space(4);
            if (GUILayout.Button("Refresh mesh cache"))
                rot.RebuildMeshCache();
        }

        void DrawSliderPair(string label, string mainProperty, string fineProperty)
        {
            EditorGUILayout.PropertyField(
                serializedObject.FindProperty(mainProperty),
                new GUIContent(label));

            EditorGUILayout.PropertyField(
                serializedObject.FindProperty(fineProperty),
                new GUIContent($"{label} (fine)", "Small adjustment added on top of the main slider."));
        }
    }
}
