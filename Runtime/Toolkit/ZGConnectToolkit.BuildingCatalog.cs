using System;
using System.Collections.Generic;
using UnityEngine;
using ZGConnect.RealtimeStreaming;

namespace ZGConnect
{
    /// <summary>
    /// Offline building catalog — searches tile metadata on disk for buildings in unloaded tiles.
    /// </summary>
    public sealed partial class ZGConnectToolkit
    {
        readonly Dictionary<string, BuildingsMetadataJson> _tileMetadataCache = new(StringComparer.OrdinalIgnoreCase);

        struct TileSearchCandidate
        {
            public string TileId;
            public Vector3 UnityPosition;
            public int TileSizeMeters;
            public bool HasBuildings;
        }

        /// <summary>
        /// All buildings within a horizontal radius — searches the full dataset via tile metadata,
        /// not only streamed-in meshes. Unloaded hits are metadata-only handles (Transform = null).
        /// </summary>
        public List<ZGBuildingHandle> GetBuildingsInRadius(Vector3 center, float radiusMeters) =>
            GetBuildingsInRadius(center, radiusMeters, includeUnloadedTiles: true);

        /// <summary>
        /// Buildings within a horizontal radius. Set <paramref name="includeUnloadedTiles"/> to false
        /// for the faster loaded-only path (previous behaviour).
        /// </summary>
        public List<ZGBuildingHandle> GetBuildingsInRadius(
            Vector3 center,
            float radiusMeters,
            bool includeUnloadedTiles)
        {
            var results = new List<ZGBuildingHandle>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            float radiusSqr = radiusMeters * radiusMeters;

            AppendLoadedBuildingsInRadius(center, radiusSqr, seen, results);

            if (includeUnloadedTiles)
                AppendCatalogBuildingsInRadius(center, radiusSqr, seen, results);

            return results;
        }

        void AppendLoadedBuildingsInRadius(
            Vector3 center,
            float radiusSqr,
            HashSet<string> seen,
            List<ZGBuildingHandle> results)
        {
            foreach (ZGBuildingHandle building in GetLoadedBuildings())
            {
                if (!IsWithinRadius(building.Position, center, radiusSqr))
                    continue;

                string key = MakeBuildingKey(building.TileId, building.BuildingId, building.Name);
                if (!seen.Add(key))
                    continue;

                results.Add(building);
            }
        }

        void AppendCatalogBuildingsInRadius(
            Vector3 center,
            float radiusSqr,
            HashSet<string> seen,
            List<ZGBuildingHandle> results)
        {
            if (!TryGetMetadataSearchContext(out string datasetRoot, out bool orthoRoofStyle, out BuildingMetadataSourceMode sourceMode))
                return;

            foreach (TileSearchCandidate tile in GetTileCandidatesOverlappingRadius(center, radiusSqr))
            {
                if (!tile.HasBuildings)
                    continue;

                if (!TryLoadTileMetadata(datasetRoot, tile.TileId, orthoRoofStyle, sourceMode, out BuildingsMetadataJson meta))
                    continue;

                if (meta.Buildings == null)
                    continue;

                foreach (BuildingEntryJson entry in meta.Buildings)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.Name))
                        continue;

                    Vector3 worldPos = ResolveBuildingWorldPosition(entry, tile.UnityPosition);
                    if (!IsWithinRadius(worldPos, center, radiusSqr))
                        continue;

                    string buildingId = BuildingSurfaceUtility.ExtractBuildingIdFromObjectName(entry.Name);
                    string key = MakeBuildingKey(tile.TileId, buildingId, entry.Name);
                    if (!seen.Add(key))
                        continue;

                    BuildingInfoSnapshot snapshot = BuildingInfoSnapshot.FromEntry(tile.TileId, entry.Name, entry);
                    results.Add(new ZGBuildingHandle(this, tile.TileId, entry.Name, worldPos, snapshot));
                }
            }
        }

        IEnumerable<TileSearchCandidate> GetTileCandidatesOverlappingRadius(Vector3 center, float radiusSqr)
        {
            RealtimeStreamingController streamer = RealtimeStreamer;
            StreamingDatasetManifest manifest = streamer != null ? streamer.Manifest : null;

            if (manifest?.Tiles != null && manifest.Tiles.Count > 0)
            {
                int tileSize = manifest.TileSizeMeters;
                bool useOrtho = streamer.ActiveBuildingStyle == RealtimeBuildingStyle.OrthoRoof;

                foreach (StreamingTileEntry leaf in manifest.Tiles)
                {
                    if (leaf == null)
                        continue;

                    Vector3 tilePos = leaf.GetUnityPosition();
                    if (!TileOverlapsCircle(tilePos, tileSize, center, radiusSqr))
                        continue;

                    bool hasBuildings = useOrtho ? leaf.HasOrthoRoofBuildings : leaf.HasFacadeBuildings;
                    if (!hasBuildings)
                        hasBuildings = leaf.HasFacadeBuildings || leaf.HasOrthoRoofBuildings;

                    yield return new TileSearchCandidate
                    {
                        TileId = leaf.TileId,
                        UnityPosition = tilePos,
                        TileSizeMeters = tileSize,
                        HasBuildings = hasBuildings,
                    };
                }

                yield break;
            }

            CityDataset dataset = TerrainStreamer != null ? TerrainStreamer.Dataset : null;
            if (dataset?.tiles == null)
                yield break;

            int dsTileSize = dataset.tileSizeMeters;
            foreach (CityTileRecord record in dataset.tiles)
            {
                if (record == null || string.IsNullOrEmpty(record.tileId))
                    continue;

                if (!TileOverlapsCircle(record.unityPosition, dsTileSize, center, radiusSqr))
                    continue;

                yield return new TileSearchCandidate
                {
                    TileId = record.tileId,
                    UnityPosition = record.unityPosition,
                    TileSizeMeters = dsTileSize,
                    HasBuildings = true,
                };
            }
        }

        bool TryGetMetadataSearchContext(
            out string datasetRoot,
            out bool orthoRoofStyle,
            out BuildingMetadataSourceMode sourceMode)
        {
            datasetRoot = null;
            orthoRoofStyle = false;
            sourceMode = BuildingMetadataSourceMode.PreferBinary;

            RealtimeStreamingController streamer = RealtimeStreamer;
            if (streamer != null)
            {
                datasetRoot = streamer.DatasetRoot;
                orthoRoofStyle = streamer.ActiveBuildingStyle == RealtimeBuildingStyle.OrthoRoof;
                sourceMode = streamer.BuildingMetadataSourceMode;

                if (string.IsNullOrEmpty(datasetRoot))
                    datasetRoot = RuntimeStreamingPaths.DatasetRoot();

                return !string.IsNullOrEmpty(datasetRoot);
            }

            datasetRoot = RuntimeStreamingPaths.DatasetRoot();
            return !string.IsNullOrEmpty(datasetRoot);
        }

        bool TryLoadTileMetadata(
            string datasetRoot,
            string tileId,
            bool orthoRoofStyle,
            BuildingMetadataSourceMode sourceMode,
            out BuildingsMetadataJson metadata)
        {
            metadata = null;
            if (string.IsNullOrEmpty(datasetRoot) || string.IsNullOrEmpty(tileId))
                return false;

            if (_tileMetadataCache.TryGetValue(tileId, out metadata))
                return metadata != null;

            if (!BuildingMetadataLoadUtility.TryLoadForTile(
                    datasetRoot, tileId, orthoRoofStyle, sourceMode, out metadata, out _))
            {
                _tileMetadataCache[tileId] = null;
                return false;
            }

            _tileMetadataCache[tileId] = metadata;
            return metadata != null;
        }

        static Vector3 ResolveBuildingWorldPosition(BuildingEntryJson entry, Vector3 tileUnityPosition)
        {
            if (entry?.LocalPosition != null)
            {
                return tileUnityPosition + new Vector3(
                    entry.LocalPosition.X,
                    entry.LocalPosition.Y,
                    entry.LocalPosition.Z);
            }

            if (entry?.Gps != null && (entry.Gps.Lat != 0 || entry.Gps.Lon != 0))
                return ZGConnectCoordinates.WGS84ToUnity(entry.Gps.Lat, entry.Gps.Lon);

            return tileUnityPosition;
        }

        static bool TileOverlapsCircle(Vector3 tileUnityPos, int tileSizeMeters, Vector3 center, float radiusSqr)
        {
            float minX = tileUnityPos.x;
            float maxX = tileUnityPos.x + tileSizeMeters;
            float minZ = tileUnityPos.z;
            float maxZ = tileUnityPos.z + tileSizeMeters;

            float closestX = Mathf.Clamp(center.x, minX, maxX);
            float closestZ = Mathf.Clamp(center.z, minZ, maxZ);
            float dx = center.x - closestX;
            float dz = center.z - closestZ;
            return dx * dx + dz * dz <= radiusSqr;
        }

        static bool IsWithinRadius(Vector3 position, Vector3 center, float radiusSqr)
        {
            float dx = position.x - center.x;
            float dz = position.z - center.z;
            return dx * dx + dz * dz <= radiusSqr;
        }

        static string MakeBuildingKey(string tileId, string buildingId, string name)
        {
            string id = !string.IsNullOrEmpty(buildingId) ? buildingId : name;
            return $"{tileId}|{id}";
        }
    }
}
