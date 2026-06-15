using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace ZGConnect
{
    /// <summary>
    /// Configures a URP overlay camera that renders only the BuildingHighlight layer on top of the main view.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BuildingHighlightCamera : MonoBehaviour
    {
        [SerializeField] Camera _mainCamera;
        Camera _highlightCamera;
        bool _initialized;

        public void Initialize(Camera mainCamera)
        {
            if (_initialized)
                return;

            _mainCamera = mainCamera != null ? mainCamera : GetComponent<Camera>();
            if (_mainCamera == null)
            {
                Debug.LogError("[ZGConnect] BuildingHighlightCamera requires a main camera.");
                return;
            }

            if (!BuildingInteractionLayers.IsValid)
            {
                Debug.LogError(
                    $"[ZGConnect] Layer '{BuildingInteractionLayers.HighlightLayerName}' is not defined in Tag Manager.");
                return;
            }

            EnsureHighlightCamera();
            ConfigureMainCameraMask();
            RegisterOverlayStack();
            _initialized = true;
        }

        void EnsureHighlightCamera()
        {
            Transform existing = transform.Find("HighlightCamera");
            if (existing != null)
            {
                _highlightCamera = existing.GetComponent<Camera>();
                if (_highlightCamera != null)
                    return;
            }

            var highlightGo = new GameObject("HighlightCamera");
            highlightGo.transform.SetParent(transform, false);
            highlightGo.transform.localPosition = Vector3.zero;
            highlightGo.transform.localRotation = Quaternion.identity;
            highlightGo.transform.localScale = Vector3.one;

            _highlightCamera = highlightGo.AddComponent<Camera>();
            _highlightCamera.clearFlags = CameraClearFlags.Depth;
            _highlightCamera.cullingMask = BuildingInteractionLayers.HighlightLayerMask;
            _highlightCamera.depth = _mainCamera.depth + 1f;
            _highlightCamera.fieldOfView = _mainCamera.fieldOfView;
            _highlightCamera.orthographic = _mainCamera.orthographic;
            _highlightCamera.orthographicSize = _mainCamera.orthographicSize;
            _highlightCamera.nearClipPlane = _mainCamera.nearClipPlane;
            _highlightCamera.farClipPlane = _mainCamera.farClipPlane;
            _highlightCamera.allowHDR = _mainCamera.allowHDR;
            _highlightCamera.allowMSAA = _mainCamera.allowMSAA;

            var highlightData = highlightGo.GetComponent<UniversalAdditionalCameraData>();
            if (highlightData == null)
                highlightData = highlightGo.AddComponent<UniversalAdditionalCameraData>();
            highlightData.renderType = CameraRenderType.Overlay;
            highlightData.requiresColorOption = CameraOverrideOption.Off;
            highlightData.requiresDepthOption = CameraOverrideOption.Off;
        }

        void ConfigureMainCameraMask()
        {
            _mainCamera.cullingMask &= ~BuildingInteractionLayers.HighlightLayerMask;
        }

        void RegisterOverlayStack()
        {
            if (_highlightCamera == null)
                return;

            UniversalAdditionalCameraData mainData = _mainCamera.GetUniversalAdditionalCameraData();
            if (mainData == null)
                return;

            if (!mainData.cameraStack.Contains(_highlightCamera))
                mainData.cameraStack.Add(_highlightCamera);
        }

        void LateUpdate()
        {
            if (_highlightCamera == null || _mainCamera == null)
                return;

            _highlightCamera.fieldOfView = _mainCamera.fieldOfView;
            _highlightCamera.orthographic = _mainCamera.orthographic;
            _highlightCamera.orthographicSize = _mainCamera.orthographicSize;
            _highlightCamera.nearClipPlane = _mainCamera.nearClipPlane;
            _highlightCamera.farClipPlane = _mainCamera.farClipPlane;
        }
    }
}

