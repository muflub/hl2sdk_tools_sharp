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
/// know, as the room pack's readers do.
/// </para>
/// </remarks>
public static class Nav3dFormat
{
    /// <summary>The file's eight magic bytes: <c>SSNAV3D</c> and a NUL.</summary>
    public static ReadOnlySpan<byte> Magic => "SSNAV3D\0"u8;

    /// <summary>The only version this build reads and writes.</summary>
    public const int Version = 1;

    /// <summary>The file extension <c>ssmap link</c> writes beside the map.</summary>
    public const string Extension = ".nav3d";

    /// <summary>
    /// The envelope in front of the image: magic, version, codec, the
    /// image's length and the stored length. It is never compressed, so a
    /// reader learns the version and codec before it decodes anything.
    /// </summary>
    public const int EnvelopeBytes = 24;

    /// <summary>The image header's size in this version; the header records its own size so a later one may grow.</summary>
    public const int HeaderBytes = 112;

    /// <summary>One entry of the section directory: tag, index, offset, length.</summary>
    public const int DirectoryEntryBytes = 16;

    /// <summary>The directory index of a section that belongs to the level rather than to one agent.</summary>
    public const uint LevelIndex = 0xFFFFFFFFu;

    /// <summary>A string reference, or a cell's room name, that is absent.</summary>
    public const uint NoString = 0xFFFFFFFFu;

    /// <summary>The most agents a file may describe: an agent mask is 32 bits.</summary>
    public const int MaxAgents = 32;

    /// <summary>The largest level voxel coordinate a leaf can store (16 bits).</summary>
    public const int MaxVoxelCoordinate = ushort.MaxValue;

    /// <summary>Level section: the string table (NUL-terminated UTF-8; offset 0 is the empty string).</summary>
    public const string StringsTag = "STRS";

    /// <summary>Level section: one <see cref="AgentRecordBytes"/>-byte record per agent.</summary>
    public const string AgentsTag = "AGNT";

    /// <summary>Level section: one <see cref="CellRecordBytes"/>-byte record per grid cell.</summary>
    public const string CellsTag = "CELL";

    /// <summary>Level section: one <see cref="DoorRecordBytes"/>-byte record per socket of a placed room.</summary>
    public const string DoorsTag = "DOOR";

    /// <summary>Level section: one <see cref="PoiRecordBytes"/>-byte record per point of interest.</summary>
    public const string PoisTag = "POIS";

    /// <summary>Agent section: each cell's root node, <c>int32</c>, -1 for a cell with no room.</summary>
    public const string RootsTag = "ROOT";

    /// <summary>Agent section: the octree nodes, one <c>uint32</c> each (<see cref="NodeKindShift"/>).</summary>
    public const string NodesTag = "NODE";

    /// <summary>Agent section: one <see cref="LeafRecordBytes"/>-byte record per free leaf.</summary>
    public const string LeavesTag = "LEAF";

    /// <summary>Agent section: <c>leafCount + 1</c> <c>uint32</c> starts into <see cref="AdjacencyTag"/>.</summary>
    public const string AdjacencyStartTag = "ADJS";

    /// <summary>Agent section: the neighbour lists, one <c>uint32</c> each (<see cref="ThroughDoorBit"/>).</summary>
    public const string AdjacencyTag = "ADJN";

    /// <summary>Agent section: one <see cref="LinkRecordBytes"/>-byte record per leaf pair joined through a door.</summary>
    public const string LinksTag = "LINK";

    /// <summary>Agent section: one <see cref="ComponentRecordBytes"/>-byte record per connected component.</summary>
    public const string ComponentsTag = "COMP";

    /// <summary>Agent section: each point of interest's leaf, <c>int32</c>, -1 where it does not apply or lies in no free leaf.</summary>
    public const string PoiLeavesTag = "POIL";

    /// <summary>The size of an <see cref="AgentsTag"/> record.</summary>
    public const int AgentRecordBytes = 32;

    /// <summary>The size of a <see cref="CellsTag"/> record.</summary>
    public const int CellRecordBytes = 8;

    /// <summary>The size of a <see cref="DoorsTag"/> record.</summary>
    public const int DoorRecordBytes = 16;

    /// <summary>The size of a <see cref="PoisTag"/> record.</summary>
    public const int PoiRecordBytes = 48;

    /// <summary>The size of a <see cref="LeavesTag"/> record.</summary>
    public const int LeafRecordBytes = 16;

    /// <summary>The size of a <see cref="LinksTag"/> record.</summary>
    public const int LinkRecordBytes = 12;

    /// <summary>The size of a <see cref="ComponentsTag"/> record.</summary>
    public const int ComponentRecordBytes = 8;

    /// <summary>A node word's kind sits in its top two bits.</summary>
    public const int NodeKindShift = 30;

    /// <summary>A node word's payload: the low 30 bits.</summary>
    public const uint NodePayloadMask = (1u << NodeKindShift) - 1;

    /// <summary>An adjacency entry's top bit: the neighbour is reached through a door between two rooms.</summary>
    public const uint ThroughDoorBit = 0x80000000u;

    /// <summary>The standard <c>CONTENTS_*</c> bits a standing player collides with in the world: solid, window, grate, moveable, monster and player clip.</summary>
    public const int PlayerSolidMask = 0x1 | 0x2 | 0x8 | 0x4000 | 0x2000000 | 0x10000;

    /// <summary>The standard <c>CONTENTS_*</c> bits an NPC collides with: as <see cref="PlayerSolidMask"/>, but monster clip instead of player clip.</summary>
    public const int NpcSolidMask = 0x1 | 0x2 | 0x8 | 0x4000 | 0x2000000 | 0x20000;

    /// <summary>A node word.</summary>
    /// <param name="kind">What the node is.</param>
    /// <param name="payload">The first child's index for an inner node, the leaf's index for a free leaf, 0 otherwise.</param>
    /// <returns>The packed word.</returns>
    public static uint Node(Nav3dNodeKind kind, uint payload) => ((uint)kind << NodeKindShift) | (payload & NodePayloadMask);

    /// <summary>A node word's kind.</summary>
    /// <param name="node">The packed word.</param>
    /// <returns>Its kind.</returns>
    public static Nav3dNodeKind KindOf(uint node) => (Nav3dNodeKind)(node >> NodeKindShift);

    /// <summary>A node word's payload.</summary>
    /// <param name="node">The packed word.</param>
    /// <returns>Its low 30 bits.</returns>
    public static uint PayloadOf(uint node) => node & NodePayloadMask;

    /// <summary>The octree depth a cell of <paramref name="cellVoxels"/> voxels a side needs: the least D with 2^D at least that.</summary>
    /// <param name="cellVoxels">Voxels along a cell's edge, at least 1.</param>
    /// <returns>The depth.</returns>
    public static int DepthFor(int cellVoxels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cellVoxels, 1);
        int depth = 0;
        while ((1 << depth) < cellVoxels)
        {
            depth++;
        }

        return depth;
    }
}

/// <summary>What an octree node is: the top two bits of its word.</summary>
public enum Nav3dNodeKind : byte
{
    /// <summary>Eight children, stored back to back from the payload's index, in octant order.</summary>
    Inner = 0,

    /// <summary>A cube the agent is free in everywhere; the payload is its leaf's index.</summary>
    Free = 1,

    /// <summary>A cube the agent is blocked in somewhere in every voxel.</summary>
    Blocked = 2,

    /// <summary>A cube beyond the cell, when a cell's voxel count is not a power of two.</summary>
    Outside = 3,
}

/// <summary>
/// What a free leaf touches: the bits of a leaf's flags byte.
/// </summary>
/// <remarks>
/// A leaf is merged from smaller ones only when all of them carry the same
/// flags, so a leaf's flags hold for every voxel in it. Contact is judged
/// per voxel against its six face neighbours: a neighbour the agent is
/// blocked in is a contact, and the surface that blocks it (the brush face
/// that separates the agent from the obstacle) says which kind by its normal.
/// </remarks>
[Flags]
public enum Nav3dLeafFlags : byte
{
    /// <summary>No contact: open space.</summary>
    None = 0,

    /// <summary>Something walkable is directly under the agent: a surface whose normal is within the level's slope of straight up.</summary>
    Floor = 1 << 0,

    /// <summary>A surface too steep to walk on touches the agent: a wall, or a slope beyond the limit.</summary>
    Wall = 1 << 1,

    /// <summary>A surface facing down, within the slope of straight down, is directly over the agent.</summary>
    Ceiling = 1 << 2,

    /// <summary>The face neighbour toward +x (east) is blocked.</summary>
    SidePositiveX = 1 << 3,

    /// <summary>The face neighbour toward +y (north) is blocked.</summary>
    SidePositiveY = 1 << 4,

    /// <summary>The face neighbour toward -x (west) is blocked.</summary>
    SideNegativeX = 1 << 5,

    /// <summary>The face neighbour toward -y (south) is blocked.</summary>
    SideNegativeY = 1 << 6,

    /// <summary>The leaf is joined to a leaf of another room through a door (set by the link).</summary>
    Door = 1 << 7,
}

/// <summary>What a point of interest is, beyond its type string: the bits of its flags.</summary>
[Flags]
public enum Nav3dPoiFlags : ushort
{
    /// <summary>Nothing special.</summary>
    None = 0,

    /// <summary>The yaw is meaningful: the author gave the point a facing.</summary>
    HasFacing = 1 << 0,

    /// <summary>The point was made by the compile at a door's centre, one per agent that fits through it.</summary>
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
