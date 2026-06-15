using UnityEngine;

namespace ZGConnect
{
    [CreateAssetMenu(menuName = "ZG Connect/Vegetation/Prototype")]
    public class VegetationPrototype : ScriptableObject
    {
        public string prototypeId;

        public Mesh mesh;
        public Material[] materials;

        public Vector2 minMaxScale = new Vector2(0.8f, 1.2f);
        public Vector2 yRotationRange = new Vector2(0f, 360f);

        public float minDistanceBetweenInstances = 2f;

        public bool alignToTerrainNormal = false;
        public float maxSlope = 35f;

        public Bounds localBounds = new Bounds(Vector3.zero, Vector3.one * 10f);
    }
}
