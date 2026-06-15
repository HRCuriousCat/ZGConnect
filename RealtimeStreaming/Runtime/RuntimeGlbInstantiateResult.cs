using GLTFast;
using UnityEngine;

namespace ZGConnect.RealtimeStreaming
{
    public sealed class RuntimeGlbInstantiateResult
    {
        public bool Success;
        public GameObject Root;
        public GltfImport Import;
        public RuntimeBuildingPrepareResult Prepared;
        public string TileId;
        public BuildingLodStorageMode LodMode;
        public BuildingsMetadataJson Metadata;
    }
}

