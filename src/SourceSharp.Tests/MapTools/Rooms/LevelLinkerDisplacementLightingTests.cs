//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;

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
    /// A room with displacements alone, every socket capped, links to vrad of
    /// its own link and to the full compile of its flattened level (vbsp,
    /// vvis and vrad with the rooms' switches) at every quarter turn: every
    /// luxel of every displacement the same bytes as both, and every other
    /// face's the same as vrad of the link (the displacement moved with its
    /// room lights, and shadows, exactly as the turned room's). Measured: 108
    /// displacement luxels in the hub and 56 in the other room, all exact.
    /// </summary>
    [Theory]
    [MemberData(nameof(RoomsAndTurns))]
    public async Task ACappedRoomsDisplacementsAreExactAtEveryTurn(string room, int rotation)
    {
        string row = $"{room}@{rotation}";
        BspData linked = await fixture.LinkedAsync(row);
        BspData relit = await fixture.RelitAsync(row);
        (bool styles, int luxels, int differ, int thin) = LitCompare.SameFaces(linked, relit);
        output.WriteLine($"{row} against vrad of the link: {luxels} luxels, {differ} differ, {thin} on thin faces");
        Assert.True(styles);
        Assert.Equal(0, differ);

        (int count, int exact, _, _) = DispLuxels(linked, relit);
        output.WriteLine($"{row} displacements against vrad of the link: {count} luxels, {exact} exact");
        Assert.True(count > 0);
        Assert.Equal(count, exact);

        (count, exact, _, _) = DispLuxels(linked, await fixture.FlatAsync(row));
        output.WriteLine($"{row} displacements against the full compile: {count} luxels, {exact} exact");
        Assert.Equal(count, exact);
    }

    /// <summary>
    /// Two rooms with displacements jointed, at two turns, against vrad of
    /// the linked level itself (the rooms design, 9.8, as PR 10 holds a
    /// jointed level): the whole level within PR 10's tolerances (near and
    /// elsewhere p95 under 8%, energy within 2%, under 3% of luxels more
    /// than 5% brighter); the displacements' luxels, which the door light
    /// reaches on their surfaces (<see cref="DoorFaceCells.OnSurface"/>),
    /// p95 under 20% and energy within 2%, and the base alone at least twice
    /// as far off. Measured: level near p95 0.070 and 0.030, elsewhere 0.077
    /// and 0.047, energy 0.996 and 0.998; displacements p95 0.106 and 0.149
    /// (the base alone 0.42 and 0.43), energy 0.994 and 0.991.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public async Task AJointedLevelLightsItsDisplacementsThroughTheDoors(int rotation)
    {
        string row = $"hub@{rotation}, other@{rotation}";
        LinkedLevel level = await RoomLightHarness.LinkAsync(fixture.Lit, RoomPropHarness.Level(row));
        BspData relit = await fixture.RelitAsync(row);
        DoorLightCompare.Metric all = DoorLightCompare.Measure(level.Bsp, relit, DoorLightCompare.Joints(level), allStyles: true);
        output.WriteLine($"{row} level: {all}");
        Assert.True(all.NearP95 <= 0.08, $"near p95 {all.NearP95}");
        Assert.True(all.FarP95 <= 0.08, $"far p95 {all.FarP95}");
        Assert.InRange(all.Energy, 0.98, 1.02);
        Assert.True(all.Brighter <= all.Count * 3 / 100, $"{all.Brighter} of {all.Count} brighter");

        (int count, _, double p95, double max) = DispLuxels(level.Bsp, relit);
        double energy = DispRatio(level.Bsp, relit);
        (_, _, double baseP95, _) = DispLuxels(await fixture.BaseLinkedAsync(row), relit);
        output.WriteLine($"{row} displacements: {count} luxels, p95 {p95:G3}, max {max:G3}, energy {energy:G4}; base alone p95 {baseP95:G3}");
        Assert.True(p95 <= 0.2, $"displacement p95 {p95}");
        Assert.InRange(energy, 0.98, 1.02);
        Assert.True(baseP95 >= p95 * 2, $"base alone p95 {baseP95}");
    }

    /// <summary>
    /// Rooms with displacements are baked, their door light recorded, packed
    /// and linked to the same bytes at one thread and at four (the rooms
    /// design, 15.5): the base bake, the door light (whose receivers on a
    /// displacement stand on its surface) and the lit level.
    /// </summary>
    [Fact]
    public async Task TheLitPackAndLinkAreTheSameBytesAtAnyThreadCount()
    {
        RoomLibrary four = await RoomLightHarness.CompileAsync(LitDisplacementFixture.Library, degree: 4, options: LitDisplacementFixture.Options, doorLight: true);
        foreach (RoomObject room in fixture.Lit.Rooms)
        {
            RoomObject other = four.Get(room.Definition.Name);
            Assert.Equal((await RoomPackItem.CreateAsync(room)).Extra.Select(x => x.Bytes.ToArray()), (await RoomPackItem.CreateAsync(other)).Extra.Select(x => x.Bytes.ToArray()));
        }

        LevelGrid level = RoomPropHarness.Level("hub@90, other@270", "other@0, hub@180");
        Assert.Equal(await Bytes(fixture.Lit, level, 1), await Bytes(four, level, 4));

        static async Task<byte[]> Bytes(RoomLibrary rooms, LevelGrid level, int degree)
        {
            using MemoryStream stream = new();
            await BspFile.SaveAsync((await RoomLightHarness.LinkAsync(rooms, level, degree)).Bsp, stream, BspWriteMode.Canonical, CancellationToken.None);
            return stream.ToArray();
        }
    }

    /// <summary>
    /// The door light's cells on a displacement face stand on its displaced
    /// surface (the rooms design, 9.4: a displacement's luxels are its
    /// surface's), each with the surface's normal there, which leans with
    /// the terrain; a brush face's stand on its plane with the plane's normal,
    /// as they always did.
    /// </summary>
    [Fact]
    public void ADisplacementsDoorCellsStandOnItsSurface()
    {
        BspData hub = fixture.Lit.Get("hub").Bsp;
        DoorFaceCells?[] cells = RoomDoorLight.FaceCellsOf(hub);
        DFace[] faces = BspStructView.As<DFace>(hub[BspLump.Faces]).ToArray();
        SourceSharp.MapTools.Disp.CoreDispInfo[] surfaces = RoomDisplacementHarness.Surfaces(hub);
        int onSurface = 0;
        for (int f = 0; f < faces.Length; f++)
        {
            if (cells[f] is not { } face)
            {
                continue;
            }

            if (faces[f].DispInfo < 0)
            {
                Assert.Null(face.Surface);
                Assert.Null(face.CellNormals);
                Assert.Equal(face.Normal, face.NormalAt(0));
                Vec3 expected = face.Origin + (face.AxisS * face.Coordinates(0).S) + (face.AxisT * face.Coordinates(0).T);
                Assert.Equal(expected, face.Point(0));
                continue;
            }

            Assert.NotNull(face.Surface);
            SourceSharp.MapTools.Disp.CoreDispInfo surface = surfaces[faces[f].DispInfo];
            float lo = float.MaxValue, hi = float.MinValue;
            for (int v = 0; v < surface.Size; v++)
            {
                lo = MathF.Min(lo, surface.Vert(v).Z);
                hi = MathF.Max(hi, surface.Vert(v).Z);
            }

            bool leans = false;
            for (int c = 0; c < face.Count; c++)
            {
                Vec3 p = face.Point(c);
                Assert.InRange(p.Z, lo, hi + 1.01f);
                Vec3 n = face.NormalAt(c);
                Assert.InRange(n.Length(), 0.999f, 1.001f);
                Assert.True(n.Z > 0.5f);
                leans |= n.Z < 0.999f;
                onSurface++;
            }

            Assert.True(leans);
        }

        Assert.True(onSurface > 0);
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
