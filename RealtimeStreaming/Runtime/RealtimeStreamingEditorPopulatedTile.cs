using UnityEngine;

namespace ZGConnect.RealtimeStreaming
{
    public sealed class RealtimeStreamingEditorPopulatedTile : MonoBehaviour
    {
        public enum ContentType
        {
            Terrain = 0,
            Buildings = 1,
        }

        [SerializeField] string tileKey;
        [SerializeField] ContentType contentType;

        public string TileKey => tileKey;
        public ContentType PopulatedContentType => contentType;

        public void Initialize(string key, ContentType type)
        {
            tileKey = key;
            contentType = type;
        }

        void Awake()
        {
            if (!Application.isPlaying || contentType != ContentType.Buildings)
                return;

            EnsureBuildingTileMarkers();
        }

        void EnsureBuildingTileMarkers()
        {
            RuntimeBuildingTileRuntimeState state = GetComponent<RuntimeBuildingTileRuntimeState>();
            if (state == null)
            {
                state = gameObject.AddComponent<RuntimeBuildingTileRuntimeState>();
                state.IsFullyFinalized = transform.Find(RuntimeBuildingTilePostProcessor.CombinedRenderRootName) != null;
            }

            state.ResolveCombinedRenderRoot();
            if (state.CombinedRenderRoot != null)
            {
                state.IsFullyFinalized = true;
                state.SetCombinedRenderVisible(true);
            }

            if (GetComponent<RuntimeBuildingTileBakedMarker>() == null)
                gameObject.AddComponent<RuntimeBuildingTileBakedMarker>();
        }
    }
}
