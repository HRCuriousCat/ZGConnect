using GLTFast;
using UnityEngine;

namespace ZGConnect.RealtimeStreaming
{
    /// <summary>
    /// Keeps GltfImport instances alive while building hierarchies reference their meshes/materials.
    /// GLTFast destroys sub-assets when GltfImport.Dispose is called.
    /// </summary>
    public sealed class RuntimeBuildingsGltfHolder : MonoBehaviour
    {
        GltfImport _mainImport;
        GltfImport _lod1Import;

        public void SetImports(GltfImport mainImport, GltfImport lod1Import = null)
        {
            ReleaseImports();
            _mainImport = mainImport;
            _lod1Import = lod1Import;
        }

        void OnDestroy()
        {
            ReleaseImports();
        }

        void ReleaseImports()
        {
            if (_lod1Import != null)
            {
                _lod1Import.Dispose();
                _lod1Import = null;
            }

            if (_mainImport != null)
            {
                _mainImport.Dispose();
                _mainImport = null;
            }
        }
    }
}

