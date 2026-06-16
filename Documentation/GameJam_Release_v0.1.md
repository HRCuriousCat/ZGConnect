# ZG Connect — Game Jam Release (v0.1)

**Prvi javni release** Unity plugina za **streamanu 3D kartu Zagreba** (teren, zgrade, vegetacija) s GPS/EPSG koordinatama i API-jem za brzi prototip igara i demo aplikacija.

---

## Što je uključeno

### Runtime streaming

- **Teren** — učitavanje 1×1 km tileova iz packanog dataseta (`StreamingAssets/ZGConnect`)
- **Zgrade** — facade bundlei s LOD-om, colliderima i metapodacima (adresa, katovi, površina…)
- **Vegetacija** — baked bundlei + runtime fallback s maskama
- **Sekvencijalni initial load** — teren → zgrade → vegetacija (loading screen prati napredak)
- **Ograničena regija učitavanja** — manji, fokusirani dio grada za brži start na Game Jamu
- **Runtime HUD** — broj učitanih tileova, queue, HLOD status

### ZG Connect Toolkit (hackathon API)

Jedan komponenta (`ZGConnectToolkit`) za:

- GPS ↔ Unity ↔ EPSG:3765 konverzije
- teren (visina, nagib, snap na tlo)
- zgrade (pretraga po radijusu, highlight, fly-to)
- navigaciju kamere / teleport na koordinate

Vidi: [ZGConnectToolkit_Guide.md](./ZGConnectToolkit_Guide.md)

### Editor alati

- **RealTime Asset Importer** — pack streaming dataseta
- **Dataset Import Manager** — import heightmapa, zgrada, vegetacije
- **Tile Map Selector** — odabir regije na karti
- **Populate Scene** — statični teren/zgrade u editoru (za debug ili offline rad)

---

## Kako pokrenuti (Game Jam)

1. Otvori projekt u **Unity 6** (`6000.3.8f1`)
2. Start scena: **`Scenes/Loader.unity`**
3. Play → loading screen → **`Runtime_Streaming`**
4. Za brzi prototip dodaj **`ZGConnectToolkit`** na prazan GameObject i koristi `ZGConnectToolkit.Instance`

### Scene u paketu

| Scena | Namjena |
|--------|---------|
| `Loader.unity` | **Preporučeni ulaz** — intro + handoff na streaming |
| `Runtime_Streaming.unity` | **Game Jam default** — uža regija, HLOD 1×1, brži load |
| `Runtime_Streaming_workshop.unity` | Šira pokrivenost, HLOD 2×2/4×4 uključen |
| `Runtime_Streaming_normal.unity` | Veća regija, HLOD uključen |

---

## Dataset (ovaj build)

- **Koordinatni sustav:** EPSG:3765 (HTRS96 / Croatia TM), WGS84 za GPS
- **Tile grid:** 1×1 km
- **Manifest:** ~640+ tileova u `StreamingAssets/ZGConnect/manifest.json`
- **Basemap:** orthophoto (packani terrain bundlei)
- **Game Jam regija** (`Runtime_Streaming`): ograničen EPSG pravokutnik oko centra demo područja (postavke na `RealtimeStreamingController`)

---

## Što radi dobro na Jamu

- Letenje / hodanje kamerom kroz grad s dinamičkim učitavanjem
- Klik na zgradu + čitanje metapodataka (ako su materijali/popup dodijeljeni u sceni)
- GPS teleport, geofence, “odredi udaljenost do cilja” preko Toolkita
- Spawn na terenu, izbjegavanje krovova, pretraga zgrada u radijusu
- Brzi hackathon prototipi bez vlastitog terrain pipelinea

---

## Poznata ograničenja (v0.1)

- **Veliki build** — `StreamingAssets` je velik; player build i prvi pack traju dugo. Za Jam koristi **pre-built** player ili užu regiju.
- **Building highlight / info popup** — u default `Runtime_Streaming` sceni materijali i popup prefab **nisu dodijeljeni**; interakcija je uključena, ali vizualni feedback treba dodati u editoru.
- **Photogrammetry / Google 3D Tiles** — folder `PhotogrammetryStreaming` **nije dio ovog releasea** i nije održavan.
- **Vegetacija na editor-populate terenima** — radi samo uz `Stream Terrain = off` + prethodni Populate Scene.
- **HLOD 2×2/4×4** — isključen u Game Jam sceni radi predvidljivosti; uključen u workshop/normal varijantama.
- **Prvi release** — očekuj rupe u datasetu na rubovima regije, sporiji load na slabijem hardveru, povremene streaming race conditione na rubu load radijusa.

---

## Tehnički stack

- Unity **6000.3.8f1**
- **URP**
- **GLTFast** — runtime učitavanje GLB fallbacka
- **Addressables** (gdje je packano)
- Dataset: terrain/building/vegetation **AssetBundle** + manifest JSON

---

## Za developere na Jamu

```csharp
// Minimalni primjer
var zg = ZGConnectToolkit.Instance;
zg.TeleportToGps(45.8131f, 15.9772f); // centar
zg.BuildingClicked += b => Debug.Log(b.GetSummary());
```

Dokumentacija: [ZGConnectToolkit_Guide.md](./ZGConnectToolkit_Guide.md)  
Editor: meni **ZG Connect → RealTime Asset Importer**

---

## Izvan opsega ovog releasea

- Google photogrammetrija / 3D Tiles streaming
- Multiplayer / networking
- Mobilni optimizirani build (desktop-first)
- Kompletan coverage cijelog Zagreba bez region filtera u jednoj sceni

---

## Licenca / atribucija

*(Dodaj LICENSE i izvore podataka: OSM, DGU, vlastiti orthophoto — prema onome što tvoj dataset koristi.)*
