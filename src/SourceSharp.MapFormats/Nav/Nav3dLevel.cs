//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapFormats.Nav;

/// <summary>One free leaf: a cube of voxels an agent is free in everywhere, with what it touches.</summary>
/// <param name="X">The leaf's low corner, in level voxels from the level's origin, along x.</param>
/// <param name="Y">The low corner along y.</param>
/// <param name="Z">The low corner along z.</param>
/// <param name="SizeLog2">The leaf's edge is <c>2^SizeLog2</c> voxels.</param>
/// <param name="Flags">What the leaf touches.</param>
/// <param name="Component">The connected component the leaf belongs to.</param>
/// <param name="Cell">The grid cell holding the leaf: <c>row × columns + column</c>.</param>
public readonly record struct Nav3dLeaf(
    ushort X, ushort Y, ushort Z, byte SizeLog2, Nav3dLeafFlags Flags, uint Component, uint Cell)
{
    /// <summary>The leaf's edge in voxels.</summary>
    public int Size => 1 << SizeLog2;

    /// <summary>How many voxels the leaf holds.</summary>
    public long Voxels => 1L << (3 * SizeLog2);
}

/// <summary>A pair of leaves in two rooms joined through a door.</summary>
/// <param name="LeafA">The leaf on the door's side.</param>
/// <param name="LeafB">The leaf on the other side.</param>
/// <param name="Door">The door record, on <paramref name="LeafA"/>'s side.</param>
public readonly record struct Nav3dDoorLink(uint LeafA, uint LeafB, uint Door);

/// <summary>A connected component's size.</summary>
/// <param name="Leaves">How many leaves it holds.</param>
/// <param name="Voxels">How many voxels those leaves hold.</param>
public readonly record struct Nav3dComponent(uint Leaves, uint Voxels);

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
/// <param name="AgentMask">Bit <c>a</c> set when the point applies to agent <c>a</c>.</param>
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

/// <summary>One agent size and its navigation graph.</summary>
/// <param name="Name">The agent's name, as the library configures it.</param>
/// <param name="Mins">The agent's box, relative to its origin (the point a leaf is free for).</param>
/// <param name="Maxs">The box's far corner.</param>
/// <param name="ContentsMask">The <c>CONTENTS_*</c> bits the agent collides with.</param>
public sealed record Nav3dAgent(string Name, Vec3 Mins, Vec3 Maxs, int ContentsMask)
{
    /// <summary>Each cell's root node, -1 for a cell with no room; <c>row × columns + column</c>.</summary>
    public int[] Roots { get; init; } = [];

    /// <summary>The octree nodes of every cell, back to back.</summary>
    public uint[] Nodes { get; init; } = [];

    /// <summary>The free leaves.</summary>
    public Nav3dLeaf[] Leaves { get; init; } = [];

    /// <summary>Where each leaf's neighbours start in <see cref="Adjacency"/>; one more entry than leaves.</summary>
    public uint[] AdjacencyStart { get; init; } = [0];

    /// <summary>The neighbour lists: a leaf index, with <see cref="Nav3dFormat.ThroughDoorBit"/> when the step crosses a door.</summary>
    public uint[] Adjacency { get; init; } = [];

    /// <summary>The leaf pairs joined through doors.</summary>
    public Nav3dDoorLink[] Links { get; init; } = [];

    /// <summary>The connected components.</summary>
    public Nav3dComponent[] Components { get; init; } = [];

    /// <summary>Each point of interest's leaf for this agent, -1 when it does not apply or lies in no free leaf.</summary>
    public int[] PoiLeaves { get; init; } = [];
}

/// <summary>
/// A whole <c>.nav3d</c> file as data: what the linker builds and
/// <see cref="Nav3dWriter"/> writes, and what <see cref="Nav3dReader.ToLevel"/>
/// reads back.
/// </summary>
/// <remarks>
/// The level is a grid of cubic cells, <see cref="Columns"/> west to east by
/// <see cref="Rows"/> south to north, each <see cref="CellSize"/> units on a
/// side and split into <see cref="CellVoxels"/> voxels along each edge. Cell
/// (column, row) spans <c>Origin + (column × CellSize, row × CellSize, 0)</c>
/// to that plus <c>CellSize</c> on every axis. Each agent has one octree per
/// occupied cell.
/// </remarks>
public sealed class Nav3dLevel
{
    /// <summary>The cells' edge, in units.</summary>
    public float CellSize { get; init; }

    /// <summary>A voxel's edge, in units: <see cref="CellSize"/> / <see cref="CellVoxels"/>.</summary>
    public float VoxelSize { get; init; }

    /// <summary>Voxels along a cell's edge.</summary>
    public int CellVoxels { get; init; }

    /// <summary>The grid's columns, west to east.</summary>
    public int Columns { get; init; }

    /// <summary>The grid's rows, south to north.</summary>
    public int Rows { get; init; }

    /// <summary>The grid's low corner in level coordinates.</summary>
    public Vec3 Origin { get; init; }

    /// <summary>The least normal z a floor may have: the cosine of the steepest walkable slope.</summary>
    public float FloorNormalZ { get; init; }

    /// <summary>The cells, <c>row × columns + column</c>.</summary>
    public IReadOnlyList<Nav3dCell> Cells { get; init; } = [];

    /// <summary>Every socket of every placed room.</summary>
    public IReadOnlyList<Nav3dDoor> Doors { get; init; } = [];

    /// <summary>The points of interest.</summary>
    public IReadOnlyList<Nav3dPoi> Pois { get; init; } = [];

    /// <summary>The agents and their graphs.</summary>
    public IReadOnlyList<Nav3dAgent> Agents { get; init; } = [];

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

    /// <summary>The octree depth each cell's root has.</summary>
    public int OctreeDepth => Nav3dFormat.DepthFor(Math.Max(1, CellVoxels));
}
