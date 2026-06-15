namespace ZGConnect
{
    /// <summary>Runtime building metadata loaded on demand (no MonoBehaviour on the building GO).</summary>
    public sealed class BuildingInfoSnapshot
    {
        public string buildingId;
        public string tileId;
        public string sourceName;
        public BuildingGpsData gps;
        public BuildingOsmData osm;
        public string address;
        public int floors;
        public float grossFloorArea;
        public string useClassification;
        public int constructionYear;

        /// <summary>Formatted GPS coordinates (building GPS, or OSM query fallback).</summary>
        public string gpsText;
        public string street;
        public string houseNumber;
        public string city;
        public string postcode;
        public string buildingTag;
        public string osmRole;
        public string osmFeature;

        public static BuildingInfoSnapshot FromEntry(string tileId, string buildingObjectName, BuildingEntryJson entry)
        {
            if (entry == null)
                return null;

            var snapshot = new BuildingInfoSnapshot
            {
                tileId = tileId,
                buildingId = BuildingSurfaceUtility.ExtractBuildingIdFromObjectName(buildingObjectName),
                sourceName = !string.IsNullOrEmpty(entry.Name) ? entry.Name : buildingObjectName,
            };

            BuildingMetadataApplier.ApplyToSnapshot(snapshot, entry);
            return snapshot;
        }

        public string GetOsmTag(string tagKey) =>
            BuildingOsmMetadataUtility.GetBuildingMatchTag(osm, tagKey);

        public void CopyToInspectorData(BuildingInfoSnapshotInspectorData target) =>
            target?.CopyFrom(this);
    }
}
