using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// Material helpers so mesh combine can merge geometry instead of treating every glTF import as unique.
    /// </summary>
    public static class SpatialMaterialCombineUtility
    {
        public static int NormalizeForCombine(
            IEnumerable<MeshRenderer> renderers,
            Material combineMaterialOverride,
            bool deduplicateByShader)
        {
            if (renderers == null)
                return 0;

            if (combineMaterialOverride != null)
                return AssignSharedMaterial(renderers, combineMaterialOverride);

            if (!deduplicateByShader)
                return CountUniqueMaterials(renderers);

            return DeduplicateByShader(renderers);
        }

        public static int AssignSharedMaterial(IEnumerable<MeshRenderer> renderers, Material material)
        {
            if (material == null || renderers == null)
                return 0;

            int changed = 0;
            foreach (MeshRenderer renderer in renderers)
            {
                if (renderer == null)
                    continue;

                Material[] slots = renderer.sharedMaterials;
                bool slotChanged = false;
                for (int i = 0; i < slots.Length; i++)
                {
                    if (slots[i] == material)
                        continue;

                    slots[i] = material;
                    slotChanged = true;
                }

                if (slotChanged)
                {
                    renderer.sharedMaterials = slots;
                    changed++;
                }
            }

            return changed;
        }

        static int DeduplicateByShader(IEnumerable<MeshRenderer> renderers)
        {
            var canonicalByKey = new Dictionary<int, Material>();
            int changed = 0;

            foreach (MeshRenderer renderer in renderers)
            {
                if (renderer == null)
                    continue;

                Material[] slots = renderer.sharedMaterials;
                bool slotChanged = false;
                for (int i = 0; i < slots.Length; i++)
                {
                    Material material = slots[i];
                    if (material == null || material.shader == null)
                        continue;

                    int dedupKey = GetMaterialDedupKey(material);
                    if (!canonicalByKey.TryGetValue(dedupKey, out Material canonical))
                    {
                        canonical = material;
                        canonicalByKey.Add(dedupKey, canonical);
                    }

                    if (slots[i] == canonical)
                        continue;

                    slots[i] = canonical;
                    slotChanged = true;
                }

                if (slotChanged)
                {
                    renderer.sharedMaterials = slots;
                    changed++;
                }
            }

            return changed;
        }

        static int GetMaterialDedupKey(Material material)
        {
            string normalized = BuildingSurfaceUtility.NormalizeMaterialName(material.name);
            if (BuildingSurfaceUtility.TryResolveCategoryFromMaterialName(normalized, out _) &&
                BuildingSurfaceUtility.TryResolveSurfaceTypeFromMaterialName(normalized, out _))
            {
                // Keep facade vs roof (and variant picks) separate even when shaders match.
                return normalized.GetHashCode();
            }

            return material.shader.name.GetHashCode();
        }

        public static int CountUniqueMaterials(IEnumerable<MeshRenderer> renderers)
        {
            if (renderers == null)
                return 0;

            var unique = new HashSet<Material>();
            foreach (MeshRenderer renderer in renderers)
            {
                if (renderer == null)
                    continue;

                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material != null)
                        unique.Add(material);
                }
            }

            return unique.Count;
        }
    }
}
