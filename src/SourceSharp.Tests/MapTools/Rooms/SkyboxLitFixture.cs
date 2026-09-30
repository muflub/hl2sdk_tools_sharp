//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Concurrent;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// A lit library with a 3D skybox whose geometry shadows the sky: the base
/// bake's hub (a lamp, no sky) and other room (a sky ceiling and a lamp),
/// the library's sun, and the skybox harness's cell with an overhang above
/// its camera, so a sky ray recast into the skybox from part of the other
/// room meets the overhang and from the rest does not. Compiled and lit
/// once for a class, as <c>ssmap room</c> compiles and lights it; each
/// level's links and full compiles are built once and shared.
/// </summary>
public sealed class SkyboxLitFixture : IAsyncLifetime
{
    private readonly ConcurrentDictionary<string, Lazy<Task<BspData>>> _maps = new(StringComparer.Ordinal);

    /// <summary>The switches the fixture's rooms are lit with.</summary>
    public static VradOptions Options => RoomLightHarness.Options;

    /// <summary>
    /// The overhang, skybox-local: a slab above the camera, over the half of
    /// the skybox the sun's rays lean towards (the sun stands to the -x, -y
    /// side at 50 degrees), with its edge where the rays recast from the
    /// other room's cell cross it, so it shades part of the room.
    /// </summary>
    public static Box Overhang { get; } = new(new Vec3(16, 16, 160), new Vec3(118, 240, 176));

    /// <summary>The library VMF: the lit rooms, the sun, and the skybox with its overhang.</summary>
    public static VmfDocument Library { get; } = Make(Overhang);

    /// <summary>The rooms and the skybox, lit.</summary>
    public RoomLibrary Lit { get; private set; } = null!;

    /// <summary>The same rooms and skybox, unlit.</summary>
    public RoomLibrary Unlit { get; private set; } = null!;

    /// <summary>A library VMF with the overhang at the given box (null for a skybox without one).</summary>
    public static VmfDocument Make(Box? overhang)
    {
        VmfDocument library = RoomLightHarness.Library(
            true,
            [1],
            (0, RoomLightHarness.Light(800, new Vec3(128, 128, 150))),
            (1, RoomLightHarness.Light(810, new Vec3(100, 60, 120))));
        RoomSkyboxHarness.AddSkybox(library);
        if (overhang is { } box)
        {
            VmfChunk world = library.GetChunk(MapFileLoader.WorldChunk)!;
            world.Children.Add(VmfPlacement.MoveSolid(
                RoomModel.Slab(RoomHarness.Plain, box.Mins, box.Maxs, 7102), QuarterTurn.Translation(RoomSkyboxHarness.Corner)));
        }

        RoomLightHarness.WorldAlign(library);
        return library;
    }

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        Lit = await CompileAsync(Library, light: true);
        Unlit = await CompileAsync(Library, light: false);
    }

    /// <inheritdoc/>
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The library's rooms and skybox compiled, and lit when asked, one at a
    /// time, the skybox named as the library's and compiled first, so each
    /// sky room's bake recasts into it (<paramref name="recast"/> false lights
    /// every room sealed and alone, as before the skybox joined the bake).
    /// </summary>
    public static async Task<RoomLibrary> CompileAsync(VmfDocument library, bool light, int degree = 1, bool doorLight = false, bool recast = true)
    {
        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(library);
        RoomLibrary compiled = new(split.Rooms[0].Definition.Kit, split.Rooms[0].Definition.CellSize)
        {
            LibraryEntities = split.LibraryEntities,
            Options = split.Options,
            SkyboxRoom = split.Skybox!.Definition.Name,
        };
        RoomLightingSettings settings = new(Options)
        {
            Sun = RoomLightingSettings.SunOf(split.LibraryEntities),
            DoorLight = doorLight,
            Skybox = split.Skybox,
        };

        RoomObject skybox = await LightAsync(split.Skybox, settings);
        if (recast)
        {
            settings = settings.WithSkybox(RoomSkybox.Of(skybox));
        }

        foreach (LibraryRoom room in split.Rooms)
        {
            compiled.Add(await LightAsync(room, settings));
        }

        compiled.Add(skybox);
        return compiled;

        async Task<RoomObject> LightAsync(LibraryRoom room, RoomLightingSettings settings)
        {
            VbspContext context = await RoomLightHarness.ContextAsync(room.Definition.Name, degree);
            RoomObject compiledRoom = await RoomCompiler.CompileAsync(room.Document, room.Definition, context);
            if (!light)
            {
                return compiledRoom;
            }

            compiledRoom = compiledRoom with
            {
                Lighting = await RoomLighting.BakeAsync(compiledRoom, settings, context.Content!, context.Parallelism, CancellationToken.None),
            };
            return doorLight
                ? compiledRoom with
                {
                    DoorLight = await RoomDoorLight.BakeAsync(compiledRoom, compiledRoom.Lighting!, settings, context.Content!, context.Parallelism, CancellationToken.None),
                }
                : compiledRoom;
        }
    }

    /// <summary>A level of the given rows linked from the lit rooms.</summary>
    public Task<BspData> LinkedAsync(string row) => MapAsync("lit:" + row, async () => (await RoomLightHarness.LinkAsync(Lit, RoomPropHarness.Level(row))).Bsp);

    /// <summary>The level linked from the unlit rooms, then lit by vrad as it stands (the skybox included).</summary>
    public Task<BspData> RelitAsync(string row) => MapAsync("relit:" + row, async () =>
        await RoomLightHarness.RelightAsync((await RoomLightHarness.LinkAsync(Unlit, RoomPropHarness.Level(row))).Bsp, Options));

    /// <summary>The level flattened and compiled whole: vbsp, vvis and vrad with the rooms' switches.</summary>
    public Task<BspData> FlatAsync(string row) => MapAsync("flat:" + row, () => RoomLightHarness.CompileFlatLitAsync(Library, RoomPropHarness.Level(row), Options));

    private Task<BspData> MapAsync(string key, Func<Task<BspData>> make) =>
        _maps.GetOrAdd(key, _ => new Lazy<Task<BspData>>(make)).Value;
}
