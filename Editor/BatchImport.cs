using System.IO;
using UnityEditor;
using UnityEngine;

public static class BatchImport
{
    [MenuItem("ZG Connect/Import Building Tiles")]
    static void ImportBuildingTiles()
    {
        string sourceFolder = EditorUtility.OpenFolderPanel(
            "Select folder with GLB + JSON files", "", "");

        if (string.IsNullOrEmpty(sourceFolder)) return;

        string destFolder = "Assets/Buildings";
        Directory.CreateDirectory(destFolder);

        var files = Directory.GetFiles(sourceFolder, "buildings_*.*");

        // Tell Unity: stop watching for changes, we'll trigger import manually
        AssetDatabase.StartAssetEditing();
        
        try
        {
            int i = 0;
            foreach (var file in files)
            {
                string dest = Path.Combine(destFolder, Path.GetFileName(file));
                File.Copy(file, dest, overwrite: true);
                
                EditorUtility.DisplayProgressBar(
                    "Copying files...",
                    Path.GetFileName(file),
                    (float)i++ / files.Length);
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            // Now trigger ONE batch import of everything
            AssetDatabase.StopAssetEditing();
            AssetDatabase.Refresh();
        }
    }
}
