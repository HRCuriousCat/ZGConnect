using UnityEngine;

namespace ZGConnect
{
    public struct VegetationCullContext
    {
        public Vector3 cameraPosition;
        public bool    hasCamera;
        public VegetationDrawDistanceSettings drawDistance;
        public Plane[] frustumPlanes;

        public bool UsesPerInstanceDistance => hasCamera && drawDistance.IsEnabled;
        public bool UsesFrustum             => frustumPlanes != null && frustumPlanes.Length > 0;

        public bool RequiresInstanceLoop =>
            UsesPerInstanceDistance || UsesFrustum;
    }
}
