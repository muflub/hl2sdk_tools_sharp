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
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// A library with a 3D skybox room: the prop harness's hub and other rooms,
/// and a third cell marked <c>info_room_skybox</c>, its shell of 3D sky
/// material, a block inside it and its <c>sky_camera</c>; with the room
/// compile, link and flattened compile the skybox facts run.
/// </summary>
internal static class RoomSkyboxHarness
{
    /// <summary>The skybox room's name.</summary>
    public const string Name = "sky";

    /// <summary>The skybox's library corner: the third cell along +x.</summary>
    public static Vec3 Corner { get; } = new(2 * (RoomHarness.Cell + RoomHarness.LibraryGap), 0, 0);

    /// <summary>Where the sky camera stands in the skybox, room-local.</summary>
    public static Vec3 Camera { get; } = new(128, 128, 128);

    /// <summary>The block inside the skybox, room-local.</summary>
    public static Box Block { get; } = new(new Vec3(40, 40, 16), new Vec3(88, 88, 72));

    /// <summary>The skybox room's definition: the library's grid and kit, no socket.</summary>
    public static RoomDefinition Definition => new(Name, RoomHarness.Cell, RoomHarness.WalkableKit, []);

    /// <summary>The <c>sky_camera</c>, at a room-local point.</summary>
    public static VmfChunk SkyCamera(int id = 700600, Vec3? at = null) =>
        RoomPropHarness.Entity("sky_camera", id, at ?? Camera, ("scale", "16"), ("angles", "0 0 0"));

    /// <summary>
    /// The prop harness's library of hub and other, with the skybox cell:
    /// its shell (sky inside), its block, its marker and, unless left out,
    /// its camera; each extra entity at a skybox-local point.
    /// </summary>
    public static VmfDocument Library(bool camera = true, params VmfChunk[] extra)
    {
        VmfDocument library = RoomPropHarness.Library();
        VmfChunk world = library.GetChunk(MapFileLoader.WorldChunk)!;
        QuarterTurn move = QuarterTurn.Translation(Corner);
        VmfDocument shell = RoomModel.Build(Definition, RoomHarness.WalkableKit.Depth, RoomLightHarness.Sky);
        foreach (VmfChunk solid in shell.GetChunk(MapFileLoader.WorldChunk)!.GetChunks(MapFileLoader.SolidChunk))
        {
            world.Children.Add(VmfPlacement.MoveSolid(solid, move));
        }

        world.Children.Add(VmfPlacement.MoveSolid(RoomModel.Slab(RoomHarness.Plain, Block.Mins, Block.Maxs, 7101), move));
        library.Chunks.Add(Marker(Name, Corner));
        if (camera)
        {
            library.Chunks.Add(VmfPlacement.MoveEntity(SkyCamera(), move));
        }

        foreach (VmfChunk entity in extra)
        {
            library.Chunks.Add(VmfPlacement.MoveEntity(entity, move));
        }

        return library;
    }

    /// <summary>The <c>info_room_skybox</c> marker at a library corner.</summary>
    public static VmfChunk Marker(string name, Vec3 corner)
    {
        VmfChunk marker = new(MapFileLoader.EntityChunk);
        marker.AddKey("id", "800100");
        marker.AddKey("classname", RoomLibraryVmf.SkyboxEntity);
        marker.AddKey("origin", VmfPlacement.Format(corner));
        marker.AddKey(RoomLibraryVmf.NameKey, name);
        return marker;
    }

    /// <summary>The library's rooms and its skybox compiled as <c>ssmap room</c> compiles them, the skybox named as the library's.</summary>
    public static async Task<RoomLibrary> CompileAsync(VmfDocument library, int degree = 1)
    {
        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(library);
        RoomLibrary compiled = new(split.Rooms[0].Definition.Kit, split.Rooms[0].Definition.CellSize)
        {
            LibraryEntities = split.LibraryEntities,
            Options = split.Options,
            SkyboxRoom = split.Skybox?.Definition.Name,
        };
        foreach (LibraryRoom room in split.Skybox is { } sky ? [.. split.Rooms, sky] : split.Rooms)
        {
            VbspContext context = await RoomLightHarness.ContextAsync(room.Definition.Name, degree);
            compiled.Add(await RoomCompiler.CompileAsync(room.Document, room.Definition, context));
        }

        return compiled;
    }

    /// <summary>The level flattened and compiled whole.</summary>
    public static async Task<BspData> CompileFlatAsync(VmfDocument library, LevelGrid level)
    {
        VbspResult whole = await RoomHarness.CompileAsync(LevelFlattener.Flatten(level, library), await RoomLightHarness.ContextAsync("flat"));
        Assert.NotNull(whole.Bsp);
        return whole.Bsp!;
    }

    /// <summary>
    /// Sample points of the skybox's cell as a level places it, below its
    /// south-west cell: the lattice of <see cref="RoomAreaPortalHarness.Samples"/>
    /// one cell down.
    /// </summary>
    public static IEnumerable<Vec3> SkyboxSamples(int column, int row)
    {
        for (int i = 0; i < 32; i++)
        {
            for (int j = 0; j < 32; j++)
            {
                foreach (float z in new[] { -196f, -60f })
                {
                    yield return new Vec3((column * RoomHarness.Cell) + 4 + (8 * i), (row * RoomHarness.Cell) + 4 + (8 * j), z);
                }
            }
        }
    }

    /// <summary>A map's entities of one class, each as its pairs but <c>hammerid</c>.</summary>
    public static List<string> OfClass(BspData bsp, string classname) =>
        [.. EntityLump.Parse(bsp[BspLump.Entities]).Where(e => e.ClassName == classname)
            .Select(e => string.Join(" | ", e.Pairs.Where(p => p.Key != "hammerid").Select(p => $"{p.Key}={p.Value}")))];

    /// <summary>A map's worldspawn key.</summary>
    public static string? World(BspData bsp, string key) => EntityLump.Parse(bsp[BspLump.Entities])[0].Get(key);

    /// <summary>A point as the harness writes one.</summary>
    public static string At(Vec3 v) => string.Create(CultureInfo.InvariantCulture, $"{v.X} {v.Y} {v.Z}");
}
