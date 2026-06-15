using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ZGConnect
{
    /// <summary>GPS / coordinate helpers: conversions, distances, bearings, geofencing, links.</summary>
    public sealed partial class ZGConnectToolkit
    {
        const double EarthRadiusMeters = 6371000.0;

        // ── Conversions ────────────────────────────────────────────────────────

        /// <summary>Unity world position → WGS84 (latitude, longitude) in decimal degrees.</summary>
        public static (double lat, double lon) WorldToGps(Vector3 worldPos) =>
            ZGConnectCoordinates.UnityToWGS84(worldPos);

        /// <summary>
        /// WGS84 → Unity world position, snapped onto the terrain surface when terrain is
        /// loaded at that point (plus optional offset above ground). Falls back to Y = 0
        /// when no terrain is available there yet.
        /// </summary>
        public Vector3 GpsToWorld(double latitude, double longitude, float heightAboveGround = 0f)
        {
            Vector3 pos = ZGConnectCoordinates.WGS84ToUnity(latitude, longitude);
            if (TryGetGroundHeight(pos, out float groundY))
                pos.y = groundY;
            return pos + Vector3.up * heightAboveGround;
        }

        /// <summary>WGS84 → Unity world position with an explicit Y value (no terrain lookup).</summary>
        public static Vector3 GpsToWorldFlat(double latitude, double longitude, float unityY = 0f) =>
            ZGConnectCoordinates.WGS84ToUnity(latitude, longitude, unityY);

        /// <summary>Unity world position → EPSG:3765 / HTRS96 Croatia TM (easting, northing) in metres.</summary>
        public static (double easting, double northing) WorldToEpsg3765(Vector3 worldPos) =>
            (worldPos.x + ZGConnectCoordinates.OriginEasting, worldPos.z + ZGConnectCoordinates.OriginNorthing);

        /// <summary>EPSG:3765 (easting, northing) → Unity world position (Y = 0).</summary>
        public static Vector3 Epsg3765ToWorld(double easting, double northing, float unityY = 0f) =>
            new((float)(easting - ZGConnectCoordinates.OriginEasting),
                unityY,
                (float)(northing - ZGConnectCoordinates.OriginNorthing));

        /// <summary>Unity Y value → real-world elevation in metres above sea level.</summary>
        public static float UnityYToElevation(float unityY) =>
            ZGConnectCoordinates.UnityYToElevation(unityY);

        /// <summary>
        /// Real-world ground elevation (metres above sea level) at a world position.
        /// Returns false when no terrain is loaded there.
        /// </summary>
        public bool TryGetGroundElevation(Vector3 worldPos, out float elevationMeters)
        {
            if (TryGetGroundHeight(worldPos, out float groundY))
            {
                elevationMeters = ZGConnectCoordinates.UnityYToElevation(groundY);
                return true;
            }

            elevationMeters = 0f;
            return false;
        }

        // ── Distances and bearings ─────────────────────────────────────────────

        /// <summary>
        /// Great-circle (haversine) distance between two GPS points, in metres.
        /// </summary>
        public static double GpsDistanceMeters(double lat1, double lon1, double lat2, double lon2)
        {
            double dLat = (lat2 - lat1) * Math.PI / 180.0;
            double dLon = (lon2 - lon1) * Math.PI / 180.0;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                       + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0)
                       * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return EarthRadiusMeters * 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }

        /// <summary>
        /// Horizontal (XZ) distance between two world positions, in metres
        /// (1 Unity unit = 1 m in ZGConnect datasets).
        /// </summary>
        public static float HorizontalDistanceMeters(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>
        /// Compass bearing from one world position to another, in degrees
        /// (0 = north, 90 = east). Useful for compass needles and target arrows.
        /// </summary>
        public static float BearingDegrees(Vector3 from, Vector3 to)
        {
            float bearing = Mathf.Atan2(to.x - from.x, to.z - from.z) * Mathf.Rad2Deg;
            return (bearing + 360f) % 360f;
        }

        /// <summary>Compass bearing between two GPS points, in degrees (0 = north, 90 = east).</summary>
        public static double GpsBearingDegrees(double lat1, double lon1, double lat2, double lon2)
        {
            double p1 = lat1 * Math.PI / 180.0;
            double p2 = lat2 * Math.PI / 180.0;
            double dLon = (lon2 - lon1) * Math.PI / 180.0;
            double y = Math.Sin(dLon) * Math.Cos(p2);
            double x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dLon);
            double bearing = Math.Atan2(y, x) * 180.0 / Math.PI;
            return (bearing + 360.0) % 360.0;
        }

        /// <summary>Total length of a polyline path in metres (sum of segment lengths).</summary>
        public static float MeasurePathDistance(IList<Vector3> points)
        {
            if (points == null || points.Count < 2)
                return 0f;

            float total = 0f;
            for (int i = 1; i < points.Count; i++)
                total += Vector3.Distance(points[i - 1], points[i]);
            return total;
        }

        // ── Geofencing ─────────────────────────────────────────────────────────

        /// <summary>
        /// Point-in-polygon test over GPS coordinates (ray casting). Polygon vertices are
        /// Vector2(latitude, longitude); the polygon closes automatically.
        /// </summary>
        public static bool IsGpsInsidePolygon(double latitude, double longitude, IList<Vector2> gpsPolygon)
        {
            if (gpsPolygon == null || gpsPolygon.Count < 3)
                return false;

            bool inside = false;
            int count = gpsPolygon.Count;
            for (int i = 0, j = count - 1; i < count; j = i++)
            {
                double yi = gpsPolygon[i].x, xi = gpsPolygon[i].y;
                double yj = gpsPolygon[j].x, xj = gpsPolygon[j].y;

                bool intersects = (yi > latitude) != (yj > latitude)
                    && longitude < (xj - xi) * (latitude - yi) / (yj - yi) + xi;
                if (intersects)
                    inside = !inside;
            }

            return inside;
        }

        /// <summary>Point-in-polygon test for a world position against a GPS polygon (geofence zones).</summary>
        public static bool IsWorldPosInsideGpsPolygon(Vector3 worldPos, IList<Vector2> gpsPolygon)
        {
            (double lat, double lon) = ZGConnectCoordinates.UnityToWGS84(worldPos);
            return IsGpsInsidePolygon(lat, lon, gpsPolygon);
        }

        // ── Formatting and links ───────────────────────────────────────────────

        /// <summary>Formats GPS coordinates for display: "45.81234° N   15.97654° E".</summary>
        public static string FormatGps(double latitude, double longitude) =>
            ZGConnectCoordinates.Format(latitude, longitude);

        /// <summary>Formats the GPS position of a world point for display.</summary>
        public static string FormatGps(Vector3 worldPos)
        {
            (double lat, double lon) = ZGConnectCoordinates.UnityToWGS84(worldPos);
            return ZGConnectCoordinates.Format(lat, lon);
        }

        /// <summary>Google Maps URL for a GPS coordinate (sharing / debugging).</summary>
        public static string GetGoogleMapsUrl(double latitude, double longitude) =>
            string.Format(CultureInfo.InvariantCulture,
                "https://www.google.com/maps?q={0:F7},{1:F7}", latitude, longitude);

        /// <summary>Google Maps URL for a world position.</summary>
        public static string GetGoogleMapsUrl(Vector3 worldPos)
        {
            (double lat, double lon) = ZGConnectCoordinates.UnityToWGS84(worldPos);
            return GetGoogleMapsUrl(lat, lon);
        }

        /// <summary>OpenStreetMap URL for a GPS coordinate.</summary>
        public static string GetOpenStreetMapUrl(double latitude, double longitude) =>
            string.Format(CultureInfo.InvariantCulture,
                "https://www.openstreetmap.org/?mlat={0:F7}&mlon={1:F7}#map=18/{0:F5}/{1:F5}",
                latitude, longitude);
    }
}
