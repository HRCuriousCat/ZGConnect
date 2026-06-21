using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using ZGConnect;
using ZGConnect.Editor;
using ZGConnect.SpatialStreaming;

namespace ZGConnect.SpatialStreaming.Editor
{
    public sealed class SpatialBakedTileProxyInfo
    {
        public string TileId;
        public int Left;
        public int Bottom;
        public Vector3 UnityPosition;
        public string PrefabAssetPath;
    }

    public static class SpatialSupertileBakeUtility
    {
        public static IEnumerator BakeSupertilesCoroutine(
            SpatialBakeProfile profile,
            int tileSizeMeters,
            List<SpatialBakedTileProxyInfo> tileProxies,
            SpatialDatasetManifest manifest,
            List<AssetBundleBuild> bundleBuilds,
            string stagingRoot,
            Action<float, string> onProgress = null)
        {
            if (profile == null || !profile.bakeHlodSupertiles || tileProxies == null || tileProxies.Count == 0)
                yield break;

            manifest.Supertiles ??= new List<SpatialSupertileManifestEntry>();
            manifest.Supertiles.Clear();

            IEnumerator bake2 = BakeFactorCoroutine(
                profile,
                tileSizeMeters,
                tileProxies,
                manifest,
                bundleBuilds,
                stagingRoot,
                factor: 2,
                onProgress);
            while (bake2.MoveNext())
                yield return bake2.Current;

            IEnumerator bake4 = BakeFactorCoroutine(
                profile,
                tileSizeMeters,
                tileProxies,
                manifest,
                bundleBuilds,
                stagingRoot,
                factor: 4,
                onProgress);
            while (bake4.MoveNext())
                yield return bake4.Current;
        }

        public static void BakeSupertiles(
            SpatialBakeProfile profile,
            int tileSizeMeters,
            List<SpatialBakedTileProxyInfo> tileProxies,
            SpatialDatasetManifest manifest,
            List<AssetBundleBuild> bundleBuilds,
            string stagingRoot)
        {
            if (profile == null || !profile.bakeHlodSupertiles || tileProxies == null || tileProxies.Count == 0)
                return;

            manifest.Supertiles ??= new List<SpatialSupertileManifestEntry>();
            manifest.Supertiles.Clear();

            BakeFactor(
                profile,
                tileSizeMeters,
                tileProxies,
                manifest,
                bundleBuilds,
                stagingRoot,
                factor: 2);

            BakeFactor(
                profile,
                tileSizeMeters,
                tileProxies,
                manifest,
                bundleBuilds,
                stagingRoot,
                factor: 4);
        }

        static IEnumerator BakeFactorCoroutine(
            SpatialBakeProfile profile,
            int tileSizeMeters,
            List<SpatialBakedTileProxyInfo> tileProxies,
            SpatialDatasetManifest manifest,
            List<AssetBundleBuild> bundleBuilds,
            string stagingRoot,
            int factor,
            Action<float, string> onProgress)
        {
            int blockSizeMeters = tileSizeMeters * factor;
            var groups = new Dictionary<string, List<SpatialBakedTileProxyInfo>>();

            foreach (SpatialBakedTileProxyInfo tile in tileProxies)
            {
                if (tile == null || string.IsNullOrEmpty(tile.PrefabAssetPath))
                    continue;

                int blockLeft = SpatialTileIdUtility.AlignDownMeters(tile.Left, blockSizeMeters);
                int blockBottom = SpatialTileIdUtility.AlignDownMeters(tile.Bottom, blockSizeMeters);
                string key = SpatialStreamingPaths.GetSupertileId(factor, blockLeft, blockBottom);
                if (!groups.TryGetValue(key, out List<SpatialBakedTileProxyInfo> list))
                {
                    list = new List<SpatialBakedTileProxyInfo>();
                    groups[key] = list;
                }

                list.Add(tile);
            }

            int total = groups.Count;
            int done = 0;
            foreach (KeyValuePair<string, List<SpatialBakedTileProxyInfo>> kvp in groups)
            {
                if (kvp.Value.Count == 0)
                    continue;

                onProgress?.Invoke(
                    total > 0 ? (float)done / total : 0f,
                    $"HLOD{factor} {kvp.Key}");

                if (!TryBakeSupertilePrefab(
                        profile,
                        factor,
                        tileSizeMeters,
                        kvp.Value,
                        stagingRoot,
                        out SpatialSupertileManifestEntry entry,
                        out string prefabAssetPath,
                        out string bundleName))
                {
                    done++;
                    yield return null;
                    continue;
                }

                manifest.Supertiles.Add(entry);
                bundleBuilds.Add(new AssetBundleBuild
                {
                    assetBundleName = bundleName,
                    assetNames = new[] { prefabAssetPath },
                });

                done++;
                yield return null;
            }
        }

        static void BakeFactor(
            SpatialBakeProfile profile,
            int tileSizeMeters,
            List<SpatialBakedTileProxyInfo> tileProxies,
            SpatialDatasetManifest manifest,
            List<AssetBundleBuild> bundleBuilds,
            string stagingRoot,
            int factor)
        {
            int blockSizeMeters = tileSizeMeters * factor;
            var groups = new Dictionary<string, List<SpatialBakedTileProxyInfo>>();

            foreach (SpatialBakedTileProxyInfo tile in tileProxies)
            {
                if (tile == null || string.IsNullOrEmpty(tile.PrefabAssetPath))
                    continue;

                int blockLeft = SpatialTileIdUtility.AlignDownMeters(tile.Left, blockSizeMeters);
                int blockBottom = SpatialTileIdUtility.AlignDownMeters(tile.Bottom, blockSizeMeters);
                string key = SpatialStreamingPaths.GetSupertileId(factor, blockLeft, blockBottom);
                if (!groups.TryGetValue(key, out List<SpatialBakedTileProxyInfo> list))
                {
                    list = new List<SpatialBakedTileProxyInfo>();
                    groups[key] = list;
                }

                list.Add(tile);
            }

            foreach (KeyValuePair<string, List<SpatialBakedTileProxyInfo>> kvp in groups)
            {
                if (kvp.Value.Count == 0)
                    continue;

                if (!TryBakeSupertilePrefab(
                        profile,
                        factor,
                        tileSizeMeters,
                        kvp.Value,
                        stagingRoot,
                        out SpatialSupertileManifestEntry entry,
                        out string prefabAssetPath,
                        out string bundleName))
                {
                    continue;
                }

                manifest.Supertiles.Add(entry);
                bundleBuilds.Add(new AssetBundleBuild
                {
                    assetBundleName = bundleName,
                    assetNames = new[] { prefabAssetPath },
                });
            }
        }

        static bool TryBakeSupertilePrefab(
            SpatialBakeProfile profile,
            int factor,
            int tileSizeMeters,
            List<SpatialBakedTileProxyInfo> tiles,
            string stagingRoot,
            out SpatialSupertileManifestEntry entry,
            out string prefabAssetPath,
            out string bundleName)
        {
            entry = null;
            prefabAssetPath = null;
            bundleName = null;

            Material placeholder = SpatialBakeProxyUtility.ResolveBakePlaceholderMaterial(profile);
            if (placeholder == null)
                return false;

            int alignedBlockSize = tileSizeMeters * factor;
            int blockLeft = SpatialTileIdUtility.AlignDownMeters(tiles[0].Left, alignedBlockSize);
            int blockBottom = SpatialTileIdUtility.AlignDownMeters(tiles[0].Bottom, alignedBlockSize);
            Vector3 origin = new Vector3(float.MaxValue, 0f, float.MaxValue);
            var childTileIds = new List<string>();
            var groups = new Dictionary<string, SupertileSurfaceGroup>(System.StringComparer.Ordinal);

            foreach (SpatialBakedTileProxyInfo tile in tiles)
            {
                if (tile == null)
                    continue;

                origin.x = Mathf.Min(origin.x, tile.UnityPosition.x);
                origin.z = Mathf.Min(origin.z, tile.UnityPosition.z);
                if (!childTileIds.Contains(tile.TileId))
                    childTileIds.Add(tile.TileId);

                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(tile.PrefabAssetPath);
                if (prefab == null)
                    continue;

                Vector3 offset = tile.UnityPosition - origin;
                foreach (MeshRenderer meshRenderer in prefab.GetComponentsInChildren<MeshRenderer>(true))
                {
                    MeshFilter filter = meshRenderer.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null)
                        continue;

                    if (!SpatialFootprintBoxUtility.TryResolveProxySurfaceHint(
                            meshRenderer,
                            out BuildingCategory category,
                            out BuildingSurfaceMaterialType surfaceType))
                    {
                        continue;
                    }

                    string groupKey = BuildingSurfaceUtility.CategoryToSlotToken(category) + "_" +
                                        BuildingSurfaceUtility.SurfaceToSlotToken(surfaceType);
                    if (!groups.TryGetValue(groupKey, out SupertileSurfaceGroup group))
                    {
                        group = new SupertileSurfaceGroup
                        {
                            Category = category,
                            SurfaceType = surfaceType,
                        };
                        groups[groupKey] = group;
                    }

                    group.Instances.Add(new CombineInstance
                    {
                        mesh = filter.sharedMesh,
                        subMeshIndex = 0,
                        transform = Matrix4x4.TRS(offset, Quaternion.identity, Vector3.one),
                    });
                }
            }

            if (groups.Count == 0)
                return false;

            var root = new GameObject(SpatialMeshCombineUtility.CombinedRenderRootName);
            var ownedMeshes = new List<Mesh>();
            int totalInstances = 0;

            foreach (KeyValuePair<string, SupertileSurfaceGroup> kvp in groups)
            {
                SupertileSurfaceGroup group = kvp.Value;
                if (group.Instances.Count == 0)
                    continue;

                totalInstances += group.Instances.Count;
                string meshName = SpatialFootprintBoxUtility.BuildFootprintProxyMeshName(
                    group.Category,
                    group.SurfaceType);
                Mesh combined = SpatialMeshCombineUtility.CombineMeshesInSpace(
                    group.Instances,
                    mergeSubMeshes: true,
                    meshName,
                    ownedMeshes);

                if (combined == null)
                    continue;

                string objectName = SpatialFootprintBoxUtility.BuildCombinedProxyObjectName(
                    group.Category,
                    group.SurfaceType);
                var child = new GameObject(objectName);
                child.transform.SetParent(root.transform, false);
                child.AddComponent<MeshFilter>().sharedMesh = combined;
                var renderer = child.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = placeholder;
                var hint = child.AddComponent<SpatialFootprintProxySurfaceHint>();
                hint.category = group.Category;
                hint.surfaceType = group.SurfaceType;
            }

            if (root.transform.childCount == 0)
            {
                UnityEngine.Object.DestroyImmediate(root);
                return false;
            }

            string supertileId = SpatialStreamingPaths.GetSupertileId(factor, blockLeft, blockBottom);
            string folder = $"{stagingRoot}/hlod{factor}";
            ZGConnectPathUtils.EnsureAssetFolder(folder);
            prefabAssetPath = $"{folder}/Spatial_{supertileId}.prefab";

            if (!SpatialBakeProxyUtility.SaveDetachedPrefab(root, prefabAssetPath, ownedMeshes, profile, out string error))
            {
                UnityEngine.Object.DestroyImmediate(root);
                if (profile.verboseBakeLogging)
                    SpatialBakeVerboseLog.Global("hlod-save-failed", $"{supertileId}: {error}");
                return false;
            }

            UnityEngine.Object.DestroyImmediate(root);

            entry = new SpatialSupertileManifestEntry
            {
                SupertileId = supertileId,
                Factor = factor,
                Left = blockLeft,
                Bottom = blockBottom,
                UnityPosition = new[] { origin.x, origin.y, origin.z },
                BundleRel = SpatialStreamingPaths.GetSupertileBundleRelativePath(factor, blockLeft, blockBottom),
                ChildTileIds = childTileIds,
            };
            bundleName = $"hlod{factor}/{blockLeft}_{blockBottom}";

            if (profile.verboseBakeLogging)
            {
                SpatialBakeVerboseLog.Global(
                    "hlod-baked",
                    $"{supertileId} tiles={childTileIds.Count} groups={groups.Count} instances={totalInstances}");
            }

            return true;
        }

        sealed class SupertileSurfaceGroup
        {
            public BuildingCategory Category;
            public BuildingSurfaceMaterialType SurfaceType;
            public readonly List<CombineInstance> Instances = new();
        }
    }
}
