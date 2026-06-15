using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using ZGConnect.Editor;

namespace ZGConnect.RealtimeStreaming.Editor
{
    public static class BuildingLodPackUtility
    {
        public static bool TryPackTileWithLodHierarchy(
            string sourceGlbPath,
            string outputGlbPath,
            string tileId,
            out bool usedDualFallback)
        {
            usedDualFallback = false;
            if (!File.Exists(sourceGlbPath))
                return false;

            string tempAssetDir = "Assets/StreamingAssets/ZGConnect/_pack_temp";
            ZGConnectPathUtils.EnsureAssetFolder(tempAssetDir);
            string tempGlbAsset = $"{tempAssetDir}/pack_{tileId}.glb";
            string tempFull = ZGConnectPathUtils.AssetPathToFullPath(tempGlbAsset);

            File.Copy(sourceGlbPath, tempFull, true);
            AssetDatabase.ImportAsset(tempGlbAsset, ImportAssetOptions.ForceUpdate);

            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(tempGlbAsset);
            if (asset == null)
            {
                File.Copy(sourceGlbPath, outputGlbPath, true);
                return true;
            }

            GameObject instance = UnityEngine.Object.Instantiate(asset);
            instance.name = $"TileBuildings_{tileId}";
            instance.hideFlags = HideFlags.HideAndDontSave;

            try
            {
                RestructureWithLodBoxes(instance.transform);
                string outDir = Path.GetDirectoryName(outputGlbPath);
                if (!string.IsNullOrEmpty(outDir))
                    Directory.CreateDirectory(outDir);

                if (ZGConnectGlbExportUtility.IsAvailable)
                {
                    try
                    {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    ZGConnectGlbExportUtility.ExportRootGeometryOnly(instance, outputGlbPath);
                    sw.Stop();

                    if (sw.Elapsed.TotalSeconds > 30)
                    {
                        usedDualFallback = true;
                        File.Copy(sourceGlbPath, outputGlbPath, true);
                        string lod1Path = outputGlbPath.Replace(".glb", "_lod1.glb");
                        ZGConnectGlbExportUtility.ExportRootGeometryOnly(instance, lod1Path);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning(
                            $"[ZGConnect.Realtime] LOD GLB export failed for tile '{tileId}': {ex.Message}. " +
                            "Using source GLB.");
                        File.Copy(sourceGlbPath, outputGlbPath, true);
                    }
                }
                else
                {
                    File.Copy(sourceGlbPath, outputGlbPath, true);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
                AssetDatabase.DeleteAsset(tempGlbAsset);
            }

            return OutputGlbLooksValid(outputGlbPath);
        }

        public static bool OutputGlbLooksValid(string glbPath)
        {
            if (string.IsNullOrEmpty(glbPath) || !File.Exists(glbPath))
                return false;

            return new FileInfo(glbPath).Length > 0;
        }

        static void RestructureWithLodBoxes(Transform tileRoot)
        {
            var buildings = new List<Transform>();
            for (int i = 0; i < tileRoot.childCount; i++)
                buildings.Add(tileRoot.GetChild(i));

            foreach (Transform building in buildings)
            {
                if (building.Find("LOD0") != null || building.Find("LOD1") != null)
                    continue;

                var renderers = building.GetComponentsInChildren<MeshRenderer>(true);
                if (renderers.Length == 0)
                    continue;

                Bounds bounds = renderers[0].bounds;
                for (int r = 1; r < renderers.Length; r++)
                    bounds.Encapsulate(renderers[r].bounds);

                var originalChildren = new List<Transform>();
                for (int c = 0; c < building.childCount; c++)
                    originalChildren.Add(building.GetChild(c));

                var lod0 = new GameObject("LOD0");
                lod0.transform.SetParent(building, false);
                var lod1 = new GameObject("LOD1");
                lod1.transform.SetParent(building, false);

                foreach (Transform child in originalChildren)
                    child.SetParent(lod0.transform, true);

                GameObject boxGo = CreateBoxProxy(bounds, building.name);
                boxGo.transform.SetParent(lod1.transform, false);
                boxGo.transform.localPosition = building.InverseTransformPoint(bounds.center);
                boxGo.transform.localRotation = Quaternion.identity;
                boxGo.transform.localScale = bounds.size;
            }
        }

        static GameObject CreateBoxProxy(Bounds bounds, string name)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = $"{name}_LOD1Box";
            UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
        }
    }
}
