using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect
{
    /// <summary>
    /// Returns a material suitable for <see cref="Graphics.RenderMeshInstanced"/>.
    /// Clones source assets once when instancing is disabled (does not modify project materials).
    /// </summary>
    public static class VegetationInstancingMaterialCache
    {
        private static readonly Dictionary<EntityId, Material> CacheBySourceId = new Dictionary<EntityId, Material>();

        public static Material Get(Material source)
        {
            if (source == null) return null;
            if (source.enableInstancing) return source;

            EntityId key = source.GetEntityId();
            if (CacheBySourceId.TryGetValue(key, out Material cached) && cached != null)
                return cached;

            var instanced = new Material(source)
            {
                name              = source.name + " (ZGConnect Instanced)",
                enableInstancing  = true
            };

            CacheBySourceId[key] = instanced;
            return instanced;
        }

        public static void Clear()
        {
            foreach (Material mat in CacheBySourceId.Values)
            {
                if (mat != null)
                    Object.Destroy(mat);
            }
            CacheBySourceId.Clear();
        }
    }
}

