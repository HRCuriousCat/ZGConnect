using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect
{
    /// <summary>
    /// Serializable mirror of <see cref="BuildingInfoSnapshot"/> for Unity Inspector (Play mode).
    /// </summary>
    [Serializable]
    public sealed class BuildingInfoSnapshotInspectorData
    {
        [Tooltip("Numeric building id extracted from the mesh name.")]
        public string buildingId;

        [Tooltip("Terrain tile id (e.g. 454000_5069000).")]
        public string tileId;

        [Tooltip("Original JSON / mesh name.")]
        public string sourceName;

        [Header("GPS")]
        public bool gpsHasData;
        public double gpsLatitude;
        public double gpsLongitude;

        [Tooltip("Formatted coordinates shown in UI (GPS or OSM query fallback).")]
        public string gpsText;

        [Header("Address")]
        public string address;
        public string street;
        public string houseNumber;
        public string city;
        public string postcode;

        [Header("OSM")]
        public bool osmHasData;
        public string osmCoordSource;
        public double osmQueryLatitude;
        public double osmQueryLongitude;
        public string osmRole;
        public string osmFeature;
        public string buildingTag;
        public string osmLabel;
        public string osmMatchMethod;
        public float osmMatchScore;
        public float osmDistanceM;

        [Header("Attribute enrichment")]
        public int floors;
        public float grossFloorArea;
        public string useClassification;
        public int constructionYear;

        [Header("OSM tags (matched building)")]
        public List<BuildingOsmTagEntry> osmTags = new();

        public void CopyFrom(BuildingInfoSnapshot snapshot)
        {
            Clear();
            if (snapshot == null)
                return;

            buildingId = snapshot.buildingId;
            tileId = snapshot.tileId;
            sourceName = snapshot.sourceName;

            gpsHasData = snapshot.gps.hasData;
            gpsLatitude = snapshot.gps.latitude;
            gpsLongitude = snapshot.gps.longitude;
            gpsText = snapshot.gpsText;

            address = snapshot.address;
            street = snapshot.street;
            houseNumber = snapshot.houseNumber;
            city = snapshot.city;
            postcode = snapshot.postcode;

            osmHasData = snapshot.osm.hasData;
            osmCoordSource = snapshot.osm.coordSource;
            osmQueryLatitude = snapshot.osm.queryLatitude;
            osmQueryLongitude = snapshot.osm.queryLongitude;
            osmRole = snapshot.osmRole;
            osmFeature = snapshot.osmFeature;
            buildingTag = snapshot.buildingTag;

            floors = snapshot.floors;
            grossFloorArea = snapshot.grossFloorArea;
            useClassification = snapshot.useClassification;
            constructionYear = snapshot.constructionYear;

            if (!snapshot.osm.hasData || !snapshot.osm.buildingMatch.hasMatch)
                return;

            BuildingOsmMatchData match = snapshot.osm.buildingMatch;
            osmLabel = match.label;
            osmMatchMethod = match.matchMethod;
            osmMatchScore = match.matchScore;
            osmDistanceM = match.distanceM;

            if (match.tags == null || match.tags.Count == 0)
                return;

            osmTags = new List<BuildingOsmTagEntry>(match.tags.Count);
            osmTags.AddRange(match.tags);
        }

        public void Clear()
        {
            buildingId = null;
            tileId = null;
            sourceName = null;
            gpsHasData = false;
            gpsLatitude = 0d;
            gpsLongitude = 0d;
            gpsText = null;
            address = null;
            street = null;
            houseNumber = null;
            city = null;
            postcode = null;
            osmHasData = false;
            osmCoordSource = null;
            osmQueryLatitude = 0d;
            osmQueryLongitude = 0d;
            osmRole = null;
            osmFeature = null;
            buildingTag = null;
            osmLabel = null;
            osmMatchMethod = null;
            osmMatchScore = 0f;
            osmDistanceM = 0f;
            floors = 0;
            grossFloorArea = 0f;
            useClassification = null;
            constructionYear = 0;
            osmTags?.Clear();
        }
    }
}
