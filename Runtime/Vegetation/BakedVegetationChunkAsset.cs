using System;
using UnityEngine;

namespace ZGConnect
{
    [Serializable]
    public struct BakedVegetationInstanceData
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;
        public float random;
    }

    [Serializable]
    public struct BakedVegetationBatchData
    {
        public int prototypeIndex;
        public BakedVegetationInstanceData[] instances;
    }

    [CreateAssetMenu(fileName = "BakedVegetationChunk", menuName = "ZGConnect/Baked Vegetation Chunk")]
    public sealed class BakedVegetationChunkAsset : ScriptableObject
    {
        public string tileId;
        public Bounds worldBounds;
        public BakedVegetationBatchData[] batches = Array.Empty<BakedVegetationBatchData>();

        public VegetationChunk ToRuntimeChunk(VegetationPrototype[] prototypes)
        {
            var chunk = new VegetationChunk
            {
                tileId = tileId,
                worldBounds = worldBounds,
            };

            if (batches == null)
                return chunk;

            foreach (BakedVegetationBatchData batchData in batches)
            {
                if (batchData.instances == null || batchData.instances.Length == 0)
                    continue;

                var batch = new VegetationBatch
                {
                    prototypeIndex = batchData.prototypeIndex,
                    prototype = batchData.prototypeIndex >= 0 && prototypes != null &&
                                batchData.prototypeIndex < prototypes.Length
                        ? prototypes[batchData.prototypeIndex]
                        : null,
                };

                foreach (BakedVegetationInstanceData inst in batchData.instances)
                {
                    batch.instances.Add(new VegetationInstance
                    {
                        position = inst.position,
                        rotation = inst.rotation,
                        scale = inst.scale,
                        random = inst.random,
                    });
                }

                batch.BuildMatrices();
                chunk.batches.Add(batch);
            }

            return chunk;
        }

        public static BakedVegetationChunkAsset FromRuntimeChunk(VegetationChunk chunk)
        {
            var asset = CreateInstance<BakedVegetationChunkAsset>();
            asset.tileId = chunk?.tileId;
            asset.worldBounds = chunk?.worldBounds ?? default;

            if (chunk?.batches == null || chunk.batches.Count == 0)
            {
                asset.batches = Array.Empty<BakedVegetationBatchData>();
                return asset;
            }

            asset.batches = new BakedVegetationBatchData[chunk.batches.Count];
            for (int i = 0; i < chunk.batches.Count; i++)
            {
                VegetationBatch batch = chunk.batches[i];
                var instances = new BakedVegetationInstanceData[batch.instances.Count];
                for (int j = 0; j < batch.instances.Count; j++)
                {
                    VegetationInstance inst = batch.instances[j];
                    instances[j] = new BakedVegetationInstanceData
                    {
                        position = inst.position,
                        rotation = inst.rotation,
                        scale = inst.scale,
                        random = inst.random,
                    };
                }

                asset.batches[i] = new BakedVegetationBatchData
                {
                    prototypeIndex = batch.prototypeIndex,
                    instances = instances,
                };
            }

            return asset;
        }
    }
}
