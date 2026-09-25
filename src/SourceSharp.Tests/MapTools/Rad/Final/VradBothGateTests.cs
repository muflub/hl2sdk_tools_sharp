using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad;

using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.Rad.Final;

/// <summary>
/// <c>-both</c> in one process against stock's launcher running the DLL twice
/// (<c>vrad -bounce 0 -both -threads 1</c>, references in
/// <c>P4F_STOCK_DIR/b0both</c>).
/// </summary>
/// <remarks>
/// The HDR pass reads the map the LDR pass wrote, in both; so the LDR lumps
/// must equal the LDR-only compile's and the HDR lumps must equal stock's HDR
/// pass. The byte-exact lumps are asserted exact; LIGHTING_HDR has the same
/// frozen tolerance story as LIGHTING (supersampling's uninitialised reads).
/// </remarks>
public sealed class VradBothGateTests(ITestOutputHelper output)
{
    private static readonly VradOptions BothBounceZero =
        VradOptions.Default with { Bounces = 0, Range = VradLightingRange.Both };

    public static TheoryData<string> Maps() => VradStockReference.CatalogueMaps();

    [VradStockTheory]
    [MemberData(nameof(Maps))]
    public async Task FacesHdrLumpMatchesStock(string name) =>
        await AssertLumpAsync(name, BspLump.FacesHdr);

    [VradStockTheory]
    [MemberData(nameof(Maps))]
    public async Task WorldLightsHdrLumpMatchesStock(string name) =>
        await AssertLumpAsync(name, BspLump.WorldLightsHdr);

    [VradStockTheory]
    [MemberData(nameof(Maps))]
    public async Task LdrFacesLumpMatchesStock(string name) =>
        await AssertLumpAsync(name, BspLump.Faces);

    [VradStockTheory]
    [MemberData(nameof(Maps))]
    public async Task LdrLightingLumpIsTheLdrOnlyCompilesLump(string name)
    {
        (BspData both, _) = await VradStockReference.CompileAsync("b0both", name, BothBounceZero);
        (BspData ldr, _) = await VradStockReference.CompileAsync(
            "b0", name, VradOptions.Default with { Bounces = 0 });
        Assert.True(both[BspLump.Lighting].Data.Span.SequenceEqual(ldr[BspLump.Lighting].Data.Span));
    }

    [VradStockTheory]
    [MemberData(nameof(Maps))]
    public async Task HdrLightingLumpHasStocksLength(string name)
    {
        (BspData managed, _) = await VradStockReference.CompileAsync("b0both", name, BothBounceZero);
        BspData stock = await VradStockReference.LoadAsync(VradStockReference.ReferenceFor("b0both", name));
        Assert.Equal(stock[BspLump.LightingHdr].Length, managed[BspLump.LightingHdr].Length);
    }

    [VradStockTheory]
    [MemberData(nameof(Maps))]
    public async Task HdrLightingLumpIsWithinItsFrozenTolerance(string name)
    {
        (BspData managed, _) = await VradStockReference.CompileAsync("b0both", name, BothBounceZero);
        BspData stock = await VradStockReference.LoadAsync(VradStockReference.ReferenceFor("b0both", name));

        LightingComparison c = LightingComparison.Compare(stock, managed, BspLump.FacesHdr, BspLump.LightingHdr);
        output.WriteLine(c.Summary("hdr:" + name));
        int allowed = FrozenTolerance.HdrDifferingRecords(name);
        Assert.True(
            c.Records - c.ExactRecords <= allowed,
            $"{c.Records - c.ExactRecords} HDR records differ; frozen at {allowed}");
    }

    private async Task AssertLumpAsync(string name, BspLump lump)
    {
        (BspData managed, _) = await VradStockReference.CompileAsync("b0both", name, BothBounceZero);
        BspData stock = await VradStockReference.LoadAsync(VradStockReference.ReferenceFor("b0both", name));

        ReadOnlySpan<byte> a = stock[lump].Data.Span;
        ReadOnlySpan<byte> b = managed[lump].Data.Span;
        output.WriteLine($"{name} {lump}: stock {a.Length} bytes, managed {b.Length}");
        Assert.True(a.SequenceEqual(b), $"{lump} differs (stock {a.Length}, managed {b.Length})");
    }
}
