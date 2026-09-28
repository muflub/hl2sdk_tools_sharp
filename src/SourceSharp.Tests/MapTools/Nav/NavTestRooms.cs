//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Rooms;
using SourceSharp.Tests.MapTools.Rooms;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// Kit rooms built as brushes by hand, without a compile: the shell the room
/// model writes (floor, ceiling, four walls split around each opening, a
/// plug per socket) and whatever a fact adds, so a fact can place a step at
/// a height no voxel boundary rounds, a water volume or a ladder the room
/// linker would refuse, and link the rooms with the navigation's own linker.
/// </summary>
internal static class NavTestRooms
{
    /// <summary>The walkable kit's shell of a room: what <see cref="RoomModel"/> writes, as solid boxes, plugs included.</summary>
    public static List<NavBrush> Shell(RoomDefinition definition)
    {
        float c = definition.CellSize;
        float wall = definition.Kit.Depth;
        List<NavBrush> brushes =
        [
            NavBrush.Box(new Vec3(0, 0, 0), new Vec3(c, c, wall), 1),
            NavBrush.Box(new Vec3(0, 0, c - wall), new Vec3(c, c, c), 1),
        ];
        (float u0, float v0, float u1, float v1) = definition.Kit.OpeningUnit(c);
        foreach (RoomFacing facing in Enum.GetValues<RoomFacing>())
        {
            RoomSocket? socket = definition.Sockets.FirstOrDefault(s => s.Facing == facing);
            List<(float, float, float, float)> bands = socket is null
                ? [(0f, 0f, c, c)]
                : [(0f, 0f, u0 * c, c), (u1 * c, 0f, c, c), (u0 * c, v1 * c, u1 * c, c), (u0 * c, 0f, u1 * c, v0 * c)];
            foreach ((float a0, float b0, float a1, float b1) in bands)
            {
                if (a1 - a0 < 0.001f || b1 - b0 < 0.001f)
                {
                    continue;
                }

                (Vec3 lo, Vec3 hi) = facing switch
                {
                    RoomFacing.PositiveX => (new Vec3(c - wall, a0, b0), new Vec3(c, a1, b1)),
                    RoomFacing.NegativeX => (new Vec3(0, a0, b0), new Vec3(wall, a1, b1)),
                    RoomFacing.PositiveY => (new Vec3(a0, c - wall, b0), new Vec3(a1, c, b1)),
                    _ => (new Vec3(a0, 0, b0), new Vec3(a1, wall, b1)),
                };
                brushes.Add(NavBrush.Box(lo, hi, 1));
            }
        }

        foreach (RoomSocket socket in definition.Sockets)
        {
            Box seal = RoomLinter.SealBox(definition, socket, c);
            brushes.Add(NavBrush.Box(seal.Mins, seal.Maxs, 1));
        }

        return brushes;
    }

    /// <summary>A room's geometry: its shell and more brushes, obstacles and ladders.</summary>
    public static NavGeometry Geometry(
        RoomDefinition definition, IEnumerable<NavBrush>? extra = null, IEnumerable<NavObstacleSource>? obstacles = null, IEnumerable<Box>? ladders = null) => new()
    {
        Brushes = [.. Shell(definition), .. extra ?? []],
        Obstacles = [.. obstacles ?? []],
        Ladders = [.. ladders ?? []],
    };

    /// <summary>A room's navigation from its hand-built geometry.</summary>
    public static RoomNav Nav(
        RoomDefinition definition, NavGeometry geometry, NavSettings? settings = null, IReadOnlyList<AuthoredPoi>? pois = null, RoomRole role = RoomRole.None) =>
        RoomNavBuilder.Build(definition, geometry, pois ?? [], role, settings ?? NavSettings.Default);

    /// <summary>A level of hand-built rooms, its joints derived from where they stand, linked and read back.</summary>
    public static Nav3dReader Link(params (RoomDefinition Definition, RoomNav Nav, int X, int Y, int Rotation)[] cells)
    {
        int columns = cells.Max(c => c.X) + 1;
        int rows = cells.Max(c => c.Y) + 1;
        LevelCell?[] grid = new LevelCell?[columns * rows];
        foreach ((RoomDefinition definition, _, int x, int y, int rotation) in cells)
        {
            grid[(y * columns) + x] = new LevelCell(definition.Name, rotation);
        }

        LevelGrid level = new("navtest", "library.vmf", rows, columns, grid);
        Dictionary<string, RoomDefinition> definitions = cells.ToDictionary(c => c.Definition.Name, c => c.Definition, StringComparer.Ordinal);
        Dictionary<string, RoomNav> navs = cells.ToDictionary(c => c.Definition.Name, c => c.Nav, StringComparer.Ordinal);
        LevelLayout layout = level.ToLayout(name => definitions.GetValueOrDefault(name), RoomHarness.Cell, RoomHarness.WalkableKit);
        return Nav3dReader.Open(Nav3dWriter.Write(LevelNavLinker.Link(
            layout, columns, rows, (room, turn) => navs[room].Turned(turn - navs[room].Turn), null, Guid.Empty)));
    }

    /// <summary>A voxel's leaf and whether an agent fits there, read from a level.</summary>
    public static bool Fits(Nav3dReader nav, int cell, int x, int y, int z, float width, float height, Nav3dClipClass clipClass = Nav3dClipClass.Player)
    {
        int leaf = nav.FindLeaf(cell, x, y, z);
        return leaf >= 0 && nav.Passable(leaf, z, width, height, clipClass);
    }
}
