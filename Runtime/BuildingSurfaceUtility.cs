using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect
{
    public static class BuildingSurfaceUtility
    {
        public const int SaltFacade       = unchecked((int)0x7A3B1C2Du);
        public const int SaltFlatRoof     = unchecked((int)0x8C4D2E1Fu);
        public const int SaltSlopedRoof   = unchecked((int)0x9D5E3F20u);

        public static int StableHash(string s)
        {
            unchecked
            {
                int h = 23;
                if (string.IsNullOrEmpty(s)) return h;
                foreach (char c in s)
                    h = h * 31 + c;
                return h;
            }
        }

        /// <summary>
        /// Stable per-building seed for variant picking (survives reloads).
        /// Uses world-space footprint center + instance id so GLB nodes with local (0,0,0) still differ.
        /// </summary>
        public static string ComposeVariantSeedForBuilding(GameObject building, string buildingId)
        {
            if (building == null)
                return buildingId ?? string.Empty;

            Vector3 world = building.transform.position;
            Renderer[] renderers = building.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length > 0)
            {
                Bounds b = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++)
                    b.Encapsulate(renderers[i].bounds);
                if (b.size.sqrMagnitude > 1e-8f)
                    world = b.center;
            }

            return ComposeVariantSeed(
                buildingId,
                building.name,
                world,
                building.GetInstanceID());
        }

        /// <summary>
        /// Quantized world position + optional Unity instance id for uniqueness within a tile.
        /// </summary>
        public static string ComposeVariantSeed(
            string buildingId,
            string objectName,
            Vector3 worldPosition,
            int unityInstanceId = 0)
        {
            int qx = Mathf.RoundToInt(worldPosition.x * 100f);
            int qy = Mathf.RoundToInt(worldPosition.y * 10f);
            int qz = Mathf.RoundToInt(worldPosition.z * 100f);
            string inst = unityInstanceId != 0 ? unityInstanceId.ToString() : "0";
            return $"{buildingId ?? ""}|{objectName ?? ""}|{inst}|{qx},{qy},{qz}";
        }

        /// <summary>
        /// Picks a variant index in [0, variantCount) from a stable seed and salt.
        /// Salt separates facade vs roof; include category in salt at call site when needed.
        /// </summary>
        public static int VariantIndex(string variantSeed, int variantCount, int salt = 0)
        {
            if (variantCount <= 1) return 0;
            int h = MixHash(StableHash(variantSeed), salt);
            return (h & int.MaxValue) % variantCount;
        }

        private static int MixHash(int hash, int salt)
        {
            unchecked
            {
                uint x = (uint)hash ^ (uint)salt;
                x ^= x >> 16;
                x *= 0x7feb352d;
                x ^= x >> 15;
                x *= 0x846ca68b;
                x ^= x >> 16;
                return (int)x;
            }
        }

        public static int CategoryVariantSalt(BuildingCategory category) =>
            ((int)category + 1) * 0x2c1b3c6d;

        public static int FloorCountFromHeight(float heightMeters, float metersPerFloor)
        {
            if (heightMeters <= 0.01f) return 1;
            return Mathf.Max(1, Mathf.RoundToInt(heightMeters / Mathf.Max(0.5f, metersPerFloor)));
        }

        public static BuildingCategory CategoryFromFloorCount(int floors)
        {
            if (floors <= 3) return BuildingCategory.House;
            if (floors <= 7) return BuildingCategory.MidRise;
            return BuildingCategory.HighRise;
        }

        public static BuildingCategory ResolveCategory(
            float buildingHeightMeters,
            float metersPerFloor,
            int enrichedFloors = 0)
        {
            int floors = enrichedFloors > 0
                ? enrichedFloors
                : FloorCountFromHeight(buildingHeightMeters, metersPerFloor);
            return CategoryFromFloorCount(floors);
        }

        /// <summary>
        /// Maps embedded GLB material names to facade/roof profile buckets.
        /// neboder → HighRise, building → MidRise, house → House (case-insensitive substring).
        /// </summary>
        /// <summary>
        /// E.g. "zagreb_Part_1619" → "1619", "zagreb_Part_1619 1" → "1619".
        /// </summary>
        public static string ExtractBuildingIdFromObjectName(string objectName)
        {
            if (string.IsNullOrEmpty(objectName))
                return objectName;

            string name = objectName;

            int spaceIdx = name.LastIndexOf(' ');
            if (spaceIdx > 0 && spaceIdx < name.Length - 1)
            {
                if (int.TryParse(name.Substring(spaceIdx + 1), out _))
                    name = name.Substring(0, spaceIdx);
            }

            int usIdx = name.LastIndexOf('_');
            if (usIdx >= 0 && usIdx < name.Length - 1)
            {
                string candidate = name.Substring(usIdx + 1);
                if (int.TryParse(candidate, out _))
                    return candidate;
            }

            return name;
        }

        public static string NormalizeMaterialName(string materialName)
        {
            if (string.IsNullOrEmpty(materialName))
                return materialName;

            const string instanceSuffix = " (Instance)";
            if (materialName.EndsWith(instanceSuffix, System.StringComparison.Ordinal))
                return materialName.Substring(0, materialName.Length - instanceSuffix.Length);

            return materialName;
        }

        public static bool TryResolveCategoryFromMaterialName(
            string materialName,
            out BuildingCategory category)
        {
            category = BuildingCategory.House;
            materialName = NormalizeMaterialName(materialName);
            if (string.IsNullOrEmpty(materialName))
                return false;

            string n = materialName.ToLowerInvariant();
            if (n.Contains("neboder"))
            {
                category = BuildingCategory.HighRise;
                return true;
            }

            if (n.Contains("building"))
            {
                category = BuildingCategory.MidRise;
                return true;
            }

            if (n.Contains("house"))
            {
                category = BuildingCategory.House;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Highest-priority category found across material names (neboder &gt; building &gt; house).
        /// </summary>
        public static BuildingCategory ResolveCategoryFromMaterialNames(
            IEnumerable<Material> materials)
        {
            if (materials == null)
                return BuildingCategory.House;

            bool sawHouse = false;
            bool sawBuilding = false;

            foreach (Material mat in materials)
            {
                if (mat == null)
                    continue;

                if (TryResolveCategoryFromMaterialName(mat.name, out BuildingCategory cat))
                {
                    if (cat == BuildingCategory.HighRise)
                        return BuildingCategory.HighRise;
                    if (cat == BuildingCategory.MidRise)
                        sawBuilding = true;
                    else if (cat == BuildingCategory.House)
                        sawHouse = true;
                }
            }

            if (sawBuilding)
                return BuildingCategory.MidRise;
            if (sawHouse)
                return BuildingCategory.House;
            return BuildingCategory.House;
        }

        /// <summary>
        /// Maps GLB material names to surface slots: facade, roof_flat, roof_sloped.
        /// </summary>
        public static bool TryResolveSurfaceTypeFromMaterialName(
            string materialName,
            out BuildingSurfaceMaterialType surfaceType)
        {
            surfaceType = BuildingSurfaceMaterialType.Facade;
            materialName = NormalizeMaterialName(materialName);
            if (string.IsNullOrEmpty(materialName))
                return false;

            string n = materialName.ToLowerInvariant();

            if (n.Contains("roof_sloped") || n.Contains("roof sloped")
                || n.Contains("sloped_roof") || n.Contains("roofsloped"))
            {
                surfaceType = BuildingSurfaceMaterialType.RoofSloped;
                return true;
            }

            if (n.Contains("roof_flat") || n.Contains("roof flat")
                || n.Contains("flat_roof") || n.Contains("roofflat"))
            {
                surfaceType = BuildingSurfaceMaterialType.RoofFlat;
                return true;
            }

            if (n.Contains("facade"))
            {
                surfaceType = BuildingSurfaceMaterialType.Facade;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Resolves canonical JSON slot keys (e.g. house_facade, building_roof_flat).
        /// Falls back to legacy GLB material name parsing.
        /// </summary>
        public static bool TryResolveCategoryAndSurfaceFromSlotKey(
            string slotKey,
            out BuildingCategory category,
            out BuildingSurfaceMaterialType surfaceType)
        {
            category = BuildingCategory.House;
            surfaceType = BuildingSurfaceMaterialType.Facade;
            if (string.IsNullOrWhiteSpace(slotKey))
                return false;

            string key = slotKey.Trim().ToLowerInvariant();

            if (key.StartsWith("neboder_"))
                category = BuildingCategory.HighRise;
            else if (key.StartsWith("building_"))
                category = BuildingCategory.MidRise;
            else if (key.StartsWith("house_"))
                category = BuildingCategory.House;
            else if (!TryResolveCategoryFromMaterialName(slotKey, out category))
                return false;

            if (key.Contains("roof_sloped"))
                surfaceType = BuildingSurfaceMaterialType.RoofSloped;
            else if (key.Contains("roof_flat"))
                surfaceType = BuildingSurfaceMaterialType.RoofFlat;
            else if (key.Contains("facade"))
                surfaceType = BuildingSurfaceMaterialType.Facade;
            else if (!TryResolveSurfaceTypeFromMaterialName(slotKey, out surfaceType))
                return false;

            return true;
        }

        /// <summary>
        /// Stable slot key for JSON (e.g. house_facade) when the source name is parseable.
        /// </summary>
        public static string CanonicalizeSlotKey(string materialName)
        {
            if (string.IsNullOrEmpty(materialName))
                return materialName;

            if (!TryResolveCategoryFromMaterialName(materialName, out BuildingCategory category) ||
                !TryResolveSurfaceTypeFromMaterialName(materialName, out BuildingSurfaceMaterialType surfaceType))
                return materialName;

            return CategoryToSlotToken(category) + "_" + SurfaceToSlotToken(surfaceType);
        }

        public static string CategoryToSlotToken(BuildingCategory category) =>
            category switch
            {
                BuildingCategory.HighRise => "neboder",
                BuildingCategory.MidRise  => "building",
                _                       => "house",
            };

        public static string SurfaceToSlotToken(BuildingSurfaceMaterialType surfaceType) =>
            surfaceType switch
            {
                BuildingSurfaceMaterialType.RoofFlat   => "roof_flat",
                BuildingSurfaceMaterialType.RoofSloped => "roof_sloped",
                _                                      => "facade",
            };
    }
}
