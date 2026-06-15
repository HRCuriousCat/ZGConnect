using UnityEngine;

namespace ZGConnect
{
    public static class VegetationDistanceUtility
    {
        public static float HorizontalDistanceXZ(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
