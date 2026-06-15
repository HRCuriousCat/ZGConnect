using System;
using UnityEngine;

namespace ZGConnect.PhotogrammetryStreaming
{
    /// <summary>
    /// Inspector-tweakable rotation knobs for Google 3D Tiles GLB placement experiments.
    /// </summary>
    [Serializable]
    public class PhotogrammetryRotationTuning
    {
        public enum PlacementMode
        {
            CesiumParity,
            LegacyEuler
        }

        public enum RotationCombineOrder
        {
            EnuThenUndoGltfAxis,
            UndoGltfAxisThenEnu,
            EnuOnly,
            UndoGltfAxisOnly,
            OffsetOnly,
            Identity
        }

        [Header("Placement")]
        [Tooltip("CesiumParity: tileTransform * RTC * Y_UP_TO_Z_UP matrix chain (recommended). LegacyEuler: manual pivot tuning.")]
        public PlacementMode placementMode = PlacementMode.CesiumParity;

        [Tooltip("Multiply full 3D Tiles TransformToEcef into the chain. Off for Google GLB (rtc + glTF nodes are enough).")]
        public bool applyTileTreeTransform;

        [Tooltip("Extra Euler on Content pivot after Cesium matrix placement (fine-tune global tileset roll/pitch/yaw).")]
        public Vector3 cesiumContentPivotEulerOffset;

        [Header("Legacy Euler mode (ignored when CesiumParity)")]
        [Tooltip("Rotate RTC ECEF-aligned vertices using local East-North-Up at rtcCenter.")]
        public bool applyEnuRtcRotation;

        [Tooltip("Use inverse ENU rotation instead of forward.")]
        public bool invertEnuRtcRotation;

        [Header("3D Tiles glTF axis convention")]
        [Tooltip("Undo the Y-up to Z-up rotation often baked into Google tile glTF nodes.")]
        public bool undoGltfYUpToZUp;

        [Tooltip("Euler degrees for the baked glTF axis fix (default -90 X).")]
        public Vector3 gltfYUpToZUpEuler = new Vector3(-90f, 0f, 0f);

        [Tooltip("How ENU and glTF axis rotations are multiplied.")]
        public RotationCombineOrder combineOrder = RotationCombineOrder.EnuThenUndoGltfAxis;

        [Tooltip("Extra Euler degrees applied last on the Content pivot.")]
        public Vector3 contentPivotEulerOffset = Vector3.zero;

        [Header("glTF node cleanup after import")]
        public bool stripGltfLargeEcefTranslation;

        [Tooltip("Strip baked -90 X (or similar) from imported glTF root nodes.")]
        public bool stripGltfBakedYUpToZUp;

        [Range(1f, 15f)]
        public float stripYUpToZUpAngleTolerance = 5f;

        [Header("3D Tiles tree transform")]
        [Tooltip("Multiply tile node TransformToEcef rotation (from tileset JSON) into Content pivot.")]
        public bool applyTileNodeRotation;

        public Quaternion BuildContentPivotRotation(
            EcefToUnityTransform transform,
            Vector3d rtcCenterEcef,
            Matrix4d? tileTransformToEcef)
        {
            Quaternion enu = Quaternion.identity;
            if (applyEnuRtcRotation)
            {
                enu = transform.EcefRtcFrameToUnityRotation(rtcCenterEcef);
                if (invertEnuRtcRotation)
                    enu = Quaternion.Inverse(enu);
            }

            Quaternion gltfAxis = Quaternion.Euler(gltfYUpToZUpEuler);
            Quaternion undoGltfAxis = undoGltfYUpToZUp ? Quaternion.Inverse(gltfAxis) : gltfAxis;

            Quaternion combined = combineOrder switch
            {
                RotationCombineOrder.EnuThenUndoGltfAxis => enu * undoGltfAxis,
                RotationCombineOrder.UndoGltfAxisThenEnu => undoGltfAxis * enu,
                RotationCombineOrder.EnuOnly => enu,
                RotationCombineOrder.UndoGltfAxisOnly => undoGltfAxis,
                RotationCombineOrder.OffsetOnly => Quaternion.identity,
                RotationCombineOrder.Identity => Quaternion.identity,
                _ => enu * undoGltfAxis
            };

            if (applyTileNodeRotation && tileTransformToEcef.HasValue)
                combined = EcefMatrixRotationToUnity(tileTransformToEcef.Value, transform) * combined;

            if (contentPivotEulerOffset.sqrMagnitude > 0.0001f)
                combined = combined * Quaternion.Euler(contentPivotEulerOffset);

            return combined;
        }

        static Quaternion EcefMatrixRotationToUnity(Matrix4d m, EcefToUnityTransform t)
        {
            Vector3 unityX = t.EcefOffsetToUnity(new Vector3d(m.At(0, 0), m.At(1, 0), m.At(2, 0))).normalized;
            Vector3 unityY = t.EcefOffsetToUnity(new Vector3d(m.At(0, 1), m.At(1, 1), m.At(2, 1))).normalized;
            Vector3 unityZ = t.EcefOffsetToUnity(new Vector3d(m.At(0, 2), m.At(1, 2), m.At(2, 2))).normalized;

            var basis = Matrix4x4.identity;
            basis.SetColumn(0, unityX);
            basis.SetColumn(1, unityY);
            basis.SetColumn(2, unityZ);
            return basis.rotation;
        }

        public int ComputeFingerprint()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + placementMode.GetHashCode();
                h = h * 31 + applyTileTreeTransform.GetHashCode();
                h = h * 31 + cesiumContentPivotEulerOffset.GetHashCode();
                h = h * 31 + applyEnuRtcRotation.GetHashCode();
                h = h * 31 + invertEnuRtcRotation.GetHashCode();
                h = h * 31 + undoGltfYUpToZUp.GetHashCode();
                h = h * 31 + gltfYUpToZUpEuler.GetHashCode();
                h = h * 31 + combineOrder.GetHashCode();
                h = h * 31 + contentPivotEulerOffset.GetHashCode();
                h = h * 31 + stripGltfLargeEcefTranslation.GetHashCode();
                h = h * 31 + stripGltfBakedYUpToZUp.GetHashCode();
                h = h * 31 + stripYUpToZUpAngleTolerance.GetHashCode();
                h = h * 31 + applyTileNodeRotation.GetHashCode();
                return h;
            }
        }
    }
}
