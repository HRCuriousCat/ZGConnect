using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect
{
    public class VegetationChunk
    {
        public string tileId;
        public Bounds worldBounds;
        public readonly List<VegetationBatch> batches = new List<VegetationBatch>();

        public int TotalInstances
        {
            get
            {
                int count = 0;
                foreach (var b in batches)
                    count += b.instances.Count;
                return count;
            }
        }
    }
}
