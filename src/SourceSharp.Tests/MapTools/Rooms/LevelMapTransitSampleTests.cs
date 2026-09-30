//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Map2d;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapGen.Rooms;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rooms;

using SourceSharp.RoomContracts;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The transit sample's library compiled once, as <c>ssmap room</c> compiles
/// it (markers and transition data included), for the level map's facts.
/// </summary>
public sealed class TransitSampleFixture : IAsyncLifetime
{
    private ContentFileSystem? _content;

    /// <summary>The sample's library VMF.</summary>
    internal VmfDocument Vmf { get; private set; } = new();

    /// <summary>The compiled rooms.</summary>
    internal RoomLibrary Library { get; private set; } = new(SocketKit.Standard, 256);

    /// <summary>The sample's files, as the generator writes them.</summary>
    internal IReadOnlyDictionary<string, byte[]> Files { get; } = RoomsTransitSample.Build();

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        InMemoryFileSystem disk = new();
        foreach ((string path, byte[] bytes) in Files)
        {
            if (path.StartsWith("materials/", StringComparison.Ordinal))
            {
                disk.AddFile(path, bytes);
            }
        }

        _content = new ContentFileSystem([await DirectoryContentMount.MountAsync(disk, VPath.Empty)]);
        Vmf = await VmfDocument.ParseAsync(Files[Rooms3x3Kit.LibraryFile]);
        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(Vmf);
        Library = new RoomLibrary(split.Rooms[0].Definition.Kit, split.Rooms[0].Definition.CellSize) { Options = split.Options };
        List<RoomObject> rooms = [];
        await RoomLibraryCompiler.CompileAsync(
            split.Rooms,
            new RoomLibraryCompileSettings(VbspOptions.Default, _content) { Parallelism = new CompileParallelism { MaxDegree = 2 } },
            (outcome, _) =>
            {
                rooms.Add(outcome.Compiled ?? throw new InvalidOperationException($"room {outcome.Room.Definition.Name}: {outcome.Error?.Message}"));
                return ValueTask.CompletedTask;
            });
        foreach (RoomObject room in rooms)
        {
            Library.Add(room);
        }
    }

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        if (_content is not null)
        {
            await _content.DisposeAsync();
        }
    }

    /// <summary>A level of the sample, flattened and compiled whole, as <c>ssmap vbsp</c> compiles the reference.</summary>
    internal async Task<BspData> CompileFlatAsync(LevelGrid level)
    {
        VmfDocument flat = LevelFlattener.FlattenLevel(level, Vmf, new LevelFlattenOptions()).Vmf;
        VbspResult whole = await RoomHarness.CompileAsync(flat, new VbspContext(VbspOptions.Default, _content!) { MapBase = level.Name });
        return whole.Bsp ?? throw new InvalidOperationException($"{level.Name}: the flattened level did not compile");
    }
}

/// <summary>
/// The transit sample's levels (the rooms design, 18.6): for each, at every
/// quarter turn of the whole level, the level map the link writes from the
/// rooms' map sections is the map <c>ssmap map2d</c> makes from the
/// flattened level's compile cut by the level file, byte for byte; and it
/// marks the spawn, the arrivals and the exits.
/// </summary>
public sealed class LevelMapTransitSampleTests(TransitSampleFixture fixture) : IClassFixture<TransitSampleFixture>
{
    /// <summary>Every level of the sample at every turn.</summary>
    public static TheoryData<string, int> Cases
    {
        get
        {
            TheoryData<string, int> data = [];
            foreach (string level in (ReadOnlySpan<string>)["transit_01", "transit_02", "transit_03"])
            {
                for (int turns = 0; turns < 4; turns++)
                {
                    data.Add(level, turns);
                }
            }

            return data;
        }
    }

    /// <summary>The linked level map is the flattened compile's, at every turn of every level.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TheLinkedMapIsTheFlattenedCompiles(string name, int turns)
    {
        LevelGrid level = Turned(LevelYaml.Parse(Encoding.UTF8.GetString(fixture.Files[$"levels/{name}.yaml"]), name), turns);
        RoomLibrary rooms = fixture.Library;
        LevelLayout layout = level.ToLayout(room => rooms.Find(room)?.Definition, rooms.CellSize, rooms.Kit);
        LevelMapPlan plan = LevelMapBuilder.Plan(layout, level.Columns, level.Rows, rooms.Get);
        byte[] linked = Map2dWriter.Write(plan.Build(0x5eed));
        byte[] compiled = Map2dWriter.Write(LevelMapBuilder.FromCompile(await fixture.CompileFlatAsync(level), 0x5eed, level, [fixture.Vmf]));
        Assert.True(linked.AsSpan().SequenceEqual(compiled), $"{name} turned {turns}: the linked level map is not the flattened compile's");

        // Every level of the run spawns somewhere, and each transition room
        // it keeps has its arrival and its exit.
        Map2dLevel map = Map2dReader.Read(linked);
        Assert.Equal(LevelMap.SpawnKind, map.Markers[0].Kind);
        Assert.Equal(
            map.Markers.Count(m => m.Kind == LevelMap.ArrivalKind),
            map.Markers.Count(m => m.Kind is LevelMap.ExitUpKind or LevelMap.ExitDownKind));
        Assert.Equal(layout.Rooms.Count, map.Rooms.Length);
    }

    /// <summary>A level turned a quarter counter-clockwise <paramref name="turns"/> times: every cell moved and turned with it.</summary>
    internal static LevelGrid Turned(LevelGrid level, int turns)
    {
        for (int t = 0; t < turns; t++)
        {
            int rows = level.Rows, columns = level.Columns;
            LevelCell?[] cells = new LevelCell?[rows * columns];
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < columns; x++)
                {
                    if (level[x, y] is { } cell)
                    {
                        // (x, y) turns to (-y, x), moved back onto the grid: the new grid is `rows` columns wide.
                        cells[(x * rows) + (rows - 1 - y)] = cell with { Rotation = (cell.Rotation + 1) % 4 };
                    }
                }
            }

            LevelTransitions? transitions = level.Transitions is { SpawnCell: (int sx, int sy) } spawn
                ? spawn with { SpawnCell = (rows - 1 - sy, sx) }
                : level.Transitions;
            level = new LevelGrid(level.Name, level.Library, columns, rows, cells) { Transitions = transitions, Aliases = level.Aliases };
        }

        return level;
    }
}
