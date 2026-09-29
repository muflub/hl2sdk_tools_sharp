//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapFormats.Nav;

/// <summary>
/// The constants of the <c>.nav3d</c> level navigation file: its magic, its
/// version, its section tags and the bit layouts of its packed words.
/// </summary>
/// <remarks>
/// <para>
/// The file is specified in <c>docs/nav3d-format.md</c>, which is written
/// so a reader in another language can be built from it alone; this class
/// is the C# spelling of the same numbers. Every multi-byte value is
/// little-endian, because the file is read at run time by a game on
/// little-endian machines, and every section starts on a four-byte boundary,
/// so a reader on such a machine may view a section as an array of its
/// records without copying.
/// </para>
/// <para>
/// <b>Versions.</b> <see cref="Version"/> changes only for a change an older
/// reader must not read around: a record that changes size or meaning, or a
/// section an older reader cannot ignore. A new optional section is added
/// under a new tag without a version change; readers skip tags they do not
/// know, as the room pack's readers do. Version 2 replaced version 1's
/// per-agent octrees with one clearance grid shared by every agent size, so
/// a version 1 file is refused rather than misread.
/// </para>
/// </remarks>
public static class Nav3dFormat
{
    /// <summary>The file's eight magic bytes: <c>SSNAV3D</c> and a NUL.</summary>
    public static ReadOnlySpan<byte> Magic => "SSNAV3D\0"u8;

    /// <summary>The only version this build reads and writes.</summary>
    public const int Version = 2;

    /// <summary>The file extension <c>ssmap link</c> writes beside the map.</summary>
    public const string Extension = ".nav3d";

    /// <summary>
    /// The envelope in front of the image: magic, version, codec, the
    /// image's length and the stored length. It is never compressed, so a
    /// reader learns the version and codec before it decodes anything.
    /// </summary>
    public const int EnvelopeBytes = 24;

    /// <summary>The image header's size in this version; the header records its own size so a later one may grow.</summary>
    public const int HeaderBytes = 160;

    /// <summary>One entry of the section directory: tag, index, offset, length.</summary>
    public const int DirectoryEntryBytes = 16;

    /// <summary>The directory index every section of this version carries: they all belong to the level.</summary>
    public const uint LevelIndex = 0xFFFFFFFFu;

    /// <summary>A string reference, or a cell's room name, that is absent.</summary>
    public const uint NoString = 0xFFFFFFFFu;

    /// <summary>The most agent presets a file may carry: a point's agent mask is 32 bits.</summary>
    public const int MaxPresets = 32;

    /// <summary>The most voxels along a cell's edge: a leaf's height is one byte.</summary>
    public const int MaxCellVoxels = 128;

    /// <summary>Section: the string table (NUL-terminated UTF-8; offset 0 is the empty string).</summary>
    public const string StringsTag = "STRS";

    /// <summary>Section: one <see cref="PresetRecordBytes"/>-byte record per agent preset.</summary>
    public const string PresetsTag = "AGNT";

    /// <summary>Section: one <see cref="CellRecordBytes"/>-byte record per grid cell.</summary>
    public const string CellsTag = "CELL";

    /// <summary>Section: one <see cref="DoorRecordBytes"/>-byte record per socket of a placed room.</summary>
    public const string DoorsTag = "DOOR";

    /// <summary>Section: one <see cref="PoiRecordBytes"/>-byte record per point of interest.</summary>
    public const string PoisTag = "POIS";

    /// <summary>Section: each cell's first column, <c>int32</c>, -1 for a cell with no room.</summary>
    public const string RootsTag = "ROOT";

    /// <summary>Section: <c>columnCount + 1</c> <c>uint32</c> starts: a column's leaves are <c>[COLS[j], COLS[j+1])</c>.</summary>
    public const string ColumnsTag = "COLS";

    /// <summary>Section: one <see cref="LeafRecordBytes"/>-byte record per leaf.</summary>
    public const string LeavesTag = "LEAF";

    /// <summary>Section: the clearance records, each four-byte aligned, that leaves point into (<see cref="Nav3dClearance"/>).</summary>
    public const string ClearanceTag = "CLRS";

    /// <summary>Section: one <see cref="ObstacleRecordBytes"/>-byte record per dynamic obstacle.</summary>
    public const string ObstaclesTag = "DYNO";

    /// <summary>Section: <c>brushCount + 1</c> <c>uint32</c> starts into <see cref="BrushPlanesTag"/>, in planes.</summary>
    public const string BrushIndexTag = "BRSI";

    /// <summary>Section: the overhanging brushes' planes, four <c>float32</c> each (normal, distance).</summary>
    public const string BrushPlanesTag = "BRSP";

    /// <summary>Section: one <see cref="JumpRecordBytes"/>-byte record per jump link.</summary>
    public const string JumpsTag = "JUMP";

    /// <summary>The size of a <see cref="PresetsTag"/> record.</summary>
    public const int PresetRecordBytes = 16;

    /// <summary>The size of a <see cref="CellsTag"/> record.</summary>
    public const int CellRecordBytes = 8;

    /// <summary>The size of a <see cref="DoorsTag"/> record.</summary>
    public const int DoorRecordBytes = 16;

    /// <summary>The size of a <see cref="PoisTag"/> record.</summary>
    public const int PoiRecordBytes = 48;

    /// <summary>The size of a <see cref="LeavesTag"/> record.</summary>
    public const int LeafRecordBytes = 24;

    /// <summary>The size of an <see cref="ObstaclesTag"/> record.</summary>
    public const int ObstacleRecordBytes = 48;

    /// <summary>The size of a <see cref="JumpsTag"/> record.</summary>
    public const int JumpRecordBytes = 16;

    /// <summary>The standard <c>CONTENTS_*</c> bits a standing player collides with in the world: solid, window, grate, moveable, monster and player clip.</summary>
    public const int PlayerSolidMask = 0x1 | 0x2 | 0x8 | 0x4000 | 0x2000000 | 0x10000;

    /// <summary>The standard <c>CONTENTS_*</c> bits an NPC collides with: as <see cref="PlayerSolidMask"/>, but monster clip instead of player clip.</summary>
    public const int NpcSolidMask = 0x1 | 0x2 | 0x8 | 0x4000 | 0x2000000 | 0x20000;

    /// <summary><c>CONTENTS_SLIME</c> and <c>CONTENTS_WATER</c>: a brush of either makes the voxels it overlaps water.</summary>
    public const int WaterContents = 0x10 | 0x20;

    /// <summary><c>CONTENTS_LADDER</c>: a brush of it makes the voxels it overlaps a ladder.</summary>
    public const int LadderContents = 0x20000000;

    /// <summary>The <c>CONTENTS_*</c> bits a clip class collides with.</summary>
    /// <param name="clipClass">The class.</param>
    /// <returns><see cref="PlayerSolidMask"/> or <see cref="NpcSolidMask"/>.</returns>
    public static int SolidMask(Nav3dClipClass clipClass) => clipClass == Nav3dClipClass.Npc ? NpcSolidMask : PlayerSolidMask;
}

/// <summary>
/// Which clip brushes an agent collides with: the one per-agent property the
/// shared grid keeps apart, as two clearance records per leaf.
/// </summary>
/// <remarks>
/// Player clip stops players and not NPCs, monster clip the other way round,
/// and everything else solid stops both. So there are exactly two worlds an
/// axis-aligned agent can live in, and each leaf carries one clearance record
/// for each (often the same record, stored once). Any agent size is then
/// answered exactly in its class's world.
/// </remarks>
public enum Nav3dClipClass : byte
{
    /// <summary>Collides with player clip: <see cref="Nav3dFormat.PlayerSolidMask"/>.</summary>
    Player = 0,

    /// <summary>Collides with monster clip: <see cref="Nav3dFormat.NpcSolidMask"/>.</summary>
    Npc = 1,
}

/// <summary>What a leaf is, beyond its clearance: the bits of its flags.</summary>
[Flags]
public enum Nav3dLeafFlags : ushort
{
    /// <summary>Nothing special.</summary>
    None = 0,

    /// <summary>The leaf's voxels overlap a water or slime brush.</summary>
    Water = 1 << 0,

    /// <summary>The leaf's voxels overlap a ladder: a <c>CONTENTS_LADDER</c> brush, an <c>info_ladder</c> or a <c>func_useableladder</c>'s volume.</summary>
    Ladder = 1 << 1,

    /// <summary>For the player class, solid lies directly under the leaf's bottom voxel: the leaf stands on a floor at its player floor height.</summary>
    GroundedPlayer = 1 << 2,

    /// <summary>For the NPC class, solid lies directly under the leaf's bottom voxel.</summary>
    GroundedNpc = 1 << 3,

    /// <summary>The player class's floor is walkable: its surface's normal z is at least the level's floor threshold.</summary>
    WalkablePlayer = 1 << 4,

    /// <summary>The NPC class's floor is walkable.</summary>
    WalkableNpc = 1 << 5,
}

/// <summary>What kind of thing a dynamic obstacle is, so the runtime knows what state to watch.</summary>
public enum Nav3dObstacleKind : byte
{
    /// <summary>A door: <c>func_door</c>, <c>func_door_rotating</c>, <c>prop_door_rotating</c>. Blocks while closed.</summary>
    Door = 1,

    /// <summary>A brush that moves along a path: <c>func_movelinear</c>, <c>func_train</c>, <c>func_tracktrain</c>, <c>func_rotating</c> and the like.</summary>
    Mover = 2,

    /// <summary>A brush the map turns solid or not: <c>func_brush</c>, <c>func_wall_toggle</c>. Blocks while enabled.</summary>
    Toggle = 3,

    /// <summary>Something that breaks: <c>func_breakable</c>, <c>func_breakable_surf</c>. Blocks until broken.</summary>
    Breakable = 4,

    /// <summary>A physics object: <c>func_physbox</c>, <c>prop_physics</c> and its variants. Blocks where it rests.</summary>
    Physics = 5,

    /// <summary>A model that may animate or be moved by the map: <c>prop_dynamic</c> and its variants.</summary>
    Prop = 6,
}

/// <summary>The direction of a step from a leaf to a neighbour.</summary>
public enum Nav3dDirection : byte
{
    /// <summary>+x, into the column to the east.</summary>
    East = 0,

    /// <summary>+y, north.</summary>
    North = 1,

    /// <summary>-x, west.</summary>
    West = 2,

    /// <summary>-y, south.</summary>
    South = 3,

    /// <summary>+z: the leaf directly above in the same column, touching.</summary>
    Up = 4,

    /// <summary>-z: the leaf directly below, touching.</summary>
    Down = 5,
}

/// <summary>What a point of interest is, beyond its type string: the bits of its flags.</summary>
[Flags]
public enum Nav3dPoiFlags : ushort
{
    /// <summary>Nothing special.</summary>
    None = 0,

    /// <summary>The yaw is meaningful: the author gave the point a facing.</summary>
    HasFacing = 1 << 0,

    /// <summary>The point was made by the compile at a door's centre.</summary>
    Door = 1 << 1,

    /// <summary>A door point whose door the level joins to a neighbour (clear: the door is capped).</summary>
    Joined = 1 << 2,

    /// <summary>The point is an arrival: where a player appears arriving from another level.</summary>
    Arrival = 1 << 3,
}

/// <summary>A placed room's role in moving the player between levels.</summary>
public enum Nav3dRoomRole : byte
{
    /// <summary>An ordinary room.</summary>
    None = 0,

    /// <summary>The room's transition leads up (<c>room_role up</c>).</summary>
    Up = 1,

    /// <summary>The room's transition leads down (<c>room_role down</c>).</summary>
    Down = 2,
}
