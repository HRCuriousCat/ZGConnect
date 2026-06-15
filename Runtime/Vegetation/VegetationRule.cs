using UnityEngine;

namespace ZGConnect
{
    [CreateAssetMenu(menuName = "ZG Connect/Vegetation/Rule")]
    public class VegetationRule : ScriptableObject
    {
        public string ruleId;

        [Header("Source Mask")]
        [Tooltip("Which RGBA channel of the vegetation mask to sample: forest (R), park (G), grass (B)")]
        public string sourceMaskChannel = "forest";

        [Range(0f, 1f)]
        [Tooltip("Minimum mask value required before placing any vegetation")]
        public float densityThreshold = 0.3f;

        [Range(0f, 1f)]
        [Tooltip("Multiplier applied to the mask value for the final density roll")]
        public float density = 1f;

        [Header("Placement")]
        public float spacingMeters = 5f;
        public float jitterMeters = 2f;

        [Header("Exclusion")]
        [Range(0f, 1f)]
        [Tooltip("Mask A channel value above which this position is excluded (roads, buildings, water)")]
        public float exclusionThreshold = 0.5f;

        [Header("Species")]
        public VegetationSpeciesSet speciesSet;
    }
}
