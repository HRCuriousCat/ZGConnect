using System.Text;
using UnityEditor;
using ZGConnect;

namespace ZGConnect.RealtimeStreaming.Editor
{
    public static class RealtimePackBakeFingerprint
    {
        public static string Compute(RealtimePackOptions options)
        {
            var sb = new StringBuilder(256);
            sb.Append("v1|");
            sb.Append(options.PackColliderMode).Append('|');
            sb.Append(options.PackCombineBuildingMeshes).Append('|');
            sb.Append(options.PackCombineMeshesPerMaterial).Append('|');
            sb.Append(options.PackOrthoBasemapId ?? "ortho").Append('|');
            sb.Append(AssetGuid(options.BuildingSurfaceSettings)).Append('|');
            sb.Append(AssetGuid(options.VegetationRuleSet)).Append('|');
            sb.Append(options.PackBuildingLod).Append('|');
            sb.Append(options.OrthoMaxResolution).Append('|');
            sb.Append(options.OrthoCrunchCompression).Append('|');
            sb.Append(options.OrthoCrunchQuality).Append('|');
            sb.Append(options.PackUncompressedAssetBundles);
            return sb.ToString();
        }

        public static StreamingPackBakeManifest CreateManifest(RealtimePackOptions options) =>
            new StreamingPackBakeManifest
            {
                Fingerprint = Compute(options),
                ColliderMode = options.PackColliderMode.ToString(),
                CombineTileMeshes = options.PackCombineBuildingMeshes,
                CombineMeshesPerMaterial = options.PackCombineMeshesPerMaterial,
                OrthoBasemapId = options.PackOrthoBasemapId ?? "ortho",
                BuildingSurfaceSettingsGuid = AssetGuid(options.BuildingSurfaceSettings),
                VegetationRuleSetGuid = AssetGuid(options.VegetationRuleSet),
            };

        static string AssetGuid(UnityEngine.Object asset)
        {
            if (asset == null)
                return "none";

            string path = AssetDatabase.GetAssetPath(asset);
            return string.IsNullOrEmpty(path) ? asset.name : AssetDatabase.AssetPathToGUID(path);
        }
    }
}
