using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using ZGConnect.RealtimeStreaming;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

namespace ZGConnect
{
    public sealed class BuildingInfoPopupManager : MonoBehaviour
    {
        [SerializeField] BuildingInfoPopup _popupTemplate;
        [SerializeField] Vector2 _popupScreenOffset = new(0f, 40f);

        readonly List<BuildingInfoPopup> _activePopups = new();
        Camera _camera;
        Canvas _canvas;
        bool _initialized;
        bool _willRenderHooked;
        int _lastAnchorFrame = -1;

        public void Initialize(Camera camera, BuildingInfoPopup popupTemplate, Vector2 popupScreenOffset)
        {
            if (_initialized)
                return;

            _camera = camera;
            _popupScreenOffset = popupScreenOffset;
            if (popupTemplate != null)
                _popupTemplate = popupTemplate;
            EnsureCanvas();
            _initialized = true;
        }

        public void SpawnPopup(Transform building, BuildingInfoSnapshot snapshot)
        {
            if (building == null || snapshot == null || _popupTemplate == null || _canvas == null)
                return;

            BuildingInfoPopup instance = Instantiate(_popupTemplate, _canvas.transform);
            instance.name = $"BuildingInfoPopup_{snapshot.buildingId}_{_activePopups.Count}";
            instance.gameObject.SetActive(true);
            instance.Configure(_popupScreenOffset);
            instance.SetLinkedBuilding(building);
            instance.Populate(snapshot);
            Bounds bounds = RuntimeBuildingMeshUtility.ComputeLod0WorldBounds(building);
            instance.UpdateScreenAnchor(_canvas, _camera, bounds);
            _activePopups.Add(instance);
        }

        public void DismissAll()
        {
            for (int i = _activePopups.Count - 1; i >= 0; i--)
            {
                if (_activePopups[i] != null)
                    Destroy(_activePopups[i].gameObject);
            }
            _activePopups.Clear();
        }

        void OnEnable()
        {
            if (_willRenderHooked)
                return;

            Canvas.willRenderCanvases += HandleWillRenderCanvases;
            _willRenderHooked = true;
        }

        void OnDisable()
        {
            if (!_willRenderHooked)
                return;

            Canvas.willRenderCanvases -= HandleWillRenderCanvases;
            _willRenderHooked = false;
        }

        void LateUpdate()
        {
            if (_camera == null)
                return;

            PruneDestroyedPopups();
        }

        void HandleWillRenderCanvases()
        {
            if (!_initialized || _camera == null || _canvas == null || _activePopups.Count == 0)
                return;

            if (_lastAnchorFrame == Time.frameCount)
                return;

            _lastAnchorFrame = Time.frameCount;
            UpdatePopupAnchors();
        }

        void PruneDestroyedPopups()
        {
            for (int i = _activePopups.Count - 1; i >= 0; i--)
            {
                BuildingInfoPopup popup = _activePopups[i];
                if (popup == null)
                {
                    _activePopups.RemoveAt(i);
                    continue;
                }

                Transform building = popup.LinkedBuilding;
                if (building == null)
                {
                    Destroy(popup.gameObject);
                    _activePopups.RemoveAt(i);
                }
            }
        }

        void UpdatePopupAnchors()
        {
            for (int i = 0; i < _activePopups.Count; i++)
            {
                BuildingInfoPopup popup = _activePopups[i];
                if (popup == null || popup.LinkedBuilding == null)
                    continue;

                Bounds bounds = RuntimeBuildingMeshUtility.ComputeLod0WorldBounds(popup.LinkedBuilding);
                popup.UpdateScreenAnchor(_canvas, _camera, bounds);
            }
        }

        void EnsureCanvas()
        {
            EnsureEventSystem();

            var canvasGo = new GameObject("BuildingInteractionCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);

            _canvas = canvasGo.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.pixelPerfect = false;
            _canvas.sortingOrder = 200;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            if (_popupTemplate == null)
            {
                var templateRoot = new GameObject("PopupTemplate");
                templateRoot.transform.SetParent(transform, false);
                templateRoot.SetActive(false);
                _popupTemplate = BuildingInfoPopup.CreateTemplate(templateRoot.transform);
            }
        }

        static void EnsureEventSystem()
        {
            if (FindAnyObjectByType<EventSystem>() != null)
                return;

            var eventSystemGo = new GameObject("EventSystem");
            eventSystemGo.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
            eventSystemGo.AddComponent<InputSystemUIInputModule>();
#else
            eventSystemGo.AddComponent<StandaloneInputModule>();
#endif
        }
    }
}

