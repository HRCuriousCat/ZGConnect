using System.Globalization;
using System.Text;
using UnityEngine;

namespace ZGConnect
{
    /// <summary>
    /// Lightweight handle to one building in the streamed city.
    ///
    /// Works in both runtime modes:
    ///  - Runtime streaming (RealtimeStreamingController) — buildings carry no components;
    ///    metadata is loaded lazily through the toolkit on first access.
    ///  - Editor / dataset scenes — buildings carry a <see cref="BuildingData"/> component
    ///    which is read directly.
    ///
    /// Obtain handles through <see cref="ZGConnectToolkit"/> (search, raycast, radius queries).
    /// </summary>
    public sealed class ZGBuildingHandle
    {
        readonly ZGConnectToolkit _toolkit;

        BuildingInfoSnapshot _snapshot;
        bool _metadataAttempted;
        Bounds _bounds;
        bool _boundsComputed;
        Vector3 _worldPosition;
        bool _hasWorldPosition;

        internal ZGBuildingHandle(ZGConnectToolkit toolkit, Transform transform, string tileId)
        {
            _toolkit = toolkit;
            Transform = transform;
            TileId = tileId;
            Name = transform != null ? transform.name : string.Empty;
            BuildingId = BuildingSurfaceUtility.ExtractBuildingIdFromObjectName(Name);
        }

        /// <summary>Metadata-only handle for buildings whose tile is not streamed in yet.</summary>
        internal ZGBuildingHandle(
            ZGConnectToolkit toolkit,
            string tileId,
            string name,
            Vector3 worldPosition,
            BuildingInfoSnapshot snapshot)
        {
            _toolkit = toolkit;
            TileId = tileId;
            Name = name ?? string.Empty;
            BuildingId = BuildingSurfaceUtility.ExtractBuildingIdFromObjectName(Name);
            _worldPosition = worldPosition;
            _hasWorldPosition = true;
            _snapshot = snapshot;
            _metadataAttempted = true;
        }

        // ── Identity ───────────────────────────────────────────────────────────

        /// <summary>Scene transform of the building root. Null for metadata-only (unloaded tile) handles.</summary>
        public Transform Transform { get; }

        /// <summary>Terrain tile this building belongs to (e.g. "550000_5068000").</summary>
        public string TileId { get; }

        /// <summary>GameObject name (e.g. "zagreb_Part_1619").</summary>
        public string Name { get; }

        /// <summary>Numeric building identifier extracted from the name (e.g. "1619").</summary>
        public string BuildingId { get; }

        /// <summary>True when the building mesh is present in the scene (streamed in).</summary>
        public bool IsLoaded => Transform != null && Transform.gameObject.activeInHierarchy;

        /// <summary>True when the handle has a scene object or a resolved metadata position.</summary>
        public bool IsValid => IsLoaded || _hasWorldPosition;

        /// <summary>World position — from the scene transform when loaded, otherwise from tile metadata.</summary>
        public Vector3 Position
        {
            get
            {
                if (Transform != null)
                    return Transform.position;
                return _hasWorldPosition ? _worldPosition : Vector3.zero;
            }
        }

        // ── Metadata ───────────────────────────────────────────────────────────

        /// <summary>
        /// Full metadata snapshot (GPS, OSM tags, address enrichment). Loaded lazily on first
        /// access — the first call per tile reads the tile metadata file from disk. Null when
        /// no metadata exists for this building.
        /// </summary>
        public BuildingInfoSnapshot Metadata
        {
            get
            {
                if (_snapshot == null && !_metadataAttempted)
                {
                    _metadataAttempted = true;
                    _snapshot = _toolkit != null
                        ? _toolkit.ResolveBuildingSnapshot(Transform, TileId)
                        : null;
                }

                return _snapshot;
            }
        }

        /// <summary>True once metadata has been resolved successfully.</summary>
        public bool HasMetadata => Metadata != null;

        /// <summary>Street address ("Ilica 5") or null. Composed from OSM tags when no official address exists.</summary>
        public string Address => Metadata?.address;

        /// <summary>Street name from OSM (addr:street) or null.</summary>
        public string Street => Metadata?.street;

        /// <summary>Number of above-ground floors (0 = unknown).</summary>
        public int Floors => Metadata?.floors ?? 0;

        /// <summary>Gross floor area in m² (0 = unknown).</summary>
        public float GrossFloorArea => Metadata?.grossFloorArea ?? 0f;

        /// <summary>Land-use classification (Residential / Commercial / ...) or null.</summary>
        public string UseClassification => Metadata?.useClassification;

        /// <summary>Year of construction (0 = unknown).</summary>
        public int ConstructionYear => Metadata?.constructionYear ?? 0;

        /// <summary>OSM building tag value ("residential", "church", ...) or null.</summary>
        public string OsmBuildingTag => Metadata?.buildingTag;

        /// <summary>Returns the value of an arbitrary OSM tag on the matched footprint, or null.</summary>
        public string GetOsmTag(string tagKey) => Metadata?.GetOsmTag(tagKey);

        /// <summary>
        /// GPS coordinates of the building. Prefers metadata GPS; falls back to converting
        /// the transform position. Returns false only when the handle is fully invalid.
        /// </summary>
        public bool TryGetGps(out double latitude, out double longitude)
        {
            BuildingInfoSnapshot meta = Metadata;
            if (meta != null && meta.gps.hasData)
            {
                latitude = meta.gps.latitude;
                longitude = meta.gps.longitude;
                return true;
            }

            if (Transform != null)
            {
                (latitude, longitude) = ZGConnectCoordinates.UnityToWGS84(Transform.position);
                return true;
            }

            if (_hasWorldPosition)
            {
                (latitude, longitude) = ZGConnectCoordinates.UnityToWGS84(_worldPosition);
                return true;
            }

            latitude = 0;
            longitude = 0;
            return false;
        }

        // ── Geometry ───────────────────────────────────────────────────────────

        /// <summary>World-space bounds of the building (renderers, falling back to colliders).</summary>
        public Bounds GetWorldBounds()
        {
            if (_boundsComputed)
                return _bounds;

            _boundsComputed = true;
            _bounds = new Bounds(Position, Vector3.zero);
            if (Transform == null)
            {
                // Metadata-only: approximate footprint for rooftop/base helpers.
                if (_hasWorldPosition)
                    _bounds = new Bounds(_worldPosition, new Vector3(12f, 18f, 12f));
                return _bounds;
            }

            bool any = false;
            foreach (Renderer renderer in Transform.GetComponentsInChildren<Renderer>(true))
            {
                if (!any)
                {
                    _bounds = renderer.bounds;
                    any = true;
                }
                else
                {
                    _bounds.Encapsulate(renderer.bounds);
                }
            }

            if (!any)
            {
                foreach (Collider collider in Transform.GetComponentsInChildren<Collider>(true))
                {
                    if (!any)
                    {
                        _bounds = collider.bounds;
                        any = true;
                    }
                    else
                    {
                        _bounds.Encapsulate(collider.bounds);
                    }
                }
            }

            return _bounds;
        }

        /// <summary>Approximate building height in metres (bounds Y extent).</summary>
        public float GetHeightMeters() => GetWorldBounds().size.y;

        /// <summary>Top-centre point of the roof in world space (marker placement, drones, ...).</summary>
        public Vector3 GetRooftopPosition()
        {
            Bounds b = GetWorldBounds();
            return new Vector3(b.center.x, b.max.y, b.center.z);
        }

        /// <summary>Bottom-centre point of the building in world space.</summary>
        public Vector3 GetBasePosition()
        {
            Bounds b = GetWorldBounds();
            return new Vector3(b.center.x, b.min.y, b.center.z);
        }

        // ── Display ────────────────────────────────────────────────────────────

        /// <summary>
        /// One-line human-readable summary, e.g.
        /// "1619 — Ilica 5, Zagreb | residential | 4 floors | built 1932".
        /// </summary>
        public string GetSummary()
        {
            var sb = new StringBuilder(96);
            sb.Append(string.IsNullOrEmpty(BuildingId) ? Name : BuildingId);

            BuildingInfoSnapshot meta = Metadata;
            if (meta != null)
            {
                if (!string.IsNullOrEmpty(meta.address))
                    sb.Append(" \u2014 ").Append(meta.address);
                if (!string.IsNullOrEmpty(meta.buildingTag))
                    sb.Append(" | ").Append(meta.buildingTag);
                if (meta.floors > 0)
                    sb.Append(" | ").Append(meta.floors).Append(" floors");
                if (meta.constructionYear > 0)
                    sb.Append(" | built ").Append(meta.constructionYear);
                if (meta.useClassification is { Length: > 0 })
                    sb.Append(" | ").Append(meta.useClassification);
            }

            return sb.ToString();
        }

        /// <summary>Formatted GPS string ("45.81234° N   15.97654° E") or empty.</summary>
        public string GetGpsText()
        {
            return TryGetGps(out double lat, out double lon)
                ? ZGConnectCoordinates.Format(lat, lon)
                : string.Empty;
        }

        /// <summary>Google Maps URL pointing at this building, or null when the handle is invalid.</summary>
        public string GetGoogleMapsUrl()
        {
            if (!TryGetGps(out double lat, out double lon))
                return null;

            return string.Format(
                CultureInfo.InvariantCulture, "https://www.google.com/maps?q={0:F7},{1:F7}", lat, lon);
        }

        public override string ToString() => GetSummary();
    }
}
