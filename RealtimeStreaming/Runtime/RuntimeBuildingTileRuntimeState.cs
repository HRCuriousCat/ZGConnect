using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect.RealtimeStreaming
{
    /// <summary>Tracks runtime-generated combined meshes / disabled renderers for one building tile.</summary>
    public sealed class RuntimeBuildingTileRuntimeState : MonoBehaviour
    {
        [System.NonSerialized] List<Mesh> _ownedMeshes;

        public List<Mesh> OwnedMeshes => _ownedMeshes ??= new List<Mesh>();

        public GameObject CombinedRenderRoot;
        public bool SourceRenderersDisabled;
        public bool CombinedRenderVisible;
        public bool IsFullyFinalized;
        public int FinalizedSettingsFingerprint;
        public string PackBakeFingerprint;

        public static int ComputeSettingsFingerprint(RuntimeBuildingTilePostProcessSettings settings) =>
            unchecked(((int)settings.ColliderMode * 397) ^
                      (settings.CombineTileMeshes ? 0x1000 : 0) ^
                      (settings.CombineMeshesPerMaterial ? 0x2000 : 0));

        public bool WasBakedAtPack(StreamingPackBakeManifest bake) =>
            IsFullyFinalized &&
            !string.IsNullOrEmpty(PackBakeFingerprint) &&
            bake != null &&
            PackBakeFingerprint == bake.Fingerprint;

        public bool UsesCombinedRender =>
            CombinedRenderRoot != null && (CombinedRenderVisible || IsFullyFinalized);

        public void EnsureCombinedRenderShown()
        {
            ResolveCombinedRenderRoot();
            if (CombinedRenderRoot == null)
                return;

            CombinedRenderRoot.SetActive(true);
            SetCombinedRenderVisible(true);
        }

        public void ResolveCombinedRenderRoot()
        {
            if (CombinedRenderRoot != null)
                return;

            Transform found = transform.Find(RuntimeBuildingTilePostProcessor.CombinedRenderRootName);
            if (found != null)
                CombinedRenderRoot = found.gameObject;
        }

        public void ApplyTileRenderingEnabled(bool enabled)
        {
            if (UsesCombinedRender)
            {
                if (!enabled)
                {
                    SetCombinedRenderVisible(false);
                    if (CombinedRenderRoot != null)
                        CombinedRenderRoot.SetActive(false);
                    return;
                }

                EnsureCombinedRenderShown();
                return;
            }

            if (!enabled)
            {
                foreach (MeshRenderer renderer in _disabledRenderers)
                {
                    if (renderer != null)
                        renderer.enabled = false;
                }

                return;
            }

            RestoreSourceRenderers();
        }

        [System.NonSerialized] readonly List<MeshRenderer> _disabledRenderers = new();
        [System.NonSerialized] readonly List<LODGroup> _disabledLodGroups = new();
        [System.NonSerialized] readonly List<Collider> _buildingColliders = new();
        bool _collidersCached;
        bool _collidersEnabled = true;

        public GameObject PhysicsRoot { get; private set; }
        public bool PhysicsLoadInProgress { get; private set; }
        public bool UsesSplitPhysicsPrefab { get; private set; }

        public bool CollidersEnabled => _collidersEnabled;

        public bool HasActivePhysics =>
            PhysicsRoot != null || (_collidersCached && _collidersEnabled && !UsesSplitPhysicsPrefab);

        public void CacheBuildingColliders()
        {
            if (_collidersCached)
                return;

            _buildingColliders.Clear();
            Transform searchRoot = PhysicsRoot != null ? PhysicsRoot.transform : transform;
            foreach (Collider collider in searchRoot.GetComponentsInChildren<Collider>(true))
            {
                if (collider == null)
                    continue;

                if (PhysicsRoot == null)
                {
                    Transform combinedRoot = CombinedRenderRoot != null ? CombinedRenderRoot.transform : null;
                    if (combinedRoot != null && collider.transform.IsChildOf(combinedRoot))
                        continue;
                }

                _buildingColliders.Add(collider);
            }

            _collidersCached = true;
        }

        public void ConfigureSplitPhysicsPrefab(bool usesSplitPhysicsPrefab)
        {
            UsesSplitPhysicsPrefab = usesSplitPhysicsPrefab;
        }

        public void BeginPhysicsLoad()
        {
            PhysicsLoadInProgress = true;
        }

        public void AttachPhysicsRoot(GameObject physicsRoot)
        {
            PhysicsLoadInProgress = false;
            if (physicsRoot == null)
                return;

            PhysicsRoot = physicsRoot;
            _collidersCached = false;
            _collidersEnabled = true;
            CacheBuildingColliders();
        }

        public void DetachPhysicsRoot()
        {
            PhysicsLoadInProgress = false;
            if (PhysicsRoot != null)
            {
                RuntimeObjectUtility.Destroy(PhysicsRoot);
                PhysicsRoot = null;
            }

            _buildingColliders.Clear();
            _collidersCached = false;
            _collidersEnabled = false;
        }

        public void SetCollidersEnabled(bool enabled)
        {
            if (UsesSplitPhysicsPrefab)
                return;

            if (!_collidersCached)
                CacheBuildingColliders();

            if (_collidersEnabled == enabled)
                return;

            _collidersEnabled = enabled;
            for (int i = 0; i < _buildingColliders.Count; i++)
            {
                Collider collider = _buildingColliders[i];
                if (collider != null)
                    collider.enabled = enabled;
            }
        }

        public void ReleaseCombinedVisuals()
        {
            if (CombinedRenderRoot != null)
                RuntimeObjectUtility.Destroy(CombinedRenderRoot);
            CombinedRenderRoot = null;

            for (int i = OwnedMeshes.Count - 1; i >= 0; i--)
            {
                if (OwnedMeshes[i] != null)
                    RuntimeObjectUtility.Destroy(OwnedMeshes[i]);
            }

            OwnedMeshes.Clear();
            CombinedRenderVisible = false;
            RestoreSourceRenderers();
        }

        public void SetCombinedRenderVisible(bool visible)
        {
            CombinedRenderVisible = visible;
            if (CombinedRenderRoot == null)
                return;

            foreach (Renderer renderer in CombinedRenderRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer != null)
                    renderer.enabled = visible;
            }
        }

        public void RestoreSourceRenderers()
        {
            foreach (MeshRenderer renderer in _disabledRenderers)
            {
                if (renderer != null)
                    renderer.enabled = true;
            }

            foreach (LODGroup lodGroup in _disabledLodGroups)
            {
                if (lodGroup != null)
                    lodGroup.enabled = true;
            }

            _disabledRenderers.Clear();
            _disabledLodGroups.Clear();
            SourceRenderersDisabled = false;
        }

        public void RememberDisabledRenderer(MeshRenderer renderer)
        {
            if (renderer == null || _disabledRenderers.Contains(renderer))
                return;

            _disabledRenderers.Add(renderer);
            renderer.enabled = false;
            SourceRenderersDisabled = true;
        }

        public void RememberDisabledLodGroup(LODGroup lodGroup)
        {
            if (lodGroup == null || _disabledLodGroups.Contains(lodGroup))
                return;

            _disabledLodGroups.Add(lodGroup);
            lodGroup.enabled = false;
        }

        public void ClearRendererTracking()
        {
            _disabledRenderers.Clear();
            _disabledLodGroups.Clear();
            SourceRenderersDisabled = false;
        }

        void OnDestroy()
        {
            DetachPhysicsRoot();
            ReleaseCombinedVisuals();
            IsFullyFinalized = false;
            FinalizedSettingsFingerprint = 0;
        }
    }
}
