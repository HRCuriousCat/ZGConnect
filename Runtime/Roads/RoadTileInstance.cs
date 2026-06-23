using UnityEngine;

namespace ZGConnect.Roads
{
    public sealed class RoadTileInstance
    {
        public readonly string TileId;
        public readonly GameObject Root;
        public readonly Mesh AsphaltMesh;
        public readonly Mesh SidewalkMesh;

        public RoadTileInstance(string tileId, GameObject root, Mesh asphaltMesh, Mesh sidewalkMesh)
        {
            TileId = tileId;
            Root = root;
            AsphaltMesh = asphaltMesh;
            SidewalkMesh = sidewalkMesh;
        }

        public void Dispose()
        {
            if (Application.isPlaying)
            {
                if (AsphaltMesh != null)
                    Object.Destroy(AsphaltMesh);
                if (SidewalkMesh != null)
                    Object.Destroy(SidewalkMesh);
                if (Root != null)
                    Object.Destroy(Root);
            }
            else
            {
                if (AsphaltMesh != null)
                    Object.DestroyImmediate(AsphaltMesh);
                if (SidewalkMesh != null)
                    Object.DestroyImmediate(SidewalkMesh);
                if (Root != null)
                    Object.DestroyImmediate(Root);
            }
        }
    }
}
