using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    public static class SpatialTileDistanceUtility
    {
        public static float TileBoundaryDistance(Vector3 camPos, Vector3 tileOrigin, int tileSizeMeters)
        {
            float minX = tileOrigin.x;
            float maxX = tileOrigin.x + tileSizeMeters;
            float minZ = tileOrigin.z;
            float maxZ = tileOrigin.z + tileSizeMeters;

            float dx = camPos.x < minX ? minX - camPos.x : camPos.x > maxX ? camPos.x - maxX : 0f;
            float dz = camPos.z < minZ ? minZ - camPos.z : camPos.z > maxZ ? camPos.z - maxZ : 0f;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        public static float SubcellBoundaryDistance(
            Vector3 camPos,
            Vector3 tileOrigin,
            int subcellSizeMeters,
            int gridX,
            int gridY)
        {
            float minX = tileOrigin.x + gridX * subcellSizeMeters;
            float maxX = minX + subcellSizeMeters;
            float minZ = tileOrigin.z + gridY * subcellSizeMeters;
            float maxZ = minZ + subcellSizeMeters;

            float dx = camPos.x < minX ? minX - camPos.x : camPos.x > maxX ? camPos.x - maxX : 0f;
            float dz = camPos.z < minZ ? minZ - camPos.z : camPos.z > maxZ ? camPos.z - maxZ : 0f;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
