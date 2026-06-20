using System.Text;
using UnityEditor;
using UnityEngine;

namespace ZGConnect.SpatialStreaming.Editor
{
    internal static class SpatialBakeVerboseLog
    {
        public const string Prefix = "[ZGConnect.Spatial][Bake]";

        public static void Global(string step, string detail = null) =>
            Debug.Log(Format(null, null, step, detail));

        public static void Tile(string tileId, string step, string detail = null) =>
            Debug.Log(Format(tileId, null, step, detail));

        public static void Subcell(string tileId, string subcellId, string step, string detail = null) =>
            Debug.Log(Format(tileId, subcellId, step, detail));

        public static void TileWarning(string tileId, string step, string detail) =>
            Debug.LogWarning(Format(tileId, null, step, detail));

        public static void SubcellWarning(string tileId, string subcellId, string step, string detail) =>
            Debug.LogWarning(Format(tileId, subcellId, step, detail));

        public static string HierarchyStats(Transform root)
        {
            if (root == null)
                return "root=null";

            int meshFilters = root.GetComponentsInChildren<MeshFilter>(true).Length;
            int renderers = root.GetComponentsInChildren<MeshRenderer>(true).Length;
            return $"children={root.childCount} meshFilters={meshFilters} renderers={renderers} " +
                   $"active={root.gameObject.activeSelf} hideFlags={root.gameObject.hideFlags}";
        }

        public static string CountEphemeralAssets(Transform root)
        {
            if (root == null)
                return "root=null";

            int ephemeralMeshes = 0;
            int ephemeralMaterials = 0;
            int readableMeshes = 0;
            int unreadableMeshes = 0;

            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null)
                    continue;

                if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(mesh)))
                    ephemeralMeshes++;
                if (mesh.isReadable)
                    readableMeshes++;
                else
                    unreadableMeshes++;
            }

            foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material == null)
                        continue;

                    if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(material)))
                        ephemeralMaterials++;
                }
            }

            return $"ephemeralMeshes={ephemeralMeshes} ephemeralMaterials={ephemeralMaterials} " +
                   $"readableMeshes={readableMeshes} unreadableMeshes={unreadableMeshes}";
        }

        public static string PrefabSaveState(string prefabAssetPath)
        {
            if (string.IsNullOrEmpty(prefabAssetPath))
                return "path=null";

            string fullPath = ZGConnect.Editor.ZGConnectPathUtils.AssetPathToFullPath(prefabAssetPath);
            bool onDisk = System.IO.File.Exists(fullPath);
            GameObject loaded = AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath);
            return $"path={prefabAssetPath} onDisk={onDisk} assetLoaded={(loaded != null)}";
        }

        static string Format(string tileId, string subcellId, string step, string detail)
        {
            var sb = new StringBuilder(Prefix);
            if (!string.IsNullOrEmpty(tileId))
                sb.Append('[').Append(tileId).Append(']');
            if (!string.IsNullOrEmpty(subcellId))
                sb.Append('[').Append(subcellId).Append(']');
            sb.Append(' ').Append(step);
            if (!string.IsNullOrEmpty(detail))
                sb.Append(" — ").Append(detail);
            return sb.ToString();
        }
    }
}
