using System.IO;
using UnityEditor;
using UnityEngine;

namespace ZGConnect.Editor
{
    public static class ZGConnectPathUtils
    {
        /// <summary>
        /// Converts a Unity asset path (Assets/...) to a full OS file system path.
        /// </summary>
        public static string AssetPathToFullPath(string assetPath)
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
        }

        /// <summary>
        /// Ensures all folders in a Unity asset path exist, creating them if needed.
        /// folderPath must start with "Assets/".
        /// </summary>
        public static void EnsureAssetFolder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || !folderPath.StartsWith("Assets/"))
                return;

            if (!AssetDatabase.IsValidFolder(folderPath))
            {
            string[] parts = folderPath.Split('/');
            string current = parts[0];

            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
            }

            string fullPath = AssetPathToFullPath(folderPath);
            if (!Directory.Exists(fullPath))
                Directory.CreateDirectory(fullPath);
        }

        /// <summary>
        /// Builds a coord-keyed lookup from an ortho tile list for fast tile matching.
        /// Key format: "left_bottom"
        /// </summary>
        public static System.Collections.Generic.Dictionary<string, OrthoTileJson>
            BuildOrthoLookup(System.Collections.Generic.List<OrthoTileJson> tiles)
        {
            var lookup = new System.Collections.Generic.Dictionary<string, OrthoTileJson>();
            if (tiles == null) return lookup;
            foreach (var t in tiles)
                lookup[$"{t.Left}_{t.Bottom}"] = t;
            return lookup;
        }

        /// <summary>
        /// Builds a coord-keyed lookup from a tiled splatmap tile list.
        /// Key format: "left_bottom"
        /// </summary>
        public static System.Collections.Generic.Dictionary<string, SplatmapTileJson>
            BuildTiledLookup(System.Collections.Generic.List<SplatmapTileJson> tiles)
        {
            var lookup = new System.Collections.Generic.Dictionary<string, SplatmapTileJson>();
            if (tiles == null) return lookup;
            foreach (var t in tiles)
                lookup[$"{t.Left}_{t.Bottom}"] = t;
            return lookup;
        }

        /// <summary>
        /// Derives a short basemap identifier from a folder path by stripping the
        /// trailing resolution suffix (underscore + digits).
        /// Examples: "ortho_2048" → "ortho", "roadmap_1024" → "roadmap", "tron" → "tron".
        /// </summary>
        public static string DeriveBasemapId(string folderPath)
        {
            string name = Path.GetFileName(folderPath.TrimEnd('/', '\\'));
            int lastUnderscore = name.LastIndexOf('_');
            if (lastUnderscore > 0)
            {
                string suffix = name.Substring(lastUnderscore + 1);
                bool allDigits = suffix.Length > 0;
                foreach (char c in suffix)
                    if (!char.IsDigit(c)) { allDigits = false; break; }
                if (allDigits)
                    return name.Substring(0, lastUnderscore);
            }
            return name;
        }
    }
}
