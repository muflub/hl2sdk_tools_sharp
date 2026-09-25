using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad;

using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.Rad.Final;

/// <summary>
/// The whole chain -- direct light, bounce (4d), displacements (4e), leaf
/// ambient and prop lighting (4g), FinalLightFace -- against stock's default
/// <c>vrad -threads 1</c> (100 bounces): catmaps-all and p4c's texlight map in
/// <c>P4F_STOCK_DIR/full</c>, p4e's displacement maps in
/// <c>P4E_STOCK_DIR/rad</c>.
/// </summary>
public sealed class VradFullGateTests(ITestOutputHelper output)
{
    private static readonly VradOptions Default = VradOptions.Default;

    public static TheoryData<string> Maps() => VradStockReference.Maps();

    [VradStockTheory]
    [MemberData(nameof(Maps))]
    public async Task FacesLumpMatchesStock(string name) =>
        await AssertLumpAsync(name, BspLump.Faces);

    [VradStockTheory]
    [MemberData(nameof(Maps))]
    public async Task VertexNormalsLumpMatchesStock(string name) =>
        await AssertLumpAsync(name, BspLump.VertNormals);

    [VradStockTheory]
    [MemberData(nameof(Maps))]
    public async Task LightingLumpHasStocksLength(string name)
    {
        if (Missing(name))
        {
            return;
        }

        (BspData managed, _) = await VradStockReference.CompileAsync("full", name, Default);
        BspData stock = await VradStockReference.LoadAsync(VradStockReference.ReferenceFor("full", name));
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

        (BspData managed, RadResult result) = await VradStockReference.CompileAsync("full", name, Default);
        BspData stock = await VradStockReference.LoadAsync(VradStockReference.ReferenceFor("full", name));

        LightingComparison c = LightingComparison.Compare(stock, managed);
        output.WriteLine(c.Summary("full:" + name));
        foreach (string d in result.StagesNotYetPorted)
        {
            output.WriteLine($"  not yet: {d}");
        }

        foreach (string line in c.WorstFaces(8))
        {
            output.WriteLine("  " + line);
        }

        foreach (BspLump lump in new[] { BspLump.WorldLights, BspLump.LeafAmbientIndex, BspLump.LeafAmbientLighting, BspLump.Leafs })
        {
            bool same = stock[lump].Data.Span.SequenceEqual(managed[lump].Data.Span);
            output.WriteLine($"  {lump}: {(same ? "identical" : $"differs (stock {stock[lump].Length}, managed {managed[lump].Length})")}");
        }

        int allowed = FrozenTolerance.FullDifferingRecords(name);
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

        (BspData managed, _) = await VradStockReference.CompileAsync("full", name, Default);
        BspData stock = await VradStockReference.LoadAsync(VradStockReference.ReferenceFor("full", name));

        ReadOnlySpan<byte> a = stock[lump].Data.Span;
        ReadOnlySpan<byte> b = managed[lump].Data.Span;
        output.WriteLine($"{name} {lump}: stock {a.Length} bytes, managed {b.Length}");
        Assert.True(a.SequenceEqual(b), $"{lump} differs (stock {a.Length}, managed {b.Length})");
    }
}
