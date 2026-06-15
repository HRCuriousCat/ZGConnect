using UnityEditor;
using UnityEngine;

namespace ZGConnect.Editor
{
    [CustomEditor(typeof(BasemapController))]
    public class ZGConnectBasemapControllerEditor : UnityEditor.Editor
    {
        private float _transitionDuration = 1.0f;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Basemap Switching", EditorStyles.boldLabel);

            var controller = (BasemapController)target;
            CityDataset dataset = GetDataset(controller);

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Enter Play mode to switch basemaps.",
                    MessageType.Info);
                return;
            }

            if (dataset == null)
            {
                EditorGUILayout.HelpBox("Assign a CityDataset to enable switching.", MessageType.Warning);
                return;
            }

            if (dataset.availableBasemaps == null || dataset.availableBasemaps.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No basemaps registered in the dataset. " +
                    "Re-import with at least one basemap source checked.",
                    MessageType.Warning);
                return;
            }

            // Active basemap
            string active = string.IsNullOrEmpty(controller.ActiveBasemapId)
                ? "— (import default)"
                : controller.ActiveBasemapId;
            EditorGUILayout.LabelField("Active", active, EditorStyles.boldLabel);

            EditorGUILayout.Space(4);

            _transitionDuration = EditorGUILayout.FloatField("Transition (s)", _transitionDuration);
            _transitionDuration = Mathf.Max(0f, _transitionDuration);

            EditorGUILayout.Space(4);

            // One button per basemap type
            using (new EditorGUILayout.HorizontalScope())
            {
                foreach (var def in dataset.availableBasemaps)
                {
                    bool isCurrent = def.basemapId == controller.ActiveBasemapId;

                    GUI.backgroundColor = isCurrent
                        ? new Color(0.3f, 0.8f, 0.3f)
                        : Color.white;

                    string label = string.IsNullOrEmpty(def.displayName)
                        ? def.basemapId
                        : def.displayName;

                    using (new EditorGUI.DisabledScope(isCurrent))
                    {
                        if (GUILayout.Button(label, GUILayout.Height(28)))
                        {
                            if (_transitionDuration > 0f)
                                controller.TransitionTo(def.basemapId, _transitionDuration);
                            else
                                controller.SwitchTo(def.basemapId);
                        }
                    }
                }

                GUI.backgroundColor = Color.white;
            }

            // Keep inspector live while a transition might be running
            Repaint();
        }

        private static CityDataset GetDataset(BasemapController controller)
        {
            SerializedObject   so   = new SerializedObject(controller);
            SerializedProperty prop = so.FindProperty("dataset");
            return prop?.objectReferenceValue as CityDataset;
        }
    }
}
