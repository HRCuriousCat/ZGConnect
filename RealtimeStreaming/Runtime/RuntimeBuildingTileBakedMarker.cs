using UnityEngine;

namespace ZGConnect.RealtimeStreaming
{
    /// <summary>Marks a building tile prefab as fully baked during dataset pack.</summary>
    public sealed class RuntimeBuildingTileBakedMarker : MonoBehaviour
    {
        public string PackBakeFingerprint;
        public string BuildingStyleKey;
    }
}
