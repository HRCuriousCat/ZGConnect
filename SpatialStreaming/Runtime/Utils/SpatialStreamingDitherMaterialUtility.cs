using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// Applies dither fade via MaterialPropertyBlock for streamed building surfaces.
    /// </summary>
    public static class SpatialStreamingDitherMaterialUtility
    {
        public const string DitherFadeProperty = "_DitherFade";
        public const string DitherFadeOutProperty = "_DitherFadeOut";
        public static readonly int DitherFadeId = Shader.PropertyToID(DitherFadeProperty);
        public static readonly int DitherFadeOutId = Shader.PropertyToID(DitherFadeOutProperty);

        static readonly MaterialPropertyBlock SharedBlock = new();

        public static void ApplyDitherFadeIn(GameObject root, float fade)
        {
            ApplyDither(root, Mathf.Clamp01(fade), fadeOut: false);
        }

        public static void ApplyDitherFadeOut(GameObject root, float visibleAmount)
        {
            ApplyDither(root, Mathf.Clamp01(visibleAmount), fadeOut: true);
        }

        static void ApplyDither(GameObject root, float fade, bool fadeOut)
        {
            if (root == null)
                return;

            SharedBlock.Clear();
            SharedBlock.SetFloat(DitherFadeId, fade);
            SharedBlock.SetFloat(DitherFadeOutId, fadeOut ? 1f : 0f);

            MeshRenderer[] renderers = root.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer renderer = renderers[i];
                if (renderer == null || !SupportsDither(renderer))
                    continue;

                renderer.SetPropertyBlock(SharedBlock);
            }
        }

        public static void ClearDitherFade(GameObject root)
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

        static bool SupportsDither(MeshRenderer renderer)
        {
            Material material = renderer.sharedMaterial;
            if (material == null || material.shader == null)
                return false;

            return material.shader.name.Contains("BuildingSurfaceDither");
        }
    }
}
