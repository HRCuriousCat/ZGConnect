using System;

using UnityEngine;

using UnityEngine.EventSystems;

using ZGConnect.RealtimeStreaming;

#if ENABLE_INPUT_SYSTEM

using UnityEngine.InputSystem;

#endif



namespace ZGConnect

{

    public readonly struct BuildingRaycastResult

    {

        public readonly bool HasHit;

        public readonly bool IsBuilding;

        public readonly bool IsTerrain;

        public readonly Transform BuildingTransform;

        public readonly string TileId;

        public readonly RaycastHit Hit;



        public BuildingRaycastResult(

            bool hasHit,

            bool isBuilding,

            bool isTerrain,

            Transform buildingTransform,

            string tileId,

            RaycastHit hit)

        {

            HasHit = hasHit;

            IsBuilding = isBuilding;

            IsTerrain = isTerrain;

            BuildingTransform = buildingTransform;

            TileId = tileId;

            Hit = hit;

        }



        public static BuildingRaycastResult None => new(false, false, false, null, null, default);

    }



    /// <summary>

    /// Raycasts from the interaction camera to detect hovered/clicked buildings.

    /// </summary>

    public sealed class BuildingRaycastInteractor

    {

        readonly Camera _camera;

        readonly float _maxDistance;

        readonly LayerMask _raycastMask;



        public event Action<Transform> HoveredBuildingChanged;

        public event Action<BuildingRaycastResult> BuildingClicked;

        public event Action DismissPopupsRequested;



        Transform _hoveredBuilding;



        public BuildingRaycastInteractor(Camera camera, float maxDistance, LayerMask raycastMask)

        {

            _camera = camera;

            _maxDistance = maxDistance;

            _raycastMask = raycastMask;

        }



        public Transform HoveredBuilding => _hoveredBuilding;



        public void Update(bool interactionEnabled)

        {

            if (!interactionEnabled)

            {

                SetHoveredBuilding(null);

                return;

            }



            BuildingRaycastResult result = RaycastFromMouse();

            SetHoveredBuilding(result.IsBuilding ? result.BuildingTransform : null);



            if (WasLeftClickPressedThisFrame())

            {

                if (result.IsBuilding)

                    BuildingClicked?.Invoke(result);

                else if (!result.IsBuilding && (result.IsTerrain || !result.HasHit))

                    DismissPopupsRequested?.Invoke();

            }

        }



        public BuildingRaycastResult RaycastFromMouse()

        {

            if (_camera == null)

                return BuildingRaycastResult.None;



            Vector2 mousePosition = GetMousePosition();

            Ray ray = _camera.ScreenPointToRay(mousePosition);

            RaycastHit[] hits = Physics.RaycastAll(ray, _maxDistance, _raycastMask, QueryTriggerInteraction.Ignore);

            if (hits == null || hits.Length == 0)

                return BuildingRaycastResult.None;



            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));



            bool sawTerrain = false;

            RaycastHit terrainHit = default;



            foreach (RaycastHit hit in hits)

            {

                if (BuildingHitUtility.TryResolveBuildingRootFromCollider(

                        hit.collider, out Transform buildingRoot, out string tileId))

                {

                    return new BuildingRaycastResult(true, true, false, buildingRoot, tileId, hit);

                }



                if (!sawTerrain && hit.collider is TerrainCollider)

                {

                    sawTerrain = true;

                    terrainHit = hit;

                }

            }



            if (sawTerrain)

                return new BuildingRaycastResult(true, false, true, null, null, terrainHit);



            return BuildingRaycastResult.None;

        }



        void SetHoveredBuilding(Transform building)

        {

            if (_hoveredBuilding == building)

                return;



            _hoveredBuilding = building;

            HoveredBuildingChanged?.Invoke(building);

        }



        static Vector2 GetMousePosition()

        {

#if ENABLE_INPUT_SYSTEM

            if (Mouse.current == null)

                return Vector2.zero;

            return Mouse.current.position.ReadValue();

#else

            return Input.mousePosition;

#endif

        }



        static bool WasLeftClickPressedThisFrame()

        {

#if ENABLE_INPUT_SYSTEM

            if (Mouse.current == null)

                return false;

            return Mouse.current.leftButton.wasPressedThisFrame;

#else

            return Input.GetMouseButtonDown(0);

#endif

        }



        public static bool IsInteractionBlocked()

        {

            if (IsPointerOverUi())

                return true;



#if ENABLE_INPUT_SYSTEM

            if (Mouse.current == null)

                return true;

            return Mouse.current.rightButton.isPressed;

#else

            return Input.GetMouseButton(1);

#endif

        }



        static bool IsPointerOverUi()

        {

            if (EventSystem.current == null)

                return false;



            return EventSystem.current.IsPointerOverGameObject();

        }

    }

}


