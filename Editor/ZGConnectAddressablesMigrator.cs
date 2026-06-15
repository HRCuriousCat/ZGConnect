using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace ZGConnect.Editor
{
    /// <summary>
    /// One-shot migration tool: converts existing CityDataset direct asset references
    /// to Addressables, eliminating the eager-loading freeze.
    ///
    /// After migration every tile has:
    ///   terrainDataRef             populated  (Addressable key = "zgconnect/terrain/{tileId}")
    ///   terrainData                null       (no longer triggers eager load)
    ///   primaryBasemapTexture      null       (redundant — embedded in TerrainData.terrainLayers)
    ///   primaryTerrainLayer        null       (same reason)
    ///   basemapLayers[*].texture   null
    ///   basemapLayers[*].terrainLayer null
    ///
    /// Safe to run multiple times — tiles already migrated are skipped.
    /// </summary>
    public static class ZGConnectAddressablesMigrator
    {
        public const string GroupName   = "ZGConnect Terrain";
        public const string AddressRoot = "zgconnect/terrain/";

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Migrates all tiles in <paramref name="dataset"/> to Addressables.
        /// Returns the number of tiles newly migrated.
        /// </summary>
        public static int MigrateDataset(CityDataset dataset)
        {
            if (dataset == null)
            {
                Debug.LogError("[ZGConnect Addressables] No dataset provided.");
                return 0;
            }

            AddressableAssetSettings settings = EnsureAddressableSettings();
            AddressableAssetGroup    group    = EnsureGroup(settings);

            int migrated = 0;
            int already  = 0;
            int missing  = 0;

            for (int i = 0; i < dataset.tiles.Count; i++)
            {
                CityTileRecord rec = dataset.tiles[i];

                bool cancelled = EditorUtility.DisplayCancelableProgressBar(
                    "ZGConnect — Addressables Migration",
                    $"Tile {rec.tileId}  ({i + 1} / {dataset.tiles.Count})",
                    (float)(i + 1) / dataset.tiles.Count);

                if (cancelled) break;

                // terrainDataRef already valid → just clean up redundant refs
                if (rec.terrainDataRef != null && rec.terrainDataRef.RuntimeKeyIsValid())
                {
                    NullRedundantRefs(rec);
                    already++;
                    continue;
                }

                // Locate the TerrainData asset
                string tdPath = FindTerrainDataPath(rec);
                if (string.IsNullOrEmpty(tdPath))
                {
                    Debug.LogWarning(
                        $"[ZGConnect Addressables] TerrainData not found for tile '{rec.tileId}' — skipped.");
                    missing++;
                    continue;
                }

                string guid = AssetDatabase.AssetPathToGUID(tdPath);

                // Register as Addressable in the ZGConnect Terrain group
                AddressableAssetEntry entry = settings.CreateOrMoveEntry(guid, group, false, false);
                entry.address = AddressRoot + rec.tileId;

                // Populate the reference field
                rec.terrainDataRef = new AssetReferenceT<TerrainData>(guid);

                // Clear the legacy direct reference (was the cause of the freeze)
                rec.terrainData = null;

                // Clear all other eager-loading direct references
                NullRedundantRefs(rec);

                migrated++;
            }

            EditorUtility.ClearProgressBar();

            // Notify Addressables that entries changed
            settings.SetDirty(
                AddressableAssetSettings.ModificationEvent.EntryMoved,
                null, true);

            EditorUtility.SetDirty(dataset);
            AssetDatabase.SaveAssets();

            Debug.Log(
                $"[ZGConnect Addressables] Migration complete — " +
                $"migrated: {migrated}  already done: {already}  missing TerrainData: {missing}");

            return migrated;
        }

        /// <summary>
        /// Returns true if all tiles in the dataset have a valid <c>terrainDataRef</c>.
        /// </summary>
        public static bool IsFullyMigrated(CityDataset dataset)
        {
            if (dataset?.tiles == null || dataset.tiles.Count == 0) return false;
            foreach (var t in dataset.tiles)
                if (t.terrainDataRef == null || !t.terrainDataRef.RuntimeKeyIsValid())
                    return false;
            return true;
        }

        /// <summary>
        /// Returns the count of tiles that still have a direct <c>terrainData</c> reference
        /// but no <c>terrainDataRef</c>. Used by the Dataset Manager to show migration status.
        /// </summary>
        public static (int migrated, int total) MigrationStatus(CityDataset dataset)
        {
            if (dataset?.tiles == null) return (0, 0);
            int m = 0;
            foreach (var t in dataset.tiles)
                if (t.terrainDataRef != null && t.terrainDataRef.RuntimeKeyIsValid())
                    m++;
            return (m, dataset.tiles.Count);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string FindTerrainDataPath(CityTileRecord rec)
        {
            // Primary: convention path (importer always creates assets here)
            string convention = $"Assets/Generated/ZGConnect/TerrainData/Tile_{rec.tileId}.asset";
            if (AssetDatabase.LoadAssetAtPath<TerrainData>(convention) != null)
                return convention;

            // Fallback: legacy direct reference (present before migration)
            if (rec.terrainData != null)
            {
                string path = AssetDatabase.GetAssetPath(rec.terrainData);
                if (!string.IsNullOrEmpty(path)) return path;
            }

            return null;
        }

        private static void NullRedundantRefs(CityTileRecord rec)
        {
            // primaryBasemapTexture and primaryTerrainLayer are accessible at runtime via
            // TerrainData.terrainLayers (loaded transitively when TerrainData loads).
            // As direct references they force eager texture loading — must be null.
            rec.primaryBasemapTexture = null;
            rec.primaryTerrainLayer   = null;

            if (rec.basemapLayers != null)
            {
                foreach (var entry in rec.basemapLayers)
                {
                    // basemapId string kept — needed for basemap switching identification.
                    entry.texture      = null;
                    entry.terrainLayer = null;
                }
            }
        }

        private static AddressableAssetSettings EnsureAddressableSettings()
        {
            AddressableAssetSettings s = AddressableAssetSettingsDefaultObject.Settings;
            if (s != null) return s;

            // First run after installing Addressables — create defaults
            s = AddressableAssetSettings.Create(
                AddressableAssetSettingsDefaultObject.kDefaultConfigFolder,
                AddressableAssetSettingsDefaultObject.kDefaultConfigAssetName,
                true, true);

            AddressableAssetSettingsDefaultObject.Settings = s;
            return s;
        }

        /// <summary>
        /// Returns the ZGConnect Terrain Addressable group, creating it (with proper schemas)
        /// if it doesn't exist yet. Safe to call from any editor context.
        /// </summary>
        public static AddressableAssetGroup GetOrCreateGroup(AddressableAssetSettings settings)
            => EnsureGroup(settings);

        private static AddressableAssetGroup EnsureGroup(AddressableAssetSettings settings)
        {
            AddressableAssetGroup group = settings.FindGroup(GroupName);

            if (group != null)
            {
                // If BundledAssetGroupSchema is missing the group is broken —
                // delete it so we recreate it cleanly rather than trying to patch
                // a potentially inconsistent state.
                if (group.GetSchema<BundledAssetGroupSchema>() == null)
                {
                    Debug.Log($"[ZGConnect Addressables] Group '{GroupName}' has no schemas — " +
                              "removing and recreating.");
                    settings.RemoveGroup(group);
                    group = null;
                }
                else
                {
                    return group;  // healthy
                }
            }

            // Create by copying schemas from the Default Local Group.
            // This inherits Build/Load paths, compression, and all other settings
            // automatically — more reliable than adding schema types manually.
            var defaultGroup = settings.DefaultGroup;
            group = settings.CreateGroup(
                GroupName,
                false,  // setAsDefaultGroup
                false,  // readOnly
                true,   // postEvent — marks settings dirty immediately
                defaultGroup?.Schemas);

            // Safety net: if default group had no schemas either, add BundledAssetGroupSchema
            if (group.GetSchema<BundledAssetGroupSchema>() == null)
            {
                group.AddSchema<BundledAssetGroupSchema>();
                Debug.Log($"[ZGConnect Addressables] Added BundledAssetGroupSchema to '{GroupName}'.");
            }
            if (group.GetSchema<ContentUpdateGroupSchema>() == null)
                group.AddSchema<ContentUpdateGroupSchema>();

            Debug.Log($"[ZGConnect Addressables] Created Addressable group '{GroupName}' " +
                      $"with {group.Schemas.Count} schema(s).");
            return group;
        }
    }
}
