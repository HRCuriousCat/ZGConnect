using UnityEngine;

namespace ZGConnect
{
    [System.Serializable]
    public struct VegetationInstance
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;
        public int prototypeIndex;
        public float random;
    }
}
