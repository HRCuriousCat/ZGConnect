using System.IO;
using UnityEngine;

namespace ZGConnect.RealtimeStreaming
{
    public static class RuntimeTerrainFactory
    {
        public static TerrainData CreateTerrainData(
            string rawPath,
            int heightmapRes,
            Vector3 terrainSize,
            int targetRes = 0,
            bool flipHeightmapVertically = true)
        {
            float[,] src = RuntimeHeightmapUtility.DecodeRaw16(rawPath, heightmapRes, flipHeightmapVertically);
            return CreateTerrainDataFromHeights(src, heightmapRes, terrainSize, targetRes);
        }

        public static TerrainData CreateTerrainDataFromHeights(
            float[,] src,
            int heightmapRes,
            Vector3 terrainSize,
            int targetRes = 0)
        {
            int finalRes = targetRes > 0 && targetRes < heightmapRes ? targetRes : heightmapRes;
            float[,] heights = finalRes < heightmapRes
                ? RuntimeHeightmapUtility.BilinearDownsample(src, heightmapRes, finalRes)
                : src;

            TerrainData td = new TerrainData();
            td.heightmapResolution = finalRes;
            td.size = terrainSize;
            td.SetHeights(0, 0, heights);
            return td;
        }

        public static GameObject CreateTerrainGameObject(
            TerrainData td,
            Vector3 position,
            Transform parent,
            Material terrainMaterial,
            bool drawInstanced = true,
            int basemapDistance = 2000)
        {
            GameObject go = Terrain.CreateTerrainGameObject(td);
            go.transform.SetParent(parent);
            go.transform.position = position;

            Terrain terrain = go.GetComponent<Terrain>();
            if (terrainMaterial != null)
                terrain.materialTemplate = terrainMaterial;
            terrain.drawInstanced = drawInstanced;
            terrain.basemapDistance = basemapDistance;
            terrain.allowAutoConnect = false;

            TerrainCollider col = go.GetComponent<TerrainCollider>();
            if (col != null)
                col.terrainData = td;

            return go;
        }

        public readonly struct TerrainBoundsEntry
        {
            public readonly Terrain Terrain;
            public readonly int Left;
            public readonly int Bottom;
            public readonly int SizeMeters;

            public TerrainBoundsEntry(Terrain terrain, int left, int bottom, int sizeMeters)
            {
                Terrain = terrain;
                Left = left;
                Bottom = bottom;
                SizeMeters = sizeMeters;
            }
        }

        public static void SetNeighborsByBounds(
            Terrain current,
            int left,
            int bottom,
            int sizeMeters,
            System.Collections.Generic.IReadOnlyList<TerrainBoundsEntry> all)
        {
            if (current == null || all == null)
                return;

            int currentRes = current.terrainData != null ? current.terrainData.heightmapResolution : 0;

            Terrain leftT = null;
            Terrain rightT = null;
            Terrain topT = null;
            Terrain bottomT = null;

            foreach (TerrainBoundsEntry other in all)
            {
                if (other.Terrain == null || other.Terrain == current)
                    continue;

                if (other.Left + other.SizeMeters == left &&
                    RangesOverlap(bottom, sizeMeters, other.Bottom, other.SizeMeters))
                    leftT = PickNeighbor(leftT, other.Terrain, currentRes);

                if (other.Left == left + sizeMeters &&
                    RangesOverlap(bottom, sizeMeters, other.Bottom, other.SizeMeters))
                    rightT = PickNeighbor(rightT, other.Terrain, currentRes);

                if (other.Bottom + other.SizeMeters == bottom &&
                    RangesOverlap(left, sizeMeters, other.Left, other.SizeMeters))
                    bottomT = PickNeighbor(bottomT, other.Terrain, currentRes);

                if (other.Bottom == bottom + sizeMeters &&
                    RangesOverlap(left, sizeMeters, other.Left, other.SizeMeters))
                    topT = PickNeighbor(topT, other.Terrain, currentRes);
            }

            current.SetNeighbors(leftT, topT, rightT, bottomT);
        }

        /// <summary>
        /// Unity stitches edges only when heightmap resolutions match. Mixed HLOD (1×1/2×2/4×4) skips those edges.
        /// </summary>
        static Terrain PickNeighbor(Terrain currentPick, Terrain candidate, int currentHeightmapRes)
        {
            if (candidate?.terrainData == null || currentHeightmapRes <= 0)
                return currentPick;

            if (candidate.terrainData.heightmapResolution != currentHeightmapRes)
                return currentPick;

            return candidate;
        }

        static bool RangesOverlap(int aStart, int aSize, int bStart, int bSize) =>
            aStart < bStart + bSize && bStart < aStart + aSize;
    }
}
