namespace ZGConnect
{
    /// <summary>How <see cref="TerrainStreamingController"/> remaps building materials on instantiate.</summary>
    public enum BuildingMaterialApplyStyle
    {
        /// <summary>One shared facade material from <see cref="BuildingSurfaceSettings"/> (raw GLB).</summary>
        UniformFacade = 0,

        /// <summary>Facade + roof variant materials by building height/category (processed surfaces).</summary>
        FacadeAndRoofVariants = 1,

        /// <summary>Shared facade + per-tile orthophoto roof material (roof orthophoto import).</summary>
        RoofOrthophoto = 2,
    }
}

