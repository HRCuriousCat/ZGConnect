using System;
using System.Collections;
using UnityEngine;

namespace ZGConnect
{
    /// <summary>Navigation helpers: teleporting and cinematic fly-to for the camera/player.</summary>
    public sealed partial class ZGConnectToolkit
    {
        Coroutine _activeFlight;

        /// <summary>
        /// Transform moved by teleport/fly calls. Defaults to the reference camera.
        /// Point it at your player or camera rig when the camera is not free-floating.
        /// </summary>
        public Transform NavigationTarget
        {
            get
            {
                if (_navigationTarget != null)
                    return _navigationTarget;
                return ReferenceCamera != null ? ReferenceCamera.transform : null;
            }
            set => _navigationTarget = value;
        }

        /// <summary>True while a FlyTo flight is in progress.</summary>
        public bool IsFlying { get; private set; }

        // ── Teleport ───────────────────────────────────────────────────────────

        /// <summary>Instantly moves the navigation target to a world position.</summary>
        public void TeleportTo(Vector3 worldPos)
        {
            Transform target = NavigationTarget;
            if (target == null)
            {
                Debug.LogWarning("[ZGConnect] Toolkit has no navigation target (no camera found).");
                return;
            }

            StopFlight();
            target.position = worldPos;
        }

        /// <summary>
        /// Instantly moves the navigation target to a GPS coordinate, placed
        /// <paramref name="heightAboveGround"/> metres above the terrain. When the terrain
        /// there is not loaded yet, a safe altitude above the dataset is used instead
        /// (streaming will catch up around the new position).
        /// </summary>
        public void TeleportToGps(double latitude, double longitude, float heightAboveGround = 60f)
        {
            TeleportTo(ResolveAerialPosition(latitude, longitude, heightAboveGround));
        }

        /// <summary>
        /// Searches loaded buildings by address and teleports in front of the first match.
        /// Returns the matched building, or null when nothing was found.
        /// </summary>
        public ZGBuildingHandle TeleportToAddress(string addressQuery, float heightAboveGround = 60f)
        {
            var matches = FindBuildingsByAddress(addressQuery, maxResults: 1);
            if (matches.Count == 0)
                return null;

            ZGBuildingHandle building = matches[0];
            TeleportTo(ComputeFramingPosition(building));
            LookAt(building.GetWorldBounds().center);
            return building;
        }

        // ── Fly-to ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Smoothly flies the navigation target to a world position over
        /// <paramref name="duration"/> seconds (smooth-step easing).
        /// </summary>
        public Coroutine FlyTo(Vector3 worldPos, float duration = 3f, Action onComplete = null) =>
            StartFlight(worldPos, null, duration, onComplete);

        /// <summary>Smoothly flies the navigation target to a GPS coordinate.</summary>
        public Coroutine FlyToGps(
            double latitude,
            double longitude,
            float heightAboveGround = 60f,
            float duration = 3f,
            Action onComplete = null)
        {
            return StartFlight(
                ResolveAerialPosition(latitude, longitude, heightAboveGround), null, duration, onComplete);
        }

        /// <summary>
        /// Cinematic fly-to that frames a building: ends at a distance proportional to the
        /// building size, looking at its centre.
        /// </summary>
        public Coroutine FlyToBuilding(ZGBuildingHandle building, float duration = 3f, Action onComplete = null)
        {
            if (building?.Transform == null)
                return null;

            Vector3 endPos = ComputeFramingPosition(building);
            Vector3 lookTarget = building.GetWorldBounds().center;
            Quaternion endRot = Quaternion.LookRotation(lookTarget - endPos);
            return StartFlight(endPos, endRot, duration, onComplete);
        }

        /// <summary>Cancels a flight started by any FlyTo overload.</summary>
        public void StopFlight()
        {
            if (_activeFlight != null)
            {
                StopCoroutine(_activeFlight);
                _activeFlight = null;
            }

            IsFlying = false;
        }

        /// <summary>Rotates the navigation target to look at a world point.</summary>
        public void LookAt(Vector3 worldPoint)
        {
            Transform target = NavigationTarget;
            if (target != null)
                target.rotation = Quaternion.LookRotation(worldPoint - target.position);
        }

        // ── Internals ──────────────────────────────────────────────────────────

        Coroutine StartFlight(Vector3 endPos, Quaternion? endRot, float duration, Action onComplete)
        {
            Transform target = NavigationTarget;
            if (target == null)
            {
                Debug.LogWarning("[ZGConnect] Toolkit has no navigation target (no camera found).");
                return null;
            }

            StopFlight();
            _activeFlight = StartCoroutine(FlightRoutine(target, endPos, endRot, Mathf.Max(0.01f, duration), onComplete));
            return _activeFlight;
        }

        IEnumerator FlightRoutine(
            Transform target,
            Vector3 endPos,
            Quaternion? endRot,
            float duration,
            Action onComplete)
        {
            IsFlying = true;
            Vector3 startPos = target.position;
            Quaternion startRot = target.rotation;

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / duration));
                target.position = Vector3.LerpUnclamped(startPos, endPos, t);
                if (endRot.HasValue)
                    target.rotation = Quaternion.SlerpUnclamped(startRot, endRot.Value, t);
                yield return null;
            }

            target.position = endPos;
            if (endRot.HasValue)
                target.rotation = endRot.Value;

            IsFlying = false;
            _activeFlight = null;
            onComplete?.Invoke();
        }

        Vector3 ResolveAerialPosition(double latitude, double longitude, float heightAboveGround)
        {
            Vector3 pos = ZGConnectCoordinates.WGS84ToUnity(latitude, longitude);
            if (TryGetGroundHeight(pos, out float groundY))
            {
                pos.y = groundY + heightAboveGround;
            }
            else if (TryGetDatasetBounds(out Bounds bounds))
            {
                // Terrain not streamed in yet — arrive above the highest possible point.
                pos.y = bounds.max.y + heightAboveGround;
            }
            else
            {
                pos.y = heightAboveGround;
            }

            return pos;
        }

        Vector3 ComputeFramingPosition(ZGBuildingHandle building)
        {
            Bounds bounds = building.GetWorldBounds();
            float size = Mathf.Max(bounds.size.x, bounds.size.z, bounds.size.y, 20f);
            float distance = size * 2.2f;

            Transform target = NavigationTarget;
            Vector3 approachDir = target != null
                ? (target.position - bounds.center).normalized
                : new Vector3(0f, 0f, -1f);

            // Keep a sensible diagonal view even when approaching from directly above/below.
            approachDir.y = 0f;
            if (approachDir.sqrMagnitude < 0.01f)
                approachDir = new Vector3(0f, 0f, -1f);
            approachDir.Normalize();

            Vector3 pos = bounds.center + approachDir * distance + Vector3.up * (size * 0.9f);
            return SnapAboveGround(pos, 5f);
        }

        Vector3 SnapAboveGround(Vector3 pos, float minClearance)
        {
            if (TryGetGroundHeight(pos, out float groundY) && pos.y < groundY + minClearance)
                pos.y = groundY + minClearance;
            return pos;
        }
    }
}
