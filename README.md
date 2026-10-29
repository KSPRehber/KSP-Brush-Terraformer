# TerraformTech — real-time terrain editing for Kerbal Space Program

TerraformTech is a tech demo plugin for **KSP 1.12.x** that lets you reshape planetary terrain while the
game is running. You can raise, lower, flatten and smooth the ground under the cursor; vessels collide
with the edited surface right away, and the edits are stored in the save file.

It works with any terrain built by KSP's PQS system, which covers stock planets and Kopernicus planet
packs, because it never replaces the terrain. It adds a correction layer on top of whatever the planet
already generates.

---

## Contents

1. [Quick start](#quick-start)
2. [Using the tool](#using-the-tool)
3. [Project layout](#project-layout)
4. [How KSP terrain works (background)](#how-ksp-terrain-works-background)
5. [Architecture overview](#architecture-overview)
6. [The delta layer: `PQSMod_TerraformDelta`](#the-delta-layer-pqsmod_terraformdelta)
7. [Edit storage: `TerrainDeltaStore`](#edit-storage-terraindeltastore)
8. [Brushes](#brushes)
9. [Real-time rebuilding: `QuadRebuilder`](#real-time-rebuilding-quadrebuilder)
10. [Physics and vessel safety](#physics-and-vessel-safety)
11. [Persistence: `TerraformScenario`](#persistence-terraformscenario)
12. [Terrain detail boost](#terrain-detail-boost)
13. [Scene loading and deferred rebuilds](#scene-loading-and-deferred-rebuilds)
14. [Configuration](#configuration)
15. [Logging and diagnostics](#logging-and-diagnostics)
16. [Measured performance](#measured-performance)
17. [Known limitations](#known-limitations)
18. [Pitfalls found during development](#pitfalls-found-during-development)
19. [Possible next steps](#possible-next-steps)

---

## Quick start

### Requirements

- KSP 1.12.x. Development used 1.12.5, Windows build, run through Proton on Linux.
- Mono's `csc` (Roslyn) on `PATH` to compile.
- For `run.sh`: Steam with Proton 11.0 installed.

### Test install

`KSP/` is a copy of the Steam installation with every third-party mod removed. Only `GameData/Squad` and
`GameData/SquadExpansion` are kept, so the plugin runs against stock content. The Steam copy is never
modified.

### Build

```sh
./build.sh
```

The script:

1. Compiles `src/*.cs` against the test install's own assemblies (`KSP/KSP_x64_Data/Managed`), using
   `-nostdlib` so the plugin links against the exact `mscorlib` the game ships.
2. Writes `build/TerraformTech.dll`.
3. Copies `config/TerraformTech.cfg` to `KSP/GameData/TerraformTech/`.
4. Deploys the DLL to `KSP/GameData/TerraformTech/Plugins/` by copying to a temporary file and renaming it
   into place. See [Pitfalls](#pitfalls-found-during-development) for why this is not a plain `cp`.
5. Prints a note if KSP is running, since a new build only loads after a restart.

Set `KSP_DIR` to build against a different installation.

### Run

```sh
./run.sh
```

This starts `KSP/KSP_x64.exe` through Proton with its own Wine prefix in `.proton-prefix/`, so Steam's KSP
prefix is never touched. You can override `PROTON` and `STEAM_ROOT` through environment variables.

---

## Using the tool

1. Load a save and go to flight on a body with terrain (anything except gas giants and the Sun).
2. Press **F8** to toggle the Terraform window. F8 is unbound in stock KSP.
3. Choose a brush: **Raise**, **Lower**, **Flatten** or **Smooth**.
4. Set **Radius** (5–250 m) and **Strength**.
5. Hold the **left mouse button** on the ground to paint. A cyan ring shows the brush footprint, draped over
   the current (edited) terrain.

The window also shows:

| Field | Meaning |
|---|---|
| PQS vertex | Current spacing between terrain vertices at the finest detail level. Edits much smaller than this cannot show. |
| edit cell | Spacing of the edit grid (about 3.6 m on Kerbin). |
| Edited tiles | Number of 64×64 storage tiles holding edits on this body. |
| Cursor alt / edit | Terrain altitude under the cursor and how much of it comes from edits. |
| Last edit | Time for the last brush tick (brush, vessel safety, rebuild) and the number of terrain quads rebuilt. |
| Lifted vessel | Shown when a grounded vessel was raised to stay on top of rising terrain. |
| Clear all edits | Removes every edit on the body and rebuilds the affected terrain. |

The brush ignores clicks over the Terraform window and over KSP's own interface, so clicking toolbar
buttons does not paint the ground behind them.

---

## Project layout

```
build.sh                     Compile and deploy
run.sh                       Launch the test install through Proton
config/TerraformTech.cfg     Settings deployed to GameData/TerraformTech/
src/
  Terraform.cs               Registry of bodies, injection, settings, detail boost, bootstrap driver
  PQSMod_TerraformDelta.cs   The PQS mod that adds edits to terrain height
  TerrainDeltaStore.cs       Sparse cube-sphere edit grid, brushes, serialization
  QuadRebuilder.cs           Rebuilds affected terrain quads and re-cooks colliders
  TerraformTool.cs           In-flight UI, raycasting, brush ring, vessel lifting
  TerraformScenario.cs       Save/load of edits inside the .sfs
KSP/                         Stripped stock test install (not source)
.proton-prefix/              Wine prefix used by run.sh (not source)
build/                       Compiler output
```

---

## How KSP terrain works (background)

There is no stored mesh or heightmap of a KSP planet's surface. Terrain is generated at runtime by
**PQS (Procedural Quad Sphere)**:

- Each `CelestialBody` with a surface has a `PQS` component (`body.pqsController`).
- The sphere is a **cube projected onto a sphere**. Each of the 6 cube faces is a root **quad (`PQ`)**, and
  quads split into 4 children recursively (a quadtree), up to `PQS.maxLevel`.
- Quads close to the camera are subdivided further; distant quads stay coarse. Only **leaf** quads (not
  subdivided) are visible.
- Each quad is a 15×15 vertex grid (`PQS.cacheSideVertCount`). Its vertex heights are computed by running
  every vertex through a chain of **`PQSMod`** components. Examples: `PQSMod_VertexHeightMap` (heightmap
  texture), `PQSMod_VertexSimplexHeight` (noise), `PQSMod_FlattenArea` (flat spots such as KSC), and
  `PQSLandControl` (colour and scatter).
- Mods run in ascending `order`. Building a vertex has two passes: first every mod's
  `OnVertexBuildHeight` (shape), then every mod's `OnVertexBuild` (colour and other data). Colour mods
  therefore always see the final height.
- `PQSMod_QuadMeshColliders` adds a `MeshCollider` to quads at the finest levels. Those colliders are what
  vessels physically touch.
- `PQS.GetSurfaceHeight(direction)` runs the same height chain for a single direction. KSP uses it for
  `CelestialBody.TerrainAltitude`, which feeds vessel altitude, landing logic and other systems.
- **Kopernicus** builds or edits planets by configuring this same `PQS`/`PQSMod` machinery from config
  files, so anything that plugs into PQS works for Kopernicus bodies too.

Some facts verified by inspecting `Assembly-CSharp.dll` in this project:

- `PQS.SetupMods()` collects every `PQSMod` on child GameObjects of the PQS object and sorts them by
  `order`. It runs every time a sphere starts (`StartSphere`).
- `PQS.BuildQuad(quad)` returns immediately if the quad is already built (`isBuilt`) or is subdivided.
  Otherwise it runs the full vertex chain, uploads the mesh and calls every mod's `OnQuadBuilt`.
- `PQ.Build()` calls `BuildQuad` and then queues a normals update for the quad.
- `PQ.SetVisible()` calls `Build()`. A quad marked not built is rebuilt automatically when it becomes
  visible again.
- `PQSMod_QuadMeshColliders.OnQuadBuilt` assigns `meshCollider.sharedMesh = quad.mesh`.
- `CelestialBody.TerrainAltitude(lat, lon)` is
  `pqs.GetSurfaceHeight(GetRelSurfaceNVector(lat, lon)) - pqs.radius`. This pins down the coordinate
  frame used by `VertexBuildData.directionFromCenter`.
- `PQS.StartSphere` reads `maxLevel` and `minLevel` from the active terrain detail preset
  (`PQSCache.PresetList`) every time the sphere starts.

---

## Architecture overview

The approach is **"don't convert the planet, store the difference"**:

```
final height = (everything the planet already generates) + Δ(direction)
```

Converting a whole planet to an editable format is impractical. Kerbin's surface sampled at 10 m would
be around 45 billion height values. So TerraformTech keeps the original PQS chain untouched and stores
only a sparse field of height offsets (Δ) where the player has edited.

```
           ┌───────────────────────────── KSP PQS ─────────────────────────────┐
direction ─▶ stock / Kopernicus height mods ─▶ PQSMod_TerraformDelta (+Δ) ─▶ colour mods ─▶ mesh ─▶ collider
           └───────────────────────────────────────────────────▲───────────────┘
                                                               │ Sample(dir)
 Brush (TerraformTool) ──ApplyBrush──▶ TerrainDeltaStore ──────┘
        │                                    │ Serialize / Deserialize
        │                                    ▼
        │                          TerraformScenario (.sfs)
        └──▶ QuadRebuilder: rebuild affected leaf quads + re-cook colliders
```

Components:

| Component | Role |
|---|---|
| `Terraform` (static) | Registry of `BodyTerrain` per body; injects the mod; loads settings; applies the detail boost. |
| `BodyTerrain` | Per-body state: `CelestialBody`, `PQS`, store, mod, height helpers, pending rebuild regions. |
| `TerraformBootstrap` | Persistent `MonoBehaviour` (created at main menu, `DontDestroyOnLoad`). Attaches to bodies and runs deferred rebuilds once a scene is ready. |
| `PQSMod_TerraformDelta` | Adds Δ to the height of every vertex PQS builds. |
| `TerrainDeltaStore` | The edit data: sparse tiles, cube mapping, sampling, brushes, serialization. |
| `QuadRebuilder` | Finds the quads affected by an edit and rebuilds them and their colliders. |
| `TerraformTool` | Flight-scene UI, mouse raycast, brush ring, rate-limited brush application, vessel lifting. |
| `TerraformScenario` | `ScenarioModule` that saves and loads edits with the game. |

---

## The delta layer: `PQSMod_TerraformDelta`

### What it does

```csharp
public override void OnVertexBuildHeight(PQS.VertexBuildData data)
{
    data.vertHeight += store.Sample(data.directionFromCenter);
}
```

`vertHeight` is the vertex's distance from the planet centre, in metres. `directionFromCenter` is the
unit direction of the vertex in the body's local frame. The mod adds the stored offset for that
direction. If the store is empty or bypassed, it returns immediately, so unedited planets cost
essentially nothing.

### Ordering

The mod uses `order = 1000000000`, so it runs **after every other height mod**, including flatten mods
such as the one under KSC. Consequences:

- Edits apply on top of the final stock or Kopernicus shape, so you can dig into the KSC pad area.
- Colour mods run in the later `OnVertexBuild` pass, so the surface is coloured according to its new
  height. For example, a deep pit gets the colouring for that altitude.
- `PQS.GetSurfaceHeight` and `CelestialBody.TerrainAltitude` include the edits, so KSP's own altitude,
  landing and terrain queries agree with what you see.

### Injection

At the main menu, `Terraform.AttachAll()` loops over `FlightGlobals.Bodies`. For each body with a
`pqsController` it:

1. Creates a GameObject `TerraformDelta` parented under the PQS GameObject, on the same layer.
2. Adds `PQSMod_TerraformDelta` with `sphere`, `order`, `modEnabled = true` and default requirements.
3. Creates the body's `TerrainDeltaStore` and registers a `BodyTerrain`.

Planet prefabs persist for the whole session, and `PQS.SetupMods()` collects child mods every time a
sphere starts. The mod is therefore picked up automatically in the Space Center, flight and Tracking
Station without touching KSP internals. Only the main surface PQS is extended; ocean spheres are
separate PQS objects and are left alone.

---

## Edit storage: `TerrainDeltaStore`

### Grid on an equiangular cube sphere

Edits live on the same kind of geometry PQS uses: a cube projected onto a sphere.

- A direction `d` is assigned to the cube face of its largest absolute component, giving 6 faces.
- The other two components are divided by the major one, giving gnomonic coordinates `s, t` in [-1, 1].
- These are converted to **equiangular** coordinates: `a = atan(s) · 4/π`, `b = atan(t) · 4/π`. A plain
  gnomonic projection squeezes cells near face centres and stretches them near corners. The equiangular
  version keeps cell sizes much more uniform across the face.
- Each face is a `(N+1) × (N+1)` grid of **points**, where `N = CellsPerFace`. A point stores a 32-bit
  float: the radial offset in metres.

`FaceToDir` is the inverse mapping, using `tan` to go back from equiangular coordinates. A round trip
through both functions differs by less than 4×10⁻¹⁶ (tested over 100,000 random directions).

### Resolution

`CellsPerFace` is chosen per body when it is attached: the smallest power-of-two multiple of 64 that
gives cells of **4 m or less**, measured as a quarter of the planet's circumference divided by `N`.
Examples:

| Body | Radius | CellsPerFace | Cell size |
|---|---|---|---|
| Kerbin | 600 km | 262,144 | ~3.6 m |

The resolution is fixed and does not depend on PQS detail. The detail level can change (the detail boost,
the player's Terrain Detail setting), and saved edits must keep a stable grid. Saved data records
`CellsPerFace`; data with a mismatching grid is discarded with a log message instead of being misread.

### Sparse tiles

Each face is split into **64×64-point tiles**, each a `float[4096]` (16 KB). Tiles are created only when
a brush writes to them and are kept in a `Dictionary<long, float[]>` keyed by
`(face << 48) | (tileX << 24) | tileY`. A missing tile means "no edit" (Δ = 0). On Kerbin one tile covers
about 230 m × 230 m, so a typical brushed area uses only a handful of tiles.

`GetTile` keeps a one-entry cache of the last tile used. While a quad is being built its vertices lie
next to each other, so most lookups hit the same tile.

### Sampling

`Sample(dir)` maps the direction to face coordinates, finds the containing grid cell and **bilinearly
interpolates** its four corner points. Sampling only ever reads points from the direction's own face. At
face edges this stays continuous, because brushes write the same function of direction into both faces'
points along a seam.

Seam test results: a 10 m raise centred exactly on a cube **corner** (spanning three faces) changes by
at most about 3 cm per 10 cm step along a line crossing the seams, which is the slope of the bump itself.

### Base height cache

Flatten and Smooth need the **original** terrain height, not just the offset. The store keeps a second
set of tiles (`baseTiles`) of unedited altitudes, filled on demand. To sample one:

1. Set `store.Bypass = true` so the delta mod adds nothing.
2. Call `pqs.GetSurfaceHeight(dir) - pqs.radius`.
3. Restore `Bypass`.

Base heights never change, so each point is sampled at most once per session. They are not saved.

### Serialization

`Serialize()` writes, through a `DeflateStream`:

```
int32  magic  = 0x31444654 ("TFD1")
int32  CellsPerFace
int32  tileCount
repeat tileCount:
    int64   key
    float32 × 4096   (row-major, 64 × 64)
```

The compressed bytes are Base64-encoded. Untouched parts of a tile are zeros and compress very well; two
lightly edited tiles came to about 1 KB of text. `Deserialize` checks the magic number and grid size
before replacing the contents.

KSP's Mono build ships `MonoPosixHelper.dll`, which `DeflateStream` needs, so compression works inside
the game.

### Versioning

Every change increments `Version`. The scenario uses it to tell whether in-memory edits still match what
was last saved or loaded.

---

## Brushes

`ApplyBrush(op, center, radius, sphereRadius, rate, flattenAltitude, sampleBaseAltitude)` works in
PQS-local directions:

1. Convert the radius in metres to an angle: `θ = radius / sphereRadius`.
2. For each of the 6 faces whose hemisphere contains the centre, project the centre onto that face. This
   also works when the centre lies outside the face; `atan` handles it. Then compute a square window of
   grid points: `±θ · 2N/π · 1.6 + 2`. The factor 1.6 covers the equiangular stretching away from the
   face axes.
3. For each point in the window, compute its direction and its angular distance to the centre, and skip
   points outside the circle.
4. Weight each point with a smooth falloff: `w = (1 − (dist/θ)²)²`. That is 1 at the centre and 0 at the
   rim, with a soft edge.

Operations:

| Brush | Effect per tick | `rate` |
|---|---|---|
| **Raise** | `Δ += rate · w` | metres at the centre (`strength m/s × dt`) |
| **Lower** | `Δ −= rate · w` | same |
| **Flatten** | new height moves toward `flattenAltitude` by `min(1, rate · w)` | blend fraction |
| **Smooth** | new height moves toward the average of its 4 neighbours by `min(1, rate · w)` | blend fraction |

- Flatten's target altitude is captured **when the stroke starts** (mouse down), so dragging levels
  everything to the height of the first click instead of drifting.
- Flatten and Smooth work on the **total height** (base + Δ) and write back `Δ = newTotal − base`, so they
  flatten or smooth the real visible terrain, not just the offsets.
- Smooth reads a 1-point border around the window and uses a snapshot of heights (Jacobi-style), so the
  result does not depend on the order points are visited.
- For blend brushes the tool uses `rate = 1 − exp(−strength · 0.2 · dt)`, which keeps the effect
  independent of frame timing.

The tool applies the brush at most **20 times per second** (`ApplyInterval = 0.05 s`), with `dt` capped
at 0.2 s.

---

## Real-time rebuilding: `QuadRebuilder`

After the store changes, the visible mesh and colliders must be regenerated. Rebuilding the whole planet
for every brush tick would be far too slow, so only affected quads are rebuilt.

### Walking the quadtree

`QuadRebuilder` walks `pqs.quads` (the 6 roots) down through `subNodes`:

- **Subdivided quads are always descended into.** Their own meshes cannot be used for culling: in
  testing, culling by parent mesh bounds rejected the entire tree and nothing rebuilt. The full walk visits
  about 1,300–1,700 nodes and is cheap.
- **Leaf quads** are tested for overlap with the edited area:
  - *Brush edits* (`RebuildArea`): the brush centre is converted into the quad's local space and checked
    against the quad's mesh bounds, expanded by the brush radius plus a 100 m margin. The margin covers
    height changes not yet in the bounds and refreshes neighbours' edge normals.
  - *Region rebuilds* (`RebuildRegions`, used after loading or clearing): the angle between the quad
    centre's direction and each region's centre is compared with the sum of the quad's and the region's
    angular radii plus margin.
- Leaves that are not built, not active, or held in PQS's cache pool are skipped.

### Rebuilding a leaf

```csharp
q.isBuilt = false;
q.Build();                     // full PQS vertex chain, including our mod; OnQuadBuilt; queue normals
col.sharedMesh = null;         // force PhysX to re-cook the collider
col.sharedMesh = q.mesh;
```

`BuildQuad` refuses to rebuild quads that are already built, which is why `isBuilt` is cleared first.
The collider is reassigned through `null` because assigning the same `Mesh` instance again does not
reliably make PhysX re-cook it.

### Hidden parents

Parent quads (subdivided, currently hidden) still hold meshes from before the edit, and they become
visible again when the camera moves away and their children merge back. Instead of rebuilding them
straight away, the rebuilder sets `isBuilt = false` on every parent whose subtree contained a rebuilt
leaf. Since `PQ.SetVisible()` calls `Build()`, PQS rebuilds those parents itself when they are next
shown, so distant views stay consistent with the edits.

### Statistics

Every rebuild returns a `Stats` record: visited, leaves, inRange, notBuilt, inactive, cached, refused,
rebuilt, parentsMarked. The tool logs it at the end of each stroke, and region rebuilds log it as well.
These counters are how the "0 quads rebuilt" bug was diagnosed.

---

## Physics and vessel safety

### Colliders

Collision comes from the same quad meshes, re-cooked after every rebuild. There is no separate physics
representation, so what you see is what you drive on. Lowering the ground under a vessel lets it fall
naturally onto the new surface.

### Lifting grounded vessels

Raising terrain under a vessel would put the new collider inside its parts, and PhysX would push them
out violently. To prevent that, each brush tick:

1. **Before** applying the brush, for every loaded, unpacked vessel on the same body that is
   `LandedOrSplashed`, or within 5 m of the terrain, records the terrain altitude under each part within
   `radius + 5 m` of the brush centre.
2. Applies the brush to the store.
3. Measures the terrain under the same parts again and takes the largest rise per vessel.
4. If a vessel's terrain rose by more than 1 cm, moves the vessel up by that rise + 5 cm with
   `Vessel.Translate`, and calls `IgnoreGForces(10)` so the jump does not trigger G-force damage.
5. Only then rebuilds the quads and colliders.

`Vessel.Translate` goes through `Vessel.SetPosition`, which repositions every part relative to the
vessel's root.

---

## Persistence: `TerraformScenario`

Edits are saved **inside the save game** through a `ScenarioModule`, registered with
`AddToAllGames` for the Flight, Space Center and Tracking Station scenes. As a result, quicksave,
quickload and revert restore terrain together with everything else.

```
SCENARIO
{
    name = TerraformScenario
    BODY
    {
        name = Kerbin
        cellsPerFace = 262144
        data = <base64 chunk, up to 4096 chars>
        data = <next chunk>
        ...
    }
}
```

- Long Base64 strings are split into 4,096-character `data` values. When loading, they are joined back in
  order.
- Bodies without edits are not written at all.
- The last serialized string and the store version are cached per body. Saving with no changes does not
  re-compress, and loading identical data with no changes since is skipped completely. That is the common
  case when switching scenes.

When loaded data **does** differ (a different save, a revert, a quickload, or leaving flight without
saving), the scenario:

1. Queues the **currently** edited area for rebuild, so removed edits disappear.
2. Replaces the store contents (or clears them if the save has none for that body).
3. Queues the **newly loaded** edited area for rebuild.

The actual rebuild is deferred; see [Scene loading](#scene-loading-and-deferred-rebuilds).

---

## Terrain detail boost

### The problem

PQS detail limits what can be shown. On the **High** Terrain Detail preset, Kerbin's
`maxSubdivision` is 10. That is the highest stock setting, and it gives vertices about **66–75 m** apart
near the ground. The edit grid is 3.6 m, but a brush can only move existing vertices, so small craters
were undersampled. Players saw that "the terrain moved, but not as much as the brush should".

### The fix

`PQS.StartSphere` reads `maxLevel` from `PQSCache.PresetList` (the Terrain Detail presets) **every time
a sphere starts**. At the main menu, before any planet sphere starts, `Terraform.ApplyDetailBoost()`
raises `maxSubdivision` by `detailBoost` levels (default **3**) for every planet in **every** preset.
Ocean spheres are skipped. Each level halves the vertex spacing near the camera:

| Kerbin, High preset | maxLevel | Vertex spacing |
|---|---|---|
| Stock | 10 | ~66 m |
| `detailBoost = 3` | 13 | ~8.2 m |

`PQSMod_QuadMeshColliders` derives its minimum collider level from `maxLevel` when the sphere sets up its
mods, so colliders follow the finer levels automatically.

### Not compounding the boost

KSP writes the preset list back to `settings.cfg`, which now contains boosted values. If the plugin added
`+3` to whatever it read on each launch, the boost would grow every session. To avoid that, the
**original** values are recorded once in
`GameData/TerraformTech/PluginData/TerrainOriginals.cfg`:

```
ORIGINAL
{
    preset = High
    planet = Kerbin
    maxSubdivision = 10
}
```

The boost is always computed as `original + detailBoost`, using that file whenever an entry exists.

> **Note:** because KSP saves the boosted presets to `settings.cfg`, removing the plugin leaves the higher
> detail in place. Re-select a Terrain Detail preset in the game settings, or restore the values from
> `TerrainOriginals.cfg`.

---

## Scene loading and deferred rebuilds

### Why rebuilds are deferred

By the time a scene's scenarios load their data, KSP has already started the planet sphere and built
its quads, so edits that change on load have to be applied by rebuilding terrain. The first
implementation called `PQS.RebuildSphere()` at that point, in `ScenarioModule.Start` during scene load.
It worked once and then **crashed Unity natively** (access violation in `UnityPlayer.dll` on a worker
thread) while switching from flight to the Space Center. Tearing down every quad while the scene is
still being set up is unsafe.

### How it works now

- Nothing calls `RebuildSphere()` any more.
- Load and clear operations only **queue regions**: the centre direction and angular radius of every
  stored tile, from before and after the change (`BodyTerrain.QueueEditedAreaRebuild`).
- `TerraformBootstrap` (persistent across scenes) listens to `GameEvents.onGameSceneLoadRequested` (scene
  loading starts) and `GameEvents.onLevelWasLoadedGUIReady` (scene ready). It waits **30 frames after
  the scene is ready**, then runs `QuadRebuilder.RebuildRegions` for each body with pending regions.
- If the body's sphere is not active (for example, a different planet), the regions are dropped. That
  sphere builds fresh, with current edits, when it next starts.

The first deferred implementation rebuilt **every** leaf (about 1,100–1,200 quads), which took 2.3–2.8 s.
Rebuilding only the edited regions is meant to reduce that to roughly the cost of a few brush ticks.
That version is built but has **not yet been measured in game**.

---

## Configuration

`config/TerraformTech.cfg` (deployed to `GameData/TerraformTech/TerraformTech.cfg` and read through
`GameDatabase`):

```
TERRAFORMTECH
{
	detailBoost = 3    // extra PQS subdivision levels, 0–6
}
```

Other values are constants in the source:

| Constant | Location | Value | Purpose |
|---|---|---|---|
| `MaxCellSize` | `Terraform` | 4.0 m | Upper bound on edit grid cell size |
| `TileSize` | `TerrainDeltaStore` | 64 | Points per tile side |
| `ModOrder` | `PQSMod_TerraformDelta` | 1,000,000,000 | Runs after all other height mods |
| `ToggleKey` | `TerraformTool` | F8 | Toggle the tool |
| `ApplyInterval` | `TerraformTool` | 0.05 s | Brush tick rate (20 Hz) |
| Radius range | `TerraformTool` | 5–250 m | Capped to bound per-tick cost |
| `Margin` | `QuadRebuilder` | 100 m | Rebuild slack around edits |
| `SettleFrames` | `TerraformBootstrap` | 30 | Frames to wait after a scene is ready |
| `ChunkSize` | `TerraformScenario` | 4096 | Base64 characters per `data` value |

---

## Logging and diagnostics

All messages go to `KSP/KSP.log` with the prefix `[TerraformTech]`:

| Message | When |
|---|---|
| `terrain detail boost +N applied …` | Main menu, after boosting presets |
| `attached to <Body>: radius=… maxLevel=… vertexSpacing~… cellsPerFace=… cell~…` | Main menu, per body |
| `tool on at <Body>: maxLevel=… vertexSpacing~… cell~… quadLayer=…` | Pressing F8. Shows the live detail level in flight. |
| `stroke start: quad … subdiv=… built=… active=… cached=… subdivided=… bounds=… localHit=…` | Mouse down. Describes the quad under the cursor. |
| `stroke end: N tiles, last edit X ms, quads <Stats>` | Mouse up |
| `edits for <Body> changed on load (N tiles), rebuild queued` | Loaded data differed from memory |
| `rebuild of <Body> edited area (N regions): X ms, <Stats>` | Deferred region rebuild ran |
| `discarding edits … different grid resolution` / `failed to load edits …` | Bad or incompatible save data |

Useful commands:

```sh
grep TerraformTech KSP/KSP.log
grep -E "\[EXC" KSP/KSP.log
```

Native crashes (Unity access violations) don't appear in `KSP.log`. Look under
`.proton-prefix/pfx/drive_c/users/steamuser/AppData/Local/Temp/Squad/Kerbal Space Program/Crashes/`
for `error.log` and `Player.log`.

---

## Measured performance

From offline tests (Mono 6.12) and in-game logs:

| Operation | Cost |
|---|---|
| `TerrainDeltaStore.Sample` (per terrain vertex, when edits exist) | ~0.11 µs |
| One brush tick: brush + vessel check + rebuild of 1–2 quads | ~2.5–10 ms |
| Quadtree walk per rebuild | ~1,300–1,700 nodes |
| Full rebuild of every visible leaf (old approach, now removed) | 2.3–2.8 s for ~1,100–1,200 quads |
| Serialized size, 2 lightly edited tiles | ~1 KB of Base64 |

The per-vertex cost of the delta layer is negligible next to the stock height mods. Rebuild time is
dominated by PQS running its own mod chain for every vertex of a rebuilt quad.

---

## Known limitations

- **Height field only.** Every direction has exactly one height, so caves, overhangs and tunnels are
  impossible. They would need a local voxel/SDF mesh, for example marching cubes, drawn in place of PQS
  around the edit.
- **Visible detail is limited by PQS.** Even with the boost, terrain vertices on Kerbin are about 8 m
  apart. Features smaller than about 2–3 vertices (roughly 20 m) are smoothed out.
- **Scatter and buildings.** Rocks and trees on rebuilt quads may float or sink; KSC buildings and other
  `PQSCity` objects stay at their original height.
- **Scaled space.** Map view and the planet as seen from orbit use a separate pre-baked mesh, so edits
  don't show from far away.
- **Ocean.** The ocean is a separate sphere at sea level, so digging below sea level shows water over the
  pit rather than a dry hole.
- **Unloaded vessels.** Only loaded, unpacked vessels are lifted. Raising terrain under a vessel that is
  too far away to be loaded is not handled.
- **Brush cost scales with area.** At 3.6 m cells a 250 m brush touches tens of thousands of grid points
  per tick. Flatten and Smooth also sample base heights the first time an area is touched, which is
  noticeably slower than Raise and Lower.
- **The detail boost is global**, not limited to edited areas, so it costs performance everywhere.
- **Edit grid resolution is fixed per body.** Changing `MaxCellSize` makes existing saves' edits
  incompatible; they are discarded with a log message.
- **Mod compatibility** with terrain renderers such as Parallax has not been tested.
- **Flight only.** The tool only works in flight, not in the Space Center or Tracking Station.

---

## Pitfalls found during development

These came up while building the demo and are worth knowing for anyone extending it.

1. **Never overwrite a loaded plugin DLL in place.** Mono memory-maps assemblies and reads method bodies
   lazily. Copying a new build over the file while KSP was running left the game reading a mix of the
   old and new file, and the first call to the delta mod threw
   `BadImageFormatException: Method has zero rva`, leaving the Space Center stuck loading. `build.sh`
   now copies to a temporary file and renames it, which gives the file a new inode, so a running game
   keeps the old one.
2. **Don't cull the quadtree with parent meshes.** Bounds of subdivided quads' meshes are not usable.
   Culling with them silently rejected every quad ("0 quads rebuilt") even though the store was updating.
   The symptom: the brush ring, drawn at the edited height, dipped under the unchanged ground.
3. **Don't call `PQS.RebuildSphere()` during scene load.** It caused a native Unity crash on a worker
   thread. Defer rebuilds until the scene is ready and rebuild quads individually.
4. **`maxLevel` at the main menu is not proof of flight detail.** `StartSphere` re-reads it from the
   preset list every time, which is both why the boost works and why logs should be checked in flight
   (`tool on …`).
5. **Re-cooking a `MeshCollider`** needs `sharedMesh = null` before reassigning the same mesh.
6. **Coordinate frames.** Convert world positions with
   `body.GetRelSurfaceNVector(body.GetLatitude(p), body.GetLongitude(p))`. This is the same frame as
   `VertexBuildData.directionFromCenter` and `PQS.GetSurfaceHeight`. Rebuild culling uses world-space
   quad transforms, which floating-origin shifts keep consistent within a frame.
7. **`pgrep -f` matches its own shell.** A launch guard such as `pgrep -f KSP_x64.exe || ./run.sh`, run
   from a shell whose command line contains that same text, sees itself and never launches.

---

## Possible next steps

- **Detail only where needed.** Instead of a global boost, allow extra subdivision only near edited tiles,
  for example by patching `PQ.UpdateSubdivision` with Harmony.
- **Scatter and `PQSCity` updates.** Re-seat scatter and buildings on rebuilt quads, or optionally
  flatten under buildings.
- **Scaled-space update** for large edits, so they show from orbit.
- **Background brush work.** Run brush maths and collider baking (`Physics.BakeMesh`, available in
  Unity 2019.4) on worker threads, and only upload meshes on the main thread.
- **More brushes:** noise, crater stamp, ramp or road between two points, and paint-by-texture.
- **Undo/redo** using per-stroke tile snapshots.
- **Unloaded vessel safety:** adjust stored altitudes of unloaded landed vessels inside edited areas.
- **Local voxel areas** for genuine excavation (caves and tunnels), hiding PQS underneath.
