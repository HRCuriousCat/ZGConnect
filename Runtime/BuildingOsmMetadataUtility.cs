using System.Globalization;
using System.Text;

namespace ZGConnect
{
    /// <summary>Extracts common display fields from GPS / OSM building metadata.</summary>
    public static class BuildingOsmMetadataUtility
    {
        public const string TagStreet = "addr:street";
        public const string TagHouseNumber = "addr:housenumber";
        public const string TagCity = "addr:city";
        public const string TagPostcode = "addr:postcode";
        public const string TagFullAddress = "addr:full";
        public const string TagBuilding = "building";

        public static void EnrichDisplayFields(BuildingInfoSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            snapshot.gpsText = FormatGpsCoordinates(snapshot);
            snapshot.osmRole = GetBuildingMatchRole(snapshot.osm);
            snapshot.osmFeature = FormatOsmFeature(snapshot.osm);
            snapshot.buildingTag = GetBuildingMatchTag(snapshot.osm, TagBuilding);
            snapshot.houseNumber = GetBuildingMatchTag(snapshot.osm, TagHouseNumber);
            snapshot.street = GetBuildingMatchTag(snapshot.osm, TagStreet);
            snapshot.city = GetBuildingMatchTag(snapshot.osm, TagCity);
            snapshot.postcode = GetBuildingMatchTag(snapshot.osm, TagPostcode);

            if (string.IsNullOrWhiteSpace(snapshot.address))
                snapshot.address = ComposeAddress(snapshot);
        }

        public static string GetBuildingMatchTag(BuildingOsmData osm, string tagKey) =>
            TryGetBuildingMatchTag(osm, tagKey, out string value) ? value : null;

        public static bool TryGetBuildingMatchTag(BuildingOsmData osm, string tagKey, out string value)
        {
            value = null;
            if (!osm.hasData || !osm.buildingMatch.hasMatch || osm.buildingMatch.tags == null)
                return false;

            foreach (BuildingOsmTagEntry tag in osm.buildingMatch.tags)
            {
                if (string.Equals(tag.key, tagKey, System.StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(tag.value))
                {
                    value = tag.value.Trim();
                    return true;
                }
            }

            return false;
        }

        public static string GetBuildingMatchRole(BuildingOsmData osm)
        {
            if (!osm.hasData || !osm.buildingMatch.hasMatch || string.IsNullOrWhiteSpace(osm.buildingMatch.role))
                return null;

            return osm.buildingMatch.role.Trim();
        }

        public static string FormatOsmFeature(BuildingOsmData osm)
        {
            if (!osm.hasData || !osm.buildingMatch.hasMatch)
                return null;

            string type = string.IsNullOrWhiteSpace(osm.buildingMatch.osmType)
                ? "feature"
                : osm.buildingMatch.osmType.Trim();
            long id = osm.buildingMatch.osmId;
            return id > 0
                ? $"{type}/{id.ToString(CultureInfo.InvariantCulture)}"
                : type;
        }

        public static string FormatGpsCoordinates(BuildingInfoSnapshot snapshot)
        {
            if (snapshot == null)
                return null;

            if (snapshot.gps.hasData)
            {
                return FormatCoordinates(snapshot.gps.latitude, snapshot.gps.longitude);
            }

            if (snapshot.osm.hasData)
            {
                return FormatCoordinates(snapshot.osm.queryLatitude, snapshot.osm.queryLongitude);
            }

            return null;
        }

        public static string ComposeAddress(BuildingInfoSnapshot snapshot)
        {
            if (snapshot == null)
                return null;

            if (TryGetBuildingMatchTag(snapshot.osm, TagFullAddress, out string fullAddress))
                return fullAddress;

            return ComposeAddress(snapshot.street, snapshot.houseNumber, snapshot.postcode, snapshot.city);
        }

        public static string ComposeAddress(string street, string houseNumber, string postcode, string city)
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(street))
            {
                sb.Append(street.Trim());
                if (!string.IsNullOrWhiteSpace(houseNumber))
                {
                    sb.Append(' ');
                    sb.Append(houseNumber.Trim());
                }
            }
            else if (!string.IsNullOrWhiteSpace(houseNumber))
            {
                sb.Append(houseNumber.Trim());
            }

            string cityLine = ComposeCityLine(postcode, city);
            if (!string.IsNullOrWhiteSpace(cityLine))
            {
                if (sb.Length > 0)
                    sb.Append(", ");
                sb.Append(cityLine);
            }

            string text = sb.ToString().Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        static string ComposeCityLine(string postcode, string city)
        {
            bool hasPostcode = !string.IsNullOrWhiteSpace(postcode);
            bool hasCity = !string.IsNullOrWhiteSpace(city);

            if (hasPostcode && hasCity)
                return $"{postcode.Trim()} {city.Trim()}";

            if (hasPostcode)
                return postcode.Trim();

            return hasCity ? city.Trim() : null;
        }

        public static string FormatCoordinates(double latitude, double longitude) =>
            string.Format(
                CultureInfo.InvariantCulture,
                "{0:0.######}, {1:0.######}",
                latitude,
                longitude);
    }
}
