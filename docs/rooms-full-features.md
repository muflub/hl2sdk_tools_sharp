# Rooms: full map-feature support

An audit of what the room pipeline (`ssmap room`, `ssmap link`,
`ssmap link --flatten`) does with every map feature today, and a design for
carrying all of them. It is a plan, not an implementation: no product code
changes with it.

Everything here is grounded in this repository's code. Classes and methods
are named so each claim can be checked. Where the behaviour is not certain
from the code (usually because it depends on the engine or on game code,
neither of which is in this repository), the text says **uncertain** and
what would settle it.

Contents:

1. [How the pipeline works today](#1-how-the-pipeline-works-today)
2. [Findings: silently wrong today](#2-findings-silently-wrong-today)
3. [Feature matrix](#3-feature-matrix)
4. [Features, one by one](#4-features-one-by-one)
5. [Entity naming and neighbour logic](#5-entity-naming-and-neighbour-logic)
6. [The entity budget](#6-the-entity-budget)
7. [Mod entity contract](#7-mod-entity-contract)
8. [Level-wide singletons](#8-level-wide-singletons)
9. [Lighting (option C, as decided)](#9-lighting-option-c-as-decided)
10. [Navigation](#10-navigation)
11. [Transition rooms and the level spawn](#11-transition-rooms-and-the-level-spawn)
12. [Engine limits for `CheckCapacity`](#12-engine-limits-for-checkcapacity)
13. [Implementation order](#13-implementation-order)
14. [Owner decisions](#14-owner-decisions)
15. [Testing](#15-testing)
16. [Growing the 3x3 sample](#16-growing-the-3x3-sample)
17. [Multiple libraries, room heights and large areas](#17-multiple-libraries-room-heights-and-large-areas)

Terms used throughout:

- **R** is a placement's quarter turn about +z, counter-clockwise seen from
  above; **t** is its translation. A room-local point `p` goes to `R·p + t`
  (`RoomTransform.Apply` in `Rooms/RoomModel.cs`). Rotation 1 maps
  `(x, y, z)` to `(-y + tx + cell, x + ty, z)`, so the room's authored +x
  (east) faces world +y (north).
- **Carried**: the linker relocates it into the linked map correctly.
  **Refused**: the linker (or the split, or the linter) stops with an error
  naming the room. **Silently wrong**: the link succeeds and the result is
  wrong.
- **Pack time**: `ssmap room` (`RoomLibraryCompiler`, `RoomCompiler`), where
  game files are available. **Link time**: `ssmap link` (`LevelLinker`),
  where they are not (decision D1).
- **Entity** means an entry of the linked entity lump. **Edict** means a
  networked entity slot at runtime, the 2048-slot limit (section 6).

---

## 1. How the pipeline works today

| Step | Code | What it does |
| --- | --- | --- |
| Split | `RoomLibraryVmf.Split`, `VmfPlacement` | Cuts the library VMF into room-local documents. World brushes and brush entities belong to the cell their brushes are in; a point entity belongs to the cell its `origin` is in (`RoomLibraryVmf.EntityOwner`). Point entities in the gaps between cells are ignored, and the `info_room` markers are left out. Each room is moved by `-corner` with `VmfPlacement.MoveSolid` / `MoveEntity`. |
| Model lint | `RoomLinter.CheckModel` | Every brush (world and entity) inside its cell; trigger world brushes are exactly the kit's plug boxes. |
| Compile | `RoomCompiler.CompileAsync` | `Vbsp.CompileAsync`, then `RoomLinter.CheckCompiled` (sealed, interior inside the cell, every socket plugged), then `Vvis.ComputeAsync`. **No vrad.** `RoomLibraryCompiler.CompileRoomAsync` sets `VbspContext.MapBase` to the room's name, lower-cased. |
| Pack | `RoomPack`, `RoomObjectStore` | One `SSROOM01` container per room (manifest, the BSP's lumps and game lumps byte for byte, vis rows). The pack has typed per-room sections (entity counts, the link profile, the door visibility `DVIS` of Q3, names, navigation) and a library section table; readers skip unknown tags. |
| Link | `LevelLinker.LinkAsync` | `RoomLinter.CheckLayout`, `CheckCapacity`, `ValidateJoints`, `RoomLinter.CheckReachable`, the cubemaps (`LevelCubemaps`, PR 12), the pak merge (`LevelPakFiles`, PR 5; `RefusePackedFilesAsync` before it), `PlanRoom` per room (refusals, transforms), `AssignBases`, door visibility (`LevelDoorVisibility`, Q3; the door graph's closure, `DoorEdges` and `CloseRows`, with `-nodoorvis`), `Assemble` (top tree, plug carve, merges), `MergeEntities`, `MergeCollision`. |
| Flatten | `LevelFlattener.Flatten` | The level as one VMF: rooms copied with `VmfPlacement`, joined plugs left out, every `id` renumbered (`LevelFlattener.Renumber`). |

The relocation is exact: a quarter turn permutes and negates components and
the translation is a whole number of cells (`RoomTransform`,
`LevelLinker.TransformPlanes`, `LevelLinker.TransformTexInfos`,
`LevelLinker.ApplyNormal`). Every feature below keeps that property:
positions go through `Apply`, directions through `ApplyNormal`, Euler angles
add `90 × turns` to yaw (exact, because yaw is the outermost rotation), and
nothing multiplies a rotation matrix.

### 1.1 Pack storage principles

Two rules shape every "precompute" entry in this document (D16, D17). The
goal is the fastest final link; larger files on disk are an accepted cost.

**Store per rotation whenever that makes the link faster.** Placements turn
only by quarter turns, which permute and negate coordinates, so any data in
coordinates *can* be turned exactly at link. Whether it *should* be is a
question of time: data is precomputed and stored for each rotation (×4)
whenever that makes the link faster, and stored once and turned at link
only where the turn is measurably free next to reading the data. Each
feature's PR measures and states which. The expected outcome:

- **×4, turned at pack time**, the default for data the link would
  otherwise transform element by element: geometry, planes, texture axes,
  node and leaf bounds (what the link profile, Q1, moves into the pack);
  collision ledges; entity text with its placement keys turned; prop,
  detail prop, displacement, overlay, cubemap and occluder records; leaf
  ambient cubes (their horizontal faces and sample positions permute).
- **Once, because the bytes do not change with rotation**: lightmaps and
  prop vertex colours of a room the sun and sky do not reach. They are
  stored per luxel or per vertex, in face or vertex order, and a turn moves
  none of them, so four copies would be four identical copies.
- **Once or ×4 by measurement**: the door response. It is the same at every
  rotation (only the incoming light turns), so it is stored once unless the
  prototype (9.7) shows that four pre-turned copies apply faster at link.
- **×4 by necessity**: the base lighting and the outgoing door capture of a
  room that sun or sky light reaches, because the sun is fixed in the world;
  static prop, detail prop, displacement and leaf-ambient lighting follow
  the same rule. Navigation data for a non-square agent hull (10.3), in the
  `.roomnav` rather than the pack (10.4).

**How the pack marks it.** Every per-room section that could hold rotation
variants starts with a rotation count, 1 or 4, followed by that many
payloads in rotation order; the link takes payload `rotation mod count`.
For lighting, the count is 4 when the room is sunlit: any sky face
(`SURF_SKY` or `SURF_SKY2D` in its texinfos) or any leaf that pass one of
`SkyLeafVisibility` flags; else 1. `ssmap rooms` shows each room's counts.

**Compression.** Every new pack section, and every section of the
navigation files (`.roomnav`, `.nav3d`, 10.4), carries a **codec byte** ahead of its payload: 0 none,
1 Deflate, 2 Brotli, then the uncompressed length (`int64`). Deflate and
Brotli are built into .NET (`System.IO.Compression`: `ZLibStream` /
`DeflateStream`, `BrotliEncoder`), so no package is added. The existing room
container section stays as it is until the pack's format version is raised
(the reader refuses unknown versions, `RoomPack`), which the first PR that
adds a section does.

- **The default is none.** A codec is used for a section only where it
  measurably beats raw reads at link with the pack warm in the page cache.
  For the navigation file the mod loads at runtime, the codec is chosen by
  the mod's load time.
- **Determinism.** A pack is a function of its rooms (`RoomPack` remarks),
  so compression must be too: fixed parameters (Deflate at a fixed level;
  Brotli at a fixed quality and window through `BrotliEncoder`, never the
  `CompressionLevel` enum, whose mapping may change), one thread per
  section, no timestamps.
- **Across platforms.** .NET ships its own zlib-ng and Brotli in the
  runtime's native compression library rather than using the operating
  system's, so the bytes should be the same on Linux, Windows and macOS. That
  is an expectation, not a guarantee: **pinned-byte facts** compress fixed
  inputs with each codec in use and compare against checked-in bytes, in CI
  on every operating system (15.9). If they ever diverge, the fallback is a
  small in-repo deterministic Deflate encoder; decompression stays the
  framework's.

### 1.2 What `PlanRoom` refuses today

In the order it checks:

1. any non-empty lump outside `LevelLinker.CarriedLumps`. Not in the set:
   `WorldLights(Hdr)`, `DispInfo`, `DispVerts`, `DispTris`,
   `DispLightmapAlphas`, `DispLightmapSamplePositions`, `LeafWaterData`,
   `WaterOverlays`, `LeafAmbientIndex(Hdr)`,
   `LeafAmbientLighting(Hdr)`, `LightingHdr`, `FacesHdr` (`Cubemaps` left
   the list with PR 12, `ClipPortalVerts` with PR 13; since PR 11
   `Overlays` and `OverlayFades` are carried, and a room with overlays is
   refused only when it carries no overlay data from its compile);
2. more than one model, or a world model whose head node is not 0 (since
   PR 7 brush models are carried, and a room with them is refused only when
   it carries no brush model data from its compile);
3. a leaf with `LeafWaterDataId != -1`;
4. more than two areas or more than one area portal (`RefuseAreaPortals`;
   since PR 13 areas and area portals are carried, and a room with them is
   refused only when it carries no area portal data from its compile,
   `RoomAreaPortalsOf`);
5. any non-zero byte in any game lump (`RefuseGameLumpContent`: static and
   detail props; since PR 6 static props are carried, and a static prop lump
   with content is refused only when the room carries no static prop data
   from its compile);
6. displacement collision (`RefuseDisplacementCollision`);
7. and, in `LinkAsync`, a pak holding any file (`RefusePackedFilesAsync`;
   since PR 5 the files are carried and only a pak that is not a zip is
   refused, `ReadPakAsync`).

`Assemble` takes the pak (until PR 5, which merges every room's), map flags
and game lumps from the first room only,
and the areas from the room with the most (all are `{0, 1}`), because every
other room's are known to be empty or equal (`RequireAgreement`). Since PR
13 that holds only for a level without area portals; one with them has its
areas planned (`PlanAreas`, `WriteAreas`).

---

## 2. Findings: silently wrong today

Things the link or the flatten accepts and gets wrong now. None affects the
3x3 sample, which has no such content, but each should be fixed or refused
before the features that depend on it land. They are the first PR of the
order in [section 13](#13-implementation-order).

1. **`info_ladder` bounds are not moved.** vbsp turns a `func_ladder` into
   world brushes plus an `info_ladder` point entity whose `mins.x` ...
   `maxs.z` keys hold the ladder's room-local bounds
   (`MapFileLoader.AddLadderKeys`). The room still has one model, so
   `PlanRoom` accepts it, and `LevelLinker.MoveEntity` moves only `origin`,
   `angles` and `angle`. The flattened compile recomputes the bounds from
   the moved brushes, so `EntitiesAreTheSame` would catch it if the sample
   had a ladder.
2. **`occludernumber` is not rebased.** `OccluderEmitter` writes
   `occludernumber` on each `func_occluder` (0-based per compile). The
   linker rebases the occlusion lump's polygons and vertex indices
   (`Assemble`, `OccluderPolyBase`, `OccluderVertexBase`) but not the key,
   so the second room's occluder entity names the first room's occluder.
   Rendering reads the lump and is right; toggling an occluder by input
   reaches the wrong one (the input path is game code, **uncertain** in
   detail; that the key indexes the lump is certain).
3. **`LevelFlattener.Renumber` breaks side references.** It renumbers every
   `id` in document order, including brush side ids, but `env_cubemap`,
   `info_overlay` and `info_no_dynamic_shadow` name brush sides by id in
   their `sides` key (`CubemapFixups`, `OverlaySet.AddFromEntity`,
   `MapFileLoader.HandleNoDynamicShadowsEntity`). The flattened reference
   attaches them to the wrong sides or none.
4. **Overlay bases are not moved by the split or the flatten.**
   `VmfPlacement.MoveEntity` moves `origin` only. An `info_overlay` is placed
   by `BasisOrigin`, `BasisU`, `BasisV` and `BasisNormal`
   (`OverlaySet.AddFromEntity`), which stay in library coordinates. The room
   compile builds the overlay in the wrong place (the linker then refuses
   the `Overlays` lump, so it never links, but the flatten compiles it).
5. **A room's `light_environment` is turned with the room.**
   `LevelLinker.MoveEntity` and `VmfPlacement.MoveEntity` turn `angles` on
   every entity. The sun is library-wide (decision D3), so a turned sun is
   wrong. Harmless today (nothing is lit at link), but the flattened
   reference compiled with vrad would carry one differently turned sun per
   room.
6. **Library-wide entities in the gaps vanish.** `RoomLibraryVmf.Split`
   ignores point entities outside every cell. That is documented, but it
   means a `light_environment`, `sky_camera` or `env_fog_controller` placed
   there, the natural place for a library-wide entity, is dropped without a
   word.
7. **Repeated rooms duplicate names.** A room placed twice carries the same
   `targetname`s twice; outputs fire at both copies. There is no name fixup
   anywhere: `MapInstanceMerger.MergeEntities` does not rename
   `func_instance` contents either, it warns `VBSP0107`
   (`MapLoadDiagnostics.InstanceNameFixupUnsupported`) because the port
   reads no FGD. [Section 5](#5-entity-naming-and-neighbour-logic) is the
   fix.
8. **Switchable light styles collide.** `EntityStage.SetLightStyles` gives
   each named light a style from 32 in first-seen order, per compile. Two
   rooms each with one named light both get style 32. Latent today (no
   lightmaps are linked), live under option C.
9. **Brush entities' `angles` would be turned twice** (**uncertain**; latent
   at link because brush entities are refused, live in the flatten).
   `MoveEntity` turns `angles` on brush entities too, while their brushes
   are also turned. For a class that applies `angles` to its model at spawn
   that is a double turn; for one that reads it as a direction it is right.
   Which classes do which is game code. The safe rule is in
   [4.1](#41-brush-entities). **Fixed by PR 7** (section 13, its landed
   note): a brush entity's `angles` are carried as written, and refused
   unless zero, for any class outside the known-direction table.
10. **Real game content makes every room unlinkable.** With a game whose sky
    VTFs resolve, `DefaultCubemapBuilder.CreateAsync` writes
    `materials/maps/<room>/cubemapdefault.vtf` (and `.hdr.vtf`) into every
    room's pak, and `RefusePackedFilesAsync` refuses it. The 3x3 sample
    ships no textures, so its builder warns and writes nothing. Not silently
    wrong, but it blocks any real library; packed files
    ([4.13](#413-packed-files)) fix it. **Fixed by PR 5** (section 13): an
    interim change had turned the default cubemaps off in room compiles;
    PR 5 turns them back on and the link carries them, renamed to the
    level's map name.

---

## 3. Feature matrix

"Today" is what the link does. "Pack" is what can be precomputed per room,
stored per rotation where that makes the link faster, once where the bytes
do not change or the turn is free (1.1); "Link" is what has to wait for the level. "Entities" is
the runtime entity cost ([section 6](#6-the-entity-budget)). Size is the
work to carry the feature: S (days), M (a week or two), L (several weeks,
or research).

| Feature | Today | Pack (per room / rotation) | Link | Entities | Size |
| --- | --- | --- | --- | --- | --- |
| Point entities | carried (origin, yaw); names duplicated | name and I/O positions, parsed placeholders | resolve names, drop/keep, fold, singletons | 1 each; logic may fold to 0 | M |
| Brush entities | carried since PR 7 (own models, origin-relative in the entity's frame, per-model collision, (c) omission, socket furniture) | models, subtrees, per-model collision, origin class | rebase models, `model` keys, texinfo split for origin models | 1 each | L |
| `func_ladder` | silently wrong (`info_ladder` bounds) | bounds per rotation | none | 1 (`info_ladder`) | S |
| Static props | carried since PR 6 (moved, filtered, dictionaries merged, leaves recomputed, `.vhv` renamed) | props per rotation, dictionary, hulls; lighting ×1, or ×4 if sunlit | merge dictionary, recompute leaf lists, rename `.vhv` | 0 | M |
| Detail props | refused (game lump) | props per rotation, leaf-local runs; lighting ×1, or ×4 if sunlit | renumber leaves, re-sort, merge dictionaries | 0 | M |
| Displacements | refused at split (`VmfPlacement.MoveSide`) | lumps and collision per rotation, sample positions | rebase; cross-room neighbours only if allowed | 0 | L |
| Water | refused (water leaf, lump) | water data, fog ids, patched materials, fluid collision | doorway water carve, distance to water | 0 (1 `water_lod_control` per level) | L |
| Overlays | carried since PR 11 (moved and turned, ids, texinfos and faces rebased, accessors renumbered; water overlays refused with water) | overlays per rotation | rebase faces, texinfos, ids, fades | 0 unnamed, 1 named | M |
| Decals (`infodecal`) | carried | nothing | nothing | 1 each (**uncertain** after spawn) | S |
| `env_cubemap` | carried since PR 12 (samples moved, patches and copies renamed to the level) | samples per rotation, patch list | rename VTFs and patched VMTs to the level | 0 | M-L |
| Area portals | carried since PR 13 (areas joined at joints, portals and `portalnumber`s rebased, clip verts moved; door portals opt-in) | areas, portals, clip verts per rotation | area union across joints, optional door portals | 1 per portal | L |
| Occluders | carried; `occludernumber` wrong | occluders per rotation | rebase the key | 1 each (strip candidate) | S |
| Packed files | carried since PR 5 (merged, deduped, default cubemaps renamed) | the room's pak entries | merge, dedupe, rename | 0 | M |
| 2D sky | faces carried; no leaf sky flags (no vrad) | sky leaves per room | propagate sky flags across doors | 0 | S |
| 3D skybox | carried since PR 13 (the library's `info_room_skybox` room, placed below the grid, its own area) | the skybox as a library section | place it, its own area | 1 `sky_camera` per level | M |
| Transition rooms and spawn | not possible | volume, arrival and spawn POIs per rotation | destinations, emission per mode, spawn | 2 per level (mod), 3 to 5 (stock) | M |
| Navigation (3D) and points of interest | none | in `<library>.roomnav`: volumes, door portals and POIs per rotation | stitch at joined doors into the `<map>.nav3d` sidecar | 0 (POIs stripped) | L, blocked (section 10) |
| Lighting | none (no vrad at pack time) | base and capture ×1, or ×4 if sunlit; door response ×1 or ×4 by measurement | sum captures × responses | lights: see 6.3 | L |

---

## 4. Features, one by one

Each feature follows the same outline: **emits** (what vbsp, vvis and vrad
write), **today**, **pack vs link**, **transforms**, **cross-room**,
**lighting**, **entity cost**, **equivalence**, **risk and size**,
**limits**.

### 4.1 Brush entities

`func_door`, `func_door_rotating`, `func_button`, `func_brush`,
`func_illusionary`, `func_wall_toggle`, `func_movelinear`, `func_rotating`,
`func_breakable`, `func_physbox`, every `trigger_*`, `func_clip_vphysics`,
`func_water_analog`, and so on. `func_detail` is already fine: the loader
moves its brushes into the world and clears the entity (`MapFileLoader`,
the `func_detail` branch). `func_areaportal`, `func_occluder`,
`func_viscluster` and `func_ladder` are brush entities vbsp consumes; they
have their own entries.

**Emits.** `EntityStage.SetModelNumbers` gives each brush entity
`"model" "*N"` in entity order. Each model is a `DModel` (bounds, origin,
head node, face range) whose tree lives in the shared `Nodes` / `Leafs`
lumps, with its own faces, brushes and brush sides. A brush entity with an
origin brush is rebuilt relative to that origin
(`MapFileLoader.RebuildForOriginAsync`), so its geometry is entity-local
and the entity keeps `origin`. `PhysCollisionEmitter` writes one
`PhysCollide` record per model (`PhysCollideLump`, keyed by model index).
vvis ignores brush entity models. vrad lights their faces; they cast
shadows only when asked to.

**Today.** Carried since PR 7 (section 13, its landed note). Before it,
`PlanRoom` stopped at `models.Length != 1`, and the split and the flatten
carried brush entities, turning their brushes and (finding 9) their
`angles`.

**Pack vs link.** Everything per model is room-only and precomputed per
rotation: moved bounds, subtree, faces, brushes, collision record, and
for each model the lump ranges it owns (so the link can omit it,
[5.8](#58-missing-neighbours-decided)). At link only bases change: the
model index (`*k` becomes `*(ModelBase + k)`), node and leaf bases as for
the world tree, face and brush bases, the `PhysCollide` record's index.

**Transforms.** Two classes of model, recorded per model in the pack:

- **World-coordinate models** (no origin brush): vertices, planes and
  texture axes take the full `R·p + t`, as world geometry does today.
- **Origin-relative models**: vertices and planes take `R` only (the
  entity's `origin` supplies the translation), and their texture axes take
  `R` with no offset correction. A texinfo vbsp shared between a world face
  and an origin-relative face needs two linked copies. That is the tricky
  part; the shared texinfo table (queued work Q2) makes the split a lookup.

Keys that carry direction on brush entities: `movedir` (an Euler triple:
add to yaw), `angles` (below), and the axis bits in `spawnflags` on
`func_door_rotating` and `func_rotating` (**uncertain** whether they read in
the entity's frame; if so, turning `angles` is enough, otherwise X and Y
swap under odd turns). The **safe rule for `angles`**: a world-coordinate
model's brushes are already turned, so its `angles` must stay as authored
if the class applies `angles` to the model, and turn if the class reads it
as a direction. Until a class table exists, the pack refuses a brush entity
whose `angles` is not `0 0 0` and whose class is not in a known-direction
list, naming the entity; the flatten applies the same rule.

**Cross-room.** Brushes may not leave their cell (`RoomLinter.CheckModel`),
so a brush entity cannot straddle a door. A door *in* a doorway is wanted:

**Socket furniture.** A `func_door` (or a door frame prop) that fills a
socket's plug box is the natural way to put a door in a doorway. In the
room compile it is a separate model, so it does not break the seal the plug
brush makes; at a joint the plug is stripped and the door remains. But both
rooms of a joint may carry a door (two doors back to back, each
`wall_depth` deep), and at a capped socket the door is buried in the wall.
Proposed (O5): a key `room_socket` naming the socket marks the entity as
socket furniture; at a joint the linker keeps one side's (default: the room
earlier in link order; a `socket_priority` key overrides) and drops the
other's, and at a cap it drops it. Dropping is the (c) mechanism of
[5.8](#58-missing-neighbours-decided), so it costs no entities and halves
the doors.

**Lighting.** Brush entity faces get lightmaps in the base bake like world
faces. A closed `func_door` in the opening is one of the errors in
[9.6](#96-error-left).

**Entity cost.** One entity (and edict) per brush entity, plus a model.
Minimise: static geometry is `func_detail` or world, never `func_brush`;
player and NPC clips are world brushes with `tools/toolsplayerclip` rather
than `func_clip_*` entities; one `trigger_multiple` with several brushes
rather than several triggers; door sounds through the door's own keys
rather than `ambient_generic`s; socket furniture on one side of a joint
only.

**Equivalence.** Same models by class, name and bounds; the same faces per
model (area per material); traces against each model at its origin; the
same collision convexes per model. Model numbers may differ between link
and flatten order and are compared through the entities that name them.

**Risk and size: L.** The origin-relative split, texinfo sharing and the
per-class `angles` question.

**Limits.** Models ≤ 1024 (`BspLimits.Caps`); entities ≤ 8192
(`MapFile.MaxMapEntities`); the edict budget (section 6).

### 4.2 Point entities and their I/O

**Emits.** vbsp copies point entities to the entity lump
(`EntityStage.Unparse`, which skips entities with no pairs), after consuming
some (6.3). It adds a default `water_lod_control` when a map has water and
none (`EntityStage.EnsurePresenceOfWaterLodControlEntity`) and writes
`world_mins`/`world_maxs` on worldspawn (`EntityStage.ComputeBoundsNoSkybox`).
I/O is ordinary key/value pairs on the entity (the VMF's `connections`
chunk, `MapFileLoader.ConnectionsChunk`), each value
`target,input,parameter,delay,times` (comma or ESC separated).

**Today.** Carried: `LevelLinker.MergeEntities` parses each room's lump,
keeps one worldspawn (all rooms must agree, `RequireSameWorld`, except
`world_mins`/`world_maxs`/`hammerid`), moves `origin` and turns yaw
(`MoveEntity`). Silently wrong: names (finding 7), ladders (1), occluders
(2), suns (5), switched light styles (8).

**Pack vs link.** Per room: the parsed entity list; for each entity the
positions of its placement keys, name-valued keys and output fields, with
parsed placeholders (section 5); its runtime class cost (section 6); its
`room_needs` condition; and the fold analysis that does not depend on the
level (6.5). The placement keys are turned per rotation at pack time (1.1). At link: name
resolution, drops, folding, singletons, style renumbering.

**Transforms.** A table of position and direction keys:

| Key | Transform |
| --- | --- |
| `origin` | `Apply` |
| `angles`, `angle` (not -1 / -2), `movedir`, `gibdir` | yaw + 90 × turns |
| `pitch` (lights) | unchanged |
| `mins.x` ... `maxs.z` (`info_ladder`) | the box through `MoveBox` |
| `BasisOrigin` (`info_overlay`) | `Apply` |
| `BasisU`, `BasisV`, `BasisNormal` | `ApplyNormal` |
| `lightingorigin` and every other name key | a name, not a position |
| anything else holding a position | **not known without an FGD** (the `gameinfo.txt` files name one, e.g. `GameData "tf.fgd"`, but none is in the repo and nothing reads it); the pack lists keys whose value parses as three numbers and warns, so an author sees what was not moved |

**Cross-room.** Only through names (section 5).

**Lighting.** `light`, `light_spot`, `light_dynamic` positions and styles
feed the base bake (section 9).

**Entity cost.** One each, except what vbsp consumes (6.3) and what the
link strips or folds (6.4, 6.5).

**Equivalence.** `EntitiesAreTheSame` compares class, origin and yaw today.
Extend it to every key, with names compared after resolution and folding
(both paths share the code, [5.9](#59-link-and-flatten-must-agree)).

**Risk and size: M**, most of it section 5.

**Limits.** Entities ≤ 8192; edicts (section 6).

### 4.3 Static props (`prop_static`)

**Emits.** `StaticPropEmitter` writes the `sprp` game lump: the model
dictionary, a leaf list (`StaticPropLeaves`: the leaves the prop's hull
touches in the written tree) and one record per prop (origin, angles, model
index, leaf run, solidity, skin, fades, lighting origin, flags, DX levels,
lightmap resolution at version 10). It removes `prop_static` and
`info_lighting` from the entity lump, resolving `lightingorigin` to a
position. vrad lights each prop per vertex and writes `sp_N.vhv` /
`sp_hdr_N.vhv` into the pak, named by prop index
(`StaticPropLighting.FileName`, `WriteIntoAsync`). Texel-lit props
(`texelslighting_N.ppl`) are not ported (`StaticPropLighting` remarks).

**Today.** Carried since PR 6 (section 13, its landed note).

**Pack vs link.** Per rotation: each prop's moved record; its hull (convex
hull planes, or the prop-space box and hull) so the link can recompute leaf
lists **without the model**; its base lighting and door response per vertex
(section 9); its `room_needs` condition. Per room: the dictionary. At link:
merge dictionaries by name, renumber `PropType`, drop props whose condition
fails, recompute each prop's leaf list against the linked tree, rename the
`.vhv` files to the linked prop index, write the level's pak.

**Transforms.** Origin and lighting origin `Apply`; angles yaw + 90 × turns
(exact). Vertex lighting is per vertex in model order and does not move.

**Cross-room.** A prop whose hull crosses the cell face is owned by the room
its origin is in (`RoomLibraryVmf.EntityOwner`). Its pack-time leaf list
holds only its own room's leaves, so the link recomputes it from the stored
hull. Its lighting is the hard part: in the room's own bake the part beyond
the cell face is inside the plug or the void, so those vertices are black.
Proposed (O6): refuse props whose hull leaves the cell, except socket
furniture inside the plug box.

**Lighting.** Base per-vertex lighting, ×4 only in a sunlit room (1.1); no capture (props do
not emit); door response entries for vertices that receive light through a
door (9.4). A conditional prop (`room_needs`) must not cast shadows in the
bake (`disableshadows`), or dropping it would leave its shadow; the pack
refuses otherwise. Texel-lit props: refuse until vrad supports them (O13).

**Entity cost.** **Zero**: a static prop is a `sprp` record. It is the
cheapest way to place a model. `prop_dynamic` and `prop_physics` cost one
edict each; use them only for props that move, animate or are named.

**Equivalence.** Same props (model, origin, angles, skin, solidity, flags,
fades) after sorting by position; each prop's leaf list covers the leaves
its hull overlaps in the linked map; lighting within tolerance (9.8).

**Risk and size: M.** Stored hulls, leaf recompute, the pak dependency.

**Limits.** Props and dictionary entries `ushort`-indexed; the leaf list's
`FirstLeaf` `ushort` (≤ 65,535 entries in all).

### 4.4 Detail props

**Emits.** `DetailPropEmitter` scatters props on every face whose material
has `%detailtype` (and on displacements), plus `prop_detail` entities, into
`dprp`: model and sprite dictionaries and one `DetailObjectLump` per prop
(origin, angles, model, **leaf**, lighting, style run, orientation, type,
scale). Placement is seeded per face by its **Hammer face id**
(`srand(hammerfaceid)`), consumed in face order, then sorted by leaf with
the CRT's unstable `qsort` (`DetailPropEmitter` remarks). vrad writes
`dplt`/`dplh` and the prop's lighting field (`DetailPropLighting`).

**Today.** Refused by `RefuseGameLumpContent`.

**Pack vs link.** Per rotation: the props, moved, with their room-local
leaf and lighting. At link: merge dictionaries, rebase each prop's leaf by
the room's leaf base, re-sort by leaf (**uncertain** whether the engine
needs a strict order or only contiguity per leaf; a stable sort by linked
leaf is safe either way), rebase `dplt` runs and style numbers.

**Transforms.** Origin `Apply`, yaw + 90 × turns. Screen-aligned sprites are
unaffected.

**Cross-room.** Props lie on faces and faces do not cross cells, so none
straddles. A doorway has no floor face (it faced the plug), so no detail
props; the flattened compile has some there. Part of the doorway-face
exception.

**Lighting.** Per-prop base lighting, ×4 only in a sunlit room; door response entries
for props near doors (one sample point each, so cheap).

**Entity cost.** Zero (`prop_detail` entities are consumed).

**Equivalence.** **Not per prop.** The flattened compile renumbers face ids
(`LevelFlattener.Renumber`) and splits faces differently, so its props are
another random draw. Compare per room and material: count within a
tolerance, the same dictionary, props on the same surfaces, a distributional
test of positions (`Compare/DistributionalLumpDiff`). The exact test is
link against the room's own compile, prop for prop, after the transform.

**Risk and size: M.** **Limits.** 65,535 props
(`DetailPropEmitter.MaxDetailProps`); leaf field `ushort`.

### 4.5 Displacements

**Emits.** vbsp: `DispInfo` (start position, power, vertex and triangle
starts, map face, lightmap alpha and sample position starts, edge and
corner neighbours, allowed verts), `DispVerts`, `DispTris`,
`DispLightmapAlphas`, `DispLightmapSamplePositions`, `PhysDisp`, and each
displacement face's `DFace.DispInfo`. Neighbours come from
`Disp/DispNeighbourFinder`; normals are smoothed across neighbours
(`Disp/DispNormalSmoother`). vrad lights the surface (`Rad/Displacement/`).

**Today.** Refused at the split: `VmfPlacement.MoveSide` throws on a
`dispinfo` chunk; also by lump and by `RefuseDisplacementCollision`.

**Pack vs link.** Per rotation: all displacement lumps and collision, moved. At link:
rebase `DispInfo` indices (vertex, triangle, alpha and sample-position
starts, map face, neighbour indices) and faces' `DispInfo`; merge
`PhysDisp`; recompute `AllowedVerts` only where cross-room neighbours are
added.

**Transforms.** `StartPosition` `Apply`; `DispVert.Vector` `ApplyNormal`;
distance and alpha unchanged. The base face turns with its corner order
kept, so the start corner, vertex order and neighbour orientation codes
(relative to each displacement's own corners) are unchanged. `PhysDisp`:
the framing is known (`PhysDispLump`) but whether its blobs hold world-space
data is **uncertain** from this repo; read it before relocating.

**Cross-room.** Stitching across a joint (terrain through a door) means
matching edges in world space (`DispNeighbourFinder`'s test), neighbour
records, allowed verts, and re-smoothing normals along the edge, which
changes lighting there. Proposed (O8): refuse a displacement edge on a
socket plug box at pack time; the doorway floor stays a brush.

**Lighting.** Displacement lightmaps are face lightmaps with their own
sample positions: base, capture and response as for faces.

**Entity cost.** Zero.

**Equivalence.** Same surfaces (vertex positions within float tolerance),
same collision per displacement, traces agree.

**Risk and size: L.** **Limits.** `MAX_MAP_DISPINFO` (2048 in the SDK; not
in this repo's tables, confirm); `DFace.DispInfo` `short`; neighbour indices
`ushort`; `MaxDispPower` 4 (`BspLimits`).

### 4.6 Water

**Emits.** vbsp: water leaves, `LeafWaterData` (surface z, min z, surface
texinfo) referenced by `DLeaf.LeafWaterDataId`, each warped face's
`SurfaceFogVolumeId`, per-depth patched water materials
`maps/<map>/<material>_depth_<n>` in the pak (`WaterVolumes`,
`SurfaceContentExtension`), a default `water_lod_control`, and fluid
records in `PhysCollide` (`PhysCollisionEmitter`, `PhysFluidEntry`). vvis
writes `LeafMinDistToWater` (`VisFlow`).

**Today.** Refused: water leaf check and `LeafWaterData` outside the set.
`LeafMinDistToWater` is carried (always written).

**Pack vs link.** Per room: water data, fog ids, patched materials, fluid
records. At link: rebase `LeafWaterDataId`, fog ids and texinfos; merge
fluids into the collision; recompute `LeafMinDistToWater` for the level
(the room's values ignore the neighbour's water; the recompute needs only
leaf boxes and water leaves); keep one `water_lod_control` (section 8).

**Transforms.** Water surfaces are horizontal and turns are about +z:
surface z, min z and fluid planes are unchanged; indices only.

**Cross-room: water through a door.** Each room's water stops at the plug
(solid wins), so after the link the carved doorway leaf is **empty** (an air
gap as deep as two walls), no surface face spans the doorway, and no fluid
covers it. A fix needs the kit to know the water level at a socket: the
carve (`LevelLinker.CarveLeaf`) splits the doorway box at the surface plane,
the lower part becomes a water leaf of the facing water data, and the
linker adds a surface face and a fluid convex. All computable at link.
Proposed (O7): first refuse water touching a socket plug box; add "water
sockets" later.

**Lighting.** Water surfaces are lit like faces.

**Entity cost.** Zero per room; one `water_lod_control` per level (vbsp adds
one per room, the link keeps one). `func_water_analog` costs one.

**Equivalence.** Point contents on the lattice cover water; add surface z
per water leaf, fog volume per warped face, fluid volumes.

**Risk and size: L** with water sockets, **M** without. **Limits.**
`LeafWaterData` ≤ 32,768 (`WriteLimits.MaxMapLeafWaterData`); patched
materials add texdata (≤ 2048).

### 4.7 Occluders (`func_occluder`)

**Emits.** `OccluderEmitter`: the occlusion lump (occluders, polygons,
vertex indices into the vertex lump, area per occluder); the entity keeps
`occludernumber` and loses its brushes and model.

**Today.** Carried, except `occludernumber` (finding 2). Occluder areas are
all 1, right while areas are collapsed.

**Pack vs link.** Rebase `occludernumber`; remap `DOccluderData.Area` once
areas are real (4.11).

**Transforms.** Boxes through `MoveBox`, planes through `PlaneRef`, as
today. **Cross-room.** None. **Lighting.** None.

**Entity cost.** One per occluder, kept only so inputs can toggle it. An
occluder that no output names and whose start state is its lump flag could
be stripped at link (**uncertain**: whether the engine or game needs the
entity to activate it; verify in game before making it a default).

**Equivalence.** Same occluders (polygons in world space); the key names
the occluder at the same place in both maps.

**Risk and size: S.** **Limits.** None beyond counts already checked.

### 4.8 Decals (`infodecal`)

A point entity the engine or game projects onto the world at load. Carried:
origin moves, the surface is found at runtime. **Uncertain:** whether the
projection near a joined doorway can pick the neighbour's face (the
flattened map has the same geometry, so it would do the same), and whether
the entity is removed after applying (which decides its steady-state cost).
**S.**

### 4.9 Overlays

**Emits.** `OverlaySet`: `Overlays` (id, texinfo, up to 64 faces, U/V
ranges, four UV points with `BasisU` packed into the first three `z`s and a
V-flip flag in the fourth, origin, basis normal), `OverlayFades`,
`WaterOverlays` (up to 256 faces, ids from `MaxMapOverlays + 1`); a named
overlay becomes an `info_overlay_accessor` with `OverlayID`, an unnamed one
is cleared (`OverlaySet.AddFromEntity`, `EmitAsync`, `FillUv`).

**Today.** Carried since PR 11 (section 13, its landed note). Before it,
refused (`Overlays` lump), and misplaced at the split (finding 4) and in the
flatten (findings 3 and 4) until PR 1.

**Pack vs link.** Per rotation: the moved overlays. At link: rebase face
indices (dropping stripped plug faces), texinfo, ids, `OverlayID` keys,
water overlay ids; append fades in order.

**Transforms.** `Origin` `Apply`; `BasisNormal` and the packed `BasisU`
`ApplyNormal`; UV points are in the overlay's basis and do not move; the
V-flip flag is unchanged (a turn keeps handedness).

**Cross-room.** An overlay names sides of its own room only. Refuse `sides`
naming a plug face; one overlay per room at a doorway.

**Lighting.** Drawn with the lightmaps of the faces under them.

**Entity cost.** Zero unnamed; **one per named overlay** (the accessor).
Name an overlay only if something toggles it.

**Equivalence.** Same overlays by material, origin and basis; the face list
covers the same drawn area (compare union polygons; face counts differ).

**Risk and size: M.** **Limits.** 512 overlays, 16,384 water overlays
(`MapOverlay`); 64 and 256 faces each (`BspLimits`); texinfo count.

### 4.10 `env_cubemap` and the cubemap lump

**Emits.** The loader turns each `env_cubemap` into a sample and clears the
entity (`MapFileLoader`, `CubemapSample`). `CubemapFixups` points every
specular side at a patched copy of its material,
`maps/<map>/<material>_<x>_<y>_<z>` with `$envmap` naming
`maps/<map>/c<x>_<y>_<z>`, from the sample's origin and `MapBase`; sides not
named by a cubemap's `sides` go to the nearest sample. The `Cubemaps` lump
lists samples. `DefaultCubemapBuilder` writes `cubemapdefault(.hdr).vtf`
and a copy per sample into the pak from the sky's VTFs.

**Today.** Carried since PR 12 (section 13, its landed note). Before it,
refused (`Cubemaps` lump; patched materials and VTFs in the pak).

**The naming problem.** The engine names cubemap textures from the **map's
name** and the sample's **world** origin (`buildcubemaps` writes
`materials/maps/<mapname>/c<x>_<y>_<z>.vtf`). A room's patched materials
name `maps/<room>/c<local x>_<local y>_<local z>`, so after linking two
placements share one texture name, it does not match what `buildcubemaps`
writes, and default cubemaps are named after the room.

**Pack vs link.** Per rotation: the samples, and the list of patched
materials with their source VMT and sample index. At link, knowing the
output map's name (it must be; renaming the `.bsp` afterwards breaks
cubemaps as it does for stock maps): write each sample with its world
origin, one patched VMT per (material, placement, sample) named
`maps/<level>/<material>_<X>_<Y>_<Z>` with `$envmap`
`maps/<level>/c<X>_<Y>_<Z>`, texdata strings renamed to match, and default
cubemaps under the level's name. All text: the patch is a key insertion into
a VMT the pack stores.

**Transforms.** Sample origin `Apply` (integers stay integers).

**Cross-room.** A side's nearest cubemap in the room compile is in its room;
in the flattened compile it may be a neighbour's, near a door. Default (O9):
keep the room's own assignment and accept the difference.

**Lighting.** None; cubemaps are built in game.

**Entity cost.** Zero (consumed; certain).

**Equivalence.** Same samples in world space; every specular face's
`$envmap` names a sample of its own room, compared with the flatten only
where the flatten's nearest is in the same room.

**Risk and size: M-L.** Texdata pressure: every (specular material × sample
× placement) is a texdata and texinfo family.

**Limits.** `MAX_MAP_CUBEMAPSAMPLES` (1024 in the SDK; since PR 12
`WriteLimits.MaxMapCubemapSamples`, which the link refuses past and `ssmap
check` warns past, BSP0039); texdata ≤ 2048 and texinfo ≤ 12,288
(`BspLimits.Caps`), which cubemap patches reach first.

### 4.11 Area portals and areas

**Emits.** A `func_areaportal` (or `func_areaportalwindow`) becomes
`CONTENTS_AREAPORTAL` world brushes and a `portalnumber` key; the entity
stays (`MapFileLoader`, the area portal branch). vbsp floods areas
(`Portals/AreaFlood`) and writes `Areas`, `AreaPortals` (each portal listed
from both sides, `PortalKey` = portal number, `OtherArea`, a plane, a range
of `ClipPortalVerts`), each leaf's and node's area, and occluder areas
(`AreaPortalEmitter`, `OccluderEmitter`).
`VbspCompilation.Compute3DSkyboxAreas` records the areas holding a
`sky_camera`.

**Today.** Carried since PR 13 (section 13, its landed note), door portals
included as a library's opt-in. Before it, refused (`RefuseAreaPortals`);
every room was area 1 and the level one area (`Assemble`). The linker's own
remarks gave the reason: two areas with no portal between them are two
worlds to the server, which the union below keeps from happening.

**Pack vs link.** Per room: areas, portals, clip verts, leaf and node areas,
`portalnumber` keys. At link:

1. Give each room an area base. Areas that meet at a **joint with no
   portal** become one: union-find over (room, area), joined wherever a
   joint's facing leaves lie in the two areas. That generalises today's
   collapse: portal-less rooms still make one area.
2. Rebase portals, `PortalKey`s and `portalnumber`s; renumber areas through
   the union; rebuild the area lump (each area's portal run, sorted by area).
3. The carved doorway leaf takes its facing side's area
   (`LevelLinker.OpenArea` does this for area 1 today).

**Area portals as doors** (optional, O10): at a joint, add a portal on the
doorway rectangle, its clip verts, and a `func_areaportal` with
`portalnumber` and, if socket furniture is a door, `target` naming it (else
`StartOpen 1`). The engine starts portals closed until their entity opens
them (**uncertain** for a portal whose entity has no target; `StartOpen 1`
is the usual way, verify in game). Separate areas cut server traffic but
spend areas (≤ 255 usable) and **one entity per joint**. Default: joints
open, portals only where the author put one.

**Transforms.** Portal planes through `PlaneRef`; clip verts `Apply`.

**Cross-room.** Only through joints. An author's area portal may not sit in
a socket: refuse at pack time.

**Lighting.** vrad ignores area portals except for sky cameras (4.12).

**Entity cost.** One per `func_areaportal`; door portals are opt-in because
each costs one.

**Equivalence.** Same area partition of the lattice (area of every open
point, up to renaming); same portals by plane and polygon.

**Risk and size: L.** **Limits.** Areas ≤ 256, area portals ≤ 1024 (each
counts twice), clip verts `ushort` start (`MaxMapPortalVerts` 128,000 in
`WriteLimits`), leaf area 9 bits (`DLeaf.GetArea`).

### 4.12 Sky and the 3D skybox

**Emits.** Sky faces carry `SURF_SKY` / `SURF_SKY2D` texinfo flags. vrad sets
`LEAF_FLAGS_SKY` / `SKY2D` on leaves that hold or see sky faces
(`SkyLeafVisibility`, three passes, the second over the PVS;
`RadLumpWriter.WriteLeafFlags`). A 3D skybox is a sealed region with a
`sky_camera`, in its own area (`VbspCompilation.Compute3DSkyboxAreas`); vbsp
leaves it out of the world bounds (`EntityStage.ComputeBoundsNoSkybox`) and
vrad recasts sky rays into it from camera-less areas (`Rad/Light/SkyCameras`).

**Today.** 2D sky faces carried; leaf sky flags since PR 9 (its landed
note). The 3D skybox is carried since PR 13 (section 13, its landed note):
the library's `info_room_skybox` room below every level's grid, its own
area. Before it, a 3D skybox could not exist: it needs its own area and is
outside every cell.

**Pack vs link.** Per room: its sky leaves (pass one). At link: pass two over
the linked PVS (a leaf is sky-visible if a sky leaf is in its row), cheap and
without game files. The 3D skybox is a **library section** (O11): an
`info_room_skybox`-marked region compiled once like a socket-less room,
placed by the linker outside the grid as its own area, never joined; needs
areas (4.11).

**Transforms.** The skybox is never turned.

**Lighting.** The sun and sky are library-wide (D3). Every room's bake
includes the skybox geometry for the sky-ray recast.

**Entity cost.** One `sky_camera` per level.

**Equivalence.** Same leaf sky flags per lattice point's leaf; the skybox's
contents and area.

**Risk and size: S** (2D), **M** (3D). **Limits.** One area.

### 4.13 Packed files

**Emits.** The pak holds patched materials (cubemap, water depth,
`WorldVertexTransitionFixup`'s `_wvt_patch`), default cubemap VTFs, static
prop `.vhv` files (vrad), and anything an author embeds. Patched names
contain `MapBase`, the room's name.

**Today.** Refused if any file; with real content every room holds the
default cubemaps (finding 10). Carried since PR 5 (section 13, its landed
note).

**Pack vs link.** Per room: the entries. At link: one archive, merged by
name. Names with the room's `MapBase` are unique per room and shared by its
placements, which is right for identical bytes (water depth, WVT). Cubemaps
and `.vhv` are renamed (4.10, 4.3). Two rooms with the same name and
different bytes are refused, naming both. `ZipArchiveReader` and
`ZipArchiveWriter` exist (`StaticPropLighting.WriteIntoAsync` uses them).

**Entity cost.** Zero. **Equivalence.** Same files after renaming.
**Risk and size: M.** **Limits.** None in the format beyond 32-bit lump
offsets.

### 4.14 Everything else vbsp, vvis and vrad handle

| Feature | Status and plan |
| --- | --- |
| `func_detail` | Fine (merged into the world, entity cleared). |
| Hint / skip | Fine: shapes the room's own tree and vis. |
| `func_viscluster` | Fine: the loader builds its volume (`MapFile.VisClusters`), the portal file merges every leaf it covers into one cluster, and the entity is cleared, so it gets no model and no entity lump record (compile-only, 0 entities). |
| `func_ladder` | Silently wrong (finding 1); move `mins.*` / `maxs.*` through `MoveBox`. **S.** |
| `func_instance` inside a room | Merged at pack time (`MapInstanceMerger`; `MapFileReader.CheckForInstances` blanks every `func_instance`), no name fixup (`VBSP0107`); its contents are room entities and follow section 5. |
| Cordons | Refuse a cordoned library (a room compile would cut the room). **S.** |
| `info_no_dynamic_shadow`, `%compile*` flags, surface props | Texinfo and contents: carried; the entity is consumed. |
| Macro textures | `FaceMacroTextureInfo` carried (`Assemble`). |
| Vertex normals, primitives | Carried; vrad rewrites vertex normals (`RadLumpWriter.Write`), so under option C they come from the base bake. |
| `WorldLights(Hdr)` | Per room, the lights moved per rotation; at link concatenated with each `Cluster` rebased by the room's cluster base, and one sky light and sky ambient for the level (D3). `MAX_MAP_WORLDLIGHTS` 8192 (`WorldLightExporter`). **S.** |
| Leaf ambient | Samples per leaf (a compressed cube and a position in the leaf box). From the base bake (×4 only for a sunlit room), rebased by leaf, plus door response. Under a turn the position bytes permute with the box axes and the cube's horizontal faces permute. The carved doorway leaf copies its facing leaf's samples. `DLeafAmbientIndex.FirstAmbientSample` is `ushort`. **M**, with lighting. |
| `LightingHdr`, `FacesHdr` | Not carried; from the bake under option C. |
| `MapFlags` | Must agree (`RequireAgreement`); vrad sets the baked-prop-lighting flag (`RadLumpWriter.WriteLevelFlags`). |
| `LeafMinDistToWater` | Carried; recompute at link once water exists (4.6). |
| Fog, tonemap, `shadow_control`, `water_lod_control` | Singletons: section 8. |
| AI nodes (`info_node` and kin) and nav data | Carried as point entities today (nothing in vbsp here consumes them). Navigation is required and 3D, for a new AI system, and blocked on its design (section 10); points of interest go into the navigation data and are stripped (10.6). |

---

## 5. Entity naming and neighbour logic

Decisions D4, D5 and D6 settle the scheme. It replaces the earlier `@`
proposal: **there is no `@` rule**.

### 5.1 The rule

- A library author marks a room-local name with the placeholder prefix
  `cxry_`. `cxry_door` is "this room's `door`". At link it becomes
  `c<column>r<row>_door`: in the room at column 3, row 5, `c3r5_door`.
- A name without the placeholder is **global** and left exactly as written.
- Columns and rows are 0-based from the south-west cell, as the level YAML
  counts them: column `x` from the west, row `y` from the south
  (`LevelGrid`; `RoomPlacement.CellX` / `CellY`).
- Offsets of ±1 name a neighbour's local entity: `cx+1ry_door`,
  `cx-1ry_door`, `cxry+1_door`, `cxry-1_door`, and diagonals such as
  `cx+1ry-1_door`.
- **Offsets are in the room's own frame and turn with it** (D5): `cx+1`
  means "beyond this room's authored east side". The linker turns the
  offset by the placement's rotation before adding it to the cell.
- Resolution happens at link; it adds no entities.

### 5.2 Grammar

The **reserved family** is every placeholder prefix and every prefix they
resolve to. Three regular expressions (.NET syntax) define it; the
contracts assembly (7.5) holds them as the single spelling.

```
LOCAL    = ^cx(?<dx>[+-]1)?ry(?<dy>[+-]1)?_(?<rest>.+)$
RESOLVED = ^c(?<col>0|[1-9][0-9]*)r(?<row>0|[1-9][0-9]*)_
SUSPECT  = (?i)^c(?:x|[0-9])[^_]*r
```

- **`LOCAL`** (case-sensitive) is a placeholder name: `cxry_`, `cx+1ry_`,
  `cx-1ry_`, `cxry+1_`, `cxry-1_`, and the diagonals `cx+1ry+1_`,
  `cx+1ry-1_`, `cx-1ry+1_`, `cx-1ry-1_`, each followed by a non-empty rest.
  Only `+1` and `-1`; no spaces, no `+0`, no `+2`; lower case only. `rest`
  keeps its case.
- **`RESOLVED`** is what the linker writes: `c3r5_`, lower case, no leading
  zeros. When checking authored names it is applied case-insensitively.
- **`SUSPECT`** (case-insensitive) is anything that starts like either: `c`,
  then `x` or a digit, then an `r` before the first underscore.

The pack's rules for every name it reads (a `targetname`, a name-valued key,
an output's target or wholly-named parameter):

1. matches `LOCAL`: a local name, resolved at link;
2. else matches `RESOLVED` ignoring case: **refused** as reserved. A global
   name may not begin with anything the linker can produce, so it can never
   collide with a resolved local name;
3. else matches `SUSPECT`: **refused** as malformed. This catches the near
   misses: `cx+2ry_door` (offset beyond ±1), `cx+1r_door` and `cxr_door`
   (missing `y`), `cx1ry_door` (missing sign), `cx+ 1ry_door` (space),
   `CXRY_door` and `cXry_door` (case, O2), `cxrydoor` (missing underscore);
   `cxry_` with an empty rest is refused by the same rule;
4. else: a global name, left as written.

The price of rule 3 is that a few ordinary global names are refused too
(`c4rocket`, `cxr_panel`): a global name may not start with `c`, then `x`
or a digit, then an `r` before its first underscore. The refusal says so
and names the entity and key; the author renames. That is deliberate: a
typo in a placeholder must never quietly become a global name.

- **Case.** Engine and game name matching is widely case-insensitive
  (**uncertain** as a blanket rule: vbsp's own light-style grouping is
  `strcmp`, case-sensitive, `EntityStage.SetLightStyles`), so rules 2 and 3
  are case-insensitive and no case variant slips through as global.
- **Placement.** The family counts only at the **start** of a name.
  `door_cxry` or `my_cxry_door` is global; the pack warns (it looks like a
  misplaced placeholder) and leaves it.
- **Linker-owned rests.** Some rests name what the linker fills in or emits,
  and follow the same grammar: `cxry_has_<dir>` and `cxry_joined_<dir>`
  (neighbour flags, 5.8 b; `<dir>` is `east`, `north`, `west`, `south`, and
  for `has_` also `northeast`, `northwest`, `southeast`, `southwest`),
  `cxry_room` (7.2) and `cxry_transition` (section 11). They take offsets
  like any local name: `cx+1ry_has_north` is the east neighbour's north
  flag; referencing it counts as a reference in that neighbour, so the flag
  is injected there (and, when that cell is empty, the reference is dropped
  with the (a) warning). An author entity with a linker-owned rest must be
  of the expected class (`logic_branch`, `logic_room`,
  `trigger_room_transition`); anything else is refused, and so is an
  unknown direction.

### 5.3 Rotation table

Rotation is counter-clockwise seen from above (`LevelYaml`;
`RoomTransform.Apply` maps authored +x to world +y at rotation 1, and
`RoomTransform.WorldNormal` turns a socket's facing the same way). An
authored offset `(dx, dy)` becomes the level offset:

| Rotation | Level `(dx, dy)` | `cx+1ry` (E) | `cx-1ry` (W) | `cxry+1` (N) | `cxry-1` (S) | `cx+1ry+1` (NE) | `cx+1ry-1` (SE) | `cx-1ry+1` (NW) | `cx-1ry-1` (SW) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 0 | `(dx, dy)` | (+1, 0) | (-1, 0) | (0, +1) | (0, -1) | (+1, +1) | (+1, -1) | (-1, +1) | (-1, -1) |
| 90 | `(-dy, dx)` | (0, +1) | (0, -1) | (-1, 0) | (+1, 0) | (-1, +1) | (+1, +1) | (-1, -1) | (+1, -1) |
| 180 | `(-dx, -dy)` | (-1, 0) | (+1, 0) | (0, -1) | (0, +1) | (-1, -1) | (-1, +1) | (+1, -1) | (+1, +1) |
| 270 | `(dy, -dx)` | (0, -1) | (0, +1) | (+1, 0) | (-1, 0) | (+1, -1) | (-1, -1) | (+1, +1) | (-1, +1) |

**Worked example.** `cx+1ry_door` in a room at column 3, row 5, placed at
90°: the authored east wall faces north, `(+1, 0)` becomes `(0, +1)`, and
the name resolves to `c3r6_door`, the room north of it.

The implementation derives the table from `ApplyNormal` on `(dx, dy, 0)`
rather than restating it, so naming and geometry cannot disagree.

### 5.4 Which keys are rewritten

The repository has **no key list to reuse**: the `func_instance` merge does
no name fixup (`MapInstanceMerger.MergeEntities`, warning `VBSP0107`),
because fixup needs an FGD and the port reads none. The list is new:

1. **Name-valued keys**, a built-in table: `targetname`, `parentname`,
   `target`, `filtername`, `damagefilter`, `lightingorigin`,
   `LightingOriginHack`, `NextKey`, `LaserTarget`, `LightningStart`,
   `LightningEnd`, `altpath`, `SourceEntityName`, `Template01`..`Template16`,
   `Filter01`..`Filter10`, `EntityTemplate`, `master`, `MeasureTarget`,
   `MeasureReference`, `TargetReference`, `glow`, `spawntarget`. The table is
   game-dependent; a library can extend it (O3).
2. **Every output**: the `target` field always; the `parameter` field when
   the whole field is a placeholder name (`SetParent`, `SetTarget`,
   `SetDamageFilter` and the like).
3. **Any other key whose whole value is a placeholder name.** The
   placeholder is explicit, so it cannot be confused with prose, and
   resolving it anywhere removes most of the missing-FGD problem. The pack
   records which keys it resolved, for `ssmap rooms`.

Not resolved: placeholders **inside** a longer value, notably an
`AddOutput` parameter such as `OnTrigger cxry_door:Open::0:-1`. The pack
warns on any `cxry` token after the start of a value.

### 5.5 Wildcards and special names

- A trailing `*` stays inside the resolved prefix: `cxry_door*` becomes
  `c3r5_door*` and matches that room's doors only.
- Names starting with `!` (`!activator`, `!caller`, `!self`, `!player`,
  `!picker`, ...) are never touched.
- A bare `*`, or a classname used as a target, is global as written.

### 5.6 Collisions and length

- **Reserved family.** No global name may begin with anything in the
  reserved family, placeholder or resolved, in any case (5.2, rules 2 and
  3): the pack refuses it, naming the entity and key. A resolved name then
  cannot equal a global one; the link asserts it anyway (case-insensitive).
- **One global name in a room placed twice** appears twice in the level:
  right for a broadcast target, a mistake otherwise. Default (O4): warn at
  link, naming the cells.
- **Length.** The prefix adds `c`, digits, `r`, digits, `_`. The VMF reader
  caps a value at 1024 bytes (`ChunkFileReader.MaxKeyValueLength`); the
  engine's own cap on a key value is **uncertain** (believed 1024 including
  the terminator; confirm). The link refuses a resolved value past 1023
  bytes, naming entity and key; outputs are measured whole.

### 5.7 What the pack precomputes

The prefix is only known at link. The pack stores, per room, a **names
section**: for each entity, the position of every name-valued key and output
in the room's entity list, each with its parsed placeholder (`dx`, `dy`, the
token's extent) and the rest of the name; the room's local names; its
neighbour references by authored direction; its `room_needs` conditions;
and the level-independent part of the fold analysis (6.5). Link work is then
a turn of `(dx, dy)` and one concatenation per reference.

### 5.8 Missing neighbours (decided)

All three mechanisms are built (D6).

**(a) Warn and drop, the default.** An offset reference to an empty cell or
off the grid prints a warning naming the entity, key and room. An output
targeting it is removed; a name-valued key pointing at it is cleared (a
cleared `parentname` leaves the entity unparented). The link succeeds.
**Entities: 0** (it only removes).

**(b) Neighbour flags.** `logic_branch` entities named `cxry_has_<dir>`
whose `InitialValue` the linker sets to 1 if the neighbour cell in that
authored direction holds a room, 0 if not. Directions are in the room's
authored frame and turn like offsets.

- **Injected only when referenced** (decided, D7): a flag exists for a
  placement only if its room names it. No room pays for flags it does not
  use.
- **Authored or bare.** A `logic_branch`'s `OnTrue` / `OnFalse` outputs
  belong to the branch, so for room logic to branch on a flag, the author
  places the branch in the room, named `cxry_has_east`, with its outputs;
  the linker only sets `InitialValue`. When a room references a flag it did
  not place (for a `logic_branch_listener`, say), the linker injects a bare
  one.
- **Diagonals.** Recommended: offer `cxry_has_northeast`, `_northwest`,
  `_southeast`, `_southwest`; opt-in like the rest, so they cost nothing
  unless used.
- **Occupied or joined.** "A neighbour exists" and "a door leads to it"
  differ. Recommended: also offer `cxry_joined_<dir>` for the four sockets'
  directions.
- **Folded when only tested.** The value is a link-time constant. A flag
  branch whose only inputs are `Test` from outputs the linker can see (no
  `SetValue`, `Toggle`, wildcard or global reference, no listener) is folded
  away (6.5): each `Test` becomes the branch's `OnTrue` or `OnFalse`
  outputs, delays composed. In the common case the flags cost **0 entities**.
- **Entities:** 1 per referenced flag per placement, 0 after folding.

**(c) Link-time inclusion.** An entity key `room_needs` with `east`,
`!east`, `joined_east`, `!joined_east`, a diagonal, or several joined by
commas (all must hold) keeps or drops the entity per placement, evaluated in
the room's frame. The key itself is stripped from the output. **Entities:
0** (it only removes).

- **Point entities:** dropped from the entity list.
- **Static props:** the `sprp` record and its `.vhv` are omitted.
- **Brush entities:** the pack records, per model, the ranges it owns: its
  `DModel`, nodes, leaves, leaf faces, leaf brushes, faces (with their
  lightmap bytes, primitive runs and vertex-normal index runs, which the
  engine walks in face order), brushes and brush sides, and its
  `PhysCollide` record. The link omits those ranges and renumbers through a
  prefix-sum remap, so later models' `*N`, faces and brushes shift down.
  Vertices, edges, planes and texinfos may be shared with other models
  (vbsp's edge table and plane table are per compile) and are kept. The
  fallback, if the face-order lumps make omission awkward, is to keep the
  model as an orphan no entity names: it is never instantiated, its faces
  are in no world leaf and never draw, and it still counts toward
  `MAX_MAP_MODELS`. Omission is the design; the orphan is a tested fallback.
- **Lighting.** A conditional entity must not change the bake of anything
  else: at pack time it is baked **without casting shadows** (brush entities
  do not by default; a conditional static prop must have shadows disabled;
  a conditional `light` is refused, since its light is in the base). Its own
  faces or vertices get base lighting and response entries like any others,
  and when it is dropped they are simply not written. Nothing else changes,
  so the drop is exact. A conditional entity in a doorway does not affect
  the capture, because it does not cast shadows.
- **Flatten.** `LevelFlattener` evaluates `room_needs` per placement with
  the same function and leaves the entity chunk (with its brushes) out of
  the VMF, so vbsp never builds it, and strips the key from kept entities.

**How they interact.**

- (a) runs after (c): a reference **inside** an entity dropped by (c)
  produces no warning (the entity is gone).
- A reference **to** an entity (c) dropped (in this room or a neighbour) is
  removed or cleared like (a), reported only under verbose output: the drop
  was intended.
- (a) warns only for a reference to an empty cell or off the grid, and the
  pack warns at pack time for a local name that no entity defines (a typo).
- (b) and (c) are evaluated on the same neighbour table, so a room can both
  test `cxry_has_east` and carry entities with `room_needs east`.

### 5.9 Link and flatten must agree

Both paths call **one resolver** (placement, occupied cells and joints, the
emission mode of `-mod-entities`, one entity list, in; resolved and pruned
entity list, out): name resolution, (a), (b), (c), mod-class or stock
emission (7.1), then folding (6.5). `LevelLinker.MergeEntities` applies it to
the compiled entity lumps; `LevelFlattener.Flatten` to the VMF entities and
their `connections` chunks before writing. Equivalence (`EntitiesAreTheSame`,
extended to every key) compares resolved names byte for byte. The
independent monolithic map (`Rooms3x3Arrangement.MonolithicVmf`) writes
resolved names by its own code and unfolded I/O; a separate fact compares
**effective I/O** (every chain from a trigger expanded to its final
target, input, parameter and total delay) between it and both paths, so
folding is checked against code that does not fold.

### 5.10 Listing and linting

- **`ssmap rooms`** (`RoomCommands.DescribeLibrary`) lists per room: local
  names (`cxry_*`), neighbour references by authored direction, flags
  referenced, `room_needs` keys, and the entity and edict counts (6.7).
- **`RoomLinter`** gains a rule (say `RoomRule.LocalNamesWellFormed`) at
  pack time, applying 5.2: names matching `SUSPECT` but not `LOCAL` (every
  near miss listed there, case variants included), global names matching
  `RESOLVED`, linker-owned rests on the wrong class or with an unknown
  direction, a placeholder after the start of a value (warning), a local reference no entity defines
  (warning), an unknown `room_needs` direction, `room_needs` on a light or
  on a shadow-casting prop.

### 5.11 Tests

One feature, one PR, with facts for each mechanism and every rotation:

- Grammar: every `LOCAL` form accepted; every near miss of 5.2 and every
  case variant refused as malformed; `RESOLVED`-looking global names
  refused as reserved; linker-owned rests on the wrong class refused; each
  with its 15.4 message.
- Rotation: one room with references in all eight directions, placed at the
  centre of a 3x3 grid at each of the four rotations, a distinct room in
  every neighbour cell; each reference resolves to the entity of the room
  behind the same authored wall. The worked example (`c3r6_door`).
- Wildcards stay inside the prefix; `!` names untouched; the reserved-form
  refusal; the length refusal; the duplicate-global warning.
- (a): with the centre room in a corner cell at each rotation, the warnings
  name entity, key and room; outputs removed; keys cleared; no warning for
  references inside (c)-dropped entities.
- (b): flags exist only when referenced, with the right `InitialValue` at
  each rotation, for occupied and joined, cardinal and diagonal; authored
  and bare forms; folded away when only tested, kept otherwise.
- (c): point entity, static prop and brush entity dropped at each rotation;
  the omitted model's ranges and the renumbered `*N`; the orphan fallback;
  refusal for lights and shadow-casting props; lighting unchanged elsewhere.
- Link against flatten: identical resolved entity lists; effective I/O
  against the monolithic map.

---

## 6. The entity budget

The owner's constraint (D7): the engine's **2048-edict cap** makes keeping
entities per room to a minimum a top priority. Rooms repeat, so per-room
costs multiply by placements, and at runtime players, bots, their weapons,
projectiles, ragdolls and pickups draw from the same cap.

### 6.1 Principles

1. **Nothing is injected by default.** Every entity the linker adds is
   opt-in: (b) flags only when referenced, door area portals only when a
   kit asks, `logic_room` only when a room uses it. Today the linker adds
   none.
2. **Link time over runtime.** Whatever can be decided at link is: neighbour
   references resolve to plain names, missing neighbours are dropped (a),
   conditional content is dropped (c), constant logic is folded (6.5). No
   relay entities for plumbing.
3. **Strip after compile.** Entities whose job the tools already did are
   removed (6.3, 6.4).
4. **Count, budget, report.** Every room's cost is in its pack; the link
   totals it and reports headroom (6.7).

### 6.2 Which limit applies

What the repository can support, and what it cannot:

- The repository knows the **compile-time** entity cap,
  `MapFile.MaxMapEntities` = 8192 (vbsp refuses more). It knows nothing
  about runtime edicts: no game code, no FGD (the bundled `gameinfo.txt`
  files name one, e.g. `GameData "tf.fgd"`, but none is present and nothing
  reads it).
- The 2048-edict cap is the owner's constraint and general Source knowledge,
  **not verifiable in this repo**. Also general knowledge and **uncertain
  per game**: server-only "logical" entities (the logic and filter classes
  are the usual examples) do not take an edict but do take a slot in the
  server's larger entity list (believed 4096 handles in Source 2013; confirm
  per game); some classes remove themselves at spawn (unnamed lights, AI
  nodes, decals are commonly cited), so they cost a slot only while the map
  spawns, which still matters because the spawn peak must fit too.

So the linker cannot derive the classification; it must be given one.
Proposed: a **class table** with, per class, one of `edict` (networked),
`logical` (server-only), `spawn-transient` (removes itself at spawn),
`compile-only` (the tools consume it; never in the linked lump), and its
**source** (repo code, game code reference, or "owner-supplied") and
**confidence**. The repo ships the `compile-only` rows (certain, from
6.3) and a conservative default: any class not in the table counts as
`edict`. The library (or the owner's mod) supplies the game rows, as a file
beside the library or a library section, so a mod's classes are counted
right; the rows for the mod's own classes come from the contract
assembly (7.5), where each is declared `logical` or `edict` by the
contract itself. Until then the estimate over-counts, which is the safe
direction.

### 6.3 What the tools already consume

From this repository's code:

| Entity | Fate | Where | Confidence |
| --- | --- | --- | --- |
| `info_room` | left out of every room | `RoomLibraryVmf.Split` | certain |
| `func_detail` | brushes to world, entity cleared | `MapFileLoader` | certain |
| `env_cubemap` | sample recorded, entity cleared | `MapFileLoader` | certain |
| `info_overlay` (unnamed) | cleared; named ones become `info_overlay_accessor` | `OverlaySet.AddFromEntity` | certain |
| `info_overlay_transition` | cleared | `MapFileLoader` | certain |
| `info_no_dynamic_shadow` | sides recorded, entity cleared | `MapFileLoader.HandleNoDynamicShadowsEntity` | certain |
| `func_instance_parms` | cleared | `MapFileLoader` | certain |
| `func_instance` | blanked after merging | `MapFileReader.CheckForInstances` | certain |
| `prop_static`, `info_lighting` | into `sprp`, cleared | `StaticPropEmitter` | certain |
| `prop_detail`, `prop_detail_sprite` | into `dprp`, cleared | `DetailPropEmitter` | certain |
| entities outside the DX level range | cleared | `MapFileLoader` (`ShouldCullForDxLevel`) | certain |
| `func_ladder` | brushes to world, becomes `info_ladder` (kept) | `MapFileLoader` | certain it is kept |
| `func_occluder` | loses brushes and model, kept with `occludernumber` | `OccluderEmitter`, `EntityStage.SetModelNumbers` | certain it is kept |
| `func_areaportal` | brushes to world, kept with `portalnumber` | `MapFileLoader` | certain it is kept |
| `func_viscluster` | cleared, volume kept for the portal file | `MapFileLoader`, `VisClusterVolumes` | certain |
| `light`, `light_spot`, `light_environment`, `light_dynamic` | kept; vrad reads them and does not strip them | `EntityStage`, `DirectLightBuilder` | certain they are kept |
| default `water_lod_control` | added when water has none | `EntityStage` | certain |

### 6.4 What the link can strip

| Candidate | Strip? | Confidence |
| --- | --- | --- |
| `info_room` | already absent | certain |
| `room_needs`, `room_socket`, `socket_priority` keys | always (keys, not entities) | certain |
| `func_viscluster` | already absent: vbsp clears it | certain it has no runtime use in the tools; **uncertain** whether a game defines the class |
| duplicate singletons (section 8) | yes, keep one | certain for identical copies |
| socket furniture on the dropped side of a joint | yes (5.8 c) | certain |
| unnamed `light`, `light_spot` after baking | recommended, as an opt-in until checked in game | **uncertain**: the baked lighting and `WorldLights` do not need the entity; game code is believed to remove unnamed lights at spawn anyway, so stripping saves the spawn peak, not steady state |
| unnamed `func_occluder` no output names | candidate | **uncertain** (4.7) |
| unnamed `infodecal` | no | **uncertain** whether the engine applies decals from the lump without the game entity |
| `info_ladder` | no | needed by the game (**uncertain** per game) |
| `light_environment` | keep one per level | **uncertain** whether the game reads it at runtime |

### 6.5 Link-time folding

Zero entities, no game code: stateless logic the linker can see completely
is inlined and removed. Folding runs after name resolution and (a) to (c),
on the whole level (a room's relay may be called from a neighbour), and
repeats to a fixed point (relays of relays). The level-independent half
(which entities could fold, from their own keys) is precomputed in the pack.

**`logic_relay` R folds when all of these hold:**

- R's name is local (`cxry_`): a global name may be fired by something the
  linker cannot see (scripts, console, `point_servercommand`, other maps);
- every reference to R is an output's `target` field naming R exactly,
  with input `Trigger`: no wildcard that matches R, no other input
  (`Enable`, `Disable`, `Toggle`, `CancelPending`, `EnableRefire`, `Kill`,
  `AddOutput`, ...), no name-valued key naming R (templates, filters,
  parents);
- R has no `parentname` and nothing names R as parent;
- `StartDisabled` is 0 or absent;
- `spawnflags` has no "only trigger once", and has "allow fast retrigger"
  unless every output of R has delay 0 and no caller can fire twice in one
  tick (a relay without fast retrigger ignores a `Trigger` until its
  longest output has fired; folding would deliver it; **uncertain** in the
  exact timing, so the conservative rule is to require the flag);
- R's only outputs are `OnTrigger` (no `OnSpawn`), each with `times` -1
  (a finite count on R is shared across all callers and does not compose);
- no output of R targets or passes `!caller` or `!self` (both mean R before
  folding and something else after); `!activator` is fine, because a relay
  passes its activator through.

**The rewrite.** Each caller output `R,Trigger,<p>,<d1>,<n>` becomes, for
each of R's outputs `T,I,P,<d2>,-1`, the output `T,I,P,<d1+d2>,<n>`, in R's
output order, and R is removed. Delays add exactly in real time; the float
sum and the order of events due in the same tick relative to other entities'
events can differ, so folding is on by default and a library option turns
it off.

**Other safe folds:**

- **Constant `logic_branch`** (the (b) flags, and any branch whose only
  inputs are `Test` and whose value nothing changes): each `Test` becomes
  the branch's `OnTrue` or `OnFalse` outputs, same rules for callers,
  parents, wildcards and special names.
- **`logic_auto` merge:** all `logic_auto`s in the level with the same
  `spawnflags`, no `globalstate` and no name become one, their outputs
  concatenated in link order. Stock class, N rooms to 1 entity.
- **Identical filters:** local-named `filter_*` entities with identical keys
  (after resolution), referenced only through `filtername` /
  `damagefilter`, dedupe to one, references rewritten.

**Not safe, never folded:** anything stateful (`math_counter`,
`logic_compare`, `logic_case`, `logic_timer`, a relay with `StartDisabled`
or fire-once, a branch that receives `SetValue` or `Toggle`), anything named
globally or matched by a wildcard, anything parented or a parent, anything
whose behaviour depends on the caller, and `logic_auto`s with
`globalstate`.

**Flatten parity.** Folding is part of the one resolver (5.9), so
`--flatten` writes the folded I/O and vbsp compiles the same entity list.
The effective-I/O fact against the unfolded monolithic map checks the fold.

### 6.6 Combined entity classes

The owner can add game-side classes that combine several stock entities
(D8), implemented in the owner's mod, Source Sharp (D9). Their full
contract is [section 7](#7-mod-entity-contract). The linker emits them only
under `-mod-entities` (D10); without it, it emits the stock fallback, which
is always correct and only costs more entities.

The candidates the features produce:

- **`logic_room`**, one per placed room that uses it: the neighbour flags
  (up to 12 `logic_branch`es) and up to eight stateful relays that cannot
  fold, in one server-only entity. Specified in 7.2.
- **Door-side logic.** A socket door with its sounds, relays and an area
  portal: prefer the door's own sound keys over `ambient_generic`, the area
  portal's `target` naming the door over relays that open it, and a
  `logic_room` channel for the rest. A combined door class is possible but
  not needed first (7.4).
- **Triggers.** A trigger is a brush entity with one model, so a combined
  class cannot merge two triggers' volumes. Keep one trigger with many
  brushes where one volume will do.
- **Per-level hub.** `logic_auto` merging (6.5) already does the useful part
  with a stock class.

### 6.7 Counting and budgeting

- **Pack.** A per-room **entity section** records the room's entity count
  after compile, its per-class breakdown, its edict estimate (from the
  class table, 6.2), and the conditional parts (entities under `room_needs`,
  foldable logic, flags referenced), so the link can compute each
  placement's exact cost without parsing.
- **`ssmap rooms`** shows per room: entities, estimated edicts, and the range
  after conditional drops and folding (best and worst case).
- **The budget is `cap − reserve`.** The cap is 2048 edicts. The **reserve**
  is what the game needs at runtime (players, bots, weapons, view models and
  wearables, projectiles, ragdolls, pickups, spawned NPCs). It is set at the
  library level (a worldspawn key, `rooms_entity_reserve`) and per link
  (`-entity-reserve N`, which overrides). **Recommended default: 512**,
  leaving 1536 for the map. Justification, with its uncertainty: a 32-slot
  multiplayer server (players or bots alike) typically holds several edicts
  per player (the player, a view model, a handful of weapons or wearables),
  a few hundred in all, plus projectiles, dropped items and effects in
  bursts; 512 covers that with some margin. It is a guess per game, not a
  measurement: the owner's mod should measure its peak and set the library
  key.
- **`ssmap link`** totals the level in `CheckCapacity`, before any room is
  planned (the counts are in the pack): it **warns** when the map's edicts
  eat into the reserve (above `cap − reserve`), **refuses** over the cap,
  and in both cases names the most expensive rooms (by total contribution:
  cost × placements). It always reports headroom, for example:

  ```
  map entities 612 / budget 1536 (reserve 512, cap 2048); 931 entities in the entity list
  ```

  The entity-list total is reported too, against 8192 at compile time and
  with a warning near the (uncertain) runtime handle count.
- **`ssmap layout`** takes `-entity-budget N`; its default is the same
  `cap − reserve` as the link, so a generated level never eats into the
  reserve: the backtracking search (`LevelGenerator`) skips a placement
  whose worst-case cost would pass the budget. It takes `-mod-entities`
  too, since a room's cost differs between the two emission modes, and
  counts the stock mode without it.

### 6.8 Cost of each mechanism

| Mechanism | Runtime entities |
| --- | --- |
| Name resolution | 0 |
| (a) warn and drop | 0 (removes outputs) |
| (b) neighbour flags | 1 per referenced flag per placement; 0 when folded |
| (c) `room_needs` | 0 (removes entities) |
| Relay folding, constant branches | 0 (removes entities) |
| `logic_auto` merge | N rooms to 1 |
| `logic_room` (with `-mod-entities`) | 1 per room that uses it (0 if folded), replacing up to 12 branches and 8 relays |
| Socket furniture | one side per joint (saves) |
| Door area portals (opt-in) | 1 per joint |
| Singletons | 1 per level (saves duplicates) |
| 3D skybox | 1 `sky_camera` per level |
| Transition rooms and spawn | 2 per level with `-mod-entities`; 3 to 5 stock (11.6) |

### 6.9 Authoring guidance

For the rooms README once this lands.

**Free:** world brushes; `func_detail`; tool-textured clips and skip/hint;
`prop_static`; detail props and `%detailtype` materials; displacements;
unnamed overlays; `env_cubemap`; `info_lighting`; water brushes (world);
local names and neighbour references; `room_needs`; points of interest
(`info_poi`, compiled into the navigation data and stripped, 10.6), including
transition arrivals and spawn points.

**Costs one each, multiplied by placements:** brush entities (doors,
buttons, `func_brush`, breakables, every trigger), `prop_dynamic` and
`prop_physics`, named lights, named overlays, `func_occluder`,
`func_areaportal`, `info_ladder`, logic and filter entities that do not
fold, sprites, sounds (`ambient_generic`), particles, NPCs and their spawn
points.

**Prefer:** `func_detail` over `func_brush`; `prop_static` over
`prop_dynamic`; world clip brushes over `func_clip_*`; one trigger with
several brushes over several triggers; a relay with local names and no
state (folds) over one with state; `cxry_has_<dir>` tests (fold) over
runtime discovery; `room_needs` over runtime `Kill`; lights unnamed unless
switched.

---

## 7. Mod entity contract

The rooms feature targets the owner's mod, **Source Sharp** (D9). This
repository defines the entities the rooms pipeline needs from the game;
the mod implements them. This section is written so the mod side can be
built from it alone. Everything the mod must know is here; nothing depends
on reading the linker.

### 7.1 Emission switch

- **The flag is `-mod-entities`** (D10), on `ssmap link`, including
  `ssmap link --flatten`, and on `ssmap layout` (whose entity budget counts
  differ between the two modes). `ssmap room` does not take it: a pack holds
  the room as authored (placeholders, flags, relays, `room_needs`), and the
  choice of emitted classes is made at link, so one pack serves both modes.
- **Without the flag** the linker emits only stock entities (the fallbacks
  below). **With it** it emits the classes of this section where they apply.
- **Flatten parity.** The flag goes through the one resolver (5.9), so
  `--flatten` with and without it writes exactly the entities the link
  writes in the same mode, and the equivalence tests run in both modes.
- **Recorded in the map.** The linked worldspawn (and the flattened VMF's
  worldspawn, so the whole-map compile carries it too) gets
  `ssmap_entities` = `stock` or `mod`, plus `ssmap_entities_version` = `1`,
  the version of this contract. Unknown worldspawn keys are ignored by the
  engine and game (**uncertain** only for games that validate worldspawn;
  the mod does not). A mod can refuse or warn on a map whose version it does
  not know.

### 7.2 `logic_room`

One per placed room that uses it. Holds the room's neighbour flags and up
to eight relay channels.

**Networking.** Server-only: no edict. The mod derives it from its
logical-entity base (whatever it uses for `logic_relay` and `logic_branch`).
It has no position that matters; the linker places it at the room's cell
centre.

**Keys.** All but `targetname` are written by the linker, never by the
author; the author places a `logic_room` named `cxry_room` in the library
room (with its outputs) or references `cxry_room` in outputs, and the
linker fills the rest.

| Key | Type | Meaning |
| --- | --- | --- |
| `targetname` | string | Authored `cxry_room`, resolved to `c<col>r<row>_room`. |
| `neighbours` | integer | 8-bit mask, **authored frame**: bit 0 east, 1 north, 2 west, 3 south, 4 north-east, 5 north-west, 6 south-west, 7 south-east. Set when that cell holds a room. |
| `joined` | integer | 4-bit mask, authored frame: bit 0 east, 1 north, 2 west, 3 south. Set when that socket is joined to a neighbour's socket (a doorway is open). |
| `rotation` | integer | The placement's quarter turns, counter-clockwise from above, 0 to 3. Informational. |
| `column`, `row` | integer | The level cell, 0-based from the south-west. Informational. |
| `room` | string | The library room's name. Informational. |
| `relayflags1` .. `relayflags8` | integer | Per channel: 1 start disabled, 2 fire once, 4 fast retrigger. |

Because the masks are in the authored frame, the game never needs the
rotation to answer a test; `rotation`, `column`, `row` and `room` are for
scripts, debugging and logging.

**Inputs.**

| Input | Parameter | Behaviour |
| --- | --- | --- |
| `TestEast`, `TestNorth`, `TestWest`, `TestSouth`, `TestNorthEast`, `TestNorthWest`, `TestSouthWest`, `TestSouthEast` | none | Fire `On<Dir>True` if the direction's `neighbours` bit is set, else `On<Dir>False`. |
| `TestJoinedEast`, `TestJoinedNorth`, `TestJoinedWest`, `TestJoinedSouth` | none | Fire `OnJoined<Dir>True` or `OnJoined<Dir>False` from `joined`. |
| `Trigger1` .. `Trigger8` | none | Channel *k*: if enabled and not waiting, fire `OnTrigger<k>`; if fire-once, disable the channel; if not fast retrigger, wait (ignore `Trigger<k>`) until the longest delay among `OnTrigger<k>`'s connections has elapsed. |
| `Enable1` .. `Enable8`, `Disable1` .. `Disable8`, `Toggle1` .. `Toggle8` | none | Set, clear or flip channel *k*'s enabled state. |

**Outputs.** `OnEastTrue`, `OnEastFalse`, and likewise for the other seven
directions; `OnJoinedEastTrue`, `OnJoinedEastFalse`, and likewise for the
other three; `OnTrigger1` .. `OnTrigger8`. Every output fires with the
activator of the input that caused it and the `logic_room` as caller.

**State.** The masks never change at runtime. Channel state (enabled,
waiting) is per channel and saved and restored like a `logic_relay`'s.
Initial enabled state: not `relayflags<k> & 1`.

**FGD entry.**

```
@PointClass base(Targetname) iconsprite("editor/logic_relay.vmt") = logic_room :
    "Per-room hub written by ssmap link: neighbour flags and eight relay channels. Keys other than the name are filled in by the linker."
[
    neighbours(integer) : "Neighbours (authored frame: 1 E, 2 N, 4 W, 8 S, 16 NE, 32 NW, 64 SW, 128 SE)" : 0
    joined(integer) : "Joined sockets (authored frame: 1 E, 2 N, 4 W, 8 S)" : 0
    rotation(integer) : "Placement rotation, quarter turns counter-clockwise" : 0
    column(integer) : "Level column, from the west" : 0
    row(integer) : "Level row, from the south" : 0
    room(string) : "Library room name" : ""
    relayflags1(integer) : "Channel 1 flags (1 start disabled, 2 fire once, 4 fast retrigger)" : 0
    relayflags2(integer) : "Channel 2 flags" : 0
    relayflags3(integer) : "Channel 3 flags" : 0
    relayflags4(integer) : "Channel 4 flags" : 0
    relayflags5(integer) : "Channel 5 flags" : 0
    relayflags6(integer) : "Channel 6 flags" : 0
    relayflags7(integer) : "Channel 7 flags" : 0
    relayflags8(integer) : "Channel 8 flags" : 0

    input TestEast(void) : "Fire OnEastTrue or OnEastFalse"
    input TestNorth(void) : "Fire OnNorthTrue or OnNorthFalse"
    input TestWest(void) : "Fire OnWestTrue or OnWestFalse"
    input TestSouth(void) : "Fire OnSouthTrue or OnSouthFalse"
    input TestNorthEast(void) : "Fire OnNorthEastTrue or OnNorthEastFalse"
    input TestNorthWest(void) : "Fire OnNorthWestTrue or OnNorthWestFalse"
    input TestSouthWest(void) : "Fire OnSouthWestTrue or OnSouthWestFalse"
    input TestSouthEast(void) : "Fire OnSouthEastTrue or OnSouthEastFalse"
    input TestJoinedEast(void) : "Fire OnJoinedEastTrue or OnJoinedEastFalse"
    input TestJoinedNorth(void) : "Fire OnJoinedNorthTrue or OnJoinedNorthFalse"
    input TestJoinedWest(void) : "Fire OnJoinedWestTrue or OnJoinedWestFalse"
    input TestJoinedSouth(void) : "Fire OnJoinedSouthTrue or OnJoinedSouthFalse"
    input Trigger1(void) : "Fire channel 1"
    input Enable1(void) : "Enable channel 1"
    input Disable1(void) : "Disable channel 1"
    input Toggle1(void) : "Toggle channel 1"
    // Trigger, Enable, Disable and Toggle for channels 2 to 8 follow the same pattern.

    output OnEastTrue(void) : "East neighbour present"
    output OnEastFalse(void) : "East neighbour absent"
    // On<Dir>True / On<Dir>False for North, West, South, NorthEast, NorthWest, SouthWest, SouthEast.
    // OnJoined<Dir>True / OnJoined<Dir>False for East, North, West, South.
    output OnTrigger1(void) : "Channel 1 fired"
    // OnTrigger2 .. OnTrigger8.
]
```

The FGD in the mod spells out every elided line; they follow the patterns
shown exactly.

**What the linker emits (with `-mod-entities`).** One `logic_room` for a
placement whose room names `cxry_room`, or has a neighbour-flag reference
it cannot fold (6.5), or has local relays that cannot fold. It fills the
masks from the level (in the room's authored frame), `rotation`, `column`,
`row` and `room`; it merges the room's authored flag branches (callers
rewritten from `c3r5_has_east,Test` to `c3r5_room,TestEast`, the branch's
`OnTrue`/`OnFalse` moved to `OnEastTrue`/`OnEastFalse`) and up to eight of
its non-foldable local relays (callers rewritten to `Trigger<k>`,
`Enable<k>` and so on; the relay's outputs moved to `OnTrigger<k>`; its
spawnflags and `StartDisabled` into `relayflags<k>`). A relay is merged
only if none of its outputs use `!caller` (which would name the hub).
Relays past eight stay relays. Then folding runs again: a `logic_room` whose
every input is a test the linker can see, and that has no channels, is
folded away like a constant branch.

**Stock fallback (without the flag).** A `cxry_room` reference expands into
stock entities: one `logic_branch` per referenced direction
(`c3r5_has_east`, `InitialValue` from the mask, `On<Dir>True`/`False` as
its `OnTrue`/`OnFalse`; callers of `TestEast` rewritten to `Test`) and one
`logic_relay` per referenced channel (`relayflags` as spawnflags:
fire once → "only trigger once", fast retrigger → "allow fast retrigger";
start disabled → `StartDisabled 1`), then folding. An author may write
either form (flag branches or `cxry_room`); both modes accept both.

### 7.3 Contract rules for every mod class

- A class is added here, with a contract version bump, before the linker
  emits it.
- Every mod class has a stock fallback the linker can emit, so a map built
  without `-mod-entities` runs on any Source game.
- Mod classes the linker emits are server-only unless stated otherwise, so
  they cost no edicts (section 6); the class table (6.2) lists them as
  `logical`, source "this contract".
- Keys the linker fills are documented as linker-owned; the mod treats a
  missing key as its default, never as an error.

### 7.4 Candidates not yet specified

- **A combined socket door** (door, area portal control and sounds in one
  brush entity). Not needed first: the stock door with its own sound keys
  and a `func_areaportal` targeting it covers the case with two entities,
  one of them only when door portals are opted in.
- **A navigation hint or link entity** for the future AI system: deferred
  with navigation (section 10).

### 7.5 Shared C# contract types

Source Sharp is written in C#, so the contract is also code: a small,
dependency-free assembly that both the linker and the mod reference, so the
two cannot drift apart. The prose above stays the specification; the types
are its single spelling.

- **Recommended: a new assembly, `SourceSharp.RoomContracts`**, rather than
  `SourceSharp.MapFormats`. MapFormats is the file formats (BSP, VMF,
  KeyValues); the mod needs none of it, and a contracts assembly can keep a
  stricter compatibility promise (it changes only with the contract
  version). It has no project or package references, like MapFormats
  (`LibraryRuleTests` should hold it to the same rules: no package
  references, no mutable statics). `SourceSharp.MapTools` references it.
- **Contents**, all `const` or immutable:
  - class names (`LogicRoom.ClassName = "logic_room"`), key names, input and
    output names, as `const string`s per class;
  - `[Flags] enum NeighbourMask : byte` (`East = 1, North = 2, West = 4,
    South = 8, NorthEast = 16, NorthWest = 32, SouthWest = 64,
    SouthEast = 128`), `[Flags] enum JoinedMask : byte`, `[Flags] enum
    RelayFlags` (`StartDisabled = 1, FireOnce = 2, FastRetrigger = 4`);
  - the placeholder grammar's constants (`cxry_`, the reserved resolved
    form) and a pure parser, so the mod's tools and debug commands read
    names the way the linker writes them;
  - the worldspawn keys of 7.1 and `ContractVersion`;
  - the POI and navigation types once the AI design settles (section 10);
  - the `logical` rows of the entity class table (6.2) for the mod's
    classes.
- **Facts** in this repository check that the FGD text of 7.2 and the
  constants agree, and that every class the linker can emit under
  `-mod-entities` is declared in the assembly.

### 7.6 `logic_level_transition`

One per transition room (section 11), emitted only with `-mod-entities`.
Server-only; placed at the transition volume's centre.

**Keys** (all linker-owned except `targetname`):

| Key | Type | Meaning |
| --- | --- | --- |
| `targetname` | string | Authored `cxry_transition`, resolved to `c<col>r<row>_transition`. |
| `direction` | string | `up` or `down`. |
| `map` | string | The destination map's name, from the level YAML's `up_map` or `down_map`. |
| `StartDisabled` | integer | 1 to start disabled; 0 by default. |

**Inputs:** `Transition` (if enabled, fire `OnTransition`, then move the
players to `map`), `Enable`, `Disable`. **Outputs:** `OnTransition`, fired
with the activator before the level changes.

**Behaviour.** On `Transition` the mod changes to `map` and places each
arriving player at the destination level's arrival POI of the opposite
role (from `down`: the destination's `up` arrival; from `up`: its `down`
arrival), facing the POI's yaw. The arrival POIs and the level's spawn
POIs are read from the destination map's navigation data (section 10); a
fresh start uses the `up` arrival, or the `spawn` POIs (11.5). Which
players move (the activator, or everyone) is the mod's game rule, not part
of this contract.

**FGD entry.**

```
@PointClass base(Targetname) = logic_level_transition :
    "Level transition written by ssmap link. Keys other than the name are filled in by the linker."
[
    direction(choices) : "Direction" : "down" =
    [
        "up" : "Up"
        "down" : "Down"
    ]
    map(string) : "Destination map" : ""
    StartDisabled(choices) : "Start disabled" : 0 =
    [
        0 : "No"
        1 : "Yes"
    ]

    input Transition(void) : "Move the players to the destination map"
    input Enable(void) : "Enable the transition"
    input Disable(void) : "Disable the transition"

    output OnTransition(void) : "Fired just before the level changes"
]
```

**Stock fallback:** `trigger_changelevel` and `info_landmark` (11.4).

**C# types** (7.5): `LevelTransition.ClassName`, its key, input and output
names; `enum TransitionDirection { Up, Down }` with its key spellings;
`enum PoiType` with `Arrival` and `Spawn` (the other POI types come with the
navigation design).

---

## 8. Level-wide singletons

| Entity or key | Today | Plan |
| --- | --- | --- |
| `worldspawn` keys | Rooms must agree (`RequireSameWorld`); the split copies the library's worldspawn into every room, so they do. | Keep. `world_mins`/`world_maxs` stay the union (`MergeEntities`). |
| `light_environment` | Carried per room and turned (finding 5). | **Decided (D3):** library-wide. The library holds one, outside every cell (the split collects it instead of ignoring it). A room that carries one is refused at pack time unless its keys equal the library's, in which case it is dropped from the room. Never turned. |
| Sky settings (`skyname`, the sun's sky colours) | `skyname` agrees through worldspawn. | Library-wide with the sun (D3). |
| `sky_camera` | Room-local if present. | Only in the library's skybox (4.12); refused in rooms. Done since PR 13: exactly one in the skybox room, which every level carries once. |
| `env_fog_controller`, `env_tonemap_controller`, `shadow_control`, `postprocess_controller` | Carried, one per room that has one; the game takes the first or a master (**uncertain** per class). | Library-wide like the sun: collected from the gaps; a room copy refused unless equal. Per-room fog uses a trigger and a named controller, as a normal map does. |
| `water_lod_control` | vbsp adds one per room with water. | Keep the first; drop equal duplicates; refuse different ones. |
| `info_player_start` | Carried, one per room with one (the sample's end rooms). | **Decided (D15):** rooms' own starts are stripped; the level spawn is the up room's arrival (11.5). |
| Switched light styles | Collide (finding 8). | Renumber at link: each distinct resolved light name gets one style from 32 (a global name shares one across placements); face styles, detail prop styles, world lights and entity `style` keys remapped. ≤ 32 switched names (`WriteLimits.MaxSwitchedLights`), 64 styles in all (`RayAmbientLighting.MaxLightStyles`). |

---

## 9. Lighting (option C, as decided)

The owner settled the design (D2); it replaces the earlier "re-light faces
near joined doors at link" fix-up. Nothing here traces a ray or reads a game
file at link.

### 9.1 The four parts

1. **Base lighting.** A doors-closed vrad bake per room, per rotation (×4)
   when sun or sky light reaches the room and once otherwise (1.1): lightmaps and bump pages, leaf ambient, static prop per-vertex
   lighting, displacement lightmaps, detail prop lighting, vertex normals,
   leaf sky flags (pass one), world lights. The room is sealed by its plugs
   exactly as compiled (`RoomCompiler`), so this is a normal vrad run of the
   room with the library's sun turned into the room's frame.
2. **Doorway capture.** A doors-open bake of the room into a black, fully
   absorbing box, per rotation when sunlit and once otherwise: plugs removed, the room surrounded by a
   box that reflects nothing, so only the room's own light leaves. Recorded
   per door opening:
   - **direct:** which lights (and the sun through the room's own sky
     openings) shine through which parts of the opening, as a small
     visibility grid on the door plane per light;
   - **bounce:** directional radiance per door sample (an ambient cube per
     sample), per light style.
3. **Door response** ("option b"). Per door, stored once, since it does not
   depend on the room's rotation (only the incoming light turns), unless four
   pre-turned copies measure faster at link; the room's
   response to light **entering** through that door: a coarse basis on the
   opening (for example 2×4 patches × 6 directions = 48 functions), each
   mapped to the change it makes in the room's lightmaps, leaf ambient,
   static prop vertices, displacement and detail prop lighting, with
   receivers that get almost nothing pruned. (PR 10 measured this basis
   and replaced it: direct light is evaluated at link from what each side
   of the opening sees, and only the bounce goes through a sixteen-emitter
   response; section 13, its landed note.)
4. **Final bake at link.** For each joined door, in both directions: the
   neighbour's outgoing capture, projected onto this room's door basis,
   times this room's response, added to this room's base, per style. No ray
   tracing, no game files.

**Reach** (D3): only the adjacent room. Light never crosses two doors.
**Sun** (D3): one for the library; every room shares it.

Storage (D16): a room with no sky opening has no sun term, so its base
bake and capture are the same bytes at every rotation and stored once;
sunlit rooms store four. The response is the same at every rotation and is
stored once or ×4 as the prototype measures (1.1). Facts check both
properties (15.9).

### 9.2 When per rotation

The sun is fixed in the world, so in a turned room it comes from another
direction; vrad's sky-ambient sampling directions are fixed in world space
too. Point, spot and texture lights turn with the room, so a room the sun
and sky do not reach is the same at every rotation in its own frame. Baking in the room's
frame with the sun turned by the inverse rotation keeps the lightmap layout
(luxel axes come from texture axes, which turn with the room,
`LevelLinker.TransformTexInfos`), so rotation *r*'s data drops into the same
faces.

### 9.3 Sums and precision

vrad's output is linear in light intensities once geometry is fixed (direct
and bounce), so base plus door terms is a sum of linear radiance. Sum in
float and encode to `ColorRGBExp32` once, at link; the pack stores linear
values (half floats), not encoded luxels. HDR and LDR are separate passes
(`Vrad.cs` runs `RadLumpWriter.Write` per pass): store what the library
compile asks for.

Styles: a door term carries the neighbour's styles, renumbered at link
(section 8). A face holds at most four styles (`MaxLightmaps` = 4); a face
that would need more drops its weakest door style, with a warning.

### 9.4 Per-feature lighting

| Feature | Base (×1, ×4 if sunlit) | Capture (×1, ×4 if sunlit) | Response (×1) |
| --- | --- | --- | --- |
| Brush faces (world and brush entities) | lightmaps and bump pages | emits nothing itself; the room's lights do | per-luxel deltas |
| Displacements | displacement lightmaps (own sample positions) | as faces | per-luxel deltas |
| Static props | per-vertex colours per LOD and strip group, written to `.vhv` at link | none | per-vertex deltas |
| Detail props | per-prop lighting and styles | none | per-prop deltas (one sample point each) |
| Leaf ambient | samples per leaf | the ambient cubes on the door plane are the bounce capture | per-sample cube deltas |
| World lights | the room's lights, moved | n/a | n/a (the neighbour's lights are already in the level's list) |
| Sky flags | pass one per room | n/a | pass two at link over the linked PVS |
| `room_needs` content | baked without casting shadows (5.8 c) | unaffected | its entries dropped with it |

The carved doorway leaf gets its facing leaf's ambient samples. Doorway faces
do not exist (the known difference), so there is nothing to light there.

### 9.5 Storage per room

Estimates, to be replaced by the prototype's measurements. With `L` luxels
(bump pages ×4 on bumped faces), `R` the stored rotations (1, or 4 if
sunlit), `S` styles, `D` doors, `B` basis functions
(48), `p` the fraction of receivers a basis function reaches after pruning,
`V` prop vertices and `A` ambient samples:

- base: `R × (L + V) × S × 6 bytes` (half-float RGB) plus ambient
  `R × A × 6 faces × 6 bytes`;
- capture: per door, per stored rotation, per style, `G` grid cells (8×16 = 128) ×
  (a bit per light + a 36-byte cube) ≈ 5 KB;
- response: per door `B × p × (L + V + 6A) × 10 bytes` (value plus receiver
  index), once, or ×4 if that applies faster.

For `L = 20,000`, one style, four doors, `p = 0.3`, no props: base ≈ 0.12 MB
(0.5 MB if sunlit), capture ≈ 20 KB (80 KB if sunlit), response ≈ 11.5 MB,
stored once, or 46 MB as four pre-turned sets if the prototype shows those
apply faster; uncompressed, since compression is off unless it measures
faster (1.1). (Measured by PR 10, whose basis differs: the door light adds
about 4% to the stress library's pack and 3% to the 3x3 sample's, with no
responses for rooms that reflect nothing; section 13, its landed note.) These are the costs of those choices, accepted if they buy
link time. The response dominates, so its basis size, pruning threshold and resolution
(it is smooth; half the luxel resolution may do) are what the prototype must
choose.

Pack-time cost: `R` base bakes, `R` capture bakes, `D × B` response solves.
The responses reuse the room's transfers: vrad already hands transfers
between passes (`Vrad.cs`, `world.ShareTransfers()`), so each basis function
is an injection and a bounce, not a new form-factor pass.

### 9.6 Error left

- **Second bounce back through the same door**: light from A lights B, B's
  reflection of it would light A again; the design stops after A → B.
- **Two or more rooms away**: excluded by decision.
- **Basis coarseness**: a neighbour's point light through a door is a sharp
  beam; 8 patches × 6 directions blur it. The direct capture keeps which
  part of the opening is lit, but the receiving room sees it through the
  basis.
- **Door brushes in the opening at bake time**: vrad casts shadows from
  brush entities only when asked (a closed `func_door` normally lets light
  through in a stock compile too). Where an author makes a door cast
  shadows, the capture (doors open) and the full compile (door present)
  disagree; measure it.
- **Props straddling doors**: excluded by O6.

### 9.7 Prototype plan

Measure pack size and basis resolution against error, against a full vrad of
the flattened level (`link --flatten`, then `ssmap vbsp`, `ssmap vvis`,
`ssmap vrad`, same options, `-compliance correct`):

1. Levels: the 3x3 sample with lights of different strengths, a room with a
   sky opening, a switchable light, props and a displacement; one seeded
   16×16 stress level.
2. Metrics per luxel, prop vertex and ambient sample: relative error in
   linear radiance and in log space, p50 / p95 / p99 / max, separately
   within one door width of a joint and elsewhere; the whole-level
   distribution through `Compare/DistributionalLumpDiff` and `ssmap diff`.
3. Sweep: patches 1×2, 2×4, 4×8; 6 and 14 directions; pruning thresholds
   1e-3, 1e-2, 5e-2 of the brightest response; response resolution full
   and half. Record pack bytes per room, pack-time cost, link time.
4. Baselines: base only; base plus captures × responses. Choose the knee of
   error against bytes.
5. Tight checks: one room alone with every socket capped (link equals the
   full compile within float noise, at all four rotations); the
   four-rotation response agreement (9.1).

### 9.8 Equivalence for lighting

Lighting cannot be byte-equal to a full compile by design. Tests assert
tolerances chosen from the prototype: the capped single room within float
noise; a linked level within the measured p95 / p99 near doors and tighter
elsewhere; no luxel brighter than the full compile by more than the
tolerance (door terms must not invent light). PR 10's facts hold them
against vrad of the linked level: p95 under 0.08 near joints and elsewhere
and energy within 2% for rooms that reflect nothing, p95 under 0.2 and
energy within 3% for reflecting rooms, the 99th percentile of any luxel's
excess under 0.3, and a capped room the same bytes as without door light.

---

## 10. Navigation

**Blocked on the AI design.** Navigation is required (D11), for a new AI
system the owner has not designed yet, and it must be a **3D** navigation
representation: agents can move up and down (flying, climbing), not only
walk on floors. So this section does not target the stock `.nav` format or
the AI node graph; it frames what the room pipeline can offer any 3D
navigation system, and lists what the owner must decide first. It is not in
the implementation order yet.

What this repository has today: nothing on navigation. There is no nav or
node-graph code or format, `info_node` entities are carried as ordinary
point entities (nothing in vbsp here consumes them), and one bundled game
sets `nodegraph 0` (`game/mod_tf/gameinfo.txt`).

### 10.1 What the pipeline can offer

- **A per-room navigation volume, precomputed at pack time**, per rotation
  when that makes the link faster (1.1); a cell-aligned grid could also be
  turned at link as an index permutation.
  Built at pack time from the room's compile and stored in the companion
  navigation file (10.4), so the link needs no game files and no
  navigation build.
- **Doorway connections.** Each socket is a known rectangle on a known cell
  face (`RoomLinter.SealBox`, the kit). The room's navigation data records,
  per socket, its **portal**: the free region of the doorway box for each
  agent size, and which of the room's navigation cells touch it. At link a
  joined socket's two portals are the same rectangle seen from both sides,
  so stitching is joining the two rooms' portal cells, never recomputing
  either room. A capped socket's portal is dropped.
- **Conditional geometry.** Brush entities under `room_needs` (5.8 c) and
  doors in sockets change what is passable. The room's data marks the
  navigation cells each such entity blocks, and records door-blocked cells
  with the door's resolved name, so the link removes blockers of dropped
  entities and the runtime can open and close door links by name.

### 10.2 Geometry available at pack time

- The compiled BSP: brushes with their contents (solid, player clip,
  monster clip, grate, ladder, water) and the tree and leaves, which answer
  point contents and traces exactly.
- The world collision the cooker built (`PhysCollide`, one surface per
  contents class; `LevelLinker.MergeCollision` already reads its convexes).
- Static prop collision, from the models (pack time has game files;
  `Bsp/Props/IStaticPropCollision`), and displacement collision.
- The player hull the pipeline already uses (`PlayerHull`: 32 × 32 × 72,
  standing) and whatever agent hulls the AI defines.
- The door kit: socket rectangles, wall depth, cell size.

### 10.3 Rotation

A quarter turn about +z permutes x and y and leaves z alone, so a grid-based
representation (voxels, a sparse octree) whose cell size divides the room
cell and whose origin is the cell corner turns **exactly**: cells permute,
none is resampled. Convex volumes and meshes turn through `Apply` and
`ApplyNormal` like all other geometry. Vertical movement is untouched. Agent
hulls must be square in x and y (as the player's is) for a turn to leave
clearance unchanged; a non-square hull needs per-rotation data (×4)
whatever the measurement says.

### 10.4 Files (decided, D18)

Navigation data lives in **separate files**, not in the pack or the map's
pak, unless it is trivially small:

- **`<library>.roomnav`**, written by `ssmap room` beside the `.roompack`:
  the same indexed container conventions as the pack (`RoomPack`: magic,
  version, an index of rooms with typed sections, sections back to back,
  readers skipping unknown tags, one atomic replace on write), each section
  with the codec byte and rotation count of 1.1. Its header **binds it to
  the pack**: it records the pack's format version and a hash of the pack's
  index and room containers (SHA-256, as the room container already hashes
  the room's VMF), and `ssmap link` refuses a pair whose hash does not
  match, naming both files. A pack without a `.roomnav` links without
  navigation, with a warning when any room of the level had POIs.
- **`<map>.nav3d`**, written by `ssmap link` as a sidecar next to the
  `.bsp`, not into the pakfile: the rooms' navigation relocated (index
  bases, as for every other lump) plus the joined portals, written in one
  pass, with the same section and codec conventions. It must be
  distributed with the map (servers and clients that download only the
  `.bsp` do not get it); that is the mod's concern.
- **Back into the pack** only if measured trivially small: the navigation
  PR states a size threshold per room (and per level for the pak), and
  below it the data may be a pack section and a pak entry instead. The
  files are the default.

### 10.5 Open questions for the owner

1. **Representation**: voxels, a sparse voxel octree, convex volumes, a
   layered 3D mesh, or something else. It decides storage, rotation
   exactness and stitching.
2. **Agent sizes**: which hulls (walking, climbing, flying) and their
   dimensions; clearance is per agent size.
3. **Movement model**: what counts as traversable (climbable surfaces,
   ladders, water, jump links), and whether traversal costs are baked.
4. **File format and versioning** inside the `.roomnav` and `.nav3d`
   containers (the files themselves are decided, 10.4).
5. **Dynamic obstacles**: which entities the runtime treats as blockers
   (doors, `func_brush`, props), and how links are toggled.
6. **Resolution against size**: the budget per room in the `.roomnav` and
   per level in the `.nav3d`.

Size once decided: **L**; risk high until the representation is chosen.

### 10.6 Points of interest

Cover, vantage, spawn, patrol and interaction points belong to the
navigation data, not to runtime entities (D12). Authors place `info_poi`
point entities (the class name and keys are part of the mod contract,
section 7, once the AI design settles) in rooms; `ssmap room` compiles them
into the room's section of the `.roomnav` and **strips them from the entity
lump**, so they cost zero runtime entities (6.9).

Two POI types are defined now, for transition rooms (section 11):
`arrival` (where a player from another level appears) and `spawn` (extra
spawn points for a fresh start).

Per POI the `.roomnav` stores: its type, position and orientation (turned per
rotation like any point entity: `Apply` and yaw + 90 × turns), its keys,
and its name through the placeholder grammar (section 5), so a room's POI
can be named, and referenced by a neighbour, the same way as an entity. At
link the POIs are relocated with the rest of the navigation data. Storing
and stripping them does not depend on the navigation representation, so it
can land before the rest of this section (it is part of PR 2 in section
13, which introduces the `.roomnav` file for them); until navigation exists
the link writes them to the `.nav3d` sidecar on their own.

---

## 11. Transition rooms and the level spawn

A level has one **up** room and one **down** room. They move the player to
the level above or below, triggered by a player action: pressing a button,
opening a door, or walking down a hallway or staircase into a trigger. The
up room is also where a fresh start puts the player. Decisions D13 to D15.

### 11.1 Marking and the level rule

- **Marking.** An `info_room` key `room_role` = `up` or `down`; blank (the
  default) for an ordinary room. A library may hold several candidates of
  each role. `RoomLibraryVmf` reads it with the other `info_room` keys and
  `RoomDefinition` carries it; `ssmap rooms` lists it.
- **Exactly one of each per level** (D13): one placement of an up-role room
  and one of a down-role room, in different cells. No branching, no several
  down rooms. The level YAML can switch either off for the top or bottom
  level: `up: none`, `down: none`; then the level holds **no** room of that
  role.
- **Destinations** (D14) are level YAML keys, `up_map` and `down_map`: the
  map names of the levels above and below. They are clearer than
  `next_map`/`prev_map`, which say nothing about direction. A role that is
  present needs its map key; a role switched off must not have one.
- **Refusals.** The layout half of `RoomLinter` (`CheckLayout`) refuses a
  level with zero or two rooms of a role that is not switched off, a role
  room in a level that switched the role off, a missing or superfluous map
  key, and an unreachable transition room (already covered by
  `CheckReachable`). `ssmap link` and `--flatten` both run it.
  `LevelYaml` refuses unknown keys today (its key check names `library`,
  `rows`, `columns`, `grid`), so the new keys are a format extension that
  old level files never contain.

### 11.2 Layout

`ssmap layout` places the two roles, with an optional minimum distance
between them (`-transition-distance N`), measured as the number of doors on
the shortest path through joined sockets, so the player crosses the level.

**Unchanged output for libraries without roles.** `LevelGenerator` draws
everything from one `SplitMix64` stream seeded by the level's seed. Role
placement uses a **second** stream, seeded from the seed and a fixed
constant, and runs only when the library has role rooms and the level has
not switched both roles off. It picks the two role cells on the spanning
tree (respecting the distance) before the fill, and the fill then offers
only role candidates in those cells and only ordinary rooms elsewhere. For
a library without roles the second stream is never created, the candidate
lists are the ones the fill uses today, and the header lines
(`LevelGenerator.Header`) are unchanged, so every draw of the main stream is
the same and the YAML is byte-identical. The existing facts that hold the
sample's seeded levels to what `ssmap layout` writes
(`Rooms3x3CommandsTests.TheSampleSeededLevelsAreWhatSsmapLayoutWrites`)
already check this; a new fact compares a role-less library's output with
the generator's current output over many seeds and grid shapes.

**A sequence of levels** (recommended yes, optional): `ssmap layout ...
-sequence K -name <base>` writes `<base>_01.yaml` to `<base>_K.yaml` from
seeds N, N+1, ..., with `down_map` and `up_map` chained (level *i*'s
`down_map` is level *i+1*'s map name and the reverse), the first level
`up: none` and the last `down: none`. It is cheap (the generator already
produces one level from a seed) and it is the natural way to produce a
pre-linked run of maps.

### 11.3 Authoring the trigger

The author wires the player's action with stock entities and names the
transition with a local name:

- The room holds one **transition volume**: a brush entity of the
  compile-only class `trigger_room_transition`, named `cxry_transition`. In
  the hallway case it is the volume the player walks into; in the button or
  door case it can be a small volume anywhere in the room (it is only used
  in the stock fallback, 11.4).
- The action fires `Transition` at `cxry_transition`: a `func_button`'s
  `OnPressed`, a `func_door`'s `OnFullyOpen`, or a `trigger_once` whose
  volume is the hallway. The `cxry_` rules of section 5 apply as to any
  name.
- The room holds one **arrival** point: a navigation POI of type `arrival`
  (10.6) with a position and a facing, where a player coming from the other
  level appears. Zero runtime entities.
- `RoomLinter` checks at pack time that a role room has exactly one
  `cxry_transition` volume, exactly one `arrival` POI, the arrival in open
  space with room for the player hull (`PlayerHull`), and that something
  fires `Transition` at it.

The volume is a brush entity, so a role room needs brush entities to link
(PR 7 in section 13).

### 11.4 What the link emits

The destination is resolved at link and written into the map (D14): the
mod never runs the linker when the player transitions. Both paths go
through the one resolver (5.9), so `--flatten` emits the same.

**With `-mod-entities`:** the volume's model is omitted (as for a dropped
brush entity, 5.8 c) and one point entity `logic_level_transition` (7.6)
is emitted at the volume's centre, named `c<col>r<row>_transition`, with
`direction` = `up` or `down` and `map` = the level's `up_map` or
`down_map`. The author's outputs already target it by name; `Transition`
is its input. The mod moves the player to that map and places them at the
destination level's arrival POI of the opposite role (down room → the next
level's up-room arrival, and the reverse), facing its yaw.

**Without it (stock fallback):** `trigger_changelevel` plus
`info_landmark`.

- The volume becomes the `trigger_changelevel` (classname rewritten, its
  model kept), with `map` = the destination and `landmark` = the landmark's
  name. For a button or door, `spawnflags` gets "disable touch" and the
  author's `Transition` outputs are rewritten to the stock input
  `ChangeLevel`.
- **Hallway case, folded:** when the author's `trigger_once` has
  `Transition` at `cxry_transition` as its only output and no filter, and
  its volume contains the transition volume's, the linker turns the
  `trigger_once` itself into the touch-enabled `trigger_changelevel` and
  drops the transition volume. Feasible because both are brush trigger
  volumes; the author's volume becomes the changelevel volume unchanged.
  When the conditions fail, both stay (the `trigger_once` fires
  `ChangeLevel` at the changelevel).
- **Landmark.** One `info_landmark` per transition room. In level A with
  `down_map` B, the down room's landmark is named `A__B` and stands at the
  centre of the changelevel volume; in level B (whose `up_map` is A) the up
  room's landmark is also named `A__B` and stands at the arrival point. The
  engine carries the player's offset from the source landmark to the
  destination landmark, so the player lands at the arrival plus their
  offset from the volume's centre: exact for a player at the centre,
  within the volume's half-size otherwise. Keeping transition volumes small
  and arrival points clear by at least that much makes it land in open
  space. The landmark carries no rotation, so the arrival's facing is not
  applied: the player keeps their view angles (**uncertain** per game; the
  mod mode applies the facing). In multiplayer games a changelevel is
  commonly a plain map change with players respawning at spawn points
  (**uncertain** per game): then every arrival is the level spawn (11.5),
  right for going down and wrong for going up. The mod mode has no such
  gap.

Rotation: the transition volume and landmark turn with the room like all
geometry and point entities; the arrival's facing takes yaw + 90 × turns.

### 11.5 The level spawn

A fresh start (not arriving from another level) puts the player at the up
room's arrival point, facing its yaw (D15).

- **With `-mod-entities`:** the mod reads the up-role arrival POI from the
  navigation data and spawns the player there. No entity.
- **Stock fallback:** the linker emits one `info_player_start` at the up
  room's arrival point with its yaw turned, and **strips** every
  `info_player_start` authored in the rooms (stripped rather than marked
  as not the spawn: a marking key would still cost an entity each and games
  do not agree on one). This replaces O12.
- **Multiplayer.** Several players need several spawn points. The up room
  may hold further POIs of type `spawn` around the arrival; the linker emits
  one `info_player_start` per `spawn` POI of the up room in the stock
  fallback (and the mod uses them directly). A level option
  `spawn_count: K` refuses a level whose up room has fewer than K spawn
  points, so a multiplayer library is checked, not guessed.
- **A level with `up: none`** (the top level) still needs a spawn. Proposed
  (O21): the level YAML key `spawn` names a cell; by default the linker
  uses the `spawn` POIs of the room farthest (in doors) from the down room,
  ties broken by link order, and refuses a level where no room has one. The
  down room's arrival would be simpler but starts the player at the exit;
  the first room is arbitrary.

### 11.6 Entity cost

| Per level | With `-mod-entities` | Stock fallback |
| --- | --- | --- |
| Up room transition | 1 `logic_level_transition` | 1 `trigger_changelevel` + 1 `info_landmark` |
| Down room transition | 1 `logic_level_transition` | 1 `trigger_changelevel` + 1 `info_landmark` |
| Hallway fold | n/a | saves 1 per room (the `trigger_once` becomes the changelevel) |
| Arrival, spawn POIs | 0 | 0 |
| Level spawn | 0 | 1 `info_player_start` (K in multiplayer), all room starts stripped |
| **Total** | **2** | **5**, or 3 with both hallways folded; +K−1 for K spawns |

The player's button, door or trigger is the author's and counted with the
room. The transition volume costs nothing in either mode: it is consumed or
becomes the changelevel.

### 11.7 Tests

- **Layout:** role rooms placed exactly once each, at at least the minimum
  door distance, over many seeds and grids; `up: none` / `down: none`;
  a sequence with chained map names; a role-less library's output
  byte-identical to today's.
- **Rule:** each refusal of 11.1 and 11.3 with its message.
- **Rotation:** a role room at each of the four rotations: the volume,
  landmark, arrival facing and spawn point turn with it.
- **Both modes:** the emitted entities per mode (11.4), the hallway fold
  and its failure cases, the landmark names and positions, the
  `map` keys from the YAML.
- **Spawn:** the spawn at the up arrival at all four rotations, in both
  modes; room starts stripped; `spawn` POIs and `spawn_count`; the top-level
  spawn default.
- **Flatten:** link and `--flatten` emit identical entities in both modes,
  with the monolithic map written by its own code.

---

## 12. Engine limits for `CheckCapacity`

`LevelLinker.CheckCapacity` sums per-room counts before planning
(`LinkCounts.Of`, `LinkTotals.Add`). What each feature adds; "SDK" marks
Source SDK 2013 values not yet in this repo's tables (`BspLimits.Caps`,
`WriteLimits`), to be added with a validator rule in the same PR.

| Feature | New totals | Limit |
| --- | --- | --- |
| Entities (all features) | edict estimate; entity count; entity lump bytes | `cap − reserve` warn, 2048 refuse (6.7); 8192 (`MapFile.MaxMapEntities`); lump bytes SDK, **uncertain** |
| Brush entities | models | 1024 (`BspLimits.Caps`) |
| Names | resolved value length | 1023 bytes (5.6), **uncertain** engine cap |
| Static props | props, dictionary, leaf-list entries | `ushort` fields (65,535) |
| Detail props | props; `dplt` entries | 65,535 (`DetailPropEmitter.MaxDetailProps`) |
| Displacements | dispinfos; disp verts; disp tris | 2048 dispinfos (SDK); `DFace.DispInfo` `short` |
| Water | leaf water data; water texinfos | 32,768 (`WriteLimits.MaxMapLeafWaterData`) |
| Overlays | overlays; water overlays | 512; 16,384 (`MapOverlay`) |
| Cubemaps | samples; patched texdata and texinfo | 1024 samples (SDK; `WriteLimits.MaxMapCubemapSamples` and BSP0039 since PR 12); texdata 2048, texinfo 12,288 |
| Area portals | areas; portals ×2; clip verts | 256; 1024; `ushort` start, 128,000 (`WriteLimits`) |
| Lighting | lighting bytes; world lights; switched styles; styles per face; ambient samples | `MAX_MAP_LIGHTING` (SDK); 8192 (`WorldLightExporter`); 32 (`WriteLimits.MaxSwitchedLights`); 4 per face; 65,535 (`DLeafAmbientIndex`) |
| Pak | bytes | none beyond 32-bit lump offsets |

---

## 13. Implementation order

One PR per feature or small group. Already queued, and assumed:

- **Q1, link profile** (in progress in another agent): per-room and
  per-rotation work moved into the pack. Every precompute item above is a
  Q1-style pack section, per rotation or once as 1.1 decides.
- **Q2, library-wide shared tables** for planes, texdata and texinfo.
  Landed as level-wide sharing at link time: the link interns every
  room's moved planes, strings, texdata and texinfo by content
  (`LevelLinker.Tables.cs`), which took the stress library's largest
  linkable square from 20 x 20 (refused at `MAX_MAP_TEXDATA`) to 24 x 24
  (now refused at `MAX_MAP_BRUSHES`).
- **Q3, door-to-door visibility** for the linked PVS (line of sight through
  doorways, precomputed per room).
- **Q4, option C lighting** (section 9).

| # | PR | Size | Depends on | Why here | Lands with (section 15) |
| --- | --- | --- | --- | --- | --- |
| 1 | **Correctness fixes**: `info_ladder` bounds, `occludernumber` rebase, flattener keeps side-id references, overlay basis keys moved by split and flatten, `light_environment` never turned, library-wide entities collected from the gaps, refusal of non-zero `angles` on unknown brush-entity classes in split and flatten. | S | none | Each fix is a fact that fails today; later work builds on correct transforms. | 15.3 facts 1–6 and 9, red first; D for split and flatten |
| 2 | **Entity budget**: the class table (compile-only rows certain, default `edict`), per-room entity section, edict and entity totals in `CheckCapacity` with reserve, warnings, refusal and headroom report, `ssmap rooms` counts, `ssmap layout` budget, stripping of certain compile-only entities, the `.roomnav` file with its pack binding and the points of interest in it (store and strip `info_poi`, 10.4, 10.6), the section codec byte and rotation count (1.1) with the pack version raised. | M | Q1 | D7 makes it a top priority, and every later feature reports its cost through it. | 15.6; budget rows of 15.4; 15.5 for the new sections; the POI store and strip; 15.9 |
| 3 | **Naming and neighbour logic, one feature**: `cxry_` resolution, the rotation table, (a), (b) injected only when referenced, (c) for point entities and static-prop conditions, folding (relays, constant branches, `logic_auto` merge, filters), `-mod-entities` with `logic_room` and its stock fallback, the `SourceSharp.RoomContracts` assembly (7.5), the `RoomLinter` rule, `ssmap rooms` listing, one resolver shared by link and flatten. Facts for each mechanism at all four rotations (5.11). | M-L | 2, Q1 | Pure text and immediately useful (repeated rooms with logic), and it is the main lever on the entity budget. (c) on a brush entity cannot arise until #7 links brush entities; #7 adds model omission. | 5.11; 15.2 naming, mod-contract rows in both modes; 15.3 facts 7 and 8 (names); 15.4 naming rows |
| 4 | **Singletons and the library section** (section 8), with D3's refusal at pack time. | S-M | Q1, 1 | The sun section is Q4's input. | 15.2 singletons row; 15.4 sun and sky-camera rows |
| 5 | **Packed files**. | M | Q1 | Unblocks real content (finding 10); prerequisite of 6, 11, 12. | 15.2 packed files row; real-content set; 15.4 conflict row |
| 6 | **Static props** (zero-entity models). | M | 5 | High value, contained, and the cheap alternative to `prop_dynamic` under the budget. | 15.2 static props row; 15.4 hull and texel rows |
| 7 | **Brush entities**, origin-relative models, per-model collision, socket furniture, (c) model omission. | L | Q2, 3 | Doors and triggers; the biggest structural change, after the cheaper wins. | 15.2 brush entities row; (c) model omission; 15.4 angles row |
| 8 | **Transition rooms and the level spawn** (section 11): `room_role`, the level rule and YAML keys, layout placement (second stream) and `-sequence`, the transition volume, `logic_level_transition` and the stock `trigger_changelevel` + `info_landmark` fallback with the hallway fold, arrival and spawn POIs, the spawn `info_player_start` and stripping of room starts. | M | 2 (POIs), 3, 7 | Needed for any playable run of levels; the stock fallback's changelevel needs brush entities. | 11.7; transit sibling; 15.4 transition and spawn rows; both modes |
| 9 | **Q4 base bake** and **2D sky** flags. | L | 4, 6, 7 | The base bake must include props and brush entities; exact for capped rooms. | 9.7 checks; 15.3 fact 8 (lightmaps); 15.2 lighting row |
| 10 | **Q4 capture and response**, driven by the prototype (9.7). | L | 9 | Research: choose the basis from measurements. | 9.7 sweep; lighting tolerances (9.8) |
| 11 | **Overlays**. | M | 5, Q2 | Visual, contained. | 15.2 overlays row; 15.4 plug row |
| 12 | **Cubemaps**. | M-L | 5, Q2 | Needs the pak; Q2 eases texdata. | 15.2 cubemaps row |
| 13 | **Area portals and areas**, then **3D skybox**. | L, M | Q3, 7 | Door visibility and portals both describe what a doorway lets through. | 15.2 area portals and sky rows; 15.4 socket row |
| 14 | **Water**, first without water sockets, then with. | M, L | 13, 7 | Hardest cross-room case; safe refusal meanwhile. | 15.2 water row; 15.4 socket row |
| 15 | **Displacements**, no cross-room stitching. | L | 9 | Many lumps; lighting is a large part. | 15.2 displacements row; 15.4 socket row |
| 16 | **Detail props**. | M | 15, 9 | Depends on both; statistical equivalence. | 15.2 detail props row |
| 17 | **Multiple libraries per level** (17.2 to 17.5): `libraries` and `aliases` in the level file, cell resolution, the compatibility check (height excluded; the navigation grid refused, the other navigation settings warned, D25), the singleton rule at link and flatten (the skybox and the door portals option among the singletons; D24's summary line), PR 9's lighting rule across libraries with D26's sun warning, one pack per library found beside its VMF or named by `-rooms <key>=<pack>`, the level's pack id over every pack, `ssmap rooms` over several libraries. No pack format change. | M | 4, **8** | Pure text and lookup, no geometry: the cheapest of the five decisions, and the one the others build on. After PR 8 because both change `LevelYaml`'s keys and its unknown-key message (17.2). | 15.2 multiple-libraries row; 17.3 messages |
| 18 | **Combined packs** (17.10): `ssmap roompack`, the `NSPC` section, namespaced room names and `MapBase`, singletons applied at pack time, per-namespace reuse and `-only`, `ssmap room -namespace`, `-rooms` lookup of namespaces. | M | 17 | Makes a multi-library level consistent at pack time (one worldspawn and sun for every room) instead of warning at link. | 15.2 combined-packs row; 15.5 for the combined pack |
| 19 | **Room heights** (17.6): `room_height`, the door box fixed to the library's standard cell, the `SHAP` section and the pack version for shaped rooms, top-tree and solid-leaf bounds, the cell box in split, lint, props and furniture, the world-extent refusal, navigation columns taller than a cell and `.nav3d` version 3. | M | 7, **8** | One-cell rooms only, so no linker structure changes: the smallest step that gives varying heights, and the base the larger rooms extend. After PR 8 so its transit sample and `RoomTransit` are what the new cell box is applied to. Before PR 9 (Q4), whose bakes take the room's box. | 15.2 heights row |
| 20 | **Multi-cell rooms** (17.8): `room_footprint`, sockets per cell edge, the cell-block room compile and per-cell subtree roots, the top tree routing each covered cell, block-node omission, the footprint transform, joints along shared edges, `+` in the level file, the anchor cell for names and the refusals of neighbour names, navigation and props over the footprint, flatten. | L | 19, 3, 7, **8** | The large-area design. The biggest structural change since brush entities; everything it touches is already carried, so it lands after them. If Q3 has landed, its per-room door pairs are extended to several sockets on a face here. | 15.2 multi-cell row; 17.3 messages |
| 21 | **Height-aware generator over several libraries** (17.9): candidates from every library, the area stream and its groups, `-large`, `-group`, `-max-height`, `key=path` operands and the `libraries` output, the budget over every library. | M | 17, 20, **8** | Last, because it places what 17 to 20 make linkable; PR 8's role stream is kept as it is and runs on the unit tree. | 15.2 generator row; 15.5 layout determinism |

**PR 4 landed** (singletons and the library section). The split applies
D3 to every room (`RoomLibraryEntities.KeepInRoom`): a room's
`light_environment`, or a controller that is unnamed or named as the
library's copy, is dropped when its keys equal the library's (every key but
`id`, `hammerid` and `origin`, a missing key read as empty, outputs compared
in order) and refused otherwise with the 15.4 message, which names the room
and the first differing key; a room sun in a library without one is refused
too. The gaps may hold one sun and one controller per class and name. A
`sky_camera` is refused in a room (15.4) and, until the skybox room of PR 13,
in the gaps as well, where it used to be ignored. The link and the flatten
write each library entity once after the worldspawn, never turned, at the
level's origin (the one position safe in every level: vbsp's leak flood
skips an entity there), and run one keep-first rule (`LevelSingletons`)
after naming, which also keeps one `water_lod_control` and dedupes a host-
packed room's copies. The budget counts the library's entities once per
level (`LevelEntityReport.Library`, `LayoutEntityBudget.LevelEdicts`), and
`ssmap rooms` lists them on a `library:` line. The pack format version is
2: the layout is version 1's, but a version 2 pack promises its rooms were
held to the library's singletons, which the link cannot check from
compiled rooms, so a version 1 pack is refused with a message to recompile
the library. Decisions taken where this document is open: the four
controllers follow the sun's refusal text with their own class (only the
sun's and the sky camera's messages are given in 15.4); a named controller
the library does not hold is room-local, per the per-room fog note in
section 8; sky settings need no check of their own, since `skyname` comes
from the worldspawn every room copies and the sky colours are
`light_environment` keys.

**PR 5 landed** (packed files). The link writes one pak for the level
(`LevelPakFiles`): every placed room's files, read once per room from the
room's own pak lump, which the pack already stores byte for byte inside the
room's container, so the link reads nothing but the pack (D1). Files are
merged by name: equal bytes (method, checksum and stored bytes) are written
once, and two rooms packing one name differently are refused with the 15.4
text, naming both in link order (one room holding a name twice differently
is refused as `room {room} packs {file} twice with different bytes.`). The
room's default cubemap pair (`materials/maps/<room>/cubemapdefault.vtf` and
`.hdr.vtf`) is renamed to the level's map name, which the link takes from
its compile context's `MapBase` and `ssmap link` from the output file's
name, as vbsp takes it from the source's; every other file keeps its name,
the room-named patches included (4.13). A link given no map name refuses a
room that packs a default cubemap rather than carry it where nothing reads
it. The archive is stored, without timestamps, its entries in ordinal order
of their linked names, so it is a function of the file set: the same bytes
at any degree, run and layout order; a level whose rooms pack nothing
carries its first room's empty pak byte for byte, so no linked map without
packed files moved. Room compiles pack their default cubemaps again (an
interim fix had turned them off, `VbspContext.WritesDefaultCubemaps`, which
stays as a switch for hosts), since the link cannot build them without the
sky's textures. The pack format version is 3: the layout is unchanged and no
section is added, but a version 3 pack promises its rooms packed what vbsp
packs for them; a version 2 pack's rooms were compiled without the default
cubemaps, so a level linked from it would silently lack them, and it is
refused with a message to recompile the library (version 1 keeps its own
message). Decisions taken where this document is open: no per-room pack
section and no codec, because the entries are already in the container and
a stored zip's directory is read in microseconds next to the rest of a room
(1.1 stores a section only where it makes the link faster), and pak files do
not change with rotation, so they are stored once; the conflict is checked
at link, where the level says which rooms meet, not at pack time, so a
library whose two conflicting rooms are never placed together still links;
only the default cubemaps are renamed now, since the other renamed files
(cubemap sample copies and patches, 4.10; `.vhv`, 4.3) come with lumps that
are still refused, and their PRs add them to the rule; the flatten needs no
change, since vbsp packs the flattened level's files under the level's name
itself, and the real-content set (the 3x3 sample with the synthetic sky)
asserts the linked pak equals the flattened compile's, name for name and
byte for byte.

**PR 6 landed** (static props). `ssmap room` takes every `prop_static`
as the map loader read it, before vbsp turns it into a record and drops the
entity, and after the compile matches each record of the room's `sprp` lump
to its entity (the next one with the record's model, origin and angles,
which vbsp copied from it). It reads each dictionary model's meshes from
the game content with the read vbsp's hull build makes
(`StaticPropEmitter.LoadMeshesAsync`) and stores, per room, one `PROP`
section (`RoomStaticProps`): per dictionary entry the model's meshes in
model space (the hull, stored once: it does not turn), per prop its Hammer
id, its `room_needs` conditions, its socket and `socket_priority`, then the
rotation count (4) and every prop's pose per turn: origin, angles and
lighting origin turned, and its hull's box, the translation left to the
link. The records themselves, and the dictionary, stay in the room's `sprp`
lump inside its container, byte for byte. The section has the 1.1 framing
(codec byte, decoded length, revision; codec none). The link plans the
props before the pak (`LevelLinker.PlanProps`): per placement in link
order, each prop is kept when its `room_needs` holds (`RoomNeeds.Hold`, the
resolver's (c) rule on records) and, for socket furniture, when the level
keeps that side's furniture (`SocketFurniture`); the dictionary is merged in
the order a kept prop first names a model, each exact name once, so the
lump is a function of the layout. After assembly each kept record takes its
placement's translation (the same float additions as every moved point, a
zero unsigned as the flatten writes it) and its leaves
(`LevelLinker.WritePropsAsync`): a prop whose hull box lies in its cell and
clear of every jointed plug box keeps its room's own list, rebased, since
the linked tree below the top tree is the room's there; any other prop (by
a jointed door, or socket furniture) is walked through the linked tree with
vbsp's walk (`StaticPropLeaves`) and the managed hull rebuilt from the
stored meshes, which finds the carved doorway leaves and the neighbour's.
The lump replaces the first room's `sprp` in place at version 10; a level
whose rooms have no props carries the first room's game lumps byte for byte,
so no linked map without props moved and no golden digest changed. The
flatten needs no new transform: it moves a `prop_static` like any point
entity, and the pose turn is written to give the floats vbsp reads back
from the flatten's keys (`RoomStaticProps.Turn`), so the two maps' records
agree bit for bit; it applies `room_needs` through the resolver, as it
did, and drops socket furniture by the link's rule. vrad's prop lighting
files in a room's pak (`sp_N.vhv`, `sp_hdr_N.vhv`) are written once per
kept placement of their prop under its linked index, and left out for a
dropped prop (`LevelPakFiles`); rooms compile without vrad, so today only
a room a host lit carries them. Static props have no convexes in the
collide lump (the engine builds a prop's collision from its model's
`.phy`), so the collision merge is unchanged; the hull serves the leaves
and the cell rule. They cost the level no entity: `prop_static` and
`info_lighting` are compile-only, and a level with props has the entity
lump and budget of the same level without them. Limits: 65,535 props,
dictionary entries and leaf entries, each refused naming the placement
that crossed it. The pack format version stays 3: `PROP` is a tag an older
build skips, and that build still refuses a room with props by its lump; a
pack written before this PR has no `PROP` for a room with props, which this
build refuses with `room {room} has static props but no static prop data
from its compile (...); recompile the library with ssmap room.`, so no pack
reads silently wrong and there is no new promise for a version to carry.
Detail props keep their refusal and its text; the old refusal of static
props by lump id is gone. Measured on a 16 x 16 grid of hubs with four props
each (one by a jointed door), on a busy 4-core machine: poses stored for
four turns or for one (the link turning them) link in the same time within
the noise, so the writer keeps 1.1's default of four; walking every prop
added 50 to 70 ms to a link of about 30 ms, and reusing the room's lists
where they are exact brings that to about 10 to 20 ms. Decisions taken where
this document is open: O6 as recommended, refused when the room is packed
with the 15.4 text, the distance being how far the hull's box passes the
cell's (two decimals); socket furniture is a prop whose `room_socket` (O5's
key) names one of its room's sockets, and its hull may leave the cell only
into the doorway beyond that socket (the socket's plug box mirrored through
the cell face, the neighbour's plug box at a joint), tested on the hull, not
its box; O5's rule is applied to props now (dropped at a cap; at a joint
the side whose furniture has the higher `socket_priority`, its pieces'
highest, 0 unset, and on a tie the earlier in link order; a side with no
furniture never takes the doorway from one that has some), and brush
entities join it with PR 7; two refusals the table does not list are
added, `room {room}: prop_static {id} has room_socket "{value}", which is
not a socket of the room.` and `room {room}: prop_static {id} has
socket_priority "{value}", which is not a whole number.`; O13 as
recommended, refused before the room's compile with the 15.4 text (any
`generatelightmaps`); the stored hull is the model's meshes in prop space
with the pose per turn (the "prop-space box and hull" of 4.3), not planes;
lighting waits for Q4 (no base bake is stored, so there are no sunlit ×4
variants yet). A known difference, not refused: a `lightingorigin` naming a
neighbour's `info_lighting` (`cx+1ry_...`), or a global name several
placements define, is resolved inside the room by the room's compile and
across the level by the flattened compile's, so the two can light a prop
from different points; a room-local name (`cxry_...`) agrees. Not done
here: `ssmap rooms` does not list props, and the stress library, which
compiles without game content, has none.

**PR 7 landed** (brush entities). `ssmap room` describes every brush
model of a room's compile besides the world in one `BMOD` section
(`RoomBrushModels`, with the 1.1 framing: codec byte, decoded length,
revision; codec none): per model its entity's class and Hammer id, whether
it is origin-relative (its entity has an `origin`, from an origin brush
or the key alike: the loader rebuilt its brushes in the entity's own frame
when that is not zero, and one at the room's own origin is that frame at
zero, which a placement moves to the cell, so it too turns and does not
move; the flattened compile, meeting the moved origin, rebuilds it about
it), its `room_needs`, `room_socket` and `socket_priority`, the runs
of the room's lumps it owns (nodes, leaves, faces, leaf faces, brushes and
with them their sides, edges, original faces, vertex-normal indices; the
brushes are read from the map the compile loaded, the one place that says
a brush vbsp chopped away is still the entity's), and its collision
record's key data and per solid whether it was built with an outer hull;
then the rotation count (4) and per turn every model's bounds and
collision convexes turned, drag areas with x and y swapped on odd turns.
vbsp lays models out world first and model after model in every lump, and
the build checks that and reports anything else as a bug. The link plans
the models before the tree (`LevelLinker.PlanModels`): the world is model
0, then every placement's brush models in link order, each kept when its
`room_needs` holds (`RoomNeeds.Hold`, the resolver's (c) rule, which drops
its entity in the same case) and, for socket furniture, when the level
keeps that side's furniture (`SocketFurniture`, over a side's props and
brush entities together, `LevelLinker.LevelFurniture`); past
`MAX_MAP_MODELS` (1024, the world included) the link refuses naming the
placement that crossed it. A kept model's nodes, leaves, leaf faces, faces
and brushes are carried with the room's, its tree hanging from its own
head node; an omitted one's runs are left out of every lump that indexes
them and everything after shifts down (a prefix sum over the room's few
omitted runs), with its collision record and its entity. The linked face
lump is every placement's world faces, then every kept model's, so model
0's face range is the world's alone as in a map vbsp writes; the face ids,
macro textures and vertex-normal index runs follow that order. An
origin-relative model keeps its entity's frame: its vertices are linked
again, turned and not moved, after the room's own (vbsp shares one vertex
table among its models), and its planes and texinfos are interned in the
shared tables turned and not moved, which is the texinfo split of 4.1 done
as a lookup (a plane or texinfo both frames use is linked twice); its node,
leaf and model bounds turn and do not move, and its entity's moved `origin`
places it. A world-coordinate model is moved as the world is. A brush
model's brushes never fold into the world's boxes. Each kept model's
collision record is rebuilt from its stored convexes (moved for a
world-coordinate model), their client data renumbered to the linked
brushes, with the outer hull and drag areas its compile had, and its key
data (mass, material, volume) carried; a room compiled without a cooker has
none, as before. The plug census now takes only the world's brushes,
leaves and faces, so a door hung in a doorway or a trigger inside one is
never stripped or carved as the plug. Each brush entity's `model` key names
its linked model, and the furniture keys (`room_socket`,
`socket_priority`) are stripped from brush entities in the link and the
flatten alike (6.4); an entity whose furniture model the level omits is
dropped and leaves the entity budget. The flatten treats brush entities as
furniture by the same rule and drops them with their brushes. Omission
leaves the model's vertices, edges, surfedges, original faces, primitives
and lightmap bytes in place, unreachable (vertices may be shared, and the
rest is addressed only through the dropped faces), and the facts hold the
tree, faces, brushes, models, vis and collision of a level that omits a
model to those of the same level whose room never had it; the orphan
fallback of 5.8 was not needed and is not built. Decisions taken where
this document is open: O15 as recommended, with the known-direction table
empty (as far as this repository can tell, stock brush classes that move
take a direction from a key of their own, `movedir`, `pushdir` or `gibdir`,
and those that rotate start from `angles` as the model's orientation; a
class joins the table once checked in game, 15.8), so a brush entity's
`angles` and `angle` are carried as written and a room whose brush entity
(of a class vbsp does not consume) has them non-zero is refused by the
split, and so by the pack and the flatten, and by a room compile, with the
15.4 text (finding 9 and fact 9 of 15.3); the direction keys `movedir`,
`pushdir` and `gibdir` turn as a yaw on every brush entity, in the link and
the flatten (on point entities they are left as they were); O5 extends to
brush entities as PR 6 describes, a brush entity with `room_socket` being
furniture whose brushes the cell rule holds as any brush (inside the cell,
or crossing a cell face only as kit hardware), and a `room_socket` naming
no socket or a `socket_priority` that is no number refused with the prop's
text, the class in place of `prop_static`; storage is four turns, the
1.1 default (measured on a 16 x 16 level of two rooms of three brush
entities each, on a busy 4-core machine, the two storages link in times
within the run-to-run noise, the four-turn one no slower). The cell rule of
the model lint read an origin-relative entity's brushes in its own frame
and refused a door whose frame reaches below zero; it now reads them where
they stand. The pack format version stays 3: `BMOD` is a tag an older build
skips, and that build refuses a room with brush models by its model count;
a pack written before this PR has no `BMOD`, and this build refuses its
rooms with brush models with `room {room} has {k} brush entity models but
no brush entity data from its compile (...); recompile the library with
ssmap room.`. The capacity check counts a room's structures as compiled
(an upper bound when the level omits a model) plus its origin-relative
vertex copies. Brush entities cost one entity each (the class table's
default, an edict), less the ones `room_needs` and the furniture rule omit,
and the link's budget counts exactly the entities its lump holds. A known
difference, not refused: an origin brush whose centre is off whole units
gives the room compile and the flattened compile each their own truncation
of the centre (vbsp writes `origin` as whole units), so the axis can differ
by a unit between the two maps; on whole units they agree. Not done here:
the 3x3 sample's `cross` room did not grow its door and trigger (as PR 6
left the `tee` prop), since the harness levels carry the end-to-end facts
at every rotation and the sample's unchanged digests are what shows a
level without brush entities links as before; `ssmap rooms` does not list
brush models; and the stress library has none.

**PR 8 landed** (transition rooms and the level spawn). A room's part in
its level's transitions is read from its VMF at pack time by one function
(`RoomTransit.FromVmf`), which `ssmap room` stores in a `TRAN` section and
`--flatten` calls on the same library: its role (the `info_room`'s
`room_role`, which `LibraryRoom` already carried for navigation), its
transition volume's Hammer id and centre, the hallway `trigger_once` the
stock fallback folds (when every condition holds) and its centre, its
arrival and its `spawn` points, room-local. Only a room with a role or
spawn points has it, so a library without either packs to the same bytes.
The 11.3 refusals run there, before the room compiles, and the arrival's
clearance after it, against the compiled world's player-blocking brushes
(solid, window, grate, moveable, player clip, monster; a brush entity's
brushes do not block) and the cell. The level rule (11.1) and the spawn
(11.5) are decided once per link or flatten (`LevelTransitionPlan`), and
the one resolver (5.9) writes the result: with `-mod-entities` each volume
is dropped and a `logic_level_transition` stands at its centre (`direction`,
`map`, and `StartDisabled` carried from the volume when set); without it
the volume becomes the `trigger_changelevel` (touch disabled, and every
`Transition` output in the level that names it fires `ChangeLevel`), or
the hallway `trigger_once` becomes it (touch enabled, its transition
output gone, its own spawn flag 2, which on a `trigger_once` means NPCs,
cleared) and the volume is dropped; one `info_landmark` per transition
room, named `<upper>__<lower>`, at the down room's changelevel centre
(the folded trigger's when it folds) and at the up room's arrival; and
one `info_player_start` per spawn point after the spawn room's entities.
The link omits a dropped volume's model as it omits any (`PlanModels`), and
the flatten leaves the entity out with its brushes, so the two maps carry
the same entities and model count in both modes at every turn. Every
`info_player_start` the rooms hold is stripped, in both modes. `ssmap
layout` places the roles from a second `SplitMix64` stream seeded with the
seed exclusive-or'd with a fixed constant (`LevelGenerator.RoleStream`):
per spanning tree it shuffles the occupied cells, takes the first as the up
cell and the first other one at least `-transition-distance` tree doors
away as the down cell, the fill offers only a role's rooms in its cell and
only ordinary rooms elsewhere, and a filled level whose own joints bring
the two closer is treated as a failed fill. `-sequence K -name <base>`
writes the chained run. `samples/rooms-transit` is the transit sibling
(15.7), a fact holding it to its generator and another to what `ssmap
layout` writes for its run; the whole run was built end to end through the
CLI in both modes, each map passing `ssmap check` and agreeing with its
flattened compile. The contract version stays 1: section 7, which version
1 names as the contract, specified `logic_level_transition` from the start,
so a mod built to it already knows the class; the linker only began writing
it now. The pack format version stays 3: `TRAN` is a tag an older build
skips (such a build never applies the level rule), and a room whose compile
has a `trigger_room_transition` but no transition data (a pack written
before this PR) is refused at link with `room {room} has a
trigger_room_transition but no transition data from its compile (...);
recompile the library with ssmap room.`. Stored once, not per turn: a
handful of points the link turns as it turns every point entity.
Decisions taken where this document is open, or where it left a detail:

- **When a level has transitions.** A level whose file has a transition
  key, or that places a role room, is a level of a run: the rule holds, the
  transitions and spawn are written and the room starts stripped. Any other
  level is a standalone map and links and flattens exactly as before (every
  level of a role-less library; the 3x3 sample's digests do not move).
- **O20, O21, O22, O23 as recommended.** `up_map` / `down_map`; the
  `spawn` key is `spawn: [column, row]`, and without it the spawn points of
  the room farthest in doors from the down room (ties to the earlier in
  link order; with no down room, the first room in link order with spawn
  points); `-sequence` is built; `spawn_count: K` counts the spawn points
  the stock fallback writes, which for an up room are its arrival and its
  `spawn` points (so K = 1 + the room's spawn points, and the stock
  fallback writes K starts). Refusals the tables do not list: a `spawn`
  cell whose room has no spawn point (`level {level}: spawn names cell
  ({x}, {y}), which holds no room with a spawn point.`), a `spawn` cell in a
  level with an up room (`level {level}: names spawn cell ({x}, {y}), but a
  level with an up room spawns at its arrival point.`), and `spawn_count`
  on a top level names the spawn room (`... but spawn room {room} has {m}
  spawn points.`).
- **The fold** takes one more condition than 11.4 gives: the trigger must
  be the only thing that fires the transition, since the fold drops the
  volume and another caller's output would then name nothing. A hallway
  that fails any condition stays a caller like a button or a door.
- **More 11.3 refusals:** a `trigger_room_transition` named other than
  `cxry_transition`, one without brushes, and one in a room without a
  role, each with its own message (`RoomTransit`).
- **The landmark** is exactly 11.4's, one per transition room. As 11.4
  says of the facing, the upward trip is the uncertain one: the upper
  level's landmark of the pair stands at its down room's changelevel, so a
  player going up arrives there, offset, rather than at the down room's
  arrival; the mod mode places the player at the arrival. To be checked in
  game (15.8) before relying on the stock upward trip.
- **With `-mod-entities`** nothing is written for the spawn, and a link
  with transitions that writes no `.nav3d` warns that the mod has no
  arrival or spawn point for the level. Which room a top level spawns in
  is not carried to the mod (the stock fallback writes it); the mod picks
  from the navigation's spawn points until the contract says more.
- **Messages.** The 15.4 texts as written, with three spellings settled:
  a role's article (`an up room`, `a down room`) in the 11.1 map-key and
  the 11.3 volume and arrival messages, a final period on the map-key
  messages, and `none` for the cells of a role that has no room.
- **The level file.** A map name is letters, digits, `_`, `-` and `.`,
  since it is written into keys and landmark names; `spawn` is checked
  against the grid when read; the unknown-key message now names the
  optional keys too. The keys are written between `columns` and `grid`, and
  only when the level has them, so every level file written before is
  written as it was.
- **`ssmap layout`** with a role library and one level needs
  `-up-map`/`-down-map` or `-no-up`/`-no-down`; `-sequence` takes the maps
  from the run, up to 999 levels, names padded to two digits or more, and
  `-out` is then the folder. The minimum distance applies only when a level
  has both roles; a library lacking a role a level keeps is refused. The
  layout's entity budget adds, per role room in the stock fallback, its
  landmark and, for the up room, its starts (the arrival and spawn points);
  the stripped room starts are still counted, so it never under-counts.
  The header lines are unchanged.

Not done here: `ssmap rooms` lists roles but not arrival or spawn points;
the stress library has no roles; the navigation sidecar is unchanged (it
already carried the arrival points by role from PR 2's store).

**Q3 landed** (door-to-door visibility for the linked PVS). The link used
to close the door graph transitively (`DoorEdges`, `CloseRows`), and every
room of a level is reachable, so every cluster saw every other. It now
composes the PVS through the doorways (`LevelDoorVisibility`) from what
`ssmap room` stores per room (`RoomDoorVisibility`, the `DVIS` section),
without flooding the level or running vvis on it:

- **Per room, at pack time**, from the room's own vvis and plug census:
  per socket, the clusters that see its doorway (a cluster that sees, or is
  seen by, one of the socket's facing clusters); per socket pair, whether
  a line can cross the room from one doorway to the other (some facing
  cluster of one sees some facing cluster of the other, or the plugs
  touch); per cluster, the bounds of its open leaves. All three are
  rotation-free and conservative: vvis keeps every sight line, and each is
  a necessary condition for one.
- **At link**, per jointed doorway looked through from one side, a flow in
  the manner of vvis's portal flow over the doorway rectangles alone (on
  the cell faces, where the two plugs meet), treating each room as its
  empty convex cell: the next doorway is cut to what lies in front of the
  first, the first to what lies behind the next, and from the third on the
  next is clipped by the planes separating the first from the last
  (`VisClip`, vvis's own predicates, the separators memoised per frame as
  vvis does). A room is entered at most once per chain (a line crosses a
  convex cell once), and the flow only turns from one doorway of a room to
  another where the room's pair relation allows. Each room entered marks
  the clusters that see its entry doorway and whose bounds are not wholly
  behind the first doorway's plane or any separating plane (by a margin of
  one unit). A cluster sees what the flows out of the doorways it sees
  mark, and a pair is kept only when both directions keep it (vvis makes
  its rows symmetric the same way). Two rooms that share a cell face make
  one convex box, so a pair across it is kept only if a segment between
  the two clusters' bounds can cross the doorway (the plane's cut through
  the two boxes' hull must meet the doorway), and a pair across a face with
  no doorway is dropped. Inside a room the rows are its own vvis, the
  carved doorway joined to the socket's first facing cluster (which sees,
  and is seen by, whatever sees the doorway, and whose bounds take the plug
  box). The PAS is vvis's: the union of the rows a cluster sees.
- **Exact, rotation-free, deterministic.** Each flow runs in the frame of
  the room it starts from (`RoomTransform.Unapply`), and every doorway and
  box is moved into it by quarter turns and whole cells, which round
  nothing on integer geometry; so a level turned as a whole runs the same
  numbers and composes the same rows (the turned 3x3 samples are held to
  that, cluster for cluster), and each room's flows write only its own
  rows, so any thread count writes the same bytes. A flow that would enter
  more than 2^22 rooms gives up and marks every cluster (never reached;
  a fact lowers the cap to exercise it).
- **Conservative, and how it is proved.** A linked PVS cannot be checked
  against vvis on the flattened level pointwise: that map's clusters are not
  the linked map's, and a monolithic cluster pair that sees each other says
  only that some point of one sees some point of the other, which only the
  old closure ever satisfied everywhere. The facts check the claim itself:
  for pairs of sample points (nine in each open leaf of the flattened
  level's compile) joined by a segment through open leaves only, vvis on the
  flattened level keeps the pair (so these are lines vvis must keep) and
  so does the linked PVS; on all sixteen 3x3 cases, on generated 4x4, 5x4
  and 5x5 levels of the sample's rooms, and on a U-turn whose ends no line
  joins. The linked PVS also always lies within the door graph's closure and
  keeps every room's own rows.
- **Gains.** Cluster pairs marked visible, before (the closure, every pair)
  and after, with the visibility lump's bytes; vvis on the flattened level
  for comparison where it was run (it has more, smaller clusters):

  | Level | Clusters | Before: pairs, bytes | After: pairs, bytes | vvis on the flattened level: clusters, pairs, bytes |
  | --- | --- | --- | --- | --- |
  | 3x3 sample (the four turns alike) | 32 | 1,024, 516 | 564, 518 to 522 | 43, 855 to 959, 865 to 902 |
  | 3x3 seeded levels (12) | 16 to 34 | all pairs, 196 to 616 | 56% to 83% of the pairs, the lump within +48 bytes | 27 to 55 |
  | generated 5 x 5 of the sample's rooms | 66 | 4,356 | 1,918, 1,741 | 111, 3,904, 3,872 |
  | stress 8 x 8 | 315 | 99,225, 27,724 | 31,435, 26,386 | 429, 38,983, 43,817 |
  | stress 12 x 12 | 676 | 456,976, 120,332 | 58,186, 87,842 | 917, 76,296, 143,373 |
  | stress 24 x 24 | 2,682 | 7,193,124, 1,823,764 | 281,908, 665,441 | |
  | stress 33 x 33 | 5,133 | 26,347,689, 6,631,840 | 686,929, 1,958,372 | |

  On a large level the lump shrinks about threefold and the pairs
  thirty-fold; per cluster the linked map sees about what vvis on the
  flattened map sees (86 clusters on average at 12 x 12, against vvis's 83
  of its smaller clusters). On a level as small as the 3x3 sample the lump
  need not shrink: a row of 32 clusters is four bytes all ones, and the
  run-length code spends two bytes on each zero byte it gains.
- **Link time** (the 256-room stress library, pack in the page cache):
  medians and minimums of 11 warm links in one process, and of three
  cold `ssmap link -no-nav` runs, on a 4-core box shared with other work
  (load 30 to 40, so the spread is wide):

  | Level | Warm, main | Warm, Q3 | Cold `ssmap link`, main | Cold, Q3 |
  | --- | --- | --- | --- | --- |
  | 24 x 24 | 68 to 98 ms min, 109 to 230 median | 117 to 175 ms min, 154 to 232 median | 1.02 to 1.10 s | 1.23 to 1.60 s |
  | 33 x 33 | 153 to 236 ms min, 205 to 308 median | 300 to 367 ms min, 343 to 446 median | 1.45 to 1.74 s | 1.92 to 2.08 s |

  The door flows cost about 110 ms of one thread at 33 x 33 (about 54,000
  rooms entered from 2,800 doorways), the rows (a bit-matrix transpose and
  the neighbour tables) about 35 ms, the PAS about 15 ms; the flows run one
  room per work item on the link's thread pool. The written map is smaller
  (33 x 33: 19,084,940 bytes to 14,411,580), and `ssmap check` passes it
  with the one warning main's map has (no cubemap samples).
- **Pack.** Version 4. `DVIS` follows `LNKA` (read with it), carries the
  codec byte and decoded length every link section starts with (none by
  default: 70 to 298 bytes a room, 41,878 bytes for the stress library's
  256 rooms, 0.15% of its pack), a revision,
  then the rotation count and that many payloads: cluster count, socket
  count, the per-socket bit sets, the pair flags, and per cluster a flag and
  its box. The relations do not change with a turn, and turning a room's few
  cluster boxes at link is a handful of negations per placement, so it is
  stored once; a count of 4 (each payload's boxes turned) is read, its four
  payloads' relations must agree, and a fact holds it to the same link
  bytes (the choice can move on measurement alone). Version 4 promises
  `DVIS` for every room with link sections, and a version 4 room without it
  is refused as damaged; a version 3 pack still links, its door visibility
  worked out at link from the rooms it holds, to the same bytes (a fact).
  Damaged sections are refused naming the room and section. PR 6's
  `PROP`, PR 7's `BMOD` and PR 8's `TRAN` stay optional tags (their notes
  above say why they needed no version): a version 3 pack that has them
  still reads, and links to the same bytes it did, `DVIS` worked out at
  link. Transition rooms are ordinary rooms to the door visibility: their
  sockets and clusters are read the same way.
- **Brush entities and props.** `DVIS` is read off the room's own vvis
  and the world's plug census, and the link's flows use only the world's
  clusters and doorways, so neither PR 7's brush models (kept or omitted
  as furniture: an omitted model's leaves hold no cluster of their own,
  and vis is the world's) nor PR 6's props change a room's door
  visibility or the flows; a brush door hung in a doorway, like any brush
  entity, does not block vis, as it does not in vvis.

Decisions taken where this document is open: the conservative claim is
about sight lines (as above), not pointwise containment of the flattened
compile's rows; the doorway is the plug's face on the cell face, not the
tunnel of the two plugs' inner faces, which would be tighter but holds only
where a room's wall is at least the plug's depth all round the socket, which
nothing checks; the per-room relations are read off the room's vvis rather
than recomputed with a flow inside the room, so they carry vvis's own
slack; the PAS is vvis's radius-two union, not the PVS; the closure is
kept behind `-nodoorvis` (`LevelLinkOptions.DoorVisibility`) as the
measure and for hosts that want the old bytes; the `ssmap link` line
reporting the map is followed by one reporting the visible pairs and the
lump's size. Area portals (PR 13) can now take the doorway flow's per-joint
cones as the place to start.

**PR 12 landed** (cubemaps). `ssmap room` stores, per room whose compile
has `env_cubemap` samples, one `CUBE` section (`RoomCubemaps`, with the 1.1
framing: codec byte, decoded length, revision; codec none): the map name
the room was compiled under (its `MapBase`, the room's name lower-cased,
which every name vbsp made after a sample holds); the rotation count (4)
and per turn every sample's origin as the room's loader read it, turned;
the texdata strings that are a sample's patch, each with its table entry,
sample and patched material; and the packed files named after a sample,
each with its kind (patched material, LDR or HDR texture copy), sample and
material. A string is a patch when it is `maps/<map>/<material>_<x>_<y>_<z>`
at one of the room's samples' positions and the room packs its patch
file (vbsp writes one whenever it makes the texdata, so an authored name
that only looks like one is left alone); a file is a sample's when it is
a patch file, a dependent's (`$bottommaterial`, `$crackmaterial`,
`$fallbackmaterial`, which has no texdata) included, or one of the
sample's two default cubemap copies. The sizes stay in the room's own
cubemap lump, which the container holds byte for byte. The link plans the
level's cubemaps once, in `CheckCapacity` and again before the pak merge
(`LevelCubemaps`): per placement in link order, each sample's origin
turned, plus the placement's offset with the float additions the flatten
makes (`QuarterTurn.Apply`), truncated as vbsp truncates; each patch
renamed to `maps/<level>/<material>_<X>_<Y>_<Z>` and each texture to
`maps/<level>/c<X>_<Y>_<Z>`, the level's name being its compile context's
`MapBase` (`ssmap link`: the output file's name), as for the default
cubemaps (PR 5). The cubemap lump is every placement's samples in link
order, their sizes and padding the room's, which is the order the flattened
level lists its `env_cubemap`s in; a placement's texdata strings are
interned under their linked names (`LinkTextures.InternStrings`), so each
placement of a room with patches brings its own patched texdata and
texinfos, and the capacity check counts them at every placement; the pak
writes each of a room's cubemap files once per placement under its linked
name, a patched material's text rewritten by replacing, in one pass over
its quoted values, the sample's old texture and patch names with the new
ones, so the file is what vbsp writes for the same material and sample in
the flattened level; the room-named copies are not carried. A level whose
rooms have no sample writes no cubemap lump and the same bytes as before.
The flatten needs no change: it moves `origin` (4.2) and keeps `sides`
lists on the moved sides (PR 1), and vbsp names the flattened level's
samples and patches after the level itself. Measured equivalence: the
harness hub (two samples off the whole units, one naming a slab by
`sides`, one left to the nearest; a specular material with a specular
`$bottommaterial`) placed twice at the four turns, and the real-content
3x3 sample with a specular block and a sample in its `hall`
(`rooms3x3`, `rooms3x3_turn1`, through `ssmap room`, `ssmap link`, `ssmap
check` and `--flatten` with `ssmap vbsp`): the linked and flattened maps
carry the same cubemap lump, the same patched texdata names and the same
packed files byte for byte, every patched face of the linked map names a
sample of its own cell, and `ssmap check` reports the linked map clean,
the "no cubemap samples" warning (BSP0029) that every linked level had
gone once a room carries a sample. The pack is the same bytes at one
thread and four, and so is the link. Cubemaps cost no entity (`env_cubemap`
is compile-only): a level's entity lump and budget are those of the same
level without samples. The pack format version stays 4: `CUBE` is a tag
an older build skips, and that build refuses a room with samples by its
lump; a pack written before this PR has no `CUBE` for a room with
samples, which this build refuses with `room {room} has {k} cubemap
samples but no cubemap data from its compile (a pack written before the
link carried cubemaps, or a room built without ssmap room); recompile the
library with ssmap room.`, so no pack reads silently wrong. Decisions
taken where this document is open, or where it left a detail:

- **O9 as recommended.** Each room's faces keep the samples its own
  compile chose; the map name is fixed at link, and a link given none
  refuses a room that packs a cubemap file, with PR 5's message.
- **The origins are stored as read, not as the lump's integers.** vbsp
  truncates a sample's origin toward zero, and the flattened compile
  truncates the moved float, so turning the room's truncated integers
  would put a sample off the whole units a unit away wherever the turn
  negates it (10.5 turned twice into a 256-unit cell is 245.5, which
  truncates to 245; the room's truncated 10 turned and moved is 246). The
  stored floats give the flattened compile's integers for any origin, so
  no origin is refused and there is no known difference here.
- **Storage is four turns**, the 1.1 default for placement records: twelve
  bytes a sample a turn, against four float negations a sample at link,
  both far below what the link's timings resolve, so the default stands
  without a measurement to move it.
- **Limits.** `MAX_MAP_CUBEMAPSAMPLES` joins `WriteLimits` (1024) and the
  capacity check refuses past it with `room {room} at cell ({x}, {y})
  pushes the link to {n} cubemap samples; vbsp writes at most 1024
  (MAX_MAP_CUBEMAPSAMPLES).`; since this repository cannot settle whether
  the engine reads more, `ssmap check` reports a map past it as a warning
  (BSP0039), not an error. A renamed patch as long as vbsp refuses (127
  characters or more, `TEXTURE_NAME_LENGTH` less one) is refused with
  `room {room} at cell ({x}, {y}): the cubemap patch {name} is {n}
  characters long; vbsp names a patch in fewer than 127.`, and a patched
  material packed compressed, whose text the link cannot rewrite, with
  `room {room} packs {file} compressed; the link renames the cubemap names
  inside a patched material and reads only stored files.` (vbsp always
  stores).
- **Two samples at one truncated position** share their names, as vbsp's
  do; the first takes them.

A known difference, not refused (O9): near a door the flattened compile's
nearest sample for a face can be the neighbour's, where the linked face
keeps its own room's, and then the two maps' patch for that face differ.
Not done here: the checked-in 3x3 sample's `hall` did not grow its sample
and specular floor (as PR 6 and PR 7 left theirs), since the real-content
fact adds them to the library in the test and the sample's unchanged
digests are what shows a level without cubemaps links as before; `ssmap
rooms` does not list samples; the stress library has none; and whether
`buildcubemaps` on a linked map writes the names the patches expect stays
on the in-game checklist (15.8).

**PR 11 landed** (overlays). `ssmap room` describes a room whose compile
wrote overlays in one `OVLY` section (`RoomOverlays`, with the 1.1 framing:
codec byte, decoded length, revision; codec none): the overlay count, then
the rotation count (4) and per turn every record's origin turned (not yet
moved), its `BasisU` and its basis normal turned, each turned direction's
zeros unsigned as the flatten writes them. The records and the fade lump
stay in the room's container byte for byte; a room without overlays gets no
section, so a library without them packs to the same bytes. The link
carries the `Overlays` and `OverlayFades` lumps (`LevelLinker.LinkOverlays`):
every placement's records in link order, the placement's overlay `k` taking
id `OverlayBase + k` (vbsp numbers a map's overlays in entity order, and the
flatten writes the placements' entities in link order, so its compile
numbers them the same way), its texinfo the shared table's (an overlay's
texinfo has zero axes and a -99999 offset, which no turn or move changes,
so every placement of a material names one entry), its origin the turned
origin plus the placement's translation added as one vector, exactly as the
flatten moves `BasisOrigin` (`QuarterTurn.Apply`), zeros unsigned, its basis
normal and `BasisU` the stored turn's, and its face list each room face's
linked face; the UV points' `x` and `y`, the handedness flag, the extents,
the render order and the fades are the room's. A face the level does not
draw is left out of the list: a jointed plug's (kept in the face list,
drawn nodraw) and a face of a brush model the level omits (`room_needs`,
socket furniture), neither of which the flattened level has; the second is
what an overlay on a dropped door comes to, and the flatten's compile drops
it the same way. A level whose rooms have no overlays carries neither lump,
so no linked map without overlays moved and no digest changed. A named
overlay's `info_overlay_accessor` keeps the overlay's keys: the link
rebases its `OverlayID` by the placement's base, moves its `BasisOrigin` as
the record's and turns its `BasisU`, `BasisV` and `BasisNormal` as the
flatten does (on that class only, so a room without overlays turns its
entities exactly as before). Facts hold link and flatten at every rotation
to the same overlays, bit for bit but the face lists, and to the same area
of each overlay's square covered by its faces (each face clipped to the
square in the overlay's basis, summed: the union polygon of 4.9, whatever
faces the two compiles cut), and the accessor to the same keys but one; a room with both an overlay and
a cubemap sample (the overlay on a face a sample patches) links as it
flattens at every turn and passes `ssmap check`.
The pack format version stays 4: `OVLY` is a tag an older build skips, and
that build refuses a room with overlays by its lump; a pack written before
this PR has no `OVLY`, and this build refuses its rooms with overlays with
`room {room} has {k} overlays but no overlay data from its compile (a pack
written before the link carried overlays, or a room built without ssmap
room); recompile the library with ssmap room.`, which also guarantees every
linked overlay was held to the rules below when its room was packed.
Overlays cost what 15.6 says, 1 per named overlay (the accessor, a default
`edict` class) and 0 otherwise (`info_overlay` is compile-only), and the
counts already came from the compiled lump, so the budget needed no
change. Stored four turns, the 1.1 default: measured on a 16 x 16 level of
the two harness rooms (384 overlays) on a busy 4-core machine, the
minimum of nine warm links is 62 to 74 ms with either storage, the same
within the noise; a count of 1 is read and linked to the same bytes (a
fact). Decisions taken where this document is open, or where it left a
detail:

- **The plug refusal** is 15.4's text, made by the split
  (`RoomOverlays.PlugProblem`), so by the pack and the flatten alike, and
  by a room compile given a VMF: an `info_overlay` whose `sides` (read as
  vbsp reads the list) names a side of a world brush whose box is a
  socket's plug box, the rule the flatten leaves joined plugs out by. A
  side id the room does not have (another room's, or a stale one) is not
  refused: vbsp ignores it, in the room's compile and in the flattened
  level's alike, so the two maps agree.
- **`room_needs` on an overlay** is refused, with a message of its own
  (`room {room}: entity {id} (info_overlay) has room_needs, but an overlay
  is built into its room's compile and cannot be dropped.`): an unnamed
  overlay leaves no entity for the resolver to drop, and dropping one
  would renumber every later overlay the accessors name.
- **Water overlays** (`overlaytransition`, the `WaterOverlays` lump) are
  not carried and stay refused by their lump until water (PR 14): they are
  drawn along water, which the link refuses, and the split carries neither
  a world-level `overlaytransition` chunk nor moves its bracketed basis
  keys.
- **The link's cap** is 512 overlays (`MAX_MAP_OVERLAYS`), counted in
  `CheckCapacity` before any room is planned: `room {room} at cell ({x},
  {y}) pushes the link to {n} overlays; a map holds at most 512
  (MAX_MAP_OVERLAYS).` vbsp refuses a map past it, so the flattened level
  would not compile. The 64 faces an overlay holds need no check: the link
  only drops faces. Two lumps vbsp would not write are refused as damaged,
  naming the room: a record whose id is not its place, and a fade lump of
  another length.
- **A known difference, not refused:** the accessor's `sides` holds the
  room's side ids in the link and the flattened map's renumbered ones in
  the flatten (its `hammerid` differs as every entity's does). The game
  finds the overlay by `OverlayID`; that nothing reads `sides` at runtime
  is **uncertain** and belongs to the 15.8 checklist.
- **Fact 3 of 15.3** named a joined plug from its `info_overlay`, which is
  now refused; the plug is named by its `info_no_dynamic_shadow` instead,
  which still exercises the flatten dropping a joined plug's side.

Not done here: the 3x3 sample's `tee` did not grow its overlay (as PR 6
and PR 7 left the sample alone), since the harness levels carry the
end-to-end facts at every rotation and the sample's unchanged digests are
what shows a level without overlays links as before; the stress library has
none; `ssmap rooms` does not list overlays. An authoring note the facts
met: vbsp finds a side by the first side with its id once the loader has
sorted each brush's sides, so a hand-built brush whose sides share one id
(as the room model's do) is named by whichever side sorts first; Hammer
gives every side its own id.

**PR 9 landed** (the Q4 base bake and the 2D sky flags). `ssmap room`
lights every room after its compile (`RoomLighting`, from
`RoomLibraryCompileSettings.Lighting`): one vrad run of the room exactly as
compiled, sealed by its plugs, with the library's `light_environment` added
right after the worldspawn at the level's origin, as the link and the flatten
write it (D3). Everything vrad lights is stored: per face its four styles and
lightmap offset and its luxels (bump pages and the per-style average luxels
included), the room's world lights, the leaf ambient index and samples, the
static props' vertex colours (when the switches ask for static prop
lighting), and for the room as a whole vrad's vertex normals and their
indices, the map flags, pass one of the sky test (each leaf holding a sky
face) and the sun's two world lights. Detail prop and displacement lighting
wait for the PRs that lift those refusals (15, 16). The link lays a
placement's stored turn over its compile (`LevelLinker.PlanRoom`) and writes
the level's lighting (`LevelLinker.WriteLighting`); nothing is traced and no
game file is read at link.

- **Once or four times (1.1, D16).** A room with a sky face (`SURF_SKY` or
  `SURF_SKY2D`; pass one flags exactly the leaves holding one, so the two
  halves of 1.1's rule are one test) in a library with a sun is lit four
  times; any other room once. A library without a `light_environment` has no
  sun or sky light at all, so its sky rooms are stored once too (a case the
  rule did not name). The link takes payload `rotation mod count`.
- **The bake frame.** Each run lights the room in its own frame, which keeps
  the lightmap layout, with everything vrad holds fixed in the world turned
  into that frame by the inverse of the placement's turn (`BakeFrame`, a
  `VradContext.FrameTurns` the room bake alone sets): the sun's direction,
  the sky-ambient sampling directions, the sun's area jitter, the leaf sky
  probe, the direction tables of the leaf ambient cubes and of prop direct
  and indirect lighting, and the order leaf ambient candidates are drawn in.
  Turning only the sun, as 9.2 put it, is not enough: the sky's sampling
  directions are a fixed, not quarter-turn symmetric set, so a turned room
  saw a different sky. Turn 0 is the arithmetic vrad always ran.
- **Linear values (9.3).** Luxels, ambient cube faces and vertex colours are
  stored as half floats of the exact value of the `ColorRGBExp32` vrad wrote
  (an 8-bit mantissa times a power of two, which a half holds exactly for
  exponents -24 to 8), and encoded once at link with vrad's own encoder,
  which gives vrad's bytes back; a fact holds every canonical encoding in
  that range and every stored value of a real bake to it. PR 10 sums door
  terms onto these before the one encode.
- **Stored once, turned at link.** Every payload is in the room's frame;
  what has a direction in it is turned at link: ambient cube faces permute,
  ambient position bytes permute and flip (`255 - b`), world light origins
  and spot and surface normals turn and move, vertex normals turn (a
  negative zero written as zero, as vrad writes a turned map's). Measured on
  the 256-room stress library, the link's whole lighting work at 33 x 33,
  turns, encodes and ambient included, is about 0.6 s (below), so
  pre-turned copies were not worth four times the bytes.
- **Written once per stored turn.** Every placement of a room at one stored
  turn points its faces at one block of lightmaps, and every placement of a
  room at one turn at one run of ambient samples; a face reads only its own
  luxels, so sharing changes nothing a face sees. Vertex normals are
  interned by value across the level, since the 16-bit normal index would
  not hold a large level's rooms end to end; a level past a field (65,536
  ambient samples, normals or leaves an index names, 8,192 world lights) is
  refused naming the placement that crossed it.
- **The level's lumps.** Faces (and `FacesHdr`) take the stored styles and
  offsets; original faces keep their compile's; the world lights are listed
  as vrad lists a full compile's (every placement's entity lights, the last
  placement first, then the sun's two, then the surface lights), clusters
  rebased; a leaf with samples names its run, an empty leaf the nearest leaf
  with samples (vrad's rule, remapped; for the link's own new leaves found
  in the linked tree), and a carved doorway the facing leaf's run (9.4); the
  map flags are the bake's. With a sun, the sky flags are pass one from the
  rooms and pass two over the linked PVS (per cluster, which is vrad's
  per-leaf walk to the same answer), 3D sky clearing 2D; without a sun the
  leaves keep their compiles' flags, as vrad leaves a map's.
- **Switchable styles (section 8, finding 8, 15.3 fact 8).** The link
  renumbers every named light's `style` over the linked entity lump in its
  order, one style from 32 per distinct name as vbsp numbers the flattened
  map (`LevelLightStyles`), and a lit level's faces and world lights follow
  their lights. The fact was red first (both lights kept style 32). This is
  done whether or not the level is lit, so an unlit level whose rooms name
  lights in two placements now carries distinct styles; every other unlit
  level links to the bytes it did.
- **Exactness, measured (9.7 check 5, 9.8).** A room alone, every socket
  capped, links to vrad of its own linked map at every quarter turn, byte
  for byte on every luxel, face style, world light, leaf ambient index,
  vertex normal, prop colour, sky flag and map flag, for a room lit once (a
  lamp, a static prop, a door) and a sunlit one (a sky ceiling, stored per
  turn). Leaf ambient samples are the same bytes at turn 0; at a turn, a
  room stored once gives each leaf exactly the turned room's mean light, and
  a sunlit room within 0.23 of it (p95), because which of a leaf's
  candidates vrad keeps depends on their last bits. Against the full compile
  of the flattened level (vbsp, vvis, vrad, same switches), the maps are the
  same luxels at turn 0; at a turn vbsp compiling the turned VMF places some
  walls' lightmap grids elsewhere (vbsp is not quarter-turn invariant), and
  where the grids meet at least 98% of luxels are exact and the rest within
  0.2 (a prop shadow's edge). A jointed level of the two rooms lacks the light
  its doorway lets through (the sun onto the hub's floor, each lamp into the
  other room): half its luxels and more are exact, p95 of the rest near 97%
  darker within a door width of the joint and elsewhere alike, which the door
  terms of PR 10 add; it invents no light (one luxel in a thousand at most
  is more than 5% brighter, where the room's own plug reflected a little).
- **Test rooms need real texture axes.** `RoomModel`'s slabs give every
  side the floor's texture axes, which leaves every wall's lightmap a line
  vrad cannot sample (it paints such a face red); the lit facts' harness
  world-aligns its sides, as Hammer does.

Storage: an optional `LITE` section per lit room, after its whole-room
sections, with the 1.1 framing (codec byte, decoded length, revision 1;
codec none), then the rotation count and that many payloads in turn order,
each behind its byte length, then the room-wide part (`RoomLighting`'s
remarks give the layout). The pack format version stays 4: `LITE` is a tag an
older build skips, and a pack written before this PR simply has unlit rooms.
A damaged section is refused naming the room and `LITE` (15.9's texts). A
level of lit and unlit rooms, or of rooms lit differently, is refused:
`room {room} has no baked lighting, but room {other} of the same level has;
a level's rooms are lit alike. Recompile the library with ssmap room.` and
`rooms {a} and {b} were lit with different settings (ranges, sun or map
flags); ...`.

Decisions taken where this document is open, or where it left a detail:
lighting is on by default in `ssmap room`, with `-nolight` as the off switch
(a pack and every level linked from it the same bytes as before, but for the
build identity each container records) and `-vrad "<stock vrad options>"`
for the switches, `-luxeldensity` below 1 refused since it would change the
room's geometry; the library compile's `-compliance` applies to the bake; a
host's settings default to unlit; the pack id and the incremental cache's
key carry the switches and the sun; HDR, LDR or both as `-vrad` asks, each
range linked; `ssmap rooms` lists each lit room's `lighting: 1 turn, no sun
or sky reaches it` or `lighting: 4 turns, sun or sky reaches it`; a point
light's normal (the direction of its entity's angles, which nothing lights
by) is kept as baked; a library's per-map `.rad` file is read under each
room's name, as the room compile names it. 15.9's "the four bakes of a
sunless room are the same bytes" holds for every face style, offset and
luxel, the world lights and the vertex normals; its leaf ambient and prop
colours come from world-fixed sampling directions and are the turned room's
own at link, as the capped-room facts show. Not done here: the door capture
and response (PR 10), the 3x3 sample's `end` room did not grow its sky
opening and switchable light (the harness levels carry both at every turn,
and the sample's digests stay those of an unlit link), and the stress
library has no sun.

Measured (4-core machine, the whole `ssmap` process, three runs each): the
256-room stress library packs in 15.6 s lit against 11.4 s unlit, 29.4 MB
against 27.2 MB (+8%); its 33 x 33 level links in 2.5 to 2.7 s lit against
1.7 to 2.0 s unlit, 15.4 MB against 14.4 MB, both clean under `ssmap check`.
The 3x3 sample packs to 561 KB lit against 518 KB, its level to 125 KB
against 98 KB; every level of the 3x3 and transit samples links lit and
passes `ssmap check`, in both emission modes for the transit run.

**PR 10 landed** (Q4 capture and response, the basis chosen by the
prototype of 9.7). A lit room now also records its door light
(`RoomDoorLight`, baked by `ssmap room` right after its base bake unless
told `-nodoorlight`), and the link adds to each placement of a level the
light its jointed neighbours send through their doors
(`LevelLinker.PlanDoorLightAsync`, then `WriteLighting`): lightmaps, leaf
ambient and static prop colours, per style. Nothing is traced and no game
file is read at link. Reach is one door (D3).

- **The basis the sweep chose: evaluated direct light plus a small
  response grid.** The design's basis on the opening (patches times
  directional lobes, 9.1 part 3) blurs what matters most: a lamp's light
  through a door is a sharp-edged beam. So the pack records, per socket,
  which part of the opening each side sees, and the link evaluates the
  beam itself, as vrad's direct lighting would, with no ray traced:
  - *receivers* (once per socket): every lit face's sample cells, and for
    each which of the opening's 6 x 14 cells (16 units on the kit's 96 x 224
    door) it sees past the room's own geometry;
  - *captures* (per stored turn, per socket): the room lit doors-open (plug
    brushes cast nothing, plug faces reflect nothing: the black box of 9.1),
    and every world light, the sun through the room's own sky included,
    that reaches a cell of the opening, kept whole with its falloff terms and
    the cells it reaches; what the room's surfaces and sky send out is
    gathered from each cell by 256 leaf-ambient rays and kept as stand-in
    point sources, one per 64-unit cube of the room the rays met, each as
    bright as the light it sends through the opening; a ray that leaves by
    another doorway meets nothing (the black box), and a sky hit is placed on
    the sky face;
  - *ambient receivers* (per stored turn, per socket): which cells each leaf
    ambient sample sees;
  - *responses* (once per socket, only for a room that reflects light or
    lights its props): the open room lit by nothing but a point emitter at
    each node of a 2 x 2 x 2 grid over the neighbour's interior, inverse-square
    and constant (sixteen vrad runs sharing their transfers): the light that
    bounced onto each face (half resolution, from `VradContext.BounceObserver`,
    so the emitter's direct light, which the link evaluates, is not in it),
    the leaf ambient samples and the prop colours, per unit of intensity.
  At link, each neighbour source is turned into the receiving room's
  door-local frame (`DoorFrame.ToNeighbour`: `(2 Depth - x, -y, z)`, whatever
  the two turns), and at each receiving cell (3 x 3 receivers a cell) the
  light passes only where the segment toward the source crosses a cell the
  receiver sees and a cell the source reaches (`DoorLightMath.Through`);
  luxels are the mean of the cells around them, as vrad's cells make its
  luxels. Its flux through the opening is shared over the response grid's
  eight nodes around it (trilinear, flux-matched), and each emitter's
  response added. Leaf ambient takes the stand-ins through the opening (in
  the cube's world-light units, a 255th of a luxel's) and the responses;
  props the responses.
- **The sweep** (`vrad` of the linked level itself as the reference, since
  it has the same faces; relative error of style-0 luxels as in PR 9's
  facts, "near" within a door width of the joint; two rooms jointed at a
  turn, lamps 200 to 800 bright; "reflective" is a shell of reflectivity
  0.6/0.55/0.5):

  | Basis | Scene | near p95 | near p99 | elsewhere p95 | energy |
  | --- | --- | --- | --- | --- | --- |
  | base alone (PR 9) | any lamp, dark neighbour | 1.00 | 1.00 | 1.00 | 0.65 to 0.92 |
  | patches 2 x 4, cosine lobes | lamp, dark neighbour | ~1.0 | 1.0 | ~1.0 | up to 3.0 |
  | point grid 3 x 3 x 3 | lamp on a node | 0.03 | 1.0 | | |
  | point grid 3 x 3 x 3 | lamp off the nodes | | 1.0 | | |
  | chosen, masks 6 x 14, 3 x 3 subsamples | constant lamp, centre | 0.013 | 0.26 | 0.20 | 0.998 |
  | chosen | constant lamp, off centre | 0.020 | 0.11 | 0.020 | 1.001 |
  | chosen | inverse-square lamp | 0.007 | 0.055 | 0.021 | 1.000 |
  | chosen | spotlight | 0.000 | 0.043 | 0.010 | 0.999 |
  | chosen | PR 9's fixture: lamp, prop, door, sky, sun, switchable lamp | 0.038 to 0.053 | 0.33 to 0.43 | 0.024 to 0.057 | 0.997 |
  | chosen, response grid 1 | inverse-square lamp, reflective | 0.22 | 0.25 | 0.23 | 1.019 |
  | chosen, response grid 2 | inverse-square lamp, reflective | 0.14 | 0.24 | 0.13 | 1.013 |
  | chosen, response grid 3 | inverse-square lamp, reflective | 0.21 | 0.24 | 0.16 | 1.018 |
  | chosen, response grid 2 | lamps in both rooms, reflective | 0.10 | 0.13 | 0.09 | 1.014 |

  The other knobs moved nothing or made it worse: masks 3 x 7, 12 x 28 and
  24 x 56 measure as 6 x 14 (3 x 7 loses a little on a spotlight's far
  edge); one receiver per cell leaves p99 up to 1.0 on beam edges and 2 x 2
  up to 0.5, 3 x 3 under 0.26; 64, 256 capture rays and 32 to 128 of the
  earlier virtual point lights agree within 2%; pruning response faces at
  1e-3, 1e-2 or 5e-2 of the brightest changes nothing measurable (1e-3 is
  kept); half-resolution responses lose nothing. The near p99 of 0.26 to
  0.43 is luxels a few percent of the peak, where the beam's edge on a
  far wall falls between vrad's samples. On the 3x3 sample (no reflection,
  constant lamps) against vrad of levels holding one door each (D3's
  reach): near p95 0.045, p99 0.14, elsewhere p95 0.045 to 0.105, energy
  0.99; against vrad of the whole linked level the link is 5 to 11% short of
  energy (light that crosses two doors, which D3 leaves out), near p95
  0.19.
- **Tolerances (9.8), held by facts.** A capped room links to the same
  bytes with its door light as without, at every turn, so it is still
  exact (`LevelLinkerDoorLightTests.ACappedRoomLinksTheSameBytesWithItsDoorLight`).
  A jointed level against vrad of the link: near and elsewhere p95 under
  0.08, energy within 2%, under 3% of luxels more than 5% brighter, the base
  alone at least twice as far off near the joint
  (`AJointedLevelAgreesWithVradOfTheLinkWithinTheTolerances`); reflecting
  rooms: p95 under 0.2 near and elsewhere, energy within 3%, the 99th
  percentile of any luxel's excess over vrad under 0.3 (measured 0.12:
  "invents no light") (`LevelLinkerDoorLightBounceTests`); the door light
  only adds to the base, every luxel of every style
  (`TheDoorLightOnlyAddsToTheBase`); a neighbour's switchable lamp crosses
  under its renumbered style; the hub's leaf ambient gains the sky seen
  through the door within a factor of three of vrad's (measured 2.1, one
  stored sample against vrad's seven) and its prop's colours end nearer
  vrad's.
- **Styles (9.3).** Door terms keep the sending light's style, renumbered
  for the level as the base's are; they are summed into the receiving
  face's slot of that style, or a new slot; a face that would need more
  than four drops its weakest door-only style with 15.4's warning, `face
  {face} of room {room} at cell ({x}, {y}) needs {k} light styles; the
  lightest door style {s} was dropped.`, printed by `ssmap link`. Only
  style 0 bounces (as in vrad) and only style 0 reaches leaf ambient.
- **Once or four times (D16, O14).** Captures and ambient receivers follow
  the base bake (x4 for a sunlit room); receivers and responses are stored
  once: a fact lights a response at each quarter turn in the bake frame and
  finds every face's bounced light the same and the leaf samples within
  4e-6. Every door-lit face gets its own block of lightmaps (its base
  decoded, the door terms summed, encoded once, averages the medians vrad
  writes), so stored-turn sharing now holds for faces no door light
  reaches.
- **vrad hooks.** `VradContext.BounceObserver` hands the bake each luxel's
  bounced light as it is added, and `NoTextureLights` lets a response run
  leave the room's texture lights dark. Neither is set by `ssmap vrad`, so
  vrad's output is unchanged (2fort under T4 `13dd86de1bde7eb2` at 4 threads
  and 1).

Storage: an optional `DLIT` section per lit room, right after `LITE`, with
the 1.1 framing (codec none, revision 1): the sockets' receivers (per face
its cell codes, 0 none, 1 every cell, 2 some, then the partial masks as
128-bit words), a range flag byte, and per range the captures and ambient
receivers per stored turn and socket (each source 85 bytes) and the
responses per socket (none or sixteen emitters, each its faces' half-float
luxels, its samples' leaf, position and cube, its props' colours). The pack
version stays 4; a pack without `DLIT` links its lit rooms with their base
alone, as PR 9 did. A section that does not fit its room is refused naming
the room and `DLIT` (the facts cover each field). The setting is part of the
lighting's description (`|door:1`), so the pack id and the cache keys
change with it and a level of rooms lit with and without it is refused as
lit differently.

Decisions taken where this document is open, or where it left a detail:
the door light is on by default for lit rooms, `-nodoorlight` the off switch
(a usage error with `-nolight`), a `-nodoorlight` pack what PR 9 wrote (the
same sizes, 561 KB and 29.4 MB); the design's patch-by-lobe basis is replaced by evaluated
direct light plus the sixteen-emitter grid, measured above (O14: one
response set per door); a room of surfaces that reflect nothing and no lit
props stores no responses (the samples and the stress library); the
capture's rays read what vrad's leaf ambient reads (a surface's average
times its reflectivity, the sky ambient on sky); doorway faces still do not
exist, so the one-door reference and vrad of the link differ from a full
compile there as in PR 9. Error left (9.6): light crossing two doors (5 to
11% of the energy on the 3x3 sample), the second bounce back through the
same door, and in reflecting rooms the grid's coarseness (p95 0.10 to 0.14
near and elsewhere, the receiving side 1 to 4% bright).

Measured (4-core machine, the whole `ssmap` process, three runs each): the
256-room stress library packs in 13.6 s with door light against 8.0 s
with `-nodoorlight` and 6.4 s unlit on this machine, 30.7 MB against 29.4 MB
(+4.3%; no room reflects or lights props, so no responses) and 27.2 MB; its
33 x 33 level links in 3.1 to 3.2 s against 1.9 to 2.0 s base only and 1.6
to 1.7 s unlit, 20.6 MB against 15.4 MB (the door-lit faces' own lightmap
blocks) and 14.4 MB, all clean under `ssmap check`; of the 1.2 s, 0.65 s
is the door terms (after skipping a source at a cell whose four corner
receivers miss the same edge of an opening, `DoorLightMath.Outcode`) and
0.4 s the larger lighting lump. The 3x3 sample packs in 1.95 s against
1.52 s, 578 KB against 561 KB, its level links in 0.65 s to 167 KB against
125 KB; every level of the 3x3 and transit samples links lit and passes
`ssmap check` (its one warning, no cubemap sample), in both emission modes
for the transit run. `ssmap all` on 2fort and the sandbox writes the same
maps as before.

**PR 13 landed** (area portals and areas, then the 3D skybox, in that
order on one branch).
`ssmap room` describes a room whose compile has area portals in one `APRT`
section (`RoomAreaPortals`, with the 1.1 framing: codec byte, decoded
length, revision; codec none): the room's area count, listing count and
clip vertex count (its three lumps stay in the container byte for byte and
are checked against what vbsp writes, reserved area 0 and listing 0 empty,
each area's run after the last, every listing naming an area, portal,
plane and vertices the room has), the portal numbers its compile gave out
(its `func_areaportal` and `func_areaportalwindow` entities, 1 to k once
each), then the rotation count (4) and per turn every clip vertex turned.
A room without area portals gets no section, so a library without them
packs to the same bytes. The link plans the level's areas after the bases
(`LevelLinker.PlanAreas`): every placement's own areas start apart; at a
joint, every area the facing clusters of one side lie in joins every area
the other side's lie in (a union-find whose lower node is the root, so the
result does not depend on the order joints are met); the level's areas are
numbered from 1 in the order their first member appears, placement by
placement in link order. That is today's collapse generalised: rooms
without a portal are one area each, and a level of them one area. A
placement's portal numbers follow every earlier placement's
(`RoomPlan.PortalBase`), its entities' `portalnumber` rebased the same way
(`TranslateEntity`), as the flattened level's loader numbers them in entity
order. Leaves, nodes (a node wholly in one area; a mixed one stays -1),
occluders and the carved doorway leaves take their level areas. The
`Areas`, `AreaPortals` and `ClipPortalVerts` lumps are written from the
plan (`LevelLinker.WriteAreas`): area by area, each area's listings in the
order of their linked numbers (vbsp lists a map's portals area by area and
under each in entity order), a listing's plane the room's through the
shared table (`PlaneRef`, oriented into the listing's own area as vbsp
orients it), its outline a copy of the room's clip vertices at the
placement's turn plus its translation. A level whose rooms have no area
portal (and whose library asks for no door portals) carries the one open
area as before, the first room's two lumps byte for byte, and no clip
vertex lump; no digest moved. The pack format version stays 4: `APRT` is a
tag an older build skips, and that build refuses a room with area portals
by its lumps; a pack written before this PR has no `APRT` for such a room,
and this build refuses it with `room {room} has {k} area portals but no
area portal data from its compile (a pack written before the link carried
area portals, or a room built without ssmap room); recompile the library
with ssmap room.`, which also guarantees every linked portal was held to
the rules below when its room was packed. The old refusal (`... a linkable
room has no area portal ...`) is gone, a fact asserting so.

- **The relation to door visibility.** Area portals do not cut the linked
  PVS: vvis sees through an area portal (the engine closes it at runtime),
  so a room's own rows already look through its portals, and the door
  flows (Q3) are unchanged; a fact holds a line through both a doorway and
  a portal to be kept, as vvis on the flattened level keeps it. The door
  portals below stand on the doorway rectangles the flows look through, so
  the doorway the PVS says a line crosses is the one a portal can close.
- **O10 as recommended**: joints open and areas unioned; author portals
  only by default; door portals opt-in per kit. A library's kit is the
  library's, so the switch is a library key, `rooms_door_portals 1`
  (`RoomLibraryOptions.DoorPortals`, in the `LOPT` section, written only
  when set, a library key the split and the flatten keep out of the
  rooms). With it, every joint gets a door portal (`LevelDoorPortals`), no
  joint joins areas, and each room's areas stay its own: the portal's
  plane is the cell face (the top tree's split between the two cells, so
  it is found in the shared table), its outline the doorway's rectangle on
  it in the order vbsp's hull walk leaves it (`AreaPortalGeometry.Hull`,
  extracted from the clip geometry), numbered after every placement's own
  portals in plan order (each joint once, from its earlier placement, in
  that placement's joint order), its `func_areaportal` written after every
  other entity with `portalnumber` and, when the joint's kept socket
  furniture (the furniture rule's side) is a named `func_door` or
  `func_door_rotating` without `room_needs`, `target` naming it resolved
  for its placement, else `StartOpen 1`. The flatten writes the same
  entity at the end of its entities with one brush of `tools/toolsareaportal`
  filling the doorway a unit either side of the cell face. Each costs one
  entity: the link's budget counts one `func_areaportal` per joint with the
  level's own entities, and `ssmap layout -entity-budget` charges each room
  half its sockets, rounded up, which never under-counts. A level of more
  than 255 rooms with door portals passes `MAX_MAP_AREAS` and is refused.
- **Refusals.** At pack time (the split, so the pack and the flatten alike,
  and a room compile given a VMF): 15.4's socket row, `room {room}:
  func_areaportal {id} lies in socket "{socket}"'s plug box.` (the class
  written as the entity's, so a window says `func_areaportalwindow`), for
  a portal brush that overlaps a plug box beyond the cell tolerance
  (touching its face is allowed: a portal dividing a room from its
  neighbour stands just inside the doorway); two refusals the table does
  not list, `room {room}: func_areaportal {id} has room_socket; an area
  portal is built into its room's world and cannot be socket furniture.`
  and `room {room}: entity {id} (func_areaportal) has room_needs, but an
  area portal is built into its room's compile and cannot be dropped.`
  (vbsp moves a portal's brush into the world, so the link could not drop
  it, and dropping it would renumber every later portal). At link: the
  loader's caps, `MAX_MAP_AREAS` (256 entries, area 0 included) naming the
  placement whose area crossed it, `MAX_MAP_AREAPORTALS` (1024 listings),
  and the `ushort` fields of a listing's key and first clip vertex (vbsp's
  own 128,000 vertex cap is wider than the field).
- **A portal the level joins around.** When the rooms around a portal join
  its two sides into one area (a ring), vbsp, meeting that in the
  flattened level, warns that the portal does not touch two areas and
  lists nothing for it, keeping the entity and its number; the link does
  the same and prints `room {room} at cell ({x}, {y}): area portal {n} has
  one area on both sides once the level joins the rooms around it; the
  level keeps its entity but lists no portal for it.` (`ssmap link`,
  before the headroom line), rather than refusing a layout the generator
  may well make.
- **Storage** is four turns, the 1.1 default for data the link would turn
  point by point: a portal's clip vertices are a handful of points, so
  either storage is far below what the link's timings resolve; a count of
  1 is read and links to the same bytes (a fact).
- **Known differences, not refused.** Which face of a portal's brush vbsp
  puts the portal on, and which side's area the brush's own leaf takes,
  follow the order its area flood meets the portal's two sides, which a
  room's compile and the flattened level's need not share: the linked and
  flattened portals have the same outline and normal and lie on one face
  or the other of the brush, and the facts compare the partition of open
  space outside portal brushes and the portals by outline, holding each
  map's to a face of the brush (for door portals, the link's on the cell
  face and the flattened one a unit off it). The areas themselves are
  numbered differently in the two maps (the link by placement, vbsp by its
  flood), the same partition up to renaming (4.11's equivalence).

Measured equivalence: the harness split room (an inner wall whose doorway
a portal fills, sockets east and west) between two hubs at the four turns,
two of them in a row with portal and window classes, a ring of hubs around
it, and door portals on the same levels and on a hub with a door as socket
furniture, each linked and flattened and compiled whole: the same partition
into areas of every open sample point, the same portals by outline between
the same areas, the same portal numbers and portal entities; a lit library
with a portal links with the unlit link's areas and portals, and passes
`ssmap check`; a level with portals through the CLI (`ssmap room`, `ssmap
link`, `ssmap check`) has no error. Area portals cost what 15.6 lists: one
entity each (`func_areaportal`, a default `edict` class), counted from the
compiled lump, so the budget needed no change for author portals. Not done
here: the 3x3 sample did not grow a portal room (the harness levels carry
the facts at every turn, and the sample's digests show a level without
portals links as before), the stress library has none, and `ssmap rooms`
does not list portals.

**The 3D skybox (PR 13, second half).** A library marks its skybox room
with an `info_room_skybox` point entity at the cell's low corner, with a
`name` like a room's (O11 as recommended); the cell is the library's grid,
and the split (`RoomLibraryVmf.SplitLibrary`) owns the brushes and entities
in it as a room's and sets the room apart (`RoomLibrarySplit.Skybox`, not
one of the rooms, so no level and no `ssmap layout` places it). Its rules,
each refused by the split, so by the pack and the flatten: one skybox at
most (`the library has {k} info_room_skybox entities; a library has one
skybox room at most.`), exactly one `sky_camera` (`the skybox room
"{room}" has {k} sky_camera entities; the engine draws a skybox from
exactly one.`), no socket (`the skybox room "{room}" has a door plug on its
{wall} wall; the skybox is never joined, so it has no sockets.`), and no
`room_needs` or `room_socket` (`room {room}: entity {id} ({class}) has
{key}, but the skybox room has no neighbours and no sockets.`); the library
singletons' rules hold for it as for a room, and a `sky_camera` anywhere
else is refused as PR 4 refused it. `ssmap room` compiles, lights and packs
it after the rooms like any room, and names it in a `SKYB` library section
(`RoomLibrarySkybox`: codec byte, length, revision, the name), written only
for a library with a skybox; `ssmap link` reads the section and loads the
skybox with the rooms, at its one turn. The pack format version stays 4:
an older build skips the tag and links a level without its skybox, as it
always did.

- **Placement: below the grid** (O11), one cell under the level's lowest
  column and row (`LevelLinker.SkyboxPlacement`), unturned (4.12: the
  skybox is never turned): a placement's `Level` of -1, which only
  `RoomTransform` reads, as a whole number of cells along z (a height on
  the grid's level keeps its bits, a negative zero included). The link
  carries the skybox after every room of the level, never in the layout:
  the grid's logic (joints, neighbours, reachability, furniture,
  transitions, names, the door flows, the door portals) reads the level's
  own placements (`GridCells`, `OnGrid`), since the skybox shares its
  cell's column and row. The top tree gains a root at the grid's floor
  (z = 0): above it the grid's tree as before, below it the skybox room's
  own tree, whose sealed compile puts everything outside its shell in
  solid leaves, as the grid's single-cell nodes rely on above and below a
  cell; the shared solid leaf reaches a cell further down. The flatten
  writes the skybox's brushes after every room's and its entities after
  every room's, moved by the same placement.
- **Its own area.** The skybox has no joint, so its areas join nothing
  (the level's areas are planned whenever a library has a skybox) and it
  is an area of its own, numbered after the rooms', as vbsp's flood makes
  a sealed skybox; its clusters see only one another (its own vvis rows).
  The level's `world_mins` and `world_maxs` leave it out, as vbsp leaves a
  3D skybox out of them (`EntityStage.ComputeBoundsNoSkybox`), and model
  0's bounds keep it, as vbsp's do.
- **Its entities** are moved to it, never through the naming resolver (it
  stands in no cell of the grid, so a room-local name in it is written as
  authored, in the link and the flatten alike), and written after every
  room's and before the door portals', where the flatten writes them. They
  count once per level with the library's own entities, in the link's
  budget and in `ssmap layout -entity-budget`: one `sky_camera` per level
  (6.8), plus whatever else the skybox holds.
- **Lighting.** Under a lit library the skybox is baked like any room and
  its leaves take the link's sky pass (PR 9), which flags them as seeing
  3D sky (their own shell). 4.12's "every room's bake includes the skybox
  geometry for the sky-ray recast" is not done: each room is baked sealed
  and alone at pack time, and a recast into the skybox would need the
  skybox's geometry in every room's vrad run, which is the lighting
  work's (PR 10, with the door terms). Until then a room's sky light is
  what an empty skybox would give; a skybox whose own geometry would
  shadow a room's sky is a known difference from the flattened level's
  full compile.

Measured equivalence: the harness's hub and other room with the skybox (a
3D sky shell, a block, a prop and its camera) at the four turns, with an
area portal room and door portals, and lit with a sun and a sky ceiling:
the linked and flattened maps put every sample point of the rooms' cells
and the skybox's in the same open space and areas (one to one), carry the
same `sky_camera`, props and world bounds, and the linked map passes
`ssmap check`; through the CLI, `ssmap room` packs the skybox and `ssmap
link` places it from the pack alone. Not done here: the 3x3 sample did not
grow a skybox (its digests show a library without one links as before),
`ssmap rooms` does not list it, and the stress library has none.

Measured against the base (PR 9's head), the whole `ssmap` process on a busy
4-core machine: the 3x3 and transit samples pack, link (both modes for the
transit run) and pass `ssmap check` with its one warning (no cubemap
sample), their linked maps the same bytes but for the pack and level ids
stamped in the worldspawn (the pack id records the build); the 256-room
stress library's 33 x 33 level links in 1.5 to 1.7 s against 1.6 to 1.7 s,
to the same bytes, and passes `ssmap check`; `ssmap all` on 2fort and the
sandbox writes the same maps as before. The link spends nothing on areas
for a level without area portals, door portals or a skybox.

**PR 17 landed** (multiple libraries per level, 17.2 to 17.5). A level
file may name its libraries with `libraries:` (a mapping of key to VMF, in
the order that decides the singletons) and give `aliases:`; `LevelYaml`
reads both with 17.3's texts and writes them back where 17.2 puts them.
Cells resolve where the rooms are known (`LevelLibraries.Resolve`: link,
flatten and `ssmap rooms` over a level) to qualified names, `key.room`,
which is the name the level places a room by from then on: the linker, the
flatten and every message see `base.corner` and `caves.corner` as two
rooms. A `library:` level keeps bare names and links, flattens and writes
exactly as before; only its aliases, when it has any, are replaced by the
rooms they name. `LevelLibraries.Check` holds the placed libraries to one
cell size, one kit and one navigation grid (D21, D25) and gives the
singleton, option, skybox, navigation and worldspawn lines of 17.3 in
that order per library (D20, D24, O25 as its default); the link reads it
from each library's pack and a room it places, the flatten from the VMFs,
and both print the same lines before their others. `LevelLibraries.Combine`
puts every loaded room of every library into one `RoomLibrary` under its
qualified name, with its library's index and name keys
(`RoomLibrary.SourceOf`, `NameKeysOf`), and the first library's entities,
options and skybox; the linker then runs as for one library.

- **Two rooms of one name.** Everything the link keeps per room is now
  keyed by the name the level places it by, not the name it was compiled
  under: the pak list, the static prop lighting files, the cubemap
  placements, the light blocks and ambient runs, the entity counts, the
  plug censuses and the naming tables. The pak merge renames a room's
  default cubemaps by its compile name (`LevelPakFiles.Merge`'s
  `compileNames`). For a level of one library the two names are the same,
  so no byte moved; a fact holds a level of two libraries, lit and unlit,
  byte for byte to the level of one library holding both room sets under
  other names. The `logic_room`'s `room` key (with `-mod-entities`) carries
  the qualified name, in link and flatten alike.
- **Worldspawn and sun.** The worldspawn is the first placed room's of the
  earliest listed library the level places; only rooms of that library are
  held to it (`RequireSameWorld`), another library's differing key is the
  worldspawn line. The save counter is the first library's in both maps.
  The level's sun world lights and sky are the first library's first lit
  placement's bake (`PlanLighting`), else the first lit placement's as
  before. PR 9's lighting rule is unchanged across libraries. PR 17 is
  stacked on PR 10, so D26 is in refuse mode: a sunlit room baked under a
  sun the level drops is refused by the link, naming the library's first
  such room in link order (the flatten bakes nothing, so it has no such
  refusal). The mode is one switch, `LevelLibraries.RefusesDroppedSun`,
  and the warning a build without the door light would give stays written
  (`SunLine`) and held by a fact.
- **Door light across libraries.** The door terms (PR 10) are planned per
  placement from each room's own stored door light, so a joint between two
  libraries' rooms carries light both ways as any joint does; the door
  light's per-room caches (face cells, leaves) are keyed by the placed
  name like everything else. A fact links base's hub to caves' other at two
  turns, each library door-lit on its own: the same bytes as the
  one-library level of those rooms, and within 9.8's door-light
  tolerances against vrad of the linked level, where the base alone is not.
- **Navigation.** `LevelNavLinker.Link` takes which library each room
  comes from: rooms of one library are held to one another's settings as
  before, rooms of different libraries only to one grid, and the header
  takes the first placed library's settings. The level's pack id is
  `RoomCompileIds.LevelPackId` over every key and pack id in order; for one
  pack it is that pack's id, so a `library:` level's ids do not move.
- **Packs.** Each key's pack is `<library>.roompack` beside its VMF unless
  `-rooms <key>=<pack>` names another. Until PR 18 no pack has namespaces,
  so a plain `-rooms <pack>` is taken only for a level that lists one
  library, and refused with 17.10's plain-pack text otherwise. A key the
  level does not list is refused: `-rooms names library {key}, which the
  level does not list; its libraries are {keys}.`; a missing pack names its
  key: `there is no room pack {pack} for library {key}; compile the
  library with ssmap room, or point -rooms {key}= at its pack`. Every pack
  stream is closed before the link starts. The pack format does not change.
- **`ssmap rooms <level.yaml>`** lists every library of the level under a
  `library {key}: {path}` line (a `library:` level's under `library:
  {path}`) and then prints the level's library lines as warnings, or fails
  with a compatibility refusal; `-rooms <key>=<pack>` finds a key's pack.
- **Decisions taken where this section left a detail.** An alias's value
  resolves as a qualified or bare name, never as another alias, so the
  order aliases are written in never matters. Every alias is resolved and
  every library checked whether or not a cell uses it. The same-file check
  compares the paths as the host resolves them and names the resolved
  path. `mapversion` gets no option line (the table's row says why). The
  compatibility check runs over the libraries the level places (17.5).

Measured against the base (main with the section 17 design): `ssmap all`
on 2fort and the sandbox writes the same maps; the stress library's 33 x 33
level links to the same bytes (1.5 to 1.7 s, as before) and passes `ssmap
check`; the 3x3 and transit samples' packs differ only in the build
identity the pack id and each room container record (every other section
the same bytes), and every level of both, linked by this build from the
base's packs, is the same bytes as the base's link (both modes for the
transit run).

Reasoning: correctness first (cheap, each a failing fact today); then the
budget and the naming and logic feature, because the owner ranks entity
count first and every later feature is measured against it; then the other
text-only work (singletons, pak); then features by value against risk.
Brush entities before lighting because the base bake must include them.
Transition rooms come right after brush entities: they make a run of
levels playable, and their stock fallback needs brush models.
Navigation (section 10) is not scheduled: it waits for the AI design; only
its points-of-interest store (PR 2) is independent of it.
Areas after Q3. Water and displacements late: their cross-room cases are the
hardest and their refusals are safe meanwhile.

---

## 14. Owner decisions

### Decided

| # | Decision |
| --- | --- |
| D1 | As much as possible at pack time, stored in the `.roompack`; link needs nothing but the pack, no game files. Per-rotation precompute (×4) wherever it makes the link faster (D16). |
| D2 | Lighting is option C as in section 9: base bake per room ×4 rotations; doorway capture into a black absorbing box per rotation (direct visibility grid per light, bounce as directional radiance per door sample per style); precomputed door response per door (coarse basis to lightmaps, leaf ambient, prop lighting, pruned); at link each joined door adds neighbour capture × this room's response, both ways; no ray tracing at link. Replaces the link-time relight near doors. |
| D3 | Reach is the adjacent room only. All rooms share one sun; `light_environment` and sky settings are library-wide, and a room that disagrees is refused when the pack is built. |
| D4 | Local names use the placeholder `cxry_` with `±1` offsets; names without it are global and untouched; no `@` rule; resolved at link to `c<col>r<row>_`, 0-based from the south-west. |
| D5 | Neighbour offsets are in the room's own frame and turn with its placement (5.3). |
| D6 | All three missing-neighbour mechanisms are built: (a) warn and drop by default, (b) neighbour-flag `logic_branch`es, (c) `room_needs` inclusion, including brush entities (5.8). |
| D7 | Entity count is a top priority (the 2048-edict cap). Injected entities are opt-in; (b) flags are injected only when referenced; the linker injects nothing else by default; link-time mechanisms are preferred to runtime ones; the link budget is `cap − reserve`, with a configurable reserve and a headroom report (section 6). |
| D8 | New combined game-side entity classes are allowed; their contract is section 7, and every one has a stock fallback. |
| D9 | The target game is the owner's mod, **Source Sharp** (written in C#). This repository defines the entities the rooms feature needs (section 7); the mod implements them. |
| D10 | A command-line flag enables the mod's classes (`-mod-entities`, 7.1); without it the linker emits stock entities only. `--flatten` honours it identically, and the choice is recorded in the linked worldspawn. |
| D11 | Navigation is required, for a new AI system not yet designed, and must be **3D** (agents fly and climb). Not the stock `.nav` or node graph. Blocked on the AI design (section 10). |
| D12 | Points of interest (cover, vantage, spawn, patrol, interaction) belong to the navigation data: `info_poi`-style entities are compiled into the room's navigation section by `ssmap room` and stripped, costing zero runtime entities (10.6). |
| D13 | Transition rooms: exactly one up room and one down room per level (no branching), marked by `room_role` on `info_room`; the level YAML may switch either off for the top or bottom level (section 11). |
| D14 | Transitions are resolved at `ssmap link`: levels are linked ahead of time and each transition's destination map name, from the level YAML, is written into the transition entity or `trigger_changelevel`; the mod never invokes the linker at transition time. |
| D15 | A fresh start spawns the player at the up room's arrival point, facing its yaw: from the navigation data with `-mod-entities`, one emitted `info_player_start` in the stock fallback, with the rooms' own player starts stripped (replaces O12). |
| D16 | Link speed decides storage: precompute and store per rotation (×4) whenever that makes the link faster, once only where the bytes do not change with rotation or the turn is measurably free next to reading the data; larger files are an accepted cost. Base lighting and door capture are ×4 when sun or sky light reaches the room, once otherwise; the door response once unless ×4 measures faster (1.1). |
| D17 | Pack sections and navigation files carry a codec byte (none, Deflate, Brotli; built into .NET). The default is none; a codec only where it measurably beats raw reads at link with a warm page cache (by the mod's load time for the navigation file). Deterministic, with pinned-byte facts on every OS and an in-repo compressor as the fallback (1.1). |
| D18 | Navigation data lives in separate files unless trivially small: `ssmap room` writes `<library>.roomnav` beside the `.roompack` (same container conventions, a header binding it to the pack; a mismatched pair is refused), and `ssmap link` writes `<map>.nav3d` as a sidecar next to the `.bsp`, not in the pakfile. Back into the pack only under a size threshold the navigation PR states (10.4). |
| D19 | (2026-09-29) A level file may name several source VMFs, with aliases: `libraries:` maps a key to each VMF, `aliases:` maps a short name to a room, and a cell holds an alias, `lib.room`, or a bare room name. `library: x.vmf` stays valid. A bare name is allowed only when exactly one library has it, and is refused otherwise with the candidates listed; an alias may not shadow a room name (17.2). |
| D20 | (2026-09-29) Singletons (the sun, the environment controllers, the entity reserve and the other library options) come from the first listed VMF. A duplicate in another VMF is a warning, not an error, and is dropped (17.4). |
| D21 | (2026-09-29) Libraries in one level must agree on the cell size and the socket (door) kit, refused otherwise naming both. Rooms may have different heights, so the room height is not part of the check (17.5). |
| D22 | (2026-09-29) `ssmap layout` draws from every listed library and is height-aware: it can make large open areas grouped together, connected to smaller rooms or hallways (17.9). |
| D23 | (2026-09-29) A command combines many VMFs into one `.roompack`, with room names namespaced per library (17.10). |
| D24 | (2026-09-29, was O24) Equal duplicate singletons across libraries warn with one summary line per library, not one line per entity (17.4). |
| D25 | (2026-09-29, was O26) Navigation settings across libraries: a library whose navigation voxel grid differs from the first's is refused; different step and jump heights, costs and agent presets only warn, and the level takes the first library's (17.5). |
| D26 | (2026-09-29, was O27) A sunlit room (stored at four turns) baked under a library sun the level drops is refused once the door light (PR 10) lands; until then the link and the flatten warn. `ssmap roompack` compiles every namespace under the first library's worldspawn and sun, so a combined pack never meets the case (17.4). |
| D27 | (2026-09-29, was O31) Raised or sunken floors and stacked storeys are not planned; storeys are authored inside a tall room (17.6). |
| D28 | (2026-09-29, was O32) The combine verb is `ssmap roompack`, with `ssmap room -namespace` for one library (17.10). |

### Open, with recommended defaults

| # | Question | Recommended default |
| --- | --- | --- |
| O1 | Neighbour-flag diagonals and joined variants. | Offer both (`cxry_has_northeast`, `cxry_joined_east`), opt-in, so free unless used. |
| O2 | Placeholder case variants (`CXRY_`) and other near misses of the reserved family. | Refuse at pack time anything matching `SUSPECT` but not `LOCAL`, and any global name matching `RESOLVED`, case-insensitively (5.2). |
| O3 | Which keys get placeholders resolved. | Built-in name-key table, every output target, output parameters wholly a placeholder, and any key whose whole value is a placeholder; the library may extend the table. |
| O4 | A global `targetname` in a room placed more than once. | Warn at link, naming the cells. |
| O5 | Socket furniture. | `room_socket` key; at a joint keep the earlier room's (link order), `socket_priority` overrides; drop at a cap. |
| O6 | Props whose hull leaves the cell. | Refuse at pack time, except socket furniture inside the plug box. |
| O7 | Water touching a socket. | Refuse first; kit water levels later. |
| O8 | Displacements meeting at a joint. | Refuse a displacement edge on a plug box. |
| O9 | Cubemap assignment near doors. | Each room's faces use its own cubemaps; the map name is fixed at link. |
| O10 | Area portals at joints. | Joints open, areas unioned; author portals only; door portals opt-in per kit (1 entity each). |
| O11 | 3D skybox. | A library skybox room (`info_room_skybox`), placed below the grid, its own area. |
| O12 | (Replaced by D15.) | |
| O13 | Texel-lit static props. | Refuse until vrad supports them. |
| O14 | Response storage. | Let the prototype choose; one response set per door if the rotations agree. |
| O15 | Brush entities with non-zero `angles`. | Refuse unless the class is in a known-direction table. |
| O16 | Where the shared C# contract types live (7.5). | A new dependency-free assembly, `SourceSharp.RoomContracts`, held to the library rules. |
| O17 | Entity reserve. | 512 (budget 1536), library key `rooms_entity_reserve`, link option `-entity-reserve`; the mod should measure its peak and set it. |
| O18 | Stripping unnamed lights after baking. | Opt-in until checked in game; then default on. |
| O20 | Level YAML key names for destinations. | `up_map` and `down_map`. |
| O21 | The spawn of a level with `up: none`. | A `spawn` cell key; by default the `spawn` POIs of the room farthest in doors from the down room; refuse if none. |
| O22 | `ssmap layout -sequence K`: a chained run of levels from consecutive seeds. | Yes, optional. |
| O23 | Multiplayer spawn points. | `spawn` POIs around the up arrival; `spawn_count: K` on the level refuses fewer. |
| O19 | Relay folding. | On by default; a library option turns it off (same-tick event order can change). |
| O24 | (Decided: D24.) | |
| O25 | A singleton only a later library has (17.4). (2026-09-29: the owner is not sure; the default below is built, and the point stays open for the owner to revisit.) | Dropped with a warning; the first library supplies every singleton. |
| O26 | (Decided: D25.) | |
| O27 | (Decided: D26.) | |
| O28 | Neighbour names in rooms larger than one cell (17.8). | Refused for now; `joined_<socket>` works; a socket-based grammar later. |
| O29 | Level-file mark for a covered cell (17.8). | `+`, the room written in its south-west cell. |
| O30 | Larger role rooms in `ssmap layout` (17.8). | Linked when hand-placed; the generator offers one-cell role rooms only. |
| O31 | (Decided: D27.) | |
| O32 | (Decided: D28.) | |
| O33 | Tall one-cell rooms outside groups (17.9). | Only in groups, under `-large`. |

---

## 15. Testing

One place for what every feature and rule must be tested with. Each PR in
section 13 names the parts of this section it lands with. The repository's
rules apply throughout (CLAUDE.md): every logic path gets a fact, a fix
comes with a fact that fails without it, facts live in the folder that
mirrors the code (`src/SourceSharp.Tests/MapTools/Rooms/` for the room
pipeline), and a failing fact is never skipped to get green.

### 15.1 Kinds of fact and the axes every one runs on

- **Unit facts** test a pure function without a compile: the name grammar
  and resolver, the rotation table, the fold, the key transforms, budget
  arithmetic, YAML parsing, layout placement.
- **Linker facts** build small rooms in memory (`RoomHarness`), compile and
  link them, and read the linked lumps directly, in the style of
  `LevelLinkerRelocationTests` and `LevelLinkerRefusalTests`.
- **End-to-end facts** take a sample library through `ssmap room`, `ssmap
  link` and `ssmap link --flatten` plus the whole-map compile, and compare
  the two maps in game-observable terms, in the style of
  `Rooms3x3EquivalenceTests` over `Rooms3x3Fixture`.

The axes, applied wherever they are relevant:

- **Rotation:** every placement-dependent fact is a theory over rotations
  0, 90, 180 and 270.
- **Mode:** every fact about emitted entities is a theory over
  `-mod-entities` off and on; link and flatten must agree in each mode.
- **Determinism** (15.5): the same bytes at any thread count and on every
  run, for packs and for linked maps.
- **Entity budget** (15.6): every feature's end-to-end fact also asserts the
  linked entity and edict counts equal the pack's prediction.
- **Messages:** every refusal and warning is asserted by its exact text
  (15.4), not by type alone.

### 15.2 Matrix

"Unit / Linker / E2E" name the facts to write; "Equivalence" is what link
and flatten must agree on, with the accepted difference after the semicolon;
R = rotations, M = both modes, D = determinism, B = budget counts.

| Feature or rule | Unit | Linker | E2E fixture | Equivalence | R | M | D | B |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Correctness fixes (15.3) | key transforms, side-id remap | each finding red first | 3x3 plus a ladder, occluders, overlays | entities, sides | yes | | yes | |
| Naming and neighbours (5) | grammar (5.2), resolver, rotation table, (a)(b)(c) rules, fold | resolved lumps, dropped models | naming level (15.7) | resolved entities byte for byte; effective I/O against the unfolded monolithic map | yes | yes | yes | yes |
| Entity budget (6) | class table, totals, reserve, headroom text | `CheckCapacity` refusal and warning | stress library | same counts both paths | | yes | yes | yes |
| Mod entity contract (7) | FGD text against the C# constants | `logic_room`, `logic_level_transition` emission and fallback | naming level, transit set | same entities per mode | yes | yes | yes | yes |
| Singletons (8) | agreement rules | one sun, one fog, `water_lod_control` dedupe | 3x3 with a library sun | entities | yes | | yes | yes |
| Point entities (4.2) | key transform table | moved keys | 3x3 | every key after resolution | yes | yes | yes | yes |
| Brush entities (4.1) | origin-class transforms, texinfo split | models, `*N`, subtrees, collision records, omission | `cross` door, trigger | models by class and bounds, per-model traces and convexes; model numbering | yes | | yes | yes |
| Static props (4.3) | record transform, hull leaf walk | dictionary merge, leaf lists, `.vhv` names | `tee` prop | records; leaf lists cover the hull's leaves | yes | | yes | yes (0) |
| Detail props (4.4) | transform, re-sort | leaf rebase, `dplt` runs | `end` floor | per-room distribution; the random draw differs | yes | | yes | yes (0) |
| Displacements (4.5) | vector and start transform | index rebase, `PhysDisp` | `corner` patch | surfaces, collision, traces | yes | | yes | yes (0) |
| Water (4.6) | min-distance recompute | water data, fog ids, fluids | `hall` pool | contents, surface z, fluids; doorway water only with water sockets | yes | | yes | yes |
| Overlays (4.9) | basis transform, packed `BasisU` | face and id rebase, accessors | `tee` overlay | union polygons per overlay; face counts differ | yes | | yes | yes |
| Cubemaps (4.10) | renaming | samples, patched VMTs, texdata names | `hall` cubemap | samples; in-room assignment; nearest-cubemap differences near doors | yes | | yes | yes (0) |
| Area portals (4.11) | area union | areas, portals, `portalnumber` | portal room | area partition up to renaming | yes | | yes | yes |
| Occluders (4.7) | | `occludernumber` rebase | `end` occluder | occluders and keys | yes | | yes | yes |
| Packed files (4.13) | merge rules | dedupe, renames, conflict refusal | real-content room set | file set after renaming | | | yes | yes (0) |
| Sky (4.12) | pass two | leaf flags; skybox area | `end` sky opening, skybox room | leaf sky flags | yes | | yes | yes |
| Transitions and spawn (11) | YAML keys, rule, layout second stream, landmark names | emission per mode, hallway fold, spawn | transit set (15.7) | entities per mode, spawn position and yaw | yes | yes | yes | yes |
| Lighting (9) | sums, style renumber | base per rotation, door terms | 3x3 lit, stress | tolerances (9.8); byte equality not expected | yes | | yes | |
| Navigation and POIs (10) | POI transform; `.roomnav` and `.nav3d` containers | POI store and strip into `.roomnav`; the pack binding | transit set | POI positions and facings in `.nav3d`; the rest blocked on the AI design | yes | | yes | yes (0) |
| Multiple libraries (17.2 to 17.5) | key and alias parsing, resolution order, dotted-name guard, compatibility, singleton rule, every 17.3 text | two packs linked; singletons and options from the first; worldspawn warning; lookup per key | multi-library sample (17.12) | the level linked from two libraries equals the same level from one library holding both room sets, less the warned singletons | yes | yes | yes | yes |
| Combined packs (17.10) | `NSPC` round trip, namespaced names and `MapBase` | combined and separate packs; `-rooms` forms; `-only` and reuse copy sections byte for byte | multi-library sample | a combined pack and namespaced separate packs link to the same map | | | yes | yes |
| Room heights (17.6) | door box against today's, height rules, extent limit | tall rooms at every rotation beside short ones: node and solid-leaf bounds, props, nav columns | tall variants (17.12) | lumps, traces and `.nav3d` against the flattened compile; cube-only levels byte-identical | yes | | yes | yes |
| Multi-cell rooms (17.8) | footprint transform, `+` cells, socket offsets and names, anchor | per-cell subtree routing, block-node omission, joints along edges, capacity | 2 x 2 and 3 x 1 rooms (17.12) | traces at every lattice point, vis a superset, entities, `.nav3d` | yes | yes | yes | yes |
| Height-aware generator (17.9) | streams, groups, filters, the shortest spelling | | seeded multi-library levels | YAML byte-identical to today's for one library without large rooms, and for `-large 0` | | | yes | yes |

### 15.3 Correctness fixes, red first

Each fact is written first, fails on the code as it is today (the expected
failure is stated), and passes with its fix. Findings 7 to 9 are fixed by
later PRs; their facts land with those PRs.

| # | Fact | Fails today because |
| --- | --- | --- |
| 1 | A room with a `func_ladder`, linked at each rotation: `info_ladder`'s `mins.*` / `maxs.*` equal the flattened compile's. | `LevelLinker.MoveEntity` leaves them room-local. |
| 2 | Two rooms with a `func_occluder` each: the second room's `occludernumber` is 1 and names its own occluder in the linked lump. | The key stays 0. |
| 3 | A flattened level with an `env_cubemap`, an `info_overlay` and an `info_no_dynamic_shadow` naming sides: every `sides` id names the moved side. | `LevelFlattener.Renumber` renumbers side ids and not the lists. |
| 4 | A library room with an `info_overlay`, split and flattened: `BasisOrigin`, `BasisU`, `BasisV`, `BasisNormal` are moved and turned. | `VmfPlacement.MoveEntity` moves `origin` only. |
| 5 | A room with a `light_environment` at rotation 90: the sun's `angles` are unchanged. | Its yaw is turned. |
| 6 | A library with a `light_environment` in the gaps: the pack's library section holds it. | `RoomLibraryVmf.Split` drops it silently. |
| 7 | A room placed twice with a `cxry_` name: two distinct resolved names (PR 3). | No name fixup exists. |
| 8 | Two rooms with a named switchable light each: two distinct styles (PR 3 for the names, PR 9 for the lightmaps). | Both get style 32. |
| 9 | A brush entity with non-zero `angles` of an unknown class: refused by split and flatten with the 15.4 message. | It is turned silently. |

### 15.4 Negative cases and their messages

Every refusal (R) and warning (W) below is asserted by its exact text; the
braces are filled from the case. Existing refusals keep their current text
(`LevelLinker.Limit`, `LoaderLimit`, `PlanRoom`'s lump and model messages)
until the feature that lifts them lands, and a fact asserts each is gone
then.

| Rule | Kind | Message |
| --- | --- | --- |
| 5.2 rule 3 | R | `room {room}: entity {id} ({class}) key "{key}": "{value}" is a malformed room-local name; a local name starts with cxry_, cx+1ry_, cx-1ry_, cxry+1_, cxry-1_ or a diagonal such as cx+1ry-1_, in lower case, followed by the name.` |
| 5.2 rule 2 | R | `room {room}: entity {id} ({class}) key "{key}": the global name "{value}" begins like a room-local or resolved name (c<column>r<row>_); rename it.` |
| 5.2 placement | W | `room {room}: entity {id} ({class}) key "{key}": "{value}" contains cxry after its start; it is a global name, since the placeholder is only read at the start of a name.` |
| 5.2 linker-owned | R | `room {room}: entity {id} is a {class} named {value}; that name belongs to a {expected class}.` |
| 5.10 undefined | W | `room {room}: entity {id} ({class}) key "{key}" names {value}, which no entity of the room defines.` |
| 5.8 (a) | W | `room {room} at cell ({x}, {y}): entity {name} ({class}) key "{key}" names {value}, but cell ({nx}, {ny}) {holds no room / is off the grid}; the {output was removed / key was cleared}.` |
| 5.8 (c) direction | R | `room {room}: entity {id} ({class}) room_needs "{value}": unknown direction "{dir}"; use east, west, north, south, a diagonal, or joined_ with a side, optionally negated with !.` |
| 5.8 (c) light | R | `room {room}: entity {id} ({class}) has room_needs, but a light's contribution is in the room's baked lighting and cannot be dropped.` |
| 5.8 (c) shadow | R | `room {room}: prop_static {id} has room_needs and casts shadows; set disableshadows or remove room_needs.` |
| 5.6 length | R | `room {room} at cell ({x}, {y}): entity {name} ({class}) key "{key}" resolves to {n} bytes; the engine reads at most 1023.` |
| 5.6 duplicate | W | `the global name "{value}" is defined by {k} placements of room {room}, at cells {cells}.` |
| 6.7 headroom | info | `map entities {n} / budget {b} (reserve {r}, cap {c}); {m} entities in the entity list` |
| 6.7 reserve | W | `map entities {n} / budget {b} (reserve {r}, cap {c}): the level uses {n − b} of the reserve; most expensive rooms: {room} x{k} = {e}, ...` |
| 6.7 cap | R | `map entities {n} exceed the cap of {c} edicts; most expensive rooms: {room} x{k} = {e}, ...` |
| 8 sun | R | `room {room}: its light_environment differs from the library's ({key}: "{a}" against "{b}"); the sun is library-wide.` |
| 8 sky camera | R | `room {room}: sky_camera is allowed only in the library's skybox room.` |
| 4.1 angles | R | `room {room}: brush entity {id} ({class}) has angles "{a}"; a turned room cannot tell whether {class} applies them to its model. Use 0 0 0, or add {class} to the known-direction table.` |
| 4.3 hull | R | `room {room}: prop_static {id} ({model}) reaches {d} units outside the cell; props stay in their cell except as socket furniture.` |
| 4.3 texel | R | `room {room}: prop_static {id} asks for texel lighting, which this vrad does not bake.` |
| 4.5 socket | R | `room {room}: the displacement on brush side {side} has an edge on socket "{socket}"'s plug box; displacements may not meet at a joint.` |
| 4.6 socket | R | `room {room}: water reaches socket "{socket}"; water may not touch a door plug.` |
| 4.9 plug | R | `room {room}: info_overlay {id} names brush side {side}, which is socket "{socket}"'s plug.` |
| 4.11 socket | R | `room {room}: func_areaportal {id} lies in socket "{socket}"'s plug box.` |
| 4.13 conflict | R | `rooms {a} and {b} both pack {file} with different bytes.` |
| 10.4 binding | R | `{roomnav} was written for another pack than {roompack}; recompile the library with ssmap room.` |
| 10.4 missing | W | `{roompack} has no {roomnav} beside it; the level links without navigation, and rooms {rooms} have points of interest.` |
| 4.14 cordon | R | `the room library has a cordon; rooms are cut by their cells, not by cordons.` |
| 11.1 count | R | `level {level}: {k} {up/down} rooms ({cells}, or none); a level has exactly one unless it says "{up/down}: none".` |
| 11.1 switched off | R | `level {level}: says "{role}: none" but places {role} room {room} at cell ({x}, {y}).` |
| 11.1 map key | R | `level {level}: has a{n} {role} room but no {role}_map.` / `level {level}: says "{role}: none" and also names {role}_map.` |
| 11.3 volume | R | `room {room}: a{n} {role} room needs exactly one trigger_room_transition named cxry_transition; it has {k}.` |
| 11.3 arrival | R | `room {room}: a{n} {role} room needs exactly one arrival point; it has {k}.` |
| 11.3 clearance | R | `room {room}: the arrival point at ({x}, {y}, {z}) has no room for a standing player (32 x 32 x 72).` |
| 11.3 wiring | R | `room {room}: nothing fires Transition at cxry_transition.` |
| 11.5 spawn | R | `level {level}: says "up: none" and no room has a spawn point; add an info_poi of type spawn or a spawn cell.` |
| 11.5 count | R | `level {level}: spawn_count {k}, but up room {room} has {m} spawn points.` |
| 11.2 distance | R | `layout: no level of {rows}x{columns} with seed {seed} places the up and down rooms at least {d} doors apart.` |
| 9 styles | W | `face {face} of room {room} at cell ({x}, {y}) needs {k} light styles; the lightest door style {s} was dropped.` |
| 17 (all) | R, W | Every message of 17.3 (level file, resolution, compatibility, singletons, pack lookup, heights, larger rooms, layout, `ssmap roompack`), each asserted by its exact text. |

### 15.5 Determinism

- **Packs:** `ssmap room` at `-threads` 1, 2 and all cores, twice each,
  writes byte-identical packs, including every new section (entity counts,
  names, fold analysis, props, displacements, POIs, lighting layers). This
  extends `RoomReproducibilityTests` and `RoomLibraryCompilerTests`.
- **Links:** `ssmap link` at thread counts 1 and many, twice each, writes
  byte-identical maps in both modes; so does `--flatten` (the VMF).
- **Navigation files:** the `.roomnav` and the `.nav3d` sidecar are
  byte-identical across thread counts and runs, like the pack.
- **Layout:** the same seed gives the same YAML with and without roles, and
  a role-less library's output equals today's (11.2).
- **Lighting:** the base bake and door terms are the same bytes at any
  thread count on one machine; across CPUs, rsqrt-estimate differences are
  handled as the rest of vrad is (`VendorGolden`), never by loosening.

### 15.6 Entity-budget assertions

Per end-to-end level, in both modes: the linked entity count and edict
estimate equal the pack's prediction for that level; the headroom line is
printed with those numbers; and per feature:

| Feature | Asserted count |
| --- | --- |
| Static and detail props, displacements, cubemaps, packed files, POIs | 0 entities added |
| Overlays | 1 per named overlay, 0 otherwise |
| Brush entities, point entities | 1 each, minus (c) drops, socket-furniture drops and folds |
| (b) flags | 1 per referenced flag per placement; 0 after folding |
| `logic_room` | 1 per room that uses it, only with `-mod-entities` |
| Transitions and spawn | 2 per level with `-mod-entities`; 5 stock (3 with both hallways folded); +K−1 for K spawns |
| Singletons | exactly 1 each per level |
| Stripping | none of the 6.3 compile-only classes in the linked lump |

A level built past `cap − reserve` warns and one past the cap is refused,
both naming the costliest rooms; `ssmap layout` with a budget never writes
a level over it.

### 15.7 Fixtures

- **`samples/rooms-3x3`** keeps its five kinds, its levels and its seeds.
  Features are added to the kinds off the centre and out of the doorways
  (section 16); the sockets do not change, so the layouts (which read
  sockets, not contents) stay the same and the seeded-level facts keep
  holding.
- **A naming sibling** (`samples/rooms-names`): one room with references in
  all eight directions, its flags, a foldable and a stateful relay, and
  `room_needs` entities; levels with it at the centre of a 3x3 grid at each
  rotation, and in a corner for (a).
- **A transit sibling** (`samples/rooms-transit`): an up room with a button
  and spawn points, a down room with a hallway trigger, arrivals, and a
  three-level `-sequence`; kept apart so the 3x3 library stays role-less
  and its layouts unchanged.
- **Real-content set:** a small library compiled against synthetic content
  with sky VTFs (`SourceSharp.MapGen/Content`), so default cubemaps land in
  the paks (finding 10).
- **Stress:** `RoomsStressLibrary` varies the new features, for the
  capacity and budget checks at 16×16.
- Every generated sample stays checked by a fact against its generator, as
  `Rooms3x3SampleTests.TheCheckedInSampleIsWhatTheGeneratorWrites` does.

### 15.8 Only verifiable in game

The uncertain engine and game behaviours, as a manual checklist to run in
Source Sharp (and a stock game for the fallback) before the matching
default is relied on:

- [ ] Name matching is case-insensitive for targets and outputs (5.2).
- [ ] An entity key value longer than 1023 bytes is refused or truncated
      (5.6).
- [ ] `logic_branch`, `logic_relay`, `logic_auto` and filters take no edict;
      the entity handle limit (6.2).
- [ ] Unnamed `light` and `light_spot` are removed at spawn; the level
      lights identically with them stripped (6.4, O18).
- [ ] `light_environment` is not read at runtime (6.4).
- [ ] An unnamed `func_occluder` with its entity stripped still occludes;
      occluder toggling by input after the rebase (4.7).
- [ ] `infodecal` near a joined doorway lands on the right face, and is
      removed after applying (4.8).
- [ ] A named overlay toggled through its `info_overlay_accessor` after the
      link renumbered its `OverlayID`, and the accessor's `sides` key not
      read at runtime (4.9).
- [ ] A relay without fast retrigger drops a second `Trigger` inside its
      longest delay; same-tick event order after folding (6.5).
- [ ] `func_door_rotating` and `func_rotating` axis flags read in the
      entity's frame; which brush-entity classes apply `angles` to the
      model (4.1).
- [ ] An area portal whose entity has `StartOpen 1` and no target is open
      (4.11).
- [ ] The sky is drawn from leaves flagged by pass two (4.12).
- [ ] Detail props render with a stable sort by leaf (4.4).
- [ ] A static prop straddling leaves of two rooms is drawn from both
      (4.3).
- [ ] `buildcubemaps` on a linked map writes the names the patched VMTs
      expect (4.10).
- [ ] Fog, tonemap and shadow controllers: which one wins with several (8).
- [ ] Unknown worldspawn keys (`ssmap_entities`) are ignored (7.1).
- [ ] Stock `trigger_changelevel` with "disable touch" fires on
      `ChangeLevel`; the landmark lands the player at the arrival;
      facing is kept or not; multiplayer changelevel behaviour (11.4).
- [ ] The runtime reserve: peak edicts with a full server and bots, to set
      `rooms_entity_reserve` (6.7).

### 15.9 Storage and compression

- **Pinned bytes:** fixed inputs compressed with each codec (Deflate at the
  chosen level, Brotli at the chosen quality and window) equal checked-in
  bytes; the facts run in CI on Linux, Windows and macOS (Intel and Apple
  Silicon). A failure is a runtime change to decide on, never a golden to
  refresh silently.
- **Round trip:** every section decodes to its input for each codec; each
  section's default (none unless its PR measured otherwise) is asserted, and
  a benchmark in the PR records the link-time read cost with and without the
  codec on a warm page cache.
- **Determinism:** packs with compressed sections are byte-identical at any
  thread count and run (15.5).
- **Navigation files:** the same codec facts for `.roomnav` and `.nav3d`
  sections; a `.roomnav` whose binding hash does not match its pack is
  refused, and one that matches links; the `.nav3d` sidecar is written next
  to the `.bsp` and nothing navigation-related is in the pak (unless below
  the stated threshold).
- **Refusals:** an unknown codec byte, a decoded length that differs from
  the stored one, and a truncated payload are refused, naming the room and
  section: `room {room}: section {tag} uses codec {n}, which this build does
  not read.`, `room {room}: section {tag} decodes to {a} bytes, not the {b}
  it records.`
- **Rotation count:** a sunless room's lighting section holds one payload
  and a sunlit room's four; the four bakes of a sunless room, if computed,
  are the same bytes; a sunlit room's door response computed at each of the
  four rotations agrees to float noise; the link takes payload
  `rotation mod count` at every rotation.
- **Once against ×4:** for each section stored once and turned at link,
  the linked result at each rotation equals the one from ×4 pre-turned
  payloads, byte for byte (the exactness claim of 1.1), so the storage
  choice can change on measurement alone.

---

## 16. Growing the 3x3 sample

The sample is generated (`SourceSharp.MapGen.Rooms`: `Rooms3x3Kit`,
`Rooms3x3Sample`, `Rooms3x3Arrangement`, `Rooms3x3Permutations`;
`tools/RoomsSample`) and checked in; `Rooms3x3EquivalenceTests` runs every
criterion over 16 levels. It grows **one feature per PR**, in the order of
section 13, each with its criterion added to `Rooms3x3EquivalenceTests` and
to the independent monolithic map (`Rooms3x3Arrangement.MonolithicVmf`), so
every feature is checked against code that shares none of the pipeline's
placement.

- **Content without Steam.** `SourceSharp.MapGen/Content` already
  synthesises models (`StudioModelWriter`), VTFs (`VtfWriter`) and materials
  (`SyntheticContent`), and the test catalogue has a fixture per feature
  (`Catalog/MapFeature`: `StaticProp`, `DetailProps`, `Displacement*`,
  `Water`, `Overlay`, `EnvCubemap`, `Areaportal`, `SkyboxThreeD`,
  `Occluder`, `BrushEntity`, `SwitchableLight`, and so on). The sample's
  game folder reuses them.
- **Keep the kinds distinct.** Each kind carries its feature off the centre
  and out of the doorways, so every rotation stays a different map:
  - `cross`: a `func_door` as socket furniture on its east socket, a
    `trigger_multiple` whose outputs reach `cxry_` and `cx+1ry_` names, a
    foldable relay, a `cxry_has_<dir>` test and an entity with `room_needs`
    (naming, rotation, (a) to (c), folding);
  - `tee`: a static prop and an `info_overlay`;
  - `corner`: a small displacement patch and a `func_ladder`;
  - `hall`: a water pool away from the sockets, an `env_cubemap` and a
    specular floor;
  - `end`: a sky opening (sun through it), a `func_occluder`, a switchable
    light, detail props on a `%detailtype` floor;
  - the library: a `light_environment` in the gaps and, later, a skybox room.
- **Criteria per feature**, from each section's "Equivalence": resolved
  entities and effective I/O; models, per-model traces and collision; props
  by record and leaf coverage; overlays by union polygon; cubemap samples
  and in-room assignment; areas as a partition of the lattice; water
  contents, surface z and fluids; displacement surfaces and collision;
  detail props by distribution; lighting by tolerance (9.8).
- **Entity budget facts.** Per level, the linked entity and edict counts
  equal the pack's prediction, the headroom line is printed, a level past
  `cap − reserve` warns and one past the cap is refused naming its costliest
  rooms, and `ssmap layout` never exceeds its budget. Every entity criterion
  runs in both emission modes (with and without `-mod-entities`), link and
  flatten agreeing in each.
- **Known differences** extend the doorway-face exception, each with a fact
  asserting exactly that difference and nothing else: missing doorway faces
  (today), no detail props in doorways, cubemap assignment near doors,
  lighting tolerances.
- **Transition rooms.** A small separate library and level set (a role-less
  3x3 library must keep generating exactly today's levels, 11.2): an up
  room with a button and a down room with a hallway trigger, arrival and
  spawn POIs, a three-level `-sequence`, each level checked in both modes,
  at all four rotations of the role rooms, against `--flatten`.
- **The naming rotation level** (5.11) is its own small level: one room with
  references in all eight directions at the centre, at each rotation, a
  distinct room in every neighbour cell, and a corner variant for (a).
- **Sweep.** `SSMAP_ROOMS3X3_SWEEP` keeps working; lighting criteria are
  optional in the sweep (vrad per arrangement is expensive), gated by their
  own variable.
- **Stress.** `RoomsStressLibrary` varies the new features too, so the
  capacity and entity-budget checks are exercised by a 16×16 level.

---

## 17. Multiple libraries, room heights and large areas

The owner's decisions D19 to D23 (section 14, 2026-09-29): a level may take
its rooms from several library VMFs, rooms may differ in height, the
generator draws from every library and makes large open areas, and a command
combines many libraries into one pack. This section is the plan for all
five. The owner's answers to its open points (the same day) are D24 to D28:
one summary line for equal duplicate singletons, the navigation grid
refused and the other navigation settings warned, a sunlit room baked under
a dropped sun refused once the door light lands, no stacked storeys, and
`ssmap roompack` as the verb. It builds on PR 8's level file keys
(section 11) and on what PRs 9 to 13 landed (the base bake and its pack id,
the door portals option, the skybox room), and changes nothing they decide.
The implementation order is PRs 17 to 21 in section 13.

### 17.1 What the code assumes today about height and footprint

**Every room is exactly one cell, and the cell is a cube.** Nothing in the
pipeline reads a room height or a room size of its own; both are the cell
size. The assumptions, and what each means for taller or larger rooms:

| Assumption | Where | What it decides |
| --- | --- | --- |
| A room fills one cell | `RoomDefinition` ("built to occupy exactly one grid cell"), `LevelLayout.Validate` (one room per cell), `LevelGrid.ToLayout` (a joint is two sockets across one shared cell wall), `RoomLinter.CheckReachable` (a socket reaches the cell one step away), `LevelGenerator` (one candidate per cell, a four-bit socket mask) | Every notion of neighbour. |
| The cell is a cube | `RoomLibraryVmf` (`Marker.Cell` is the corner plus `(c, c, c)`; the `cell_size` key reads "the cell is a cube"), `RoomLinter.CheckModel` (every brush in `[0, c]³`), `RoomLinter.CheckCompiled` (the interior in the cell), `RoomModel` (floor at 0, ceiling at `c`) | The only Z extent a room has is the cell size. |
| The door fixes the height | `SocketKit.OpeningUnit` centres the opening on the face vertically; `PlayerHull.DoorProblem` then requires its sill on the floor, which forces `door_height = cell_size − 2 × wall_depth` (within `SillTolerance`, 0.01) | The kit alone fixes every room's height. A taller room would lift its door off the floor. |
| One socket per face | `RoomDefinition.Validate` ("two sockets on the same face"), `RoomFacing` (four values), socket names default to the wall's | A room larger than a cell has no way to name a second door on one side. |
| Placements never move in z | `RoomTransform.Apply`: whole cells in x and y, quarter turns about +z; every floor is at z = 0 | Rooms of different heights still meet at their floors; nothing stacks. |
| The top tree is planar | `LevelLinker.BuildTopNodes` / `BuildRegion` split only on x and y cell faces and send each occupied cell to its room's root; `TopPlanes` lists those faces | Z needs no plane: a point above a room's ceiling falls into that room's tree, which answers solid. |
| Top-tree bounds are one cell tall | `BuildRegion` bounds every node `z ∈ [0, c]`; `Assemble` bounds the shared solid leaf the same | The engine culls nodes by these bounds, so a room taller than the cell under such a node would be culled when only its upper part is in view (**uncertain** in detail: which engine walks read node bounds; that the renderer's walk does is certain). |
| Capacity ignores extent | `CheckCapacity` sums per-placement counts (`LinkCounts`, `LinkTotals`); no check of the level against the engine's ±16,384 coordinate range (`GeometryEpsilons.MaxCoordInteger`) | A tall room or a wide grid could pass the link and break the engine's limits. |
| Sockets, plugs and doors are per face | `RoomLinter.SealBox` (the plug box from `OpeningUnit × cell`), the plug census, `ValidateJoints`, `DoorEdges` (room-local plug boxes against the room's leaves) | Height-independent once the door box is fixed; footprint-dependent through "one socket per face". |
| Vis is the door graph | `DoorEdges`, `CloseRows` (the closure over joints); Q3 plans door-to-door sight per room | Height-independent; a larger room simply has more doors, and its own vvis is exact inside it. |
| Sky is per room | 4.12: pass one per room at pack time, pass two over the linked PVS; the 3D skybox below the grid | Height-independent. |
| Lighting is per room and per door | Section 9: base bake per room, capture and response per door | Height-independent in kind; the capture's black box must enclose the room's shape, and storage grows with the room's luxels (9.5). |
| Navigation voxels are cubic cells | `RoomNavBuilder` builds an `n × n × n` grid (`NavSettings.CellVoxels`), door points at the middle of each face; `docs/nav3d-format.md` 1 and 2 define cubic cells, one room per cell, columns of `cellVoxels²` per placed cell, runs with 8-bit `zLo` and `height` | A taller room needs more voxels per column; a larger room needs columns in several cells. |
| Linker entities stand at the cell centre | `LevelLinker.CellCentre`: `(c/2, c/2, c/2)`, turned | One spelling shared by link and flatten (5.9). |
| Names and neighbour logic are per cell | 5.2: `cxry_` with ±1 offsets; (b) flags and `room_needs` name four sides, one neighbour each | A room with two neighbours on a side has no name for either. |
| Props and furniture stay in the cell | PR 6 and PR 7's cell rule (the hull or brush inside the cell box, furniture only into its doorway) | The cell box is the cube. |

In short: **height** is fixed by the kit and assumed in the cell box (split,
lint, props, furniture, navigation, linker entities) and in the top tree's
bounds, and nowhere else; the linker's exactness never depended on it, since
z is never turned or moved. **Footprint** is assumed wherever the code says
"neighbour": joints, reachability, the top tree, names and the generator.

### 17.2 The level file with several libraries

```yaml
# three libraries, two aliases
libraries:                 # key: library VMF, relative to this file; the FIRST supplies the singletons
  base:  ../rooms.vmf
  caves: ../caves.vmf
  halls: ../halls.vmf
aliases:                   # optional: a short name for a room
  C: base.corner
  X: caves.cross
rows: 2
columns: 4
grid:
  - [C@270, X,     base.tee, ~]
  - [hall2, +,     tee@90,   C]
```

- **`library` or `libraries`, exactly one.** `library: x.vmf` keeps its
  meaning and its bytes: a level file written today reads and writes exactly
  as it does. `libraries` is a YAML mapping, in the order written (the
  representation model keeps it), of at least one entry. A **key** starts
  with a letter and holds only letters, digits, `_` and `-`: no dot, since
  the dot separates the key from the room in a cell. Two keys that differ
  only in case are refused: a key becomes a pack namespace and the front
  of room names (17.10), and room names are unique ignoring case. Two keys
  naming the same
  file, after resolving against the level file's folder, are refused.
- **`aliases`** is optional, with either form. An alias is a name by the
  room-name rule (`RoomNames`) without a dot. Its value is what a cell may
  hold, without a rotation: `lib.room` or a bare room name, resolved by the
  rules below. An alias may not be the name of any room of any listed
  library (D19: aliases cannot shadow room names); it may equal a library
  key, since a key only matters before a dot.
- **A cell** is `~`, `+` (a cell a larger room covers, 17.8), or a token
  with an optional `@` rotation. The token is resolved in this order:
  1. **Qualified**: it holds a dot, and the part before the first dot is a
     library key. The rest is a room of that library; if it has none, the
     cell is refused.
  2. **Alias**: it is an alias; its value is resolved by these rules.
  3. **Bare**: a room name, looked up in every library. Exactly one library
     has it: that room. None: refused, naming the libraries. Several:
     refused, listing every qualified candidate (D19).
- **Why qualified first.** Room names may hold dots today (`RoomNames`
  allows `.`), so `v2.hall` could be a room of that name. The level refuses
  the ambiguity instead of picking: when a listed library has a room whose
  name begins with another listed library's key and a dot, the level is
  refused at load (the dotted-name guard), whether or not a cell uses it,
  so an edit elsewhere in the file never changes what a cell means. The
  cure is to rename the key, which is the level author's to choose.
- **Lookup is exact**, as the pack index is (`RoomPackIndex.Find`), so
  `Corner` is not `corner`. Candidate lists are in library order.
- **Resolution needs the libraries' room lists**, so `LevelYaml.Parse`
  checks the syntax and `LevelGrid` keeps each cell's line and column;
  resolution runs where the rooms are known (link, flatten, layout,
  `ssmap rooms`) and reports with the same `line L, column C: ` prefix
  (`LevelFileException`).
- **Writing.** `LevelYaml.Write` writes `library` for a one-library level,
  as today. For several it writes `libraries` in level order, the
  level's aliases if it has any (the generator makes none), and each cell
  as the grid holds it; `LevelLibraries.Shorten` first gives each cell its
  shortest unambiguous spelling: bare when one library has the name, else
  `key.room`. The keys
  go where `library` goes, before `rows`; PR 8's transition keys keep their
  place between `columns` and `grid`, and `aliases` goes before them, after
  `columns`. The unknown-key message lists every key in that order (17.3).
- **Transitions.** Role rooms (section 11) may come from any library; the
  level rule counts placements, not libraries. `spawn: [column, row]` may
  name a `+` cell; it means the room covering it.

### 17.3 Messages

Level file messages carry the `line L, column C: ` prefix of
`LevelFileException`. R refuses, W warns; braces are filled from the case.
The link prints its warnings as `ssmap link: warning: {text}` and the
flatten the same lines, in the same order (5.9); `ssmap rooms` over a
level prints the singleton and compatibility lines too. Two texts point at
`ssmap roompack` (PR 18); PR 17 prints them as written, and if PR 18 has
not landed with it, the verb in them is the one PR 18 adds.

| Rule | Kind | Message |
| --- | --- | --- |
| 17.2 both | R | `a level names its libraries once: library or libraries, not both.` |
| 17.2 neither | R | today's `the level has no {missing}.`, with the library entry of the list spelt `library or libraries` |
| 17.2 unknown key | R | `unknown key "{key}"; a level has library (or libraries), rows, columns and grid, and may have aliases, up, down, up_map, down_map, spawn and spawn_count.` (PR 8's text, with `libraries` and `aliases` added) |
| 17.2 shape | R | `libraries is a mapping of a key to a room library VMF, like base: ../rooms.vmf.` |
| 17.2 empty | R | `libraries names no library.` |
| 17.2 key | R | `the library key "{key}" is not a key; a key starts with a letter and holds only letters, digits, '_' and '-'.` |
| 17.2 key case | R | `the library keys "{a}" and "{b}" differ only in case.` |
| 17.2 path | R | `library {key} is empty; it names a room library VMF.` |
| 17.2 same file | R | `libraries {a} and {b} name the same file, {path}.` |
| 17.2 aliases shape | R | `aliases is a mapping of a short name to a room, like C: base.corner.` |
| 17.2 alias name | R | `the alias "{alias}" is not a name; an alias starts with a letter, a digit or '_' and holds only letters, digits, '_' and '-'.` |
| 17.2 alias turn | R | `the alias "{alias}" names "{value}"; an alias names a room, and the cell gives the turn, like {alias}@90.` |
| 17.2 alias shadows | R | `the alias "{alias}" is also the name of room {room}; an alias may not hide a room.` (`{room}` qualified when the level has `libraries`) |
| 17.2 dotted name | R | `room "{room}" of library {key} reads as room "{rest}" of library {prefix}; rename the library key {prefix}.` |
| 17.2 qualified | R | `library {key} ({path}) has no room "{room}".` |
| 17.2 bare, none | R | `no library of the level has a room "{room}"; its libraries are {keys}.` (a `library` level keeps today's missing-room message) |
| 17.2 bare, several | R | `the room "{room}" is in libraries {k1} and {k2}; write {k1}.{room} or {k2}.{room}, or name one with an alias.` (three or more: `{k1}, {k2} and {k3}`, `{k1}.{room}, {k2}.{room} or {k3}.{room}`) |
| 17.5 cell | R | `libraries {a} ({pa}) and {b} ({pb}) are built for different grids: cell_size {x} against {y}; the rooms of a level share one cell size.` |
| 17.5 kit | R | `libraries {a} ({pa}) and {b} ({pb}) have different door kits: {key} {x} against {y}; the rooms of a level join through one kit.` (`{key}` the first that differs of `door_width`, `door_height`, `wall_depth`) |
| 17.5 navigation grid | R | `libraries {a} and {b} build navigation on different grids: {x} against {y} voxels per cell; a level's navigation is one grid.` |
| 17.4 differs | W | `library {b}: its {class}{ "name"} differs from library {a}'s ({key}: "{x}" against "{y}"); the level takes library {a}'s, the first listed, and drops it.` |
| 17.4 equal | W | `library {b}: {n} singleton(s) equal to library {a}'s dropped ({classes}).` (one line per library) |
| 17.4 first lacks | W | `library {b}: its {class}{ "name"} is dropped; the level's singletons come from library {a}, which has none.` |
| 17.4 option | W | `library {b}: {key} {x} is ignored; the level takes library {a}'s, {y}.` |
| 17.4 worldspawn | W | `library {b}: its rooms were compiled with worldspawn {key} "{x}"; the level's is "{y}" (library {a}). They link as compiled; build the libraries into one pack with ssmap roompack to compile them with the level's.` |
| 17.4 navigation | W | `library {b}: its rooms' navigation was built with {key} {x}; the level's is {y} (library {a}).` |
| 17.4 skybox | W | `library {b}: its skybox room "{x}" is dropped; the level's skybox is library {a}'s, "{y}".` |
| 17.4 skybox, first lacks | W | `library {b}: its skybox room "{x}" is dropped; the level's singletons come from library {a}, which has no skybox.` |
| 17.4 sun, until PR 10 | W | `library {b}: room {room} was baked under library {b}'s sun, and the level takes library {a}'s; it links as baked. Build the libraries with one sun.` (one line per library, naming its first such room in link order) |
| 17.4 sun, once PR 10 lands | R | `library {b}: room {room} was baked under library {b}'s sun, but the level takes library {a}'s; a sunlit room links only under the sun it was baked with. Build the libraries with one sun.` |
| 17.4 lighting | R | PR 9's texts, unchanged: `room {room} has no baked lighting, but room {other} of the same level has; ...` and `rooms {a} and {b} were lit with different settings (ranges, sun or map flags); ...`, whichever libraries the two rooms come from |
| 17.10 not a namespace | R | `room pack {pack} combines libraries {keys}; it has none named {key}.` |
| 17.10 plain pack | R | `room pack {pack} holds one library without a namespace; give it to one key with -rooms {key}={pack}.` |
| 17.10 moved | W | `library {key}: the level names {path}, but room pack {pack} built it from {recorded}.` |
| 17.6 too low | R | `room {room}: room_height {h} leaves no room for the door; a room is at least {min} tall (door_height + 2 x wall_depth).` |
| 17.6 not whole | R | `room {room}: room_height "{v}" is not a whole number of units.` |
| 17.6 voxels | R | `room {room}: room_height {h} is not a whole number of navigation voxels ({v} units each).` |
| 17.6 too tall | R | `room {room}: room_height {h} is taller than {max}, the most {limit}.` (`{limit}`: `the engine's coordinates allow`, or `navigation describes (255 voxels)`) |
| 17.6 extent | R | `level {level}: reaches {axis} = {v} at cell ({x}, {y}); the engine's coordinates stop at 16384.` |
| 17.8 footprint | R | `room {room}: room_footprint "{v}" is not two whole numbers of cells from 1 to 16, like "2 3".` |
| 17.8 `+` alone | R | `+ is in no room's footprint; + marks the cells a larger room covers besides its south-west one.` |
| 17.8 covered | R | `room {room} at cell ({x}, {y}) covers cell ({cx}, {cy}), which holds {token}; write + there.` |
| 17.8 off grid | R | `room {room} at cell ({x}, {y}) turned {deg} covers {w} x {d} cells and runs off the {rows}x{columns} grid.` |
| 17.8 neighbour name | R | `room {room}: entity {id} ({class}) key "{key}": "{value}" names a neighbour cell; a room of {w} x {d} cells names only its own cell (cxry_).` |
| 17.8 side | R | `room {room}: entity {id} ({class}) room_needs "{value}": a room of {w} x {d} cells has several neighbours on a side; name a socket, joined_<socket>.` |
| 17.8 blocks | R | `room {room}: its compile does not split at every cell face of its {w} x {d} footprint (cell ({i}, {j})); this is a bug in the room compile.` |
| 17.9 no large rooms | R | `layout: -large asks for large areas, but no library of the level has a room taller or wider than one cell.` |
| 17.9 no fit | R | `layout: no level of {rows}x{columns} with seed {seed} covers {k} cells with large rooms in groups of {g}; lower -large or grow the grid.` |
| 17.10 key twice | R | `ssmap roompack: the key {key} is given twice.` |
| 17.10 no key | R | `ssmap roompack: {path} gives no key ({stem} is not a key); write key={path}.` |
| 17.10 only unknown | R | `ssmap roompack: -only names {key}, which is not one of {keys}.` |
| 17.10 only stale | R | `ssmap roompack: library {key} changed since {pack} was built ({what}); rebuild it too, or leave out -only.` (`{what}`: `its VMF` or `the first library's singletons`) |

### 17.4 Singletons across libraries

D20: the **first listed library supplies every singleton**. "Singleton" means
everything a level has once, whichever room it came through:

| What | Kept from the first library | Another library's copy |
| --- | --- | --- |
| Library entities (`LENT`: the sun, fog, tonemap, `shadow_control`, `postprocess_controller`) | written once after the worldspawn, as today (`LevelSingletons`) | dropped; warned as differing, equal (one summary line, D24) or one the first lacks (O25) |
| The skybox room (PR 13's `info_room_skybox`, named in the `SKYB` section) | placed below the grid, as today (`LevelLinker.SkyboxPlacement`) | never loaded or placed; warned with the skybox lines (a compiled room has no keys to call two copies equal, so every other library's skybox warns) |
| Library options (`LOPT`): entity reserve (`rooms_entity_reserve`), logic folding (`rooms_fold_logic`), door portals (`rooms_door_portals`, PR 13: a joint between two libraries gets a portal exactly when the first library asks), `mapversion` | the level's | dropped with the option warning when a later library sets it to other than the level's value, the key and value spelt as the library keys are; `mapversion` is dropped without one (it is the editor's save counter and differs on every save, so the line would print on every link) |
| Worldspawn keys (the linked map has one) | the worldspawn of the first library's first placed room, as `MergeEntities` takes the first room's today; when the level places no room of the first library, the first placed room's of the earliest listed library it places | a room of another library whose worldspawn differs is linked as compiled, with one warning per library naming the first differing key in its worldspawn's order (`world_mins`, `world_maxs`, `hammerid`, the ids the link stamps and the `nav_` keys, which the navigation line reports, are not compared) |
| Navigation settings that are not the grid (slope, step and jump heights and distance, costs, agent presets, D25) | the `.nav3d` header's, from the first library | the rooms' records are carried as built, with the navigation warning |

- **Not singletons.** Each library's **name keys** (`rooms_name_keys`,
  O3) stay with that library's rooms: they shaped the rooms' `NAM`
  sections at pack time (`RoomCacheKey` folds them in), so taking the first
  library's would change what another library's rooms mean. The
  navigation grid (voxels per cell) and the kit are compatibility, 17.5.
- **Why a warning for an equal copy.** D20 says duplicates warn. An equal
  copy is harmless, and a level of libraries that share one sun would warn
  on every link, so equal copies are summed into one line per library
  (D24). Equal is `LevelSingletons`' rule: every key but `id`, `hammerid`
  and `origin`, a missing key read as empty, outputs in order.
- **Why the first library's even when it lacks one.** A singleton only a
  later library has is also dropped (with its own warning) rather than
  filling the gap, so the rule is one sentence and a level's sun never
  depends on which rooms it happens to place (O25: built so, and open for
  the owner to revisit).
- **Which libraries speak.** Every listed library is checked, whether or
  not the level places one of its rooms: the level file names it, so its
  singletons are the level's to settle, and a warning never appears or
  goes away because an edit elsewhere in the grid placed a room. The
  worldspawn line is the one exception, since a library's worldspawn is
  read from a room it places.
- **The room-level check is per library.** D3's pack-time rule still holds
  a room's sun to its own library's; the level-wide sun is the first
  library's. `RequireSameWorld` keeps refusing two rooms of **one** library
  that disagree (they cannot, the split copies the worldspawn), and warns
  across libraries instead.
- **Link and flatten agree** (5.9): the flatten writes the first library's
  worldspawn and library entities and prints the same warnings.
- **Why the warnings point at `ssmap roompack`.** Rooms of separate packs
  were compiled, and since PR 9 baked, under their own library's
  worldspawn and sun; the link cannot redo that. The combine command
  applies the rule before compiling (17.10), so a combined pack links
  without these warnings.
- **Lighting (PR 9, D26).** Each pack's rooms were baked at pack time
  with their own library's `light_environment` added after the worldspawn
  (`RoomLighting`), and the pack id and the room cache key carry that sun
  and the `-vrad` switches. The link keeps PR 9's level rule across
  libraries unchanged: a level of lit and unlit rooms, or of rooms lit with
  different ranges, sun presence or map flags, is refused with PR 9's
  texts, whichever libraries the rooms come from; so a lit library and an
  unlit one, or a library with a sun and one without, never share a level.
  Libraries that each have a sun may differ in it. A room stored once (no
  sun or sky reaches it) is lit by its own lamps only and links under any
  sun. A **sunlit** room (rotation count 4, 1.1) baked under a sun that
  differs from the first library's (`LevelSingletons`' equality) is lit by
  a sun the level does not have: D26 refuses it once the door light
  (PR 10) lands, naming the library's first such room in link order (17.3;
  PR 17 landed on PR 10, so it refuses), and before the door light the link
  warned instead; the flatten bakes nothing, so it has no such line, and vrad of the flattened level lights every room under
  the level's sun. The level's two sun world
  lights and its sky are the bake's (PR 9 takes them from the first lit
  placement); across libraries they are taken from the first placed room of
  the first library when the level places one, so they match the sun
  entity the level writes.
- **Budget.** The level's library entities are the first library's, counted
  once (`LevelEntityReport.Library`, `LayoutEntityBudget.LevelEdicts`),
  with its skybox room's entities and, when it asks for door portals, one
  `func_areaportal` per joint (PR 13).

### 17.5 Compatibility

D21: the libraries of a level must agree on the **cell size** and the
**door kit** (`door_width`, `door_height`, `wall_depth`), refused naming
both libraries (17.3). The check runs over the libraries the level places
rooms of, in library order, each against the earliest of them: a library
the level only names joins nothing, and at link its grid is known only
from a room it holds (a pack records the grid in each room's definition,
not in a library section), so the flatten, which could split it, checks the
same set and the two refuse alike. The same comparison already holds rooms of one library
together (`RoomLibraryVmf.CheckMarkers`) and a layout to its library
(`RoomLinter`'s placement rule); PR 17 runs it across libraries.

**The room height is not compared**, and after 17.6 it is per room anyway.
`door_height` is part of the kit and is compared: two rooms only join if
their openings are the same rectangle. The **navigation grid** (voxels per
cell) is compared too, when the libraries build navigation: the `.nav3d`
file has one voxel size (`docs/nav3d-format.md` 2), so rooms on two grids
cannot be stitched. The other navigation settings follow the singleton rule
(17.4): D25, the owner's answer to O26. The settings are the libraries'
worldspawn `nav_` keys (`NavSettings`), read at link from a placed room's
compiled worldspawn and in the flatten from the library VMF, so the check
does not need the navigation loaded; a library that builds no navigation
(`nav 0`) is not compared. The stitch (`LevelNavLinker`), which refused
rooms whose settings differ at all, now refuses only a different grid
(voxel size, voxels per cell) and writes the first library's settings in
the header.

### 17.6 Room heights

A room's height becomes its own: the `info_room` key **`room_height`**, in
units, default the cell size, so every existing library means what it did.

- **The door stays where it is.** The door box is the one a standard,
  cube-shaped room of the library has: `SealBox` keeps computing its z range
  from `OpeningUnit` and the **cell size**, never the room height. So a
  tall room's doors and plugs are bit for bit a short room's, joints between
  rooms of different heights match exactly, and every cube room's plug box
  is unchanged. (Recomputing the sill as `wall_depth` would be cleaner but
  could move a plug by the last bit where `PlayerHull.SillTolerance` let a
  library through, and change its rooms' compiles.) Above the door, a tall
  room has a lintel up to its own ceiling.
- **Rules.** A whole number of units; at least `door_height + 2 x
  wall_depth`; at most 16,384; when the library builds navigation, a whole
  number of voxels and at most 255 of them (the `.nav3d` run fields are
  8 bits). A room lower than the cell is allowed: a low hallway.
- **Floors stay at z = 0.** Placements still never move in z, so doors line
  up by construction. Sunken or raised floors are not in this plan (D27,
  confirmed from O31): they need a vertical offset in the kit.
- **What changes, and where.** The cell box becomes
  `[0, c] × [0, c] × [0, h]` in the split (`Marker.Cell`, so a tall room may
  not overlap the room built above it in the library), the model lint and
  the compiled lint, the prop and furniture cell rule, navigation
  (`n × n × h/voxel` voxels; door points unchanged) and the linker's cell
  centre (`(c/2, c/2, h/2)`, the cube's value for a cube). The top tree
  bounds each node by the tallest room in its region and the shared solid
  leaf by the tallest room of the level; for a level of cubes both are
  today's. `CheckCapacity` gains the extent check: the grid in x and y and
  the tallest room in z within ±16,384. The linker's transforms, the plug
  census and carve, the door graph, the sky passes and the entity budget do
  not change.
- **Lighting (section 9).** Nothing new in kind: the base bake and the
  capture box enclose the room as it is; the door basis is on the unchanged
  opening. A tall sunlit room is ×4 like any sunlit room.
- **Why not a height class or a multiple of the cell.** Nothing in the link
  needs heights to be quantised (z is neither turned nor moved), so the only
  granularity is navigation's voxel. The generator groups by height itself
  (17.9).

### 17.7 Large open areas: the options

"Large open areas grouped together, connected to smaller rooms or
hallways" (D22) needs space larger than one cell with no wall in it. Three
ways to get it, against the linker's exactness rules:

**A. Multi-cell rooms.** A room covers `W × D` cells (and has its own
height). It is authored, compiled, vis'd and lit as one room.

- *Quarter turns and whole-cell translation*: exact. A `W × D` room at a
  quarter turn maps `(x, y)` to `(−y + D·c + tx, x + ty)`: a permutation, a
  negation and an addition of whole cells, the same arithmetic as today with
  `c` replaced by `D·c` (`Rotate` is unchanged; `Translate` adds the turned
  footprint's offset, which is `c` itself for a 1 × 1 room, so its bits do
  not move).
- *Plug carving*: unchanged per socket; a room has one socket per cell edge
  of its perimeter instead of one per face.
- *Vis*: better than today inside the room: vvis of the whole room is exact,
  where a grid of cells would be closed over its door graph. Door graph at
  its joints unchanged.
- *The top tree* is the hard part. It routes each cell to a room root; a
  room covering several cells would be reached through several top-tree
  leaves. Pointing them all at one root makes the tree a graph, and the
  engine gives each node one parent at load (the renderer marks visible
  nodes up the parent chain), so a node reached two ways is drawn only
  through one of them. Two ways out:
  - build the top tree over footprints, splitting only on planes that cut
    no room: works for most layouts, but a pinwheel of four larger rooms
    around a fifth has no such plane, and a level that is otherwise valid
    would be refused for a reason an author cannot see;
  - **compile the room so its tree splits at every cell face first**, which
    is what stock vbsp already does with its 1024-unit blocks
    (`BlockGrid.BuildBlockTree`, each block's tree built from the brushes
    clipped to it and stitched under axis planes). With the block size equal
    to the cell size, the room's tree is a kd tree over its cells with one
    self-contained subtree per cell. The link then routes each covered cell
    to that cell's subtree, exactly as a 1 × 1 room's cell goes to its root,
    and omits the room's few block nodes above them. `TopPlanes` and the top
    tree's algorithm stay as they are. This is the one chosen.
- *Lighting*: one bake over the whole area, exact inside it; the door terms
  are those of any room. Storage grows with the luxels (9.5).
- *Cost*: L. Sockets, joints, names, navigation and the generator all learn
  footprints.

**B. Group tall one-cell rooms with sockets on every side.** No linker
change at all, and heights from 17.6 are enough. But every seam is a wall
with a doorway in it: the player sees a grid of rooms, not an open area,
and light reaches only one door away (D3). It does not meet D22. It stays
useful as the generator's grouping of tall rooms (17.9).

**C. "Open" sockets that merge two cells without a doorway.** A second
socket kind whose opening is the whole face between floor and ceiling, its
plug the whole wall, carved at a joint like a door.

- *Turns, translation, vis*: exact and unchanged, as any socket.
- *Plug carving*: the carve leaves a gap two wall depths wide across the
  whole face. The floor and ceiling faces under and over the plug were
  never emitted (vbsp drops faces inside solid), and the link cannot make
  faces without a compile, so today's small "missing doorway faces"
  difference becomes a visible strip without floor or ceiling across every
  seam of the area.
- *Heights*: the opening can only be as tall as the lower room.
- *Lighting*: D3 stops light at one door. A lamp in one cell of a 4 × 4 area
  of open-socket rooms lights its neighbours and nothing further, and the
  door response's basis (9.1, patches over the opening) is spread over a
  whole face; the error lands where the player looks across the area.
- *Kit*: a second kit, part of the compatibility check.
- *Cost*: M, cheap in the generator (tiles), expensive in what it leaves.

**Recommendation: A**, multi-cell rooms with the cell-block compile, with
B's grouping in the generator. Reasons: it is the only option in which a
large area is actually open (no seams, no missing faces), lit by one bake
(no light cut off two cells away), and seen by an exact vis; it keeps every
exactness property (whole cells, quarter turns, exact plug carve) and the
top tree's algorithm; tall one-cell rooms (17.6) are its `1 × 1` case, so
heights and footprints are one chain of work; and the cost sits at pack
time (block splits, larger compiles), where D1 and D16 want it.

### 17.8 Multi-cell rooms

- **Authoring.** The `info_room` key **`room_footprint`**, `"W D"`: cells
  along the room's own +x and +y, 1 to 16 each (a guard, like
  `LevelYaml.MaxCells`), default `"1 1"`. The marker stays at the low
  corner; the room's box is `[0, W·c] × [0, D·c] × [0, h]`. Brushes may
  cross the room's internal cell faces freely; only its perimeter is held to
  the kit.
- **Sockets per cell edge.** A socket is found, as today, by a plug filling
  the kit's box at the centre of one **cell edge** of the perimeter: the
  socket is `(face, offset)`, the offset counted in cells from the room's
  own south (on the east and west faces) or west (on the north and south
  faces). Default names: a 1 × 1 room keeps `east`, `west`, `north`,
  `south`; a larger room names them `east0`, `east1`, ... in offset order,
  overridable by `socket_east0` and so on. `RoomDefinition.Validate` refuses
  two sockets at one `(face, offset)`.
- **The room compile** splits at every cell face first: `ssmap room` sets
  the block size of `BlockGrid` to the cell size for a room larger than one
  cell (a room-compile setting on the vbsp context, not a stock option:
  `ssmap vbsp` never sees it, so stock output does not move and there is no
  compliance entry; exposing it to `ssmap vbsp` would need one). A 1 × 1
  room compiles exactly as today. After the compile, the pack walks the
  room's tree from its head through the block planes and records per cell
  the root of its subtree and the block nodes above them; a tree that does
  not split at every cell face is refused as a bug (17.3). The internal
  splits cost faces (a large floor is cut every cell); `CheckCapacity`
  counts them as it counts everything, from the compile.
- **Link.** `LevelLayout.Validate` refuses footprints that overlap. The top
  tree's occupants map every covered cell to (placement, the room-local
  cell), and a covered cell's top-tree leaf points at that cell's subtree
  root, turned. The room's block nodes are omitted from the node lump with
  a prefix sum, as PR 7 omits a dropped model's runs. Joints: two sockets
  across one shared **cell edge** of two different placements
  (`LevelGrid.ToLayout`, `ValidateJoints`, `CheckReachable`, `DoorEdges`,
  all per socket already). Everything per room (entities, props, brush
  models, pak, collision, lighting, navigation) is carried as for any room.
- **The level file.** The room is written in its **anchor** cell, the
  south-west cell of its footprint as placed (the first of its cells in link
  order, `LevelGrid.Placed`), and every other cell it covers holds `+`. The
  reader checks that each `+` is covered and each covered cell is `+`, with
  the 17.3 messages. `+` is not a room name (`RoomNames` starts names with a
  letter, a digit or `_`), so a build before this one refuses such a file
  with its room-name message rather than misreading it. O29 asks the owner
  about the mark.
- **Names (section 5).** `cxry_` resolves to the anchor cell,
  `c<col>r<row>_`. Neighbour offsets (`cx+1ry_` and the others), the (b)
  flags and `room_needs` directions name one neighbour per side, which a
  larger room does not have, so in a room larger than one cell they are
  refused at pack time (17.3); `room_needs` takes `joined_<socket>` with a
  socket name there. A grammar for neighbours of larger rooms is O28.
- **Props, furniture, POIs.** The cell rule reads the footprint box;
  furniture names one of the room's sockets as today.
- **Navigation.** One voxel grid over the footprint (`W·n × D·n × h/voxel`);
  door points at the middle of each socket's cell edge. In the `.nav3d` the
  room's columns are written per covered cell, as every placed cell's are,
  so the reader's point lookup is unchanged; only the height varies
  (17.11).
- **Transitions (section 11).** A role room may be larger than one cell;
  the link and the level rule treat it as any placement. The generator
  offers only one-cell role rooms in role cells (O30).
- **Flatten.** `VmfPlacement` with the footprint transform; it writes each
  room's brushes once.

### 17.9 The generator: several libraries, heights, large areas

`ssmap layout` gains the libraries and three options; with neither it
writes exactly what it writes today.

```
ssmap layout <library.vmf> ...                            (one library, as today)
ssmap layout <key>=<library.vmf> [<key>=<library.vmf> ...] -rows R -columns C -seed N
             [-empty <ratio>] [-large <share>] [-group <n>] [-max-height <units>]
             [-rooms <pack> | -rooms <key>=<pack> ...] [-entity-budget <n>] [-mod-entities]
             [PR 8's role and sequence options] [-out <level.yaml>]
```

- **Candidates.** Every room of every library, in library order then room
  order, each under its qualified name. A room is **standard** when it is
  one cell and no taller than the cell size, **large** otherwise (a low
  hallway is standard). `-max-height H` drops rooms taller than H.
- **`-large <share>`**, in `[0, 1)`, default 0: the share of occupied cells
  large rooms cover. At 0 the large rooms are dropped from the candidate
  lists before anything is drawn, so a library that gains large rooms still
  generates exactly the levels it did, and a single-library run is
  byte-identical to today's. **`-group <n>`**, default 3: the most large
  rooms in one group.
- **Order of work**, each step drawing from its own stream so that a step
  that does not run leaves the others' draws alone:
  1. **Empty cells**, main stream, as today.
  2. **Large groups**, from an **area stream** seeded with the seed XOR a
     fixed constant (the first 64 bits of the fractional part of √2,
     `0x6A09E667F3BCC908`, beside PR 8's `RoleStream`), created only when
     `-large` is above 0. Until the covered cells reach
     `floor(share × occupied)`:
     - **Start a group**: the next cell of a shuffled list of free cells;
       a large candidate (room and turn, shuffled) whose footprint, anchored
       there, lies on free occupied cells and whose sockets agree with every
       large room it touches (a socket on both sides of a shared edge or on
       neither).
     - **Grow it** to at most `-group` rooms: the free cells sharing an edge
       with the group, shuffled; for each, the candidates that fit there and
       have a socket meeting one of the group's on the shared edge (so the
       group is joined inside), ordered by how close their height is to the
       group's first room (in whole cells of height, a stable sort after the
       shuffle, so ties stay random): **height-aware grouping**, tall halls
       with tall halls.
     - **Keep groups apart and connected**: a group may not share an edge
       with another group, and needs at least one socket on its boundary
       facing a free cell, so every large area is reached through standard
       rooms or hallways (unless one group covers every occupied cell).
     - A group that cannot start is skipped; when the target is not reached
       the phase restarts with the stream running on, up to
       `LevelGenerator.Attempts`, then refuses (17.3).
  3. **Spanning tree**, main stream, over **units**: a large room is one
     unit, a free cell another. The shared edges between units are shuffled
     and kept as today, except that an edge touching a large room is
     eligible only where that room has a socket. With no large rooms the
     edge list and the draws are today's.
  4. **Roles**, PR 8's role stream, on the unit tree, unchanged.
  5. **Fill**, main stream: today's backtracking over the free cells with
     the standard candidates; the placed large rooms act as neighbours
     already placed (a shared edge has a socket on both sides or neither).
- **Determinism.** A seed, the libraries in order and their rooms in order
  always give the same file, at any thread count; the header gains
  `large share {s}, groups of {g}` only when `-large` is above 0.
- **Budget.** `RoomEdicts` covers every library's rooms; `LevelEdicts` is
  the first library's entities (17.4). `-rooms` reads counts from a
  combined pack or one pack per key.
- **Output.** One library: `library`, as today. Several: `libraries` in
  operand order, cells in their shortest spelling (17.2), `+` for covered
  cells. A bare path among `key=path` operands takes the file's stem as its
  key.

### 17.10 Combining libraries into one pack

A new verb, **`ssmap roompack`** (D28, confirmed from O32):

```
ssmap roompack -out <pack.roompack> <key>=<library.vmf> [<key>=<library.vmf> ...]
               [-only <key>[,<key> ...]] [-incremental [-cache-dir <dir>] | -nocache]
               [-nav-turn0] [-nav-codec <codec>] [stock vbsp options]
ssmap roompack -level <level.yaml> [-out <pack.roompack>] [...]
```

- **Operands.** `key=path`, in the order that decides the singletons; a
  bare path takes its stem as key, refused if the stem is not a key.
  `-level` takes the keys, paths and order from a level file's
  `libraries` (its `library`, for one), and `-out` then defaults to the
  level file with `.roompack`. The game is mounted as for `ssmap room`.
- **What it does.** Split every library first (a broken library fails
  before any compile); check compatibility (17.5); apply the singleton rule
  (17.4), printing its warnings once, here; rename each room to
  `key.room`; compile every room of every namespace in one pool
  (`RoomLibraryCompiler`, `-threads` at once), each with the **first
  library's worldspawn** (D26) and its `MapBase` the qualified name, lower
  cased; write one pack. The level-wide `LENT`, `LOPT` and `SKYB` of the
  pack are the first library's; each namespace keeps its own name keys.
  Rooms are lit (PR 9) under the first library's sun, so the combined
  pack's id and every room's cache key carry that sun (D26: a combined
  pack never holds a sunlit room baked under a dropped sun).
- **Namespacing.** A room of a combined pack is named `key.room` in the
  pack index and in its own definition, so every message names it
  qualified and its room-named files (`materials/maps/<mapbase>/...`, 4.13)
  cannot collide with another library's room of the same name. Separate
  packs do not have that guarantee: two libraries' `corner` rooms both pack
  under `corner`, equal bytes merge and different bytes are refused by
  4.13's conflict rule. `ssmap room -namespace <key>` compiles one library
  as a one-namespace pack with the same names, for a level that keeps
  separate packs.
- **Incremental rebuild.** The pack is rewritten whole and replaced in one
  step, as `ssmap room` does (its index is in front). With `-incremental`,
  every room goes through the room cache (`RoomCompileCache`), whose key
  already covers the room's VMF, the vbsp options, navigation, name keys
  and the game content it read; the injected worldspawn and qualified name
  are part of the room's document, so they are covered too. Rebuilding one
  library then costs its changed rooms' compiles and a copy of the rest.
  **`-only <keys>`** is the fast path: only those namespaces are split and
  compiled; every other namespace's sections are copied byte for byte from
  the existing pack, after checking that its VMF's SHA-256 and the first
  library's singleton digest (17.11) are what that pack recorded, refused
  otherwise (17.3). It trusts the copied namespaces' game content, which is
  why it is opt-in.
- **How `ssmap link` finds rooms.** For each library key of the level:
  - no `-rooms`: the pack beside that library, `<library>.roompack`, as
    today for one library;
  - `-rooms <pack>`: a pack with namespaces (`NSPC`), in which every key of
    the level must be a namespace; a plain pack is accepted only for a
    one-library level, as today;
  - `-rooms <key>=<pack>`, repeatable, overriding one key: a plain pack is
    that library, a pack with namespaces gives the namespace of that key.
  A plain pack's rooms are looked up by bare name, a namespaced pack's by
  `key.room`. A level with `library: x.vmf` and a namespaced `-rooms` pack
  reads as `libraries: {<stem of x>: x.vmf}`. When the recorded source of a
  namespace and the level's path differ in file name, the link warns and
  links (paths move between machines; the namespace is the identity).
  `ssmap layout -rooms` and `ssmap rooms -rooms` take the same forms.
- **The level's pack id.** The map's `ss_pack_id` and the `.nav3d`
  header name one pack, and the level id hashes it (`RoomCompileIds`). A
  one-library level keeps its pack's id, so its ids do not move. A level
  of several packs uses an id derived (`RoomCompileIds.Derive`) from every
  library's key and pack id in library order, so re-packing any library
  changes the level's ids, as re-packing its one library does today.
- **Singletons with a combined pack** come from the level's first key, as
  always; when that is the pack's first namespace (the normal case, and
  always with `-level`) there is nothing to warn about, because the rooms
  were compiled under it.

### 17.11 Pack and file format changes

- **Combined packs: no version change.** The pack layout is `RoomPack`'s,
  version 4 (PR 4 raised it to 2, PR 5 to 3 and Q3 to 4 with `DVIS`; the
  `PROP`, `BMOD`, `TRAN`, `CUBE`, `OVLY`, `LITE`, `APRT` and `SKYB`
  sections of PRs 6 to 13 are optional tags an older build skips). New
  library section **`NSPC`**, an optional known tag in the same way (no
  version bump; PR 10's door-light sections are added the same way on their
  own branch), with the 1.1 framing: per namespace in order, its key, the
  source path as given (relative to the pack, `/` separators), the SHA-256
  of the library VMF's bytes, the SHA-256 of the first library's singletons
  the namespace was compiled under, its first room index and room count
  (rooms are grouped by namespace, in library order within each), and its
  own name keys. An older build skips `NSPC` and reads the rooms under their
  qualified names, which are legal room names, with the pack's `LENT` and
  `LOPT`, which are the level's; it cannot read a `libraries` level (unknown
  key), so nothing links silently wrong.
- **Shaped rooms: a new section and a version for packs that hold one.** A
  room taller or shorter than its cell, or larger than one cell, carries a
  **`SHAP`** section (1.1 framing, rotation count 1): its height, its
  footprint, each socket's offset in socket order, and for a larger room the
  per-cell subtree roots and the block node runs to omit (node indices do
  not change with rotation). A pack holding any shaped room is written at
  the **next pack version** (5: Q3 took 4, and PRs 6 to 13 added only
  optional tags), because an older build would skip `SHAP` and link a tall room as a cube (its top-tree bounds wrong) or
  refuse a larger room's second socket on a face only by luck. A pack of
  cube rooms keeps the current version and its bytes, so no golden digest
  moves. The room container (`SSROOM01`) does not change, so the room cache
  keeps every cube room's entries.
- **Navigation.** A shaped room's navigation section (`NVRr`) raises its
  revision for that room only (taller columns, the footprint's region). The
  `.nav3d` stays version 2 for a level of cubes; a level placing a shaped
  room is written at **version 3**, whose one addition is a per-placed-cell
  voxel height (a `CHGT` section); `docs/nav3d-format.md` 1, 2 and 16 are
  updated in PR 19.
- **Level file**: new keys only (17.2), `+` cells (17.8).

### 17.12 Tests

Rows for 15.2 are in that table; every message of 17.3 is asserted by its
exact text (15.4). Beyond them:

- **Samples.** A multi-library sibling, `samples/rooms-multi`: a second
  library beside the 3x3 one (same cell and kit) with rooms of the same
  names as some of the 3x3's (to exercise bare-name ambiguity, aliases and
  `MapBase` namespacing), two tall rooms, a `2 x 2` hall and a `3 x 1`
  gallery; levels using both libraries at every rotation of the shaped
  rooms, one seeded with `-large`. The 3x3 library stays as it is, so its
  seeded levels keep holding (15.7), and a fact holds `ssmap layout` on it
  byte for byte with and without `-large 0`.
- **Equivalence.** Each multi-library level links to the same map as the
  same level from one library VMF holding both room sets (names aside),
  and a combined pack to the same map as namespaced separate packs; shaped
  rooms against the flattened compile in game-observable terms (traces at
  every lattice point, vis a superset, entities, `.nav3d`).
- **Exactness.** A `W x D` room at every rotation: every moved vertex is
  bit for bit `Apply` of the room's; a 1 × 1 room's `Translate` bits are
  unchanged; a tall room's plug boxes equal a cube room's.
- **Top tree.** Every covered cell descends to its own subtree root; no node
  is reached twice; block nodes are omitted and every index shifted; node
  and solid-leaf bounds enclose the tallest room; a level of cubes has
  today's top tree byte for byte.
- **Generator.** Groups never touch, each reaches a standard room, heights
  cluster (a fact over many seeds), the area stream is never created at
  `-large 0`, and PR 8's role placement is unchanged by it.
- **Combine.** `-only` copies sections byte for byte and refuses a stale
  namespace; the same inputs give the same pack at any thread count (15.5);
  an older reader skips `NSPC` (a pack read with the section removed links
  the same `library` level).
- **Format.** A pack with a shaped room is refused by the version check of
  a build that reads only the old version (the fact writes the header
  directly); a pack of cubes is byte-identical to today's.

### 17.13 Open points

These are also rows of section 14's open table (O24 to O33). The owner
answered five of them on 2026-09-29; they are decisions D24 to D28 now,
and their rows here say what was decided. O25 is built as recommended and
stays open.

| # | Question | Recommended default, or the decision |
| --- | --- | --- |
| O24 | Equal duplicate singletons: a warning each, one summary line, or silence? | **Decided (D24):** one summary line per library (D20 asks for a warning; per-entity lines would warn on every link of libraries sharing a sun). |
| O25 | A singleton only a later library has. | Dropped with a warning: the first library supplies every singleton, never a mix. **Open:** the owner is not sure (2026-09-29); built as this default until the owner revisits it. |
| O26 | Navigation settings across libraries. | **Decided (D25):** the voxel grid is compatibility (refused); step and jump heights, costs and agent presets follow the singleton rule (warned). |
| O27 | Rooms of other libraries compiled under a worldspawn and sun the level drops. | **Decided (D26):** `ssmap roompack` compiles every namespace under the first library's worldspawn and singletons; separate packs link with a warning, except that a sunlit room baked under a dropped sun is refused once the door light (PR 10) lands, and warned until then. |
| O28 | Neighbour names and sides in a room larger than one cell. | Refused at pack time for now; `joined_<socket>` works. A later grammar could name a neighbour by socket (`cxry_<socket>_`). |
| O29 | The mark for a covered cell in the level file. | `+`, with the room in its south-west cell. |
| O30 | Role rooms larger than one cell in `ssmap layout`. | Link accepts them; the generator offers only one-cell role rooms. |
| O31 | Rooms whose floor is not at z = 0, and stacked storeys. | **Decided (D27):** not planned: both need a vertical kit and z splits in the top tree. Storeys are authored inside a tall room. |
| O32 | The combine verb's name. | **Decided (D28):** `ssmap roompack`, with `ssmap room -namespace` for one library. |
| O33 | Scattering tall one-cell rooms outside groups. | Only in groups (`-large`); revisit if levels look too regular. |
