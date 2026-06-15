using UnityEditor;
using UnityEngine;

namespace ZGConnect.RealtimeStreaming.Editor
{
    /// <summary>
    /// Asset-bundle instances in edit mode often keep MonoBehaviours as missing-script slots.
    /// GameObjectUtility alone is not always enough; SerializedObject fallback removes them.
    /// </summary>
    static class RealtimeStreamingEditorMissingScriptUtility
    {
        const int kMaxStripPasses = 8;

        public static void StripHierarchy(GameObject root)
        {
            if (root == null)
                return;

            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
                StripGameObject(transform.gameObject);
        }

        public static void StripGameObject(GameObject gameObject)
        {
            if (gameObject == null)
                return;

            for (int pass = 0; pass < kMaxStripPasses; pass++)
            {
                int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(gameObject);
                if (missing == 0)
                    return;

                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(gameObject);

                if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(gameObject) >= missing)
                    RemoveMissingScriptsViaSerializedObject(gameObject);
            }
        }

        static void RemoveMissingScriptsViaSerializedObject(GameObject gameObject)
        {
            Component[] components = gameObject.GetComponents<Component>();
            SerializedObject serializedObject = new SerializedObject(gameObject);
            SerializedProperty componentProperty = serializedObject.FindProperty("m_Component");
            if (componentProperty == null || !componentProperty.isArray)
                return;

            for (int i = components.Length - 1; i >= 0; i--)
            {
                if (components[i] != null)
                    continue;

                componentProperty.DeleteArrayElementAtIndex(i);
            }

            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
