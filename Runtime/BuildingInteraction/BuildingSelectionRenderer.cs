using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect
{
    /// <summary>
    /// Persistent selection highlights â€” one mesh copy per selected building until cleared.
    /// </summary>
    public sealed class BuildingSelectionRenderer
    {
        readonly Transform _root;
        readonly Material _selectionMaterial;
        readonly Dictionary<Mesh, Mesh> _meshCloneCache = new();
        readonly Dictionary<Transform, GameObject> _ghosts = new();

        public BuildingSelectionRenderer(Transform root, Material selectionMaterial)
        {
            _root = root;
            _selectionMaterial = selectionMaterial;
        }

        public bool IsSelected(Transform building)
        {
            return building != null && _ghosts.ContainsKey(building);
        }

        public void Select(Transform building)
        {
            if (building == null || _selectionMaterial == null || IsSelected(building))
                return;

            GameObject ghostRoot = BuildingGhostBuilder.Build(
                building,
                _root,
                _selectionMaterial,
                _meshCloneCache,
                $"Selection_{building.name}");

            if (ghostRoot != null)
                _ghosts[building] = ghostRoot;
        }

        public void ClearAll()
        {
            foreach (GameObject ghost in _ghosts.Values)
            {
                if (ghost != null)
                    Object.Destroy(ghost);
            }

            _ghosts.Clear();
        }

        public void Dispose()
        {
            ClearAll();
            BuildingGhostBuilder.DestroyMeshCloneCache(_meshCloneCache);
        }

        public void Tick()
        {
            var stale = new List<Transform>();
            foreach (KeyValuePair<Transform, GameObject> entry in _ghosts)
            {
                if (entry.Key == null || !entry.Key.gameObject.activeInHierarchy)
                    stale.Add(entry.Key);
            }

            for (int i = 0; i < stale.Count; i++)
                RemoveSelection(stale[i]);
        }

        void RemoveSelection(Transform building)
        {
            if (building == null)
                return;

            if (_ghosts.TryGetValue(building, out GameObject ghost))
            {
                if (ghost != null)
                    Object.Destroy(ghost);
                _ghosts.Remove(building);
            }
        }
    }
}

