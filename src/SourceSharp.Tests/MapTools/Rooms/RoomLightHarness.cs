//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Lit rooms for the base bake's facts (the rooms design, section 9): the
/// prop harness's two walkable rooms (<see cref="RoomPropHarness"/>) with
/// lights, an optional library sun in the gap between their cells and an
/// optional sky ceiling, compiled and baked as <c>ssmap room</c> does, and
/// the full compile of a flattened level to hold a link against.
/// </summary>
internal static class RoomLightHarness
{
    /// <summary>The sky material: 3D sky, as a game's <c>tools/toolsskybox</c> is.</summary>
    public const string Sky = "unit/sky";

    /// <summary>The vrad switches the facts bake with: stock's, a few bounces so a bake stays quick.</summary>
    public static VradOptions Options { get; } = VradOptions.Default with { Bounces = 4 };

    /// <summary>A light at a room-local origin, of brightness 200, optionally named (switchable).</summary>
    public static VmfChunk Light(int id, Vec3 origin, string? name = null, string colour = "255 240 220 200")
    {
        VmfChunk light = RoomPropHarness.Entity("light", id, origin, ("_light", colour));
        if (name is not null)
        {
            light.AddKey("targetname", name);
        }

        return light;
    }

    /// <summary>The library's sun, standing in the gap between the two rooms' cells.</summary>
    public static VmfChunk Sun(string angles = "0 30 0", string pitch = "-50") => RoomPropHarness.Entity(
        "light_environment",
        950,
        new Vec3(RoomHarness.Cell + (RoomHarness.LibraryGap / 2), 128, 128),
        ("angles", angles),
        ("pitch", pitch),
        ("_light", "255 250 230 400"),
        ("_ambient", "120 140 180 60"));

    /// <summary>
    /// The prop harness's library of hub (room 0) and other (room 1) with the
    /// given entities, each ceiling's underside made sky in the rooms listed,
    /// and the sun in the gap when asked.
    /// </summary>
    public static VmfDocument Library(bool sun, int[] skyRooms, params (int Room, VmfChunk Entity)[] extra)
    {
        VmfDocument library = RoomPropHarness.Library(extra);
        if (sun)
        {
            library.Chunks.Add(Sun());
        }

        foreach (int room in skyRooms)
        {
            SkyCeiling(library, room);
        }

        return library;
    }

    /// <summary>
    /// Makes room <paramref name="room"/>'s ceiling underside sky: every
    /// brush side of the world whose three points lie on the ceiling's lower
    /// plane, inside that room's cell, takes <see cref="Sky"/>.
    /// </summary>
    public static void SkyCeiling(VmfDocument library, int room)
    {
        float x0 = room * (RoomHarness.Cell + RoomHarness.LibraryGap);
        float z = RoomHarness.Cell - RoomHarness.WalkableKit.Depth;
        VmfChunk world = library.GetChunk(MapFileLoader.WorldChunk)!;
        foreach (VmfChunk solid in world.GetChunks(MapFileLoader.SolidChunk))
        {
            foreach (VmfChunk side in solid.GetChunks("side"))
            {
                Vec3[] points = Points(side.GetValue("plane")!);
                if (points.All(p => Math.Abs(p.Z - z) < 0.01f && p.X >= x0 - 0.01f && p.X <= x0 + RoomHarness.Cell + 0.01f))
                {
                    foreach (VmfKey key in side.Keys)
                    {
                        if (string.Equals(key.Name, "material", StringComparison.Ordinal))
                        {
                            key.Value = Sky;
                        }
                    }
                }
            }
        }
    }

    private static Vec3[] Points(string plane)
    {
        string[] parts = plane.Replace("(", " ", StringComparison.Ordinal).Replace(")", " ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Vec3[] points = new Vec3[3];
        for (int i = 0; i < 3; i++)
        {
            points[i] = new Vec3(
                float.Parse(parts[i * 3], CultureInfo.InvariantCulture),
                float.Parse(parts[(i * 3) + 1], CultureInfo.InvariantCulture),
                float.Parse(parts[(i * 3) + 2], CultureInfo.InvariantCulture));
        }

        return points;
    }

    /// <summary>A compile context with the harness materials, the sky and the prop models.</summary>
    public static async Task<VbspContext> ContextAsync(string mapBase = "roomtest", int degree = 1)
    {
        Dictionary<string, byte[]> files = new(RoomPropHarness.Models(), StringComparer.Ordinal)
        {
            [$"materials/{Sky}.vmt"] = "\"UnlitGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compileSky\" \"1\"\n}\n"u8.ToArray(),
        };
        VbspContext context = await RoomHarness.ContextAsync(extraFiles: files);
        context.MapBase = mapBase;
        context.Parallelism = new CompileParallelism { MaxDegree = degree };
        return context;
    }

    /// <summary>The settings a library's rooms are lit with, its sun taken from its gaps.</summary>
    public static RoomLightingSettings Settings(RoomLibrarySplit split, VradOptions? options = null) =>
        new(options ?? Options) { Sun = RoomLightingSettings.SunOf(split.LibraryEntities) };

    /// <summary>The library's rooms compiled and lit as <c>ssmap room</c> does, one at a time, at <paramref name="degree"/>.</summary>
    public static async Task<RoomLibrary> CompileAsync(VmfDocument library, int degree = 1, VradOptions? options = null, bool light = true)
    {
        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(library);
        RoomLibrary compiled = new(split.Rooms[0].Definition.Kit, split.Rooms[0].Definition.CellSize)
        {
            LibraryEntities = split.LibraryEntities,
            Options = split.Options,
        };
        RoomLightingSettings settings = Settings(split, options);
        foreach (LibraryRoom room in split.Rooms)
        {
            VbspContext context = await ContextAsync(room.Definition.Name, degree);
            RoomObject compiledRoom = await RoomCompiler.CompileAsync(room.Document, room.Definition, context);
            if (light)
            {
                compiledRoom = compiledRoom with
                {
                    Lighting = await RoomLighting.BakeAsync(compiledRoom, settings, context.Content!, context.Parallelism, CancellationToken.None),
                };
            }

            compiled.Add(compiledRoom);
        }

        return compiled;
    }

    /// <summary>A level linked from compiled rooms.</summary>
    public static async Task<LinkedLevel> LinkAsync(RoomLibrary library, LevelGrid level, int degree = 1, string mapBase = "")
    {
        LevelLayout layout = RoomPropHarness.Layout(library, level);
        VbspContext context = await RoomHarness.ContextAsync();
        context.MapBase = mapBase;
        context.Parallelism = new CompileParallelism { MaxDegree = degree };
        return await LevelLinker.LinkAsync(layout, library, context);
    }

    /// <summary>
    /// The level flattened and compiled whole: vbsp, vvis and vrad, with the
    /// switches the rooms were lit with (the rooms design, 9.7: <c>link
    /// --flatten</c>, then <c>ssmap vbsp</c>, <c>ssmap vvis</c>, <c>ssmap vrad</c>).
    /// </summary>
    public static async Task<BspData> CompileFlatLitAsync(VmfDocument library, LevelGrid level, VradOptions? options = null)
    {
        VbspContext context = await ContextAsync("flat");
        VbspResult whole = await RoomHarness.CompileAsync(LevelFlattener.Flatten(level, library), context);
        Assert.NotNull(whole.Bsp);
        Assert.NotNull(whole.Portals);
        BspData bsp = whole.Bsp!;
        _ = await Vvis.ComputeAsync(bsp, PortalSet.FromPortalFile(whole.Portals!), new VisContext(), CancellationToken.None);
        _ = await Vrad.LightAsync(bsp, new VradContext { Options = options ?? Options, MapName = "flat", Content = context.Content });
        return bsp;
    }

    /// <summary>A linked level lit afresh: vrad run on the link's own map, with the switches the rooms were lit with.</summary>
    public static async Task<BspData> RelightAsync(BspData linked, VradOptions? options = null)
    {
        VbspContext context = await ContextAsync("relit");
        BspData copy = RoomLighting.Copy(linked);
        _ = await Vrad.LightAsync(copy, new VradContext { Options = options ?? Options, MapName = "relit", Content = context.Content });
        return copy;
    }
}
