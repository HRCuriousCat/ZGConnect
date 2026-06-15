using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ZGConnect.PhotogrammetryStreaming
{
    public sealed class TilesetTraverser
    {
        readonly PhotogrammetryLodProfile _lod;
        readonly ZagrebGeographicClipper _clipper;
        readonly EcefToUnityTransform _transform;
        readonly Vector3d _regionAnchorEcef;

        public TilesetTraverser(
            PhotogrammetryLodProfile lod,
            ZagrebGeographicClipper clipper,
            EcefToUnityTransform transform)
        {
            _lod = lod;
            _clipper = clipper;
            _transform = transform;
            _regionAnchorEcef = transform.AnchorEcef;
        }

        public void CollectDesiredTiles(
            Tile3DNode root,
            Camera camera,
            HashSet<string> desiredContentUris,
            HashSet<string> preloadJsonUris,
            Vector3 cameraWorldPos,
            IReadOnlyCollection<string> alreadyLoadedUris)
        {
            desiredContentUris.Clear();
            preloadJsonUris.Clear();
            if (root == null || camera == null)
                return;

            var jsonCandidates = new List<(string uri, float distance)>();
            var glbCandidates = new List<(string uri, float distance)>();
            var stack = new Stack<Tile3DNode>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var node = stack.Pop();
                bool hasGlb = TileContentUri.IsGlb(node.ContentUri);
                bool hasExpandableJson = TileContentUri.IsExpandableTileset(node.ContentUri);

                float distance = GetDistanceToNodeUnity(node, cameraWorldPos);

                if (!PassesRegionClip(node, hasGlb, hasExpandableJson, alreadyLoadedUris, distance))
                    continue;

                if (hasExpandableJson &&
                    (alreadyLoadedUris == null || !alreadyLoadedUris.Contains(node.ContentUri)))
                {
                    jsonCandidates.Add((node.ContentUri, distance));
                }

                if (hasGlb)
                {
                    bool shouldRefine = node.Children.Count > 0 &&
                        ComputeScreenSpaceError(node.GeometricError, distance, camera) >
                        _lod.maximumScreenSpaceError;

                    if (shouldRefine)
                    {
                        // SSE too high — descend to finer children instead of this coarse GLB.
                    }
                    else
                    {
                        if (ShouldLoadGlb(node, distance, camera))
                            glbCandidates.Add((node.ContentUri, distance));
                        continue;
                    }
                }

                if (node.Children.Count == 0)
                    continue;

                var childOrder = new List<Tile3DNode>(node.Children);
                childOrder.Sort((a, b) =>
                {
                    float da = GetDistanceToNodeUnity(a, cameraWorldPos);
                    float db = GetDistanceToNodeUnity(b, cameraWorldPos);
                    return da.CompareTo(db);
                });

                int start = Mathf.Max(0, childOrder.Count - _lod.loadingDescendantLimit);
                for (int i = childOrder.Count - 1; i >= start; i--)
                    stack.Push(childOrder[i]);
            }

            foreach (var entry in glbCandidates
                         .OrderBy(c => c.distance)
                         .Take(_lod.maxGlbTilesPerCollect))
            {
                desiredContentUris.Add(entry.uri);
            }

            foreach (var entry in jsonCandidates
                         .OrderBy(c => c.distance)
                         .Take(_lod.maxJsonExpansionPerCollect))
            {
                preloadJsonUris.Add(entry.uri);
            }
        }

        bool ShouldLoadGlb(Tile3DNode node, float horizontalDistanceM, Camera camera)
        {
            if (horizontalDistanceM > _lod.maxFocusRadiusM)
                return false;

            if (!_clipper.IntersectsRegion(node) &&
                !Tile3DNodeTransform.EnclosesWorldEcef(node, _regionAnchorEcef))
                return false;

            if (_lod.enableFrustumCulling && !IsVisible(node, camera))
                return false;

            return true;
        }

        bool PassesRegionClip(
            Tile3DNode node,
            bool hasGlb,
            bool hasExpandableJson,
            IReadOnlyCollection<string> alreadyLoadedUris,
            float horizontalDistanceM)
        {
            if (_clipper.IntersectsRegion(node))
                return true;

            if (hasGlb)
                return Tile3DNodeTransform.EnclosesWorldEcef(node, _regionAnchorEcef) ||
                       _clipper.IntersectsRegion(node);

            if (hasExpandableJson &&
                (alreadyLoadedUris == null || !alreadyLoadedUris.Contains(node.ContentUri)))
                return true;

            if (node.Depth < 2)
                return true;

            return Tile3DNodeTransform.EnclosesWorldEcef(node, _regionAnchorEcef);
        }

        float GetDistanceToNode(Tile3DNode node, Vector3 cameraPos)
        {
            if (node.Bounds == null)
                return 1000f;
            Vector3 center = _transform.EcefToUnity(Tile3DNodeTransform.GetWorldCenterEcef(node));
            return Vector3.Distance(cameraPos, center);
        }

        float GetDistanceToNodeUnity(Tile3DNode node, Vector3 cameraPos)
        {
            if (node.Bounds == null)
                return float.MaxValue;
            Vector3 center = _transform.EcefToUnity(Tile3DNodeTransform.GetWorldCenterEcef(node));
            return Vector3.Distance(new Vector3(cameraPos.x, 0f, cameraPos.z),
                new Vector3(center.x, 0f, center.z));
        }

        static double ComputeScreenSpaceError(double geometricError, float distance, Camera camera)
        {
            if (geometricError <= 0 || distance <= 0.1f)
                return 0;
            float vFov = camera.fieldOfView * Mathf.Deg2Rad;
            return geometricError * camera.pixelHeight / (distance * 2.0 * Mathf.Tan(vFov * 0.5f));
        }

        bool IsVisible(Tile3DNode node, Camera camera)
        {
            if (node.Bounds == null)
                return true;

            Vector3 center = _transform.EcefToUnity(Tile3DNodeTransform.GetWorldCenterEcef(node));
            float r = (float)Tile3DNodeTransform.GetWorldBoundingRadius(node);
            var bounds = new Bounds(center, Vector3.one * Mathf.Max(r * 2f, 50f));
            var planes = GeometryUtility.CalculateFrustumPlanes(camera);
            if (GeometryUtility.TestPlanesAABB(planes, bounds))
                return true;

            // Coarse ECEF boxes can be tighter than the mesh; accept if center projects inside the view.
            Vector3 view = camera.WorldToViewportPoint(center);
            return view.z > 0f && view.x >= -0.25f && view.x <= 1.25f && view.y >= -0.25f && view.y <= 1.25f;
        }
    }
}
