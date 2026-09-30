//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Rooms;

using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Displacement lighting through the link (the rooms design, 9.4: a
/// displacement's lightmap is a face lightmap with its own sample
/// positions, baked, captured and answered as a face's): a room with
/// displacements, capped, links to vrad of its own link and to the full
/// compile of its flattened level; jointed, its displacements gain the light
/// the door lets through within the door light's tolerances.
/// </summary>
public sealed class LevelLinkerDisplacementLightingTests(LitDisplacementFixture fixture, ITestOutputHelper output) : IClassFixture<LitDisplacementFixture>
{
    /// <summary>Both rooms at the four quarter turns.</summary>
    public static TheoryData<string, int> RoomsAndTurns => LevelLinkerLightingTests.RoomsAndTurns;

    /// <summary>
    /// Probe: a capped room against vrad of its own link, displacement faces and the rest.
    /// </summary>
    [Theory]
    [MemberData(nameof(RoomsAndTurns))]
    public async Task ProbeCapped(string room, int rotation)
    {
        string row = $"{room}@{rotation}";
        BspData linked = await fixture.LinkedAsync(row);
        BspData relit = await fixture.RelitAsync(row);
        (bool styles, int luxels, int differ, int thin) = LitCompare.SameFaces(linked, relit);
        output.WriteLine($"{row} vs relit: styles {styles}, {luxels} luxels, {differ} differ, {thin} thin");
        (int n, int exact, double p95, double max) = DispLuxels(linked, relit);
        output.WriteLine($"{row} disp vs relit: {n} luxels, {exact} exact, p95 {p95:G3}, max {max:G3}");
        BspData flat = await fixture.FlatAsync(row);
        (n, exact, p95, max) = DispLuxels(linked, flat);
        output.WriteLine($"{row} disp vs flat: {n} luxels, {exact} exact, p95 {p95:G3}, max {max:G3}");
    }

    /// <summary>Probe: a jointed level with door light against vrad of the link.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public async Task ProbeJointed(int rotation)
    {
        string row = $"hub@{rotation}, other@{rotation}";
        LinkedLevel level = await RoomLightHarness.LinkAsync(fixture.Lit, RoomPropHarness.Level(row));
        BspData relit = await fixture.RelitAsync(row);
        List<DoorLightCompare.Joint> joints = DoorLightCompare.Joints(level);
        output.WriteLine($"{row} all: {DoorLightCompare.Measure(level.Bsp, relit, joints, allStyles: true)}");
        (int n, int exact, double p95, double max) = DispLuxels(level.Bsp, relit);
        output.WriteLine($"{row} disp: {n} luxels, {exact} exact, p95 {p95:G3}, max {max:G3}");
        (n, exact, p95, max) = DispLuxels(await fixture.LinkedAsync(row), relit);
        output.WriteLine($"{row} disp base only: {n} luxels, {exact} exact, p95 {p95:G3}, max {max:G3}");
        output.WriteLine($"{row} disp ratio: {DispRatio(level.Bsp, relit):G4}");
    }

    /// <summary>The sum of one map's displacement luxels over another's.</summary>
    internal static double DispRatio(BspData a, BspData b)
    {
        DispInfo[] da = RoomDisplacementHarness.Infos(a), db = RoomDisplacementHarness.Infos(b);
        DFace[] fa = BspStructView.As<DFace>(a[BspLump.Faces]).ToArray(), fb = BspStructView.As<DFace>(b[BspLump.Faces]).ToArray();
        ColorRgbExp32[] la = LitCompare.Colours(a), lb = LitCompare.Colours(b);
        double sa = 0, sb = 0;
        for (int d = 0; d < da.Length; d++)
        {
            DFace x = fa[da[d].MapFace], y = fb[db[d].MapFace];
            int count = (x.LightmapTextureSizeInLuxels[0] + 1) * (x.LightmapTextureSizeInLuxels[1] + 1);
            for (int i = 0; i < count; i++)
            {
                Vec3 p = la[(x.LightOfs / 4) + i].ToLinear(), q = lb[(y.LightOfs / 4) + i].ToLinear();
                sa += p.X + p.Y + p.Z;
                sb += q.X + q.Y + q.Z;
            }
        }

        return sa / sb;
    }

    /// <summary>
    /// Two maps' displacement luxels, displacement by displacement in lump
    /// order (their faces found through the records), every style: how many,
    /// how many the same bytes, and the 95th percentile and largest relative
    /// error (<see cref="LitCompare.Relative"/>).
    /// </summary>
    internal static (int Count, int Exact, double P95, double Max) DispLuxels(BspData a, BspData b)
    {
        DispInfo[] da = RoomDisplacementHarness.Infos(a), db = RoomDisplacementHarness.Infos(b);
        Assert.Equal(da.Length, db.Length);
        DFace[] fa = BspStructView.As<DFace>(a[BspLump.Faces]).ToArray(), fb = BspStructView.As<DFace>(b[BspLump.Faces]).ToArray();
        ColorRgbExp32[] la = LitCompare.Colours(a), lb = LitCompare.Colours(b);
        List<double> relative = [];
        int exact = 0;
        for (int d = 0; d < da.Length; d++)
        {
            DFace x = fa[da[d].MapFace], y = fb[db[d].MapFace];
            Assert.Equal(x.LightmapTextureSizeInLuxels[0], y.LightmapTextureSizeInLuxels[0]);
            Assert.Equal(x.LightmapTextureSizeInLuxels[1], y.LightmapTextureSizeInLuxels[1]);
            int count = Math.Min(LitCompare.Styles(x), LitCompare.Styles(y))
                * (x.LightmapTextureSizeInLuxels[0] + 1) * (x.LightmapTextureSizeInLuxels[1] + 1);
            for (int i = 0; i < count; i++)
            {
                ColorRgbExp32 p = la[(x.LightOfs / 4) + i], q = lb[(y.LightOfs / 4) + i];
                exact += LitCompare.Same(p, q) ? 1 : 0;
                relative.Add(LitCompare.Relative(p.ToLinear(), q.ToLinear()));
            }
        }

        relative.Sort();
        return (relative.Count, exact, LitCompare.Quantile(relative, 0.95), LitCompare.Quantile(relative, 1));
    }
}
