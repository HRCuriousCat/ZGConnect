using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ZGConnect.RealtimeStreaming
{
    public static class RuntimeHeightmapUtility
    {
        public static float[,] DecodeRaw16(string rawPath, int srcRes, bool flipVertically = true)
        {
            byte[] bytes = File.ReadAllBytes(rawPath);
            return DecodeRaw16FromBytes(bytes, srcRes, flipVertically, rawPath);
        }

        public static float[,] DecodeRaw16FromBytes(
            byte[] bytes,
            int srcRes,
            bool flipVertically = true,
            string debugPath = null)
        {
            int expectedBytes = srcRes * srcRes * 2;
            if (bytes == null || bytes.Length != expectedBytes)
            {
                string label = string.IsNullOrEmpty(debugPath) ? "buffer" : debugPath;
                throw new Exception(
                    $"[ZGConnect.Realtime] RAW size mismatch: {label}\n" +
                    $"Expected {expectedBytes} bytes, got {bytes?.Length ?? 0}.");
            }

            float[,] src = new float[srcRes, srcRes];
            int idx = 0;
            for (int y = 0; y < srcRes; y++)
            {
                int destY = flipVertically ? (srcRes - 1 - y) : y;
                for (int x = 0; x < srcRes; x++)
                {
                    src[destY, x] = BitConverter.ToUInt16(bytes, idx) / 65535f;
                    idx += 2;
                }
            }

            return src;
        }

        public static float[,] BilinearDownsample(float[,] src, int srcRes, int dstRes)
        {
            if (srcRes == dstRes)
                return src;

            float[,] dst = new float[dstRes, dstRes];
            float scale = (float)(srcRes - 1) / Mathf.Max(dstRes - 1, 1);

            for (int y = 0; y < dstRes; y++)
            {
                float srcY = y * scale;
                int y0 = Mathf.FloorToInt(srcY);
                int y1 = Mathf.Min(y0 + 1, srcRes - 1);
                float fy = srcY - y0;

                for (int x = 0; x < dstRes; x++)
                {
                    float srcX = x * scale;
                    int x0 = Mathf.FloorToInt(srcX);
                    int x1 = Mathf.Min(x0 + 1, srcRes - 1);
                    float fx = srcX - x0;

                    dst[y, x] = Mathf.Lerp(
                        Mathf.Lerp(src[y0, x0], src[y0, x1], fx),
                        Mathf.Lerp(src[y1, x0], src[y1, x1], fx),
                        fy);
                }
            }

            return dst;
        }

        /// <param name="heightsInUnityOrder">
        /// When true, <paramref name="heights"/> uses Unity terrain row order (south→north);
        /// file rows are written GDAL order (north→south) so runtime <see cref="DecodeRaw16"/> can flip once.
        /// </param>
        public static void WriteRaw16(string rawPath, float[,] heights, int res, bool heightsInUnityOrder = false)
        {
            byte[] bytes = new byte[res * res * 2];
            int idx = 0;
            for (int y = 0; y < res; y++)
            {
                int srcY = heightsInUnityOrder ? (res - 1 - y) : y;
                for (int x = 0; x < res; x++)
                {
                    ushort v = (ushort)Mathf.Clamp(Mathf.RoundToInt(heights[srcY, x] * 65535f), 0, 65535);
                    bytes[idx++] = (byte)(v & 0xFF);
                    bytes[idx++] = (byte)(v >> 8);
                }
            }

            string dir = Path.GetDirectoryName(rawPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllBytes(rawPath, bytes);
        }

        public static float[,] StitchHeightmaps(IReadOnlyList<float[,]> tiles, int tileRes, int gridN)
        {
            int outRes = tileRes + (gridN - 1) * (tileRes - 1);
            float[,] merged = new float[outRes, outRes];

            for (int ty = 0; ty < gridN; ty++)
            {
                for (int tx = 0; tx < gridN; tx++)
                {
                    float[,] tile = tiles[ty * gridN + tx];
                    int baseX = tx * (tileRes - 1);
                    int baseY = ty * (tileRes - 1);

                    for (int y = 0; y < tileRes; y++)
                    {
                        for (int x = 0; x < tileRes; x++)
                            merged[baseY + y, baseX + x] = tile[y, x];
                    }
                }
            }

            return merged;
        }
    }
}
