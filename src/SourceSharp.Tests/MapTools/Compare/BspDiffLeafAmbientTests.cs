using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapTools.Compare;

using Xunit;

namespace SourceSharp.Tests.MapTools.Compare;

/// <summary>
/// LUMP_LEAF_AMBIENT_LIGHTING, whose LENGTH is not stable between runs of the
/// reference tool.
/// </summary>
/// <remarks>
/// <para>
/// plan_maptools_lane_notes.md spike 0d measured stock vrad producing
/// 9788 / 9788 / 9790 / 9790 / 9789 leaf-ambient records for one map, and
/// 936712 / 936740 / 936684 bytes for another. Where the counts differ, record
/// n of one lump is not record n of the other and an element-wise diff is
/// misaligned garbage -- the Phase 0 agent retracted its own first-pass
/// position-difference counts on exactly that ground.
/// </para>
/// <para>
/// So the rule is: where the counts differ, compare a PROPERTY and SAY so. The
/// facts below hold the comparer to both halves of that -- it must decline, and
/// it must be legible that it declined.
/// </para>
/// <para>
/// These are built in memory rather than from <c>dm_lockdown.bsp</c>, which is
/// a BSP version 19 map with LEAFS at lump version 0: its ambient cubes live
/// inside <c>dleaf_t</c> and it carries no leaf-ambient lump at all. A fact
/// about a lump the specimen does not have has to construct one.
/// </para>
/// </remarks>
public sealed class BspDiffLeafAmbientTests
{
    private static BspData WithAmbient(int records, byte firstGreen)
    {
        DLeafAmbientLighting[] samples = new DLeafAmbientLighting[records];
        for (int i = 0; i < records; i++)
        {
            samples[i].X = (byte)(i & 0xFF);
            samples[i].Y = 128;
            samples[i].Z = 64;
            for (int face = 0; face < 6; face++)
            {
                samples[i].Cube.Color[face] = new ColorRgbExp32
                {
                    R = 32,
                    G = (byte)(i == 0 && face == 0 ? firstGreen : 64),
                    B = 96,
                    Exponent = 1,
                };
            }
        }

        BspData bsp = new();
        bsp[BspLump.LeafAmbientLighting] = new BspLumpData(
            MemoryMarshal.AsBytes<DLeafAmbientLighting>(samples).ToArray(), 1, 0);
        return bsp;
    }

    [Fact]
    public async Task LumpsOfDifferentLengthsAreComparedAsAProperty()
    {
        BspData a = WithAmbient(9788, 64);
        BspData b = WithAmbient(9790, 64);

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        LumpDiff ambient = report.For(BspLump.LeafAmbientLighting);
        Assert.True(ambient.ComparedAsProperty);
        Assert.False(ambient.Identical);
    }

    [Fact]
    public async Task LumpsOfDifferentLengthsAreNotComparedElementWise()
    {
        // The half that matters. A comparer that aligned record 0 with record 0
        // and stopped at the shorter length would produce a plausible histogram
        // over misaligned data, which reads exactly like a real measurement.
        BspData a = WithAmbient(9788, 64);
        BspData b = WithAmbient(9790, 64);

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        LumpDiff ambient = report.For(BspLump.LeafAmbientLighting);
        Assert.Null(ambient.Lightmap);
    }

    [Fact]
    public async Task LumpsOfDifferentLengthsReportTheTwoRecordCounts()
    {
        BspData a = WithAmbient(9788, 64);
        BspData b = WithAmbient(9790, 64);

        DiffDifference difference = Assert.Single(
            (await BspDiff.CompareAsync(a, b, CancellationToken.None))
                .For(BspLump.LeafAmbientLighting).Differences);

        Assert.Equal("leaf ambient record count", difference.Subject);
        Assert.Equal("9788", difference.InA);
        Assert.Equal("9790", difference.InB);
    }

    [Fact]
    public async Task TheReportSaysWhyItDidNotCompareElementWise()
    {
        BspData a = WithAmbient(9788, 64);
        BspData b = WithAmbient(9790, 64);

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        string note = Assert.IsType<string>(report.For(BspLump.LeafAmbientLighting).Note);
        Assert.Contains("NOT element-wise", note, StringComparison.Ordinal);
        Assert.Contains(
            "compared as a property, NOT element-wise",
            report.For(BspLump.LeafAmbientLighting).ToText(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LumpsOfTheSameLengthAreComparedElementWise()
    {
        // The control: the property rule must bite ONLY on a length mismatch,
        // or the comparer would never look at a leaf-ambient cube at all.
        BspData a = WithAmbient(9788, 64);
        BspData b = WithAmbient(9788, 65);

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        LumpDiff ambient = report.For(BspLump.LeafAmbientLighting);
        Assert.False(ambient.ComparedAsProperty);

        LightmapStatistics stats = Assert.IsType<LightmapStatistics>(ambient.Lightmap);
        Assert.Equal(9788L * 6, stats.SampleCount);
        Assert.Equal(1, stats.DifferingSampleCount);
        Assert.Equal(LinearLight.Channel(1, 1), stats.MaxLinear, 12);
    }

    [Fact]
    public async Task IdenticalLeafAmbientLumpsAreIdentical()
    {
        BspData a = WithAmbient(9788, 64);
        BspData b = WithAmbient(9788, 64);

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        Assert.True(report.Identical, report.ToText(false));
    }

    [Fact]
    public async Task TheLeafAmbientIndexIsComparedOnItsRecordCount()
    {
        BspData a = new();
        BspData b = new();
        a[BspLump.LeafAmbientIndex] = new BspLumpData(new byte[4 * 10], 0, 0);
        b[BspLump.LeafAmbientIndex] = new BspLumpData(new byte[4 * 11], 0, 0);

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        LumpDiff index = report.For(BspLump.LeafAmbientIndex);
        Assert.True(index.ComparedAsProperty);
        Assert.Equal("leaf ambient index count", Assert.Single(index.Differences).Subject);
    }
}
