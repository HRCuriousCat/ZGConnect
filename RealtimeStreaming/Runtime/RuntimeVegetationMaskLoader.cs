using System.Collections.Generic;
using System.IO;
using UnityEngine;
using ZGConnect;

namespace ZGConnect.RealtimeStreaming
{
    public static class RuntimeVegetationMaskLoader
    {
        public static Texture2D LoadMask(string fullPath)
        {
            if (!File.Exists(fullPath))
                return null;

            byte[] bytes = File.ReadAllBytes(fullPath);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            if (!tex.LoadImage(bytes))
            {
                Object.Destroy(tex);
                return null;
            }
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            return tex;
        }

        public static bool SupertileHasVegetationMask(
            StreamingSupertileEntry supertile,
            StreamingDatasetManifest manifest,
            string datasetRoot)
        {
            if (supertile?.ChildTileIds == null || supertile.ChildTileIds.Count == 0)
                return false;

            Dictionary<string, StreamingTileEntry> leafById = BuildLeafLookup(manifest);
            foreach (string childId in supertile.ChildTileIds)
            {
                if (leafById.TryGetValue(childId, out StreamingTileEntry leaf) && leaf.HasVegetationMask)
                    return true;

                string path = GetMaskPath(datasetRoot, childId);
                if (File.Exists(path))
                    return true;
            }

            return false;
        }

        public static Texture2D ComposeSupertileVegetationMask(
            StreamingSupertileEntry supertile,
            StreamingDatasetManifest manifest,
            string datasetRoot)
        {
            if (supertile == null || manifest == null || supertile.Factor < 2)
                return null;

            if (supertile.ChildTileIds == null || supertile.ChildTileIds.Count == 0)
                return null;

            int factor = supertile.Factor;
            int tileSizeMeters = manifest.TileSizeMeters;
            Dictionary<string, StreamingTileEntry> leafById = BuildLeafLookup(manifest);

            int leafWidth = 0;
            int leafHeight = 0;
            foreach (string childId in supertile.ChildTileIds)
            {
                Texture2D sample = LoadMask(GetMaskPath(datasetRoot, childId));
                if (sample == null)
                    continue;

                leafWidth = sample.width;
                leafHeight = sample.height;
                Object.Destroy(sample);
                break;
            }

            if (leafWidth <= 0 || leafHeight <= 0)
                return null;

            var combined = new Texture2D(
                leafWidth * factor,
                leafHeight * factor,
                TextureFormat.RGBA32,
                false,
                true);

            var clear = new Color[combined.width * combined.height];
            combined.SetPixels(clear);

            bool anyCopied = false;
            foreach (string childId in supertile.ChildTileIds)
            {
                if (!leafById.TryGetValue(childId, out StreamingTileEntry leaf))
                    continue;

                int gridX = (leaf.Left - supertile.Left) / tileSizeMeters;
                int gridZ = (leaf.Bottom - supertile.Bottom) / tileSizeMeters;
                if (gridX < 0 || gridX >= factor || gridZ < 0 || gridZ >= factor)
                    continue;

                Texture2D childMask = LoadMask(GetMaskPath(datasetRoot, childId));
                if (childMask == null)
                    continue;

                if (childMask.width != leafWidth || childMask.height != leafHeight)
                {
                    Object.Destroy(childMask);
                    continue;
                }

                combined.SetPixels(
                    gridX * leafWidth,
                    gridZ * leafHeight,
                    leafWidth,
                    leafHeight,
                    childMask.GetPixels());
                Object.Destroy(childMask);
                anyCopied = true;
            }

            if (!anyCopied)
            {
                Object.Destroy(combined);
                return null;
            }

            combined.Apply(false, false);
            combined.wrapMode = TextureWrapMode.Clamp;
            combined.filterMode = FilterMode.Bilinear;
            return combined;
        }

        public static VegetationChunkRenderer SpawnVegetation(
            RuntimeTileRecord record,
            Transform camera,
            float minHeight,
            float maxHeight,
            VegetationRuleSet ruleSet,
            VegetationPrototype[] prototypes,
            VegetationSpawnAnimationSettings animationSettings,
            VegetationDrawDistanceSettings drawDistanceSettings,
            bool frustumCullChunks = true,
            bool frustumCullInstances = true)
        {
            if (record.VegetationMask == null || ruleSet == null || record.TerrainObject == null)
                return null;

            float halfY = (maxHeight - minHeight) * 0.5f;
            float midY = minHeight + halfY;
            var bounds = new Bounds(
                new Vector3(
                    record.UnityPosition.x + record.TileSizeMeters * 0.5f,
                    midY,
                    record.UnityPosition.z + record.TileSizeMeters * 0.5f),
                new Vector3(record.TileSizeMeters, halfY * 2f, record.TileSizeMeters));

            Terrain terrain = record.TerrainObject.GetComponent<Terrain>();
            var generator = new VegetationInstanceGenerator();
            VegetationChunk chunk = generator.GenerateChunk(
                record.Key,
                bounds,
                record.VegetationMask,
                ruleSet,
                prototypes,
                terrain);

            VegetationChunkRenderer renderer = record.TerrainObject.GetComponent<VegetationChunkRenderer>();
            if (renderer == null)
                renderer = record.TerrainObject.AddComponent<VegetationChunkRenderer>();

            renderer.SetChunk(
                chunk,
                prototypes,
                animationSettings,
                playSpawnAnimation: Application.isPlaying,
                drawDistanceSettings,
                camera,
                frustumCullChunks,
                frustumCullInstances);

            return renderer;
        }

        static Dictionary<string, StreamingTileEntry> BuildLeafLookup(StreamingDatasetManifest manifest)
        {
            var leafById = new Dictionary<string, StreamingTileEntry>();
            if (manifest?.Tiles == null)
                return leafById;

            foreach (StreamingTileEntry leaf in manifest.Tiles)
                leafById[leaf.TileId] = leaf;

            return leafById;
        }

        static string GetMaskPath(string datasetRoot, string tileId) =>
            Path.Combine(datasetRoot,
                $"vegetation_masks/{tileId}_vegetation.png".Replace('/', Path.DirectorySeparatorChar));
    }
}
