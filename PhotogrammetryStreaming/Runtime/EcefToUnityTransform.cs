using System;
using UnityEngine;
using ZGConnect;

namespace ZGConnect.PhotogrammetryStreaming
{
    /// <summary>
    /// Bridges Google 3D Tiles ECEF coordinates to Unity world space via EPSG:3765 anchor.
    /// </summary>
    public class EcefToUnityTransform
    {
        readonly Vector3d _anchorEcef;
        readonly double _anchorLat;
        readonly double _anchorLon;
        readonly Vector3 _unityAnchor;

        public Vector3d AnchorEcef => _anchorEcef;
        public Vector3 UnityAnchor => _unityAnchor;

        public EcefToUnityTransform(PhotogrammetryRegionPreset region)
        {
            _anchorLat = region.anchorLat;
            _anchorLon = region.anchorLon;
            _anchorEcef = GeodeticMath.Wgs84ToEcef(region.anchorLat, region.anchorLon, region.anchorHeightM);
            _unityAnchor = ZGConnectCoordinates.WGS84ToUnity(region.anchorLat, region.anchorLon, 0f);
        }

        public Vector3 EcefToUnity(Vector3d ecef)
        {
            GeodeticMath.EcefToEnu(ecef, _anchorEcef, _anchorLat, _anchorLon,
                out double east, out double north, out double up);
            Vector3 local = GeodeticMath.EnuToUnity(east, north, up);
            return _unityAnchor + local;
        }

        public Vector3 EcefOffsetToUnity(Vector3d ecefOffset)
        {
            GeodeticMath.EcefToEnu(ecefOffset, Vector3d.Zero, _anchorLat, _anchorLon,
                out double east, out double north, out double up);
            return GeodeticMath.EnuToUnity(east, north, up);
        }

        /// <summary>
        /// Google RTC vertices are ECEF-aligned offsets. Map ECEF basis at rtc to Unity (X=east, Y=up, Z=north).
        /// </summary>
        public Quaternion EcefRtcFrameToUnityRotation(Vector3d rtcCenterEcef)
        {
            GeodeticMath.EcefToWgs84(rtcCenterEcef, out double latDeg, out double lonDeg, out _);
            double lat = GeodeticMath.Deg2Rad(latDeg);
            double lon = GeodeticMath.Deg2Rad(lonDeg);
            double sinLat = Math.Sin(lat);
            double cosLat = Math.Cos(lat);
            double sinLon = Math.Sin(lon);
            double cosLon = Math.Cos(lon);

            var eastEcef = new Vector3d(-sinLon, cosLon, 0);
            var upEcef = new Vector3d(cosLat * cosLon, cosLat * sinLon, sinLat);

            Vector3 unityEast = EcefOffsetToUnity(eastEcef).normalized;
            Vector3 unityUp = EcefOffsetToUnity(upEcef).normalized;
            Vector3 unityNorth = Vector3.Cross(unityUp, unityEast).normalized;

            var basis = Matrix4x4.identity;
            basis.SetColumn(0, unityEast);
            basis.SetColumn(1, unityUp);
            basis.SetColumn(2, unityNorth);
            return basis.rotation;
        }

        /// <summary>
        /// Decompose an ECEF model matrix into Unity world TRS (Cesium GlobeAnchor equivalent).
        /// </summary>
        public void DecomposeModelToEcefToUnity(
            Matrix4d modelToEcef,
            out Vector3 worldPosition,
            out Quaternion worldRotation,
            out Vector3 worldScale)
        {
            Vector3 unityC0 = EcefOffsetToUnity(new Vector3d(
                modelToEcef.At(0, 0), modelToEcef.At(1, 0), modelToEcef.At(2, 0)));
            Vector3 unityC1 = EcefOffsetToUnity(new Vector3d(
                modelToEcef.At(0, 1), modelToEcef.At(1, 1), modelToEcef.At(2, 1)));
            Vector3 unityC2 = EcefOffsetToUnity(new Vector3d(
                modelToEcef.At(0, 2), modelToEcef.At(1, 2), modelToEcef.At(2, 2)));

            worldPosition = EcefToUnity(modelToEcef.TransformPoint(Vector3d.Zero));
            worldScale = new Vector3(unityC0.magnitude, unityC1.magnitude, unityC2.magnitude);
            if (worldScale.x < 1e-6f) worldScale.x = 1f;
            if (worldScale.y < 1e-6f) worldScale.y = 1f;
            if (worldScale.z < 1e-6f) worldScale.z = 1f;

            Vector3 forward = unityC2 / worldScale.z;
            Vector3 up = unityC1 / worldScale.y;
            if (forward.sqrMagnitude < 1e-8f || up.sqrMagnitude < 1e-8f)
                worldRotation = Quaternion.identity;
            else
                worldRotation = Quaternion.LookRotation(forward, up);
        }

        /// <summary>Maps a 3×3 ECEF rotation (e.g. Y_UP_TO_Z_UP × glTF root) to Unity world rotation.</summary>
        public Quaternion EcefBasisRotationToUnity(Matrix4d ecefRotation)
        {
            Vector3 unityX = EcefOffsetToUnity(new Vector3d(
                ecefRotation.At(0, 0), ecefRotation.At(1, 0), ecefRotation.At(2, 0)));
            Vector3 unityY = EcefOffsetToUnity(new Vector3d(
                ecefRotation.At(0, 1), ecefRotation.At(1, 1), ecefRotation.At(2, 1)));
            Vector3 unityZ = EcefOffsetToUnity(new Vector3d(
                ecefRotation.At(0, 2), ecefRotation.At(1, 2), ecefRotation.At(2, 2)));

            if (unityX.sqrMagnitude < 1e-8f || unityY.sqrMagnitude < 1e-8f || unityZ.sqrMagnitude < 1e-8f)
                return Quaternion.identity;

            unityX.Normalize();
            unityY.Normalize();
            unityZ.Normalize();

            var basis = Matrix4x4.identity;
            basis.SetColumn(0, unityX);
            basis.SetColumn(1, unityY);
            basis.SetColumn(2, unityZ);
            return basis.rotation;
        }

        public void ApplyModelToEcefToUnityTransform(Transform target, Transform parent, Matrix4d modelToEcef)
        {
            DecomposeModelToEcefToUnity(modelToEcef, out Vector3 worldPos, out Quaternion worldRot, out Vector3 scale);
            target.SetParent(parent, false);
            if (parent != null)
            {
                target.localPosition = parent.InverseTransformPoint(worldPos);
                target.localRotation = Quaternion.Inverse(parent.rotation) * worldRot;
                Vector3 parentScale = parent.lossyScale;
                target.localScale = new Vector3(
                    parentScale.x > 1e-6f ? scale.x / parentScale.x : scale.x,
                    parentScale.y > 1e-6f ? scale.y / parentScale.y : scale.y,
                    parentScale.z > 1e-6f ? scale.z / parentScale.z : scale.z);
            }
            else
            {
                target.SetPositionAndRotation(worldPos, worldRot);
                target.localScale = scale;
            }
        }

        /// <summary>Validate landmark alignment against ZGConnectCoordinates (logs error if &gt; 2 m).</summary>
        public void ValidateLandmark(string name, double lat, double lon, double heightM = 0)
        {
            Vector3d ecef = GeodeticMath.Wgs84ToEcef(lat, lon, heightM);
            Vector3 fromEcef = EcefToUnity(ecef);
            Vector3 fromCoords = ZGConnectCoordinates.WGS84ToUnity(lat, lon, 0f);
            float err = Vector3.Distance(new Vector3(fromEcef.x, 0f, fromEcef.z),
                new Vector3(fromCoords.x, 0f, fromCoords.z));
            if (err > 2f)
                Debug.LogWarning($"[Photogrammetry] Landmark '{name}' horizontal error {err:F2} m (ECEF bridge vs ZGConnectCoordinates).");
            else
                Debug.Log($"[Photogrammetry] Landmark '{name}' horizontal error {err:F2} m — OK.");
        }
    }
}
