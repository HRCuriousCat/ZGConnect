using System.IO;
using UnityEngine;

namespace ZGConnect.RealtimeStreaming
{
    public static class RuntimeStreamingPaths
    {
        public const string ZGConnectFolderName = "ZGConnect";
        public const string DefaultManifestRelativePath = "ZGConnect/manifest.json";
        public const string PackedDatasetAssetRoot = "Assets/StreamingAssets/ZGConnect";

        public static string StreamingAssetsRoot =>
            Application.streamingAssetsPath;

        public static string DatasetRoot() =>
            Path.Combine(StreamingAssetsRoot, ZGConnectFolderName);

        public static string ManifestPath(string manifestRelativePath) =>
            Path.Combine(StreamingAssetsRoot, manifestRelativePath.Replace('/', Path.DirectorySeparatorChar));

        public static bool FileExists(string fullPath)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return true;
#else
            return File.Exists(fullPath);
#endif
        }
    }
}
