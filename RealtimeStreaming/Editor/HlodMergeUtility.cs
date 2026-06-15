using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using ZGConnect.Editor;
using ZGConnect.RealtimeStreaming;

namespace ZGConnect.RealtimeStreaming.Editor
{
    public static class HlodMergeUtility
    {
        public delegate void SupertileProgressHandler(int factor, int current, int total, string supertileId);

        public static IEnumerator GenerateSupertilesCoroutine(
            string outputRoot,
            List<StreamingTileEntry> packedTiles,
            int tileSizeMeters,
            int heightmapRes,
            Dictionary<string, Dictionary<string, string>> orthoPathsByBasemap,
            int[] orthoResByFactor,
            int[] heightResByFactor,
            List<StreamingSupertileEntry> intoSupertiles,
            bool mergeHeightmaps = true,
            SupertileProgressHandler onProgress = null,
            RealtimePackOptions packOptions = null,
            Dictionary<string, StreamingSupertileEntry> existingSupertilesById = null,
            HashSet<string> freshlyPackedTileIds = null,
            HashSet<string> rebuiltSupertileIds = null)
        {
            var byId = new Dictionary<string, StreamingTileEntry>();
            foreach (StreamingTileEntry t in packedTiles)
                byId[t.TileId] = t;

            IEnumerator factor2 = GenerateFactorCoroutine(outputRoot, packedTiles, byId, tileSizeMeters, heightmapRes,
                orthoPathsByBasemap, orthoResByFactor, heightResByFactor, 2, intoSupertiles, mergeHeightmaps, onProgress,
                packOptions, existingSupertilesById, freshlyPackedTileIds, rebuiltSupertileIds);
            while (factor2.MoveNext())
                yield return factor2.Current;

            IEnumerator factor4 = GenerateFactorCoroutine(outputRoot, packedTiles, byId, tileSizeMeters, heightmapRes,
                orthoPathsByBasemap, orthoResByFactor, heightResByFactor, 4, intoSupertiles, mergeHeightmaps, onProgress,
                packOptions, existingSupertilesById, freshlyPackedTileIds, rebuiltSupertileIds);
            while (factor4.MoveNext())
                yield return factor4.Current;
        }

        static IEnumerator GenerateFactorCoroutine(
            string outputRoot,
            List<StreamingTileEntry> packedTiles,
            Dictionary<string, StreamingTileEntry> byId,
            int tileSizeMeters,
            int leafHeightRes,
            Dictionary<string, Dictionary<string, string>> orthoPathsByBasemap,
            int[] orthoResByFactor,
            int[] heightResByFactor,
            int factor,
            List<StreamingSupertileEntry> intoSupertiles,
            bool mergeHeightmaps,
            SupertileProgressHandler onProgress,
            RealtimePackOptions packOptions,
            Dictionary<string, StreamingSupertileEntry> existingSupertilesById,
            HashSet<string> freshlyPackedTileIds,
            HashSet<string> rebuiltSupertileIds)
        {
            int groupSize = tileSizeMeters * factor;
            int minLeft = int.MaxValue;
            int maxRight = int.MinValue;
            int minBottom = int.MaxValue;
            int maxTop = int.MinValue;
            foreach (StreamingTileEntry tile in packedTiles)
            {
                if (tile.Left < minLeft) minLeft = tile.Left;
                if (tile.Right > maxRight) maxRight = tile.Right;
                if (tile.Bottom < minBottom) minBottom = tile.Bottom;
                if (tile.Top > maxTop) maxTop = tile.Top;
            }

            if (minLeft >= maxRight || minBottom >= maxTop)
                yield break;

            int alignedMinLeft = HlodGridZones.AlignDown(minLeft, groupSize);
            int alignedMinBottom = HlodGridZones.AlignDown(minBottom, groupSize);
            int groupCount = 0;
            for (int probeLeft = alignedMinLeft; probeLeft < maxRight; probeLeft += groupSize)
            {
                for (int probeBottom = alignedMinBottom; probeBottom < maxTop; probeBottom += groupSize)
                    groupCount++;
            }

            int groupDone = 0;
            for (int left = alignedMinLeft; left < maxRight; left += groupSize)
            {
                for (int bottom = alignedMinBottom; bottom < maxTop; bottom += groupSize)
                {

                var children = new List<StreamingTileEntry>();
                bool complete = true;
                for (int dy = 0; dy < factor && complete; dy++)
                {
                    for (int dx = 0; dx < factor; dx++)
                    {
                        string id = $"{left + dx * tileSizeMeters}_{bottom + dy * tileSizeMeters}";
                        if (!byId.TryGetValue(id, out StreamingTileEntry child))
                        {
                            complete = false;
                            break;
                        }
                        children.Add(child);
                    }
                }

                if (!complete || children.Count != factor * factor)
                    continue;

                children.Sort((a, b) =>
                {
                    int c = a.Bottom.CompareTo(b.Bottom);
                    return c != 0 ? c : a.Left.CompareTo(b.Left);
                });

                int targetHeightRes = heightResByFactor[FactorIndex(factor)];
                int targetOrthoRes = orthoResByFactor[FactorIndex(factor)];

                string supertileId = HlodRingEvaluator.SupertileId(left, bottom, factor);
                groupDone++;
                onProgress?.Invoke(factor, groupDone, groupCount, supertileId);

                if (packOptions != null &&
                    packOptions.SkipExisting &&
                    existingSupertilesById != null &&
                    existingSupertilesById.TryGetValue(supertileId, out StreamingSupertileEntry existing) &&
                    !RealtimePackReuseUtility.GroupTouchesFreshTiles(children, freshlyPackedTileIds) &&
                    RealtimePackReuseUtility.CanReuseSupertile(existing, children, packOptions, outputRoot))
                {
                    intoSupertiles.Add(existing);
                    yield return null;
                    continue;
                }

                rebuiltSupertileIds?.Add(supertileId);
                string hmRel = $"heightmap/tiles_{factor}x{factor}/{supertileId}.raw";
                string hmPath = Path.Combine(outputRoot, hmRel.Replace('/', Path.DirectorySeparatorChar));

                bool heightmapOk = false;
                if (mergeHeightmaps)
                {
                    try
                    {
                        MergeHeightmaps(children, outputRoot, hmPath, leafHeightRes, targetHeightRes, factor);
                        heightmapOk = true;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[ZGConnect.Realtime] HLOD heightmap merge skipped for {supertileId}: {ex.Message}");
                    }
                }

                var orthoPaths = new Dictionary<string, string>();
                if (orthoPathsByBasemap != null)
                {
                    foreach (var bmKvp in orthoPathsByBasemap)
                    {
                        string basemapId = bmKvp.Key;
                        string orthoRel = $"basemaps/{basemapId}/ortho_{factor}x{factor}/{supertileId}.png";
                        string orthoPath = Path.Combine(outputRoot, orthoRel.Replace('/', Path.DirectorySeparatorChar));
                        try
                        {
                            MergeOrtho(children, outputRoot, bmKvp.Value, orthoPath, targetOrthoRes, factor);
                            orthoPaths[basemapId] = orthoRel.Replace('\\', '/');
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[ZGConnect.Realtime] HLOD ortho merge skipped for {supertileId}/{basemapId}: {ex.Message}");
                        }
                    }
                }

                if (!heightmapOk && orthoPaths.Count == 0)
                    continue;

                StreamingTileEntry sw = children[0];
                var entry = new StreamingSupertileEntry
                {
                    SupertileId = supertileId,
                    Factor = factor,
                    Left = left,
                    Bottom = bottom,
                    HeightmapPath = heightmapOk ? hmRel.Replace('\\', '/') : null,
                    HeightmapRes = targetHeightRes,
                    UnityPosition = new[]
                    {
                        sw.UnityPosition[0],
                        sw.UnityPosition[1],
                        sw.UnityPosition[2],
                    },
                    TerrainSize = new[]
                    {
                        sw.TerrainSize[0] * factor,
                        sw.TerrainSize[1],
                        sw.TerrainSize[2] * factor,
                    },
                    OrthoPaths = orthoPaths,
                    ChildTileIds = children.ConvertAll(c => c.TileId),
                };
                intoSupertiles.Add(entry);
                yield return null;
                }
            }
        }

        static void MergeHeightmaps(
            List<StreamingTileEntry> children,
            string outputRoot,
            string outPath,
            int leafRes,
            int targetRes,
            int factor)
        {
            var tiles = new float[factor * factor][,];
            for (int i = 0; i < children.Count; i++)
            {
                string rawPath = Path.Combine(outputRoot,
                    children[i].HeightmapPath.Replace('/', Path.DirectorySeparatorChar));
                tiles[i] = RuntimeHeightmapUtility.DecodeRaw16(rawPath, leafRes);
            }

            float[,] stitched = RuntimeHeightmapUtility.StitchHeightmaps(tiles, leafRes, factor);
            int stitchedRes = leafRes + (factor - 1) * (leafRes - 1);
            float[,] final = RuntimeHeightmapUtility.BilinearDownsample(stitched, stitchedRes, targetRes);
            RuntimeHeightmapUtility.WriteRaw16(outPath, final, targetRes, heightsInUnityOrder: true);
        }

        static void MergeOrtho(
            List<StreamingTileEntry> children,
            string outputRoot,
            Dictionary<string, string> leafOrthoRelByTileId,
            string outPath,
            int targetRes,
            int factor)
        {
            Texture2D[] tiles = new Texture2D[children.Count];
            int tileRes = 0;
            for (int i = 0; i < children.Count; i++)
            {
                if (!leafOrthoRelByTileId.TryGetValue(children[i].TileId, out string rel))
                    throw new InvalidOperationException("Missing ortho path");

                string path = Path.Combine(outputRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                byte[] bytes = File.ReadAllBytes(path);
                var tex = new Texture2D(2, 2);
                tex.LoadImage(bytes);
                tiles[i] = tex;
                tileRes = tex.width;
            }

            int outW = tileRes * factor;
            int outH = tileRes * factor;
            var merged = new Texture2D(outW, outH, TextureFormat.RGBA32, false);
            int idx = 0;
            for (int row = 0; row < factor; row++)
            {
                for (int col = 0; col < factor; col++)
                {
                    Texture2D src = tiles[idx++];
                    merged.SetPixels(col * tileRes, row * tileRes, tileRes, tileRes, src.GetPixels());
                }
            }
            merged.Apply();

            Texture2D down = DownscaleTexture(merged, targetRes, targetRes);
            Texture2D matte = RuntimeBasemapFactory.PrepareOrthoDiffuseForTerrain(down);
            byte[] png = matte.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(matte);
            string dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllBytes(outPath, png);

            foreach (Texture2D t in tiles)
                UnityEngine.Object.DestroyImmediate(t);
            UnityEngine.Object.DestroyImmediate(merged);
        }

        static Texture2D DownscaleTexture(Texture2D src, int w, int h)
        {
            RenderTexture rt = RenderTexture.GetTemporary(w, h);
            Graphics.Blit(src, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var dst = new Texture2D(w, h, TextureFormat.RGBA32, false);
            dst.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            dst.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return dst;
        }

        static int FactorIndex(int factor) => factor switch { 1 => 0, 2 => 1, 4 => 2, _ => 0 };
    }
}
