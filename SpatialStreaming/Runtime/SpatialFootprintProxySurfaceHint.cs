using UnityEngine;
using ZGConnect;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// Baked footprint proxy slot — category + facade/roof. No textures in the bundle;
    /// <see cref="SpatialBuildingMaterialApplier"/> resolves materials at runtime from
    /// <see cref="BuildingSurfaceSettings"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SpatialFootprintProxySurfaceHint : MonoBehaviour
    {
        public BuildingCategory category = BuildingCategory.House;
        public BuildingSurfaceMaterialType surfaceType = BuildingSurfaceMaterialType.Facade;
    }
}
