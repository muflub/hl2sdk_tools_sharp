//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Small transition libraries in memory for the transition facts: an up
/// room with a button, a down room with a hallway trigger, and an ordinary
/// room with a player start and a spawn point, compiled the way
/// <c>ssmap room</c> compiles them (<see cref="RoomLibraryCompiler"/>, so the
/// points of interest come out and the transition data goes in), linked and
/// flattened.
/// </summary>
internal static class TransitHarness
{
    /// <summary>The up room: four doors on the walkable kit.</summary>
    public static RoomDefinition Up => RoomHarness.WalkableRoom(
        "up", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);

    /// <summary>The down room: four doors.</summary>
    public static RoomDefinition Down => RoomHarness.WalkableRoom(
        "down", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);

    /// <summary>An ordinary room: four doors.</summary>
    public static RoomDefinition Plain => RoomHarness.WalkableRoom(
        "plain", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);

    /// <summary>The transition volume's box, room-local.</summary>
    public static Box Volume { get; } = new(new Vec3(112, 112, 16), new Vec3(144, 144, 80));

    /// <summary>The down room's hallway trigger, room-local, around the volume.</summary>
    public static Box Hallway { get; } = new(new Vec3(96, 96, 16), new Vec3(160, 160, 128));

    /// <summary>The up room's arrival, room-local, facing east.</summary>
    public static Vec3 UpArrival { get; } = new(64, 192, 16);

    /// <summary>The up room's spawn point, room-local, facing south.</summary>
    public static Vec3 UpSpawn { get; } = new(192, 192, 16);

    /// <summary>The down room's arrival, room-local, facing north.</summary>
    public static Vec3 DownArrival { get; } = new(64, 64, 16);

    /// <summary>The plain room's spawn point, room-local.</summary>
    public static Vec3 PlainSpawn { get; } = new(192, 64, 16);

    /// <summary>An output's value as Hammer writes it.</summary>
    public static string Out(string target, string input) => $"{target}\u001b{input}\u001b\u001b0\u001b-1";

    /// <summary>A point entity at a room-local origin.</summary>
    public static VmfChunk Point(string classname, int id, Vec3 origin, params (string Key, string Value)[] keys)
    {
        VmfChunk entity = new(MapFileLoader.EntityChunk);
        entity.AddKey("id", id.ToString(CultureInfo.InvariantCulture));
        entity.AddKey("classname", classname);
        entity.AddKey("origin", VmfPlacement.Format(origin));
        foreach ((string key, string value) in keys)
        {
            entity.AddKey(key, value);
        }

        return entity;
    }

    /// <summary>A point of interest.</summary>
    public static VmfChunk Poi(int id, string type, Vec3 origin, float yaw) =>
        Point("info_poi", id, origin, ("poi_type", type), ("angles", string.Create(CultureInfo.InvariantCulture, $"0 {yaw} 0")));

    /// <summary>A brush entity of one box, with outputs.</summary>
    public static VmfChunk Brush(string classname, int id, Box box, (string Key, string Value)[] keys, params (string Output, string Value)[] outputs)
    {
        VmfChunk entity = RoomBrushHarness.Brush(classname, id, box.Mins, box.Maxs, RoomHarness.Trigger, null, keys);
        if (outputs.Length > 0)
        {
            VmfChunk connections = entity.AddChunk(MapFileLoader.ConnectionsChunk);
            foreach ((string output, string value) in outputs)
            {
                connections.AddKey(output, value);
            }
        }

        return entity;
    }

    /// <summary>The transition volume.</summary>
    public static VmfChunk VolumeEntity(int id, Box? box = null, params (string Key, string Value)[] keys) =>
        Brush(RoomTransit.VolumeClass, id, box ?? Volume, [("targetname", RoomTransit.VolumeName), .. keys]);

    /// <summary>The up room's entities: the arrival, a spawn point, a button firing the transition, and the volume.</summary>
    public static VmfChunk[] UpEntities =>
    [
        Poi(100, "arrival", UpArrival, 0),
        Poi(101, "spawn", UpSpawn, 270),
        Brush("func_button", 102, new Box(new Vec3(200, 40, 16), new Vec3(216, 56, 64)), [("wait", "-1")],
            ("OnPressed", Out(RoomTransit.VolumeName, "Transition"))),
        VolumeEntity(103),
    ];

    /// <summary>The down room's entities: the arrival, the hallway trigger around the volume, and the volume.</summary>
    public static VmfChunk[] DownEntities =>
    [
        Poi(200, "arrival", DownArrival, 90),
        Brush("trigger_once", 201, Hallway, [("spawnflags", "3")], ("OnStartTouch", Out(RoomTransit.VolumeName, "Transition"))),
        VolumeEntity(202),
    ];

    /// <summary>The plain room's entities: a player start and a spawn point.</summary>
    public static VmfChunk[] PlainEntities =>
    [
        Point("info_player_start", 300, new Vec3(128, 64, 16), ("angles", "0 0 0")),
        Poi(301, "spawn", PlainSpawn, 45),
    ];

    /// <summary>
    /// A library of the three rooms (up, down, plain, in that order), each
    /// room's entities given (or the defaults), the up and down rooms marked
    /// with their role; the up room <paramref name="upHeight"/> tall (the
    /// rooms design, 17.6), a cube by default.
    /// </summary>
    public static VmfDocument Library(VmfChunk[]? up = null, VmfChunk[]? down = null, VmfChunk[]? plain = null, bool roles = true, float upHeight = RoomHarness.Cell)
    {
        VmfDocument library = RoomHarness.LibraryVmf(Up with { Height = upHeight }, Down, Plain);
        VmfChunk[][] entities = [up ?? UpEntities, down ?? DownEntities, plain ?? PlainEntities];
        for (int room = 0; room < entities.Length; room++)
        {
            Vec3 corner = new(room * (RoomHarness.Cell + RoomHarness.LibraryGap), 0, 0);
            foreach (VmfChunk entity in entities[room])
            {
                library.Chunks.Add(VmfPlacement.MoveEntity(entity, QuarterTurn.Translation(corner)));
            }
        }

        if (roles)
        {
            Marker(library, "up").AddKey(RoomPois.RoleKey, "up");
            Marker(library, "down").AddKey(RoomPois.RoleKey, "down");
        }

        return library;
    }

    /// <summary>A room's <c>info_room</c> in a library.</summary>
    public static VmfChunk Marker(VmfDocument library, string room) =>
        library.GetChunks(MapFileLoader.EntityChunk).First(e => e.GetValue(RoomLibraryVmf.NameKey) == room);

    /// <summary>A level's text with transition keys: the grid rows north first, then the keys.</summary>
    public static LevelGrid Level(string name, string keys, params string[] rows)
    {
        string text = RoomHarness.LevelText("rooms.vmf", rows);
        int grid = text.IndexOf("grid:", StringComparison.Ordinal);
        return LevelYaml.Parse(text[..grid] + keys + text[grid..], name);
    }

    /// <summary>The library's rooms compiled as <c>ssmap room</c> compiles them; a room that fails throws its error.</summary>
    public static async Task<RoomLibrary> CompileAsync(VmfDocument library, int degree = 1)
    {
        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(library);
        VbspContext context = await RoomBrushHarness.ContextAsync("transit", cooker: null);
        RoomLibraryCompileSettings settings = new(VbspOptions.Default, context.Content)
        {
            Parallelism = new CompileParallelism { MaxDegree = degree },
        };
        RoomLibrary compiled = new(split.Rooms[0].Definition.Kit, split.Rooms[0].Definition.CellSize)
        {
            LibraryEntities = split.LibraryEntities,
            Options = split.Options,
        };
        List<RoomObject> rooms = [];
        await RoomLibraryCompiler.CompileAsync(split.Rooms, settings, (outcome, _) =>
        {
            if (outcome.Error is { } error)
            {
                throw new InvalidOperationException($"room {outcome.Room.Definition.Name}: {error.Message}", error);
            }

            rooms.Add(outcome.Compiled!);
            return ValueTask.CompletedTask;
        });
        foreach (RoomObject room in rooms)
        {
            compiled.Add(room);
        }

        return compiled;
    }

    /// <summary>The first failing room's error of a library compile.</summary>
    public static async Task<Exception> CompileErrorAsync(VmfDocument library)
    {
        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(library);
        VbspContext context = await RoomBrushHarness.ContextAsync("transit", cooker: null);
        Exception? found = null;
        await RoomLibraryCompiler.CompileAsync(
            split.Rooms,
            new RoomLibraryCompileSettings(VbspOptions.Default, context.Content) { Parallelism = new CompileParallelism { MaxDegree = 1 } },
            (outcome, _) =>
            {
                found ??= outcome.Error;
                return ValueTask.CompletedTask;
            });
        return found ?? throw new InvalidOperationException("every room compiled");
    }

    /// <summary>A level linked from compiled rooms.</summary>
    public static async Task<LinkedLevel> LinkAsync(RoomLibrary library, LevelGrid level, bool mod, int degree = 1)
    {
        LevelLayout layout = level.ToLayout(name => library.Find(name)?.Definition, library.CellSize, library.Kit);
        VbspContext context = await RoomHarness.ContextAsync();
        context.Parallelism = new CompileParallelism { MaxDegree = degree };
        return await LevelLinker.LinkAsync(layout, library, context, new LevelLinkOptions { ModEntities = mod });
    }

    /// <summary>A level flattened and compiled whole.</summary>
    public static async Task<BspData> CompileFlatAsync(VmfDocument library, LevelGrid level, bool mod)
    {
        FlattenedLevel flat = LevelFlattener.FlattenLevel(level, library, new LevelFlattenOptions { ModEntities = mod });
        VbspResult whole = await RoomHarness.CompileAsync(flat.Vmf, await RoomBrushHarness.ContextAsync("flat", cooker: null));
        Assert.NotNull(whole.Bsp);
        return whole.Bsp!;
    }

    /// <summary>A map's entities but the world, each as its keys in order, the Hammer id and model number left out, positions as numbers.</summary>
    public static List<string> Comparable(BspData bsp) =>
        [.. EntityLump.Parse(bsp[BspLump.Entities]).Skip(1).Select(e => string.Join(" | ", e.Pairs
            .Where(p => p.Key is not ("hammerid" or "model"))
            .Select(p => p.Key is "origin" ? $"origin={Position(p.Value)}" : $"{p.Key}={p.Value}")))];

    /// <summary>A map's entities of a class.</summary>
    public static List<BspEntity> OfClass(BspData bsp, string classname) =>
        [.. EntityLump.Parse(bsp[BspLump.Entities]).Where(e => e.ClassName == classname)];

    /// <summary>A map's model count, the world included.</summary>
    public static int Models(BspData bsp) => BspStructView.Count<DModel>(bsp[BspLump.Models]);

    /// <summary>A map's bytes as the link writes them.</summary>
    public static async Task<byte[]> BytesAsync(LinkedLevel level)
    {
        using MemoryStream bytes = new();
        await BspFile.SaveAsync(level.Bsp, bytes, BspWriteMode.Canonical);
        return bytes.ToArray();
    }

    /// <summary>A room-local point where a placement puts it, as the link writes it.</summary>
    public static string At(int x, int y, int rotation, Vec3 local) =>
        Text(new RoomTransform(new RoomPlacement("r", x, y, rotation / 90), RoomHarness.Cell).Apply(local));

    /// <summary>A position as an entity's origin writes it.</summary>
    public static string Text(Vec3 v) => string.Create(CultureInfo.InvariantCulture, $"{v.X} {v.Y} {v.Z}");

    private static string Position(string text) =>
        string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => float.Parse(v, CultureInfo.InvariantCulture).ToString("0.###", CultureInfo.InvariantCulture)));
}
