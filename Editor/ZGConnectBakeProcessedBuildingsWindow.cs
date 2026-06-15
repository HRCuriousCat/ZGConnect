using System.IO;
using UnityEditor;
using UnityEngine;

namespace ZGConnect.Editor
{
    /// <summary>
    /// Bakes facade/roof UVs into GLB files under {source}/Processed/.
    /// </summary>
    public class ZGConnectBakeProcessedBuildingsWindow : EditorWindow
    {
        private CityDataset _dataset;
        private string      _sourceFolder = "";
        private string      _outputFolder = "Assets/Generated/ZGConnect";
        private bool        _skipExisting = true;
        private bool        _filterByRegion;
        private BuildingSurfaceSettings _surfaceSettings;

        [MenuItem("ZG Connect/Buildings/Bake Processed GLBs")]
        public static void ShowWindow()
        {
            var w = GetWindow<ZGConnectBakeProcessedBuildingsWindow>("ZG Connect â€” Bake GLBs");
            w.minSize = new Vector2(420, 320);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Bake Processed Building GLBs", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Reads raw buildings_*.glb from the source folder, runs surface UV processing, " +
                $"and writes baked files to:\n  {{source}}/{BuildingSurfacePipeline.ProcessedFolderName}/",
                MessageType.Info);

            _dataset = (CityDataset)EditorGUILayout.ObjectField("City Dataset", _dataset, typeof(CityDataset), false);

            EditorGUILayout.BeginHorizontal();
            _sourceFolder = EditorGUILayout.TextField("Source Folder (raw GLBs)", _sourceFolder);
            if (GUILayout.Button("â€¦", GUILayout.Width(28)))
            {
                string picked = EditorUtility.OpenFolderPanel("Select raw buildings folder", _sourceFolder, "");
                if (!string.IsNullOrEmpty(picked))
                    _sourceFolder = picked;
            }
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_sourceFolder))
            {
                string processed = BuildingSurfacePipeline.GetProcessedFolder(_sourceFolder);
                EditorGUILayout.LabelField($"Output â†’ {processed}", EditorStyles.miniLabel);
            }

            _outputFolder = EditorGUILayout.TextField("Unity staging (import)", _outputFolder);
            _surfaceSettings = (BuildingSurfaceSettings)EditorGUILayout.ObjectField(
                "Surface Settings", _surfaceSettings, typeof(BuildingSurfaceSettings), false);
            _skipExisting   = EditorGUILayout.Toggle("Skip existing in Processed/", _skipExisting);
            _filterByRegion = EditorGUILayout.Toggle("Filter by region (from dataset window)", _filterByRegion);

            if (!ZGConnectGlbExportUtility.IsAvailable)
            {
                EditorGUILayout.HelpBox(
                    "UnityGLTF not found. Install com.khronos.unitygltf via Package Manager.",
                    MessageType.Warning);
            }

            using (new EditorGUI.DisabledScope(_dataset == null
                || string.IsNullOrEmpty(_sourceFolder)
                || _surfaceSettings == null
                || !Directory.Exists(_sourceFolder)))
            {
                if (GUILayout.Button("Bake Processed GLBs", GUILayout.Height(32)))
                    RunBake();
            }
        }

        private void RunBake()
        {
            var bake = new BuildingsBakeSettings
            {
                OutputFolder    = _outputFolder,
                SurfaceSettings = _surfaceSettings,
                SkipExisting    = _skipExisting,
            };

            if (_filterByRegion && ZGConnectImportRegionBridge.IsImporterReady)
            {
                var (minE, maxE, minN, maxN) = ZGConnectImportRegionBridge.GetRegionEpsg();
                bake.FilterByRegion = true;
                bake.RegionMinE = minE;
                bake.RegionMaxE = maxE;
                bake.RegionMinN = minN;
                bake.RegionMaxN = maxN;
            }

            try
            {
                ZGConnectImporter.BakeProcessedBuildings(_dataset, _sourceFolder, bake);
            }
            catch (System.Exception ex)
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("Bake Error", ex.Message, "OK");
            }
        }
    }
}

