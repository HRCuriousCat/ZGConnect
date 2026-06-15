# ZGConnect Toolkit — Developer Guide

`ZGConnectToolkit` is a single drop-in component that bundles the most common helper
operations for building apps and games on top of the ZGConnect city plugin:
coordinate conversions, terrain queries, building search and metadata, highlighting,
camera navigation and streaming utilities — all behind one API.

It is designed for rapid prototyping (hackathons, demos): add one component, then call
`ZGConnectToolkit.Instance` from anywhere.

---

## 1. Setup

1. Open a ZGConnect scene (e.g. `Runtime_Streaming`).
2. Create an empty GameObject and add **ZG Connect / ZG Connect Toolkit**
   (or just call `ZGConnectToolkit.Instance` in play mode — the component creates itself).
3. Done. All references (camera, streaming controllers, building interaction) resolve
   automatically. Assign them in the inspector only if you need to override the defaults.

```csharp
using ZGConnect;
using UnityEngine;

public class Example : MonoBehaviour
{
    void Start()
    {
        var zg = ZGConnectToolkit.Instance;
        zg.TeleportToGps(45.8131, 15.9772); // Trg bana Jelačića
    }
}
```

### Inspector

| Section | Purpose |
|---|---|
| **References** | Camera + controllers. Leave empty for auto-resolve. |
| **Navigation** | The transform moved by `TeleportTo` / `FlyTo`. Defaults to the camera; point it at your player rig if needed. |
| **Highlight** | Default color / outline width for `HighlightBuilding`. |
| **Scene Gizmos** | Draw dataset bounds, the 1 km tile grid and the world origin in the Scene view. |
| **Live Dashboard** (play mode) | Camera GPS, elevation, current tile, streaming status, loaded tile counts, building under cursor. |
| **Debug Tools** (play mode) | Teleport/fly/prewarm to lat-lon, copy camera GPS, open Google Maps, log buildings in radius, log loaded tiles, clear highlights. |

---

## 2. Coordinates & GPS

ZGConnect maps Unity world space 1:1 to metres. The chain is
`Unity ↔ EPSG:3765 (HTRS96 / Croatia TM) ↔ WGS84 (GPS)`.

| Function | Description |
|---|---|
| `WorldToGps(Vector3) → (lat, lon)` | World position → GPS decimal degrees. *(static)* |
| `GpsToWorld(lat, lon, heightAboveGround = 0)` | GPS → world position, snapped to terrain when loaded. |
| `GpsToWorldFlat(lat, lon, unityY = 0)` | GPS → world position with explicit Y (no terrain lookup). *(static)* |
| `WorldToEpsg3765(Vector3) → (E, N)` | World → official Croatian grid coordinates. *(static)* |
| `Epsg3765ToWorld(E, N, unityY = 0)` | Croatian grid → world. *(static)* |
| `UnityYToElevation(float)` | Unity Y → metres above sea level. *(static)* |
| `TryGetGroundElevation(Vector3, out float)` | Terrain elevation (m a.s.l.) at a point. |
| `GpsDistanceMeters(lat1, lon1, lat2, lon2)` | Haversine distance between GPS points. *(static)* |
| `HorizontalDistanceMeters(a, b)` | XZ distance between world points, in metres. *(static)* |
| `BearingDegrees(from, to)` | Compass bearing 0–360° (0 = north) between world points. *(static)* |
| `GpsBearingDegrees(...)` | Compass bearing between GPS points. *(static)* |
| `MeasurePathDistance(IList<Vector3>)` | Total polyline length in metres. *(static)* |
| `IsGpsInsidePolygon(lat, lon, polygon)` | Geofence test. Polygon vertices = `Vector2(lat, lon)`. *(static)* |
| `IsWorldPosInsideGpsPolygon(pos, polygon)` | Geofence test for a world position. *(static)* |
| `FormatGps(lat, lon)` / `FormatGps(Vector3)` | `"45.81234° N   15.97654° E"`. *(static)* |
| `GetGoogleMapsUrl(...)` / `GetOpenStreetMapUrl(...)` | Share/debug links. *(static)* |
| `IsInsideDataset(Vector3)` / `IsInsideDatasetGps(lat, lon)` | Is the point covered by the dataset? |
| `TryGetDatasetBounds(out Bounds)` | World-space bounds of the whole dataset. |

```csharp
// Compass arrow pointing to a goal
float bearing = ZGConnectToolkit.BearingDegrees(player.position, goal.position);
needle.rotation = Quaternion.Euler(0, 0, -bearing);

// "You are 320 m from the target"
double d = ZGConnectToolkit.GpsDistanceMeters(lat1, lon1, lat2, lon2);

// Capture-the-flag zone over the city centre
var zone = new List<Vector2> {
    new(45.8150f, 15.9700f), new(45.8150f, 15.9850f),
    new(45.8090f, 15.9850f), new(45.8090f, 15.9700f),
};
bool inZone = ZGConnectToolkit.IsWorldPosInsideGpsPolygon(player.position, zone);
```

---

## 3. Terrain

| Function | Description |
|---|---|
| `TryGetGroundHeight(Vector3, out float)` | Terrain surface Y at a position. |
| `TryGetGroundHeightAtGps(lat, lon, out float)` | Same, for a GPS coordinate. |
| `SnapToGround(Vector3, heightOffset = 0)` | Returns the position projected onto the terrain. |
| `GetGroundNormal(Vector3)` | Terrain surface normal (Vector3.up fallback). |
| `GetSlopeAngle(Vector3)` | Slope in degrees (0 = flat). |
| `GetTileIdAt(Vector3)` | Tile id covering the position, e.g. `"550000_5068000"`. |
| `IsTileLoadedAt(Vector3)` | Is the leaf terrain tile streamed in at full resolution? |
| `IsTerrainReadyAt(Vector3)` | Is any terrain surface (incl. HLOD) present there? |
| `TryGetRandomPointOnGround(center, radius, out pos, avoidBuildings)` | Random spawn point on terrain, optionally rejecting points on buildings. |

```csharp
// Spawn a pickup on the ground near the player, never on a roof
if (zg.TryGetRandomPointOnGround(player.position, 150f, out Vector3 spawn, avoidBuildings: true))
    Instantiate(pickupPrefab, spawn + Vector3.up * 0.5f, Quaternion.identity);

// Placement rule: no buildings on slopes steeper than 25°
bool buildable = zg.GetSlopeAngle(cursorPos) < 25f;
```

---

## 4. Buildings

All building queries return `ZGBuildingHandle` — a lightweight wrapper that works in both
runtime modes (streamed building tiles *and* scenes with `BuildingData` components).
Metadata (address, OSM tags, floors, year...) loads lazily on first access and is cached.

### Search & queries

| Function | Description |
|---|---|
| `GetLoadedBuildings()` | Enumerates all currently loaded buildings (lazy, no metadata cost). |
| `GetLoadedBuildingCount()` | Count of loaded buildings. |
| `FindBuildingById("1619")` | Find by numeric id or full source name. |
| `GetBuildingAt(Vector3)` / `GetBuildingAtGps(lat, lon)` | Building standing on a point (vertical probe). |
| `GetNearestBuilding(pos, maxRadius)` | Closest loaded building. |
| `GetBuildingsInRadius(center, radius)` | All buildings in a circle — **full dataset** via tile metadata (loaded + unloaded tiles). |
| `GetBuildingsInRadius(center, radius, includeUnloadedTiles: false)` | Streamed meshes only (faster, old behaviour). |
| `GetLoadedBuildingsInRadius(center, radius)` | Shortcut for loaded-only search (previous behaviour). |
| `FindBuildingsByAddress("Ilica 5")` | Fuzzy, diacritic-insensitive address search (č=c, š=s, ...). ⚠ loads metadata per candidate; first call may take a moment. |
| `QueryBuildings(predicate)` | Generic filter over all loaded buildings. |
| `GetBuildingUnderCursor()` | Building under the mouse. |
| `TryRaycastBuilding(ray, out b, out hit)` | Raycast with any ray (VR controller, NPC sight...). |
| `InvalidateBuildingCache()` | Force refresh of internal caches. |

### `ZGBuildingHandle`

| Member | Description |
|---|---|
| `Transform`, `TileId`, `Name`, `BuildingId`, `IsValid`, `Position` | Identity. `IsLoaded` = mesh in scene; `Transform` may be null for metadata-only hits. |
| `Metadata`, `HasMetadata` | Full `BuildingInfoSnapshot` (lazy). |
| `Address`, `Street`, `Floors`, `GrossFloorArea`, `UseClassification`, `ConstructionYear`, `OsmBuildingTag` | Common attributes (defaults when unknown). |
| `GetOsmTag("amenity")` | Any OSM tag of the matched footprint. |
| `TryGetGps(out lat, out lon)` | Building GPS (metadata first, transform fallback). |
| `GetWorldBounds()`, `GetHeightMeters()`, `GetRooftopPosition()`, `GetBasePosition()` | Geometry. |
| `GetSummary()`, `GetGpsText()`, `GetGoogleMapsUrl()` | Display strings. |

```csharp
// All residential buildings older than 1950 within 300 m
var old = zg.QueryBuildings(b =>
    b.ConstructionYear is > 0 and < 1950 &&
    ZGConnectToolkit.HorizontalDistanceMeters(b.Position, player.position) < 300f);

// Drone landing pad on the nearest tall building
var tower = zg.QueryBuildings(b => b.GetHeightMeters() > 30f, requireMetadata: false)
    .OrderBy(b => Vector3.Distance(b.Position, drone.position))
    .FirstOrDefault();
if (tower != null) drone.FlyTo(tower.GetRooftopPosition());
```

### Highlighting

Persistent, programmatic highlights — independent of the hover/click selection that
`BuildingInteractionController` manages. Requires the `BuildingHighlight` layer
(already part of ZGConnect scenes).

| Function | Description |
|---|---|
| `HighlightBuilding(b)` / `HighlightBuilding(b, color)` | Outline a building until cleared. |
| `ClearHighlight(b)` / `ClearAllHighlights()` | Remove highlights. |
| `HighlightedBuildingCount` | Active highlight count. |

```csharp
// Mark all quest targets in orange
foreach (var b in zg.FindBuildingsByAddress("trg bana"))
    zg.HighlightBuilding(b, new Color(1f, 0.6f, 0.1f));
```

### Events

| Event | Fired when |
|---|---|
| `BuildingClicked` (`ZGBuildingHandle`) | User clicks a building (needs `BuildingInteractionController`). |
| `BuildingHoverChanged` (`ZGBuildingHandle`, null = none) | Hovered building changes. |

```csharp
zg.BuildingClicked += building =>
    Debug.Log($"Clicked: {building.GetSummary()}");
```

---

## 5. Navigation

Moves `NavigationTarget` (camera by default — set it to your player rig if needed).

| Function | Description |
|---|---|
| `TeleportTo(Vector3)` | Instant move. |
| `TeleportToGps(lat, lon, heightAboveGround = 60)` | Instant move to GPS, above ground (safe altitude when terrain not loaded yet). |
| `TeleportToAddress("Ilica 5")` | Address search + teleport in front of the match. Returns the building or null. |
| `FlyTo(Vector3, duration = 3, onComplete)` | Smooth flight (smooth-step easing). |
| `FlyToGps(lat, lon, heightAboveGround, duration, onComplete)` | Smooth flight to GPS. |
| `FlyToBuilding(b, duration, onComplete)` | Cinematic flight that frames the building. |
| `StopFlight()` / `IsFlying` | Flight control. |
| `LookAt(Vector3)` | Rotate the navigation target toward a point. |

```csharp
// Demo wow-moment: type an address, fly there
var building = zg.FindBuildingsByAddress(inputField.text).FirstOrDefault();
if (building != null)
{
    zg.PrewarmArea(building.Position);          // start loading tiles there
    zg.FlyToBuilding(building, duration: 4f,
        onComplete: () => zg.HighlightBuilding(building));
}
```

---

## 6. Streaming

| Member | Description |
|---|---|
| `IsStreaming` | Realtime streaming active? |
| `GetInitialLoadProgress01()` | 0–1 progress for loading screens. |
| `IsInitialLoadComplete()` | Initial camera-radius ring fully loaded? |
| `GetLoadStatusText()` | "Loading terrain (12/16 - 75%)". |
| `LoadedTerrainTileCount` / `LoadedBuildingTileCount` | Live counts. |
| `GetLoadedTerrainTileIds()` | Ids of loaded terrain tiles. |
| `SetBuildingStreamingEnabled(bool)` | Runtime performance toggle. |
| `SetBuildingCullDistance(float)` | Runtime building cull distance. |
| `PrewarmArea(center, seconds = 6)` / `PrewarmGps(lat, lon, seconds)` | Temporarily redirect streaming focus to preload a remote area (before teleports / cutscenes). |
| `TerrainTileLoaded` / `TerrainTileUnloaded` (string tileId) | Terrain tile events. |
| `BuildingTileLoaded` / `BuildingTileUnloaded` (string tileId) | Building tile events — spawn per-area content here. |

```csharp
// Loading screen
loadingBar.value = zg.GetInitialLoadProgress01();
statusLabel.text = zg.GetLoadStatusText();

// Spawn NPCs whenever a building tile streams in
zg.BuildingTileLoaded += tileId =>
{
    foreach (var b in zg.GetLoadedBuildings().Where(b => b.TileId == tileId).Take(3))
        SpawnNpcAt(zg.SnapToGround(b.GetBasePosition(), 0.1f));
};
```

---

## 7. Recipes

**GPS HUD under the minimap**

```csharp
hudLabel.text = ZGConnectToolkit.FormatGps(camera.transform.position);
```

**Trigger when the player enters a neighbourhood**

```csharp
if (ZGConnectToolkit.IsWorldPosInsideGpsPolygon(player.position, gornjiGradPolygon))
    questManager.EnterZone("Gornji Grad");
```

**Tower-defense placement validation**

```csharp
bool valid = zg.IsTerrainReadyAt(p)
          && zg.GetSlopeAngle(p) < 20f
          && zg.GetBuildingAt(p) == null;
```

**City trivia: click a building, show its facts**

```csharp
zg.BuildingClicked += b =>
{
    infoPanel.Show(
        title: b.Address ?? b.Name,
        body: $"Built {b.ConstructionYear}, {b.Floors} floors, {b.GrossFloorArea:F0} m²\n" +
              $"{b.GetGpsText()}");
};
```

---

## 8. Performance notes

- **Metadata is lazy.** Enumeration and radius queries on **loaded** buildings are cheap.
  `GetBuildingsInRadius` also reads tile metadata files (`buildings_{tileId}.bytes`/`.json`)
  for tiles that overlap the search circle but are not streamed in yet — first call per tile
  loads that file from disk (cached afterwards). Large radii spanning many tiles take longer.
- Handles from **unloaded** tiles have `Transform == null` and `IsLoaded == false` but still
  expose `Address`, `Metadata`, `Position`, `GetSummary()`. Highlighting / `FlyToBuilding`
  require `IsLoaded == true`.

## 9. Requirements

- Works in both runtime modes: `RealtimeStreamingController` (Runtime_Streaming) and
  `TerrainStreamingController` (dataset scenes). Streaming-specific features
  (prewarm, tile events, load progress) need the realtime controller.
- Highlighting needs the `BuildingHighlight` layer in the Tag Manager and the
  `ZGConnect/BuildingOutline` shader (both ship with the plugin).
- Building click/hover events need a `BuildingInteractionController` in the scene
  (added automatically by ZGConnect runtime scenes).
