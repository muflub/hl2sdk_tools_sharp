//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Rooms;

namespace SourceSharp.MapTools.Nav;

/// <summary>
/// Builds a room's navigation from its compile: the voxel classification
/// for each agent with every door open, what capping each door changes,
/// each door's portal, and the room's points of interest checked and
/// placed.
/// </summary>
/// <remarks>
/// <para>
/// <b>The room alone stands for the level.</b> A room is voxelised without
/// its neighbours, so what lies beyond its cell has to be assumed, and the
/// assumption is the kit: outside the cell is solid, except that behind
/// each open door the neighbour's side of the wall has the same opening,
/// <c>wall_depth</c> deep (every room of a library shares one kit, and a
/// joined door always meets the same door), and past that wall is the
/// neighbour's room, taken to be open near its door. The door's own plug
/// brush is left out for the open state and put back for the capped one,
/// with the neighbour's side solid. Where an agent's half-width is at most
/// <c>wall_depth</c> (the sample kit's 16 and the player's 16), a door
/// voxel's own swept box never reaches past the neighbour's wall, so its
/// free or blocked class is exact; what the assumption decides is only
/// the side flag of a door voxel whose outward neighbour is the
/// neighbour's first voxel, which is exact when the neighbour has nothing
/// within half an agent and a voxel of the inside of its door. The facts
/// check the sample levels voxel for voxel against the whole-map compile.
/// </para>
/// <para>
/// <b>Caps compose.</b> Capping a door adds solids and never removes any, so
/// a voxel's class with a set of doors capped is its open class with each
/// capped door's changes applied, and the changes of two doors never
/// disagree. Each socket stores only its own changes, and a placement with
/// any doors capped is one pass over those lists at link.
/// </para>
/// <para>
/// <b>Rotation is not recomputed.</b> The room is built at turn 0 only; its
/// other turns are the same answer permuted (<see cref="RoomNav.Turned"/>),
/// which a fact checks against voxelising the room turned.
/// </para>
/// </remarks>
public static class RoomNavBuilder
{
    /// <summary>How close a brush's bounds must come to a socket's plug box to be the plug.</summary>
    private const float PlugTolerance = 0.01f;

    /// <summary>Builds a room's navigation from its compiled BSP.</summary>
    /// <param name="definition">The room.</param>
    /// <param name="bsp">The room's compiled BSP, room-local, plugs in.</param>
    /// <param name="pois">The room's points of interest (<see cref="RoomPois.Extract"/>).</param>
    /// <param name="role">The room's transition role.</param>
    /// <param name="settings">The library's navigation settings.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The room's navigation at turn 0.</returns>
    /// <exception cref="RoomLibraryException">The settings do not fit the room's cell.</exception>
    /// <exception cref="RoomLintException">A point of interest is where its agents cannot be, or names an unknown agent.</exception>
    public static RoomNav Build(
        RoomDefinition definition,
        BspData bsp,
        IReadOnlyList<AuthoredPoi> pois,
        RoomRole role,
        NavSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        return Build(definition, NavBrush.FromBsp(bsp), pois, role, settings, cancellationToken);
    }

    /// <summary>Builds a room's navigation from its brushes (room-local, plugs in).</summary>
    /// <param name="definition">The room.</param>
    /// <param name="brushes">The room's brushes; a brush filling a socket's plug box is that socket's plug.</param>
    /// <param name="pois">The room's points of interest.</param>
    /// <param name="role">The room's transition role.</param>
    /// <param name="settings">The library's navigation settings.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The room's navigation at turn 0.</returns>
    public static RoomNav Build(
        RoomDefinition definition,
        IReadOnlyList<NavBrush> brushes,
        IReadOnlyList<AuthoredPoi> pois,
        RoomRole role,
        NavSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(brushes);
        ArgumentNullException.ThrowIfNull(pois);
        ArgumentNullException.ThrowIfNull(settings);
        definition.Validate();

        float cell = definition.CellSize;
        int n = settings.CellVoxels(cell);
        NavRegion region = new(0, 0, 0, n, n, n, settings.VoxelSize);

        // The plugs, found by their boxes, and everything else.
        int[] plugOf = new int[definition.Sockets.Count];
        Array.Fill(plugOf, -1);
        List<NavBrush> fixedBrushes = [];
        for (int b = 0; b < brushes.Count; b++)
        {
            int socket = PlugSocket(definition, brushes[b]);
            if (socket >= 0 && plugOf[socket] < 0)
            {
                plugOf[socket] = b;
            }
            else
            {
                fixedBrushes.Add(brushes[b]);
            }
        }

        List<NavBrush> open = [.. fixedBrushes, .. Outside(definition, capped: -1)];
        List<RoomNavAgent> agents = [];
        NavVoxelGrid[] openGrids = new NavVoxelGrid[settings.Agents.Count];
        for (int a = 0; a < settings.Agents.Count; a++)
        {
            NavAgentSpec agent = settings.Agents[a];
            NavVoxelGrid grid = NavVoxeliser.Classify(open, region, agent, settings.FloorNormalZ, cancellationToken);
            openGrids[a] = grid;
            List<RoomNavSocket> sockets = [];
            for (int s = 0; s < definition.Sockets.Count; s++)
            {
                List<NavBrush> capped = [.. fixedBrushes, .. Outside(definition, capped: s)];
                if (plugOf[s] >= 0)
                {
                    capped.Add(brushes[plugOf[s]]);
                }

                NavVoxelGrid closed = NavVoxeliser.Classify(capped, region, agent, settings.FloorNormalZ, cancellationToken);
                sockets.Add(new RoomNavSocket(Portal(grid, definition.Sockets[s].Facing), CapChanges(grid, closed)));
            }

            (uint[] nodes, RoomNavLeaf[] leaves) = NavOctree.Build(grid.Cells, n);
            agents.Add(new RoomNavAgent(nodes, leaves, sockets));
        }

        return new RoomNav
        {
            CellSize = cell,
            VoxelSize = settings.VoxelSize,
            CellVoxels = n,
            FloorNormalZ = settings.FloorNormalZ,
            Turn = 0,
            Role = role,
            Agents = settings.Agents,
            Sockets = definition.Sockets,
            Pois = PlacePois(definition, pois, settings, openGrids),
            AgentData = agents,
        };
    }

    /// <summary>
    /// The solid the room assumes beyond its cell: a thick slab past each of
    /// the six faces; but past a wall with an open socket, only the
    /// neighbour's side of that wall, <c>wall_depth</c> deep, with the same
    /// opening, and nothing beyond it (the neighbour's room, open near its
    /// door). <paramref name="capped"/> names the one socket shut, or -1 for
    /// every socket open.
    /// </summary>
    /// <remarks>
    /// Nothing, rather than solid, past the neighbour's wall: the voxel just
    /// past the face (the neighbour's own first voxel, which decides whether
    /// a door voxel's outward side is blocked) sweeps the agent's box half a
    /// width past the neighbour's wall, into the neighbour's room. Solid
    /// there shut every door; empty there is what the neighbour's room is
    /// near a door it lets agents through. The link joins two rooms only
    /// where both sides' door voxels are free, so this never invents a
    /// passage.
    /// </remarks>
    internal static List<NavBrush> Outside(RoomDefinition definition, int capped)
    {
        float c = definition.CellSize;
        float big = (2 * c) + 4096;
        float depth = definition.Kit.Depth;
        int solid = 1;
        List<NavBrush> boxes =
        [
            NavBrush.Box(new Vec3(-big, -big, -big), new Vec3(c + big, c + big, 0), solid),
            NavBrush.Box(new Vec3(-big, -big, c), new Vec3(c + big, c + big, c + big), solid),
        ];

        foreach (RoomFacing facing in Enum.GetValues<RoomFacing>())
        {
            int socket = -1;
            for (int s = 0; s < definition.Sockets.Count; s++)
            {
                if (definition.Sockets[s].Facing == facing)
                {
                    socket = s;
                }
            }

            (int axis, bool high) = facing switch
            {
                RoomFacing.PositiveX => (0, true),
                RoomFacing.NegativeX => (0, false),
                RoomFacing.PositiveY => (1, true),
                _ => (1, false),
            };

            (float nearLo, float nearHi) = high ? (c, c + depth) : (-depth, 0f);
            if (socket < 0 || socket == capped)
            {
                boxes.Add(Slab(axis, high ? c : -big, high ? c + big : 0, -big, c + big, -big, c + big));
                continue;
            }

            Box plug = RoomLinter.SealBox(definition, definition.Sockets[socket], c);
            (float u0, float u1) = axis == 0 ? (plug.Mins.Y, plug.Maxs.Y) : (plug.Mins.X, plug.Maxs.X);
            (float v0, float v1) = (plug.Mins.Z, plug.Maxs.Z);
            boxes.Add(Slab(axis, nearLo, nearHi, -big, u0, -big, c + big));
            boxes.Add(Slab(axis, nearLo, nearHi, u1, c + big, -big, c + big));
            boxes.Add(Slab(axis, nearLo, nearHi, u0, u1, -big, v0));
            boxes.Add(Slab(axis, nearLo, nearHi, u0, u1, v1, c + big));
        }

        return boxes;
    }

    /// <summary>A box given its range along a horizontal axis, the other horizontal axis and z.</summary>
    private static NavBrush Slab(int axis, float aLo, float aHi, float bLo, float bHi, float zLo, float zHi) =>
        axis == 0
            ? NavBrush.Box(new Vec3(aLo, bLo, zLo), new Vec3(aHi, bHi, zHi), 1)
            : NavBrush.Box(new Vec3(bLo, aLo, zLo), new Vec3(bHi, aHi, zHi), 1);

    /// <summary>Which socket's plug box a brush fills, or -1.</summary>
    private static int PlugSocket(RoomDefinition definition, NavBrush brush)
    {
        for (int s = 0; s < definition.Sockets.Count; s++)
        {
            Box plug = RoomLinter.SealBox(definition, definition.Sockets[s], definition.CellSize);
            if (Math.Abs(brush.MinX - plug.Mins.X) <= PlugTolerance && Math.Abs(brush.MinY - plug.Mins.Y) <= PlugTolerance
                && Math.Abs(brush.MinZ - plug.Mins.Z) <= PlugTolerance && Math.Abs(brush.MaxX - plug.Maxs.X) <= PlugTolerance
                && Math.Abs(brush.MaxY - plug.Maxs.Y) <= PlugTolerance && Math.Abs(brush.MaxZ - plug.Maxs.Z) <= PlugTolerance)
            {
                return s;
            }
        }

        return -1;
    }

    /// <summary>The free voxels of the boundary layer at a face whose neighbour past the face is free too.</summary>
    internal static IReadOnlyList<NavVoxel> Portal(NavVoxelGrid grid, RoomFacing facing)
    {
        int n = grid.Region.SizeX;
        List<NavVoxel> portal = [];
        for (int z = 0; z < n; z++)
        {
            for (int w = 0; w < n; w++)
            {
                (int x, int y, int bx, int by) = facing switch
                {
                    RoomFacing.PositiveX => (n - 1, w, n, w),
                    RoomFacing.NegativeX => (0, w, -1, w),
                    RoomFacing.PositiveY => (w, n - 1, w, n),
                    _ => (w, 0, w, -1),
                };
                if ((grid[x, y, z] & NavVoxelGrid.FreeBit) != 0 && !grid.IsBlockedWithRing(bx, by, z))
                {
                    portal.Add(new NavVoxel((byte)x, (byte)y, (byte)z));
                }
            }
        }

        return RoomNav.SortVoxels(portal);
    }

    /// <summary>Every voxel whose class differs between the open and the capped grid, in voxel order.</summary>
    private static List<NavCapChange> CapChanges(NavVoxelGrid open, NavVoxelGrid capped)
    {
        int n = open.Region.SizeX;
        List<NavCapChange> changes = [];
        ReadOnlySpan<ushort> a = open.Cells;
        ReadOnlySpan<ushort> b = capped.Cells;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i])
            {
                continue;
            }

            NavVoxel voxel = new((byte)(i % n), (byte)(i / n % n), (byte)(i / (n * n)));
            changes.Add((b[i] & NavVoxelGrid.FreeBit) == 0
                ? new NavCapChange(voxel, true, Nav3dLeafFlags.None)
                : new NavCapChange(voxel, false, (Nav3dLeafFlags)(b[i] & ~a[i] & 0xFF)));
        }

        return changes;
    }

    /// <summary>Checks each point against the agents it applies to, and turns it into the stored form.</summary>
    private static List<RoomNavPoi> PlacePois(
        RoomDefinition definition, IReadOnlyList<AuthoredPoi> pois, NavSettings settings, NavVoxelGrid[] grids)
    {
        List<RoomNavPoi> placed = [];
        uint all = settings.Agents.Count == 32 ? uint.MaxValue : (1u << settings.Agents.Count) - 1;
        foreach (AuthoredPoi poi in pois)
        {
            string who = $"room \"{definition.Name}\": {RoomPois.Entity} {poi.Id} at ({Fmt(poi.Origin)})";
            uint mask = 0;
            foreach (string name in poi.Agents)
            {
                int found = -1;
                for (int a = 0; a < settings.Agents.Count; a++)
                {
                    if (settings.Agents[a].Name == name)
                    {
                        found = a;
                    }
                }

                if (found < 0)
                {
                    throw new RoomLintException(
                        $"{who} applies to agent \"{name}\", which the library's {NavSettings.AgentsKey} does not name"
                        + $" ({string.Join(", ", settings.Agents.Select(x => x.Name))}).");
                }

                mask |= 1u << found;
            }

            if (mask == 0)
            {
                mask = all;
            }

            bool arrival = poi.Type == RoomPois.ArrivalType;
            if (arrival)
            {
                int player = settings.PlayerAgent;
                if (player < 0)
                {
                    throw new RoomLintException($"{who} is an arrival point, but no agent of the library collides like the player.");
                }

                if (poi.Agents.Count > 0 && mask != 1u << player)
                {
                    throw new RoomLintException(
                        $"{who} is an arrival point, which applies to the player's agent \"{settings.Agents[player].Name}\" alone.");
                }

                mask = 1u << player;
            }

            for (int a = 0; a < settings.Agents.Count; a++)
            {
                if ((mask & (1u << a)) == 0)
                {
                    continue;
                }

                NavVoxelGrid grid = grids[a];
                int n = grid.Region.SizeX;
                int x = VoxelOf(poi.Origin.X, grid.Region.VoxelSize, n);
                int y = VoxelOf(poi.Origin.Y, grid.Region.VoxelSize, n);
                int z = VoxelOf(poi.Origin.Z, grid.Region.VoxelSize, n);
                ushort code = grid[x, y, z];
                if ((code & NavVoxelGrid.FreeBit) == 0)
                {
                    throw new RoomLintException(
                        $"{who} is where agent \"{settings.Agents[a].Name}\" does not fit (voxel {x} {y} {z} is blocked for it);"
                        + " a point of interest stands where the agents it applies to can stand.");
                }

                if (arrival && ((Nav3dLeafFlags)(code & 0xFF) & Nav3dLeafFlags.Floor) == 0)
                {
                    throw new RoomLintException(
                        $"{who} is an arrival point off the floor (voxel {x} {y} {z} has no floor under it); a player arrives standing.");
                }
            }

            placed.Add(new RoomNavPoi(poi.Origin, poi.Yaw, poi.HasFacing, poi.Radius, poi.Type, poi.Tags, poi.Name, mask));
        }

        return placed;
    }

    /// <summary>The voxel holding a coordinate: half-open voxels, the cell's far face counted in its last voxel.</summary>
    internal static int VoxelOf(float coordinate, double voxel, int n) =>
        (int)Math.Clamp(Math.Floor(coordinate / voxel), 0, n - 1);

    private static string Fmt(Vec3 v) =>
        string.Create(CultureInfo.InvariantCulture, $"{v.X:0.###} {v.Y:0.###} {v.Z:0.###}");
}
