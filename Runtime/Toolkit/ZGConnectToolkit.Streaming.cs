using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using ZGConnect.RealtimeStreaming;

namespace ZGConnect
{
    /// <summary>Streaming helpers: load progress, tile lists, events and area prewarming.</summary>
    public sealed partial class ZGConnectToolkit
    {
        // ── Events ─────────────────────────────────────────────────────────────

        /// <summary>Raised when a terrain tile finishes loading. Argument = tile id (e.g. "550000_5068000").</summary>
        public event Action<string> TerrainTileLoaded;

        /// <summary>Raised when a terrain tile is unloaded.</summary>
        public event Action<string> TerrainTileUnloaded;

        /// <summary>Raised when a building tile becomes active — spawn your per-area content here.</summary>
        public event Action<string> BuildingTileLoaded;

        /// <summary>Raised when a building tile is deactivated.</summary>
        public event Action<string> BuildingTileUnloaded;

        /// <summary>Raised when the user clicks a building (requires BuildingInteractionController in the scene).</summary>
        public event Action<ZGBuildingHandle> BuildingClicked;

        /// <summary>Raised when the hovered building changes; null = pointer left all buildings.</summary>
        public event Action<ZGBuildingHandle> BuildingHoverChanged;

        // ── Status ─────────────────────────────────────────────────────────────

        /// <summary>True while the realtime streamer is active.</summary>
        public bool IsStreaming => RealtimeStreamer != null && RealtimeStreamer.IsStreamingActive;

        /// <summary>Number of currently loaded terrain tiles (leaf + supertiles).</summary>
        public int LoadedTerrainTileCount => RealtimeStreamer != null ? RealtimeStreamer.LoadedTileCount : 0;

        /// <summary>Number of currently active building tiles.</summary>
        public int LoadedBuildingTileCount
        {
            get
            {
                if (RealtimeStreamer == null)
                    return 0;

                int count = 0;
                foreach (RuntimeTileRecord _ in RealtimeStreamer.LoadedBuildingTiles)
                    count++;
                return count;
            }
        }

        /// <summary>
        /// Normalized 0–1 progress of the initial load around the camera —
        /// drives a loading bar in two lines of code.
        /// </summary>
        public float GetInitialLoadProgress01() =>
            RealtimeStreamer != null
                ? RealtimeStreamer.GetInitialLoadProgress().NormalizedProgress
                : 1f;

        /// <summary>True when the initial tile ring around the camera is fully loaded.</summary>
        public bool IsInitialLoadComplete() =>
            RealtimeStreamer == null || RealtimeStreamer.IsIntroInitialLoadComplete();

        /// <summary>Human-readable loading status ("Loading terrain", "12/16 - 75%").</summary>
        public string GetLoadStatusText()
        {
            if (RealtimeStreamer == null)
                return string.Empty;

            StreamingLoadStatus status = RealtimeStreamer.GetInitialLoadStatus();
            return string.IsNullOrEmpty(status.StageDetail)
                ? status.StageLabel
                : $"{status.StageLabel} ({status.StageDetail})";
        }

        /// <summary>Ids of all currently loaded terrain tiles.</summary>
        public List<string> GetLoadedTerrainTileIds()
        {
            var ids = new List<string>();
            if (RealtimeStreamer == null)
                return ids;

            foreach (RuntimeTileRecord record in RealtimeStreamer.LoadedTerrainTiles)
                ids.Add(record.Key);
            return ids;
        }

        /// <summary>Enables or disables building streaming at runtime (performance toggle).</summary>
        public void SetBuildingStreamingEnabled(bool enabled) =>
            RealtimeStreamer?.SetStreamBuildingsEnabled(enabled);

        /// <summary>Sets the building cull distance in metres at runtime.</summary>
        public void SetBuildingCullDistance(float meters) =>
            RealtimeStreamer?.SetBuildingCullDistanceMeters(meters);

        // ── Prewarm ────────────────────────────────────────────────────────────

        /// <summary>
        /// Temporarily redirects streaming focus to a world position so tiles there load
        /// ahead of time — call before a teleport or cutscene to avoid pop-in.
        /// Restores the normal camera focus after <paramref name="durationSeconds"/>.
        /// </summary>
        public Coroutine PrewarmArea(Vector3 center, float durationSeconds = 6f)
        {
            if (RealtimeStreamer == null)
            {
                Debug.LogWarning("[ZGConnect] PrewarmArea requires a RealtimeStreamingController.");
                return null;
            }

            return StartCoroutine(PrewarmRoutine(center, durationSeconds));
        }

        /// <summary>Prewarms streaming around a GPS coordinate.</summary>
        public Coroutine PrewarmGps(double latitude, double longitude, float durationSeconds = 6f) =>
            PrewarmArea(ZGConnectCoordinates.WGS84ToUnity(latitude, longitude), durationSeconds);

        IEnumerator PrewarmRoutine(Vector3 center, float durationSeconds)
        {
            Transform original = RealtimeStreamer.StreamingCamera;

            var anchor = new GameObject("ZGConnectToolkit_PrewarmAnchor").transform;
            anchor.SetParent(transform, false);
            anchor.position = SnapToGround(center, 100f);

            RealtimeStreamer.SetStreamingCameraOverride(anchor);
            yield return new WaitForSeconds(Mathf.Max(0.5f, durationSeconds));

            if (RealtimeStreamer != null && original != null)
                RealtimeStreamer.SetStreamingCameraOverride(original);

            Destroy(anchor.gameObject);
        }

        // ── Event plumbing ─────────────────────────────────────────────────────

        void HookStreamerEvents()
        {
            if (_streamerEventsHooked || RealtimeStreamer == null)
                return;

            _realtimeStreamer.TerrainTileLoaded += OnTerrainTileLoadedInternal;
            _realtimeStreamer.TerrainTileUnloaded += OnTerrainTileUnloadedInternal;
            _realtimeStreamer.BuildingTileLoaded += OnBuildingTileLoadedInternal;
            _realtimeStreamer.BuildingTileUnloaded += OnBuildingTileUnloadedInternal;
            _streamerEventsHooked = true;
        }

        void UnhookStreamerEvents()
        {
            if (!_streamerEventsHooked || _realtimeStreamer == null)
                return;

            _realtimeStreamer.TerrainTileLoaded -= OnTerrainTileLoadedInternal;
            _realtimeStreamer.TerrainTileUnloaded -= OnTerrainTileUnloadedInternal;
            _realtimeStreamer.BuildingTileLoaded -= OnBuildingTileLoadedInternal;
            _realtimeStreamer.BuildingTileUnloaded -= OnBuildingTileUnloadedInternal;
            _streamerEventsHooked = false;
        }

        void HookInteractionEvents()
        {
            if (_interactionEventsHooked || BuildingInteraction == null)
                return;

            _buildingInteraction.BuildingSelected += OnBuildingSelectedInternal;
            _buildingInteraction.BuildingHoverChanged += OnBuildingHoverChangedInternal;
            _interactionEventsHooked = true;
        }

        void UnhookInteractionEvents()
        {
            if (!_interactionEventsHooked || _buildingInteraction == null)
                return;

            _buildingInteraction.BuildingSelected -= OnBuildingSelectedInternal;
            _buildingInteraction.BuildingHoverChanged -= OnBuildingHoverChangedInternal;
            _interactionEventsHooked = false;
        }

        void OnTerrainTileLoadedInternal(RuntimeTileRecord record) =>
            TerrainTileLoaded?.Invoke(record.Key);

        void OnTerrainTileUnloadedInternal(RuntimeTileRecord record) =>
            TerrainTileUnloaded?.Invoke(record.Key);

        void OnBuildingTileLoadedInternal(RuntimeTileRecord record) =>
            BuildingTileLoaded?.Invoke(record.Key);

        void OnBuildingTileUnloadedInternal(RuntimeTileRecord record) =>
            BuildingTileUnloaded?.Invoke(record.Key);

        void OnBuildingSelectedInternal(Transform buildingRoot, BuildingInfoSnapshot snapshot)
        {
            if (BuildingClicked == null || buildingRoot == null)
                return;

            BuildingClicked.Invoke(new ZGBuildingHandle(this, buildingRoot, snapshot?.tileId));
        }

        void OnBuildingHoverChangedInternal(Transform buildingRoot)
        {
            if (BuildingHoverChanged == null)
                return;

            if (buildingRoot == null)
            {
                BuildingHoverChanged.Invoke(null);
                return;
            }

            BuildingHoverChanged.Invoke(
                new ZGBuildingHandle(this, buildingRoot, ResolveTileIdFromHierarchy(buildingRoot)));
        }

        static string ResolveTileIdFromHierarchy(Transform node)
        {
            BuildingData data = node.GetComponentInParent<BuildingData>();
            if (data != null && !string.IsNullOrEmpty(data.tileId))
                return data.tileId;

            const string prefix = "TileBuildings_";
            Transform current = node;
            while (current != null)
            {
                if (current.name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    string suffix = current.name.Substring(prefix.Length);
                    if (!suffix.EndsWith("_Physics", StringComparison.Ordinal))
                        return suffix;
                }

                current = current.parent;
            }

            return null;
        }
    }
}
