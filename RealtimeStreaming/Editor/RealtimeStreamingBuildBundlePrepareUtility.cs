using UnityEditor;
using UnityEngine;
using ZGConnect.RealtimeStreaming;

namespace ZGConnect.RealtimeStreaming.Editor
{
    /// <summary>
    /// Prepares building tile instances loaded from AssetBundles for embed-safe prefab rebuild.
    /// </summary>
    static class RealtimeStreamingBuildBundlePrepareUtility
    {
        public static void PrepareVisualInstance(GameObject instance)
        {
            if (instance == null)
                return;

            TileMarkerSnapshot snapshot = CaptureTileMarkerSnapshot(instance, isPhysicsRoot: false);
            RealtimePackBuildingBakeUtility.StripBuildingDataComponents(instance.transform);
            RealtimeStreamingEditorMissingScriptUtility.StripHierarchy(instance);
            RuntimeBuildingTilePostProcessor.StripEmptyBuildingShells(instance.transform);
            RestoreVisualTileMarkers(instance, snapshot);
        }

        public static void PreparePhysicsInstance(GameObject instance)
        {
            if (instance == null)
                return;

            TileMarkerSnapshot snapshot = CaptureTileMarkerSnapshot(instance, isPhysicsRoot: true);
            RealtimePackBuildingBakeUtility.StripBuildingDataComponents(instance.transform);
            RealtimeStreamingEditorMissingScriptUtility.StripHierarchy(instance);
            RestorePhysicsTileMarkers(instance, snapshot);
        }

        sealed class TileMarkerSnapshot
        {
            public string PackBakeFingerprint;
            public string BuildingStyleKey;
            public int FinalizedSettingsFingerprint;
            public bool IsFullyFinalized;
        }

        static TileMarkerSnapshot CaptureTileMarkerSnapshot(GameObject root, bool isPhysicsRoot)
        {
            var snapshot = new TileMarkerSnapshot
            {
                PackBakeFingerprint = ReadSerializedString(root, "PackBakeFingerprint"),
                BuildingStyleKey = ReadSerializedString(root, "BuildingStyleKey"),
                FinalizedSettingsFingerprint = ReadSerializedInt(root, "FinalizedSettingsFingerprint"),
                IsFullyFinalized = ReadSerializedBool(root, "IsFullyFinalized"),
            };

            if (isPhysicsRoot)
            {
                RuntimeBuildingTilePhysicsMarker physicsMarker = root.GetComponent<RuntimeBuildingTilePhysicsMarker>();
                if (physicsMarker != null)
                {
                    snapshot.PackBakeFingerprint ??= physicsMarker.PackBakeFingerprint;
                    snapshot.BuildingStyleKey ??= physicsMarker.BuildingStyleKey;
                }
            }
            else
            {
                RuntimeBuildingTileBakedMarker bakedMarker = root.GetComponent<RuntimeBuildingTileBakedMarker>();
                if (bakedMarker != null)
                {
                    snapshot.PackBakeFingerprint ??= bakedMarker.PackBakeFingerprint;
                    snapshot.BuildingStyleKey ??= bakedMarker.BuildingStyleKey;
                }

                RuntimeBuildingTileRuntimeState state = root.GetComponent<RuntimeBuildingTileRuntimeState>();
                if (state != null)
                {
                    snapshot.PackBakeFingerprint ??= state.PackBakeFingerprint;
                    snapshot.FinalizedSettingsFingerprint = state.FinalizedSettingsFingerprint;
                    snapshot.IsFullyFinalized = state.IsFullyFinalized;
                }
            }

            if (!snapshot.IsFullyFinalized)
                snapshot.IsFullyFinalized = root.transform.Find(RuntimeBuildingTilePostProcessor.CombinedRenderRootName) != null;

            return snapshot;
        }

        static void RestoreVisualTileMarkers(GameObject root, TileMarkerSnapshot snapshot)
        {
            RealtimeStreamingEditorMissingScriptUtility.StripGameObject(root);
            foreach (RuntimeBuildingTileBakedMarker existing in root.GetComponents<RuntimeBuildingTileBakedMarker>())
                Object.DestroyImmediate(existing);
            foreach (RuntimeBuildingTileRuntimeState existing in root.GetComponents<RuntimeBuildingTileRuntimeState>())
                Object.DestroyImmediate(existing);

            RuntimeBuildingTileBakedMarker bakedMarker = root.AddComponent<RuntimeBuildingTileBakedMarker>();
            bakedMarker.PackBakeFingerprint = snapshot.PackBakeFingerprint;
            bakedMarker.BuildingStyleKey = snapshot.BuildingStyleKey;

            RuntimeBuildingTileRuntimeState state = root.AddComponent<RuntimeBuildingTileRuntimeState>();
            state.PackBakeFingerprint = snapshot.PackBakeFingerprint;
            state.FinalizedSettingsFingerprint = snapshot.FinalizedSettingsFingerprint;
            state.IsFullyFinalized = snapshot.IsFullyFinalized;
            state.ResolveCombinedRenderRoot();
            if (state.CombinedRenderRoot != null)
                state.SetCombinedRenderVisible(true);
        }

        static void RestorePhysicsTileMarkers(GameObject root, TileMarkerSnapshot snapshot)
        {
            RealtimeStreamingEditorMissingScriptUtility.StripGameObject(root);
            foreach (RuntimeBuildingTilePhysicsMarker existing in root.GetComponents<RuntimeBuildingTilePhysicsMarker>())
                Object.DestroyImmediate(existing);

            RuntimeBuildingTilePhysicsMarker physicsMarker = root.AddComponent<RuntimeBuildingTilePhysicsMarker>();
            physicsMarker.PackBakeFingerprint = snapshot.PackBakeFingerprint;
            physicsMarker.BuildingStyleKey = snapshot.BuildingStyleKey;
        }

        public static void FinalizeEditorPopulatedBuildingTile(GameObject root)
        {
            if (root == null)
                return;

            root.hideFlags = HideFlags.None;
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
                transform.gameObject.hideFlags = HideFlags.None;

            if (PrefabUtility.IsPartOfAnyPrefab(root))
            {
                PrefabUtility.UnpackPrefabInstance(
                    root,
                    PrefabUnpackMode.Completely,
                    InteractionMode.AutomatedAction);
            }

            EditorUtility.SetDirty(root);
            if (root.scene.IsValid())
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(root.scene);
        }

        static string ReadSerializedString(GameObject gameObject, string propertyName)
        {
            foreach (Component component in gameObject.GetComponents<Component>())
            {
                if (component == null)
                    continue;

                SerializedObject serializedObject = new SerializedObject(component);
                SerializedProperty property = serializedObject.FindProperty(propertyName);
                if (property == null || property.propertyType != SerializedPropertyType.String)
                    continue;

                if (!string.IsNullOrEmpty(property.stringValue))
                    return property.stringValue;
            }

            return null;
        }

        static int ReadSerializedInt(GameObject gameObject, string propertyName)
        {
            foreach (Component component in gameObject.GetComponents<Component>())
            {
                if (component == null)
                    continue;

                SerializedObject serializedObject = new SerializedObject(component);
                SerializedProperty property = serializedObject.FindProperty(propertyName);
                if (property == null || property.propertyType != SerializedPropertyType.Integer)
                    continue;

                if (property.intValue != 0)
                    return property.intValue;
            }

            return 0;
        }

        static bool ReadSerializedBool(GameObject gameObject, string propertyName)
        {
            foreach (Component component in gameObject.GetComponents<Component>())
            {
                if (component == null)
                    continue;

                SerializedObject serializedObject = new SerializedObject(component);
                SerializedProperty property = serializedObject.FindProperty(propertyName);
                if (property == null || property.propertyType != SerializedPropertyType.Boolean)
                    continue;

                if (property.boolValue)
                    return true;
            }

            return false;
        }
    }
}
