//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Rooms;

namespace SourceSharp.MapTools.Nav;

/// <summary>A voxel of a room's cell.</summary>
/// <param name="X">Along x, 0 to 127.</param>
/// <param name="Y">Along y.</param>
/// <param name="Z">Along z.</param>
public readonly record struct NavVoxel(byte X, byte Y, byte Z);

/// <summary>What capping a socket does to one voxel of its room.</summary>
/// <param name="Voxel">The voxel.</param>
/// <param name="Blocks">Whether the capped plug blocks the agent there.</param>
/// <param name="AddFlags">Otherwise, the contact flags the plug adds to the free voxel.</param>
public readonly record struct NavCapChange(NavVoxel Voxel, bool Blocks, Nav3dLeafFlags AddFlags);

/// <summary>One socket of a room, for one agent: its door portal and what capping it changes.</summary>
/// <param name="Portal">
/// The free voxels of the cell's boundary layer at the socket whose
/// neighbour beyond the cell face is free too: where the agent passes
/// through the open door. Empty when the door is too small for the agent.
/// </param>
/// <param name="Capped">The voxels whose class changes when the socket is capped, in voxel order.</param>
public sealed record RoomNavSocket(IReadOnlyList<NavVoxel> Portal, IReadOnlyList<NavCapChange> Capped);

/// <summary>One agent's navigation of a room at one turn: the octree with every door open, and per socket its portal and its cap.</summary>
/// <param name="Nodes">The octree's node words (<see cref="NavOctree"/>).</param>
/// <param name="Leaves">The free leaves.</param>
/// <param name="Sockets">Per socket, in the room's socket order.</param>
public sealed record RoomNavAgent(uint[] Nodes, RoomNavLeaf[] Leaves, IReadOnlyList<RoomNavSocket> Sockets);

/// <summary>A point of interest an author placed in a room, in the room's own coordinates at one turn.</summary>
/// <param name="Position">Its position, room-local (the cell is <c>[0, cell]³</c>).</param>
/// <param name="Yaw">Its facing in degrees, 0 to 360, counter-clockwise from +x.</param>
/// <param name="HasFacing">Whether the author gave it a facing.</param>
/// <param name="Radius">Its radius, 0 for none.</param>
/// <param name="Type">Its type.</param>
/// <param name="Tags">Its tags as written.</param>
/// <param name="Name">Its name, possibly with a room-local prefix (<see cref="RoomLocalNames"/>), or null.</param>
/// <param name="AgentMask">The agents it applies to, bit per agent.</param>
public sealed record RoomNavPoi(
    Vec3 Position, float Yaw, bool HasFacing, float Radius, string Type, string Tags, string? Name, uint AgentMask)
{
    /// <summary>Whether the point is an arrival (<see cref="RoomPois.ArrivalType"/>).</summary>
    public bool IsArrival => Type == RoomPois.ArrivalType;
}

/// <summary>
/// A room's navigation at one turn, as <c>ssmap room</c> precomputes it and
/// the link stitches it: the settings it was built with, the room's role
/// and sockets, its points of interest, and per agent its octree, door
/// portals and caps.
/// </summary>
/// <remarks>
/// Everything is in the cell's own voxels and coordinates at
/// <see cref="Turn"/> quarter turns, so the link places it by adding the
/// cell's offset, never by resampling. Every door is open in the octree;
/// a capped door is the socket's <see cref="RoomNavSocket.Capped"/> changes
/// applied. Opening is the one state a room cannot know alone (it needs the
/// neighbour's door), so it is the state stored, and closing is the
/// precomputed difference.
/// </remarks>
public sealed record RoomNav
{
    /// <summary>The cell's edge.</summary>
    public required float CellSize { get; init; }

    /// <summary>The voxel's edge.</summary>
    public required float VoxelSize { get; init; }

    /// <summary>Voxels along the cell's edge.</summary>
    public required int CellVoxels { get; init; }

    /// <summary>The floor threshold it was built with.</summary>
    public required float FloorNormalZ { get; init; }

    /// <summary>The quarter turns this copy is at, 0 to 3.</summary>
    public required int Turn { get; init; }

    /// <summary>The room's transition role.</summary>
    public required RoomRole Role { get; init; }

    /// <summary>The agents it was built for.</summary>
    public required IReadOnlyList<NavAgentSpec> Agents { get; init; }

    /// <summary>The room's sockets, in its socket order, with their facing at turn 0.</summary>
    public required IReadOnlyList<RoomSocket> Sockets { get; init; }

    /// <summary>The authored points of interest at this turn.</summary>
    public required IReadOnlyList<RoomNavPoi> Pois { get; init; }

    /// <summary>Per agent, its navigation at this turn.</summary>
    public required IReadOnlyList<RoomNavAgent> AgentData { get; init; }

    /// <summary>
    /// This navigation turned by more quarter turns: the octrees rebuilt from
    /// their turned grids, the portals and caps' voxels turned, the points
    /// moved and their facings turned. Exact: a permutation of voxels and a
    /// quarter-turn of coordinates, no resampling.
    /// </summary>
    /// <param name="quarterTurns">Further counter-clockwise quarter turns.</param>
    /// <returns>The navigation at <c>(Turn + quarterTurns) mod 4</c>.</returns>
    public RoomNav Turned(int quarterTurns)
    {
        int r = ((quarterTurns % 4) + 4) % 4;
        if (r == 0)
        {
            return this;
        }

        int n = CellVoxels;
        List<RoomNavAgent> agents = new(AgentData.Count);
        foreach (RoomNavAgent agent in AgentData)
        {
            ushort[] dense = NavOctree.Turn(NavOctree.Expand(agent.Nodes, agent.Leaves, n), n, r);
            (uint[] nodes, RoomNavLeaf[] leaves) = NavOctree.Build(dense, n);
            List<RoomNavSocket> sockets = new(agent.Sockets.Count);
            foreach (RoomNavSocket socket in agent.Sockets)
            {
                sockets.Add(new RoomNavSocket(
                    SortVoxels(socket.Portal.Select(v => TurnVoxel(v, n, r))),
                    [.. socket.Capped
                        .Select(c => c with { Voxel = TurnVoxel(c.Voxel, n, r), AddFlags = NavOctree.TurnFlags(c.AddFlags, r) })
                        .OrderBy(c => Index(c.Voxel, n))]));
            }

            agents.Add(new RoomNavAgent(nodes, leaves, sockets));
        }

        RoomTransform turn = new(new RoomPlacement("turn", 0, 0, r), CellSize);
        return this with
        {
            Turn = (Turn + r) % 4,
            AgentData = agents,
            Pois = [.. Pois.Select(p => p with { Position = turn.Apply(p.Position), Yaw = TurnYaw(p.Yaw, r) })],
        };
    }

    /// <summary>A yaw turned by quarter turns, kept in <c>[0, 360)</c>.</summary>
    /// <param name="yaw">Degrees.</param>
    /// <param name="quarterTurns">Counter-clockwise quarter turns.</param>
    /// <returns>The turned yaw.</returns>
    public static float TurnYaw(float yaw, int quarterTurns)
    {
        float turned = yaw + (90f * (((quarterTurns % 4) + 4) % 4));
        while (turned >= 360f)
        {
            turned -= 360f;
        }

        return turned;
    }

    /// <summary>A voxel's index in a cell's dense grid, x fastest.</summary>
    /// <param name="v">The voxel.</param>
    /// <param name="n">Voxels along the cell's edge.</param>
    /// <returns>The index.</returns>
    public static int Index(NavVoxel v, int n) => (((v.Z * n) + v.Y) * n) + v.X;

    internal static IReadOnlyList<NavVoxel> SortVoxels(IEnumerable<NavVoxel> voxels) =>
        [.. voxels.OrderBy(v => v.Z).ThenBy(v => v.Y).ThenBy(v => v.X)];

    private static NavVoxel TurnVoxel(NavVoxel v, int n, int r)
    {
        (int x, int y) = NavOctree.TurnVoxel(v.X, v.Y, n, r);
        return new NavVoxel((byte)x, (byte)y, v.Z);
    }
}
