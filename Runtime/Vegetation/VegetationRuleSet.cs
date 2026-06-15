using UnityEngine;

namespace ZGConnect
{
    [CreateAssetMenu(menuName = "ZG Connect/Vegetation/Rule Set")]
    public class VegetationRuleSet : ScriptableObject
    {
        public int globalSeed = 12345;
        public VegetationRule[] rules;
    }
}
