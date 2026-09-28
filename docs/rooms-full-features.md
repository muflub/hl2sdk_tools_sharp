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
11. [Engine limits for `CheckCapacity`](#11-engine-limits-for-checkcapacity)
12. [Implementation order](#12-implementation-order)
13. [Owner decisions](#13-owner-decisions)
14. [Growing the 3x3 sample](#14-growing-the-3x3-sample)

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
| Pack | `RoomPack`, `RoomObjectStore` | One `SSROOM01` container per room (manifest, the BSP's lumps and game lumps byte for byte, vis rows). The pack has typed per-room sections and a library section table, both empty past the container today; readers skip unknown tags. |
| Link | `LevelLinker.LinkAsync` | `RoomLinter.CheckLayout`, `CheckCapacity`, `ValidateJoints`, `RoomLinter.CheckReachable`, `RefusePackedFilesAsync`, `PlanRoom` per room (refusals, transforms), `AssignBases`, door-graph vis (`DoorEdges`, `CloseRows`), `Assemble` (top tree, plug carve, merges), `MergeEntities`, `MergeCollision`. |
| Flatten | `LevelFlattener.Flatten` | The level as one VMF: rooms copied with `VmfPlacement`, joined plugs left out, every `id` renumbered (`LevelFlattener.Renumber`). |

The relocation is exact: a quarter turn permutes and negates components and
the translation is a whole number of cells (`RoomTransform`,
`LevelLinker.TransformPlanes`, `LevelLinker.TransformTexInfos`,
`LevelLinker.ApplyNormal`). Every feature below keeps that property:
positions go through `Apply`, directions through `ApplyNormal`, Euler angles
add `90 × turns` to yaw (exact, because yaw is the outermost rotation), and
nothing multiplies a rotation matrix.

What `PlanRoom` refuses today, in the order it checks:

1. any non-empty lump outside `LevelLinker.CarriedLumps`. Not in the set:
   `WorldLights(Hdr)`, `DispInfo`, `DispVerts`, `DispTris`,
   `DispLightmapAlphas`, `DispLightmapSamplePositions`, `LeafWaterData`,
   `ClipPortalVerts`, `Cubemaps`, `Overlays`, `OverlayFades`,
   `WaterOverlays`, `LeafAmbientIndex(Hdr)`, `LeafAmbientLighting(Hdr)`,
   `LightingHdr`, `FacesHdr`;
2. more than one model, or a world model whose head node is not 0;
3. a leaf with `LeafWaterDataId != -1`;
4. more than two areas or more than one area portal (`RefuseAreaPortals`);
5. any non-zero byte in any game lump (`RefuseGameLumpContent`: static and
   detail props);
6. displacement collision (`RefuseDisplacementCollision`);
7. and, in `LinkAsync`, a pak holding any file (`RefusePackedFilesAsync`).

`Assemble` takes the pak, map flags and game lumps from the first room only,
and the areas from the room with the most (all are `{0, 1}`), because every
other room's are known to be empty or equal (`RequireAgreement`).

---

## 2. Findings: silently wrong today

Things the link or the flatten accepts and gets wrong now. None affects the
3x3 sample, which has no such content, but each should be fixed or refused
before the features that depend on it land. They are the first PR of the
order in [section 12](#12-implementation-order).

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
   [4.1](#41-brush-entities).
10. **Real game content makes every room unlinkable.** With a game whose sky
    VTFs resolve, `DefaultCubemapBuilder.CreateAsync` writes
    `materials/maps/<room>/cubemapdefault.vtf` (and `.hdr.vtf`) into every
    room's pak, and `RefusePackedFilesAsync` refuses it. The 3x3 sample
    ships no textures, so its builder warns and writes nothing. Not silently
    wrong, but it blocks any real library; packed files
    ([4.13](#413-packed-files)) fix it.

---

## 3. Feature matrix

"Today" is what the link does. "Pack" is what can be precomputed per room
and per rotation; "Link" is what has to wait for the level. "Entities" is
the runtime entity cost ([section 6](#6-the-entity-budget)). Size is the
work to carry the feature: S (days), M (a week or two), L (several weeks,
or research).

| Feature | Today | Pack (per room / rotation) | Link | Entities | Size |
| --- | --- | --- | --- | --- | --- |
| Point entities | carried (origin, yaw); names duplicated | name and I/O positions, parsed placeholders | resolve names, drop/keep, fold, singletons | 1 each; logic may fold to 0 | M |
| Brush entities | refused (`models != 1`) | models, subtrees, per-model collision, origin class | rebase models, `model` keys, texinfo split for origin models | 1 each | L |
| `func_ladder` | silently wrong (`info_ladder` bounds) | bounds per rotation | none | 1 (`info_ladder`) | S |
| Static props | refused (game lump) | props per rotation, dictionary, hulls, lighting | merge dictionary, recompute leaf lists, rename `.vhv` | 0 | M |
| Detail props | refused (game lump) | props per rotation, leaf-local runs, lighting | renumber leaves, re-sort, merge dictionaries | 0 | M |
| Displacements | refused at split (`VmfPlacement.MoveSide`) | lumps per rotation, collision, sample positions | rebase; cross-room neighbours only if allowed | 0 | L |
| Water | refused (water leaf, lump) | water data, fog ids, patched materials, fluid collision | doorway water carve, distance to water | 0 (1 `water_lod_control` per level) | L |
| Overlays | refused (`Overlays` lump); split misplaces them | overlays per rotation | rebase faces, texinfos, ids, fades | 0 unnamed, 1 named | M |
| Decals (`infodecal`) | carried | nothing | nothing | 1 each (**uncertain** after spawn) | S |
| `env_cubemap` | refused (`Cubemaps` lump, pak) | samples per rotation, patch list | rename VTFs and patched VMTs to the level | 0 | M-L |
| Area portals | refused | areas, portals, clip verts | area union across joints, optional door portals | 1 per portal | L |
| Occluders | carried; `occludernumber` wrong | occluders per rotation | rebase the key | 1 each (strip candidate) | S |
| Packed files | refused | the room's pak entries | merge, dedupe, rename | 0 | M |
| 2D sky | faces carried; no leaf sky flags (no vrad) | sky leaves per room | propagate sky flags across doors | 0 | S |
| 3D skybox | not possible (areas collapsed) | the skybox as a library section | place it, its own area | 1 `sky_camera` per level | M |
| Navigation (3D) and points of interest | none | volumes and door portals per rotation; POIs | stitch at joined doors | 0 (POIs stripped) | L, blocked (section 10) |
| Lighting | none (no vrad at pack time) | base ×4, doorway capture, door response | sum captures × responses | lights: see 6.3 | L |

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

**Today.** Refused: `PlanRoom` stops at `models.Length != 1`. The split and
the flatten carry brush entities, turning their brushes and (finding 9)
their `angles`.

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
level (6.5). Per rotation: the moved placement keys. At link: name
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

**Today.** Refused by `RefuseGameLumpContent`.

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

**Lighting.** Base per-vertex lighting per rotation; no capture (props do
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

**Lighting.** Per-prop base lighting per rotation; door response entries
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

**Pack vs link.** Per rotation: all displacement lumps, moved. At link:
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

**Today.** Refused (`Overlays` lump), and misplaced at the split (finding 4)
and in the flatten (findings 3 and 4).

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

**Today.** Refused (`Cubemaps` lump; patched materials and VTFs in the pak).

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

**Limits.** `MAX_MAP_CUBEMAPSAMPLES` (1024 in the SDK; not in this repo's
tables); texdata ≤ 2048 and texinfo ≤ 12,288 (`BspLimits.Caps`), which
cubemap patches reach first.

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

**Today.** Refused (`RefuseAreaPortals`); every room is area 1 and the level
is one area (`Assemble`). The linker's own remarks give the reason: two
areas with no portal between them are two worlds to the server.

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

**Today.** 2D sky faces carried; no leaf has sky flags (no vrad). A 3D skybox
cannot exist: it needs its own area and is outside every cell.

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
default cubemaps (finding 10).

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
| `func_viscluster` | The loader records it (`MapFile.VisClusterEntities`), but nothing in the repo sets `PortalFileBuilder.VisClusters`, so it has no effect on vis here, and the entity keeps its brushes, gets a model and makes the linker refuse the room (`models != 1`). Plan: wire the resolver (a vbsp matter), then strip the entity and its model at link (compile-only, 0 entities). |
| `func_ladder` | Silently wrong (finding 1); move `mins.*` / `maxs.*` through `MoveBox`. **S.** |
| `func_instance` inside a room | Merged at pack time (`MapInstanceMerger`; `MapFileReader.CheckForInstances` blanks every `func_instance`), no name fixup (`VBSP0107`); its contents are room entities and follow section 5. |
| Cordons | Refuse a cordoned library (a room compile would cut the room). **S.** |
| `info_no_dynamic_shadow`, `%compile*` flags, surface props | Texinfo and contents: carried; the entity is consumed. |
| Macro textures | `FaceMacroTextureInfo` carried (`Assemble`). |
| Vertex normals, primitives | Carried; vrad rewrites vertex normals (`RadLumpWriter.Write`), so under option C they come from the base bake. |
| `WorldLights(Hdr)` | Per room, the lights moved per rotation; at link concatenated with each `Cluster` rebased by the room's cluster base, and one sky light and sky ambient for the level (D3). `MAX_MAP_WORLDLIGHTS` 8192 (`WorldLightExporter`). **S.** |
| Leaf ambient | Samples per leaf (a compressed cube and a position in the leaf box). Per rotation from the base bake, rebased by leaf, plus door response. Under a turn the position bytes permute with the box axes and the cube's horizontal faces permute. The carved doorway leaf copies its facing leaf's samples. `DLeafAmbientIndex.FirstAmbientSample` is `ushort`. **M**, with lighting. |
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

```
local-name = "c" "x" [ offset ] "r" "y" [ offset ] "_" rest
offset     = ( "+" / "-" ) "1"
rest       = one or more characters, kept as written
```

- Only `+1` and `-1`; no spaces, no `+0`, no `+2`.
- **Lower case only**: `cxry_`, `cx+1ry-1_`. The resolved prefix is lower
  case too (`c3r5_`).
- **Case.** Engine and game name matching is widely case-insensitive
  (**uncertain** as a blanket rule: vbsp's own light-style grouping is
  `strcmp`, case-sensitive, `EntityStage.SetLightStyles`). So a case variant
  (`CXRY_door`, `cXry_door`) is not quietly global: the pack refuses it as
  malformed (O2). `rest` keeps its case.
- The placeholder counts only at the **start** of a name. `door_cxry` or
  `my_cxry_door` is an ordinary global name; the pack warns (it looks like a
  misplaced placeholder) and leaves it.

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

- **Reserved form.** No global name may look like a resolved one
  (`^c[0-9]+r[0-9]+_`, case-insensitive): the pack refuses it, naming the
  entity and key. A resolved name then cannot equal a global one; the link
  asserts it anyway.
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
  pack time: malformed tokens (`cx+2ry_`, `cx+ ry_`, `cxr_`, case variants),
  an offset beyond ±1, a global name in the reserved form, a placeholder
  after the start of a value (warning), a local reference no entity defines
  (warning), an unknown `room_needs` direction, `room_needs` on a light or
  on a shadow-casting prop.

### 5.11 Tests

One feature, one PR, with facts for each mechanism and every rotation:

- Grammar: each malformed form refused with its message; case variants.
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
| `func_viscluster` | kept, with a model | `MapFileLoader` | certain it is kept (see 4.14) |
| `light`, `light_spot`, `light_environment`, `light_dynamic` | kept; vrad reads them and does not strip them | `EntityStage`, `DirectLightBuilder` | certain they are kept |
| default `water_lod_control` | added when water has none | `EntityStage` | certain |

### 6.4 What the link can strip

| Candidate | Strip? | Confidence |
| --- | --- | --- |
| `info_room` | already absent | certain |
| `room_needs`, `room_socket`, `socket_priority` keys | always (keys, not entities) | certain |
| `func_viscluster` (once wired) | yes: compile-only | certain it has no runtime use in the tools; **uncertain** whether a game defines the class |
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

### 6.9 Authoring guidance

For the rooms README once this lands.

**Free:** world brushes; `func_detail`; tool-textured clips and skip/hint;
`prop_static`; detail props and `%detailtype` materials; displacements;
unnamed overlays; `env_cubemap`; `info_lighting`; water brushes (world);
local names and neighbour references; `room_needs`; points of interest
(`info_poi`, compiled into the navigation data and stripped, 10.6).

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

---

## 8. Level-wide singletons

| Entity or key | Today | Plan |
| --- | --- | --- |
| `worldspawn` keys | Rooms must agree (`RequireSameWorld`); the split copies the library's worldspawn into every room, so they do. | Keep. `world_mins`/`world_maxs` stay the union (`MergeEntities`). |
| `light_environment` | Carried per room and turned (finding 5). | **Decided (D3):** library-wide. The library holds one, outside every cell (the split collects it instead of ignoring it). A room that carries one is refused at pack time unless its keys equal the library's, in which case it is dropped from the room. Never turned. |
| Sky settings (`skyname`, the sun's sky colours) | `skyname` agrees through worldspawn. | Library-wide with the sun (D3). |
| `sky_camera` | Room-local if present. | Only in the library's skybox (4.12); refused in rooms. |
| `env_fog_controller`, `env_tonemap_controller`, `shadow_control`, `postprocess_controller` | Carried, one per room that has one; the game takes the first or a master (**uncertain** per class). | Library-wide like the sun: collected from the gaps; a room copy refused unless equal. Per-room fog uses a trigger and a named controller, as a normal map does. |
| `water_lod_control` | vbsp adds one per room with water. | Keep the first; drop equal duplicates; refuse different ones. |
| `info_player_start` | Carried, one per room with one (the sample's end rooms). | Keep all by default, as a whole-map compile would (the game picks); a level YAML key may name the start room (O12). |
| Switched light styles | Collide (finding 8). | Renumber at link: each distinct resolved light name gets one style from 32 (a global name shares one across placements); face styles, detail prop styles, world lights and entity `style` keys remapped. ≤ 32 switched names (`WriteLimits.MaxSwitchedLights`), 64 styles in all (`RayAmbientLighting.MaxLightStyles`). |

---

## 9. Lighting (option C, as decided)

The owner settled the design (D2); it replaces the earlier "re-light faces
near joined doors at link" fix-up. Nothing here traces a ray or reads a game
file at link.

### 9.1 The four parts

1. **Base lighting.** A doors-closed vrad bake per room and per rotation
   (×4): lightmaps and bump pages, leaf ambient, static prop per-vertex
   lighting, displacement lightmaps, detail prop lighting, vertex normals,
   leaf sky flags (pass one), world lights. The room is sealed by its plugs
   exactly as compiled (`RoomCompiler`), so this is a normal vrad run of the
   room with the library's sun turned into the room's frame.
2. **Doorway capture.** A doors-open bake of the room into a black, fully
   absorbing box, one per rotation: plugs removed, the room surrounded by a
   box that reflects nothing, so only the room's own light leaves. Recorded
   per door opening:
   - **direct:** which lights (and the sun through the room's own sky
     openings) shine through which parts of the opening, as a small
     visibility grid on the door plane per light;
   - **bounce:** directional radiance per door sample (an ambient cube per
     sample), per light style.
3. **Door response** ("option b"). Per door and per rotation, the room's
   response to light **entering** through that door: a coarse basis on the
   opening (for example 2×4 patches × 6 directions = 48 functions), each
   mapped to the change it makes in the room's lightmaps, leaf ambient,
   static prop vertices, displacement and detail prop lighting, with
   receivers that get almost nothing pruned.
4. **Final bake at link.** For each joined door, in both directions: the
   neighbour's outgoing capture, projected onto this room's door basis,
   times this room's response, added to this room's base, per style. No ray
   tracing, no game files.

**Reach** (D3): only the adjacent room. Light never crosses two doors.
**Sun** (D3): one for the library; every room shares it.

A note for the prototype, not a change to the decision: the response is
geometry and reflectivity only and the sun does not enter it, so in the
room's frame it should agree across the four rotations up to float noise;
if the prototype confirms that, one response set per door serves all four
(a quarter of the response storage). Likewise a room with no sky opening has
no sun term, and its four base bakes should agree.

### 9.2 Why per rotation

The sun is fixed in the world, so in a turned room it comes from another
direction; vrad's sky-ambient sampling directions are fixed in world space
too. Point, spot and texture lights turn with the room. Baking in the room's
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

| Feature | Base (×4) | Capture | Response |
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
(bump pages ×4 on bumped faces), `S` styles, `D` doors, `B` basis functions
(48), `p` the fraction of receivers a basis function reaches after pruning,
`V` prop vertices and `A` ambient samples:

- base: `4 × (L + V) × S × 6 bytes` (half-float RGB) plus ambient
  `4 × A × 6 faces × 6 bytes`;
- capture: per door, per rotation, per style, `G` grid cells (8×16 = 128) ×
  (a bit per light + a 36-byte cube) ≈ 5 KB;
- response: per door `B × p × (L + V + 6A) × 10 bytes` (value plus receiver
  index), ×4 if kept per rotation.

For `L = 20,000`, one style, four doors, `p = 0.3`, no props: base ≈ 0.5 MB,
capture ≈ 80 KB, response ≈ 11.5 MB per rotation set (46 MB for four). The
response dominates, so its basis size, pruning threshold and resolution
(it is smooth; half the luxel resolution may do) are what the prototype must
choose.

Pack-time cost: four base bakes, four capture bakes, `D × B` response solves.
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
tolerance (door terms must not invent light).

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

- **A per-room, per-rotation navigation volume, precomputed into the pack.**
  Built at pack time from the room's compile, stored as a per-room section
  like every other precompute (D1), so the link needs no game files and no
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
clearance unchanged; a non-square hull needs per-rotation data, which the
pack stores anyway (×4).

### 10.4 How the link emits it

Two choices, for the owner:

- **Packed into the map's pak** (as `.vhv` files are, 4.13), under a fixed
  name such as `maps/<level>.nav3d`: it ships with the `.bsp`, and servers
  and clients that download the map get it. Recommended, unless the data is
  large enough that the pak's size matters.
- **A sidecar** next to the `.bsp` (`<level>.nav3d`): simpler to inspect,
  but it must be distributed with the map.

Either way, the linked file is the rooms' data relocated (index bases, as
for every other lump) plus the joined portals, written in one pass.

### 10.5 Open questions for the owner

1. **Representation**: voxels, a sparse voxel octree, convex volumes, a
   layered 3D mesh, or something else. It decides storage, rotation
   exactness and stitching.
2. **Agent sizes**: which hulls (walking, climbing, flying) and their
   dimensions; clearance is per agent size.
3. **Movement model**: what counts as traversable (climbable surfaces,
   ladders, water, jump links), and whether traversal costs are baked.
4. **File format and versioning**, and pak or sidecar.
5. **Dynamic obstacles**: which entities the runtime treats as blockers
   (doors, `func_brush`, props), and how links are toggled.
6. **Resolution against size**: the budget per room in the pack and per
   level on disk.

Size once decided: **L**; risk high until the representation is chosen.

### 10.6 Points of interest

Cover, vantage, spawn, patrol and interaction points belong to the
navigation data, not to runtime entities (D12). Authors place `info_poi`
point entities (the class name and keys are part of the mod contract,
section 7, once the AI design settles) in rooms; `ssmap room` compiles them
into the room's navigation section and **strips them from the entity
lump**, so they cost zero runtime entities (6.9).

Per POI the pack stores: its type, position and orientation (turned per
rotation like any point entity: `Apply` and yaw + 90 × turns), its keys,
and its name through the placeholder grammar (section 5), so a room's POI
can be named, and referenced by a neighbour, the same way as an entity. At
link the POIs are relocated with the rest of the navigation data. Storing
and stripping them does not depend on the navigation representation, so it
can land before the rest of this section (it is part of PR 2 in section
12); until navigation exists the link can write them to the same pak entry
or sidecar the navigation data will use.

---

## 11. Engine limits for `CheckCapacity`

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
| Cubemaps | samples; patched texdata and texinfo | 1024 samples (SDK); texdata 2048, texinfo 12,288 |
| Area portals | areas; portals ×2; clip verts | 256; 1024; `ushort` start, 128,000 (`WriteLimits`) |
| Lighting | lighting bytes; world lights; switched styles; styles per face; ambient samples | `MAX_MAP_LIGHTING` (SDK); 8192 (`WorldLightExporter`); 32 (`WriteLimits.MaxSwitchedLights`); 4 per face; 65,535 (`DLeafAmbientIndex`) |
| Pak | bytes | none beyond 32-bit lump offsets |

---

## 12. Implementation order

One PR per feature or small group. Already queued, and assumed:

- **Q1, link profile** (in progress in another agent): per-room and
  per-rotation work moved into the pack. Every "per rotation" item above is a
  Q1-style pack section.
- **Q2, library-wide shared tables** for planes, texdata and texinfo.
- **Q3, door-to-door visibility** for the linked PVS (line of sight through
  doorways, precomputed per room).
- **Q4, option C lighting** (section 9).

| # | PR | Size | Depends on | Why here |
| --- | --- | --- | --- | --- |
| 1 | **Correctness fixes**: `info_ladder` bounds, `occludernumber` rebase, flattener keeps side-id references, overlay basis keys moved by split and flatten, `light_environment` never turned, library-wide entities collected from the gaps, refusal of non-zero `angles` on unknown brush-entity classes in split and flatten. | S | none | Each fix is a fact that fails today; later work builds on correct transforms. |
| 2 | **Entity budget**: the class table (compile-only rows certain, default `edict`), per-room entity section, edict and entity totals in `CheckCapacity` with reserve, warnings, refusal and headroom report, `ssmap rooms` counts, `ssmap layout` budget, stripping of certain compile-only entities, the points-of-interest section (store and strip `info_poi`, 10.6). | M | Q1 | D7 makes it a top priority, and every later feature reports its cost through it. |
| 3 | **Naming and neighbour logic, one feature**: `cxry_` resolution, the rotation table, (a), (b) injected only when referenced, (c) for point entities and static-prop conditions, folding (relays, constant branches, `logic_auto` merge, filters), `-mod-entities` with `logic_room` and its stock fallback, the `SourceSharp.RoomContracts` assembly (7.5), the `RoomLinter` rule, `ssmap rooms` listing, one resolver shared by link and flatten. Facts for each mechanism at all four rotations (5.11). | M-L | 2, Q1 | Pure text and immediately useful (repeated rooms with logic), and it is the main lever on the entity budget. (c) on a brush entity cannot arise until #7 links brush entities; #7 adds model omission. |
| 4 | **Singletons and the library section** (section 8), with D3's refusal at pack time. | S-M | Q1, 1 | The sun section is Q4's input. |
| 5 | **Packed files**. | M | Q1 | Unblocks real content (finding 10); prerequisite of 6, 10, 11. |
| 6 | **Static props** (zero-entity models). | M | 5 | High value, contained, and the cheap alternative to `prop_dynamic` under the budget. |
| 7 | **Brush entities**, origin-relative models, per-model collision, socket furniture, (c) model omission. | L | Q2, 3 | Doors and triggers; the biggest structural change, after the cheaper wins. |
| 8 | **Q4 base bake** and **2D sky** flags. | L | 4, 6, 7 | The base bake must include props and brush entities; exact for capped rooms. |
| 9 | **Q4 capture and response**, driven by the prototype (9.7). | L | 8 | Research: choose the basis from measurements. |
| 10 | **Overlays**. | M | 5, Q2 | Visual, contained. |
| 11 | **Cubemaps**. | M-L | 5, Q2 | Needs the pak; Q2 eases texdata. |
| 12 | **Area portals and areas**, then **3D skybox**. | L, M | Q3, 7 | Door visibility and portals both describe what a doorway lets through. |
| 13 | **Water**, first without water sockets, then with. | M, L | 12, 7 | Hardest cross-room case; safe refusal meanwhile. |
| 14 | **Displacements**, no cross-room stitching. | L | 8 | Many lumps; lighting is a large part. |
| 15 | **Detail props**. | M | 14, 8 | Depends on both; statistical equivalence. |

Reasoning: correctness first (cheap, each a failing fact today); then the
budget and the naming and logic feature, because the owner ranks entity
count first and every later feature is measured against it; then the other
text-only work (singletons, pak); then features by value against risk.
Brush entities before lighting because the base bake must include them.
Navigation (section 10) is not scheduled: it waits for the AI design; only
its points-of-interest store (PR 2) is independent of it.
Areas after Q3. Water and displacements late: their cross-room cases are the
hardest and their refusals are safe meanwhile.

---

## 13. Owner decisions

### Decided

| # | Decision |
| --- | --- |
| D1 | As much as possible at pack time, stored in the `.roompack`; link needs nothing but the pack, no game files. Per-rotation precompute (×4) is expected. |
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

### Open, with recommended defaults

| # | Question | Recommended default |
| --- | --- | --- |
| O1 | Neighbour-flag diagonals and joined variants. | Offer both (`cxry_has_northeast`, `cxry_joined_east`), opt-in, so free unless used. |
| O2 | Placeholder case variants (`CXRY_`). | Refuse as malformed at pack time. |
| O3 | Which keys get placeholders resolved. | Built-in name-key table, every output target, output parameters wholly a placeholder, and any key whose whole value is a placeholder; the library may extend the table. |
| O4 | A global `targetname` in a room placed more than once. | Warn at link, naming the cells. |
| O5 | Socket furniture. | `room_socket` key; at a joint keep the earlier room's (link order), `socket_priority` overrides; drop at a cap. |
| O6 | Props whose hull leaves the cell. | Refuse at pack time, except socket furniture inside the plug box. |
| O7 | Water touching a socket. | Refuse first; kit water levels later. |
| O8 | Displacements meeting at a joint. | Refuse a displacement edge on a plug box. |
| O9 | Cubemap assignment near doors. | Each room's faces use its own cubemaps; the map name is fixed at link. |
| O10 | Area portals at joints. | Joints open, areas unioned; author portals only; door portals opt-in per kit (1 entity each). |
| O11 | 3D skybox. | A library skybox room (`info_room_skybox`), placed below the grid, its own area. |
| O12 | Several `info_player_start`. | Keep all; a level YAML key may name the start room. |
| O13 | Texel-lit static props. | Refuse until vrad supports them. |
| O14 | Response storage. | Let the prototype choose; one response set per door if the rotations agree. |
| O15 | Brush entities with non-zero `angles`. | Refuse unless the class is in a known-direction table. |
| O16 | Where the shared C# contract types live (7.5). | A new dependency-free assembly, `SourceSharp.RoomContracts`, held to the library rules. |
| O17 | Entity reserve. | 512 (budget 1536), library key `rooms_entity_reserve`, link option `-entity-reserve`; the mod should measure its peak and set it. |
| O18 | Stripping unnamed lights after baking. | Opt-in until checked in game; then default on. |
| O19 | Relay folding. | On by default; a library option turns it off (same-tick event order can change). |

---

## 14. Growing the 3x3 sample

The sample is generated (`SourceSharp.MapGen.Rooms`: `Rooms3x3Kit`,
`Rooms3x3Sample`, `Rooms3x3Arrangement`, `Rooms3x3Permutations`;
`tools/RoomsSample`) and checked in; `Rooms3x3EquivalenceTests` runs every
criterion over 16 levels. It grows **one feature per PR**, in the order of
section 12, each with its criterion added to `Rooms3x3EquivalenceTests` and
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
- **The naming rotation level** (5.11) is its own small level: one room with
  references in all eight directions at the centre, at each rotation, a
  distinct room in every neighbour cell, and a corner variant for (a).
- **Sweep.** `SSMAP_ROOMS3X3_SWEEP` keeps working; lighting criteria are
  optional in the sweep (vrad per arrangement is expensive), gated by their
  own variable.
- **Stress.** `RoomsStressLibrary` varies the new features too, so the
  capacity and entity-budget checks are exercised by a 16×16 level.
