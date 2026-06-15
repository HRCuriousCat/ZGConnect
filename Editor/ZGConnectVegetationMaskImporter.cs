using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace ZGConnect.Editor
{
    public class VegetationMaskImportSettings
    {
        public CityDataset Dataset;
        public string      SourceFolder;
        public string      AssetFolder = "Assets/Generated/ZGConnect/VegetationMasks";
        public bool        SkipExisting = true;
        public bool        FilterByRegion;
        public int         RegionMinE;
        public int         RegionMaxE;
        public int         RegionMinN;
        public int         RegionMaxN;
    }

    public struct VegetationMaskImportResult
    {
        public int Assigned;
        public int Skipped;
        public int NoDatasetTile;
        public int RegionSkipped;
        public int UnrecognizedFiles;
        public bool Cancelled;
    }

    /// <summary>
    /// Copies per-tile vegetation mask PNGs into the Unity project and wires them to
    /// <see cref="CityTileRecord.vegetationMask"/> on the CityDataset.
    /// </summary>
    public static class ZGConnectVegetationMaskImporter
    {
        private static readonly Regex TileMaskName =
            new Regex(@"^(?<id>\d+_\d+)_vegetation\.png$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        [MenuItem("ZG Connect/Import Vegetation Masks")]
        public static void OpenDatasetManagerOnVegetationTab()
        {
            ZGConnectDatasetManagerWindow.ShowWindowOnTab(2);
        }

        public static VegetationMaskImportResult Import(VegetationMaskImportSettings settings)
        {
            var result = new VegetationMaskImportResult();

            if (settings?.Dataset == null || settings.Dataset.tiles == null)
                return result;

            if (string.IsNullOrWhiteSpace(settings.SourceFolder) || !Directory.Exists(settings.SourceFolder))
                return result;

            var tileById = new Dictionary<string, CityTileRecord>();
            foreach (CityTileRecord rec in settings.Dataset.tiles)
            {
                if (!string.IsNullOrEmpty(rec.tileId))
                    tileById[rec.tileId] = rec;
            }

            string[] pngs = Directory.GetFiles(settings.SourceFolder, "*_vegetation.png", SearchOption.TopDirectoryOnly);
            if (pngs.Length == 0)
                return result;

            string assetFolder = string.IsNullOrWhiteSpace(settings.AssetFolder)
                ? "Assets/Generated/ZGConnect/VegetationMasks"
                : settings.AssetFolder;

            ZGConnectPathUtils.EnsureAssetFolder(assetFolder);

            try
            {
                for (int i = 0; i < pngs.Length; i++)
                {
                    string fileName = Path.GetFileName(pngs[i]);
                    Match m = TileMaskName.Match(fileName);
                    if (!m.Success)
                    {
                        result.UnrecognizedFiles++;
                        continue;
                    }

                    string tileId = m.Groups["id"].Value;
                    if (!tileById.TryGetValue(tileId, out CityTileRecord record))
                    {
                        result.NoDatasetTile++;
                        continue;
                    }

                    if (settings.FilterByRegion && !TileIntersectsRegion(record, settings))
                    {
                        result.RegionSkipped++;
                        continue;
                    }

                    if (settings.SkipExisting && record.vegetationMask != null)
                    {
                        result.Skipped++;
                        continue;
                    }

                    if (EditorUtility.DisplayCancelableProgressBar(
                            "Vegetation masks",
                            tileId,
                            (float)i / pngs.Length))
                    {
                        result.Cancelled = true;
                        break;
                    }

                    string dstAssetPath = $"{assetFolder}/{fileName}";
                    string dstFull      = Path.GetFullPath(dstAssetPath);

                    File.Copy(pngs[i], dstFull, overwrite: true);
                    AssetDatabase.ImportAsset(dstAssetPath, ImportAssetOptions.ForceUpdate);
                    ConfigureMaskTextureImport(dstAssetPath);

                    Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(dstAssetPath);
                    if (tex == null)
                    {
                        Debug.LogWarning($"[ZGConnect] Could not load mask texture: {dstAssetPath}");
                        continue;
                    }

                    record.vegetationMask = tex;
                    result.Assigned++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (result.Assigned > 0)
            {
                EditorUtility.SetDirty(settings.Dataset);
                AssetDatabase.SaveAssets();
            }

            return result;
        }

        public static int CountMaskFiles(string sourceFolder)
        {
            if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder))
                return 0;
            return Directory.GetFiles(sourceFolder, "*_vegetation.png", SearchOption.TopDirectoryOnly).Length;
        }

        public static int CountDatasetTilesWithMask(CityDataset dataset)
        {
            if (dataset?.tiles == null) return 0;
            int n = 0;
            foreach (CityTileRecord rec in dataset.tiles)
                if (rec.vegetationMask != null) n++;
            return n;
        }

        private static bool TileIntersectsRegion(CityTileRecord rec, VegetationMaskImportSettings s) =>
            rec.left   < s.RegionMaxE &&
            rec.right  > s.RegionMinE &&
            rec.bottom < s.RegionMaxN &&
            rec.top    > s.RegionMinN;

        /// <summary>
        /// Linear, CPU-readable — required by <see cref="VegetationInstanceGenerator"/> at runtime.
        /// </summary>
        public static void ConfigureMaskTextureImport(string assetPath)
        {
            var imp = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (imp == null) return;

            imp.textureType        = TextureImporterType.Default;
            imp.sRGBTexture        = false;
            imp.mipmapEnabled      = false;
            imp.wrapMode           = TextureWrapMode.Clamp;
            imp.isReadable         = true;
            imp.textureCompression = TextureImporterCompression.Uncompressed;
            imp.maxTextureSize     = 4096;
            imp.SaveAndReimport();
        }
    }
}
