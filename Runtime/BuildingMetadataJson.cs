using System.Collections.Generic;
using Newtonsoft.Json;

namespace ZGConnect
{
    /// <summary>
    /// Companion JSON for buildings_{tileId}.glb (geometry + material slot keys + OSM match).
    /// </summary>
    public class BuildingsMetadataJson
    {
        [JsonProperty("tileId")]          public string              TileId;
        [JsonProperty("tileSizeMeters")]  public int                 TileSizeMeters;
        [JsonProperty("tileOriginUnity")] public TilePositionJson    TileOriginUnity;
        [JsonProperty("buildingCount")]   public int                 BuildingCount;
        [JsonProperty("buildings")]       public List<BuildingEntryJson> Buildings;

        [JsonProperty("surfaceBaked")]           public bool SurfaceBaked;
        [JsonProperty("surfacePipelineVersion")] public int  SurfacePipelineVersion;

        /// <summary>When true, roof submeshes use tile-space UVs; runtime applies tile orthophoto.</summary>
        [JsonProperty("roofOrthophotoUv")]      public bool   RoofOrthophotoUv;
        [JsonProperty("roofOrthophotoBasemapId")] public string RoofOrthophotoBasemapId;
    }

    public class BuildingEntryJson
    {
        [JsonProperty("name")]          public string          Name;
        [JsonProperty("localPosition")] public TilePositionJson LocalPosition;
        [JsonProperty("localRotation")] public TilePositionJson LocalRotation;
        [JsonProperty("localScale")]    public TilePositionJson LocalScale;

        [JsonProperty("gps")] public BuildingGpsJson Gps;
        [JsonProperty("osm")] public BuildingOsmBlockJson Osm;

        /// <summary>Material slot keys per submesh (single-mesh buildings).</summary>
        [JsonProperty("materialSlots")] public List<string> MaterialSlots;

        /// <summary>Per-renderer slot keys when a building has multiple MeshRenderers.</summary>
        [JsonProperty("meshMaterials")] public List<BuildingMeshMaterialsJson> MeshMaterials;
    }

    public class BuildingGpsJson
    {
        [JsonProperty("lat")] public double Lat;
        [JsonProperty("lon")] public double Lon;
    }

    public class BuildingOsmBlockJson
    {
        [JsonProperty("coordSource")] public string CoordSource;
        [JsonProperty("queryLat")]    public double QueryLat;
        [JsonProperty("queryLon")]    public double QueryLon;
        [JsonProperty("building")]    public BuildingOsmMatchJson Building;
        [JsonProperty("atPoint")]     public List<BuildingOsmAtPointJson> AtPoint;
    }

    public class BuildingOsmMatchJson
    {
        [JsonProperty("osmType")]      public string OsmType;
        [JsonProperty("osmId")]        public long   OsmId;
        [JsonProperty("role")]         public string Role;
        [JsonProperty("label")]        public string Label;
        [JsonProperty("matchMethod")]  public string MatchMethod;
        [JsonProperty("matchScore")]   public float  MatchScore;
        [JsonProperty("distanceM")]    public float  DistanceM;
        [JsonProperty("tags")]         public Dictionary<string, string> Tags;
    }

    public class BuildingOsmAtPointJson
    {
        [JsonProperty("osmType")] public string OsmType;
        [JsonProperty("osmId")]   public long   OsmId;
        [JsonProperty("role")]    public string Role;
        [JsonProperty("label")]   public string Label;
        [JsonProperty("areaM2")]  public float  AreaM2;
        [JsonProperty("tags")]    public Dictionary<string, string> Tags;
    }

    public class BuildingMeshMaterialsJson
    {
        /// <summary>Empty = MeshRenderer on building root; otherwise child path (e.g. "LOD0").</summary>
        [JsonProperty("rendererPath")]  public string RendererPath;
        [JsonProperty("materialSlots")] public List<string> MaterialSlots;
    }

    public class TilePositionJson
    {
        [JsonProperty("x")] public float X;
        [JsonProperty("y")] public float Y;
        [JsonProperty("z")] public float Z;
    }
}
