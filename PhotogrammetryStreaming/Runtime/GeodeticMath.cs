using System;
using UnityEngine;

namespace ZGConnect.PhotogrammetryStreaming
{
    public static class GeodeticMath
    {
        const double Wgs84A = 6378137.0;
        const double Wgs84F = 1.0 / 298.257223563;

        public static Vector3d Wgs84ToEcef(double latDeg, double lonDeg, double heightM)
        {
            double lat = latDeg * Math.PI / 180.0;
            double lon = lonDeg * Math.PI / 180.0;
            double e2 = 2 * Wgs84F - Wgs84F * Wgs84F;
            double sinLat = Math.Sin(lat);
            double cosLat = Math.Cos(lat);
            double n = Wgs84A / Math.Sqrt(1.0 - e2 * sinLat * sinLat);
            double x = (n + heightM) * cosLat * Math.Cos(lon);
            double y = (n + heightM) * cosLat * Math.Sin(lon);
            double z = (n * (1.0 - e2) + heightM) * sinLat;
            return new Vector3d(x, y, z);
        }

        public static void EcefToEnu(
            Vector3d ecef,
            Vector3d anchorEcef,
            double anchorLatDeg,
            double anchorLonDeg,
            out double east,
            out double north,
            out double up)
        {
            double lat = anchorLatDeg * Math.PI / 180.0;
            double lon = anchorLonDeg * Math.PI / 180.0;
            double sinLat = Math.Sin(lat);
            double cosLat = Math.Cos(lat);
            double sinLon = Math.Sin(lon);
            double cosLon = Math.Cos(lon);

            double dx = ecef.x - anchorEcef.x;
            double dy = ecef.y - anchorEcef.y;
            double dz = ecef.z - anchorEcef.z;

            east = -sinLon * dx + cosLon * dy;
            north = -sinLat * cosLon * dx - sinLat * sinLon * dy + cosLat * dz;
            up = cosLat * cosLon * dx + cosLat * sinLon * dy + sinLat * dz;
        }

        public static Vector3 EnuToUnity(double east, double north, double up, float unityYScale = 1f)
        {
            return new Vector3((float)east, (float)up * unityYScale, (float)north);
        }

        public static double Rad2Deg(double rad) => rad * 180.0 / Math.PI;
        public static double Deg2Rad(double deg) => deg * Math.PI / 180.0;

        public static void EcefToWgs84(Vector3d ecef, out double latDeg, out double lonDeg, out double heightM)
        {
            double e2 = 2 * Wgs84F - Wgs84F * Wgs84F;
            double ep2 = e2 / (1.0 - e2);
            double p = Math.Sqrt(ecef.x * ecef.x + ecef.y * ecef.y);
            double theta = Math.Atan2(ecef.z * Wgs84A, p * (1.0 - e2));
            double sinTheta = Math.Sin(theta);
            double cosTheta = Math.Cos(theta);
            latDeg = Rad2Deg(Math.Atan2(
                ecef.z + ep2 * (1.0 - e2) * Wgs84A * sinTheta * sinTheta * sinTheta,
                p - e2 * Wgs84A * cosTheta * cosTheta * cosTheta));
            lonDeg = Rad2Deg(Math.Atan2(ecef.y, ecef.x));
            double sinLat = Math.Sin(Deg2Rad(latDeg));
            double n = Wgs84A / Math.Sqrt(1.0 - e2 * sinLat * sinLat);
            heightM = p / Math.Cos(Deg2Rad(latDeg)) - n;
        }

        public static bool Wgs84RectsOverlap(
            double southA, double westA, double northA, double eastA,
            double southB, double westB, double northB, double eastB)
        {
            if (northA < southB || southA > northB)
                return false;
            return LongitudeRangesOverlap(westA, eastA, westB, eastB);
        }

        static bool LongitudeRangesOverlap(double westA, double eastA, double westB, double eastB)
        {
            if (westA <= eastA && westB <= eastB)
                return !(eastA < westB || eastB < westA);

            if (westA > eastA && westB > eastB)
                return true;

            if (westA > eastA)
                return westB <= eastA || eastB >= westA;

            return westA <= eastB || eastA >= westB;
        }
    }

    public readonly struct Vector3d
    {
        public readonly double x;
        public readonly double y;
        public readonly double z;

        public Vector3d(double x, double y, double z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public static Vector3d Zero => new(0, 0, 0);

        public double Magnitude => Math.Sqrt(x * x + y * y + z * z);

        public static Vector3d operator +(Vector3d a, Vector3d b) =>
            new(a.x + b.x, a.y + b.y, a.z + b.z);

        public static Vector3d operator -(Vector3d a, Vector3d b) =>
            new(a.x - b.x, a.y - b.y, a.z - b.z);

        public static Vector3d operator *(Vector3d a, double s) =>
            new(a.x * s, a.y * s, a.z * s);

        public Vector3 ToVector3() => new((float)x, (float)y, (float)z);
    }
}
