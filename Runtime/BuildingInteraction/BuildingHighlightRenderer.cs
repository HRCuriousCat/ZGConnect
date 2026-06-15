using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect
{
    /// <summary>
    /// Transient hover highlight â€” one building at a time, cleared when the pointer leaves.
    /// </summary>
    public sealed class BuildingHighlightRenderer
    {
        readonly Transform _root;
        readonly Material _highlightMaterial;
        readonly Dictionary<Mesh, Mesh> _meshCloneCache = new();

        GameObject _ghostRoot;
        Transform _currentBuilding;

        public BuildingHighlightRenderer(Transform root, Material highlightMaterial)
        {
            _root = root;
            _highlightMaterial = highlightMaterial;
        }

        public Transform CurrentBuilding => _currentBuilding;

        public void SetTarget(Transform building)
        {
            if (building == null)
            {
                Clear();
                return;
            }

            if (_currentBuilding == building && _ghostRoot != null)
                return;

            Clear();
            _currentBuilding = building;
            _ghostRoot = BuildingGhostBuilder.Build(
                building,
                _root,
                _highlightMaterial,
                _meshCloneCache,
                $"Hover_{building.name}");
        }

        public void Clear()
        {
            _currentBuilding = null;
            if (_ghostRoot != null)
            {
                Object.Destroy(_ghostRoot);
                _ghostRoot = null;
            }
        }

        public void Dispose()
        {
            Clear();
            BuildingGhostBuilder.DestroyMeshCloneCache(_meshCloneCache);
        }

        public void Tick()
        {
            if (_currentBuilding == null)
                return;

            if (!_currentBuilding.gameObject.activeInHierarchy)
                Clear();
        }
    }
}
