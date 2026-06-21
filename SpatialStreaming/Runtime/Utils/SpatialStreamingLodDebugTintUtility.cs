using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// Applies per-LOD debug tints via <see cref="MaterialPropertyBlock"/> on streamed mesh roots.
    /// </summary>
    public static class SpatialStreamingLodDebugTintUtility
    {
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly MaterialPropertyBlock SharedBlock = new();

        static readonly Color DetailTint = new(0.35f, 0.95f, 0.45f, 1f);
        static readonly Color SubcellProxyTint = new(0.35f, 0.85f, 1f, 1f);
        static readonly Color TileProxyTint = new(0.95f, 0.9f, 0.25f, 1f);
        static readonly Color Hlod2Tint = new(1f, 0.55f, 0.15f, 1f);
        static readonly Color Hlod4Tint = new(1f, 0.25f, 0.35f, 1f);

        public static Color GetTint(SpatialStreamingLodLevel lodLevel) => lodLevel switch
        {
            SpatialStreamingLodLevel.Detail => DetailTint,
            SpatialStreamingLodLevel.SubcellProxy => SubcellProxyTint,
            SpatialStreamingLodLevel.TileProxy => TileProxyTint,
            SpatialStreamingLodLevel.Hlod2x2 => Hlod2Tint,
            SpatialStreamingLodLevel.Hlod4x4 => Hlod4Tint,
            _ => Color.white,
        };

        public static void Apply(GameObject root, SpatialStreamingLodLevel lodLevel)
        {
            if (root == null)
                return;

            Color tint = GetTint(lodLevel);
            SharedBlock.Clear();
            SharedBlock.SetColor(BaseColorId, tint);
            SharedBlock.SetColor(ColorId, tint);

            MeshRenderer[] renderers = root.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer renderer = renderers[i];
                if (renderer != null)
                    renderer.SetPropertyBlock(SharedBlock);
            }
        }

        public static void Clear(GameObject root)
        {
            if (root == null)
                return;

            MeshRenderer[] renderers = root.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer renderer = renderers[i];
                if (renderer != null)
                    renderer.SetPropertyBlock(null);
            }
        }
    }
}
