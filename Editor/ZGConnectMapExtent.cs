using UnityEngine;

namespace ZGConnect.Editor
{
    public struct ZGConnectMapGeorefBounds
    {
        public int MinE;
        public int MaxE;
        public int MinN;
        public int MaxN;

        public bool IsValid => MaxE > MinE && MaxN > MinN;
    }

    /// <summary>
    /// Geographic extent for the Zagreb overview map (north-up).
    /// WGS84 corners match preset "Granice grada Zagreba";
    /// EPSG:3765 values align with <see cref="ZGConnect.ZGConnectCoordinates"/>.
    /// </summary>
    public static class ZGConnectMapExtent
    {
        public const string PlaceholderAssetPath =
            "Assets/ZGConnect/Assets/UI/zagreb_map_overview_placeholder.png";

        // WGS84
        public const float MinLat = 45.724f;
        public const float MinLon = 15.818f;
        public const float MaxLat = 45.901f;
        public const float MaxLon = 16.109f;

        // EPSG:3765 fallback when heightmap coverage is unavailable
        public const int MinE = 442000;
        public const int MaxE = 481000;
        public const int MinN = 5061000;
        public const int MaxN = 5079000;

        public static double ExtentWidthM  => MaxE - MinE;
        public static double ExtentHeightM => MaxN - MinN;

        public static float EToU(double e) =>
            (float)((e - MinE) / ExtentWidthM);

        public static float NToV(double n) =>
            (float)((n - MinN) / ExtentHeightM);

        public static double UToE(float u) =>
            MinE + u * ExtentWidthM;

        public static double VToN(float v) =>
            MinN + v * ExtentHeightM;

        public static void GetCityBoundingBox(out int minE, out int maxE, out int minN, out int maxN)
        {
            minE = MinE;
            maxE = MaxE;
            minN = MinN;
            maxN = MaxN;
        }

        public static void GetHeightmapCoverageGeoref(
            ZGConnectMapGeorefBounds overviewExtent,
            out int minE,
            out int maxE,
            out int minN,
            out int maxN)
        {
            if (overviewExtent.IsValid)
            {
                minE = overviewExtent.MinE;
                maxE = overviewExtent.MaxE;
                minN = overviewExtent.MinN;
                maxN = overviewExtent.MaxN;
                return;
            }

            GetCityBoundingBox(out minE, out maxE, out minN, out maxN);
        }

        public const int PlaceholderWidth = 2048;

        public static int ExpectedPlaceholderHeight(int width = PlaceholderWidth) =>
            Mathf.Max(1, Mathf.RoundToInt(width * (float)(ExtentHeightM / ExtentWidthM)));

        public static bool IsProceduralPlaceholder(Texture2D texture)
        {
            if (texture == null)
                return false;

            int expectedHeight = ExpectedPlaceholderHeight(PlaceholderWidth);
            return texture.width == PlaceholderWidth &&
                   Mathf.Abs(texture.height - expectedHeight) <= 2;
        }
    }
}
