using UnityEngine;

namespace ZGConnect.Roads
{
    [CreateAssetMenu(fileName = "RoadStyleProfile", menuName = "ZG Connect/Roads/Style Profile")]
    public sealed class RoadStyleProfile : ScriptableObject
    {
        public Material asphaltMaterial;
        public Material sidewalkMaterial;
        public Material curbMaterial;
        public Material markingMaterial;
        public float sidewalkHeightOffset = 0.08f;
        public float curbHeight = 0.12f;
        public float markingHeightOffset = 0.025f;
    }
}
