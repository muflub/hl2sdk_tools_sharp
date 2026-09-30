# The `.map2d` level map file

`ssmap link` writes `<map>.map2d` beside every linked `<map>.bsp`: a 2D
vector map of the level seen from above, which the game loads to draw a map
overlay (the rooms design, section 18). `ssmap map2d` writes the same file
for any compiled map. This document is the file's specification, version
**1**. It is written so a reader can be built from it alone, in any
language; the C# reader in `SourceSharp.MapFormats` (`Map2dReader`) is one
such reader, and the facts check the two against each other.

Contents:

1. [What the file describes](#1-what-the-file-describes)
2. [Coordinates and conventions](#2-coordinates-and-conventions)
3. [Layout](#3-layout): header, section directory
4. [Sections](#4-sections): every record, byte by byte
5. [The binding: refusing a stale map](#5-the-binding-refusing-a-stale-map)
6. [Where the data comes from](#6-where-the-data-comes-from)
7. [How a reader draws it](#7-how-a-reader-draws-it)
8. [The SVG preview](#8-the-svg-preview)
9. [Versioning](#9-versioning)

---

## 1. What the file describes

- **Floors**: every surface a player can stand on, projected onto the xy
  plane, as filled polygons: an outer ring and its holes. Walls are the
  polygons' outlines; nothing above a floor is drawn, so a room reads as its
  floor plan. Every ring carries its **height band**, the lowest and highest
  z of the floor it draws, so a game can show the floor the player is on and
  dim or hide the others (a gallery over a hall is two polygons that overlap
  on the plane, each with its band).
- **Placements**: the level's rooms, each in its cell. Every ring, door and
  marker names the placement it belongs to, so a game can reveal the map
  room by room (fog of war) or highlight the room the player is in. A room
  may carry a **label** (its `info_room`'s `map_label`).
- **Doors**: one per socket of each placement, a segment across the opening
  on the cell face, **open** where the socket is joined to a neighbour's and
  **closed** where a cap seals it. A joined doorway has two records, one per
  side, each naming the placement it opens into. The floor of a doorway is
  the door's, not either room's: the polygons stop at the rooms' inner walls
  and the door segment spans the gap.
- **Markers**: points of interest, each with a **kind** (a short identifier
  the game maps to an icon), a position, a yaw and an optional label: the
  authors' (`info_poi` with `map_marker`) and the linker's own (`spawn`,
  `arrival`, `exit_up`, `exit_down`).

A level's map is a few kilobytes (a 3x3 level about 2 KB) and sharp at any
zoom.

## 2. Coordinates and conventions

- **Units** are Source units, as in the map. **+x is east, +y is north**, z
  is up: the game places the player's arrow with no conversion and scales
  the header's extent to its panel.
- **Endianness**: every multi-byte value is **little-endian**. Floats are
  IEEE 754 binary32, always finite.
- **Polygon points are whole units** (`int16` or `int32`, section 3). Doors
  and markers keep the map's floats.
- **Rings**: an outer ring is **counter-clockwise** seen from above, a hole
  **clockwise**; no point repeats, the first is not repeated at the end, no
  three consecutive points are collinear, and every ring starts at the point
  its placement's room had least (x, then y) before it was turned. Rings do
  not cross; two rings of one band may touch at a point.
- **Yaw** is degrees counter-clockwise from +x, `[0, 360)`.
- **Strings** are offsets into the string table; offset 0 is the empty
  string, which a label uses for "none".
- **Placements** are indices into `ROOM`; −1 means none (a map made without
  a level file has no placements).
- **Rotation** is quarter turns counter-clockwise, 0 to 3, as in the level
  file (90° is 1).

## 3. Layout

```
header (headerBytes, 96 in version 1)
section directory (sectionCount × 12 bytes)
sections (each starting on a 4-byte boundary, zero padding between and after)
```

The file is never compressed: it is small, and read once when the map loads.

### 3.1 Header (offset 0)

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `char[8]` | magic `SSMAP2D\0` (`53 53 4D 41 50 32 44 00`) |
| 8 | `int32` | version: **1** |
| 12 | `int32` | `headerBytes`: 96 in version 1. The directory starts here. |
| 16 | `int32` | `sectionCount`, at most 64 |
| 20 | `uint32` | `mapChecksum`: the checksum of the `.bsp` the file was made for (section 5) |
| 24 | `uint32` | flags: bit 0 set when every point is an `int16` pair (`PNTS`); no other bit is defined |
| 28 | `float32` | `cellSize`, or 0 for a map made without a level file |
| 32 | `int32` | `columns` (cells west to east), or 0 |
| 36 | `int32` | `rows` (cells south to north), or 0 |
| 40 | `int32[4]` | the extent on the plane: `minX`, `minY`, `maxX`, `maxY` |
| 56 | `int32[2]` | the extent in z: `minZ`, `maxZ` |
| 64 | `int32` | `roomCount` |
| 68 | `int32` | `ringCount` |
| 72 | `int32` | `pointCount` |
| 76 | `int32` | `doorCount` |
| 80 | `int32` | `markerCount` |
| 84 | `byte[12]` | zero, reserved |

**The extent** is the box the contents fill: every ring's points and band,
every door's ends and band, every marker's position, floats widened outward
to whole units (`floor` of the least, `ceil` of the greatest); all zero for
an empty map. It is a function of the contents, and a reader checks it.

The writer stores `int16` points whenever every point fits
`[-32768, 32767]`, which every level within the engine's ±16,384 does.

### 3.2 Section directory

`sectionCount` entries of 12 bytes, at offset `headerBytes`:

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `char[4]` | tag, ASCII |
| 4 | `uint32` | offset of the section from the file's start; a multiple of 4 |
| 8 | `uint32` | length in bytes |

A reader looks sections up by tag and **ignores tags it does not know**.
Version 1 writes six, in this order: `STRS`, `ROOM`, `POLY`, `PNTS`,
`DOOR`, `MARK`; all six are required.

## 4. Sections

| Tag | Record | Count |
| --- | --- | --- |
| `STRS` | bytes | string table: NUL-terminated UTF-8; offset 0 is the empty string |
| `ROOM` | 24 bytes | `roomCount` |
| `POLY` | 24 bytes | `ringCount` |
| `PNTS` | 4 or 8 bytes | `pointCount` |
| `DOOR` | 40 bytes | `doorCount` |
| `MARK` | 28 bytes | `markerCount` |

### 4.1 `STRS`

UTF-8 strings, each followed by a NUL, the first the empty string (so the
table starts with a NUL); an offset names the first byte of a string. Each
string is stored once, in the order the writer first meets it (rooms' names
and labels, then doors' sockets, then markers' kinds and labels).

### 4.2 `ROOM`: a placement

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `int32` | cell column, from the west edge |
| 4 | `int32` | cell row, from the south edge |
| 8 | `int32` | rotation, 0 to 3 |
| 12 | `float32` | the room's height |
| 16 | `uint32` | the room's name, as the level places it (`lib.room` in a level of several libraries) |
| 20 | `uint32` | its label, or 0 |

Placements are in link order: the level file's rows from the south, each
west to east. A placement's cell spans `(column × cellSize, row × cellSize)`
to that plus `cellSize` on the plane, and 0 to its height in z. Its name in
the naming grammar (the rooms design, section 5) is `c<column>r<row>`.

### 4.3 `POLY`: a ring

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `int32` | placement, or −1 |
| 4 | `int32` | band: lowest z, whole units |
| 8 | `int32` | band: highest z, whole units, at least the lowest |
| 12 | `uint32` | flags: bit 0 set for a hole |
| 16 | `int32` | first point, an index into `PNTS` |
| 20 | `int32` | point count, at least 3 |

A **polygon** is an outer ring followed by its holes: a hole belongs to the
nearest outer ring before it, and shares its placement and band. Polygons
are ordered by placement, then by band (low first), then by their outer
rings' points. The rings' points are consecutive in `PNTS`, in ring order.

### 4.4 `PNTS`: points

`pointCount` pairs `x, y`: `int16` each when header flag bit 0 is set,
`int32` each otherwise.

### 4.5 `DOOR`: a door

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `int32` | placement |
| 4 | `uint32` | the socket's name |
| 8 | `uint32` | flags: bit 0 set when **open** (joined) |
| 12 | `int32` | the placement it opens into, or −1 when closed |
| 16 | `float32[2]` | one end, x and y |
| 24 | `float32[2]` | the other end, x and y |
| 32 | `float32` | the opening's bottom z |
| 36 | `float32` | the opening's top z |

Doors are ordered by placement, then by the room's socket order. The
segment lies on the cell face, across the full width of the kit's opening.

### 4.6 `MARK`: a marker

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `uint32` | kind |
| 4 | `uint32` | label, or 0 |
| 8 | `int32` | placement, or −1 |
| 12 | `float32[3]` | position: x, y, z (where a player's feet would be) |
| 24 | `float32` | yaw |

The linker's markers come first: `spawn` (where a fresh start puts the
player: the up room's arrival, or with `up: none` the spawn room's first
spawn point), then per transition room in link order its `arrival` and its
exit (`exit_up` or `exit_down`, at the transition volume's centre, yaw 0),
both labelled with the map the transition leads to. Then the authors'
markers, placement by placement, each room's in its VMF's order. A level
without transitions has no linker markers.

Kinds are lower-case identifiers: a letter, then letters, digits or
underscores, 32 characters at most. `spawn`, `arrival`, `exit_up` and
`exit_down` are the linker's; `SourceSharp.RoomContracts.LevelMap` is the
list both sides share. Labels are at most 64 bytes of UTF-8.

## 5. The binding: refusing a stale map

`mapChecksum` is the checksum the engine computes for a map when it loads it:
the CRC-32 (the zip polynomial, reflected `0xEDB88320`, initial value and
final XOR `0xFFFFFFFF`) of every lump's bytes in lump order, **the entity
lump (lump 0) left out**, each lump as the header's directory names it. The
engine already has the number (a server and its clients compare it), so the
check is free; a mod may also compute it from the file
(`SourceSharp.MapFormats.Bsp.BspMapChecksum`).

A game refuses a `.map2d` whose `mapChecksum` is not the loaded map's: it
was made for another compile of the map (a sidecar left beside a relinked
map), and its floors and doors would be drawn where the map no longer has
them. Leaving the entity lump out keeps the binding across an edit that
touches only entities.

## 6. Where the data comes from

**Pack time** (`ssmap room`), per room (the room's `MAPV` pack section,
stored once, room-local):

- **The face rule.** A face of the room's compile counts when its plane's
  normal z is at least **0.7** (the player's walkable slope) and it is drawn:
  not sky, 2D sky, nodraw, water, trigger (the door plugs and caps), hint or
  skip. Faces a solid brush sits on never exist in a compile. Displacements
  are left out. Brush entities count when a player stands on them
  (`func_brush` unless its `Solidity` is 1, `func_door`,
  `func_door_rotating`, `func_movelinear`, `func_platrot`,
  `func_tracktrain`, `func_train`, `func_breakable`, `func_physbox`,
  `func_wall`, `func_wall_toggle`, `func_button`, `func_rot_button`),
  except socket furniture (door hardware): a brush entity's face inside a
  furniture brush's box.
- **The cut.** Faces are clipped to the cell and kept when their z is within
  the room's height; floor inside a plug box (a doorway's) is cut away.
- **The snap.** Points are rounded to whole units, halves up, in the room's
  own frame.
- **The union.** Faces are grouped: two faces are in one group when their z
  bands overlap and their areas touch; each group is unioned exactly on
  integers into outer rings and holes, and its band is its faces' lowest to
  highest z. Points on a straight line are dropped.

**Link time** (`ssmap link`): each placement's polygons are turned and moved
to its cell on whole units, exactly; doors take their state from the
joints; markers are turned with their room; the linker's markers come from
the rooms' transition data. No geometry work: a copy and a turn.

**Any compiled map** (`ssmap map2d <map.bsp> [-level <level.yaml>]`): the
same face rule over the map. With a level file the faces are cut into the
level's cells, each placement's taken into its room's frame and unioned
there, and the doors and markers read from the level's libraries: the
flattened level's compile gives the linked level's file byte for byte, but
for `mapChecksum`. Without one, the whole map is one union in its own frame
(placement −1), with no doors, and its markers are its `info_player_start`s
(as `spawn`) and any entity with a `map_marker`.

## 7. How a reader draws it

```c++
struct Header { char magic[8]; int32_t version, headerBytes, sectionCount; uint32_t mapChecksum, flags;
                float cellSize; int32_t columns, rows, minX, minY, maxX, maxY, minZ, maxZ,
                rooms, rings, points, doors, markers; };
const Header* h = (const Header*)file;
check(memcmp(h->magic, "SSMAP2D\0", 8) == 0 && h->version == 1);
check(h->mapChecksum == engineMapCrc);                   // else the file is stale: do not draw it
for (int i = 0; i < h->sectionCount; i++) {
    const uint8_t* d = file + h->headerBytes + 12 * i;
    remember(tag(d), rdu32(d + 4) /* offset */, rdu32(d + 8) /* length */);
}
// Validate once: sections inside the file, sized to the counts, strings in the
// table, every ring's points in PNTS, every placement index in range.

float scale = panelWidth / float(h->maxX - h->minX);    // world units to pixels
auto toPanel = [&](float x, float y) { return Vec2((x - h->minX) * scale, (h->maxY - y) * scale); };

int band = BandOfPlayer(playerZ);                         // the game's choice
for (int r = 0; r < h->rings; ) {                         // a polygon: an outer ring and its holes
    int end = r + 1;
    while (end < h->rings && (POLY[end].flags & 1)) end++;
    if (Revealed(POLY[r].placement))
        FillEvenOdd(POLY + r, end - r, POLY[r].zLow <= playerZ + step && playerZ <= POLY[r].zHigh + step ? bright : dim);
    r = end;
}
for (int d = 0; d < h->doors; d++)
    if (Revealed(DOOR[d].placement)) Line(DOOR[d], (DOOR[d].flags & 1) ? openColour : closedColour);
for (int m = 0; m < h->markers; m++)
    if (MARK[m].placement < 0 || Revealed(MARK[m].placement)) Icon(IconOf(Str(MARK[m].kind)), MARK[m]);
Arrow(playerX, playerY, playerYaw);
```

## 8. The SVG preview

`ssmap link -map2d-svg` and `ssmap map2d -svg` also write `<map>.svg`: the
same data drawn for people and web pages. Floors are filled paths (outer
ring and holes, even-odd), lower bands first and darker; doors are lines,
open green and closed red; markers are dots with their kind and label; a
room's label sits at its cell's centre. The preview is a function of the
file's contents, the same bytes on every machine; the game reads the
`.map2d`, never the SVG.

## 9. Versioning

The version changes only for a change an older reader must not read around:
a record that changes size or meaning. A new section is added under a new
tag without a version change, and readers skip tags they do not know. A
header larger than 96 bytes is read by an older reader as far as it knows
(`headerBytes` says where the directory starts).
