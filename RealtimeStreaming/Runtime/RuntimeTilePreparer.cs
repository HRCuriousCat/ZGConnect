using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace ZGConnect.RealtimeStreaming
{
    public sealed class RuntimeTilePrepareResult
    {
        public bool Success;
        public string Error;
        public float[,] Heights;
        public int HeightmapRes;
        public Vector3 TerrainSize;
        public byte[] OrthoPngBytes;
    }

    /// <summary>
    /// Off-main-thread tile preparation: disk I/O and CPU decode only.
    /// Unity objects (TerrainData, Texture2D, GameObject) must still be created on the main thread.
    /// </summary>
    public static class RuntimeTilePreparer
    {
        public static Task<RuntimeTilePrepareResult> PrepareAsync(
            string heightmapPath,
            int heightmapRes,
            Vector3 terrainSize,
            bool flipHeightmap,
            string orthoPathOrNull)
        {
            return Task.Run(() =>
            {
                var result = new RuntimeTilePrepareResult
                {
                    TerrainSize = terrainSize,
                    HeightmapRes = heightmapRes,
                };

                try
                {
                    if (string.IsNullOrEmpty(heightmapPath) || !File.Exists(heightmapPath))
                    {
                        result.Error = $"Missing heightmap: {heightmapPath}";
                        return result;
                    }

                    byte[] rawBytes = File.ReadAllBytes(heightmapPath);
                    result.Heights = RuntimeHeightmapUtility.DecodeRaw16FromBytes(
                        rawBytes, heightmapRes, flipHeightmap);
                    result.Success = true;

                    if (!string.IsNullOrEmpty(orthoPathOrNull) && File.Exists(orthoPathOrNull))
                        result.OrthoPngBytes = File.ReadAllBytes(orthoPathOrNull);
                }
                catch (Exception ex)
                {
                    result.Error = ex.Message;
                }

                return result;
            });
        }
    }
}

