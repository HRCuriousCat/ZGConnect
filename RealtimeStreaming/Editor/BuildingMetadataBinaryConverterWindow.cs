using System.IO;
using UnityEditor;
using UnityEngine;
using ZGConnect.Editor;

namespace ZGConnect.RealtimeStreaming.Editor
{
    public sealed class BuildingMetadataBinaryConverterWindow : EditorWindow
    {
        const string PrefsPrefix = "ZGConnect.BuildingMetadataBinary.";

        string _inputFolder = "";
        string _outputFolder = "";
        bool _incrementalSkip = true;
        bool _forceRebuild;
        string _lastReport = "";

        [MenuItem("ZG Connect/Convert Building Metadata to Binary")]
        public static void Open()
        {
            var window = GetWindow<BuildingMetadataBinaryConverterWindow>("Building Metadata Binary");
            window.minSize = new Vector2(460, 280);
        }

        void OnEnable() => LoadSettings();

        void OnDisable() => SaveSettings();

        void OnGUI()
        {
            ZGConnectEditorBranding.DrawWindowBackground(this);
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Building metadata JSON → .bytes", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Scans a folder for buildings_{tileId}.json files and writes parallel " +
                "buildings_{tileId}.bytes files for fast runtime loading. " +
                "Output defaults to a sibling folder with a _bin suffix.",
                MessageType.Info);

            EditorGUILayout.Space(4);
            DrawFolderField("Input JSON folder", ref _inputFolder, true);

            using (new EditorGUILayout.HorizontalScope())
            {
                DrawFolderField("Output .bytes folder", ref _outputFolder, false);
                if (GUILayout.Button("Use _bin", GUILayout.Width(72)))
                {
                    _outputFolder = string.IsNullOrEmpty(_inputFolder)
                        ? string.Empty
                        : BuildingMetadataPathUtility.GetBinaryFolderForJsonFolder(_inputFolder);
                }
            }

            _incrementalSkip = EditorGUILayout.ToggleLeft(
                "Skip when .bytes is newer than .json", _incrementalSkip);
            _forceRebuild = EditorGUILayout.ToggleLeft("Force rebuild all", _forceRebuild);

            EditorGUILayout.Space(8);
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_inputFolder) || !Directory.Exists(_inputFolder)))
            {
                if (GUILayout.Button("Convert Folder", GUILayout.Height(28)))
                    RunConversion();
            }

            if (!string.IsNullOrEmpty(_lastReport))
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Last run", EditorStyles.miniBoldLabel);
                EditorGUILayout.TextArea(_lastReport, GUILayout.MinHeight(72));
            }
        }

        static void DrawFolderField(string label, ref string folder, bool pickExisting)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                folder = EditorGUILayout.TextField(label, folder);
                if (GUILayout.Button("...", GUILayout.Width(28)))
                {
                    string picked = pickExisting
                        ? EditorUtility.OpenFolderPanel(label, folder, "")
                        : EditorUtility.SaveFolderPanel(label, folder, "");
                    if (!string.IsNullOrEmpty(picked))
                        folder = picked;
                }
            }
        }

        void RunConversion()
        {
            string output = string.IsNullOrEmpty(_outputFolder)
                ? BuildingMetadataPathUtility.GetBinaryFolderForJsonFolder(_inputFolder)
                : _outputFolder;

            BuildingMetadataBinaryConverterResult result = BuildingMetadataBinaryConverter.ConvertFolder(
                _inputFolder,
                output,
                _incrementalSkip,
                _forceRebuild);

            _outputFolder = output;
            _lastReport =
                $"Scanned: {result.Scanned}\n" +
                $"Converted: {result.Converted}\n" +
                $"Skipped: {result.Skipped}\n" +
                $"Failed: {result.Failed}";

            if (result.Errors.Count > 0)
            {
                _lastReport += "\n\nErrors:\n" + string.Join("\n", result.Errors);
                Debug.LogWarning($"[ZGConnect.Realtime] Building metadata binary conversion completed with {result.Failed} failure(s).");
            }
            else
            {
                Debug.Log(
                    $"[ZGConnect.Realtime] Building metadata binary conversion complete. " +
                    $"Converted {result.Converted}, skipped {result.Skipped}. Output: {output}");
            }

            Repaint();
        }

        void LoadSettings()
        {
            _inputFolder = EditorPrefs.GetString(PrefsPrefix + "InputFolder", _inputFolder);
            _outputFolder = EditorPrefs.GetString(PrefsPrefix + "OutputFolder", _outputFolder);
            _incrementalSkip = EditorPrefs.GetBool(PrefsPrefix + "IncrementalSkip", true);
            _forceRebuild = EditorPrefs.GetBool(PrefsPrefix + "ForceRebuild", false);
        }

        void SaveSettings()
        {
            EditorPrefs.SetString(PrefsPrefix + "InputFolder", _inputFolder ?? string.Empty);
            EditorPrefs.SetString(PrefsPrefix + "OutputFolder", _outputFolder ?? string.Empty);
            EditorPrefs.SetBool(PrefsPrefix + "IncrementalSkip", _incrementalSkip);
            EditorPrefs.SetBool(PrefsPrefix + "ForceRebuild", _forceRebuild);
        }
    }
}
