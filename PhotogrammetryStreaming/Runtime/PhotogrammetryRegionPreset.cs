using UnityEngine;

namespace ZGConnect.PhotogrammetryStreaming
{
    public enum PhotogrammetryRegionKind
    {
        ZagrebCityBounds = 0,
        ZagrebCityCenter = 1,
        CustomWgs84Rect = 2,
    }

    [CreateAssetMenu(fileName = "PhotogrammetryRegionPreset", menuName = "ZG Connect/Photogrammetry/Region Preset")]
    public class PhotogrammetryRegionPreset : ScriptableObject
    {
        public PhotogrammetryRegionKind kind = PhotogrammetryRegionKind.ZagrebCityBounds;

        [Header("Custom WGS84 (used when kind = CustomWgs84Rect)")]
        public double minLat = 45.724;
        public double minLon = 15.818;
        public double maxLat = 45.901;
        public double maxLon = 16.109;

        [Header("Anchor (city center for ECEF bridge)")]
        public double anchorLat = 45.81286;
        public double anchorLon = 15.97862;
        public double anchorHeightM = 138.75;

        public void GetWgs84Bounds(out double south, out double west, out double north, out double east)
        {
            switch (kind)
            {
                case PhotogrammetryRegionKind.ZagrebCityCenter:
                    south = 45.75;
                    west = 15.85;
                    north = 45.87;
                    east = 16.10;
                    break;
                case PhotogrammetryRegionKind.CustomWgs84Rect:
                    south = minLat;
                    west = minLon;
                    north = maxLat;
                    east = maxLon;
                    break;
                default:
                    south = 45.724;
                    west = 15.818;
                    north = 45.901;
                    east = 16.109;
                    break;
            }
        }
    }
}
