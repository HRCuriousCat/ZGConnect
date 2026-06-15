using UnityEngine;

namespace ZGConnect.PhotogrammetryStreaming
{
    /// <summary>
    /// Matches cesium-native / Cesium for Unity GLB tile placement:
    /// tileTransform * RTC * Y_UP_TO_Z_UP, then ECEF→Unity via georeference-style bridge.
    /// glTF node transforms remain on imported children (not stripped).
    /// </summary>
    public static class CesiumStyleTilePlacement
    {
        const double MinRtcMagnitude = 1_000_000.0;

        public static Matrix4d BuildTileTransformToEcef(
            Matrix4d tileTransformToEcef,
            Vector3d rtcCenterEcef,
            bool applyTileTreeTransform)
        {
            Matrix4d result = applyTileTreeTransform ? tileTransformToEcef : Matrix4d.Identity;
            if (rtcCenterEcef.Magnitude >= MinRtcMagnitude)
                result = Matrix4d.Multiply(result, Matrix4d.Translation(rtcCenterEcef));
            return Matrix4d.Multiply(result, Matrix4d.YUpToZUp);
        }

        public static Vector3d ResolveRtcCenter(Vector3d parsedRtc, Vector3d fallbackEcef) =>
            parsedRtc.Magnitude >= MinRtcMagnitude ? parsedRtc : fallbackEcef;

        /// <summary>
        /// Fold glTF root rotation/scale into the tile chain; ECEF translation stays in RTC on Content only.
        /// Avoids million-unit float localPosition on imported nodes (tile seam gaps).
        /// </summary>
        public static Matrix4d BuildModelToEcef(
            Matrix4d tileTransformToEcef,
            Vector3d rtcCenterEcef,
            bool applyTileTreeTransform,
            Matrix4d gltfRootNodeMatrix)
        {
            Matrix4d tileLevel = BuildTileTransformToEcef(tileTransformToEcef, rtcCenterEcef, applyTileTreeTransform);
            if (GltfGlbParser.IsNearIdentity(gltfRootNodeMatrix))
                return tileLevel;
            return Matrix4d.Multiply(tileLevel, GltfGlbParser.RotationScaleOnly(gltfRootNodeMatrix));
        }

        public static void ApplyTileTransformToUnity(
            Transform target,
            Transform parent,
            EcefToUnityTransform bridge,
            Matrix4d tileTransformToEcef,
            Vector3d rtcCenterEcef)
        {
            Matrix4d modelToEcef = BuildTileTransformToEcef(tileTransformToEcef, rtcCenterEcef, true);
            bridge.ApplyModelToEcefToUnityTransform(target, parent, modelToEcef);
        }
    }
}
