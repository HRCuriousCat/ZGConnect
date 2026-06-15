using UnityEngine;
using UnityEngine.Serialization;
using ZGConnect.RealtimeStreaming;

namespace ZGConnect
{
    /// <summary>
    /// Orchestrates building hover highlighting, click selection, and info popups in Runtime_Streaming.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BuildingInteractionController : MonoBehaviour
    {
        [Header("Camera")]
        [SerializeField] Camera _interactionCamera;

        [Header("Roots")]
        [Tooltip("Parent for hover/selection mesh copies. Defaults to RealtimeStreamingController when unset.")]
        [SerializeField] Transform _rootsParent;

        [Header("Highlight")]
        [Tooltip("Material applied to every submesh on the hovered building highlight copy.")]
        [FormerlySerializedAs("_outlineMaterial")]
        [FormerlySerializedAs("_highlightMaterial")]
        [FormerlySerializedAs("_highlightMaterial0")]
        [SerializeField] Material _highlightMaterial;
        [Tooltip("Material applied to clicked/selected buildings until selection is cleared.")]
        [SerializeField] Material _selectionMaterial;
        [Tooltip("Only used when no highlight material is assigned and the fallback outline shader is created at runtime.")]
        [FormerlySerializedAs("_outlineColor")]
        [SerializeField] Color _fallbackOutlineColor = new(1f, 0.72f, 0.3f, 1f);
        [Tooltip("Only used when no selection material is assigned and the fallback outline shader is created at runtime.")]
        [SerializeField] Color _fallbackSelectionOutlineColor = new(0.35f, 0.85f, 1f, 1f);
        [Tooltip("Only used for the fallback ZGConnect/BuildingOutline shader.")]
        [FormerlySerializedAs("_outlineWidth")]
        [SerializeField] float _fallbackOutlineWidth = 0.02f;

        [Header("Raycast")]
        [SerializeField] float _maxRaycastDistance = 50000f;
        [SerializeField] LayerMask _raycastMask = ~0;

        [Header("Popup")]
        [SerializeField] BuildingInfoPopup _popupPrefab;
        [SerializeField] Vector2 _popupScreenOffset = new(0f, 40f);

        [Header("Inspector (Play mode)")]
        [Tooltip("Updated when a building is clicked. Assign TMP fields from these values in your popup prefab.")]
        [SerializeField] BuildingInfoSnapshotInspectorData _selectedBuildingInspector = new();

        Transform _highlightRoot;
        Transform _selectionRoot;
        BuildingHighlightRenderer _highlightRenderer;
        BuildingSelectionRenderer _selectionRenderer;
        BuildingRaycastInteractor _interactor;
        BuildingInfoPopupManager _popupManager;
        BuildingHighlightCamera _highlightCamera;
        BuildingLazyMetadataResolver _lazyMetadataResolver;
        Material _runtimeHighlightMaterial;
        Material _runtimeSelectionMaterial;
        bool _initialized;

        public BuildingInfoSnapshotInspectorData SelectedBuildingInspector => _selectedBuildingInspector;

        /// <summary>Raised after a building is clicked and its metadata has been resolved.</summary>
        public event System.Action<Transform, BuildingInfoSnapshot> BuildingSelected;

        /// <summary>Raised when the hovered building changes. Null = pointer left all buildings.</summary>
        public event System.Action<Transform> BuildingHoverChanged;

        /// <summary>Raised when the current selection and popups are dismissed.</summary>
        public event System.Action SelectionDismissed;

        public static BuildingInteractionController EnsureOnCamera(
            Camera camera,
            Material highlightMaterial = null,
            Material selectionMaterial = null,
            BuildingInfoPopup popupPrefab = null,
            Transform rootsParent = null)
        {
            if (camera == null)
                return null;

            var controller = camera.GetComponent<BuildingInteractionController>();
            if (controller == null)
                controller = camera.gameObject.AddComponent<BuildingInteractionController>();

            controller.ApplyOptionalResources(highlightMaterial, selectionMaterial, popupPrefab, rootsParent);
            controller.Initialize(camera, rootsParent);
            return controller;
        }

        public void ConfigureLazyMetadataResolver(BuildingLazyMetadataResolver resolver) =>
            _lazyMetadataResolver = resolver;

        public void ApplyOptionalResources(
            Material highlightMaterial,
            Material selectionMaterial,
            BuildingInfoPopup popupPrefab,
            Transform rootsParent = null)
        {
            if (_initialized)
                return;

            if (highlightMaterial != null)
                _highlightMaterial = highlightMaterial;
            if (selectionMaterial != null)
                _selectionMaterial = selectionMaterial;
            if (popupPrefab != null)
                _popupPrefab = popupPrefab;
            if (rootsParent != null)
                _rootsParent = rootsParent;
        }

        void Start()
        {
            if (_initialized)
                return;

            Initialize(
                _interactionCamera != null ? _interactionCamera : GetComponent<Camera>(),
                _rootsParent);
        }

        public void Initialize(Camera camera, Transform rootsParent = null)
        {
            if (_initialized)
                return;

            if (rootsParent != null)
                _rootsParent = rootsParent;

            _interactionCamera = camera != null ? camera : Camera.main;
            if (_interactionCamera == null)
            {
                Debug.LogWarning("[ZGConnect] BuildingInteractionController has no camera.");
                return;
            }

            if (!BuildingInteractionLayers.IsValid)
            {
                Debug.LogError(
                    $"[ZGConnect] Layer '{BuildingInteractionLayers.HighlightLayerName}' is missing from Tag Manager.");
                return;
            }

            EnsureHighlightCamera();
            EnsureHighlightRoot();
            EnsureSelectionRoot();
            EnsureHighlightMaterials();
            EnsurePopupManager();

            _highlightRenderer = new BuildingHighlightRenderer(_highlightRoot, _runtimeHighlightMaterial);
            _selectionRenderer = new BuildingSelectionRenderer(_selectionRoot, _runtimeSelectionMaterial);
            _interactor = new BuildingRaycastInteractor(_interactionCamera, _maxRaycastDistance, _raycastMask);
            _interactor.HoveredBuildingChanged += OnHoveredBuildingChanged;
            _interactor.BuildingClicked += OnBuildingClicked;
            _interactor.DismissPopupsRequested += OnDismissPopupsRequested;

            _initialized = true;
        }

        void Update()
        {
            if (!_initialized || _interactor == null)
                return;

            bool interactionEnabled = !BuildingRaycastInteractor.IsInteractionBlocked();
            _interactor.Update(interactionEnabled);
            _highlightRenderer?.Tick();
            _selectionRenderer?.Tick();
        }

        void OnDestroy()
        {
            if (_interactor != null)
            {
                _interactor.HoveredBuildingChanged -= OnHoveredBuildingChanged;
                _interactor.BuildingClicked -= OnBuildingClicked;
                _interactor.DismissPopupsRequested -= OnDismissPopupsRequested;
            }

            _highlightRenderer?.Dispose();
            _selectionRenderer?.Dispose();

            if (_runtimeHighlightMaterial != null)
                Destroy(_runtimeHighlightMaterial);
            if (_runtimeSelectionMaterial != null)
                Destroy(_runtimeSelectionMaterial);
        }

        void EnsureHighlightCamera()
        {
            _highlightCamera = GetComponent<BuildingHighlightCamera>();
            if (_highlightCamera == null)
                _highlightCamera = gameObject.AddComponent<BuildingHighlightCamera>();
            _highlightCamera.Initialize(_interactionCamera);
        }

        void EnsureLazyMetadataResolver()
        {
            if (_lazyMetadataResolver != null && _lazyMetadataResolver.IsConfigured)
                return;

            var streamer = FindAnyObjectByType<RealtimeStreamingController>();
            if (streamer != null)
                _lazyMetadataResolver = streamer.GetBuildingLazyMetadataResolver();
        }

        Transform ResolveRootsParent()
        {
            if (_rootsParent != null)
                return _rootsParent;

            var streamer = FindAnyObjectByType<RealtimeStreamingController>();
            if (streamer != null)
                return streamer.transform;

            Debug.LogWarning(
                "[ZGConnect] BuildingInteractionController could not find RealtimeStreamingController. " +
                "Highlight/selection roots will be parented to the interaction camera.");
            return transform;
        }

        void EnsureHighlightRoot()
        {
            if (_highlightRoot != null)
                return;

            var rootGo = new GameObject("BuildingHighlightRoot");
            rootGo.transform.SetParent(ResolveRootsParent(), false);
            _highlightRoot = rootGo.transform;
        }

        void EnsureSelectionRoot()
        {
            if (_selectionRoot != null)
                return;

            var rootGo = new GameObject("BuildingSelectionRoot");
            rootGo.transform.SetParent(ResolveRootsParent(), false);
            _selectionRoot = rootGo.transform;
        }

        void EnsureHighlightMaterials()
        {
            if (_runtimeHighlightMaterial == null)
            {
                if (_highlightMaterial != null)
                    _runtimeHighlightMaterial = new Material(_highlightMaterial);
                else
                {
                    _runtimeHighlightMaterial = CreateFallbackOutlineMaterial();
                    ApplyFallbackOutlineSettings(_runtimeHighlightMaterial, _fallbackOutlineColor);
                }
            }

            if (_runtimeSelectionMaterial == null)
            {
                if (_selectionMaterial != null)
                    _runtimeSelectionMaterial = new Material(_selectionMaterial);
                else
                {
                    _runtimeSelectionMaterial = CreateFallbackOutlineMaterial();
                    ApplyFallbackOutlineSettings(_runtimeSelectionMaterial, _fallbackSelectionOutlineColor);
                }
            }
        }

        static Material CreateFallbackOutlineMaterial()
        {
            Shader shader = Shader.Find("ZGConnect/BuildingOutline");
            if (shader == null)
            {
                Debug.LogError("[ZGConnect] Shader 'ZGConnect/BuildingOutline' was not found.");
                return null;
            }

            return new Material(shader);
        }

        void ApplyFallbackOutlineSettings(Material material, Color outlineColor)
        {
            if (material == null)
                return;

            if (material.HasProperty("_OutlineColor"))
                material.SetColor("_OutlineColor", outlineColor);
            if (material.HasProperty("_OutlineWidth"))
                material.SetFloat("_OutlineWidth", _fallbackOutlineWidth);
        }

        void EnsurePopupManager()
        {
            _popupManager = GetComponent<BuildingInfoPopupManager>();
            if (_popupManager == null)
                _popupManager = gameObject.AddComponent<BuildingInfoPopupManager>();
            _popupManager.Initialize(_interactionCamera, _popupPrefab, _popupScreenOffset);
        }

        void OnHoveredBuildingChanged(Transform building)
        {
            BuildingHoverChanged?.Invoke(building);

            if (_highlightRenderer == null)
                return;

            if (building == null || _selectionRenderer != null && _selectionRenderer.IsSelected(building))
                _highlightRenderer.Clear();
            else
                _highlightRenderer.SetTarget(building);
        }

        void OnBuildingClicked(BuildingRaycastResult result)
        {
            if (result.BuildingTransform == null)
                return;

            EnsureLazyMetadataResolver();
            if (_lazyMetadataResolver == null)
            {
                Debug.LogWarning(
                    $"[ZGConnect] Could not load building metadata for '{result.BuildingTransform.name}' " +
                    $"(tile '{result.TileId}'): RealtimeStreamingController is missing or metadata is not configured.");
                return;
            }

            if (!_lazyMetadataResolver.TryLoadBuildingInfo(
                    result.BuildingTransform,
                    result.TileId,
                    out BuildingInfoSnapshot snapshot,
                    out string failureReason))
            {
                Debug.LogWarning(
                    $"[ZGConnect] Could not load building metadata for '{result.BuildingTransform.name}' " +
                    $"(tile '{result.TileId}'): {failureReason}");
                return;
            }

            snapshot.CopyToInspectorData(_selectedBuildingInspector);
            _selectionRenderer?.Select(result.BuildingTransform);
            _highlightRenderer?.Clear();
            _popupManager?.SpawnPopup(result.BuildingTransform, snapshot);
            BuildingSelected?.Invoke(result.BuildingTransform, snapshot);
        }

        void OnDismissPopupsRequested()
        {
            _selectedBuildingInspector.Clear();
            _selectionRenderer?.ClearAll();
            _popupManager?.DismissAll();
            SelectionDismissed?.Invoke();
        }
    }
}
