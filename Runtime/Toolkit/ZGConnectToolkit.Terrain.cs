using UnityEngine;

namespace ZGConnect
{
    /// <summary>Terrain helpers: ground height, slope, normals, tile lookup, random points.</summary>
    public sealed partial class ZGConnectToolkit
    {
        const float GroundRaycastHeight = 6000f;

        // ── Ground height ──────────────────────────────────────────────────────

        /// <summary>
        /// Unity Y of the terrain surface at a world position. Samples loaded Unity terrains
        /// first, then falls back to a physics raycast against terrain colliders.
        /// Returns false when no terrain is loaded at that point.
        /// </summary>
        public bool TryGetGroundHeight(Vector3 worldPos, out float groundY)
        {
            Terrain terrain = FindTerrainAt(worldPos);
            if (terrain != null)
            {
                groundY = terrain.SampleHeight(worldPos) + terrain.transform.position.y;
                return true;
            }

            if (TryRaycastTerrain(worldPos, out RaycastHit hit))
            {
                groundY = hit.point.y;
                return true;
            }

            groundY = 0f;
            return false;
        }

        /// <summary>Terrain surface Y at a GPS coordinate. False when no terrain is loaded there.</summary>
        public bool TryGetGroundHeightAtGps(double latitude, double longitude, out float groundY) =>
            TryGetGroundHeight(ZGConnectCoordinates.WGS84ToUnity(latitude, longitude), out groundY);

        /// <summary>
        /// Returns the position projected onto the terrain surface (plus optional offset).
        /// When no terrain is loaded at that point, the input position is returned unchanged.
        /// Perfect for spawning objects: <c>obj.position = toolkit.SnapToGround(pos, 1f);</c>
        /// </summary>
        public Vector3 SnapToGround(Vector3 worldPos, float heightOffset = 0f)
        {
            if (TryGetGroundHeight(worldPos, out float groundY))
                worldPos.y = groundY + heightOffset;
            return worldPos;
        }

        // ── Surface orientation ────────────────────────────────────────────────

        /// <summary>
        /// Terrain surface normal at a world position. Returns Vector3.up when no terrain
        /// is loaded there.
        /// </summary>
        public Vector3 GetGroundNormal(Vector3 worldPos)
        {
            Terrain terrain = FindTerrainAt(worldPos);
            if (terrain != null)
            {
                Vector3 local = worldPos - terrain.transform.position;
                Vector3 size = terrain.terrainData.size;
                return terrain.terrainData.GetInterpolatedNormal(local.x / size.x, local.z / size.z);
            }

            if (TryRaycastTerrain(worldPos, out RaycastHit hit))
                return hit.normal;

            return Vector3.up;
        }

        /// <summary>
        /// Terrain slope angle in degrees at a world position (0 = flat). Useful for spawn
        /// rules like "no placement on slopes steeper than 30°".
        /// </summary>
        public float GetSlopeAngle(Vector3 worldPos) =>
            Vector3.Angle(GetGroundNormal(worldPos), Vector3.up);

        // ── Tile lookup ────────────────────────────────────────────────────────

        /// <summary>
        /// Tile id covering a world position (e.g. "550000_5068000"), computed from the
        /// EPSG:3765 grid. The tile may or may not be loaded.
        /// </summary>
        public string GetTileIdAt(Vector3 worldPos)
        {
            (double easting, double northing) = WorldToEpsg3765(worldPos);
            int size = TileSizeMeters;
            int left = Mathf.FloorToInt((float)(easting / size)) * size;
            int bottom = Mathf.FloorToInt((float)(northing / size)) * size;
            return $"{left}_{bottom}";
        }

        /// <summary>
        /// True when the leaf terrain tile covering this position is currently streamed in
        /// at full resolution (HLOD supertiles do not count).
        /// </summary>
        public bool IsTileLoadedAt(Vector3 worldPos)
        {
            if (RealtimeStreamer != null)
                return RealtimeStreamer.IsTerrainTileLoaded(GetTileIdAt(worldPos));

            return FindTerrainAt(worldPos) != null;
        }

        /// <summary>True when any terrain surface (leaf tile or HLOD supertile) exists at this position.</summary>
        public bool IsTerrainReadyAt(Vector3 worldPos) =>
            FindTerrainAt(worldPos) != null || TryRaycastTerrain(worldPos, out _);

        // ── Random points ──────────────────────────────────────────────────────

        /// <summary>
        /// Random point on the terrain within a horizontal radius around a centre point —
        /// ideal for spawning pickups, NPCs or quest markers. Optionally rejects points
        /// covered by a building. Returns false when no valid point was found.
        /// </summary>
        public bool TryGetRandomPointOnGround(
            Vector3 center,
            float radiusMeters,
            out Vector3 result,
            bool avoidBuildings = false,
            int maxAttempts = 16)
        {
            for (int i = 0; i < maxAttempts; i++)
            {
                Vector2 offset = Random.insideUnitCircle * radiusMeters;
                var candidate = new Vector3(center.x + offset.x, center.y, center.z + offset.y);

                if (!TryGetGroundHeight(candidate, out float groundY))
                    continue;

                candidate.y = groundY;

                if (avoidBuildings && GetBuildingAt(candidate) != null)
                    continue;

                result = candidate;
                return true;
            }

            result = center;
            return false;
        }

        // ── Internals ──────────────────────────────────────────────────────────

        static Terrain FindTerrainAt(Vector3 worldPos)
        {
            foreach (Terrain terrain in Terrain.activeTerrains)
            {
                if (terrain == null || terrain.terrainData == null)
                    continue;

                Vector3 origin = terrain.transform.position;
                Vector3 size = terrain.terrainData.size;
                if (worldPos.x >= origin.x && worldPos.x <= origin.x + size.x &&
                    worldPos.z >= origin.z && worldPos.z <= origin.z + size.z)
                {
                    return terrain;
                }
            }

            return null;
        }

        static bool TryRaycastTerrain(Vector3 worldPos, out RaycastHit terrainHit)
        {
            var ray = new Ray(new Vector3(worldPos.x, GroundRaycastHeight, worldPos.z), Vector3.down);
            RaycastHit[] hits = Physics.RaycastAll(ray, GroundRaycastHeight * 2f, ~0, QueryTriggerInteraction.Ignore);

            terrainHit = default;
            float best = float.MaxValue;
            bool found = false;

            foreach (RaycastHit hit in hits)
            {
                if (hit.collider is TerrainCollider && hit.distance < best)
                {
                    best = hit.distance;
                    terrainHit = hit;
                    found = true;
                }
            }

            return found;
        }
    }
}
