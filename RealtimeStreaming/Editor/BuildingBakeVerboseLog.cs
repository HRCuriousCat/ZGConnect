using System.Text;
using UnityEngine;

namespace ZGConnect.RealtimeStreaming.Editor
{
    internal static class BuildingBakeVerboseLog
    {
        public const string Prefix = "[ZGConnect.Realtime][BuildingBake]";

        public static void Global(string step, string detail = null) =>
            Debug.Log(Format(null, step, detail));

        public static void Tile(string tileId, string step, string detail = null) =>
            Debug.Log(Format(tileId, step, detail));

        public static void TileWarning(string tileId, string step, string detail) =>
            Debug.LogWarning(Format(tileId, step, detail));

        public static string HierarchyStats(Transform root)
        {
            if (root == null)
                return "root=null";

            int meshFilters = root.GetComponentsInChildren<MeshFilter>(true).Length;
            int renderers = root.GetComponentsInChildren<MeshRenderer>(true).Length;
            int colliders = root.GetComponentsInChildren<Collider>(true).Length;
            int lodGroups = root.GetComponentsInChildren<LODGroup>(true).Length;
            return $"children={root.childCount} meshFilters={meshFilters} renderers={renderers} " +
                   $"colliders={colliders} lodGroups={lodGroups}";
        }

        static string Format(string tileId, string step, string detail)
        {
            var sb = new StringBuilder(Prefix);
            if (!string.IsNullOrEmpty(tileId))
                sb.Append('[').Append(tileId).Append(']');
            sb.Append(' ').Append(step);
            if (!string.IsNullOrEmpty(detail))
                sb.Append(" â€” ").Append(detail);
            return sb.ToString();
        }
    }
}

