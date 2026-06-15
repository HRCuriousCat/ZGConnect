using System.Collections.Generic;
using Newtonsoft.Json;
using ZGConnect;

namespace ZGConnect.Editor
{
    // ── Heightmap metadata.json ────────────────────────────────────────────────

    public class HeightmapMetadataJson
    {
        [JsonProperty("settings")] public HeightmapSettingsJson Settings;
        [JsonProperty("tiles")]    public List<HeightmapTileJson> Tiles;
    }

    public class HeightmapSettingsJson
    {
        [JsonProperty("resolution")]                  public int Resolution;
        [JsonProperty("tile_size_meters")]            public int TileSizeMeters;
        [JsonProperty("min_height")]                  public float MinHeight;
        [JsonProperty("max_height")]                  public float MaxHeight;
        [JsonProperty("terrain_height")]              public float TerrainHeight;
        [JsonProperty("unity_origin_x")]              public int UnityOriginX;
        [JsonProperty("unity_origin_y")]              public int UnityOriginY;
        [JsonProperty("max_invalid_ratio_to_export")] public float MaxInvalidRatioToExport;
    }

    public class HeightmapTileJson
    {
        [JsonProperty("raw_file")]           public string RawFile;
        [JsonProperty("left")]               public int Left;
        [JsonProperty("bottom")]             public int Bottom;
        [JsonProperty("right")]              public int Right;
        [JsonProperty("top")]                public int Top;
        [JsonProperty("unity_position")]     public TilePositionJson UnityPosition;
        [JsonProperty("terrain_size")]       public TerrainSizeJson TerrainSize;
        [JsonProperty("heightmap_resolution")] public int HeightmapResolution;
        [JsonProperty("invalid_ratio")]      public float InvalidRatio;
        [JsonProperty("global_min_height")]  public float GlobalMinHeight;
        [JsonProperty("global_max_height")]  public float GlobalMaxHeight;
    }

    // ── Ortho/basemap metadata.json ────────────────────────────────────────────

    public class OrthoMetadataJson
    {
        [JsonProperty("settings")] public OrthoSettingsJson Settings;
        [JsonProperty("tiles")]    public List<OrthoTileJson> Tiles;
    }

    public class OrthoSettingsJson
    {
        [JsonProperty("texture_resolution")] public int TextureResolution;
        [JsonProperty("file_extension")]     public string FileExtension;
        [JsonProperty("crs")]                public string Crs;
        [JsonProperty("tile_size_meters")]   public float TileSizeMeters;
        [JsonProperty("max_invalid_ratio")]  public float MaxInvalidRatio;
    }

    public class OrthoTileJson
    {
        [JsonProperty("texture_file")] public string TextureFile;
        [JsonProperty("left")]         public int Left;
        [JsonProperty("bottom")]       public int Bottom;
        [JsonProperty("right")]        public int Right;
        [JsonProperty("top")]          public int Top;
        [JsonProperty("invalid_ratio")] public float InvalidRatio;
        [JsonProperty("status")]       public string Status;
    }

    // Buildings metadata: see Runtime/BuildingMetadataJson.cs (ZGConnect namespace).

    // ── Tiled/Splatmap metadata.json ──────────────────────────────────────────

    public class TiledMetadataJson
    {
        [JsonProperty("type")]     public string Type;
        [JsonProperty("settings")] public TiledSettingsJson Settings;
        [JsonProperty("layers")]   public List<LayerDefinitionJson> Layers;
        [JsonProperty("tiles")]    public List<SplatmapTileJson> Tiles;
    }

    public class TiledSettingsJson
    {
        [JsonProperty("tile_size_meters")] public float TileSizeMeters;
        [JsonProperty("crs")]              public string Crs;
    }

    public class LayerDefinitionJson
    {
        [JsonProperty("id")]               public string Id;
        [JsonProperty("texture_file")]     public string TextureFile;
        [JsonProperty("tile_size_meters")] public float TileSizeMeters;
    }

    public class SplatmapTileJson
    {
        [JsonProperty("left")]      public int Left;
        [JsonProperty("bottom")]    public int Bottom;
        [JsonProperty("right")]     public int Right;
        [JsonProperty("top")]       public int Top;
        [JsonProperty("splatmaps")] public List<string> Splatmaps;
    }

    // ── Shared ─────────────────────────────────────────────────────────────────

    public class TerrainSizeJson
    {
        [JsonProperty("x")] public float X;
        [JsonProperty("y")] public float Y;
        [JsonProperty("z")] public float Z;
    }
}
