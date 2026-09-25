using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Zip;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.MapTools.Rad.Props;
using SourceSharp.Tests.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Props;

/// <summary>
/// Static-prop per-vertex lighting against stock's own <c>.vhv</c> bytes on
/// the committed fixture. The models come from the installed game.
/// </summary>
public sealed class StaticPropLightingTests : IClassFixture<StaticPropFixture>
{
    private readonly StaticPropFixture _fixture;

    /// <summary>Takes the shared fixture.</summary>
    /// <param name="fixture">The loaded map.</param>
    public StaticPropLightingTests(StaticPropFixture fixture) => _fixture = fixture;

    [Fact]
    public void TheFixtureCarriesStocksEightVhvFiles()
    {
        Assert.Equal(8, _fixture.StockPak.Entries.Count(e => e.Name.EndsWith(".vhv", StringComparison.Ordinal)));
    }

    [InstalledGameFact]
    public async Task TheLdrPassWritesStocksFilesByteForByte()
    {
        StaticPropLightingResult r = await _fixture.RunAsync(LightingMode.Ldr);
        Assert.Equal(4, r.Files.Length);
        Assert.Empty(_fixture.Mismatches(r));
    }

    [InstalledGameFact]
    public async Task TheHdrPassWritesStocksFilesByteForByte()
    {
        StaticPropLightingResult r = await _fixture.RunAsync(LightingMode.Hdr);
        Assert.Equal(["sp_hdr_0.vhv", "sp_hdr_1.vhv", "sp_hdr_2.vhv", "sp_hdr_3.vhv"], r.Files.Select(f => f.FileName));
        Assert.Empty(_fixture.Mismatches(r));
    }

    [InstalledGameFact]
    public async Task EightWorkersWriteTheSameFilesAsOne()
    {
        StaticPropLightingResult one = await _fixture.RunAsync(LightingMode.Ldr, 1);
        StaticPropLightingResult eight = await _fixture.RunAsync(LightingMode.Ldr, 8);
        Assert.Equal(one.Files.Select(f => f.Data), eight.Files.Select(f => f.Data));
    }

    [InstalledGameFact]
    public async Task TheFixtureExercisesTheBadVertexCrawl()
    {
        StaticPropLightingResult r = await _fixture.RunAsync(LightingMode.Ldr);
        Assert.Equal(1364, r.BadVertices);
    }

    [InstalledGameFact]
    public async Task AFreshEnumeratorPerRayMovesTheFilesOffStocks()
    {
        ComplianceOptions fresh = ComplianceOptions.Stock.Flipping(StockQuirk.IndirectSurfaceEnumeratorReused);
        StaticPropLightingResult r = await _fixture.RunAsync(LightingMode.Ldr, 1, fresh);
        Assert.NotEmpty(_fixture.Mismatches(r));
    }

    [InstalledGameFact]
    public async Task RelightingABadVertexWithThePropsFlagsMovesTheFilesOffStocks()
    {
        ComplianceOptions keep = ComplianceOptions.Stock.Flipping(StockQuirk.StaticPropBadVertexDropsPropFlags);
        StaticPropLightingResult r = await _fixture.RunAsync(LightingMode.Ldr, 1, keep);
        Assert.NotEmpty(_fixture.Mismatches(r));
    }

    [InstalledGameFact]
    public async Task TheNoSelfShadowingPropIsLitWithoutItsOwnTriangles()
    {
        StaticPropLightingResult r = await _fixture.RunAsync(LightingMode.Ldr);
        Assert.Equal(1, r.SelfShadowingSkipped);
    }

    [InstalledGameFact]
    public async Task WritingIntoTheMapReplacesTheVhvEntries()
    {
        StaticPropLightingResult r = await _fixture.RunAsync(LightingMode.Ldr);
        BspData copy = await StaticPropFixture.LoadAsync();
        await StaticPropLighting.WriteIntoAsync(copy, r, CancellationToken.None);

        ZipArchiveReader pak = await ZipArchiveReader.ParseAsync(copy[BspLump.PakFile].Data);
        Assert.Equal(8, pak.Entries.Count);
        Assert.Equal(r.Files[0].Data, pak.Find("sp_0.vhv")!.Data);
    }
}
