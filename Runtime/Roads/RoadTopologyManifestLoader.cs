using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace ZGConnect.Roads
{
    public sealed class RoadTopologyManifestLoader
    {
        readonly string _rootPath;
        readonly Dictionary<string, RoadTopologyManifestTile> _tilesById = new();
        readonly Dictionary<Vector2Int, RoadTopologyManifestTile> _tilesByGrid = new();

        public RoadTopologyManifest Manifest { get; private set; }

        public RoadTopologyManifestLoader(string manifestPath)
        {
            _rootPath = Path.GetDirectoryName(manifestPath);
            Manifest = JsonConvert.DeserializeObject<RoadTopologyManifest>(File.ReadAllText(manifestPath));
            BuildIndex();
        }

        void BuildIndex()
        {
            _tilesById.Clear();
            _tilesByGrid.Clear();
            if (Manifest?.Tiles == null)
                return;

            int tileSize = Manifest.TileSizeMeters > 0 ? Manifest.TileSizeMeters : 1000;
            foreach (RoadTopologyManifestTile tile in Manifest.Tiles)
            {
                if (tile == null || string.IsNullOrEmpty(tile.TileId))
                    continue;

                Vector3 tileOrigin = tile.UnityOrigin;
                _tilesById[tile.TileId] = tile;
                _tilesByGrid[new Vector2Int(FloorDiv(tileOrigin.x, tileSize), FloorDiv(tileOrigin.z, tileSize))] = tile;
            }
        }

        public bool TryGetTile(string tileId, out RoadTopologyManifestTile tile) =>
            _tilesById.TryGetValue(tileId, out tile);

        public bool TryGetTileAtGrid(int gridX, int gridZ, out RoadTopologyManifestTile tile) =>
            _tilesByGrid.TryGetValue(new Vector2Int(gridX, gridZ), out tile);

        public bool TryResolveCameraTile(Vector3 worldPosition, out RoadTopologyManifestTile tile)
        {
            tile = null;
            int tileSize = Manifest?.TileSizeMeters > 0 ? Manifest.TileSizeMeters : 1000;
            int gridX = FloorDiv(worldPosition.x, tileSize);
            int gridZ = FloorDiv(worldPosition.z, tileSize);
            return TryGetTileAtGrid(gridX, gridZ, out tile);
        }

        public RoadTopologyTile LoadTile(RoadTopologyManifestTile tile)
        {
            if (tile == null || string.IsNullOrEmpty(tile.TopologyPath))
                return null;

            string path = Path.Combine(_rootPath, tile.TopologyPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                return null;

            return JsonConvert.DeserializeObject<RoadTopologyTile>(File.ReadAllText(path));
        }

        static int FloorDiv(float value, int divisor) =>
            Mathf.FloorToInt(value / divisor);
    }
}
