using System;

namespace ZGConnect.PhotogrammetryStreaming
{
    public static class TileContentUri
    {
        public static string PathWithoutQuery(string uri)
        {
            if (string.IsNullOrEmpty(uri))
                return uri;
            int q = uri.IndexOf('?', StringComparison.Ordinal);
            return q >= 0 ? uri.Substring(0, q) : uri;
        }

        public static bool IsJson(string uri)
        {
            string path = PathWithoutQuery(uri);
            return !string.IsNullOrEmpty(path) &&
                   path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsGlb(string uri)
        {
            string path = PathWithoutQuery(uri);
            return !string.IsNullOrEmpty(path) &&
                   path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsExpandableTileset(string uri)
        {
            if (string.IsNullOrEmpty(uri) || IsGlb(uri))
                return false;

            string path = PathWithoutQuery(uri);
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                return true;

            return path.IndexOf("/v1/3dtiles/", StringComparison.OrdinalIgnoreCase) >= 0
                   || path.IndexOf("/3dtiles/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsTileContent(string uri) => IsExpandableTileset(uri) || IsGlb(uri);
    }
}
