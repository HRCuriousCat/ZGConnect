using System.Collections.Generic;
using UnityEngine;
using ZGConnect.RealtimeStreaming;

namespace ZGConnect
{
    /// <summary>Builds highlight-layer mesh copies for hover and selection states.</summary>
    public static class BuildingGhostBuilder
    {
        const int MaxSubMeshes = 3;

        static Mesh _unitCubeMesh;

        public static GameObject Build(
            Transform building,
            Transform parent,
            Material material,
            Dictionary<Mesh, Mesh> meshCloneCache,
            string rootName)
        {
            if (building == null || parent == null || material == null)
                return null;

            meshCloneCache ??= new Dictionary<Mesh, Mesh>();

            var ghostRoot = new GameObject(rootName);
            ghostRoot.transform.SetParent(parent, false);

            int highlightLayer = BuildingInteractionLayers.HighlightLayer;
            int rendererIndex = 0;

            foreach (MeshRenderer sourceRenderer in RuntimeBuildingMeshUtility.GetLod0Renderers(building))
            {
                MeshFilter sourceFilter = sourceRenderer.GetComponent<MeshFilter>();
                if (sourceFilter == null || sourceFilter.sharedMesh == null)
                    continue;

                TryAddGhostPart(
                    ghostRoot.transform,
                    sourceFilter.transform,
                    sourceFilter.sharedMesh,
                    material,
                    meshCloneCache,
                    highlightLayer,
                    ref rendererIndex);
            }

            if (rendererIndex == 0)
                TryAddGhostFromColliders(building, ghostRoot.transform, material, meshCloneCache, highlightLayer, ref rendererIndex);

            if (rendererIndex == 0)
            {
                Object.Destroy(ghostRoot);
                return null;
            }

            return ghostRoot;
        }

        static void TryAddGhostFromColliders(
            Transform building,
            Transform ghostRoot,
            Material material,
            Dictionary<Mesh, Mesh> meshCloneCache,
            int highlightLayer,
            ref int rendererIndex)
        {
            foreach (MeshCollider meshCollider in building.GetComponentsInChildren<MeshCollider>(true))
            {
                if (meshCollider == null || IsUnderCombinedRender(meshCollider.transform))
                    continue;

                Mesh sourceMesh = meshCollider.sharedMesh;
                if (sourceMesh == null)
                    continue;

                if (TryAddGhostPart(
                        ghostRoot,
                        meshCollider.transform,
                        sourceMesh,
                        material,
                        meshCloneCache,
                        highlightLayer,
                        ref rendererIndex))
                {
                    return;
                }
            }

            foreach (BoxCollider boxCollider in building.GetComponentsInChildren<BoxCollider>(true))
            {
                if (boxCollider == null || IsUnderCombinedRender(boxCollider.transform))
                    continue;

                if (TryAddGhostPartFromBox(ghostRoot, boxCollider, material, highlightLayer, ref rendererIndex))
                    return;
            }
        }

        static bool TryAddGhostPartFromBox(
            Transform ghostRoot,
            BoxCollider boxCollider,
            Material material,
            int highlightLayer,
            ref int rendererIndex)
        {
            if (boxCollider.size.sqrMagnitude <= 0.0001f)
                return false;

            Mesh cubeMesh = GetUnitCubeMesh();
            if (cubeMesh == null)
                return false;

            var frameGo = new GameObject($"Part_{rendererIndex++}");
            frameGo.transform.SetParent(ghostRoot, false);
            frameGo.transform.position = boxCollider.transform.position;
            frameGo.transform.rotation = boxCollider.transform.rotation;
            frameGo.transform.localScale = boxCollider.transform.lossyScale;
            frameGo.layer = highlightLayer;

            var partGo = new GameObject("BoxVolume");
            partGo.transform.SetParent(frameGo.transform, false);
            partGo.transform.localPosition = boxCollider.center;
            partGo.transform.localRotation = Quaternion.identity;
            partGo.transform.localScale = boxCollider.size;
            partGo.layer = highlightLayer;

            var filter = partGo.AddComponent<MeshFilter>();
            filter.sharedMesh = cubeMesh;

            var renderer = partGo.AddComponent<MeshRenderer>();
            ConfigureGhostRenderer(renderer, BuildMaterialsForMesh(cubeMesh, material));
            return true;
        }

        static bool TryAddGhostPart(
            Transform ghostRoot,
            Transform sourceTransform,
            Mesh sourceMesh,
            Material material,
            Dictionary<Mesh, Mesh> meshCloneCache,
            int highlightLayer,
            ref int rendererIndex)
        {
            if (sourceMesh == null)
                return false;

            if (!meshCloneCache.TryGetValue(sourceMesh, out Mesh clonedMesh) || clonedMesh == null)
            {
                clonedMesh = RuntimeBuildingMeshUtility.CloneMeshGeometry(sourceMesh);
                if (clonedMesh == null)
                    return false;
                meshCloneCache[sourceMesh] = clonedMesh;
            }

            var partGo = new GameObject($"Part_{rendererIndex++}");
            partGo.transform.SetParent(ghostRoot, false);
            partGo.transform.position = sourceTransform.position;
            partGo.transform.rotation = sourceTransform.rotation;
            partGo.transform.localScale = sourceTransform.lossyScale;
            partGo.layer = highlightLayer;

            var filter = partGo.AddComponent<MeshFilter>();
            filter.sharedMesh = clonedMesh;

            var renderer = partGo.AddComponent<MeshRenderer>();
            ConfigureGhostRenderer(renderer, BuildMaterialsForMesh(clonedMesh, material));
            return true;
        }

        static void ConfigureGhostRenderer(MeshRenderer renderer, Material[] materials)
        {
            renderer.sharedMaterials = materials;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = true;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        static bool IsUnderCombinedRender(Transform transform)
        {
            Transform current = transform;
            while (current != null)
            {
                if (current.name == RuntimeBuildingTilePostProcessor.CombinedRenderRootName)
                    return true;
                current = current.parent;
            }

            return false;
        }

        static Mesh GetUnitCubeMesh()
        {
            if (_unitCubeMesh != null)
                return _unitCubeMesh;

            var temp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            MeshFilter tempFilter = temp.GetComponent<MeshFilter>();
            _unitCubeMesh = tempFilter != null
                ? Object.Instantiate(tempFilter.sharedMesh)
                : null;
            if (_unitCubeMesh != null)
                _unitCubeMesh.name = "BuildingHighlight_UnitCube";

            Object.Destroy(temp);
            return _unitCubeMesh;
        }

        public static void DestroyMeshCloneCache(Dictionary<Mesh, Mesh> meshCloneCache)
        {
            if (meshCloneCache == null)
                return;

            foreach (Mesh mesh in meshCloneCache.Values)
            {
                if (mesh != null)
                    Object.Destroy(mesh);
            }

            meshCloneCache.Clear();
        }

        static Material[] BuildMaterialsForMesh(Mesh mesh, Material material)
        {
            int subMeshCount = mesh != null ? Mathf.Max(1, mesh.subMeshCount) : 1;
            subMeshCount = Mathf.Min(subMeshCount, MaxSubMeshes);

            var materials = new Material[subMeshCount];
            for (int i = 0; i < subMeshCount; i++)
                materials[i] = material;

            return materials;
        }
    }
}