using UnityEngine;

namespace ZGConnect
{
    [CreateAssetMenu(menuName = "ZG Connect/Vegetation/Species Set")]
    public class VegetationSpeciesSet : ScriptableObject
    {
        public VegetationPrototype[] prototypes;
        public float[] weights;

        public VegetationPrototype PickPrototype(float t)
        {
            if (prototypes == null || prototypes.Length == 0)
                return null;

            if (weights == null || weights.Length != prototypes.Length)
                return prototypes[Mathf.FloorToInt(t * prototypes.Length) % prototypes.Length];

            float total = 0f;
            for (int i = 0; i < weights.Length; i++)
                total += weights[i];

            if (total <= 0f)
                return prototypes[0];

            float pick = t * total;
            float acc = 0f;
            for (int i = 0; i < prototypes.Length; i++)
            {
                acc += weights[i];
                if (pick <= acc)
                    return prototypes[i];
            }

            return prototypes[prototypes.Length - 1];
        }
    }
}
