using System;
using UnityEngine;

namespace ZGConnect
{
    [Serializable]
    public struct VegetationSpawnAnimationSettings
    {
        public VegetationSpawnAnimationMode mode;

        [Tooltip("Chunk pop-in duration (seconds). Used when mode = ChunkPopIn.")]
        public float chunkPopInDuration;

        [Tooltip("Per-tree grow duration after its stagger (seconds). Used when mode = PerTree.")]
        public float perTreeGrowDuration;

        [Tooltip("Max random delay before a tree starts growing (seconds). Used when mode = PerTree.")]
        public float perTreeMaxStagger;

        public static VegetationSpawnAnimationSettings Default => new VegetationSpawnAnimationSettings
        {
            mode                 = VegetationSpawnAnimationMode.ChunkPopIn,
            chunkPopInDuration   = 0.35f,
            perTreeGrowDuration  = 1.2f,
            perTreeMaxStagger    = 0.8f
        };

        public bool IsActive => mode != VegetationSpawnAnimationMode.None;

        public float CompletionTime
        {
            get
            {
                switch (mode)
                {
                    case VegetationSpawnAnimationMode.ChunkPopIn:
                        return Mathf.Max(0.01f, chunkPopInDuration);
                    case VegetationSpawnAnimationMode.PerTree:
                        return Mathf.Max(0.01f, perTreeMaxStagger + perTreeGrowDuration);
                    default:
                        return 0f;
                }
            }
        }
    }
}

