# The `.nav3d` level navigation file

`ssmap link` writes `<map>.nav3d` beside every linked `<map>.bsp`: a 3D
navigation of the level's free space, for agents that walk, climb and fly.
This document is the file's specification. It is written so a reader can be
built from it alone, in any language; the C# reader in
`SourceSharp.MapFormats` (`Nav3dReader`) is one such reader, and the facts
check the two against each other.

Contents:

1. [What the file describes](#1-what-the-file-describes)
2. [Coordinates and conventions](#2-coordinates-and-conventions)
3. [Layout](#3-layout): envelope, header, section directory
4. [Sections](#4-sections): every record, byte by byte
5. [Octree nodes and point lookup](#5-octree-nodes-and-point-lookup)
6. [Leaves, flags and neighbours](#6-leaves-flags-and-neighbours)
7. [Points of interest, the spawn and arrivals](#7-points-of-interest-the-spawn-and-arrivals)
8. [Ids: tying the navigation to its map](#8-ids-tying-the-navigation-to-its-map)
9. [How a reader walks it](#9-how-a-reader-walks-it) (C++-style pseudocode)
10. [Using the reader from the mod (C#)](#10-using-the-reader-from-the-mod-c)
11. [A worked example](#11-a-worked-example)
12. [Versioning](#12-versioning)
13. [Where the data comes from](#13-where-the-data-comes-from): configuration, room pack sections, seams
14. [Measurements and the choices they made](#14-measurements-and-the-choices-they-made)

---

## 1. What the file describes

A level is a grid of cubic **cells**, one room per cell (some cells empty).
Each cell is split into **voxels**, `cellVoxels` along each edge (16 by
default: a 256-unit cell, 16-unit voxels).

For each **agent** (a box size and the contents it collides with), and each
placed cell, the file holds a **sparse voxel octree** of that cell. An octree
node is either split into eight children, or is uniform: **free**, **blocked**
or **outside** the cell. A uniform free node is a **leaf**: a cube of voxels
the agent is free in everywhere, with the same **contact flags** everywhere.
Open space far from surfaces merges into large leaves; space next to a floor,
wall or ceiling stays at the voxel size, where its flags differ.

**Free means the agent fits.** A voxel is free when the agent's box, placed
with its origin *anywhere* in the voxel, overlaps no solid it collides with:
the solids grown by the agent's box (a Minkowski sum), tested exactly. So a
free leaf is a guarantee, not a likelihood.

Across leaves the file holds **adjacency** (every pair of leaves that share
part of a face, including pairs in two rooms joined through a door),
**connected components**, the level's **doors**, and **points of interest**.

The file does not choose how agents move. A walker restricts itself to leaves
with the `Floor` flag; a climber to `Floor`, `Wall` and `Ceiling` leaves; a
flyer uses any free leaf. The graph is the same for all three.

## 2. Coordinates and conventions

- **Units** are Source units (inches). **Z is up.** Rotation is
  counter-clockwise seen from above.
- **Endianness**: every multi-byte value is **little-endian**. Floats are IEEE
  754 binary32. Ids are 16 bytes in RFC 9562 (big-endian, "network") order.
- **The grid**: `columns` cells west to east (+x), `rows` south to north
  (+y). Cell `(column, row)` has index `row × columns + column` and spans
  `origin + (column × cellSize, row × cellSize, 0)` to that plus `cellSize` on
  every axis. The linker's origin is `(0, 0, 0)`.
- **Voxels**: `voxelSize = cellSize / cellVoxels`. A **level voxel coordinate**
  counts voxels from the origin: voxel `(X, Y, Z)` spans
  `origin + (X, Y, Z) × voxelSize` to that plus `voxelSize`. Cell `c`'s voxels
  are `X` in `[column × cellVoxels, (column + 1) × cellVoxels)`, likewise `Y`,
  and `Z` in `[0, cellVoxels)`.
- **Half-open**: a point on a voxel boundary belongs to the voxel above it on
  each axis: `X = floor((p.x − origin.x) / voxelSize)`.
- **An agent's origin** is the point a leaf is free for. The agents
  `ssmap` builds by default stand on their origin: the box runs from
  `(−w/2, −w/2, 0)` to `(w/2, w/2, h)`, as a Source player's or NPC's does, so
  a `Floor` leaf's bottom is where the feet stand.
- **Directions** (doors, side flags): 0 east (+x), 1 north (+y), 2 west (−x),
  3 south (−y). Yaw is degrees counter-clockwise from +x, `[0, 360)`.
- **Strings** are offsets into the string table; `0xFFFFFFFF` means none.

## 3. Layout

```
envelope (24 bytes, never compressed)
stored image (raw, or compressed by the envelope's codec)
    header (headerBytes, 112 in version 1)
    section directory (sectionCount × 16 bytes)
    sections (each starting on a 4-byte boundary, zero padding between)
```

### 3.1 Envelope

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `char[8]` | magic `SSNAV3D\0` (`53 53 4E 41 56 33 44 00`) |
| 8 | `int32` | version, **1** |
| 12 | `uint8` | codec: 0 none, 1 Deflate (raw RFC 1951), 2 Brotli (RFC 7932) |
| 13 | `uint8[3]` | zero |
| 16 | `int32` | `imageLength`: the image's length after decoding |
| 20 | `int32` | `storedLength`: bytes that follow the envelope; the file is exactly `24 + storedLength` bytes |

With codec 0 the stored bytes are the image (`storedLength == imageLength`),
and a reader may use them in place. Otherwise the reader decodes them into a
buffer of `imageLength` bytes, once, at load; every offset below is from the
start of the **image**.

### 3.2 Header (image offset 0)

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `int32` | `headerBytes`: the header's size; 112 in version 1. The directory starts here. |
| 4 | `int32` | `sectionCount` |
| 8 | `int32` | `agentCount`, 0 to 32 |
| 12 | `float32` | `cellSize` |
| 16 | `float32` | `voxelSize` |
| 20 | `int32` | `cellVoxels` |
| 24 | `int32` | `octreeDepth`: the least D with 2^D ≥ `cellVoxels` |
| 28 | `int32` | `columns` |
| 32 | `int32` | `rows` |
| 36 | `float32[3]` | `origin` |
| 48 | `float32` | `floorNormalZ`: a surface with normal z at least this is a floor (0.7 by default) |
| 52 | `int32` | `poiCount` |
| 56 | `int32` | `doorCount` |
| 60 | `int32` | `spawnPoi`, or −1 |
| 64 | `int32` | `upArrivalPoi`, or −1 |
| 68 | `int32` | `downArrivalPoi`, or −1 |
| 72 | `byte[16]` | `levelId` (section 8) |
| 88 | `byte[16]` | `packId` (section 8) |
| 104 | `byte[8]` | zero, reserved |

### 3.3 Section directory

`sectionCount` entries of 16 bytes, at image offset `headerBytes`:

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `char[4]` | tag, ASCII |
| 4 | `uint32` | index: `0xFFFFFFFF` for a level section, else the agent the section belongs to |
| 8 | `uint32` | offset of the section, from the image's start; a multiple of 4 |
| 12 | `uint32` | length in bytes |

A reader looks sections up by `(tag, index)` and **ignores tags it does not
know**. Each `(tag, index)` appears at most once.

## 4. Sections

Level sections (index `0xFFFFFFFF`):

| Tag | Record | Count |
| --- | --- | --- |
| `STRS` | bytes | string table: NUL-terminated UTF-8 strings; offset 0 is the empty string |
| `AGNT` | 32 bytes | `agentCount` |
| `CELL` | 8 bytes | `columns × rows` |
| `DOOR` | 16 bytes | `doorCount` |
| `POIS` | 48 bytes | `poiCount` |

Agent sections (index = agent):

| Tag | Record | Count |
| --- | --- | --- |
| `ROOT` | `int32` | one per cell: the cell's root node, −1 for an empty cell |
| `NODE` | `uint32` | the octree nodes of every cell, back to back (section 5) |
| `LEAF` | 16 bytes | the free leaves |
| `ADJS` | `uint32` | `leafCount + 1`: where each leaf's neighbours start in `ADJN` |
| `ADJN` | `uint32` | the neighbour lists (section 6) |
| `LINK` | 12 bytes | leaf pairs joined through doors |
| `COMP` | 8 bytes | connected components |
| `POIL` | `int32` | one per point of interest: its leaf for this agent, or −1 |

**`AGNT`** (per agent)

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `uint32` | name (string) |
| 4 | `float32[3]` | box mins, relative to the agent's origin |
| 16 | `float32[3]` | box maxs |
| 28 | `int32` | contents mask: the `CONTENTS_*` bits the agent collides with (player `0x0201400B`, NPC `0x0202400B`) |

**`CELL`** (per cell, `row × columns + column`)

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `uint32` | room name (string), `0xFFFFFFFF` for an empty cell |
| 4 | `uint8` | rotation: quarter turns counter-clockwise |
| 5 | `uint8` | role: 0 none, 1 up, 2 down |
| 6 | `uint8` | joined doors: bit d set for direction d |
| 7 | `uint8` | capped doors: bit d set for direction d |

**`DOOR`** (per socket of a placed room; a joined door appears once for each side)

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `uint32` | cell |
| 4 | `uint8` | direction it faces (0 E, 1 N, 2 W, 3 S) |
| 5 | `uint8` | 1 joined, 0 capped |
| 6 | `uint16` | zero |
| 8 | `uint32` | socket name (string) |
| 12 | `int32` | the facing door's index when joined, else −1 |

**`LEAF`** (per free leaf)

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `uint16[3]` | the leaf's low corner, in level voxel coordinates |
| 6 | `uint8` | `sizeLog2`: the leaf is `2^sizeLog2` voxels on a side |
| 7 | `uint8` | flags (section 6) |
| 8 | `uint32` | component |
| 12 | `uint32` | cell |

**`LINK`** (per door link): `uint32` leaf A, `uint32` leaf B, `uint32` door
(the `DOOR` record on A's side).

**`COMP`** (per component): `uint32` leaves in it, `uint32` voxels in it
(volume = voxels × voxelSize³).

**`POIS`** (per point)

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `float32[3]` | position, level coordinates |
| 12 | `float32` | yaw, degrees |
| 16 | `float32` | radius, 0 for none |
| 20 | `uint32` | type (string) |
| 24 | `uint32` | tags (string, comma-separated as authored) |
| 28 | `uint32` | name (string, room-local names resolved), or `0xFFFFFFFF` |
| 32 | `uint32` | cell |
| 36 | `uint32` | agent mask: bit a set when the point applies to agent a |
| 40 | `int32` | door record for a door point, else −1 |
| 44 | `uint16` | flags: 1 has facing, 2 door point, 4 joined (door point on a joined door), 8 arrival |
| 46 | `uint8` | role of the room it is in: 0 none, 1 up, 2 down |
| 47 | `uint8` | zero |

## 5. Octree nodes and point lookup

A node is one `uint32`. Its top two bits are its kind; the low 30 its payload.

| Kind | Bits 31-30 | Payload |
| --- | --- | --- |
| inner | `00` | index of the first of its **eight children**, stored back to back in `NODE` |
| free | `01` | the leaf's index in `LEAF` |
| blocked | `10` | zero |
| outside | `11` | zero: the cube lies beyond the cell (only when `cellVoxels` is not a power of two) |

A cell's root (`ROOT[cell]`) covers `2^octreeDepth` voxels on a side from the
cell's low corner. An inner node of edge `s` has children of edge `s/2`,
ordered by **octant**: bit 0 set for the upper half along x, bit 1 along y,
bit 2 along z. Child `c` of a node at `(x0, y0, z0)` sits at
`(x0 + (c & 1) × s/2, y0 + ((c >> 1) & 1) × s/2, z0 + ((c >> 2) & 1) × s/2)`.
Children always follow their parent in the array, so a walk from a root
never loops; a lookup descends at most `octreeDepth` levels.

**Point lookup** (`FindLeaf`): compute the level voxel `(X, Y, Z)` of the
point; the cell is `(X / cellVoxels, Y / cellVoxels)`; outside the grid, or in
a cell whose root is −1, there is no leaf. Descend from the root with the
voxel's in-cell coordinates, choosing the child whose octant holds them,
until the node is not inner. A free node gives the leaf; any other kind
means the agent does not fit there.

## 6. Leaves, flags and neighbours

**Flags** (`LEAF` byte 7):

| Bit | Name | Meaning |
| --- | --- | --- |
| 0 | `Floor` | something walkable is directly under the agent: a surface whose normal z ≥ `floorNormalZ` |
| 1 | `Wall` | a surface too steep to walk on touches the agent: a wall, or a slope past the limit |
| 2 | `Ceiling` | a surface facing down (normal z ≤ −`floorNormalZ`) is directly over the agent |
| 3 | `SidePositiveX` | the face neighbour to the east is blocked |
| 4 | `SidePositiveY` | north is blocked |
| 5 | `SideNegativeX` | west is blocked |
| 6 | `SideNegativeY` | south is blocked |
| 7 | `Door` | the leaf is joined to a leaf of another room through a door |

Contact is judged per voxel against its six face neighbours: a blocked
neighbour is a contact, and the brush face that separates the agent's box
from the obstacle says which kind by its normal. A voxel over a gentle slope
is a floor voxel; over a steep one, a wall voxel. A leaf is merged only from
voxels with equal flags, so a leaf's flags hold for every voxel in it.

**Adjacency.** Two leaves are neighbours when they share part of a face (an
area, not an edge or a corner), whether they are the same size or not, and
also when they are in two rooms joined through a door and their voxels meet
face to face on the shared cell face. `ADJN[ADJS[l] .. ADJS[l+1])` lists leaf
`l`'s neighbours in ascending leaf order; each entry is a leaf index, with
**bit 31 set when the step crosses a door**. The relation is symmetric. The
file lists neighbours explicitly so a reader never has to search the tree for
them; a reader that wants to derive them anyway finds, for each face of a
leaf, the leaves holding the voxels just beyond it (`FindLeaf` on each), within
the cell, and across a joined door the other room's portal voxels (`LINK`).

**Components** are the connected components of that graph, numbered in order
of their first leaf. Two leaves in one component are connected by free space
for that agent; different components are not, whatever the movement rule.

## 7. Points of interest, the spawn and arrivals

Points come from two places:

- **Authored**: `info_poi` point entities in a room of the library (section 13).
  Their position and yaw turn with the room, and a room-local name
  (`cxry_…`) is resolved to the cell (`c<column>r<row>_…`), its `±1` offsets
  turned with the room.
- **Door points**: one per door and per agent that fits through it, at the
  door's centre on the cell face, on the lowest voxel of the doorway, facing
  out of the room (type `door`, flag 2, flag 4 when the level joins the door).
  A capped doorway is solid, so its door point's `POIL` is −1.

Well-known types: `cover`, `vantage`, `spawn`, `patrol`, `interaction`,
`arrival`, `custom`, and `door` (compile-made only). Any other string is the
author's.

**Arrivals.** An `arrival` point is where a player appears on arriving from
another level. It has a facing, applies to the player's agent alone, and
stands on a floor where the player fits. Its record carries the role of its
room; the header's `upArrivalPoi` and `downArrivalPoi` index the first
arrival in an up room and in a down room (rooms in the level's placement
order), or −1.

**The spawn.** `spawnPoi` is where a player spawns on a fresh start: the up
room's arrival. A level with no up room has −1 until the level-transition
design names another rule; the field is there so that rule needs no format
change.

## 8. Ids: tying the navigation to its map

The game loads `level.bsp` and, beside it, `level.nav3d`; nothing stops the
two coming from different links. So the link writes one **level id** into
both: the map's worldspawn key `ss_level_id` and the header's `levelId`.
The mod checks they are equal (`Nav3dReader.MatchesMap`) before trusting the
navigation. The map's `ss_pack_id` and the header's `packId` name the room
compile the level's rooms came from.

Both ids are **RFC 9562 version 8** UUIDs whose free bits are the first bits
of a SHA-256 over their inputs, so the same inputs always give the same id
and the outputs stay reproducible byte for byte. The pack id hashes the
library VMF's bytes, the room options, the navigation settings and the tool
version; the level id hashes the pack id, the level file's bytes and the link
options. The room pack stores its id in a library section tagged `CMPL`.

## 9. How a reader walks it

```c++
// Load: read the whole file into memory.
struct Envelope { char magic[8]; int32_t version; uint8_t codec, pad[3]; int32_t imageLength, storedLength; };
const Envelope* e = (const Envelope*)file;
check(memcmp(e->magic, "SSNAV3D\0", 8) == 0 && e->version == 1 && 24 + e->storedLength == fileSize);
const uint8_t* image = file + 24;
if (e->codec == 1) image = inflateRaw(file + 24, e->storedLength, e->imageLength);   // zlib, windowBits = -15
if (e->codec == 2) image = brotliDecode(file + 24, e->storedLength, e->imageLength);

int32_t headerBytes = rd32(image + 0), sectionCount = rd32(image + 4);
// ... read the rest of the header, then the directory:
for (int i = 0; i < sectionCount; i++) {
    const uint8_t* d = image + headerBytes + 16 * i;
    remember(tag(d), rdu32(d + 4) /* index */, rdu32(d + 8) /* offset */, rdu32(d + 12) /* length */);
}
// Validate once: every section inside the image, record counts matching the
// header, every node's child block and leaf index in range, every adjacency
// entry < leafCount. After that no query can read out of bounds.

// Which leaf holds point p, for agent a?
int FindLeaf(int a, Vec3 p) {
    double lx = p.x - origin.x, ly = p.y - origin.y, lz = p.z - origin.z;
    if (lx < 0 || ly < 0 || lz < 0) return -1;
    int64_t X = floor(lx / voxelSize), Y = floor(ly / voxelSize), Z = floor(lz / voxelSize);
    if (X >= columns * cellVoxels || Y >= rows * cellVoxels || Z >= cellVoxels) return -1;
    int cell = (Y / cellVoxels) * columns + (X / cellVoxels);
    int x = X % cellVoxels, y = Y % cellVoxels, z = (int)Z;
    int32_t root = ROOT[a][cell];
    if (root < 0) return -1;
    uint32_t node = NODE[a][root];
    int half = (1 << octreeDepth) >> 1, x0 = 0, y0 = 0, z0 = 0;
    while ((node >> 30) == 0 /* inner */ && half > 0) {
        int oct = 0;
        if (x >= x0 + half) { oct |= 1; x0 += half; }
        if (y >= y0 + half) { oct |= 2; y0 += half; }
        if (z >= z0 + half) { oct |= 4; z0 += half; }
        node = NODE[a][(node & 0x3FFFFFFF) + oct];
        half >>= 1;
    }
    return (node >> 30) == 1 /* free */ ? (int)(node & 0x3FFFFFFF) : -1;
}

// A* over leaves: neighbours come straight from the CSR arrays.
for (uint32_t i = ADJS[a][l]; i < ADJS[a][l + 1]; i++) {
    uint32_t entry = ADJN[a][i];
    int next = entry & 0x7FFFFFFF;
    bool throughDoor = entry >> 31;
    if (walker && !(LEAF[a][next].flags & FLOOR)) continue;   // the movement rule is the caller's
    // cost: distance between leaf centres, e.g. centre = (corner + size / 2) * voxelSize + origin
}

// Quick rejection: two leaves in different components are never connected.
if (LEAF[a][from].component != LEAF[a][to].component) return NO_PATH;
```

## 10. Using the reader from the mod (C#)

The mod references `SourceSharp.MapFormats`, which has no dependencies. The
reader validates the file once and then answers from its bytes: point lookups,
neighbour walks and leaf reads allocate nothing (a fact checks it).

```csharp
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

byte[] bytes = File.ReadAllBytes("maps/level.nav3d");
Nav3dReader nav = Nav3dReader.Open(bytes);             // throws InvalidDataException on a bad file

// Is this the navigation of the map we loaded? (the worldspawn's ss_level_id)
if (!nav.MatchesMap(worldspawn["ss_level_id"]))
    throw new InvalidOperationException("level.nav3d belongs to another build of the map");

// Where does the player start?
if (nav.TryGetSpawn(out Vec3 spawn, out float yaw))
    SpawnPlayer(spawn, yaw);

int standing = nav.FindAgent("standing");
int leaf = nav.FindLeaf(standing, npc.Origin);          // -1: the NPC is where it does not fit
foreach (Nav3dNeighbour n in nav.Neighbours(standing, leaf))
{
    Nav3dLeaf next = nav.Leaf(standing, n.Leaf);
    if ((next.Flags & Nav3dLeafFlags.Floor) == 0) continue;   // walkers stay on floors
    Vec3 corner = nav.LeafMins(standing, n.Leaf);
    float size = nav.LeafSize(standing, n.Leaf);
    // ... A* bookkeeping
}

// Points of interest: cheap fields without strings, the full record when needed.
for (int p = 0; p < nav.PoiCount; p++)
{
    if (nav.PoiTypeUtf8(p).SequenceEqual("cover"u8))
        cover.Add((nav.PoiPosition(p), nav.PoiLeaf(standing, p)));
}
```

`Nav3dReader.ToLevel()` reads everything into objects (`Nav3dLevel`) for tools;
`Nav3dWriter.Write` writes one. A compressed file is decoded once, in `Open`.

## 11. A worked example

A two-cell level, one agent, built by hand in the facts
(`Nav3dFileTests.Sample`): cells of 32 units, two voxels a side (voxel 16),
two columns and one row. Cell 0 is one free leaf of the whole cell; cell 1 is
split, its two low-x, low-z voxels free as two leaves, the rest blocked. A
door joins them. Raw, the file is 724 bytes:

```
0000  53 53 4e 41 56 33 44 00 01 00 00 00 00 00 00 00   magic, version 1, codec 0
0010  bc 02 00 00 bc 02 00 00                            imageLength = storedLength = 700
                              70 00 00 00 0d 00 00 00   image: headerBytes 112, 13 sections
0020  01 00 00 00 00 00 00 42 00 00 80 41 02 00 00 00   1 agent, cellSize 32.0, voxelSize 16.0, cellVoxels 2
0030  01 00 00 00 02 00 00 00 01 00 00 00 00 00 00 00   depth 1, columns 2, rows 1, origin.x 0
0040  00 00 00 00 00 00 00 00 33 33 33 3f 02 00 00 00   origin.y, .z, floorNormalZ 0.7, 2 points
0050  02 00 00 00 00 00 00 00 00 00 00 00 ff ff ff ff   2 doors, spawn 0, up arrival 0, down -1
0060  0f 1e 2d 3c 4b 5a 89 78 86 85 f4 e3 d2 c1 b0 a9   levelId 0f1e2d3c-4b5a-8978-8685-f4e3d2c1b0a9
0070  01 23 45 67 89 ab 8d ef 81 23 45 67 89 ab cd ef   packId
0080  00 00 00 00 00 00 00 00                            reserved
                              53 54 52 53 ff ff ff ff   directory: "STRS", level
0090  40 01 00 00 30 00 00 00                            at image 0x140, 48 bytes
      ...                                                AGNT, CELL, DOOR, POIS, then ROOT NODE LEAF ADJS ADJN LINK COMP POIL for agent 0
```

(File offsets; the image starts at file offset `0x18`, so image offset
`0x140` is file offset `0x158`.) The `NODE` section of agent 0, at image
`0x228`, holds ten words:

```
00 00 00 40   node 0  0x40000000  free, leaf 0: cell 0's root, the whole cell one leaf
02 00 00 00   node 1  0x00000002  inner, children at 2-9: cell 1's root
01 00 00 40   node 2  0x40000001  octant 0 (low x, low y, low z): free, leaf 1
00 00 00 80   node 3  0x80000000  octant 1 (high x): blocked
02 00 00 40   node 4  0x40000002  octant 2 (high y): free, leaf 2
00 00 00 80   nodes 5-9           octants 3-7: blocked
```

Looking up the point `(40, 20, 5)` for agent 0: voxel `X = 2, Y = 1, Z = 0`,
so cell `1` (column 1, row 0) and in-cell voxel `(0, 1, 0)`. `ROOT[1] = 1`;
node 1 is inner with `half = 1`; `y ≥ 1` sets octant bit 1, so the child is
node `2 + 2 = 4`: free, leaf 2. Leaf 2's record reads corner `(2, 1, 0)`,
`sizeLog2 0`, flags `0x09` (`Floor | SidePositiveX`), component 0, cell 1.
Its neighbours, `ADJN[ADJS[2] .. ADJS[3]) = ADJN[3..4)`, are `{1}`: leaf 1,
not through a door. Leaf 1's neighbours are `0x80000000 | 0` (leaf 0, through
the door) and `2`.

## 12. Versioning

- The **envelope's version** changes only for a change an older reader must
  not read around: a record that changes size or meaning, a section it
  cannot ignore. A reader refuses any version it does not know.
- A **new optional section** gets a new tag and changes no version: older
  readers skip it.
- The **header** records its own size, so fields can be added at its end
  without moving the directory; a reader uses `headerBytes` to find the
  directory, never the constant 112.
- The room pack's navigation sections (section 13) carry their own version.

## 13. Where the data comes from

### 13.1 Configuration: the library's worldspawn

The settings belong to the whole library (every room must be voxelised on
one grid for one set of agents, or the link could not stitch them), so they
are keys of the library VMF's worldspawn, which Hammer edits in Map
Properties and every room's own VMF inherits:

| Key | Default | Meaning |
| --- | --- | --- |
| `nav` | on | `0` builds no navigation |
| `nav_voxel_size` | `16` | voxel edge; must divide the cell, at most 128 voxels a side |
| `nav_max_slope` | (normal z ≥ 0.7) | steepest walkable floor in degrees |
| `nav_agents` | `standing 32 72 player; flyer 32 32 npc` | `name width height [mask]` entries; mask `player`, `npc` or a number |

The **player's agent** is the first whose mask is `player`: arrivals must fit
it. Agents are square seen from above, so a quarter-turned room gives the
same free space as the room turned.

### 13.2 Authoring points of interest

An `info_poi` point entity inside a room's cell: `poi_type`, `poi_tags`,
optional `poi_radius`, optional `angles` (its yaw is the facing),
optional `poi_agents` (comma-separated agent names; default all), optional
`targetname` (`cxry_` names are room-local). A point must stand where every
agent it applies to fits, or the room fails to compile with a message naming
the entity. `info_poi` entities are **taken out of the map**: they cost no
entity at run time, and live only in the navigation. An `info_room`'s
`room_role` key (`up`, `down`) gives the room's role.

### 13.3 The room pack's sections

`ssmap room` precomputes each room's navigation and stores it in the
`.roompack` beside the room's container, under tags `NVR0` (the room as
authored) and `NVR1`-`NVR3` (turned one, two and three quarter turns). The
link reads, for each placement, the section of its turn, and nothing else of
the pack. All integers are **big-endian** (the pack's convention); the
section starts with a 4-byte envelope that is never compressed:

| Bytes | Field |
| --- | --- |
| 2 | `uint16` version, 1 |
| 1 | codec (as the `.nav3d` envelope's) |
| 1 | turn, 0-3 (must match the tag) |
| 4 | `int32` payload's raw length |
| rest | payload, stored by the codec |

Payload: `float32` cell size, voxel size, `int32` voxels per edge, `float32`
floor normal z, `uint8` role; agents (`uint8` count; each a string name,
`float32` width, height, `int32` mask); sockets (`uint8` count; each `uint8`
turn-0 facing, string name); points (`int32` count; each `float32` x, y, z,
yaw, `uint8` has-facing, `float32` radius, string type, string tags, `uint8`
has-name then the name, `uint32` agent mask); then per agent the octree
(`int32` node count and the node words, as in section 5, root at 0; `int32`
leaf count and leaves of five bytes: x, y, z, sizeLog2, flags, in the cell's
voxels), and per socket its **portal** (`int32` count, voxels of three bytes:
the doorway's free voxels in the boundary layer) and its **cap** (`int32`
count, changes of five bytes: voxel, 1 when capping blocks it, the flags
capping adds). A string is `uint16` length and UTF-8.

The octree is built with **every door open**: opening is the one state a room
cannot know alone. Capping a door only adds solids, so a placement with some
doors capped is the open octree with those sockets' cap changes applied, and
the changes of different doors never disagree.

The library section `CMPL` holds the pack id's 16 bytes.

**Why the navigation lives in the pack.** The owner's decision: the pack
already has typed per-room sections that readers skip when unknown, so an
older build still links a newer pack (without navigation), and the link
seeks straight to the placed rooms' sections. A pack built with `nav 0`, or
before navigation existed, links with one warning and no `.nav3d`;
`ssmap link -require-nav` makes that an error.

### 13.4 Seams: rooms stitched against the whole map

A room is voxelised without its neighbours. Outside its cell it assumes the
kit: solid everywhere except behind each open door, where the neighbour's
wall has the same opening, `wall_depth` deep, and beyond it the neighbour's
room is taken to be open near its door. The link joins two rooms only where
both sides' doorway voxels are free, so this never invents a passage.

A fact voxelises every 3x3 sample level straight from its flattened
whole-map compile and compares every voxel of every placed room, for both
default agents, with the stitched file: they are **identical, seams
included**. In general the stitched result is exact when an agent's
half-width is at most `wall_depth` and a room keeps the inside of each door
clear for half an agent plus one voxel. Beyond that, a doorway voxel's side
flag facing the neighbour can differ, and a doorway voxel's own class can be
optimistic for an agent wider than twice the wall is deep; neither changes
which rooms connect.

## 14. Measurements and the choices they made

Measured on a 4-core machine shared with other work (load average 20-40
during the runs), on Microsoft's .NET 10.0.12 runtime. Wall times under that
load are noisy; CPU times and in-process timings are steadier.

**The 3x3 sample** (5-room library, 9-room level, both default agents):

| | without navigation | with navigation |
| --- | --- | --- |
| `ssmap room` (5 rooms) | 0.85-0.97 s | 0.90-0.92 s |
| `.roompack` | 69 KB | 211 KB (turn 0 only), 637 KB (all four turns) |
| `ssmap link` | 0.34-0.58 s | 0.55-0.63 s |
| stitching in process | | 12.7 ms (read 3.4, stitch 6.5, write 0.9) |
| `.nav3d` | | 932 KB: 8,544 standing leaves, 10,174 flyer leaves |

**The stress library** (`tools/RoomsSample --stress`, 256 rooms, a 16x16
level from `ssmap layout -seed 1`, 256 rooms placed, 861 doors):

| | without navigation | with navigation |
| --- | --- | --- |
| `ssmap room` (256 rooms, wall) | 6.5-9.7 s | 7.0-8.0 s (the machine's load dominates the spread) |
| room navigation build, per room | | 2.1 ms warm, 3.5 ms in a cold `ssmap room` (the room's own compile: ~3 ms) |
| `.roompack` | 3.71 MB | 11.05 MB (turn 0 only), 33.07 MB (all four turns: the default) |
| `ssmap link` (median wall) | ~0.8 s | ~1.3 s with all turns stored, ~1.7 s turning at link |
| stitching in process (warm) | | ~210 ms: read 41-55, stitch 120-150, write 30-45 |
| `.nav3d` | | 28.3 MB: 260k standing leaves, 306k flyer leaves, 1 component each |

**Voxel size.** 16 is the default: the largest voxel that keeps the sample kit
exact (wall depth 16, player half-width 16, a 96-wide door leaves four voxels
of centre line). At 32 the lowest free voxel floats 16 units over the floor
and the floor contact is lost; at 8 the answer on this kit is the same with
more leaves. Per room, stress library, in a warm process: 32 gives 313
leaves, a 5.6 KB section and 0.8 ms of build; 16 gives 2,172 leaves, 28.8 KB
and 2.1 ms; 8 gives 11,608 leaves, 142 KB and 12.1 ms.

**Turns stored ×4 or turned at link.** Turning is a lossless permutation of
voxels and costs little, but the link from a pack with all four turns was
measurably faster (16x16: 1.32 s against 1.74 s median wall, 0.86 s against
1.0 s user CPU, in a fresh process), and the owner's rule is that disk is
cheap and link time decides. So `ssmap room` stores all four turns by default
(`-nav-turn0` stores turn 0 only). A fact proves the link writes the same
file from either pack, and another that voxelising a room turned equals
turning its stored navigation, at all four turns.

**Compression.** Both artifacts carry a codec byte; the default is **none**,
because decoding raw data was faster than decoding compressed data in both
places it matters (the pack warm in the page cache):

| 256 rooms' `NVR0` sections | size | read (median, all rooms) |
| --- | --- | --- |
| none | 7.34 MB | 23.6 ms |
| deflate:1 | 3.36 MB | 37.9 ms |
| deflate:6 | 2.23 MB | 38.6 ms |
| brotli:1 | 2.29 MB | 48.4 ms |
| brotli:5 | 1.56 MB | 44.3 ms |

| 16x16 `.nav3d` | size | write | load (read + open, warm) |
| --- | --- | --- | --- |
| none | 28.3 MB | 69 ms | 24 ms |
| deflate:1 | 15.2 MB | 242 ms | 99 ms |
| deflate:6 | 9.7 MB | 791 ms | 76 ms |
| deflate:9 | 8.1 MB | 2193 ms | 66 ms |
| brotli:1 | 10.7 MB | 161 ms | 104 ms |
| brotli:5 | 7.9 MB | 1009 ms | 90 ms |
| brotli:9 | 7.4 MB | 5574 ms | 90 ms |

`-nav-codec none|deflate[:0-9]|brotli[:0-11]` on `ssmap room` and
`ssmap link` chooses otherwise. Compressed bytes are the same on every run
and at any thread count. Brotli and raw bytes are pinned by the facts; the
Deflate pin is Microsoft's runtime's, which carries its own zlib-ng on every
OS (a distribution's packaged runtime that links the system zlib writes
other, equally valid, Deflate bytes). Equality across operating systems is
expected and is checked by CI's Windows and macOS runners, not yet here.

**Separate files or the pack.** Navigation is about three times a room's
container (28.7 KB against 14.5 KB per stress room, turn 0), not "trivially
small"; it lives in the pack anyway by the owner's decision (13.3), in its
own sections, read only for the placed rooms.
