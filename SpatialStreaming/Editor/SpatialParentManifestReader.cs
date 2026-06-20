using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ZGConnect.SpatialStreaming.Editor
{
    public sealed class SpatialParentTileInfo
    {
        public string TileId;
        public Vector3 UnityPosition;
        public bool HasFacadeBuildings;
        public bool HasOrthoRoofBuildings;
    }

    public static class SpatialParentManifestReader
    {
        public static bool TryLoad(
            string manifestFullPath,
            out int tileSizeMeters,
            out Vector2Int unityOrigin,
            out List<SpatialParentTileInfo> tiles,
            out string error)
        {
            tileSizeMeters = 1000;
            unityOrigin = Vector2Int.zero;
            tiles = new List<SpatialParentTileInfo>();
            error = null;

            if (string.IsNullOrEmpty(manifestFullPath) || !File.Exists(manifestFullPath))
            {
                error = $"Parent manifest not found: {manifestFullPath}";
                return false;
            }

            JObject root = JObject.Parse(File.ReadAllText(manifestFullPath));
            tileSizeMeters = root.Value<int?>("tileSizeMeters") ?? 1000;

            if (root["unityOrigin"] is JArray origin && origin.Count >= 2)
            {
                unityOrigin = new Vector2Int(
                    origin[0].Value<int>(),
                    origin[1].Value<int>());
            }

            if (root["tiles"] is not JArray tileArray)
            {
                error = "Parent manifest has no tiles array.";
                return false;
            }

            foreach (JToken token in tileArray)
            {
                if (token is not JObject obj)
                    continue;

                string tileId = obj.Value<string>("tileId");
                if (string.IsNullOrEmpty(tileId))
                    continue;

                var info = new SpatialParentTileInfo
                {
                    TileId = tileId,
                    HasFacadeBuildings = obj.Value<bool?>("hasFacadeBuildings") ?? false,
                    HasOrthoRoofBuildings = obj.Value<bool?>("hasOrthoRoofBuildings") ?? false,
                };

                if (obj["unityPosition"] is JArray pos && pos.Count >= 3)
                {
                    info.UnityPosition = new Vector3(
                        pos[0].Value<float>(),
                        pos[1].Value<float>(),
                        pos[2].Value<float>());
                }

                tiles.Add(info);
            }

            return tiles.Count > 0;
        }
    }
}
