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
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Two room libraries for the multi-library facts (the rooms design, 17.2
/// to 17.5): <c>base</c>, the prop harness's hub and other with a marker and
/// a prop in its hub, and <c>caves</c>, a hub of the same name with its own
/// marker and prop, and a two-socket hall. Both on the walkable kit and the
/// harness's grid, so a level may join them; the two hubs share a name, so
/// every table the link keeps per room is exercised with two rooms of one
/// name.
/// </summary>
internal static class MultiLibraryHarness
{
    /// <summary>Where each library's hub marker stands, room-local.</summary>
    public static Vec3 BaseMarker { get; } = new(160, 64, 32);

    /// <summary>Where the caves hub's marker stands, room-local.</summary>
    public static Vec3 CavesMarker { get; } = new(64, 160, 32);

    /// <summary>The caves library's two-socket hall.</summary>
    public static RoomDefinition Hall => RoomHarness.WalkableRoom("hall", RoomFacing.PositiveX, RoomFacing.NegativeX);

    /// <summary>The base library: hub (a marker, a box prop) and other.</summary>
    public static VmfDocument Base(params VmfChunk[] gaps)
    {
        VmfDocument library = RoomPropHarness.Library(
            (0, RoomPropHarness.Entity("info_target", 990001, BaseMarker, ("targetname", "base_marker"))),
            (0, RoomPropHarness.Prop(990002, RoomPropHarness.BoxModel, new Vec3(64, 192, 16))));
        foreach (VmfChunk gap in gaps)
        {
            library.Chunks.Add(gap);
        }
        return library;
    }

    /// <summary>The caves library: its own hub (another marker, a post prop) and the hall.</summary>
    public static VmfDocument Caves(params VmfChunk[] gaps)
    {
        VmfDocument library = RoomHarness.LibraryVmf(RoomPropHarness.Hub, Hall);
        library.Chunks.Add(RoomPropHarness.Entity("info_target", 991001, CavesMarker, ("targetname", "caves_marker")));
        library.Chunks.Add(RoomPropHarness.Prop(991002, RoomPropHarness.PostModel, new Vec3(192, 64, 16)));
        foreach (VmfChunk gap in gaps)
        {
            library.Chunks.Add(gap);
        }
        return library;
    }

    /// <summary>A level of the two libraries, rows north first.</summary>
    public static LevelGrid Level(params string[] rows) => LevelYaml.Parse(Text(rows), "multi");

    /// <summary>The level file's text.</summary>
    public static string Text(params string[] rows) =>
        "libraries:\n  base: ../base.vmf\n  caves: ../caves.vmf\n"
        + $"rows: {rows.Length}\ncolumns: {rows[0].Split(',').Length}\ngrid:\n"
        + string.Concat(rows.Select(r => $"  - [{r}]\n"));

    /// <summary>A library's room names, as a pack's index lists them.</summary>
    public static IReadOnlyList<string> Names(RoomLibrary library) => [.. library.Rooms.Select(r => r.Definition.Name)];

    /// <summary>A level resolved and its libraries combined, as <c>ssmap link</c> does.</summary>
    public static (LevelGrid Level, LevelLibrarySet Set) Combine(LevelGrid level, params RoomLibrary[] libraries)
    {
        LevelGrid resolved = LevelLibraries.Resolve(level, [.. libraries.Select(Names)]);
        return (resolved, LevelLibraries.Combine(resolved, libraries));
    }

    /// <summary>A level of several libraries linked from their compiled rooms, at a degree.</summary>
    public static async Task<(LinkedLevel Linked, IReadOnlyList<string> Warnings)> LinkAsync(LevelGrid level, int degree, params RoomLibrary[] libraries)
    {
        (LevelGrid resolved, LevelLibrarySet set) = Combine(level, libraries);
        return (await LinkAsync(resolved, set.Rooms, degree), set.Warnings);
    }

    /// <summary>A resolved level linked from one (possibly combined) library, at a degree.</summary>
    public static async Task<LinkedLevel> LinkAsync(LevelGrid resolved, RoomLibrary rooms, int degree = 1)
    {
        LevelLayout layout = resolved.ToLayout(name => rooms.Find(name)?.Definition, rooms.CellSize, rooms.Kit);
        VbspContext context = await RoomHarness.ContextAsync();
        context.Parallelism = new CompileParallelism { MaxDegree = degree };
        return await LevelLinker.LinkAsync(layout, rooms, context);
    }

    /// <summary>The level flattened from the library VMFs and compiled whole, against the models.</summary>
    public static async Task<(BspData Bsp, IReadOnlyList<string> Warnings)> CompileFlatAsync(LevelGrid level, params VmfDocument[] libraries)
    {
        FlattenedLevel flat = LevelFlattener.FlattenLevel(level, libraries, new LevelFlattenOptions());
        VbspResult whole = await RoomHarness.CompileAsync(flat.Vmf, await RoomPropHarness.ContextAsync("flat"));
        Assert.NotNull(whole.Bsp);
        return (whole.Bsp!, flat.Warnings);
    }

    /// <summary>A map as its canonical bytes.</summary>
    public static async Task<byte[]> BytesAsync(BspData bsp)
    {
        using MemoryStream stream = new();
        await BspFile.SaveAsync(bsp, stream, BspWriteMode.Canonical, CancellationToken.None);
        return stream.ToArray();
    }

    /// <summary>A map's point entities of one class, each as its name and origin to a unit, sorted.</summary>
    public static List<string> Points(BspData bsp, string classname) =>
        [.. EntityLump.Parse(bsp[BspLump.Entities]).Where(e => e.ClassName == classname)
            .Select(e => $"{e.Get("targetname")} {Rounded(e.Get("origin"))}").Order(StringComparer.Ordinal)];

    /// <summary>A map's worldspawn key.</summary>
    public static string? World(BspData bsp, string key) => EntityLump.Parse(bsp[BspLump.Entities])[0].Get(key);

    private static string Rounded(string? origin) =>
        origin is null
            ? "-"
            : string.Join(' ', origin.Split(' ').Select(v => Math.Round(double.Parse(v, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture)));
}
