using GLTFast;
using UnityEngine;

namespace ZGConnect.PhotogrammetryStreaming
{
    /// <summary>Keeps GltfImport alive while tile meshes reference its sub-assets.</summary>
    public sealed class PhotogrammetryTileHolder : MonoBehaviour
    {
        GltfImport _import;
        string _copyright;

        public string Copyright => _copyright;

        public void SetImport(GltfImport import, string copyright = null)
        {
            _import = import;
            _copyright = copyright;
        }

        public void DisposeImport()
        {
            _import?.Dispose();
            _import = null;
        }

        void OnDestroy() => DisposeImport();
    }
}
