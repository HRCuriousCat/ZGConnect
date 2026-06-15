using UnityEngine;

namespace ZGConnect
{
    [DisallowMultipleComponent]
    public class CityTile : MonoBehaviour
    {
        public string tileId;

        public int left;
        public int bottom;
        public int right;
        public int top;

        public Vector3 unityPosition;

        public Terrain terrain;
        public TerrainCollider terrainCollider;

        [Header("Runtime State")]
        public bool terrainLoaded = true;
        public bool basemapAssigned = false;
        public bool buildingsLoaded = false;

        private void Reset()
        {
            terrain = GetComponent<Terrain>();
            terrainCollider = GetComponent<TerrainCollider>();
        }
    }
}
