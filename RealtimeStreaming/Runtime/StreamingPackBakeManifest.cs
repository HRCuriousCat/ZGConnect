using System;
using Newtonsoft.Json;

namespace ZGConnect.RealtimeStreaming
{
    [Serializable]
    public class StreamingPackBakeManifest
    {
        [JsonProperty("fingerprint")] public string Fingerprint;
        [JsonProperty("colliderMode")] public string ColliderMode = "ConvexMesh";
        [JsonProperty("combineTileMeshes")] public bool CombineTileMeshes = true;
        [JsonProperty("combineMeshesPerMaterial")] public bool CombineMeshesPerMaterial = true;
        [JsonProperty("orthoBasemapId")] public string OrthoBasemapId = "ortho";
        [JsonProperty("buildingSurfaceSettingsGuid")] public string BuildingSurfaceSettingsGuid;
        [JsonProperty("vegetationRuleSetGuid")] public string VegetationRuleSetGuid;

        public RealtimeBuildingColliderMode GetColliderMode()
        {
            if (Enum.TryParse(ColliderMode, true, out RealtimeBuildingColliderMode mode))
                return mode;
            return RealtimeBuildingColliderMode.ConvexMesh;
        }

        public RuntimeBuildingTilePostProcessSettings ToPostProcessSettings() =>
            new RuntimeBuildingTilePostProcessSettings(
                GetColliderMode(),
                CombineTileMeshes,
                CombineMeshesPerMaterial);
    }

    public static class StreamingPackBakeKeys
    {
        public const string Facade = "facade";
        public const string OrthoRoof = "ortho_roof";
    }
}
