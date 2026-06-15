using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect.RealtimeStreaming
{
    public readonly struct RuntimeBuildingTilePostProcessSettings
    {
        public readonly RealtimeBuildingColliderMode ColliderMode;
        public readonly bool CombineTileMeshes;
        public readonly bool CombineMeshesPerMaterial;

        public RuntimeBuildingTilePostProcessSettings(
            RealtimeBuildingColliderMode colliderMode,
            bool combineTileMeshes,
            bool combineMeshesPerMaterial)
        {
            ColliderMode = colliderMode;
            CombineTileMeshes = combineTileMeshes;
            CombineMeshesPerMaterial = combineMeshesPerMaterial;
        }
    }

    /// <summary>
    /// Pack-time building colliders and render-only mesh combine. Runtime loads pre-baked results from bundles.
    /// </summary>
    public static class RuntimeBuildingTilePostProcessor
    {
        public const string CombinedRenderRootName = "CombinedRender";

        public static void Release(Transform tileRoot)
        {
            if (tileRoot == null)
                return;

            RuntimeBuildingTileRuntimeState state = tileRoot.GetComponent<RuntimeBuildingTileRuntimeState>();
            if (state != null)
                state.ReleaseCombinedVisuals();
        }

        public static void ClearAllColliders(IReadOnlyList<Transform> buildings)
        {
            if (buildings == null)
                return;

            foreach (Transform building in buildings)
            {
                if (building != null)
                    ClearColliders(building.gameObject);
            }
        }

        public static void ApplyColliderForBuilding(
            Transform building,
            Transform tileRoot,
            RuntimeBuildingTileRuntimeState state,
            RealtimeBuildingColliderMode mode)
        {
            if (building == null || mode == RealtimeBuildingColliderMode.None)
                return;

            ClearColliders(building.gameObject);

            switch (mode)
            {
                case RealtimeBuildingColliderMode.Box:
                    AddBoxCollider(building);
                    break;
                case RealtimeBuildingColliderMode.ConvexMesh:
                    AddMeshCollider(building, tileRoot, state, convex: true);
                    break;
                case RealtimeBuildingColliderMode.FullMesh:
                    AddMeshCollider(building, tileRoot, state, convex: false);
                    break;
            }
        }

        public static void DisableSourceRenderersForFinalize(
            Transform tileRoot,
            RuntimeBuildingTileRuntimeState state)
        {
            if (tileRoot == null || state == null)
                return;

            BuildingLodController.ForEachBuildingTransform(tileRoot, building =>
            {
                foreach (MeshRenderer renderer in RuntimeBuildingMeshUtility.GetLod0Renderers(building))
                    state.RememberDisabledRenderer(renderer);

                LODGroup lodGroup = building.GetComponent<LODGroup>();
                if (lodGroup != null)
                    state.RememberDisabledLodGroup(lodGroup);
            });
        }

        public static void BuildCombinedRender(
            Transform tileRoot,
            RuntimeBuildingTileRuntimeState state,
            bool perMaterial)
        {
            if (tileRoot == null || state == null)
                return;

            var renderers = new List<MeshRenderer>();
            BuildingLodController.ForEachBuildingTransform(tileRoot, building =>
            {
                foreach (MeshRenderer renderer in RuntimeBuildingMeshUtility.GetLod0Renderers(building))
                    renderers.Add(renderer);

                LODGroup lodGroup = building.GetComponent<LODGroup>();
                if (lodGroup != null)
                    state.RememberDisabledLodGroup(lodGroup);
            });

            if (renderers.Count == 0)
                return;

            if (state.CombinedRenderRoot != null)
                RuntimeObjectUtility.Destroy(state.CombinedRenderRoot);

            var combinedRoot = new GameObject(CombinedRenderRootName);
            combinedRoot.transform.SetParent(tileRoot, false);
            combinedRoot.transform.localPosition = Vector3.zero;
            combinedRoot.transform.localRotation = Quaternion.identity;
            combinedRoot.transform.localScale = Vector3.one;
            combinedRoot.SetActive(false);
            state.CombinedRenderRoot = combinedRoot;
            state.CombinedRenderVisible = false;

            Matrix4x4 tileToLocal = tileRoot.worldToLocalMatrix;

            if (perMaterial)
            {
                var groups = new Dictionary<Material, List<CombineInstance>>();
                foreach (MeshRenderer renderer in renderers)
                {
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null)
                        continue;

                    Material[] materials = renderer.sharedMaterials;
                    Mesh mesh = filter.sharedMesh;
                    int subMeshCount = Mathf.Max(1, mesh.subMeshCount);
                    Matrix4x4 matrix = tileToLocal * filter.transform.localToWorldMatrix;

                    for (int s = 0; s < subMeshCount; s++)
                    {
                        Material mat = s < materials.Length ? materials[s] : materials[0];
                        if (mat == null)
                            continue;

                        if (!groups.TryGetValue(mat, out List<CombineInstance> list))
                        {
                            list = new List<CombineInstance>();
                            groups[mat] = list;
                        }

                        list.Add(new CombineInstance
                        {
                            mesh = mesh,
                            subMeshIndex = s,
                            transform = matrix,
                        });
                    }
                }

                int groupIndex = 0;
                foreach (KeyValuePair<Material, List<CombineInstance>> kvp in groups)
                {
                    Mesh combinedMesh = RuntimeBuildingMeshUtility.CombineMeshesInSpace(
                        kvp.Value,
                        mergeSubMeshes: true,
                        $"{tileRoot.name}_Combined_{groupIndex++}",
                        state.OwnedMeshes);
                    if (combinedMesh == null)
                        continue;

                    CreateCombinedRenderer(combinedRoot.transform, combinedMesh, kvp.Key, state);
                }
            }
            else
            {
                var instances = new List<CombineInstance>();
                var materials = new List<Material>();

                foreach (MeshRenderer renderer in renderers)
                {
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null)
                        continue;

                    Material[] rendererMaterials = renderer.sharedMaterials;
                    Mesh mesh = filter.sharedMesh;
                    int subMeshCount = Mathf.Max(1, mesh.subMeshCount);
                    Matrix4x4 matrix = tileToLocal * filter.transform.localToWorldMatrix;

                    for (int s = 0; s < subMeshCount; s++)
                    {
                        Material mat = s < rendererMaterials.Length ? rendererMaterials[s] : rendererMaterials[0];
                        instances.Add(new CombineInstance
                        {
                            mesh = mesh,
                            subMeshIndex = s,
                            transform = matrix,
                        });
                        materials.Add(mat);
                    }
                }

                Mesh combinedMesh = RuntimeBuildingMeshUtility.CombineMeshesInSpace(
                    instances,
                    mergeSubMeshes: false,
                    $"{tileRoot.name}_Combined",
                    state.OwnedMeshes);
                if (combinedMesh == null)
                    return;

                var combinedGo = new GameObject("CombinedMesh");
                combinedGo.transform.SetParent(combinedRoot.transform, false);
                combinedGo.AddComponent<MeshFilter>().sharedMesh = combinedMesh;
                MeshRenderer combinedRenderer = combinedGo.AddComponent<MeshRenderer>();
                combinedRenderer.sharedMaterials = materials.ToArray();
                combinedRenderer.enabled = false;
                state.OwnedMeshes.Add(combinedMesh);
            }

            foreach (MeshRenderer renderer in renderers)
                state.RememberDisabledRenderer(renderer);

            state.SetCombinedRenderVisible(false);
        }

        static RuntimeBuildingTileRuntimeState GetOrCreateState(Transform tileRoot)
        {
            RuntimeBuildingTileRuntimeState state = tileRoot.GetComponent<RuntimeBuildingTileRuntimeState>();
            if (state == null)
                state = tileRoot.gameObject.AddComponent<RuntimeBuildingTileRuntimeState>();
            return state;
        }

        static void ClearColliders(GameObject building)
        {
            foreach (Collider collider in building.GetComponents<Collider>())
                RuntimeObjectUtility.Destroy(collider);

            foreach (Collider collider in building.GetComponentsInChildren<Collider>(true))
            {
                if (collider.gameObject == building)
                    continue;

                RuntimeObjectUtility.Destroy(collider);
            }
        }

        static void AddBoxCollider(Transform building)
        {
            Bounds worldBounds = RuntimeBuildingMeshUtility.ComputeLod0WorldBounds(building);
            if (worldBounds.size.sqrMagnitude <= 0.0001f)
                return;

            Vector3 localCenter = building.InverseTransformPoint(worldBounds.center);
            Vector3 localSize = building.InverseTransformVector(worldBounds.size);
            localSize = new Vector3(Mathf.Abs(localSize.x), Mathf.Abs(localSize.y), Mathf.Abs(localSize.z));

            var box = building.gameObject.AddComponent<BoxCollider>();
            box.center = localCenter;
            box.size = localSize;
        }

        /// <summary>
        /// Per-building LOD0 combine for physics â€” not the tile-level CombinedRender from
        /// <see cref="BuildCombinedRender"/>. Colliders need one hull per building before LOD
        /// source geometry is stripped for the baked bundle.
        /// </summary>
        static void AddMeshCollider(
            Transform building,
            Transform tileRoot,
            RuntimeBuildingTileRuntimeState state,
            bool convex)
        {
            Mesh combined = RuntimeBuildingMeshUtility.CombineBuildingLod0Meshes(building, building);
            if (combined == null)
            {
                AddBoxCollider(building);
                return;
            }

            Mesh colliderMesh = RuntimeBuildingMeshUtility.CloneMeshGeometry(combined);
            if (colliderMesh == null)
            {
                RuntimeObjectUtility.Destroy(combined);
                AddBoxCollider(building);
                return;
            }

            if (!ReferenceEquals(combined, colliderMesh))
                RuntimeObjectUtility.Destroy(combined);

            colliderMesh.name = $"{building.name}_ColliderMesh";

            if (convex && !RuntimeBuildingMeshUtility.CanBakeConvexCollider(colliderMesh))
            {
                RuntimeObjectUtility.Destroy(colliderMesh);
                Debug.LogWarning(
                    $"[ZGConnect.Realtime] Building '{building.name}': convex mesh collider bake failed, using box collider.");
                AddBoxCollider(building);
                return;
            }

            var meshCollider = building.gameObject.AddComponent<MeshCollider>();
            meshCollider.sharedMesh = colliderMesh;
            meshCollider.convex = convex;

            state.OwnedMeshes.Add(colliderMesh);
        }

        /// <summary>Replaces convex mesh colliders that PhysX cannot cook (pack-time safety net).</summary>
        public static void SanitizeConvexMeshColliders(Transform tileRoot)
        {
            if (tileRoot == null)
                return;

            foreach (MeshCollider meshCollider in tileRoot.GetComponentsInChildren<MeshCollider>(true))
            {
                if (meshCollider == null || !meshCollider.convex)
                    continue;

                Mesh mesh = meshCollider.sharedMesh;
                if (mesh != null && RuntimeBuildingMeshUtility.CanBakeConvexCollider(mesh))
                    continue;

                Transform building = meshCollider.transform;
                Debug.LogWarning(
                    $"[ZGConnect.Realtime] Sanitizing invalid convex collider on '{building.name}', using box collider.");

                RuntimeObjectUtility.Destroy(meshCollider);
                if (building.GetComponent<Collider>() == null)
                    AddBoxCollider(building);
            }
        }

        static void CreateCombinedRenderer(
            Transform parent,
            Mesh mesh,
            Material material,
            RuntimeBuildingTileRuntimeState state)
        {
            var go = new GameObject($"Combined_{material.name}");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.enabled = false;
            state.OwnedMeshes.Add(mesh);
        }

        /// <summary>
        /// Removes per-building LOD mesh geometry after pack-time combine/collider bake.
        /// CombinedRender on the tile root is the only visual mesh data kept in the bundle.
        /// </summary>
        public static void StripBakedSourceVisualGeometry(
            Transform tileRoot,
            RuntimeBuildingTileRuntimeState state)
        {
            if (tileRoot == null)
                return;

            Transform combinedRoot = state?.CombinedRenderRoot != null
                ? state.CombinedRenderRoot.transform
                : tileRoot.Find(CombinedRenderRootName);

            BuildingLodController.ForEachBuildingTransform(tileRoot, building =>
            {
                LODGroup lodGroup = building.GetComponent<LODGroup>();
                if (lodGroup != null)
                    RuntimeObjectUtility.Destroy(lodGroup);

                Transform lod0 = building.Find("LOD0");
                Transform lod1 = building.Find("LOD1");
                DestroyLodVisualSubtree(lod0);
                DestroyLodVisualSubtree(lod1);

                if (lod0 == null && lod1 == null)
                    StripFlatHierarchyRenderers(building, combinedRoot);
            });

            state?.ClearRendererTracking();
        }

        static void DestroyLodVisualSubtree(Transform lodRoot)
        {
            if (lodRoot != null)
                RuntimeObjectUtility.Destroy(lodRoot.gameObject);
        }

        static void StripFlatHierarchyRenderers(Transform building, Transform combinedRoot)
        {
            if (building == null)
                return;

            var filters = building.GetComponentsInChildren<MeshFilter>(true);
            foreach (MeshFilter filter in filters)
            {
                if (filter == null)
                    continue;

                if (combinedRoot != null && filter.transform.IsChildOf(combinedRoot))
                    continue;

                MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
                if (renderer != null)
                    RuntimeObjectUtility.Destroy(renderer);

                RuntimeObjectUtility.Destroy(filter);
            }
        }

        /// <summary>
        /// Removes per-building scaffold transforms left after CombinedRender bake (visual prefab only).
        /// </summary>
        public static void StripEmptyBuildingShells(Transform tileRoot)
        {
            if (tileRoot == null)
                return;

            Transform combinedRoot = tileRoot.Find(CombinedRenderRootName);
            const int maxPasses = 64;

            for (int pass = 0; pass < maxPasses; pass++)
            {
                var toRemove = new List<GameObject>();
                for (int i = 0; i < tileRoot.childCount; i++)
                    CollectEmptyBuildingShells(tileRoot.GetChild(i), combinedRoot, toRemove);

                if (toRemove.Count == 0)
                    break;

                foreach (GameObject gameObject in toRemove)
                {
                    if (gameObject != null)
                        RuntimeObjectUtility.Destroy(gameObject);
                }
            }
        }

        static void CollectEmptyBuildingShells(
            Transform node,
            Transform combinedRoot,
            List<GameObject> toRemove)
        {
            if (node == null)
                return;

            for (int i = node.childCount - 1; i >= 0; i--)
                CollectEmptyBuildingShells(node.GetChild(i), combinedRoot, toRemove);

            if (ShouldPreserveVisualShellNode(node, combinedRoot))
                return;

            if (node.childCount > 0)
                return;

            toRemove.Add(node.gameObject);
        }

        static bool ShouldPreserveVisualShellNode(Transform node, Transform combinedRoot)
        {
            if (node == null)
                return true;

            if (combinedRoot != null &&
                (node == combinedRoot || node.IsChildOf(combinedRoot)))
            {
                return true;
            }

            string name = node.name;
            if (name.EndsWith("_Physics", StringComparison.OrdinalIgnoreCase))
                return true;

            if (node.GetComponent<MeshFilter>() != null ||
                node.GetComponent<MeshRenderer>() != null ||
                node.GetComponent<SkinnedMeshRenderer>() != null ||
                node.GetComponent<Collider>() != null ||
                node.GetComponent<LODGroup>() != null)
            {
                return true;
            }

            foreach (MonoBehaviour behaviour in node.GetComponents<MonoBehaviour>())
            {
                if (behaviour != null)
                    return true;
            }

            return false;
        }
    }
}
