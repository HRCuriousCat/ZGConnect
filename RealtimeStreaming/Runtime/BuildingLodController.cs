using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect.RealtimeStreaming
{
    public class BuildingLodController
    {
        readonly float _cullDistanceMeters;
        readonly float _lod0Screen;
        readonly float _lod1Screen;

        public BuildingLodController(float cullDistanceMeters, float lod0Screen, float lod1Screen)
        {
            _cullDistanceMeters = cullDistanceMeters;
            _lod0Screen = lod0Screen;
            _lod1Screen = lod1Screen;
        }

        public void SetupLodGroupsFromHierarchy(Transform tileRoot)
        {
            if (tileRoot == null)
                return;

            for (int i = 0; i < tileRoot.childCount; i++)
                SetupBuildingLod(tileRoot.GetChild(i));
        }

        public void MergeDualGlb(Transform lod0Root, Transform lod1Root)
        {
            if (lod0Root == null || lod1Root == null)
                return;

            var lod1Lookup = new Dictionary<string, Transform>();
            for (int i = 0; i < lod1Root.childCount; i++)
            {
                Transform child = lod1Root.GetChild(i);
                lod1Lookup[NormalizeBuildingName(child.name)] = child;
            }

            for (int i = 0; i < lod0Root.childCount; i++)
            {
                Transform building = lod0Root.GetChild(i);
                string key = NormalizeBuildingName(building.name);
                if (!lod1Lookup.TryGetValue(key, out Transform lod1Building))
                    continue;

                SetupLodGroupFromRenderers(building, lod1Building);
            }
        }

        public void UpdateCull(Vector3 cameraPosition, Transform tileRoot)
        {
            if (tileRoot == null || tileRoot.childCount == 0)
                return;

            if (_cullDistanceMeters <= 0f)
            {
                ForEachBuildingTransform(tileRoot, building =>
                {
                    if (!building.gameObject.activeSelf)
                        building.gameObject.SetActive(true);
                });
                return;
            }

            float cullSqr = _cullDistanceMeters * _cullDistanceMeters;
            float camX = cameraPosition.x;
            float camZ = cameraPosition.z;

            ForEachBuildingTransform(tileRoot, building =>
            {
                float dx = building.position.x - camX;
                float dz = building.position.z - camZ;
                bool visible = dx * dx + dz * dz <= cullSqr;
                if (building.gameObject.activeSelf != visible)
                    building.gameObject.SetActive(visible);
            });
        }

        public static void ForEachBuildingTransform(Transform tileRoot, System.Action<Transform> action)
        {
            if (tileRoot == null || action == null)
                return;

            for (int i = 0; i < tileRoot.childCount; i++)
            {
                Transform child = tileRoot.GetChild(i);
                if (child.name == RuntimeBuildingTilePostProcessor.CombinedRenderRootName)
                    continue;

                if (IsNestedTileBuildingsGroup(child))
                    ForEachBuildingTransform(child, action);
                else
                    action(child);
            }
        }

        static bool IsNestedTileBuildingsGroup(Transform node)
        {
            if (node == null || !node.name.StartsWith("TileBuildings_", System.StringComparison.Ordinal))
                return false;

            return !HasMeshComponents(node) && node.childCount > 0;
        }

        static bool HasMeshComponents(Transform node)
        {
            if (node == null)
                return false;

            return node.GetComponent<MeshFilter>() != null
                   || node.GetComponent<MeshRenderer>() != null
                   || node.GetComponent<SkinnedMeshRenderer>() != null;
        }

        void SetupBuildingLod(Transform building)
        {
            Transform lod0 = building.Find("LOD0");
            Transform lod1 = building.Find("LOD1");

            if (lod0 != null && lod1 != null)
            {
                SetupLodGroupFromRenderers(lod0, lod1);
                return;
            }

            if (lod0 == null && lod1 == null)
                return;

            if (lod0 != null)
                SetupLodGroupFromRenderers(building, lod0);
            else if (lod1 != null)
                SetupLodGroupFromRenderers(building, lod1);
        }

        void SetupLodGroupFromRenderers(Transform lod0Node, Transform lod1Node)
        {
            Renderer[] r0 = lod0Node.GetComponentsInChildren<Renderer>(true);
            Renderer[] r1 = lod1Node.GetComponentsInChildren<Renderer>(true);
            if (r0.Length == 0 || r1.Length == 0)
                return;

            Transform host = lod0Node.parent != null ? lod0Node.parent : lod0Node;
            LODGroup group = host.GetComponent<LODGroup>();
            if (group == null)
                group = host.gameObject.AddComponent<LODGroup>();

            group.SetLODs(new[]
            {
                new LOD(_lod0Screen, r0),
                new LOD(_lod1Screen, r1),
            });
            group.RecalculateBounds();
        }

        static string NormalizeBuildingName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;
            int slash = name.LastIndexOf('/');
            return slash >= 0 ? name.Substring(slash + 1) : name;
        }
    }
}
