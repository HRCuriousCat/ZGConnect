using UnityEngine;

namespace ZGConnect.PhotogrammetryStreaming
{
    public enum TileSourceMode
    {
        LiveGoogle = 0,
        LocalBaked = 1,
    }

    [CreateAssetMenu(fileName = "PhotogrammetryApiSettings", menuName = "ZG Connect/Photogrammetry/API Settings")]
    public class PhotogrammetryApiSettings : ScriptableObject
    {
        public const string DefaultRootUrl = "https://tile.googleapis.com/v1/3dtiles/root.json";

        [Tooltip("Google Cloud API key with Map Tiles API enabled. Do not commit real keys.")]
        public string apiKey = "";

        public string rootTilesetUrl = DefaultRootUrl;

        [Tooltip("Local baked tileset.json path (relative to StreamingAssets or absolute).")]
        public string localTilesetPath = "ZGConnect/photogrammetry/tileset.json";

        public TileSourceMode tileSourceMode = TileSourceMode.LiveGoogle;

        public string BuildRootRequestUrl()
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                return rootTilesetUrl;

            char sep = rootTilesetUrl.Contains("?") ? '&' : '?';
            return $"{rootTilesetUrl}{sep}key={apiKey}";
        }
    }
}
