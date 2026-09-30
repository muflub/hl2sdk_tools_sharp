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
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// A lit library with displacements, compiled and lit once for a class: the
/// hub, lit by a lamp, with its two patches of terrain (<see cref="RoomDisplacementHarness"/>);
/// the other room, with a sky ceiling the library's sun lights and a lamp
/// by its patch. Every side is world-aligned, as the lit facts need
/// (<see cref="RoomLightHarness.WorldAlign"/>). The rooms are lit with their
/// door light, and unlit beside them; each level's links, relights and full
/// compiles are built once and shared.
/// </summary>
public sealed class LitDisplacementFixture : IAsyncLifetime
{
    private readonly ConcurrentDictionary<string, Lazy<Task<BspData>>> _maps = new(StringComparer.Ordinal);

    private readonly Lazy<Task<RoomLibrary>> _baseLit = new(() => RoomLightHarness.CompileAsync(Library, options: Options));

    /// <summary>The switches the fixture's rooms are lit with.</summary>
    public static VradOptions Options => RoomLightHarness.Options;

    /// <summary>The library VMF.</summary>
    public static VmfDocument Library { get; } = MakeLibrary();

    /// <summary>The rooms, lit with their door light.</summary>
    public RoomLibrary Lit { get; private set; } = null!;

    /// <summary>The same rooms, unlit.</summary>
    public RoomLibrary Unlit { get; private set; } = null!;

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        Lit = await RoomLightHarness.CompileAsync(Library, options: Options, doorLight: true);
        Unlit = await RoomLightHarness.CompileAsync(Library, light: false);
    }

    /// <inheritdoc/>
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>The same rooms lit without their door light (PR 9's base alone), compiled on first use.</summary>
    public Task<RoomLibrary> BaseLitAsync() => _baseLit.Value;

    /// <summary>A level of the given rows linked from the rooms lit without their door light.</summary>
    public Task<BspData> BaseLinkedAsync(string row) => MapAsync("base:" + row, async () => (await RoomLightHarness.LinkAsync(await BaseLitAsync(), RoomPropHarness.Level(row))).Bsp);

    /// <summary>A level of the given rows linked from the lit rooms.</summary>
    public Task<BspData> LinkedAsync(string row) => MapAsync("lit:" + row, async () => (await RoomLightHarness.LinkAsync(Lit, RoomPropHarness.Level(row))).Bsp);

    /// <summary>The level linked from the unlit rooms, then lit by vrad as it stands.</summary>
    public Task<BspData> RelitAsync(string row) => MapAsync("relit:" + row, async () =>
        await RoomLightHarness.RelightAsync((await RoomLightHarness.LinkAsync(Unlit, RoomPropHarness.Level(row))).Bsp, Options));

    /// <summary>The level flattened and compiled whole: vbsp, vvis and vrad with the rooms' switches.</summary>
    public Task<BspData> FlatAsync(string row) => MapAsync("flat:" + row, () => RoomLightHarness.CompileFlatLitAsync(Library, RoomPropHarness.Level(row), Options));

    private Task<BspData> MapAsync(string key, Func<Task<BspData>> make) =>
        _maps.GetOrAdd(key, _ => new Lazy<Task<BspData>>(make)).Value;

    private static VmfDocument MakeLibrary()
    {
        VmfDocument library = RoomLightHarness.Library(
            true,
            [1],
            (0, RoomLightHarness.Light(800, new Vec3(128, 128, 150))),
            (1, RoomLightHarness.Light(810, new Vec3(100, 60, 120))));
        VmfChunk world = library.GetChunk(MapFileLoader.WorldChunk)!;
        foreach ((int room, VmfChunk solid) in RoomDisplacementHarness.Patches)
        {
            Vec3 corner = new(room * (RoomHarness.Cell + RoomHarness.LibraryGap), 0, 0);
            world.Children.Add(VmfPlacement.MoveSolid(solid, QuarterTurn.Translation(corner)));
        }

        RoomLightHarness.WorldAlign(library);
        return library;
    }
}
