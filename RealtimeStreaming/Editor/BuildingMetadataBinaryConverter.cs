using System;
using System.Collections.Generic;
using System.IO;

namespace ZGConnect.RealtimeStreaming.Editor
{
    public sealed class BuildingMetadataBinaryConverterResult
    {
        public int Scanned;
        public int Converted;
        public int Skipped;
        public int Failed;
        public readonly List<string> Errors = new();
    }

    public static class BuildingMetadataBinaryConverter
    {
        public static BuildingMetadataBinaryConverterResult ConvertFolder(
            string inputFolder,
            string outputFolder,
            bool incrementalSkipNewer,
            bool forceRebuild)
        {
            var result = new BuildingMetadataBinaryConverterResult();
            if (string.IsNullOrEmpty(inputFolder) || !Directory.Exists(inputFolder))
            {
                result.Errors.Add($"Input folder not found: {inputFolder}");
                return result;
            }

            if (string.IsNullOrEmpty(outputFolder))
                outputFolder = BuildingMetadataPathUtility.GetBinaryFolderForJsonFolder(inputFolder);

            Directory.CreateDirectory(outputFolder);

            foreach (string jsonPath in Directory.EnumerateFiles(inputFolder, "buildings_*.json", SearchOption.TopDirectoryOnly))
            {
                result.Scanned++;
                string fileName = Path.GetFileNameWithoutExtension(jsonPath);
                if (!fileName.StartsWith(BuildingMetadataPathUtility.MetadataFilePrefix, StringComparison.Ordinal))
                    continue;

                string bytesPath = Path.Combine(outputFolder, fileName + ".bytes");

                if (!forceRebuild &&
                    incrementalSkipNewer &&
                    File.Exists(bytesPath) &&
                    File.GetLastWriteTimeUtc(bytesPath) >= File.GetLastWriteTimeUtc(jsonPath))
                {
                    result.Skipped++;
                    continue;
                }

                if (BuildingMetadataBinaryWriter.TryConvertJsonFileToBytes(jsonPath, bytesPath, out string error))
                {
                    result.Converted++;
                }
                else
                {
                    result.Failed++;
                    result.Errors.Add($"{Path.GetFileName(jsonPath)}: {error}");
                }
            }

            return result;
        }
    }
}
