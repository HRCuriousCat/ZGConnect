using System.Threading.Tasks;
using GLTFast;
using UnityEngine;

namespace ZGConnect.PhotogrammetryStreaming
{
    public static class GltFastTileInstantiator
    {
        const float AbsoluteEcefTranslationThreshold = 100_000f;

        public sealed class InstantiateResult
        {
            public bool Success;
            public GameObject Root;
            public PhotogrammetryTileHolder Holder;
            public string Copyright;
            public long EstimatedBytes;
        }

        public static async Task<InstantiateResult> InstantiateAsync(
            byte[] glbBytes,
            Transform parent,
            EcefToUnityTransform transform,
            Vector3d rtcCenterEcef,
            string tileName,
            PhotogrammetryRotationTuning rotationTuning = null,
            Matrix4d? tileTransformToEcef = null)
        {
            var result = new InstantiateResult();
            if (glbBytes == null || glbBytes.Length == 0)
                return result;

            result.Copyright = PhotogrammetryAttributionCollector.ExtractCopyrightFromGlb(glbBytes);
            result.EstimatedBytes = glbBytes.Length * 3;

            var import = new GltfImport();
            bool ok = await import.Load(glbBytes);
            if (!ok)
            {
                Debug.LogWarning("[Photogrammetry] glTFast Load() failed — tile may use unsupported compression (Draco/WebP).");
                import.Dispose();
                return result;
            }

            rotationTuning ??= new PhotogrammetryRotationTuning();
            Matrix4d tileMatrix = tileTransformToEcef ?? Matrix4d.Identity;

            var root = new GameObject(tileName ?? "PhotogrammetryTile");
            var contentPivot = new GameObject("Content").transform;
            contentPivot.SetParent(root.transform, false);
            root.transform.SetParent(parent, false);

            if (rotationTuning.placementMode == PhotogrammetryRotationTuning.PlacementMode.CesiumParity)
            {
                ApplyCesiumParityPlacement(
                    root.transform,
                    contentPivot,
                    parent,
                    transform,
                    tileMatrix,
                    rtcCenterEcef,
                    rotationTuning);
            }
            else
            {
                Vector3 unityPos = transform.EcefToUnity(
                    rtcCenterEcef.Magnitude >= 1_000_000.0
                        ? rtcCenterEcef
                        : transform.AnchorEcef);
                root.transform.localPosition = unityPos - parent.position;
                contentPivot.localPosition = Vector3.zero;
                contentPivot.localRotation = rotationTuning.BuildContentPivotRotation(
                    transform, rtcCenterEcef, tileTransformToEcef);
                contentPivot.localScale = Vector3.one;
            }

            await import.InstantiateMainSceneAsync(contentPivot);

            if (rotationTuning.placementMode == PhotogrammetryRotationTuning.PlacementMode.CesiumParity)
            {
                RelativizeAbsoluteEcefTranslations(contentPivot, rtcCenterEcef, transform);
            }
            else
            {
                NormalizeImportedGltfNodes(contentPivot, rotationTuning);
            }

            var holder = root.AddComponent<PhotogrammetryTileHolder>();
            holder.SetImport(import, result.Copyright);

            result.Success = true;
            result.Root = root;
            result.Holder = holder;
            return result;
        }

        /// <summary>
        /// Position from double ECEF (RTC); axis fix on Content; glTF root keeps rotation, translation relativized after import.
        /// </summary>
        static void ApplyCesiumParityPlacement(
            Transform root,
            Transform content,
            Transform parent,
            EcefToUnityTransform transform,
            Matrix4d tileMatrix,
            Vector3d rtcCenterEcef,
            PhotogrammetryRotationTuning rotationTuning)
        {
            root.localPosition = Vector3.zero;
            root.localRotation = Quaternion.identity;
            root.localScale = Vector3.one;

            Matrix4d positionToEcef = rotationTuning.applyTileTreeTransform ? tileMatrix : Matrix4d.Identity;
            if (rtcCenterEcef.Magnitude >= 1_000_000.0)
                positionToEcef = Matrix4d.Multiply(positionToEcef, Matrix4d.Translation(rtcCenterEcef));

            Vector3 worldPos = transform.EcefToUnity(positionToEcef.TransformPoint(Vector3d.Zero));
            root.localPosition = parent != null
                ? parent.InverseTransformPoint(worldPos)
                : worldPos;

            content.localPosition = Vector3.zero;
            content.localScale = Vector3.one;

            content.localRotation = transform.EcefBasisRotationToUnity(Matrix4d.YUpToZUp);

            Vector3 cesiumOffset = rotationTuning.cesiumContentPivotEulerOffset;
            if (cesiumOffset == Vector3.zero)
                cesiumOffset = rotationTuning.contentPivotEulerOffset;
            if (cesiumOffset.sqrMagnitude > 0.0001f)
                content.localRotation *= Quaternion.Euler(cesiumOffset);
        }

        /// <summary>
        /// Google embeds absolute ECEF coords as glTF node translation. Subtract tile RTC in double, then map to small parent-local offset.
        /// </summary>
        static void RelativizeAbsoluteEcefTranslations(
            Transform node,
            Vector3d tileRtcEcef,
            EcefToUnityTransform bridge)
        {
            for (int i = 0; i < node.childCount; i++)
                RelativizeAbsoluteEcefTranslations(node.GetChild(i), tileRtcEcef, bridge);

            if (node.localPosition.magnitude < AbsoluteEcefTranslationThreshold)
                return;

            var absoluteEcef = new Vector3d(node.localPosition.x, node.localPosition.y, node.localPosition.z);
            Vector3d deltaEcef = absoluteEcef - tileRtcEcef;
            Vector3 unityDeltaWorld = bridge.EcefOffsetToUnity(deltaEcef);

            Transform parent = node.parent;
            node.localPosition = parent != null
                ? parent.InverseTransformVector(unityDeltaWorld)
                : unityDeltaWorld;

        }

        public static Vector3d ParseRtcCenter(byte[] glbBytes) => ParseRtcCenterEcef(glbBytes);

        public static Vector3d ParseRtcCenterEcef(byte[] glbBytes)
        {
            if (!GltfGlbParser.TryReadJsonChunk(glbBytes, out string json))
                return Vector3d.Zero;
            return GltfGlbParser.ParseRtcCenterEcef(json);
        }

        static void NormalizeImportedGltfNodes(Transform node, PhotogrammetryRotationTuning tuning)
        {
            var bakedAxis = Quaternion.Euler(tuning.gltfYUpToZUpEuler);

            for (int i = 0; i < node.childCount; i++)
                NormalizeImportedGltfNodes(node.GetChild(i), tuning);

            if (tuning.stripGltfLargeEcefTranslation && node.localPosition.sqrMagnitude > 1e8f)
            {
                node.localPosition = Vector3.zero;
                node.localRotation = Quaternion.identity;
                node.localScale = Vector3.one;
                return;
            }

            if (tuning.stripGltfBakedYUpToZUp &&
                Quaternion.Angle(node.localRotation, bakedAxis) < tuning.stripYUpToZUpAngleTolerance)
            {
                node.localRotation = Quaternion.identity;
                node.localScale = Vector3.one;
            }
        }
    }
}
