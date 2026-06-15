using UnityEngine;

namespace ZGConnect.RealtimeStreaming
{
    /// <summary>
    /// Marks terrain/building objects populated in the editor (outside Play mode streaming).
    /// </summary>
    public sealed class RealtimeStreamingEditorPopulationRoot : MonoBehaviour
    {
        [SerializeField] RealtimeStreamingController sourceController;

        public RealtimeStreamingController SourceController => sourceController;

        public void Bind(RealtimeStreamingController controller)
        {
            sourceController = controller;
        }
    }
}
