using System.Collections.Generic;

namespace ZGConnect
{
    /// <summary>
    /// Copies fields from a buildings_{tileId}.json entry into a <see cref="BuildingData"/> component.
    /// </summary>
    public static class BuildingMetadataApplier
    {
        public static void Apply(BuildingData target, BuildingEntryJson entry)
        {
            if (target == null || entry == null)
                return;

            if (!string.IsNullOrEmpty(entry.Name))
                target.sourceName = entry.Name;

            ApplyGps(out target.gps, entry.Gps);
            ApplyOsm(out target.osm, entry.Osm);
        }

        public static void ApplyToSnapshot(BuildingInfoSnapshot target, BuildingEntryJson entry)
        {
            if (target == null || entry == null)
                return;

            if (!string.IsNullOrEmpty(entry.Name))
                target.sourceName = entry.Name;

            ApplyGps(out target.gps, entry.Gps);
            ApplyOsm(out target.osm, entry.Osm);
            BuildingOsmMetadataUtility.EnrichDisplayFields(target);
        }

        static void ApplyGps(out BuildingGpsData gps, BuildingGpsJson source)
        {
            if (source == null)
            {
                gps = default;
                return;
            }

            gps = new BuildingGpsData
            {
                hasData = true,
                latitude = source.Lat,
                longitude = source.Lon,
            };
        }

        static void ApplyOsm(out BuildingOsmData osm, BuildingOsmBlockJson source)
        {
            if (source == null)
            {
                osm = default;
                return;
            }

            var data = new BuildingOsmData
            {
                hasData = true,
                coordSource = source.CoordSource ?? string.Empty,
                queryLatitude = source.QueryLat,
                queryLongitude = source.QueryLon,
            };

            if (source.Building != null)
            {
                data.buildingMatch = new BuildingOsmMatchData
                {
                    hasMatch = true,
                    osmType = source.Building.OsmType ?? string.Empty,
                    osmId = source.Building.OsmId,
                    role = source.Building.Role ?? string.Empty,
                    label = source.Building.Label ?? string.Empty,
                    matchMethod = source.Building.MatchMethod ?? string.Empty,
                    matchScore = source.Building.MatchScore,
                    distanceM = source.Building.DistanceM,
                    tags = ToTagList(source.Building.Tags),
                };
            }

            if (source.AtPoint != null && source.AtPoint.Count > 0)
            {
                data.atPoint = new List<BuildingOsmAtPointData>(source.AtPoint.Count);
                foreach (BuildingOsmAtPointJson feature in source.AtPoint)
                {
                    if (feature == null)
                        continue;

                    data.atPoint.Add(new BuildingOsmAtPointData
                    {
                        osmType = feature.OsmType ?? string.Empty,
                        osmId = feature.OsmId,
                        role = feature.Role ?? string.Empty,
                        label = feature.Label ?? string.Empty,
                        areaM2 = feature.AreaM2,
                        tags = ToTagList(feature.Tags),
                    });
                }
            }

            osm = data;
        }

        private static List<BuildingOsmTagEntry> ToTagList(Dictionary<string, string> tags)
        {
            if (tags == null || tags.Count == 0)
                return new List<BuildingOsmTagEntry>();

            var list = new List<BuildingOsmTagEntry>(tags.Count);
            foreach (KeyValuePair<string, string> kv in tags)
            {
                list.Add(new BuildingOsmTagEntry
                {
                    key   = kv.Key ?? string.Empty,
                    value = kv.Value ?? string.Empty,
                });
            }

            return list;
        }
    }
}

