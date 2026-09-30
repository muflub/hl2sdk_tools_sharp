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
/// Builds a room's navigation from its compile: the clearance grid of the
/// cell with every door open, what capping each door changes, each door's
/// point, the room's dynamic obstacles and overhanging brushes, and its
/// points of interest checked against the library's presets.
/// </summary>
/// <remarks>
/// <para>
/// <b>The room alone stands for the level.</b> A room is built without its
/// neighbours, so what lies beyond its cell has to be assumed, and the
/// assumption is the kit: outside the cell is solid, except that behind
/// each open door the neighbour's side of the wall has the same opening,
/// <c>wall_depth</c> deep (every room of a library shares one kit, and a
/// joined door always meets the same door), and past that wall is the
/// neighbour's room, taken to be open near its door. The door's own plug
/// brush is left out for the open state and put back for the capped one,
/// with the neighbour's side solid.
/// </para>
/// <para>
/// <b>Why that is exact at the seams.</b> A voxel's record lists only the
/// obstacles no nearer one dominates. Beside a doorway the door's jambs (the
/// wall either side of the opening, floor to top) are at most half the
/// opening's width away sideways and block every height, so anything in the
/// neighbour's room farther than the jambs is dominated and cannot change a
/// record. The stitched grid therefore equals the whole level's where every
/// room keeps the inside of each door clear out to the jambs' distance plus
/// a voxel; a fact checks the sample levels voxel for voxel, clearances
/// included, against the flattened level's compile.
/// </para>
/// <para>
/// <b>Caps compose.</b> Capping a door adds solids and never removes any
/// the grid counts (the neighbour's wall pieces it replaces lie inside the
/// solid slab it puts behind the face), so a voxel's record with a set of
/// doors capped is its records with each capped door's solids merged
/// (<see cref="NavRecord.Merge"/>), and the link merges exactly that way.
/// </para>
/// <para>
/// <b>Rotation is not recomputed.</b> The room is built at turn 0 only; its
/// other turns are the same answer permuted (<see cref="RoomNav.Turned"/>),
/// which a fact checks against building the room turned.
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
    /// <param name="modelBounds">A prop model's hull by its path, or null when unknown; null leaves props out.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The room's navigation at turn 0.</returns>
    /// <exception cref="RoomLibraryException">The settings do not fit the room's cell.</exception>
    /// <exception cref="RoomLintException">A point of interest is where its presets cannot be, or names an unknown preset.</exception>
    public static RoomNav Build(
        RoomDefinition definition,
        BspData bsp,
        IReadOnlyList<AuthoredPoi> pois,
        RoomRole role,
        NavSettings settings,
        Func<string, (Vec3 Mins, Vec3 Maxs)?>? modelBounds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        return Build(definition, NavGeometry.FromBsp(bsp, modelBounds), pois, role, settings, cancellationToken);
    }

    /// <summary>Builds a room's navigation from its geometry (room-local, plugs in).</summary>
    /// <param name="definition">The room.</param>
    /// <param name="geometry">The room's geometry; a world brush filling a socket's plug box is that socket's plug.</param>
    /// <param name="pois">The room's points of interest.</param>
    /// <param name="role">The room's transition role.</param>
    /// <param name="settings">The library's navigation settings.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The room's navigation at turn 0.</returns>
    public static RoomNav Build(
        RoomDefinition definition,
        NavGeometry geometry,
        IReadOnlyList<AuthoredPoi> pois,
        RoomRole role,
        NavSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(pois);
        ArgumentNullException.ThrowIfNull(settings);
        definition.Validate();

        // The room's own box (17.6): its cell's footprint, its height up; a
        // cube's n x n x n.
        float cell = definition.CellSize;
        int n = settings.CellVoxels(cell);
        int nz = settings.ColumnVoxels(definition);
        NavRegion region = new(0, 0, 0, n, n, nz, settings.VoxelSize);

        // The plugs, found by their boxes, and everything else.
        int[] plugOf = new int[definition.Sockets.Count];
        Array.Fill(plugOf, -1);
        List<NavBrush> fixedBrushes = [];
        for (int b = 0; b < geometry.Brushes.Count; b++)
        {
            int socket = PlugSocket(definition, geometry.Brushes[b]);
            if (socket >= 0 && plugOf[socket] < 0)
            {
                plugOf[socket] = b;
            }
            else
            {
                fixedBrushes.Add(geometry.Brushes[b]);
            }
        }

        NavGeometry fixedGeometry = new()
        {
            Brushes = fixedBrushes,
            Obstacles = geometry.Obstacles,
            Ladders = geometry.Ladders,
            Warnings = geometry.Warnings,
        };
        NavRecordTable records = new();
        NavGeometry open = fixedGeometry.With(Outside(definition, capped: -1));
        NavGrid grid = NavClearanceBuilder.Build(open, region, settings, records, cancellationToken);
        IReadOnlyList<NavBrush> overhang = NavClearanceBuilder.OverhangBrushes(open);

        List<RoomNavSocket> sockets = [];
        for (int s = 0; s < definition.Sockets.Count; s++)
        {
            List<NavBrush> shut = [.. Outside(definition, capped: s)];
            if (plugOf[s] >= 0)
            {
                shut.Add(geometry.Brushes[plugOf[s]]);
            }

            NavGrid capped = NavClearanceBuilder.Build(fixedGeometry.With(shut), region, settings, records, cancellationToken);
            List<NavCapChange> changes = [];
            for (int v = 0; v < grid.Keys.Length; v++)
            {
                if (grid.Keys[v] != capped.Keys[v])
                {
                    changes.Add(new NavCapChange(v, capped.Keys[v]));
                }
            }

            sockets.Add(new RoomNavSocket(changes, DoorPoint(definition, definition.Sockets[s], grid)));
        }

        return new RoomNav
        {
            CellSize = cell,
            VoxelSize = settings.VoxelSize,
            CellVoxels = n,
            ColumnVoxels = nz,
            FloorNormalZ = settings.FloorNormalZ,
            StepHeight = settings.StepHeight,
            JumpHeight = settings.JumpHeight,
            JumpDistance = settings.JumpDistance,
            WaterCost = settings.WaterCost,
            LadderCost = settings.LadderCost,
            Turn = 0,
            Role = role,
            Agents = settings.Agents,
            Sockets = definition.Sockets,
            Pois = PlacePois(definition, pois, settings, grid, overhang),
            Records = records.Records,
            Columns = NavColumns.Of(grid),
            SocketData = sockets,
            Obstacles = [.. geometry.Obstacles.Select(o => new RoomNavObstacle(o.ClassName, o.TargetName, o.HammerId, o.Kind, o.Bounds))],
            Brushes = [.. overhang.Select(b => b.PlaneFloats())],
            Warnings = geometry.Warnings,
        };
    }

    /// <summary>
    /// Whether an agent fits in a voxel of a grid: its record's corners and
    /// overhanging brushes, the reader's own test.
    /// </summary>
    /// <param name="grid">The grid.</param>
    /// <param name="overhang">The overhanging brushes its records name.</param>
    /// <param name="x">The voxel along x.</param>
    /// <param name="y">Along y.</param>
    /// <param name="z">Along z.</param>
    /// <param name="agent">The agent.</param>
    /// <returns>True when it fits.</returns>
    public static bool Fits(NavGrid grid, IReadOnlyList<NavBrush> overhang, int x, int y, int z, NavAgentSpec agent)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(overhang);
        ArgumentNullException.ThrowIfNull(agent);
        byte[] record = grid.Record(x, y, z, agent.ClipClass);
        NavBox voxel = grid.Region.Voxel(x, y, z);
        double r = agent.Width * 0.5;
        if (Nav3dClearance.CornersBlock(record, r, agent.Height, voxel.MaxZ, []))
        {
            return false;
        }

        NavBox swept = new(voxel.MinX - r, voxel.MinY - r, voxel.MinZ, voxel.MaxX + r, voxel.MaxY + r, voxel.MaxZ + agent.Height);
        for (int i = 0; i < Nav3dClearance.BrushCount(record); i++)
        {
            if (overhang[Nav3dClearance.Brush(record, i)].Overlaps(swept))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The solid the room assumes beyond its cell: a thick slab past each of
    /// the six faces (the top one at the room's own height); but past a wall
    /// with an open socket, only the
    /// neighbour's side of that wall, <c>wall_depth</c> deep, with the same
    /// opening, and nothing beyond it (the neighbour's room, open near its
    /// door). <paramref name="capped"/> names the one socket shut, or -1 for
    /// every socket open.
    /// </summary>
    /// <remarks>
    /// Nothing, rather than solid, past the neighbour's wall: that is what the
    /// neighbour's room is near a door it lets agents through. The jambs of
    /// both walls dominate anything farther, so the assumption decides no
    /// record where a room keeps its doorway clear.
    /// </remarks>
    internal static List<NavBrush> Outside(RoomDefinition definition, int capped)
    {
        float c = definition.CellSize;
        float h = definition.Height;
        float big = (2 * Math.Max(c, h)) + 4096;
        float depth = definition.Kit.Depth;
        int solid = 1;
        List<NavBrush> boxes =
        [
            NavBrush.Box(new Vec3(-big, -big, -big), new Vec3(c + big, c + big, 0), solid),
            NavBrush.Box(new Vec3(-big, -big, h), new Vec3(c + big, c + big, h + big), solid),
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

    /// <summary>
    /// A door's point: on the cell face at the opening's middle, on the floor
    /// of the doorway's middle column (the plug's bottom when the doorway has
    /// no floor a player stands on).
    /// </summary>
    internal static Vec3 DoorPoint(RoomDefinition definition, RoomSocket socket, NavGrid grid)
    {
        float c = definition.CellSize;
        Box plug = RoomLinter.SealBox(definition, socket, c);
        int n = grid.Region.SizeX;
        double s = grid.Region.VoxelSize;
        float middleX = (plug.Mins.X + plug.Maxs.X) / 2;
        float middleY = (plug.Mins.Y + plug.Maxs.Y) / 2;
        (float px, float py, int vx, int vy) = socket.Facing switch
        {
            RoomFacing.PositiveX => (c, middleY, n - 1, VoxelOf(middleY, s, n)),
            RoomFacing.NegativeX => (0f, middleY, 0, VoxelOf(middleY, s, n)),
            RoomFacing.PositiveY => (middleX, c, VoxelOf(middleX, s, n), n - 1),
            _ => (middleX, 0f, VoxelOf(middleX, s, n), 0),
        };

        float floor = plug.Mins.Z;
        for (int z = 0; z < grid.Region.SizeZ; z++)
        {
            NavVoxelKey key = grid[vx, vy, z];
            if ((key.Flags & Nav3dLeafFlags.GroundedPlayer) != 0)
            {
                floor = key.PlayerFloorZ;
                break;
            }
        }

        return new Vec3(px, py, floor);
    }

    /// <summary>Checks each point against the presets it applies to, and turns it into the stored form.</summary>
    private static List<RoomNavPoi> PlacePois(
        RoomDefinition definition, IReadOnlyList<AuthoredPoi> pois, NavSettings settings, NavGrid grid, IReadOnlyList<NavBrush> overhang)
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

            int n = grid.Region.SizeX;
            int x = VoxelOf(poi.Origin.X, grid.Region.VoxelSize, n);
            int y = VoxelOf(poi.Origin.Y, grid.Region.VoxelSize, n);
            int z = VoxelOf(poi.Origin.Z, grid.Region.VoxelSize, grid.Region.SizeZ);
            for (int a = 0; a < settings.Agents.Count; a++)
            {
                if ((mask & (1u << a)) == 0)
                {
                    continue;
                }

                NavAgentSpec agent = settings.Agents[a];
                if (!Fits(grid, overhang, x, y, z, agent))
                {
                    throw new RoomLintException(
                        $"{who} is where agent \"{agent.Name}\" does not fit (voxel {x} {y} {z} is blocked for it);"
                        + " a point of interest stands where the agents it applies to can stand.");
                }

                if (arrival && !Standing(grid, overhang, x, y, z, agent))
                {
                    throw new RoomLintException(
                        $"{who} is an arrival point off the floor (voxel {x} {y} {z} has no floor under it); a player arrives standing.");
                }
            }

            placed.Add(new RoomNavPoi(poi.Origin, poi.Yaw, poi.HasFacing, poi.Radius, poi.Type, poi.Tags, poi.Name, mask, poi.Id));
        }

        return placed;
    }

    /// <summary>Whether an agent stands in a voxel: it fits, and the floor under it is walkable or the voxel under it too tight for it.</summary>
    private static bool Standing(NavGrid grid, IReadOnlyList<NavBrush> overhang, int x, int y, int z, NavAgentSpec agent)
    {
        NavVoxelKey key = grid[x, y, z];
        bool player = agent.ClipClass == Nav3dClipClass.Player;
        if ((key.Flags & (player ? Nav3dLeafFlags.GroundedPlayer : Nav3dLeafFlags.GroundedNpc)) != 0)
        {
            return (key.Flags & (player ? Nav3dLeafFlags.WalkablePlayer : Nav3dLeafFlags.WalkableNpc)) != 0;
        }

        return z > 0 && !Fits(grid, overhang, x, y, z - 1, agent);
    }

    /// <summary>The voxel holding a coordinate: half-open voxels, the cell's far face counted in its last voxel.</summary>
    internal static int VoxelOf(float coordinate, double voxel, int n) =>
        (int)Math.Clamp(Math.Floor(coordinate / voxel), 0, n - 1);

    private static string Fmt(Vec3 v) =>
        string.Create(CultureInfo.InvariantCulture, $"{v.X:0.###} {v.Y:0.###} {v.Z:0.###}");
}
