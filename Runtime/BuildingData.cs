using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect
{
    [Serializable]
    public struct BuildingGpsData
    {
        public bool   hasData;
        public double latitude;
        public double longitude;
    }

    [Serializable]
    public struct BuildingOsmTagEntry
    {
        public string key;
        public string value;
    }

    [Serializable]
    public struct BuildingOsmMatchData
    {
        public bool  hasMatch;
        public string osmType;
        public long   osmId;
        public string role;
        public string label;
        public string matchMethod;
        public float  matchScore;
        public float  distanceM;
        public List<BuildingOsmTagEntry> tags;
    }

    [Serializable]
    public struct BuildingOsmAtPointData
    {
        public string osmType;
        public long   osmId;
        public string role;
        public string label;
        public float  areaM2;
        public List<BuildingOsmTagEntry> tags;
    }

    [Serializable]
    public struct BuildingOsmData
    {
        public bool   hasData;
        public string coordSource;
        public double queryLatitude;
        public double queryLongitude;
        public BuildingOsmMatchData buildingMatch;
        public List<BuildingOsmAtPointData> atPoint;
    }

    /// <summary>
    /// Per-building metadata component attached to every building child inside a
    /// TileBuildings prefab at import time.
    ///
    /// The <see cref="buildingId"/> field carries the numeric identifier extracted from
    /// the original building name (e.g. "zagreb_Part_1619" → "1619"). This ID is the
    /// primary key for joining external attribute datasets (City of Zagreb open data:
    /// address, floors, gross floor area, use classification, construction year, etc.).
    ///
    /// GPS and OSM fields are populated from buildings_{tileId}.json during import.
    /// Attribute enrichment fields may be filled by a separate post-import step.
    /// </summary>
    [DisallowMultipleComponent]
    public class BuildingData : MonoBehaviour
    {
        [Header("Spatial Identity")]
        [Tooltip("Numeric identifier extracted from the building object name. " +
                 "Primary key for joining external attribute data sources.")]
        public string buildingId;

        [Tooltip("ID of the terrain tile this building belongs to (e.g. '550000_5068000').")]
        public string tileId;

        [Tooltip("Original mesh name from the buildings JSON (e.g. zagreb_Part_1619).")]
        public string sourceName;

        [Header("GPS (from buildings JSON)")]
        public BuildingGpsData gps;

        [Header("OSM (from buildings JSON)")]
        public BuildingOsmData osm;

        [Header("Attribute Enrichment")]
        [Tooltip("Street address from City of Zagreb open data (populated post-import).")]
        public string address;

        [Tooltip("Number of above-ground floors (populated post-import).")]
        public int floors;

        [Tooltip("Gross floor area in square metres (populated post-import).")]
        public float grossFloorArea;

        [Tooltip("Land-use classification, e.g. Residential / Commercial / Industrial " +
                 "(populated post-import).")]
        public string useClassification;

        [Tooltip("Year of construction (populated post-import).")]
        public int constructionYear;

        /// <summary>Returns the first OSM tag value on the matched building footprint, if any.</summary>
        public string GetOsmBuildingTag(string tagKey)
        {
            if (string.IsNullOrEmpty(tagKey)
                || !osm.hasData
                || !osm.buildingMatch.hasMatch
                || osm.buildingMatch.tags == null)
                return null;

            foreach (BuildingOsmTagEntry tag in osm.buildingMatch.tags)
            {
                if (string.Equals(tag.key, tagKey, StringComparison.OrdinalIgnoreCase))
                    return tag.value;
            }

            return null;
        }
    }
}
