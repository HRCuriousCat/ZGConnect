using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// Hierarchical substitution graph: HLOD4 (4× HLOD2) → HLOD2 (4× tile) → tile (16× detail subcells or 1 monolithic leaf).
    /// Parents hide when every child is Ready and show every ready child (full footprint, no gaps).
    /// Want expands to all siblings when any child is wanted; parent returns only when no child is wanted.
    /// Rings drive direct want; this graph expands want to full parent footprints and owns Shown state.
    /// </summary>
    public sealed class SpatialLodSubstitutionGraph
    {
        public sealed class Node
        {
            public string NodeId;
            public string RecordKey;
            public SpatialStreamingLodLevel LodLevel;
            public Node Parent;
            public readonly List<Node> Children = new(4);

            public bool Wanted;
            public bool Loaded;
            public bool Ready;

            /// <summary>Visibility output — only true when substitution rules allow drawing.</summary>
            public bool Shown;

            public int ReadyChildCount;
        }

        readonly Dictionary<string, Node> _byNodeId = new(StringComparer.Ordinal);
        readonly Dictionary<string, Node> _byRecordKey = new(StringComparer.Ordinal);
        readonly List<Node> _roots = new(64);

        public IReadOnlyDictionary<string, Node> Nodes => _byNodeId;

        public static SpatialLodSubstitutionGraph Build(SpatialDatasetRuntimeIndex runtimeIndex, SpatialDatasetManifest manifest)
        {
            var graph = new SpatialLodSubstitutionGraph();
            if (runtimeIndex == null || manifest == null)
                return graph;

            int tileSize = manifest.TileSizeMeters > 0 ? manifest.TileSizeMeters : 1000;
            var seenHlod4 = new HashSet<long>();

            foreach (KeyValuePair<long, SpatialSupertileManifestEntry> kvp in runtimeIndex.EnumerateHlod4Blocks())
            {
                SpatialSupertileManifestEntry hlod4 = kvp.Value;
                if (hlod4 == null)
                    continue;

                long packed = kvp.Key;
                if (!seenHlod4.Add(packed))
                    continue;

                graph.BuildHlod4Subtree(hlod4, runtimeIndex, manifest, tileSize);
            }

            foreach (KeyValuePair<long, SpatialSupertileManifestEntry> kvp in runtimeIndex.EnumerateHlod2Blocks())
            {
                SpatialSupertileManifestEntry hlod2 = kvp.Value;
                if (hlod2 == null || graph._byRecordKey.ContainsKey(SpatialStreamingHlodEvaluator.BuildSupertileKey(hlod2.SupertileId)))
                    continue;

                graph.BuildOrphanHlod2Subtree(hlod2, runtimeIndex, manifest, tileSize);
            }

            return graph;
        }

        void BuildHlod4Subtree(
            SpatialSupertileManifestEntry hlod4,
            SpatialDatasetRuntimeIndex runtimeIndex,
            SpatialDatasetManifest manifest,
            int tileSize)
        {
            string hlod4Key = SpatialStreamingHlodEvaluator.BuildSupertileKey(hlod4.SupertileId);
            Node h4 = GetOrCreateNode(
                NodeIdHlod4(hlod4.Left, hlod4.Bottom),
                hlod4Key,
                SpatialStreamingLodLevel.Hlod4x4,
                null);
            if (!_roots.Contains(h4))
                _roots.Add(h4);

            int block2Size = tileSize * 2;
            for (int dy = 0; dy < 2; dy++)
            {
                for (int dx = 0; dx < 2; dx++)
                {
                    int childLeft = hlod4.Left + dx * block2Size;
                    int childBottom = hlod4.Bottom + dy * block2Size;
                    if (!runtimeIndex.TryGetSupertileAtBlock(childLeft, childBottom, 2, out SpatialSupertileManifestEntry hlod2) ||
                        hlod2 == null)
                    {
                        continue;
                    }

                    Node h2 = BuildHlod2Node(hlod2, h4, runtimeIndex, manifest, tileSize);
                    if (h2 != null && !h4.Children.Contains(h2))
                        h4.Children.Add(h2);
                }
            }
        }

        void BuildOrphanHlod2Subtree(
            SpatialSupertileManifestEntry hlod2,
            SpatialDatasetRuntimeIndex runtimeIndex,
            SpatialDatasetManifest manifest,
            int tileSize)
        {
            Node h2 = BuildHlod2Node(hlod2, null, runtimeIndex, manifest, tileSize);
            if (h2 != null && !_roots.Contains(h2))
                _roots.Add(h2);
        }

        Node BuildHlod2Node(
            SpatialSupertileManifestEntry hlod2,
            Node parentHlod4,
            SpatialDatasetRuntimeIndex runtimeIndex,
            SpatialDatasetManifest manifest,
            int tileSize)
        {
            string hlod2Key = SpatialStreamingHlodEvaluator.BuildSupertileKey(hlod2.SupertileId);
            if (_byRecordKey.TryGetValue(hlod2Key, out Node existing))
                return existing;

            Node h2 = GetOrCreateNode(
                NodeIdHlod2(hlod2.Left, hlod2.Bottom),
                hlod2Key,
                SpatialStreamingLodLevel.Hlod2x2,
                parentHlod4);

            for (int dy = 0; dy < 2; dy++)
            {
                for (int dx = 0; dx < 2; dx++)
                {
                    int tileLeft = hlod2.Left + dx * tileSize;
                    int tileBottom = hlod2.Bottom + dy * tileSize;
                    string tileId = SpatialTileIdUtility.Format(tileLeft, tileBottom);
                    if (!runtimeIndex.TryGetTile(tileId, out SpatialTileManifestEntry tile) || tile == null)
                        continue;

                    Node tileNode = BuildTileNode(tile, h2, runtimeIndex);
                    if (tileNode != null && !h2.Children.Contains(tileNode))
                        h2.Children.Add(tileNode);
                }
            }

            return h2;
        }

        Node BuildTileNode(
            SpatialTileManifestEntry tile,
            Node parentHlod2,
            SpatialDatasetRuntimeIndex runtimeIndex)
        {
            if (_byNodeId.TryGetValue(NodeIdTile(tile.TileId), out Node existing))
                return existing;

            string tileProxyKey = SpatialStreamingHlodEvaluator.BuildProxyKey(tile.TileId);
            Node tileNode = GetOrCreateNode(
                NodeIdTile(tile.TileId),
                tileProxyKey,
                SpatialStreamingLodLevel.TileProxy,
                parentHlod2);

            if (!tile.UsesSubcells || tile.Subcells == null || tile.Subcells.Count == 0)
            {
                string detailKey = SpatialStreamingHlodEvaluator.BuildDetailKey(tile.TileId, "tile_coarse");
                if (!string.IsNullOrEmpty(tile.CoarseBundleRel))
                {
                    Node leaf = GetOrCreateNode(
                        NodeIdMonolithic(tile.TileId),
                        detailKey,
                        SpatialStreamingLodLevel.Detail,
                        tileNode);
                    tileNode.Children.Add(leaf);
                }

                return tileNode;
            }

            foreach (SpatialSubcellManifestEntry subcell in tile.Subcells)
            {
                if (subcell == null || string.IsNullOrEmpty(subcell.SubcellId) || string.IsNullOrEmpty(subcell.BundleRel))
                    continue;

                string detailKey = SpatialStreamingHlodEvaluator.BuildDetailKey(tile.TileId, subcell.SubcellId);
                if (_byRecordKey.ContainsKey(detailKey))
                    continue;

                Node leaf = GetOrCreateNode(
                    NodeIdSubcell(tile.TileId, subcell.SubcellId),
                    detailKey,
                    SpatialStreamingLodLevel.Detail,
                    tileNode);
                tileNode.Children.Add(leaf);
            }

            return tileNode;
        }

        Node GetOrCreateNode(string nodeId, string recordKey, SpatialStreamingLodLevel level, Node parent)
        {
            if (_byNodeId.TryGetValue(nodeId, out Node node))
                return node;

            node = new Node
            {
                NodeId = nodeId,
                RecordKey = recordKey,
                LodLevel = level,
                Parent = parent,
            };
            _byNodeId[nodeId] = node;
            if (!string.IsNullOrEmpty(recordKey))
                _byRecordKey[recordKey] = node;

            return node;
        }

        /// <summary>Apply ring-direct want keys, expand to full footprints, sync loaded/ready from residency.</summary>
        public void SyncFromWantAndLoaded(
            HashSet<string> directWant,
            IReadOnlyDictionary<string, SpatialLoadedSubcellRecord> loaded)
        {
            foreach (Node node in _byNodeId.Values)
                node.Wanted = false;

            if (directWant != null)
            {
                foreach (string key in directWant)
                {
                    if (_byRecordKey.TryGetValue(key, out Node node))
                        MarkWantedWithFootprintExpansion(node);
                }
            }

            foreach (Node node in _byNodeId.Values)
            {
                node.Loaded = false;
                node.Ready = false;
            }

            if (loaded != null)
            {
                foreach (KeyValuePair<string, SpatialLoadedSubcellRecord> kvp in loaded)
                {
                    if (kvp.Value?.Root == null)
                        continue;

                    if (!_byRecordKey.TryGetValue(kvp.Key, out Node node))
                        continue;

                    node.Loaded = true;
                    node.Ready = true;
                }
            }

            RecomputeReadyCounts();
            RefreshVisibility();
        }

        void MarkWantedWithFootprintExpansion(Node node)
        {
            if (node == null)
                return;

            node.Wanted = true;
            ExpandSiblingWant(node);

            for (Node parent = node.Parent; parent != null; parent = parent.Parent)
            {
                parent.Wanted = true;
                ExpandSiblingWant(parent);
            }
        }

        static void ExpandSiblingWant(Node node)
        {
            if (node?.Parent == null)
                return;

            foreach (Node sibling in node.Parent.Children)
                sibling.Wanted = true;
        }

        void RecomputeReadyCounts()
        {
            foreach (Node node in _byNodeId.Values)
                node.ReadyChildCount = 0;

            foreach (Node node in _byNodeId.Values)
            {
                if (node.Parent == null || !node.Ready)
                    continue;

                node.Parent.ReadyChildCount++;
            }
        }

        void RefreshVisibility()
        {
            foreach (Node root in _roots)
                RefreshNodeVisibility(root);
        }

        static void RefreshNodeVisibility(Node node)
        {
            if (node == null)
                return;

            foreach (Node child in node.Children)
                RefreshNodeVisibility(child);

            bool anyChildWanted = false;
            foreach (Node child in node.Children)
            {
                if (child.Wanted)
                {
                    anyChildWanted = true;
                    break;
                }
            }

            if (node.Children.Count == 0)
            {
                node.Shown = node.Wanted && node.Ready;
                return;
            }

            if (!anyChildWanted)
            {
                foreach (Node child in node.Children)
                    child.Shown = false;

                node.Shown = node.Wanted && node.Loaded && node.Ready;
                return;
            }

            bool allChildrenReady = node.Children.Count > 0 && node.ReadyChildCount >= node.Children.Count;
            if (allChildrenReady)
            {
                node.Shown = false;
                foreach (Node child in node.Children)
                    child.Shown = child.Ready;
            }
            else
            {
                node.Shown = node.Wanted && node.Loaded && node.Ready;
                foreach (Node child in node.Children)
                    child.Shown = false;
            }
        }

        public bool ShouldShowRecord(string recordKey)
        {
            if (string.IsNullOrEmpty(recordKey))
                return false;

            return _byRecordKey.TryGetValue(recordKey, out Node node) && node.Shown;
        }

        public bool TryGetNodeForRecord(string recordKey, out Node node) =>
            _byRecordKey.TryGetValue(recordKey, out node);

        public void MergeExpandedWantInto(HashSet<string> want)
        {
            if (want == null)
                return;

            foreach (Node node in _byNodeId.Values)
            {
                if (node.Wanted && !string.IsNullOrEmpty(node.RecordKey))
                    want.Add(node.RecordKey);
            }
        }

        static string NodeIdHlod4(int left, int bottom) => $"h4:{left}:{bottom}";
        static string NodeIdHlod2(int left, int bottom) => $"h2:{left}:{bottom}";
        static string NodeIdTile(string tileId) => $"t:{tileId}";
        static string NodeIdSubcell(string tileId, string subcellId) => $"s:{tileId}|{subcellId}";
        static string NodeIdMonolithic(string tileId) => $"m:{tileId}";
    }
}
