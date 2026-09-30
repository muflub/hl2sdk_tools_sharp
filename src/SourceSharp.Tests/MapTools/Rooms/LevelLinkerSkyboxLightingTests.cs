//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;

using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The 3D skybox in each room's bake (the rooms design, 4.12's lighting
/// line): a sky room's sky rays are recast into the library's skybox, as
/// vrad recasts them in the linked and the flattened level, so a skybox
/// whose geometry shadows the sky shadows the room's bake too.
/// </summary>
public sealed class LevelLinkerSkyboxLightingTests(SkyboxLitFixture fixture, ITestOutputHelper output) : IClassFixture<SkyboxLitFixture>
{
    /// <summary>The four quarter turns.</summary>
    public static TheoryData<int> Turns => new() { 0, 90, 180, 270 };

    /// <summary>
    /// The sky room alone, every socket capped, with the skybox below it,
    /// links to vrad of that very linked map at every quarter turn: the same
    /// styles and the same luxels byte for byte on every face more than one
    /// luxel across (thin faces as PR 9's capped facts hold them: exact at
    /// turn 0), the same world lights, vertex normals and map flags. Red
    /// before the skybox joined the bake: the overhang's shadow on the sky
    /// was missing from the room's own lightmaps.
    /// </summary>
    [Theory]
    [MemberData(nameof(Turns))]
    public async Task ASkyRoomLinksToVradOfItsOwnLinkWithTheSkyboxAtEveryTurn(int rotation)
    {
        string row = $"other@{rotation}";
        BspData linked = await fixture.LinkedAsync(row);
        BspData relit = await fixture.RelitAsync(row);
        (bool styles, int luxels, int differ, int thin) = LitCompare.SameFaces(linked, relit);
        output.WriteLine($"{row}: {luxels} luxels, {differ} differ, {thin} on thin faces differ");
        Assert.True(styles);
        Assert.True(luxels > 0);
        Assert.Equal(0, differ);
        if (rotation == 0)
        {
            Assert.Equal(0, thin);
        }

        foreach (BspLump lump in (ReadOnlySpan<BspLump>)[BspLump.WorldLights, BspLump.VertNormals, BspLump.VertNormalIndices, BspLump.MapFlags])
        {
            Assert.True(linked[lump].Data.Span.SequenceEqual(relit[lump].Data.Span), $"{row}: {lump}");
        }
    }

    /// <summary>
    /// The sky room and the skybox against the full compile of the flattened
    /// level (vbsp, vvis and vrad, the rooms' switches) within PR 9's
    /// capped-room tolerances: at turn 0 every luxel both maps hold is the
    /// same bytes; at a turn, where vbsp's grids meet, at least 98% exact,
    /// p99 within 0.05 and none past 0.25.
    /// </summary>
    [Theory]
    [MemberData(nameof(Turns))]
    public async Task ASkyRoomAgreesWithItsFlattenedFullCompileWithTheSkybox(int rotation)
    {
        string row = $"other@{rotation}";
        (int both, int exact, List<double> relative) = Compare(await fixture.LinkedAsync(row), await fixture.FlatAsync(row));
        double p99 = LitCompare.Quantile(relative, 0.99), max = LitCompare.Quantile(relative, 1);
        output.WriteLine($"{row}: {both} luxels in both, {exact} exact, p95 {LitCompare.Quantile(relative, 0.95):G3} p99 {p99:G3} max {max:G3}");
        Assert.True(both > 0);
        if (rotation == 0)
        {
            Assert.Equal(both, exact);
        }

        Assert.True(p99 <= 0.05 && max <= 0.25, $"{row}: p99 {p99}, max {max}");
        Assert.True(exact >= both * 0.98, $"{row}: {exact} of {both} exact");
    }

    /// <summary>The luxels two maps hold at the same points (thin faces left out): how many, how many exact, and each one's relative difference, sorted.</summary>
    internal static (int Both, int Exact, List<double> Relative) Compare(BspData a, BspData b)
    {
        var la = LitCompare.Lattice(a);
        var lb = LitCompare.Lattice(b);
        List<double> relative = [];
        int exact = 0;
        foreach ((var key, (List<ColorRgbExp32> colours, bool thin)) in la)
        {
            if (!lb.TryGetValue(key, out var other) || thin || other.Thin)
            {
                continue;
            }

            exact += colours.Any(c => other.Colours.Any(o => LitCompare.Same(c, o))) ? 1 : 0;
            relative.Add(colours.Min(c => other.Colours.Min(o => LitCompare.Relative(c.ToLinear(), o.ToLinear()))));
        }

        relative.Sort();
        return (relative.Count, exact, relative);
    }
}
