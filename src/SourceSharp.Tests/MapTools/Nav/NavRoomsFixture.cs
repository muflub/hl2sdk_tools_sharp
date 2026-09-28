//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Rooms;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// Small rooms on the walkable kit (96-wide doors the full interior height),
/// compiled once for the nav facts: dead ends east and west, a hall, and a
/// corner room with a pillar and a step that make every quarter turn a
/// different room.
/// </summary>
public sealed class NavRoomsFixture : IAsyncLifetime
{
    /// <summary>The agents the facts build for: the two defaults and one wider than the kit's door.</summary>
    public static NavSettings Settings { get; } = NavSettings.Default with
    {
        Agents = NavSettings.ParseAgents("standing 32 72 player; flyer 32 32 npc; wide 112 32 npc"),
    };

    public const int StandingAgent = 0;

    public const int FlyerAgent = 1;

    public const int WideAgent = 2;

    private readonly Dictionary<string, (RoomDefinition Definition, VmfDocument Vmf, RoomObject Room)> _rooms = new(StringComparer.Ordinal);

    public static RoomDefinition West => RoomHarness.WalkableRoom("west", RoomFacing.NegativeX);

    public static RoomDefinition East => RoomHarness.WalkableRoom("east", RoomFacing.PositiveX);

    public static RoomDefinition Hall => RoomHarness.WalkableRoom("hall", RoomFacing.PositiveX, RoomFacing.NegativeX);

    public static RoomDefinition Corner => RoomHarness.WalkableRoom("corner", RoomFacing.PositiveX, RoomFacing.PositiveY);

    public RoomObject Room(string name) => _rooms[name].Room;

    public RoomDefinition Definition(string name) => _rooms[name].Definition;

    public VmfDocument Vmf(string name) => _rooms[name].Vmf;

    public RoomNav Nav(string name) =>
        RoomNavBuilder.Build(Definition(name), Room(name).Bsp, [], RoomRole.None, Settings);

    /// <summary>The corner room's VMF: the shell, a pillar off-centre and a step in one corner.</summary>
    public static VmfDocument CornerVmf()
    {
        VmfDocument document = RoomHarness.BuildRoomModel(Corner);
        VmfChunk world = document.GetChunk(MapFileLoader.WorldChunk)!;
        world.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(32, 150, 16), new Vec3(72, 200, 240), 700001));
        world.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(160, 16, 16), new Vec3(240, 64, 40), 700002));
        return document;
    }

    /// <summary>A room's VMF turned in its cell by quarter turns, and its definition with its sockets turned.</summary>
    public static (VmfDocument Vmf, RoomDefinition Definition) Turned(VmfDocument vmf, RoomDefinition definition, int turns)
    {
        RoomTransform transform = new(new RoomPlacement(definition.Name, 0, 0, turns), definition.CellSize);
        QuarterTurn turn = QuarterTurn.Of(transform);
        VmfDocument turned = new();
        foreach (VmfChunk chunk in vmf.Chunks)
        {
            if (chunk.Name == MapFileLoader.WorldChunk)
            {
                VmfChunk world = new(chunk.Name);
                foreach (VmfKey key in chunk.Keys)
                {
                    world.AddKey(key.Name, key.Value);
                }

                foreach (VmfChunk solid in chunk.GetChunks(MapFileLoader.SolidChunk))
                {
                    world.Children.Add(VmfPlacement.MoveSolid(solid, turn));
                }

                turned.Chunks.Add(world);
            }
            else if (chunk.Name == MapFileLoader.EntityChunk)
            {
                turned.Chunks.Add(VmfPlacement.MoveEntity(chunk, turn));
            }
            else
            {
                turned.Chunks.Add(chunk);
            }
        }

        RoomFacing Facing((int Axis, int Sign) normal) => normal switch
        {
            (0, > 0) => RoomFacing.PositiveX,
            (0, _) => RoomFacing.NegativeX,
            (1, > 0) => RoomFacing.PositiveY,
            _ => RoomFacing.NegativeY,
        };

        RoomDefinition turnedDefinition = definition with
        {
            Name = definition.Name + "_t" + turns,
            Sockets = [.. definition.Sockets.Select(s => s with { Facing = Facing(transform.WorldNormal(s.Facing)) })],
        };
        return (turned, turnedDefinition);
    }

    public static async Task<RoomObject> CompileAsync(VmfDocument vmf, RoomDefinition definition)
    {
        VbspContext context = await RoomHarness.ContextAsync();
        return await RoomCompiler.CompileAsync(vmf, definition, context);
    }

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        foreach (RoomDefinition definition in new[] { West, East, Hall })
        {
            VmfDocument vmf = RoomHarness.BuildRoomModel(definition);
            _rooms[definition.Name] = (definition, vmf, await CompileAsync(vmf, definition));
        }

        VmfDocument corner = CornerVmf();
        _rooms[Corner.Name] = (Corner, corner, await CompileAsync(corner, Corner));
    }

    /// <inheritdoc/>
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A level's navigation from the fixture's rooms, turned as placed.</summary>
    public Nav3dLevel Link(LevelLayout layout, int columns, int rows, Func<string, RoomNav>? navOf = null)
    {
        Dictionary<string, RoomNav> navs = new(StringComparer.Ordinal);
        return LevelNavLinker.Link(
            layout, columns, rows,
            (room, turn) =>
            {
                if (!navs.TryGetValue(room, out RoomNav? nav))
                {
                    nav = navOf?.Invoke(room) ?? Nav(room);
                    navs[room] = nav;
                }

                return nav.Turned(turn);
            },
            null, Guid.Empty);
    }

    /// <summary>A layout whose joints are derived from where the rooms stand, as a level file's are.</summary>
    public LevelLayout Layout(params (string Room, int X, int Y, int Rotation)[] cells)
    {
        int columns = cells.Max(c => c.X) + 1;
        int rows = cells.Max(c => c.Y) + 1;
        LevelCell?[] grid = new LevelCell?[columns * rows];
        foreach ((string room, int x, int y, int rotation) in cells)
        {
            grid[(y * columns) + x] = new LevelCell(room, rotation);
        }

        LevelGrid level = new("navtest", "library.vmf", rows, columns, grid);
        return level.ToLayout(name => _rooms.TryGetValue(name, out var r) ? r.Definition : null, RoomHarness.Cell, RoomHarness.WalkableKit);
    }
}
