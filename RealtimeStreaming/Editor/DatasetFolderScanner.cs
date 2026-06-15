using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using ZGConnect;
using ZGConnect.Editor;

namespace ZGConnect.RealtimeStreaming.Editor
{
    public class DatasetFolderScanner
    {
        public class HeightmapSource
        {
            public string FolderPath;
            public string DisplayName;
            public HeightmapMetadataJson Metadata;
        }

        public class BasemapSource
        {
            public string FolderPath;
            public string DisplayName;
            public BasemapType Type;
            public OrthoMetadataJson Metadata;
            public TiledMetadataJson TiledMetadata;
        }

        public class ScanResult
        {
            public List<HeightmapSource> HeightmapSources = new();
            public List<BasemapSource> BasemapSources = new();
            public string VegetationMasksFolder;
            public string Error;
            public bool Success;
        }

        public static ScanResult Scan(string rootFolder)
        {
            var result = new ScanResult();
            if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder))
            {
                result.Error = "Root folder not found.";
                return result;
            }

            foreach (string folder in Directory.GetDirectories(rootFolder))
            {
                string metaPath = Path.Combine(folder, "metadata.json");
                if (!File.Exists(metaPath))
                    continue;

                string json = File.ReadAllText(metaPath);
                string folderName = Path.GetFileName(folder);

                if (TryParseHeightmap(json, folderName, out HeightmapSource hm))
                {
                    hm.FolderPath = folder;
                    result.HeightmapSources.Add(hm);
                    continue;
                }

                if (TryParseOrthoBasemap(json, folderName, out BasemapSource bm))
                {
                    bm.FolderPath = folder;
                    result.BasemapSources.Add(bm);
                    continue;
                }

                if (TryParseTiledBasemap(json, folderName, out BasemapSource tiledBm))
                {
                    tiledBm.FolderPath = folder;
                    result.BasemapSources.Add(tiledBm);
                }
            }

            string vegCandidate = Path.Combine(rootFolder, "vegetation_masks");
            if (Directory.Exists(vegCandidate))
                result.VegetationMasksFolder = vegCandidate;

            if (result.HeightmapSources.Count == 0)
                result.Error = "No heightmap source found in subfolders.";
            else
                result.Success = true;

            return result;
        }

        static bool TryParseHeightmap(string json, string folderName, out HeightmapSource result)
        {
            result = null;
            try
            {
                var meta = JsonConvert.DeserializeObject<HeightmapMetadataJson>(json);
                if (meta?.Settings?.Resolution > 0 && meta.Tiles != null)
                {
                    result = new HeightmapSource
                    {
                        DisplayName = $"{folderName}  ({meta.Settings.Resolution} px Â· {meta.Tiles.Count} tiles)",
                        Metadata = meta,
                    };
                    return true;
                }
            }
            catch { }
            return false;
        }

        static bool TryParseOrthoBasemap(string json, string folderName, out BasemapSource result)
        {
            result = null;
            try
            {
                var meta = JsonConvert.DeserializeObject<OrthoMetadataJson>(json);
                if (meta?.Settings?.TextureResolution > 0 && meta.Tiles != null)
                {
                    result = new BasemapSource
                    {
                        DisplayName = $"{folderName}  [Ortho Â· {meta.Settings.TextureResolution} px Â· {meta.Tiles.Count} tiles]",
                        Type = BasemapType.Ortho,
                        Metadata = meta,
                    };
                    return true;
                }
            }
            catch { }
            return false;
        }

        static bool TryParseTiledBasemap(string json, string folderName, out BasemapSource result)
        {
            result = null;
            try
            {
                var meta = JsonConvert.DeserializeObject<TiledMetadataJson>(json);
                if (meta?.Layers != null && meta.Layers.Count > 0 && meta.Tiles != null)
                {
                    result = new BasemapSource
                    {
                        DisplayName = $"{folderName}  [Tiled Â· {meta.Layers.Count} layers Â· {meta.Tiles.Count} tiles]",
                        Type = BasemapType.Tiled,
                        TiledMetadata = meta,
                    };
                    return true;
                }
            }
            catch { }
            return false;
        }
    }
}

