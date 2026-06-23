using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect.Roads
{
    [ExecuteAlways]
    public sealed class RoadTopologyGizmoPreview : MonoBehaviour
    {
        [SerializeField] string _manifestPath;
        [SerializeField] string _tileId;
        [SerializeField] Color _asphaltColor = new(0.05f, 0.05f, 0.05f, 0.75f);
        [SerializeField] Color _curbColor = new(1f, 0.85f, 0.2f, 1f);
        [SerializeField] float _gizmoYOffset = 0.25f;

        RoadTopologyManifestLoader _loader;
        RoadTopologyTile _tile;
        string _loadedManifestPath;
        string _loadedTileId;
        string _loadError;

        void OnValidate()
        {
            _loader = null;
            _tile = null;
            _loadedManifestPath = null;
            _loadedTileId = null;
            _loadError = null;
        }

        void EnsureLoaded()
        {
            if (string.IsNullOrWhiteSpace(_manifestPath) || string.IsNullOrWhiteSpace(_tileId))
                return;

            if (_loadedManifestPath == _manifestPath && _loadedTileId == _tileId)
                return;

            _loader = null;
            _tile = null;
            _loadError = null;
            _loadedManifestPath = _manifestPath;
            _loadedTileId = _tileId;

            try
            {
                _loader = new RoadTopologyManifestLoader(_manifestPath);
                _tile = _loader.TryGetTile(_tileId, out RoadTopologyManifestTile manifestTile)
                    ? _loader.LoadTile(manifestTile)
                    : null;
            }
            catch (Exception ex)
            {
                _loadError = ex.Message;
            }
        }

        void OnDrawGizmos()
        {
            EnsureLoaded();

            if (!string.IsNullOrEmpty(_loadError))
            {
                Debug.LogWarning($"Road topology gizmo preview could not load '{_manifestPath}': {_loadError}", this);
                _loadError = null;
            }

            if (_tile == null)
                return;

            if (_tile.AsphaltPolygons != null)
            {
                Gizmos.color = _asphaltColor;
                foreach (RoadPolygon polygon in _tile.AsphaltPolygons)
                {
                    if (polygon != null)
                        DrawRing(polygon.Outer, closed: true);
                }
            }

            if (_tile.CurbChains != null)
            {
                Gizmos.color = _curbColor;
                foreach (RoadPolyline curb in _tile.CurbChains)
                {
                    if (curb != null)
                        DrawRing(curb.Points, closed: false);
                }
            }
        }

        void DrawRing(List<float[]> points, bool closed)
        {
            if (points == null || points.Count < 2)
                return;

            for (int i = 0; i < points.Count - 1; i++)
                Gizmos.DrawLine(ToWorld(points[i]), ToWorld(points[i + 1]));

            if (closed && points.Count > 2)
                Gizmos.DrawLine(ToWorld(points[^1]), ToWorld(points[0]));
        }

        Vector3 ToWorld(float[] xy)
        {
            // Topology coordinates are absolute EPSG/world positions, not local points.
            return RoadTopologyGeometry.EpsgPointToWorld(xy, _gizmoYOffset);
        }
    }
}
