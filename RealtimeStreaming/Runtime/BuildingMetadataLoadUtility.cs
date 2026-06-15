using System;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using ZGConnect;

namespace ZGConnect.RealtimeStreaming
{
    public static class BuildingMetadataLoadUtility
    {
        public static bool TryLoad(
            string jsonPath,
            string bytesPath,
            BuildingMetadataSourceMode mode,
            out BuildingsMetadataJson metadata,
            out string error)
        {
            metadata = null;
            error = null;

            switch (mode)
            {
                case BuildingMetadataSourceMode.BinaryOnly:
                    return TryLoadBinary(bytesPath, out metadata, out error);

                case BuildingMetadataSourceMode.JsonOnly:
                    return TryLoadJson(jsonPath, out metadata, out error);

                case BuildingMetadataSourceMode.PreferBinary:
                default:
                    if (!string.IsNullOrEmpty(bytesPath) && File.Exists(bytesPath))
                        return TryLoadBinary(bytesPath, out metadata, out error);

                    if (!string.IsNullOrEmpty(jsonPath) && File.Exists(jsonPath))
                    {
                        bool ok = TryLoadJson(jsonPath, out metadata, out error);
                        if (ok)
                        {
                            Debug.LogWarning(
                                $"[ZGConnect.Realtime] Binary metadata missing; using JSON fallback: {jsonPath}");
                        }

                        return ok;
                    }

                    error = $"No building metadata found. Binary: {bytesPath}, JSON: {jsonPath}";
                    return false;
            }
        }

        public static bool TryLoadForTile(
            string datasetRoot,
            string tileId,
            bool orthoRoofStyle,
            BuildingMetadataSourceMode mode,
            out BuildingsMetadataJson metadata,
            out string error)
        {
            BuildingMetadataPathUtility.ResolveMetadataPaths(
                datasetRoot, tileId, orthoRoofStyle, out string jsonPath, out string bytesPath);
            return TryLoad(jsonPath, bytesPath, mode, out metadata, out error);
        }

        static bool TryLoadBinary(string bytesPath, out BuildingsMetadataJson metadata, out string error) =>
            BuildingMetadataBinaryLoader.TryLoadFromFile(bytesPath, out metadata, out error);

        static bool TryLoadJson(string jsonPath, out BuildingsMetadataJson metadata, out string error)
        {
            metadata = null;
            error = null;

            if (string.IsNullOrEmpty(jsonPath) || !File.Exists(jsonPath))
            {
                error = $"JSON metadata not found: {jsonPath}";
                return false;
            }

            try
            {
                metadata = JsonConvert.DeserializeObject<BuildingsMetadataJson>(File.ReadAllText(jsonPath));
                if (metadata?.Buildings == null || metadata.Buildings.Count == 0)
                {
                    error = $"No buildings in JSON metadata: {jsonPath}";
                    metadata = null;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
