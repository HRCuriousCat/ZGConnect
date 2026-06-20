using UnityEditor;
using UnityEngine;

namespace ZGConnect.RealtimeStreaming.Editor
{
    /// <summary>
    /// Re-binds tile marker components before Unity clones the scene for Play mode.
    /// Bundle-instanced objects can keep stale missing-script slots that only break on play.
    /// </summary>
    [InitializeOnLoad]
    static class RealtimeStreamingEditorPopulatedTilePlayModeFix
    {
        static RealtimeStreamingEditorPopulatedTilePlayModeFix()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingEditMode)
                return;

            RealtimeStreamingEditorPopulatedTile[] tiles =
                Object.FindObjectsByType<RealtimeStreamingEditorPopulatedTile>(
                    FindObjectsInactive.Include);

            foreach (RealtimeStreamingEditorPopulatedTile tile in tiles)
            {
                if (tile == null || tile.PopulatedContentType != RealtimeStreamingEditorPopulatedTile.ContentType.Buildings)
                    continue;

                RealtimeStreamingBuildBundlePrepareUtility.PrepareVisualInstance(tile.gameObject);
                RealtimeStreamingBuildBundlePrepareUtility.FinalizeEditorPopulatedBuildingTile(tile.gameObject);

                Transform physics = tile.transform.Find($"{tile.gameObject.name}_Physics");
                if (physics != null)
                    RealtimeStreamingBuildBundlePrepareUtility.PreparePhysicsInstance(physics.gameObject);
            }
        }
    }
}
