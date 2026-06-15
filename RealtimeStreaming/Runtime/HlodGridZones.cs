using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect.RealtimeStreaming
{
    /// <summary>
    /// Axis-aligned tile-grid rectangles for hierarchical HLOD.
    /// Indices are 1×1 EPSG cells: gx = left / tileSizeMeters.
    /// </summary>
    public readonly struct HlodGridRect
    {
        public readonly int MinX;
        public readonly int MaxX;
        public readonly int MinZ;
        public readonly int MaxZ;

        public HlodGridRect(int minX, int maxX, int minZ, int maxZ)
        {
            MinX = minX;
            MaxX = maxX;
            MinZ = minZ;
            MaxZ = maxZ;
        }

        public bool IsEmpty => MinX >= MaxX || MinZ >= MaxZ;

        public bool Contains(int gx, int gz) =>
            gx >= MinX && gx < MaxX && gz >= MinZ && gz < MaxZ;

        public bool ContainsBlock(int gx, int gz, int cellsPerSide) =>
            gx >= MinX && gz >= MinZ &&
            gx + cellsPerSide <= MaxX &&
            gz + cellsPerSide <= MaxZ;

        public bool Overlaps(HlodGridRect other) =>
            MinX < other.MaxX && MaxX > other.MinX &&
            MinZ < other.MaxZ && MaxZ > other.MinZ;
    }

    public readonly struct HlodGridZoneSet
    {
        public readonly HlodGridRect Zone1x1;
        public readonly HlodGridRect Zone2x2;
        public readonly HlodGridRect Zone4x4;

        public HlodGridZoneSet(HlodGridRect zone1x1, HlodGridRect zone2x2, HlodGridRect zone4x4)
        {
            Zone1x1 = zone1x1;
            Zone2x2 = zone2x2;
            Zone4x4 = zone4x4;
        }
    }

    public static class HlodGridZones
    {
        public const int PackRegionAlignFactor = 4;

        public static void AlignRegionEpsg(
            int minE,
            int maxE,
            int minN,
            int maxN,
            int tileSizeMeters,
            int hlodFactor,
            out int alignedMinE,
            out int alignedMaxE,
            out int alignedMinN,
            out int alignedMaxN)
        {
            int alignMeters = Mathf.Max(1, tileSizeMeters) * Mathf.Max(1, hlodFactor);
            alignedMinE = AlignDown(minE, alignMeters);
            alignedMaxE = AlignUp(maxE, alignMeters);
            alignedMinN = AlignDown(minN, alignMeters);
            alignedMaxN = AlignUp(maxN, alignMeters);
        }

        public static Vector2 CameraGridPosition(Vector3 camPos, Vector2Int unityOrigin, int tileSizeMeters)
        {
            float gx = (camPos.x + unityOrigin.x) / tileSizeMeters;
            float gz = (camPos.z + unityOrigin.y) / tileSizeMeters;
            return new Vector2(gx, gz);
        }

        public static void TileToGridIndices(int left, int bottom, int tileSizeMeters, out int gx, out int gz)
        {
            gx = left / tileSizeMeters;
            gz = bottom / tileSizeMeters;
        }

        public static HlodGridZoneSet BuildZones(
            Vector3 camPos,
            StreamingDatasetManifest manifest,
            ICollection<int> availableFactors,
            float hlod1x1LoadDistanceMeters,
            float hlod2x2LoadDistanceMeters,
            float hlod4x4LoadDistanceMeters)
        {
            int tileSize = manifest.TileSizeMeters;
            Vector2Int origin = manifest.GetUnityOrigin();
            Vector2 camGrid = CameraGridPosition(camPos, origin, tileSize);

            float nearHalf = hlod1x1LoadDistanceMeters / tileSize;
            float midHalf = hlod2x2LoadDistanceMeters / tileSize;
            float farHalf = hlod4x4LoadDistanceMeters / tileSize;

            if (!availableFactors.Contains(2))
                midHalf = nearHalf;
            if (!availableFactors.Contains(4))
                farHalf = midHalf;

            HlodGridRect zone1 = BuildAlignedRect(camGrid.x, camGrid.y, nearHalf, alignCells: 2);
            HlodGridRect zone2 = BuildAlignedRect(camGrid.x, camGrid.y, midHalf, alignCells: 4);
            HlodGridRect zone4 = BuildAlignedRect(camGrid.x, camGrid.y, farHalf, alignCells: 4);

            return new HlodGridZoneSet(zone1, zone2, zone4);
        }

        public static HlodGridRect BuildAlignedRect(
            float centerGX,
            float centerGZ,
            float halfExtentCells,
            int alignCells)
        {
            alignCells = Mathf.Max(1, alignCells);
            int rawMinX = Mathf.FloorToInt(centerGX - halfExtentCells);
            int rawMaxX = Mathf.CeilToInt(centerGX + halfExtentCells);
            int rawMinZ = Mathf.FloorToInt(centerGZ - halfExtentCells);
            int rawMaxZ = Mathf.CeilToInt(centerGZ + halfExtentCells);

            return new HlodGridRect(
                AlignDown(rawMinX, alignCells),
                AlignUp(rawMaxX, alignCells),
                AlignDown(rawMinZ, alignCells),
                AlignUp(rawMaxZ, alignCells));
        }

        public static int AlignDown(int value, int align)
        {
            if (align <= 1)
                return value;

            int rem = value % align;
            if (rem == 0)
                return value;
            return value - rem;
        }

        public static int AlignUp(int value, int align)
        {
            if (align <= 1)
                return value;

            int rem = value % align;
            if (rem == 0)
                return value;
            return value + (align - rem);
        }

        public static bool SupertileFitsZone(
            int gx,
            int gz,
            int factor,
            HlodGridRect outerZone,
            HlodGridRect innerZone)
        {
            if (!outerZone.ContainsBlock(gx, gz, factor))
                return false;

            if (innerZone.IsEmpty)
                return true;

            for (int dz = 0; dz < factor; dz++)
            {
                for (int dx = 0; dx < factor; dx++)
                {
                    if (innerZone.Contains(gx + dx, gz + dz))
                        return false;
                }
            }

            return true;
        }
    }
}
