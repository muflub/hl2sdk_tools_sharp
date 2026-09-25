using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad;

using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.Rad.Final;

/// <summary>
/// The first byte-level lighting gate (plan §8, 4f): the managed driver with
/// direct light only against stock <c>vrad -bounce 0 -threads 1</c>, on the
/// same input, under <c>ComplianceOptions.Stock</c>.
/// </summary>
/// <remarks>
/// Every lump the driver writes is compared. The lumps with no float freedom
/// -- faces, worldlights, vertex normals, leaves, map flags -- must be
/// byte-identical. LIGHTING is compared record by record; its tolerance was
/// set from the first measurement (p4f-findings.md) and is frozen here.
/// </remarks>
public sealed class VradBounceZeroGateTests(ITestOutputHelper output)
{
    private static readonly VradOptions BounceZero = VradOptions.Default with { Bounces = 0 };

    public static TheoryData<string> Maps() => VradStockReference.Maps();

    public static TheoryData<string> CatalogueMaps() => VradStockReference.CatalogueMaps();

    [VradStockTheory]
    [MemberData(nameof(Maps))]
    public async Task FacesLumpMatchesStock(string name) =>
        await AssertLumpAsync(name, BspLump.Faces);

    [VradStockTheory]
    [MemberData(nameof(Maps))]
    public async Task WorldLightsLumpMatchesStock(string name) =>
        await AssertLumpAsync(name, BspLump.WorldLights);

    [VradStockTheory]
    [MemberData(nameof(Maps))]
    public async Task VertexNormalsLumpMatchesStock(string name) =>
        await AssertLumpAsync(name, BspLump.VertNormals);

    [VradStockTheory]
    [MemberData(nameof(Maps))]
    public async Task VertexNormalIndicesLumpMatchesStock(string name) =>
        await AssertLumpAsync(name, BspLump.VertNormalIndices);

    [VradStockTheory]
    [MemberData(nameof(Maps))]
    public async Task LeafsLumpMatchesStock(string name) =>
        await AssertLumpAsync(name, BspLump.Leafs);

    [VradStockTheory]
    [MemberData(nameof(Maps))]
    public async Task MapFlagsLumpMatchesStock(string name) =>
        await AssertLumpAsync(name, BspLump.MapFlags);

    [VradStockTheory]
    [MemberData(nameof(Maps))]
    public async Task LightingLumpHasStocksLength(string name)
    {
        if (Missing(name))
        {
            return;
        }

        (BspData managed, _) = await VradStockReference.CompileAsync("b0", name, BounceZero);
        BspData stock = await VradStockReference.LoadAsync(VradStockReference.ReferenceFor("b0", name));
        Assert.Equal(stock[BspLump.Lighting].Length, managed[BspLump.Lighting].Length);
    }

    [VradStockTheory]
    [MemberData(nameof(Maps))]
    public async Task LightingLumpIsWithinItsFrozenTolerance(string name)
    {
        if (Missing(name))
        {
            return;
        }

        (BspData managed, RadResult result) = await VradStockReference.CompileAsync("b0", name, BounceZero);
        BspData stock = await VradStockReference.LoadAsync(VradStockReference.ReferenceFor("b0", name));

        LightingComparison c = LightingComparison.Compare(stock, managed);
        output.WriteLine(c.Summary("b0:" + name));
        foreach (string d in result.StagesNotYetPorted)
        {
            output.WriteLine($"  not yet: {d}");
        }

        foreach (string line in c.WorstFaces(8))
        {
            output.WriteLine("  " + line);
        }

        int allowed = FrozenTolerance.LdrDifferingRecords(name);
        Assert.True(
            c.Records - c.ExactRecords <= allowed,
            $"{c.Records - c.ExactRecords} records differ; frozen at {allowed}");
    }

    /// <summary>
    /// The same gate without supersampling (<c>-noextra</c> on both sides),
    /// which takes stock's two uninitialised-memory reads out of the picture:
    /// <c>pSampleIntensity</c> (<c>lightmap.cpp:2881</c>) and
    /// <c>PointsInWinding</c>'s <c>invalidMask</c> (<c>:2644</c>).
    /// </summary>
    /// <param name="name">The map.</param>
    /// <returns>A task.</returns>
    [VradStockTheory]
    [MemberData(nameof(CatalogueMaps))]
    public async Task LightingLumpWithoutSupersamplingIsWithinItsFrozenTolerance(string name)
    {
        (BspData managed, _) = await VradStockReference.CompileAsync(
            "b0nx", name, BounceZero with { Supersample = false });
        BspData stock = await VradStockReference.LoadAsync(VradStockReference.ReferenceFor("b0nx", name));

        LightingComparison c = LightingComparison.Compare(stock, managed);
        output.WriteLine(c.Summary("b0nx:" + name));
        foreach (string line in c.WorstFaces(8))
        {
            output.WriteLine("  " + line);
        }

        int allowed = FrozenTolerance.LdrNoExtraDifferingRecords(name);
        Assert.True(
            c.Records - c.ExactRecords <= allowed,
            $"{c.Records - c.ExactRecords} records differ; frozen at {allowed}");
    }

    private bool Missing(string name)
    {
        if (VradStockReference.MissingCorpus(name) is { } why)
        {
            output.WriteLine($"{name}: not run, {why}");
            return true;
        }

        return false;
    }

    private async Task AssertLumpAsync(string name, BspLump lump)
    {
        if (Missing(name))
        {
            return;
        }

        (BspData managed, _) = await VradStockReference.CompileAsync("b0", name, BounceZero);
        BspData stock = await VradStockReference.LoadAsync(VradStockReference.ReferenceFor("b0", name));

        ReadOnlySpan<byte> a = stock[lump].Data.Span;
        ReadOnlySpan<byte> b = managed[lump].Data.Span;
        int first = a.SequenceEqual(b) ? -1 : FirstDifference(a, b);
        output.WriteLine($"{name} {lump}: stock {a.Length} bytes, managed {b.Length}, first difference at {first}");
        Assert.True(first < 0, $"{lump} differs from byte {first} (stock {a.Length}, managed {b.Length})");
    }

    private static int FirstDifference(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return n;
    }
}

/// <summary>
/// LIGHTING compared record by record: each 4-byte <c>ColorRGBExp32</c>, and
/// per face the worst decoded relative error.
/// </summary>
internal sealed class LightingComparison
{
    private readonly List<(int Face, int Differing, int Records, double MaxRelative)> _faces = [];

    public int Records { get; private set; }

    public int ExactRecords { get; private set; }

    public int AverageRecords { get; private set; }

    public int ExactAverageRecords { get; private set; }

    public int FacesCompared { get; private set; }

    public int ExactFaces { get; private set; }

    public double MaxRelative { get; private set; }

    public List<double> Errors { get; } = [];

    public static LightingComparison Compare(
        BspData stock, BspData managed, BspLump faceLump = BspLump.Faces, BspLump lightingLump = BspLump.Lighting)
    {
        LightingComparison c = new();
        DFace[] faces = MemoryMarshal.Cast<byte, DFace>(stock[faceLump].Data.Span).ToArray();
        TexInfo[] tex = MemoryMarshal.Cast<byte, TexInfo>(stock[BspLump.TexInfo].Data.Span).ToArray();
        ReadOnlySpan<byte> s = stock[lightingLump].Data.Span;
        ReadOnlySpan<byte> m = managed[lightingLump].Data.Span;
        if (s.Length != m.Length)
        {
            throw new InvalidOperationException($"LIGHTING is {m.Length} bytes, stock {s.Length}");
        }

        for (int f = 0; f < faces.Length; f++)
        {
            DFace face = faces[f];
            if (face.LightOfs < 0)
            {
                continue;
            }

            int styles = 0;
            while (styles < 4 && face.Styles[styles] != 255)
            {
                styles++;
            }

            bool bumped = (tex[face.TexInfo].Flags & 0x800) != 0;
            int luxels = (face.LightmapTextureSizeInLuxels[0] + 1) * (face.LightmapTextureSizeInLuxels[1] + 1);
            int records = luxels * styles * (bumped ? 4 : 1);

            int differing = 0;
            double worst = 0;
            for (int r = 0; r < records; r++)
            {
                int at = face.LightOfs + (r * 4);
                c.Records++;
                if (s.Slice(at, 4).SequenceEqual(m.Slice(at, 4)))
                {
                    c.ExactRecords++;
                    c.Errors.Add(0);
                    continue;
                }

                differing++;
                double e = Relative(s.Slice(at, 4), m.Slice(at, 4));
                c.Errors.Add(e);
                worst = Math.Max(worst, e);
            }

            for (int k = 0; k < styles; k++)
            {
                int at = face.LightOfs - ((k + 1) * 4);
                c.AverageRecords++;
                if (s.Slice(at, 4).SequenceEqual(m.Slice(at, 4)))
                {
                    c.ExactAverageRecords++;
                }
            }

            c.FacesCompared++;
            if (differing == 0)
            {
                c.ExactFaces++;
            }
            else
            {
                c._faces.Add((f, differing, records, worst));
            }

            c.MaxRelative = Math.Max(c.MaxRelative, worst);
        }

        return c;
    }

    /// <summary>Decoded relative error of one record, against the larger of the two and one unit.</summary>
    public static double Relative(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        double sa = Math.Pow(2.0, (sbyte)a[3]);
        double sb = Math.Pow(2.0, (sbyte)b[3]);
        double worst = 0;
        for (int i = 0; i < 3; i++)
        {
            double x = a[i] * sa;
            double y = b[i] * sb;
            worst = Math.Max(worst, Math.Abs(x - y) / Math.Max(1.0, Math.Max(x, y)));
        }

        return worst;
    }

    public string Summary(string name)
    {
        List<double> sorted = [.. Errors];
        sorted.Sort();
        double p99 = sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.99))];
        return $"{name}: records {ExactRecords}/{Records} exact ({(Records == 0 ? 1 : ExactRecords / (double)Records):P3}), "
            + $"averages {ExactAverageRecords}/{AverageRecords}, faces {ExactFaces}/{FacesCompared} exact, "
            + $"p99 {p99:P3}, max {MaxRelative:P3}";
    }

    public IEnumerable<string> WorstFaces(int count) =>
        _faces.OrderByDescending(x => x.MaxRelative).Take(count)
            .Select(x => $"face {x.Face}: {x.Differing}/{x.Records} records differ, max {x.MaxRelative:P3}");
}
