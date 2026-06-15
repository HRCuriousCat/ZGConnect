using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using ZGConnect.Editor;
using ZGConnect.RealtimeStreaming;

namespace ZGConnect.RealtimeStreaming.Editor
{
    public static class RealtimePackReuseUtility
    {
        public static StreamingDatasetManifest TryLoadExistingManifest(string outputRoot)
        {
            string manifestPath = Path.Combine(outputRoot, "manifest.json");
            if (!File.Exists(manifestPath))
                return null;

            try
            {
                return StreamingDatasetManifest.LoadFromFile(manifestPath);
            }
            catch
            {
                return null;
            }
        }

        public static Dictionary<string, StreamingTileEntry> IndexTiles(StreamingDatasetManifest manifest)
        {
            var byId = new Dictionary<string, StreamingTileEntry>();
            if (manifest?.Tiles == null)
                return byId;

            foreach (StreamingTileEntry tile in manifest.Tiles)
            {
                if (!string.IsNullOrEmpty(tile.TileId))
                    byId[tile.TileId] = tile;
            }

            return byId;
        }

        public static Dictionary<string, StreamingSupertileEntry> IndexSupertiles(StreamingDatasetManifest manifest)
        {
            var byId = new Dictionary<string, StreamingSupertileEntry>();
            if (manifest?.Supertiles == null)
                return byId;

            foreach (StreamingSupertileEntry st in manifest.Supertiles)
            {
                if (!string.IsNullOrEmpty(st.SupertileId))
                    byId[st.SupertileId] = st;
            }

            return byId;
        }

        public static bool CanReuseLeafTile(
            StreamingTileEntry entry,
            RealtimePackOptions options,
            string outputRoot)
        {
            if (entry == null)
                return false;

            if (options.PackTerrain)
            {
                if (string.IsNullOrEmpty(entry.HeightmapPath) ||
                    !DatasetFileExists(outputRoot, entry.HeightmapPath))
                    return false;
            }

            if (options.PackTerrain && options.PackTerrainBundles &&
                !HasAllOrthoTerrainBundles(entry.TerrainBundlePaths, options, outputRoot))
                return false;

            if (options.PackOrtho)
            {
                foreach (DatasetFolderScanner.BasemapSource bm in options.Basemaps)
                {
                    if (bm.Type != BasemapType.Ortho)
                        continue;

                    string basemapId = ZGConnectPathUtils.DeriveBasemapId(bm.FolderPath);
                    if (entry.OrthoPaths == null ||
                        !entry.OrthoPaths.TryGetValue(basemapId, out string orthoRel) ||
                        !DatasetFileExists(outputRoot, orthoRel))
                        return false;
                }
            }

            if (options.PackTiled)
            {
                foreach (DatasetFolderScanner.BasemapSource bm in options.Basemaps)
                {
                    if (bm.Type != BasemapType.Tiled)
                        continue;

                    string basemapId = ZGConnectPathUtils.DeriveBasemapId(bm.FolderPath);
                    if (entry.TiledSplatPaths == null ||
                        !entry.TiledSplatPaths.TryGetValue(basemapId, out List<string> rels) ||
                        rels == null ||
                        rels.Count == 0 ||
                        rels.Any(rel => !DatasetFileExists(outputRoot, rel)))
                        return false;
                }
            }

            if (options.PackFacadeBuildings && entry.HasFacadeBuildings &&
                !DatasetFileExists(outputRoot, $"building_meshes/buildings_{entry.TileId}.glb"))
                return false;

            if (options.PackOrthoRoofBuildings && entry.HasOrthoRoofBuildings &&
                !DatasetFileExists(outputRoot, $"building_meshes_ortho/buildings_{entry.TileId}.glb"))
                return false;

            if (options.PackVegetation && entry.HasVegetationMask &&
                !DatasetFileExists(outputRoot, $"vegetation_masks/{entry.TileId}_vegetation.png"))
                return false;

            if (options.PackBuildingBundles)
            {
                if (options.PackFacadeBuildings && entry.HasFacadeBuildings &&
                    !HasBuildingBundle(entry, StreamingPackBakeKeys.Facade, outputRoot))
                    return false;

                if (options.PackOrthoRoofBuildings && entry.HasOrthoRoofBuildings &&
                    !HasBuildingBundle(entry, StreamingPackBakeKeys.OrthoRoof, outputRoot))
                    return false;
            }

            if (options.BakeVegetationBundles && options.PackVegetation && entry.HasVegetationMask &&
                !HasVegetationBundle(entry, outputRoot))
                return false;

            return true;
        }

        public static bool GroupTouchesFreshTiles(
            IEnumerable<StreamingTileEntry> children,
            HashSet<string> freshlyPackedTileIds)
        {
            if (freshlyPackedTileIds == null || freshlyPackedTileIds.Count == 0)
                return false;

            foreach (StreamingTileEntry child in children)
            {
                if (freshlyPackedTileIds.Contains(child.TileId))
                    return true;
            }

            return false;
        }

        public static bool CanReuseSupertile(
            StreamingSupertileEntry entry,
            List<StreamingTileEntry> children,
            RealtimePackOptions options,
            string outputRoot)
        {
            if (entry == null || children == null || children.Count == 0)
                return false;

            var childIds = new HashSet<string>(children.ConvertAll(c => c.TileId));
            if (entry.ChildTileIds == null ||
                entry.ChildTileIds.Count != childIds.Count ||
                entry.ChildTileIds.Any(id => !childIds.Contains(id)))
                return false;

            if (options.PackTerrain)
            {
                if (string.IsNullOrEmpty(entry.HeightmapPath) ||
                    !DatasetFileExists(outputRoot, entry.HeightmapPath))
                    return false;
            }

            if (options.PackTerrain && options.PackTerrainBundles &&
                !HasAllOrthoTerrainBundles(entry.TerrainBundlePaths, options, outputRoot))
                return false;

            if (options.PackOrtho)
            {
                foreach (DatasetFolderScanner.BasemapSource bm in options.Basemaps)
                {
                    if (bm.Type != BasemapType.Ortho)
                        continue;

                    string basemapId = ZGConnectPathUtils.DeriveBasemapId(bm.FolderPath);
                    if (entry.OrthoPaths == null ||
                        !entry.OrthoPaths.TryGetValue(basemapId, out string orthoRel) ||
                        !DatasetFileExists(outputRoot, orthoRel))
                        return false;
                }
            }

            return true;
        }

        public static bool HasBuildingBundle(
            StreamingTileEntry entry,
            string styleKey,
            string outputRoot)
        {
            return entry?.BuildingBundlePaths != null &&
                   entry.BuildingBundlePaths.TryGetValue(styleKey, out string bundleRel) &&
                   BundleFileExists(outputRoot, bundleRel);
        }

        public static bool HasVegetationBundle(StreamingTileEntry entry, string outputRoot)
        {
            return !string.IsNullOrEmpty(entry?.VegetationBundlePath) &&
                   BundleFileExists(outputRoot, entry.VegetationBundlePath);
        }

        public static bool BundleFileExists(string outputRoot, string terrainBundlePath)
        {
            if (string.IsNullOrEmpty(terrainBundlePath))
                return false;

            string full = Path.Combine(
                outputRoot,
                terrainBundlePath.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(full);
        }

        public static bool HasAllOrthoTerrainBundles(
            Dictionary<string, string> terrainBundlePaths,
            RealtimePackOptions options,
            string outputRoot)
        {
            if (options.Basemaps == null)
                return false;

            bool requiresAny = false;
            foreach (DatasetFolderScanner.BasemapSource bm in options.Basemaps)
            {
                if (bm.Type != BasemapType.Ortho)
                    continue;

                requiresAny = true;
                string basemapId = ZGConnectPathUtils.DeriveBasemapId(bm.FolderPath);
                if (terrainBundlePaths == null ||
                    !terrainBundlePaths.TryGetValue(basemapId, out string bundleRel) ||
                    !BundleFileExists(outputRoot, bundleRel))
                {
                    return false;
                }
            }

            return requiresAny;
        }

        public static bool DatasetFileExists(string outputRoot, string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                return false;

            string full = Path.Combine(
                outputRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(full);
        }

        /// <summary>
        /// True when a leaf tile has terrain the runtime streamer can load (RAW heightmap and/or ortho bundle).
        /// </summary>
        public static bool HasStreamableTerrain(StreamingTileEntry tile, string outputRoot)
        {
            if (tile == null || string.IsNullOrEmpty(outputRoot))
                return false;

            if (!string.IsNullOrEmpty(tile.HeightmapPath) &&
                DatasetFileExists(outputRoot, tile.HeightmapPath))
                return true;

            if (tile.TerrainBundlePaths == null || tile.TerrainBundlePaths.Count == 0)
                return false;

            foreach (KeyValuePair<string, string> kvp in tile.TerrainBundlePaths)
            {
                if (BundleFileExists(outputRoot, kvp.Value))
                    return true;
            }

            return false;
        }

        public static bool PackedBuildingGlbExists(string outputRoot, string folderName, string tileId) =>
            !string.IsNullOrEmpty(tileId) &&
            DatasetFileExists(outputRoot, $"{folderName}/buildings_{tileId}.glb");

        public static int PruneOrphanedBuildingMeshes(
            string outputRoot,
            IEnumerable<StreamingTileEntry> packedTiles)
        {
            if (string.IsNullOrEmpty(outputRoot) || packedTiles == null)
                return 0;

            var keepTileIds = new HashSet<string>();
            foreach (StreamingTileEntry tile in packedTiles)
            {
                if (!string.IsNullOrEmpty(tile?.TileId))
                    keepTileIds.Add(tile.TileId);
            }

            int removed = 0;
            foreach (string folder in new[] { "building_meshes", "building_meshes_ortho" })
            {
                string dir = Path.Combine(outputRoot, folder);
                if (!Directory.Exists(dir))
                    continue;

                foreach (string file in Directory.GetFiles(dir, "buildings_*.glb"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (!name.StartsWith("buildings_", StringComparison.Ordinal))
                        continue;
                    if (name.EndsWith("_lod1", StringComparison.Ordinal))
                        continue;

                    string tileId = name.Substring("buildings_".Length);
                    if (keepTileIds.Contains(tileId))
                        continue;

                    File.Delete(file);
                    string json = Path.Combine(dir, $"buildings_{tileId}.json");
                    if (File.Exists(json))
                        File.Delete(json);
                    removed++;
                }
            }

            if (removed > 0)
            {
                UnityEngine.Debug.Log(
                    $"[ZGConnect.Realtime] Pruned {removed} orphaned building GLB(s) not in the packed dataset.");
            }

            return removed;
        }

        public static void SyncBuildingFlagsFromDisk(
            IEnumerable<StreamingTileEntry> tiles,
            string outputRoot)
        {
            if (tiles == null || string.IsNullOrEmpty(outputRoot))
                return;

            foreach (StreamingTileEntry tile in tiles)
            {
                if (tile == null || string.IsNullOrEmpty(tile.TileId))
                    continue;

                if (PackedBuildingGlbExists(outputRoot, "building_meshes", tile.TileId))
                    tile.HasFacadeBuildings = true;
                if (PackedBuildingGlbExists(outputRoot, "building_meshes_ortho", tile.TileId))
                    tile.HasOrthoRoofBuildings = true;
            }
        }

        public static void AppendPreservedTiles(
            List<StreamingTileEntry> packedTiles,
            Dictionary<string, Dictionary<string, string>> orthoRelByTile,
            Dictionary<string, StreamingTileEntry> existingTilesById,
            RealtimePackOptions options,
            string outputRoot,
            HashSet<string> selectedTileIds)
        {
            if (!options.SkipExisting || existingTilesById == null)
                return;

            var packedIds = new HashSet<string>();
            foreach (StreamingTileEntry tile in packedTiles)
                packedIds.Add(tile.TileId);

            int skippedOutsideRegion = 0;
            foreach (KeyValuePair<string, StreamingTileEntry> kvp in existingTilesById)
            {
                if (selectedTileIds.Contains(kvp.Key) || packedIds.Contains(kvp.Key))
                    continue;

                StreamingTileEntry entry = kvp.Value;
                // Incremental pack: preserve tiles from prior regions. Region filter only limits
                // which tiles are processed in this run (SelectedTileIds), not the full dataset.
                if (!options.SkipExisting &&
                    options.RegionBounds.Enabled &&
                    !options.RegionBounds.IntersectsTile(entry.Left, entry.Right, entry.Bottom, entry.Top))
                {
                    skippedOutsideRegion++;
                    continue;
                }

                if (!CanReuseLeafTile(entry, options, outputRoot))
                    continue;

                packedTiles.Add(entry);
                packedIds.Add(kvp.Key);
                if (entry.OrthoPaths != null && entry.OrthoPaths.Count > 0)
                    orthoRelByTile[kvp.Key] = entry.OrthoPaths;
            }

            if (skippedOutsideRegion > 0)
            {
                UnityEngine.Debug.Log(
                    $"[ZGConnect.Realtime] Skipped reusing {skippedOutsideRegion} packed tile(s) outside the aligned region.");
            }
        }

        public static void PruneDatasetToRegionBounds(
            List<StreamingTileEntry> packedTiles,
            List<StreamingSupertileEntry> supertiles,
            StreamingRegionBoundsEpsg bounds,
            int tileSizeMeters,
            bool skipExisting = false)
        {
            if (!bounds.Enabled || skipExisting)
                return;

            int beforeTiles = packedTiles.Count;
            packedTiles.RemoveAll(tile =>
                !bounds.IntersectsTile(tile.Left, tile.Right, tile.Bottom, tile.Top));
            int removedTiles = beforeTiles - packedTiles.Count;

            var tileIds = new HashSet<string>();
            foreach (StreamingTileEntry tile in packedTiles)
                tileIds.Add(tile.TileId);

            int beforeSupertiles = supertiles?.Count ?? 0;
            if (supertiles != null)
            {
                supertiles.RemoveAll(st =>
                {
                    if (st.ChildTileIds == null || st.ChildTileIds.Exists(id => !tileIds.Contains(id)))
                        return true;

                    int width = st.Factor * tileSizeMeters;
                    int right = st.Left + width;
                    int top = st.Bottom + width;
                    return !bounds.IntersectsTile(st.Left, right, st.Bottom, top);
                });
            }

            int removedSupertiles = beforeSupertiles - (supertiles?.Count ?? 0);
            if (removedTiles > 0 || removedSupertiles > 0)
            {
                UnityEngine.Debug.LogWarning(
                    $"[ZGConnect.Realtime] Pruned dataset to aligned region E {bounds.MinE}–{bounds.MaxE}, " +
                    $"N {bounds.MinN}–{bounds.MaxN}: {removedTiles} leaf tile(s), {removedSupertiles} supertile(s).");
            }
        }

        public static void AppendPreservedSupertiles(
            List<StreamingSupertileEntry> supertiles,
            Dictionary<string, StreamingSupertileEntry> existingSupertilesById,
            HashSet<string> packedTileIds,
            HashSet<string> rebuiltSupertileIds,
            RealtimePackOptions options,
            string outputRoot,
            Dictionary<string, StreamingTileEntry> packedTilesById)
        {
            if (!options.SkipExisting || existingSupertilesById == null)
                return;

            var currentIds = new HashSet<string>();
            foreach (StreamingSupertileEntry st in supertiles)
                currentIds.Add(st.SupertileId);

            foreach (StreamingSupertileEntry entry in existingSupertilesById.Values)
            {
                if (currentIds.Contains(entry.SupertileId))
                    continue;
                if (rebuiltSupertileIds != null && rebuiltSupertileIds.Contains(entry.SupertileId))
                    continue;
                if (entry.ChildTileIds == null ||
                    entry.ChildTileIds.Any(id => !packedTileIds.Contains(id)))
                    continue;

                int width = entry.Factor * tileSizeMetersFrom(entry);
                if (!options.SkipExisting &&
                    options.RegionBounds.Enabled &&
                    !options.RegionBounds.IntersectsTile(
                        entry.Left, entry.Left + width, entry.Bottom, entry.Bottom + width))
                    continue;

                var children = new List<StreamingTileEntry>();
                foreach (string childId in entry.ChildTileIds)
                {
                    if (!packedTilesById.TryGetValue(childId, out StreamingTileEntry child))
                    {
                        children = null;
                        break;
                    }

                    children.Add(child);
                }

                if (children == null || !CanReuseSupertile(entry, children, options, outputRoot))
                    continue;

                supertiles.Add(entry);
                currentIds.Add(entry.SupertileId);
            }
        }

        static int tileSizeMetersFrom(StreamingSupertileEntry entry)
        {
            if (entry?.TerrainSize != null && entry.TerrainSize.Length >= 3 && entry.Factor > 0)
            {
                int fromSize = Mathf.RoundToInt(entry.TerrainSize[0] / entry.Factor);
                if (fromSize > 0)
                    return fromSize;
            }

            return 1000;
        }
    }
}
