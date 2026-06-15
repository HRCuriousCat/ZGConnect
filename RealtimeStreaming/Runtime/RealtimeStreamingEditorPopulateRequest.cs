#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect.RealtimeStreaming
{
    public sealed class RealtimeStreamingEditorPopulateRequest
    {
        public Vector3 CameraPosition { get; internal set; }
        public IReadOnlyList<RuntimeTileRecord> TerrainTiles { get; internal set; }
        public IReadOnlyList<StreamingTileEntry> BuildingLeaves { get; internal set; }

        public string DatasetRoot { get; internal set; }
        public string ActiveBasemapId { get; internal set; }
        public Material TerrainMaterial { get; internal set; }
        public bool StreamTerrain { get; internal set; }
        public bool StreamBuildings { get; internal set; }
        public bool PreferTerrainBundles { get; internal set; }
        public bool TerrainBundleOnlyMode { get; internal set; }
        public bool FlipHeightmapVertically { get; internal set; }
        public bool DrawInstanced { get; internal set; }
        public int BasemapDistance { get; internal set; }
        public RealtimeBuildingStyle BuildingStyle { get; internal set; }
        public BuildingSurfaceSettings BuildingSurfaceSettings { get; internal set; }
        public Material RoofOrthophotoMaterialTemplate { get; internal set; }
    }
}
#endif
