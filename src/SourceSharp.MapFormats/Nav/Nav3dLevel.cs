//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapFormats.Nav;

/// <summary>
/// One leaf: a vertical run of voxels in one column, every voxel of it with
/// the same clearance, flags and cost.
/// </summary>
/// <param name="ZLo">The run's lowest voxel, counted from the cell's floor.</param>
/// <param name="Height">How many voxels the run holds, at least 1.</param>
/// <param name="Flags">Water, ladder, and per clip class whether it stands on a floor and whether that floor is walkable.</param>
/// <param name="Cost">The traversal cost multiplier in 8.8 fixed point: 256 is 1.0.</param>
/// <param name="PlayerClearance">The player class's clearance record: its offset in the clearance section.</param>
/// <param name="NpcClearance">The NPC class's clearance record.</param>
/// <param name="PlayerFloorZ">For the player class, the altitude of the surface under the run when <see cref="Nav3dLeafFlags.GroundedPlayer"/>; else 0.</param>
/// <param name="NpcFloorZ">For the NPC class, likewise.</param>
public readonly record struct Nav3dLeaf(
    byte ZLo, byte Height, Nav3dLeafFlags Flags, ushort Cost, uint PlayerClearance, uint NpcClearance, float PlayerFloorZ, float NpcFloorZ)
{
    /// <summary>The run's highest voxel.</summary>
    public int ZHi => ZLo + Height - 1;

    /// <summary>The cost as a multiplier.</summary>
    public float CostMultiplier => Cost / 256f;

    /// <summary>A clip class's clearance record offset.</summary>
    /// <param name="clipClass">The class.</param>
    /// <returns>The offset.</returns>
    public uint Clearance(Nav3dClipClass clipClass) => clipClass == Nav3dClipClass.Npc ? NpcClearance : PlayerClearance;

    /// <summary>Whether the run stands on solid for a clip class.</summary>
    /// <param name="clipClass">The class.</param>
    /// <returns>True when grounded.</returns>
    public bool IsGrounded(Nav3dClipClass clipClass) =>
        (Flags & (clipClass == Nav3dClipClass.Npc ? Nav3dLeafFlags.GroundedNpc : Nav3dLeafFlags.GroundedPlayer)) != 0;

    /// <summary>Whether the run's floor is walkable for a clip class (grounded, and not too steep).</summary>
    /// <param name="clipClass">The class.</param>
    /// <returns>True when walkable.</returns>
    public bool IsWalkable(Nav3dClipClass clipClass) =>
        (Flags & (clipClass == Nav3dClipClass.Npc ? Nav3dLeafFlags.WalkableNpc : Nav3dLeafFlags.WalkablePlayer)) != 0;

    /// <summary>The floor's altitude for a clip class, meaningful when <see cref="IsGrounded"/>.</summary>
    /// <param name="clipClass">The class.</param>
    /// <returns>The altitude.</returns>
    public float FloorZ(Nav3dClipClass clipClass) => clipClass == Nav3dClipClass.Npc ? NpcFloorZ : PlayerFloorZ;
}

/// <summary>An agent preset: a size and clip class the level's author named, recorded for the runtime's convenience.</summary>
/// <param name="Name">The preset's name.</param>
/// <param name="Width">The box's width and depth.</param>
/// <param name="Height">The box's height, from the origin (the feet) up.</param>
/// <param name="ClipClass">Which clip brushes it collides with.</param>
/// <remarks>
/// The grid does not depend on presets: any size is answered exactly. A
/// reader derives its connected components per preset at load, and a point
/// of interest's agent mask names presets.
/// </remarks>
public sealed record Nav3dPreset(string Name, float Width, float Height, Nav3dClipClass ClipClass);

/// <summary>A dynamic obstacle: an entity the grid treats as open space, whose leaves the runtime blocks while it is in the way.</summary>
/// <param name="Name">Its <c>targetname</c>, room-local names resolved for its cell, or null when it has none.</param>
/// <param name="ClassName">Its classname.</param>
/// <param name="Cell">The cell of the room it belongs to.</param>
/// <param name="HammerId">Its <c>hammerid</c> in the room, or -1: with the cell, what finds an unnamed entity.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Mins">Its bounds' low corner, level coordinates, where the map places it.</param>
/// <param name="Maxs">Its bounds' high corner.</param>
public sealed record Nav3dObstacle(
    string? Name, string ClassName, uint Cell, int HammerId, Nav3dObstacleKind Kind, Vec3 Mins, Vec3 Maxs);

/// <summary>A jump link: two floors a jumping agent can travel between, up by jumping, down by dropping.</summary>
/// <param name="LeafA">The lower-numbered leaf.</param>
/// <param name="LeafB">The other.</param>
/// <param name="Rise">B's floor minus A's, for the classes in the mask (the floors are the same for both when both are set).</param>
/// <param name="ClassMask">Bit 0 when the link holds for the player class, bit 1 for the NPC class.</param>
/// <param name="Direction">The horizontal direction from A to B (<see cref="Nav3dDirection"/>, 0 to 3).</param>
/// <param name="Columns">How many columns apart the two are: 1 for a ledge, more across a gap.</param>
public readonly record struct Nav3dJump(uint LeafA, uint LeafB, float Rise, byte ClassMask, Nav3dDirection Direction, byte Columns);

/// <summary>One grid cell of the level.</summary>
/// <param name="Room">The placed room's name, or null for an empty cell.</param>
/// <param name="Rotation">Quarter turns counter-clockwise seen from above, 0 to 3.</param>
/// <param name="Role">The room's transition role.</param>
/// <param name="Joined">The world directions (bit 0 east, 1 north, 2 west, 3 south) whose door the level joins.</param>
/// <param name="Capped">The world directions whose door the level caps.</param>
public sealed record Nav3dCell(string? Room, byte Rotation, Nav3dRoomRole Role, byte Joined, byte Capped);

/// <summary>One socket of a placed room.</summary>
/// <param name="Cell">The cell of the room it belongs to.</param>
/// <param name="Direction">The world direction it faces: 0 east (+x), 1 north (+y), 2 west (-x), 3 south (-y).</param>
/// <param name="Joined">Whether the level joins it to the facing room's socket.</param>
/// <param name="Name">The socket's name in its room.</param>
/// <param name="Other">The facing socket's door index when joined, else -1.</param>
public sealed record Nav3dDoor(uint Cell, byte Direction, bool Joined, string Name, int Other);

/// <summary>A point of interest: a place the level's author (or the compile) marked for the AI.</summary>
/// <param name="Position">Where it is, in level coordinates (Source units, z up).</param>
/// <param name="Yaw">Its facing in degrees counter-clockwise from +x seen from above, 0 to 360; meaningful with <see cref="Nav3dPoiFlags.HasFacing"/>.</param>
/// <param name="Radius">Its radius in units, 0 when it has none.</param>
/// <param name="Type">Its type: <c>cover</c>, <c>vantage</c>, <c>spawn</c>, <c>patrol</c>, <c>interaction</c>, <c>arrival</c>, <c>door</c>, or the author's own.</param>
/// <param name="Tags">Its tags as written: comma-separated, possibly empty.</param>
/// <param name="Name">Its name with any room-local prefix resolved, or null.</param>
/// <param name="Cell">The cell of the room it belongs to.</param>
/// <param name="AgentMask">Bit <c>a</c> set when the point applies to preset <c>a</c>.</param>
/// <param name="Door">For a door point, its door record; else -1.</param>
/// <param name="Flags">What the point is.</param>
/// <param name="Role">The transition role of the room it is in.</param>
public sealed record Nav3dPoi(
    Vec3 Position,
    float Yaw,
    float Radius,
    string Type,
    string Tags,
    string? Name,
    uint Cell,
    uint AgentMask,
    int Door,
    Nav3dPoiFlags Flags,
    Nav3dRoomRole Role);

/// <summary>
/// A whole <c>.nav3d</c> file as data: what the linker builds and
/// <see cref="Nav3dWriter"/> writes, and what <see cref="Nav3dReader.ToLevel"/>
/// reads back.
/// </summary>
/// <remarks>
/// <para>
/// The level is a grid of cubic cells, <see cref="Columns"/> west to east by
/// <see cref="Rows"/> south to north, each <see cref="CellSize"/> units on a
/// side and split into <see cref="CellVoxels"/> voxels along each edge. Cell
/// (column, row) spans <c>Origin + (column × CellSize, row × CellSize, 0)</c>
/// to that plus <c>CellSize</c> on every axis; a cell whose room has a
/// height of its own (<see cref="CellHeights"/>, version 3) is that many
/// voxels tall instead.
/// </para>
/// <para>
/// Each placed cell has <c>CellVoxels²</c> voxel columns, stored as a block
/// in <see cref="ColumnStarts"/> from the cell's <see cref="Roots"/> entry,
/// x fastest. A column's free space is a list of leaves, each a run of voxels
/// with one clearance, low to high; solid is simply not listed. Nothing in
/// the level depends on the agent presets: they are recorded for the
/// runtime's convenience.
/// </para>
/// </remarks>
public sealed class Nav3dLevel
{
    /// <summary>The cells' edge, in units.</summary>
    public float CellSize { get; init; }

    /// <summary>A voxel's edge, in units: <see cref="CellSize"/> / <see cref="CellVoxels"/>.</summary>
    public float VoxelSize { get; init; }

    /// <summary>Voxels along a cell's edge.</summary>
    public int CellVoxels { get; init; }

    /// <summary>
    /// Each cell's height in voxels, <c>row × columns + column</c>, 0 for an
    /// empty cell; or null when every placed cell is <see cref="CellVoxels"/>
    /// tall (a level of cube rooms). Set, the file is version 3 with a
    /// <see cref="Nav3dFormat.CellHeightsTag"/> section; null, it is version
    /// 2, as it always was.
    /// </summary>
    /// <remarks>
    /// A cell's columns run from the floor (z = 0) to its height: a room
    /// taller than its cell has more voxels up than along, a low room fewer.
    /// Floors all stand at z = 0 (the rooms design, 17.6), so the columns of
    /// two cells side by side line up voxel for voxel as far as the lower
    /// one goes.
    /// </remarks>
    public IReadOnlyList<int>? CellHeights { get; init; }

    /// <summary>A cell's height in voxels: its <see cref="CellHeights"/> entry, or <see cref="CellVoxels"/> without them.</summary>
    /// <param name="cell">The cell, <c>row × columns + column</c>.</param>
    /// <returns>The height.</returns>
    public int CellHeight(int cell) => CellHeights is { } heights ? heights[cell] : CellVoxels;

    /// <summary>The grid's columns, west to east.</summary>
    public int Columns { get; init; }

    /// <summary>The grid's rows, south to north.</summary>
    public int Rows { get; init; }

    /// <summary>The grid's low corner in level coordinates.</summary>
    public Vec3 Origin { get; init; }

    /// <summary>The least normal z a walkable floor may have: the cosine of the steepest walkable slope.</summary>
    public float FloorNormalZ { get; init; }

    /// <summary>The highest step an agent walks up without jumping: floors of neighbouring leaves this far apart or less are one walk.</summary>
    public float StepHeight { get; init; }

    /// <summary>The highest ledge a jump reaches, and the deepest drop a jump link stands for.</summary>
    public float JumpHeight { get; init; }

    /// <summary>The farthest a jump carries, between column centres.</summary>
    public float JumpDistance { get; init; }

    /// <summary>The cells, <c>row × columns + column</c>.</summary>
    public IReadOnlyList<Nav3dCell> Cells { get; init; } = [];

    /// <summary>Every socket of every placed room.</summary>
    public IReadOnlyList<Nav3dDoor> Doors { get; init; } = [];

    /// <summary>The points of interest.</summary>
    public IReadOnlyList<Nav3dPoi> Pois { get; init; } = [];

    /// <summary>The agent presets.</summary>
    public IReadOnlyList<Nav3dPreset> Presets { get; init; } = [];

    /// <summary>Each cell's first column, -1 for a cell with no room; <c>row × columns + column</c>.</summary>
    public int[] Roots { get; init; } = [];

    /// <summary>Where each column's leaves start in <see cref="Leaves"/>; one more entry than columns.</summary>
    public uint[] ColumnStarts { get; init; } = [0];

    /// <summary>The leaves, column after column, each column's low to high.</summary>
    public Nav3dLeaf[] Leaves { get; init; } = [];

    /// <summary>The clearance records the leaves point into, back to back (<see cref="Nav3dClearance"/>).</summary>
    public byte[] Clearance { get; init; } = [];

    /// <summary>The dynamic obstacles the clearance records name.</summary>
    public IReadOnlyList<Nav3dObstacle> Obstacles { get; init; } = [];

    /// <summary>The overhanging brushes the clearance records name: each four <c>float32</c> per plane.</summary>
    public IReadOnlyList<float[]> Brushes { get; init; } = [];

    /// <summary>The jump links.</summary>
    public IReadOnlyList<Nav3dJump> Jumps { get; init; } = [];

    /// <summary>
    /// The point a player spawns at on a fresh start, or -1. The linker sets
    /// it to <see cref="UpArrivalPoi"/>; a level with no up room has none
    /// until the transition design names another rule.
    /// </summary>
    public int SpawnPoi { get; init; } = -1;

    /// <summary>The up room's arrival point, or -1.</summary>
    public int UpArrivalPoi { get; init; } = -1;

    /// <summary>The down room's arrival point, or -1.</summary>
    public int DownArrivalPoi { get; init; } = -1;

    /// <summary>
    /// The level id the linker derived and also wrote into the map's
    /// worldspawn (<c>ss_level_id</c>): equal in both exactly when the two
    /// files come from one link. <see cref="Guid.Empty"/> when unknown.
    /// </summary>
    public Guid LevelId { get; init; }

    /// <summary>The id of the room pack the level's rooms came from (<c>ss_pack_id</c>), or <see cref="Guid.Empty"/>.</summary>
    public Guid PackId { get; init; }
}
