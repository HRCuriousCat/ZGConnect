using UnityEngine;

namespace ZGConnect.RealtimeStreaming
{
    /// <summary>Marks the physics-only building tile prefab baked alongside the visual prefab.</summary>
    public sealed class RuntimeBuildingTilePhysicsMarker : MonoBehaviour
    {
        public string PackBakeFingerprint;
        public string BuildingStyleKey;
    }
}
