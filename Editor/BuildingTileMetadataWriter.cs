using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using ZGConnect;

namespace ZGConnect.Editor
{
    /// <summary>Writes buildings_{tileId}.json with per-building material slot keys.</summary>
    public static class BuildingTileMetadataWriter
    {
        public static BuildingsMetadataJson BuildFromTile(
            GameObject tileRoot,
            BuildingsMetadataJson existing = null,
            Dictionary<string, BuildingEntryJson> preProcessSlotSnapshot = null)
        {
            var meta = existing ?? new BuildingsMetadataJson();
            string tileId = tileRoot.name.StartsWith("TileBuildings_")
                ? tileRoot.name.Substring("TileBuildings_".Length)
                : tileRoot.name;

            meta.TileId                  = tileId;
            meta.SurfaceBaked            = true;
            meta.SurfacePipelineVersion = BuildingSurfacePipeline.Version;
            meta.BuildingCount          = tileRoot.transform.childCount;
            meta.Buildings              = new List<BuildingEntryJson>();

            Dictionary<string, BuildingEntryJson> sourceLookup = BuildSourceLookup(existing);

            for (int c = 0; c < tileRoot.transform.childCount; c++)
            {
                Transform child = tileRoot.transform.GetChild(c);
                BuildingEntryJson entry = BuildingMaterialSlotApplier.CollectBuildingEntry(child);

                if (preProcessSlotSnapshot != null
                    && preProcessSlotSnapshot.TryGetValue(child.name, out BuildingEntryJson snapshot))
                {
                    BuildingMaterialSlotApplier.MergeSlotSnapshot(entry, snapshot);
                }

                if (TryGetSourceEntry(child.name, sourceLookup, out BuildingEntryJson sourceEntry))
                    MergePreservedMetadata(entry, sourceEntry);

                meta.Buildings.Add(entry);
            }

            return meta;
        }

        private static Dictionary<string, BuildingEntryJson> BuildSourceLookup(
            BuildingsMetadataJson existing)
        {
            var lookup = new Dictionary<string, BuildingEntryJson>(System.StringComparer.OrdinalIgnoreCase);
            if (existing?.Buildings == null)
                return lookup;

            foreach (BuildingEntryJson entry in existing.Buildings)
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                if (!lookup.ContainsKey(entry.Name))
                    lookup[entry.Name] = entry;

                string id = BuildingSurfaceUtility.ExtractBuildingIdFromObjectName(entry.Name);
                if (!string.IsNullOrEmpty(id) && !lookup.ContainsKey(id))
                    lookup[id] = entry;
            }

            return lookup;
        }

        private static bool TryGetSourceEntry(
            string childName,
            Dictionary<string, BuildingEntryJson> lookup,
            out BuildingEntryJson entry)
        {
            entry = null;
            if (lookup == null || string.IsNullOrEmpty(childName))
                return false;

            if (lookup.TryGetValue(childName, out entry))
                return true;

            string id = BuildingSurfaceUtility.ExtractBuildingIdFromObjectName(childName);
            return !string.IsNullOrEmpty(id) && lookup.TryGetValue(id, out entry);
        }

        private static void MergePreservedMetadata(BuildingEntryJson target, BuildingEntryJson source)
        {
            if (target == null || source == null)
                return;

            if (source.Gps != null)
                target.Gps = source.Gps;
            if (source.Osm != null)
                target.Osm = source.Osm;
        }

        public static void WriteJson(
            GameObject tileRoot,
            string glbOsPath,
            BuildingsMetadataJson existing = null,
            Dictionary<string, BuildingEntryJson> preProcessSlotSnapshot = null)
        {
            BuildingsMetadataJson meta = BuildFromTile(tileRoot, existing, preProcessSlotSnapshot);
            string jsonPath = Path.ChangeExtension(glbOsPath, ".json");
            File.WriteAllText(jsonPath, JsonConvert.SerializeObject(meta, Formatting.Indented));
        }
    }
}
