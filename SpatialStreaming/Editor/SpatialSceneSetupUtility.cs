using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using ZGConnect;
using ZGConnect.SpatialStreaming;

namespace ZGConnect.SpatialStreaming.Editor
{
    public static class SpatialSceneSetupUtility
    {
        public static void EnsureStreamingRigInOpenScenes(
            SpatialGpuResidentRenderingSettings gpuSettings = null,
            BuildingSurfaceSettings buildingSurfaceSettings = null)
        {
            SpatialStreamingController[] existingControllers = Object.FindObjectsByType<SpatialStreamingController>(
                FindObjectsInactive.Include);
            if (existingControllers.Length > 0)
            {
                foreach (SpatialStreamingController existingController in existingControllers)
                {
                    if (existingController.GetComponent<SpatialStreamingDebugHud>() == null)
                        Undo.AddComponent<SpatialStreamingDebugHud>(existingController.gameObject);
                }

                Debug.Log(
                    "[ZGConnect.Spatial] SpatialStreamingController already exists — ensured debug HUD component.");
                return;
            }

            var rig = new GameObject("SpatialStreaming");
            rig.AddComponent<SpatialGpuResidentDrawerBootstrap>();
            var controller = rig.AddComponent<SpatialStreamingController>();
            rig.AddComponent<SpatialStreamingDebugHud>();

            if (gpuSettings != null)
            {
                var bootstrap = rig.GetComponent<SpatialGpuResidentDrawerBootstrap>();
                var bootstrapSo = new SerializedObject(bootstrap);
                bootstrapSo.FindProperty("_settings").objectReferenceValue = gpuSettings;
                bootstrapSo.FindProperty("_logStatusOnStart").boolValue = true;
                bootstrapSo.ApplyModifiedPropertiesWithoutUndo();
            }

            if (buildingSurfaceSettings != null)
            {
                var controllerSo = new SerializedObject(controller);
                controllerSo.FindProperty("_buildingSurfaceSettings").objectReferenceValue =
                    buildingSurfaceSettings;
                controllerSo.FindProperty("_applyMaterialsOnLoad").boolValue = true;
                controllerSo.ApplyModifiedPropertiesWithoutUndo();
            }

            Undo.RegisterCreatedObjectUndo(rig, "Create Spatial Streaming Rig");
            Selection.activeGameObject = rig;

            Scene scene = SceneManager.GetActiveScene();
            if (scene.IsValid())
                EditorSceneManager.MarkSceneDirty(scene);
        }
    }
}
