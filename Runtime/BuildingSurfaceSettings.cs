using System;
using UnityEngine;

namespace ZGConnect
{
    [Serializable]
    public class BuildingSurfaceVariant
    {
        public Material material;
    }

    [Serializable]
    public class BuildingCategoryProfile
    {
        public BuildingCategory category = BuildingCategory.House;

        [Tooltip("Floor rows baked into the facade texture (House=3, MidRise=7, HighRise=16).")]
        public int maxFloorsInTexture = 3;

        public int textureWidth  = 512;
        public int textureHeight = 512;

        [Tooltip("World meters represented by U 0..1 on facades.")]
        public float horizontalMetersPerRepeat = 3f;

        [Tooltip("Used to estimate floor count from mesh height when BuildingData.floors is 0.")]
        public float metersPerFloor = 3f;

        [Header("Facade variants (same UV layout per category)")]
        public BuildingSurfaceVariant[] facadeVariants = Array.Empty<BuildingSurfaceVariant>();

        [Header("Roof — flat")]
        public float flatRoofMetersPerTile = 4f;
        public BuildingSurfaceVariant[] flatRoofVariants = Array.Empty<BuildingSurfaceVariant>();

        [Header("Roof — sloped")]
        public float slopedRoofMetersPerTile = 2f;
        public BuildingSurfaceVariant[] slopedRoofVariants = Array.Empty<BuildingSurfaceVariant>();
    }

    [CreateAssetMenu(
        fileName = "BuildingSurfaceSettings",
        menuName = "ZG Connect/Building Surface Settings")]
    public class BuildingSurfaceSettings : ScriptableObject
    {
        public const string TexturesRoot = "Assets/Buildings/Textures";
        public const string MaterialsRoot = "Assets/Buildings/Materials";

        [Header("Surface classification")]
        [Tooltip("|normal.y| below this → wall. Default ~0.25 (~75° from horizontal).")]
        [Range(0.05f, 0.45f)]
        public float wallMaxNormalY = 0.25f;

        [Tooltip("normal.y above this → flat roof. Default ~0.92.")]
        [Range(0.7f, 0.99f)]
        public float flatRoofMinNormalY = 0.92f;

        [Header("Debug")]
        [Tooltip("Logs UV/classification diagnostics during building import.")]
        public bool debugSurfaceUv = false;

        [Header("Category profiles")]
        public BuildingCategoryProfile houseProfile = new BuildingCategoryProfile
        {
            category = BuildingCategory.House,
            maxFloorsInTexture = 3,
            textureWidth = 512,
            textureHeight = 512,
            horizontalMetersPerRepeat = 3f,
            metersPerFloor = 3f,
            flatRoofMetersPerTile = 4f,
            slopedRoofMetersPerTile = 2f,
        };

        public BuildingCategoryProfile midRiseProfile = new BuildingCategoryProfile
        {
            category = BuildingCategory.MidRise,
            maxFloorsInTexture = 7,
            textureWidth = 512,
            textureHeight = 1024,
            horizontalMetersPerRepeat = 3f,
            metersPerFloor = 3f,
            flatRoofMetersPerTile = 4f,
            slopedRoofMetersPerTile = 2f,
        };

        public BuildingCategoryProfile highRiseProfile = new BuildingCategoryProfile
        {
            category = BuildingCategory.HighRise,
            maxFloorsInTexture = 16,
            textureWidth = 512,
            textureHeight = 2048,
            horizontalMetersPerRepeat = 2.5f,
            metersPerFloor = 3f,
            flatRoofMetersPerTile = 4f,
            slopedRoofMetersPerTile = 2f,
        };

        public BuildingCategoryProfile GetProfile(BuildingCategory category)
        {
            switch (category)
            {
                case BuildingCategory.MidRise:   return midRiseProfile;
                case BuildingCategory.HighRise: return highRiseProfile;
                default:                        return houseProfile;
            }
        }

        public Material GetFacadeMaterial(BuildingCategory category, int variantIndex)
        {
            var v = GetProfile(category).facadeVariants;
            if (v == null || v.Length == 0) return null;
            variantIndex = Mathf.Clamp(variantIndex, 0, v.Length - 1);
            return v[variantIndex].material;
        }

        public Material GetFlatRoofMaterial(BuildingCategory category, int variantIndex)
        {
            var v = GetProfile(category).flatRoofVariants;
            if (v == null || v.Length == 0) return null;
            variantIndex = Mathf.Clamp(variantIndex, 0, v.Length - 1);
            return v[variantIndex].material;
        }

        public Material GetSlopedRoofMaterial(BuildingCategory category, int variantIndex)
        {
            var v = GetProfile(category).slopedRoofVariants;
            if (v == null || v.Length == 0) return null;
            variantIndex = Mathf.Clamp(variantIndex, 0, v.Length - 1);
            return v[variantIndex].material;
        }
    }
}
