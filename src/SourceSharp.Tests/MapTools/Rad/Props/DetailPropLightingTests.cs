using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.MapTools.Rad.Props;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Props;

/// <summary>
/// <c>ComputeDetailPropLighting</c> against
/// stock's own bytes on the committed fixture.
/// </summary>
public sealed class DetailPropLightingTests : IClassFixture<DetailPropFixture>
{
    private readonly DetailPropFixture _fixture;

    /// <summary>Takes the shared fixture.</summary>
    /// <param name="fixture">The loaded map.</param>
    public DetailPropLightingTests(DetailPropFixture fixture) => _fixture = fixture;

    private static byte[] LightingBytes(IEnumerable<DetailObjectLump> props) =>
        [.. props.SelectMany(p => new[] { p.Lighting.R, p.Lighting.G, p.Lighting.B, (byte)p.Lighting.Exponent })];

    [Fact]
    public async Task TheHdrPassLightsEveryPropAsStockDid()
    {
        // After -both, dprp's m_Lighting is the HDR pass's (it runs last).
        DetailPropLightingResult r = await _fixture.RunAsync(LightingMode.Hdr);

        Assert.Equal(LightingBytes(_fixture.Stock.Props), LightingBytes(r.Props));
    }

    [Fact]
    public async Task TheHdrStyleLumpIsStocksByteForByte()
    {
        DetailPropLightingResult r = await _fixture.RunAsync(LightingMode.Hdr);

        Assert.Equal(
            MemoryMarshal.AsBytes<DetailPropLightstylesLump>(DetailPropLighting.ReadStyleLump(_fixture.Lump(GameLumpId.DetailPropLightingHdr))).ToArray(),
            MemoryMarshal.AsBytes<DetailPropLightstylesLump>(r.LightStyles).ToArray());
    }

    [Fact]
    public async Task TheLdrStyleLumpIsStocksByteForByte()
    {
        DetailPropLightingResult r = await _fixture.RunAsync(LightingMode.Ldr);

        Assert.Equal(
            MemoryMarshal.AsBytes<DetailPropLightstylesLump>(DetailPropLighting.ReadStyleLump(_fixture.Lump(GameLumpId.DetailPropLighting))).ToArray(),
            MemoryMarshal.AsBytes<DetailPropLightstylesLump>(r.LightStyles).ToArray());
    }

    [Fact]
    public async Task TheStyledLightGivesEveryPropAStyleRecord()
    {
        // The fixture's style-5 light reaches every prop: 764 records, one each.
        DetailPropLightingResult r = await _fixture.RunAsync(LightingMode.Hdr);

        Assert.Equal((764, 764), (r.LightStyles.Length, r.Props.Count(p => p.LightStyleCount == 1)));
    }

    [Fact]
    public async Task WritingThePassBackReproducesStocksGameLumps()
    {
        // WriteDetailLightingLumps:929) plus the in-place dprp update: after
        // writing the HDR pass into a copy, dprp and dplh are stock's bytes.
        DetailPropLightingResult r = await _fixture.RunAsync(LightingMode.Hdr);
        BspData copy = new();
        copy.GameLumps.AddRange(_fixture.Bsp.GameLumps);

        DetailPropLighting.WriteInto(copy, DetailPropLump.Read(_fixture.Lump(GameLumpId.DetailProps)), r, hdr: true);

        Assert.Equal(
            _fixture.Lump(GameLumpId.DetailProps).Data.ToArray(),
            copy.GameLumps.First(e => e.Id == GameLumpId.MakeId(GameLumpId.DetailProps)).Data.ToArray());
    }

    [Fact]
    public async Task OneAndFourWorkersProduceTheSameBytes()
    {
        DetailPropLightingResult a = await _fixture.RunAsync(LightingMode.Hdr, 1);
        DetailPropLightingResult b = await _fixture.RunAsync(LightingMode.Hdr, 4);

        Assert.Equal(LightingBytes(a.Props), LightingBytes(b.Props));
    }

    [Fact]
    public async Task WithoutTheDirectLightsThePropsAreDarker()
    {
        // Mutation proof: the direct half is load-bearing.
        AmbientScene scene = AmbientScene.Create(_fixture.Bsp, LightingMode.Hdr);
        DetailPropLightingResult r = await DetailPropLighting.ComputeAsync(
            scene, _fixture.Stock, [], [], new PropLightSampler(_fixture.Environment, ComplianceOptions.Stock),
            ComplianceOptions.Stock, 1, CancellationToken.None);

        Assert.NotEqual(LightingBytes(_fixture.Stock.Props), LightingBytes(r.Props));
    }

    [Fact]
    public void TheFixtureHasSpritesOnly()
    {
        // No models: no content is needed for the unit tier.
        Assert.Equal((0, 2, 764), (_fixture.Stock.ModelNames.Count, _fixture.Stock.Sprites.Count, _fixture.Stock.Props.Count));
    }

    [Fact]
    public void ASpritesCentreIsTheMidpointOfItsCorners()
    {
        // UnserializeSpriteDict:858): (0, (LR.x + UL.x)/2, (LR.y + UL.y)/2).
        DetailSpriteDictLump d = _fixture.Stock.Sprites[0];
        Vec3 c = DetailPropLighting.SpriteCentres(_fixture.Stock)[0];

        Assert.Equal(new Vec3(0, (d.LowerRight[0] + d.UpperLeft[0]) * 0.5f, (d.LowerRight[1] + d.UpperLeft[1]) * 0.5f), c);
    }

    [Fact]
    public void AnglesOfZeroGiveTheIdentityBasis()
    {
        (Vec3 f, Vec3 r, Vec3 u) = DetailPropLighting.AngleVectors(new Vec3(0, 0, 0));

        Assert.Equal((new Vec3(1, 0, 0), new Vec3(0, -1, 0), new Vec3(0, 0, 1)), (f, r, u));
    }

    [Fact]
    public void AYawOfNinetyTurnsForwardOntoY()
    {
        (Vec3 f, _, _) = DetailPropLighting.AngleVectors(new Vec3(0, 90, 0));

        Assert.Equal((0f, 1f), (MathF.Round(f.X, 6), f.Y));
    }

    [Fact]
    public void AStyleLumpRoundTrips()
    {
        GameLumpEntry stock = _fixture.Lump(GameLumpId.DetailPropLightingHdr);

        Assert.Equal(stock.Data.ToArray(), DetailPropLighting.WriteStyleLump(DetailPropLighting.ReadStyleLump(stock), hdr: true).Data.ToArray());
    }
}
