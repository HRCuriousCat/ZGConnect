using System;
using UnityEngine;

namespace ZGConnect
{
    /// <summary>
    /// Converts between Unity world-space positions and real-world GPS coordinates.
    ///
    /// Coordinate chain:
    ///   Unity (x, y, z)
    ///     ↕  add / subtract dataset origin offset
    ///   EPSG:3765 — HTRS96 / Croatia TM  (easting, northing in metres)
    ///     ↕  Transverse Mercator (de)projection
    ///   WGS84  (latitude, longitude in decimal degrees)
    ///
    /// The HTRS96 datum is based on GRS80, which is virtually identical to WGS84
    /// for all practical purposes (< 1 m difference over Croatia).
    ///
    /// Call Configure() once at startup — TerrainStreamingController does this
    /// automatically when the CityDataset is loaded.
    /// </summary>
    public static class ZGConnectCoordinates
    {
        // ── Configuration ──────────────────────────────────────────────────────

        /// <summary>EPSG:3765 easting that maps to Unity X = 0.</summary>
        public static double OriginEasting  { get; private set; } = 442000.0;

        /// <summary>EPSG:3765 northing that maps to Unity Z = 0.</summary>
        public static double OriginNorthing { get; private set; } = 5051000.0;

        /// <summary>
        /// Real-world elevation (metres above sea level) at Unity Y = 0.
        /// Equal to the dataset's global minimum height.
        /// </summary>
        public static float MinHeightMetres { get; private set; } = 95f;

        /// <summary>True after Configure() has been called.</summary>
        public static bool IsConfigured { get; private set; }

        /// <summary>
        /// Initialises the coordinate system from dataset values.
        /// Called automatically by TerrainStreamingController on startup.
        /// </summary>
        public static void Configure(int originX, int originY, float minHeightM)
        {
            OriginEasting   = originX;
            OriginNorthing  = originY;
            MinHeightMetres = minHeightM;
            IsConfigured    = true;
        }

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Unity world position → (latitude, longitude) in decimal degrees (WGS84).
        /// </summary>
        public static (double lat, double lon) UnityToWGS84(Vector3 pos)
        {
            double E = pos.x + OriginEasting;
            double N = pos.z + OriginNorthing;
            return EPSG3765ToWGS84(E, N);
        }

        /// <summary>
        /// WGS84 decimal degrees → Unity world position.
        /// </summary>
        /// <param name="heightY">Unity Y value to assign (defaults to 0).</param>
        public static Vector3 WGS84ToUnity(double lat, double lon, float heightY = 0f)
        {
            var (E, N) = WGS84ToEPSG3765(lat, lon);
            return new Vector3(
                (float)(E - OriginEasting),
                heightY,
                (float)(N - OriginNorthing));
        }

        /// <summary>
        /// Unity Y value → approximate real-world elevation in metres above sea level.
        /// Accurate to ± 1 m across the dataset extent.
        /// </summary>
        public static float UnityYToElevation(float unityY) => unityY + MinHeightMetres;

        /// <summary>
        /// Formats GPS coordinates for HUD display:
        ///   "45.81234° N  15.97654° E"
        /// </summary>
        public static string Format(double lat, double lon)
        {
            string ns = lat  >= 0 ? "N" : "S";
            string ew = lon  >= 0 ? "E" : "W";
            return $"{Math.Abs(lat):F5}° {ns}   {Math.Abs(lon):F5}° {ew}";
        }

        // ── EPSG:3765 (HTRS96 / Croatia TM) ↔ WGS84 ──────────────────────────

        /// <summary>
        /// EPSG:3765 (easting, northing) → WGS84 (lat, lon) decimal degrees.
        /// Inverse Transverse Mercator — accuracy &lt; 1 mm across Croatia.
        ///
        /// Source: Snyder (1987) "Map Projections — A Working Manual", USGS PP 1395,
        ///         and EPSG Guidance Note 7-2.
        /// </summary>
        public static (double lat, double lon) EPSG3765ToWGS84(double E, double N)
        {
            // GRS80 ellipsoid
            const double a  = 6378137.0;
            const double f  = 1.0 / 298.257222101;
            double e2  = 2*f - f*f;
            double e2_ = e2 / (1 - e2);   // second eccentricity squared

            // EPSG:3765 projection parameters
            const double k0   = 0.9999;
            const double lon0 = 16.5 * Deg2Rad;   // central meridian
            const double FE   = 500000.0;           // false easting
            // false northing = 0

            double x = E - FE;
            double M = N / k0;

            // Footpoint latitude from meridional arc
            double mu = M / (a * (1 - e2/4 - 3*e2*e2/64 - 5*e2*e2*e2/256));

            double e1   = (1 - Math.Sqrt(1 - e2)) / (1 + Math.Sqrt(1 - e2));
            double e1_2 = e1  * e1;
            double e1_3 = e1_2 * e1;
            double e1_4 = e1_3 * e1;

            double phi1 = mu
                + ( 3.0*e1/2     - 27.0*e1_3/32  ) * Math.Sin(2*mu)
                + (21.0*e1_2/16  - 55.0*e1_4/32  ) * Math.Sin(4*mu)
                + (151.0*e1_3/96                  ) * Math.Sin(6*mu)
                + (1097.0*e1_4/512                ) * Math.Sin(8*mu);

            double sinP = Math.Sin(phi1);
            double cosP = Math.Cos(phi1);
            double tanP = Math.Tan(phi1);
            double sp2  = sinP * sinP;

            double T1 = tanP * tanP;
            double C1 = e2_ * cosP * cosP;
            double N1 = a / Math.Sqrt(1 - e2 * sp2);
            double R1 = a * (1 - e2) / Math.Pow(1 - e2 * sp2, 1.5);
            double D  = x / (N1 * k0);
            double D2 = D * D;

            double lat = phi1 - (N1 * tanP / R1) * (
                  D2/2
                - (5 + 3*T1 + 10*C1 - 4*C1*C1 - 9*e2_) * D2*D2/24
                + (61 + 90*T1 + 298*C1 + 45*T1*T1 - 252*e2_ - 3*C1*C1) * D2*D2*D2/720
            );

            double lon = lon0 + (
                  D
                - (1 + 2*T1 + C1) * D2*D/6
                + (5 - 2*C1 + 28*T1 - 3*C1*C1 + 8*e2_ + 24*T1*T1) * D2*D2*D/120
            ) / cosP;

            return (lat * Rad2Deg, lon * Rad2Deg);
        }

        /// <summary>
        /// WGS84 (lat, lon) decimal degrees → EPSG:3765 (easting, northing).
        /// Forward Transverse Mercator — accuracy &lt; 1 mm across Croatia.
        /// </summary>
        public static (double E, double N) WGS84ToEPSG3765(double latDeg, double lonDeg)
        {
            const double a  = 6378137.0;
            const double f  = 1.0 / 298.257222101;
            double e2  = 2*f - f*f;
            double e2_ = e2 / (1 - e2);

            const double k0   = 0.9999;
            const double lon0 = 16.5 * Deg2Rad;
            const double FE   = 500000.0;

            double phi = latDeg * Deg2Rad;
            double lam = lonDeg * Deg2Rad;

            double sinP = Math.Sin(phi);
            double cosP = Math.Cos(phi);
            double tanP = Math.Tan(phi);
            double sp2  = sinP * sinP;

            double T  = tanP * tanP;
            double C  = e2_ * cosP * cosP;
            double A  = cosP * (lam - lon0);
            double A2 = A * A;
            double N1 = a / Math.Sqrt(1 - e2 * sp2);

            // Meridional arc from equator
            double e4 = e2 * e2, e6 = e4 * e2;
            double Marc = a * (
                  (1      - e2/4    - 3*e4/64    - 5*e6/256  ) * phi
                - (3*e2/8 + 3*e4/32 + 45*e6/1024) * Math.Sin(2*phi)
                + (15*e4/256 + 45*e6/1024)         * Math.Sin(4*phi)
                - (35*e6/3072)                     * Math.Sin(6*phi)
            );

            double easting = FE + k0 * N1 * (
                  A
                + (1 - T + C) * A2*A/6
                + (5 - 18*T + T*T + 72*C - 58*e2_) * A2*A2*A/120
            );

            double northing = k0 * (Marc + N1 * tanP * (
                  A2/2
                + (5 - T + 9*C + 4*C*C) * A2*A2/24
                + (61 - 58*T + T*T + 600*C - 330*e2_) * A2*A2*A2/720
            ));

            return (easting, northing);
        }

        // ── Constants ──────────────────────────────────────────────────────────

        private const double Deg2Rad = Math.PI / 180.0;
        private const double Rad2Deg = 180.0 / Math.PI;
    }
}
