using System;
using System.Collections.Generic;
using UnityEngine;
using ZGConnect;

namespace ZGConnect.RealtimeStreaming
{
    /// <summary>Loads per-building metadata on demand from .bytes or JSON tile files.</summary>
    public sealed class BuildingLazyMetadataResolver
    {
        sealed class TileCache
        {
            public BuildingTileMetadataIndex BinaryIndex;
            public Dictionary<string, BuildingEntryJson> JsonLookup;
        }

        readonly Dictionary<string, TileCache> _tiles = new(StringComparer.OrdinalIgnoreCase);

        string _datasetRoot;
        bool _orthoRoofStyle;
        BuildingMetadataSourceMode _sourceMode = BuildingMetadataSourceMode.PreferBinary;

        public void Configure(string datasetRoot, bool orthoRoofStyle, BuildingMetadataSourceMode sourceMode)
        {
            _datasetRoot = datasetRoot;
            _orthoRoofStyle = orthoRoofStyle;
            _sourceMode = sourceMode;
        }

        public void Clear() => _tiles.Clear();

        public bool IsConfigured => !string.IsNullOrEmpty(_datasetRoot);

        public bool TryLoadBuildingInfo(
            Transform buildingRoot,
            string tileId,
            out BuildingInfoSnapshot snapshot) =>
            TryLoadBuildingInfo(buildingRoot, tileId, out snapshot, out _);

        public bool TryLoadBuildingInfo(
            Transform buildingRoot,
            string tileId,
            out BuildingInfoSnapshot snapshot,
            out string failureReason)
        {
            snapshot = null;
            failureReason = null;

            if (buildingRoot == null)
            {
                failureReason = "building transform is null";
                return false;
            }

            if (string.IsNullOrEmpty(tileId))
            {
                failureReason = "tile id is empty";
                return false;
            }

            if (string.IsNullOrEmpty(_datasetRoot))
            {
                failureReason = "dataset root is not configured";
                return false;
            }

            if (!TryLoadBuildingEntry(tileId, buildingRoot.name, out BuildingEntryJson entry, out failureReason))
                return false;

            snapshot = BuildingInfoSnapshot.FromEntry(tileId, buildingRoot.name, entry);
            if (snapshot != null)
                return true;

            failureReason = "metadata entry deserialized to null";
            return false;
        }

        bool TryLoadBuildingEntry(
            string tileId,
            string buildingName,
            out BuildingEntryJson entry,
            out string failureReason)
        {
            entry = null;
            failureReason = null;

            if (string.IsNullOrEmpty(tileId) || string.IsNullOrEmpty(buildingName))
            {
                failureReason = "tile id or building name is empty";
                return false;
            }

            TileCache cache = GetOrCreateTileCache(tileId, out failureReason);
            if (cache == null)
                return false;

            if (cache.BinaryIndex != null)
            {
                if (BuildingMetadataBinaryLoader.TryLoadBuildingEntry(
                        cache.BinaryIndex, buildingName, out entry, out string binaryError))
                {
                    return true;
                }

                failureReason = binaryError;
                return false;
            }

            if (cache.JsonLookup != null)
            {
                if (cache.JsonLookup.TryGetValue(buildingName, out entry))
                    return true;

                string id = BuildingSurfaceUtility.ExtractBuildingIdFromObjectName(buildingName);
                if (!string.IsNullOrEmpty(id) && cache.JsonLookup.TryGetValue(id, out entry))
                    return true;

                failureReason = $"building '{buildingName}' not found in tile metadata";
                return false;
            }

            failureReason = "tile metadata cache is empty";
            return false;
        }

        TileCache GetOrCreateTileCache(string tileId, out string failureReason)
        {
            failureReason = null;

            if (_tiles.TryGetValue(tileId, out TileCache cached))
                return cached;

            if (string.IsNullOrEmpty(_datasetRoot))
            {
                failureReason = "dataset root is not configured";
                return null;
            }

            BuildingMetadataPathUtility.ResolveMetadataPaths(
                _datasetRoot, tileId, _orthoRoofStyle, out string jsonPath, out string bytesPath);

            var tileCache = new TileCache();
            switch (_sourceMode)
            {
                case BuildingMetadataSourceMode.BinaryOnly:
                    if (!TryOpenBinaryIndex(bytesPath, tileCache, out failureReason))
                        return null;
                    break;

                case BuildingMetadataSourceMode.JsonOnly:
                    if (!TryOpenJsonLookup(jsonPath, tileCache, out failureReason))
                        return null;
                    break;

                default:
                    if (TryOpenBinaryIndex(bytesPath, tileCache, out _))
                        break;

                    if (!TryOpenJsonLookup(jsonPath, tileCache, out failureReason))
                    {
                        failureReason =
                            $"no metadata files for tile '{tileId}'. " +
                            $"Expected binary: {bytesPath} or JSON: {jsonPath}";
                        return null;
                    }

                    Debug.LogWarning(
                        $"[ZGConnect.Realtime] Lazy metadata: binary missing for tile '{tileId}', using JSON fallback.");
                    break;
            }

            _tiles[tileId] = tileCache;
            return tileCache;
        }

        static bool TryOpenBinaryIndex(string bytesPath, TileCache tileCache, out string failureReason)
        {
            failureReason = null;
            if (!BuildingMetadataBinaryLoader.TryOpenTileIndexFromFile(
                    bytesPath, out BuildingTileMetadataIndex index, out string error))
            {
                failureReason = error;
                return false;
            }

            tileCache.BinaryIndex = index;
            return true;
        }

        static bool TryOpenJsonLookup(string jsonPath, TileCache tileCache, out string failureReason)
        {
            failureReason = null;
            if (!BuildingMetadataLoadUtility.TryLoad(
                    jsonPath, null, BuildingMetadataSourceMode.JsonOnly, out BuildingsMetadataJson meta, out string error))
            {
                failureReason = error;
                return false;
            }

            tileCache.JsonLookup = BuildJsonLookup(meta);
            if (tileCache.JsonLookup.Count > 0)
                return true;

            failureReason = $"no buildings in JSON metadata: {jsonPath}";
            return false;
        }

        static Dictionary<string, BuildingEntryJson> BuildJsonLookup(BuildingsMetadataJson meta)
        {
            var lookup = new Dictionary<string, BuildingEntryJson>(StringComparer.OrdinalIgnoreCase);
            if (meta?.Buildings == null)
                return lookup;

            foreach (BuildingEntryJson entry in meta.Buildings)
            {
                if (entry == null || string.IsNullOrEmpty(entry.Name))
                    continue;

                if (!lookup.ContainsKey(entry.Name))
                    lookup[entry.Name] = entry;

                string id = BuildingSurfaceUtility.ExtractBuildingIdFromObjectName(entry.Name);
                if (!string.IsNullOrEmpty(id) && !lookup.ContainsKey(id))
                    lookup[id] = entry;
            }

            return lookup;
        }
    }
}
