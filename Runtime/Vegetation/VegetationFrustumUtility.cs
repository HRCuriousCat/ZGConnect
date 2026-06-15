using UnityEngine;

namespace ZGConnect
{
    public static class VegetationFrustumUtility
    {
        static int     _frame = -1;
        static Camera  _camera;
        static Plane[] _planes;

        public static Plane[] GetFrustumPlanes(Camera camera)
        {
            if (camera == null)
                return null;

            if (_frame != Time.frameCount || _camera != camera)
            {
                _planes  = GeometryUtility.CalculateFrustumPlanes(camera);
                _frame   = Time.frameCount;
                _camera  = camera;
            }

            return _planes;
        }

        public static bool TestBounds(Camera camera, Bounds bounds) =>
            TestBounds(GetFrustumPlanes(camera), bounds);

        public static bool TestBounds(Plane[] planes, Bounds bounds)
        {
            if (planes == null || planes.Length == 0)
                return true;

            return GeometryUtility.TestPlanesAABB(planes, bounds);
        }

        public static bool TestInstance(Vector3 position, Vector3 scale, Plane[] planes)
        {
            if (planes == null || planes.Length == 0)
                return true;

            float radius = Mathf.Max(Mathf.Max(scale.x, scale.y), scale.z) * 1.25f;
            var bounds = new Bounds(position + Vector3.up * (scale.y * 0.5f), Vector3.one * (radius * 2f));
            return GeometryUtility.TestPlanesAABB(planes, bounds);
        }
    }
}

