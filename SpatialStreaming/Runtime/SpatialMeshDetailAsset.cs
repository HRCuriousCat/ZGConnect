using System;
using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// Lightweight detail payload for Phase B spatial streaming.
    /// The bundle loads this descriptor plus referenced mesh/material assets, then runtime creates
    /// a minimal renderer hierarchy instead of deserializing and instantiating a prefab graph.
    /// </summary>
    public sealed class SpatialMeshDetailAsset : ScriptableObject
    {
        public string TileId;
        public string SubcellId;
        public RendererEntry[] Renderers = Array.Empty<RendererEntry>();

        [Serializable]
        public sealed class RendererEntry
        {
            public string Name;
            public Mesh Mesh;
            public Material[] Materials = Array.Empty<Material>();
        }
    }
}
