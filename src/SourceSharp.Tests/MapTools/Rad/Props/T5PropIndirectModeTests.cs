using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.MapTools.Rad.Props;
using SourceSharp.Tests.MapTools.Rad.Ambient;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Props;

/// <summary>
/// The <c>-StaticPropIndirectMode</c> consumption: the three weightings the
/// ++ binary picks between inside <c>ComputeIndirectLightingAtPoint</c>
/// (<c>vraddetailprops.cpp:658</c> counterpart <c>FUN_14003ecb0</c>, gate byte
/// <c>0x1417194ec</c>).
/// </summary>
/// <remarks>
/// There is no ++ oracle: <c>PP_CATMAPS_DIR</c> holds no vrad++ outputs for
/// these modes, so each fact is a decomp-behavior parity check against the
/// disasm, cited per branch in
/// <c>PropIndirectLighting.Compute</c> (branches at all.c:46468 / 46478 /
/// 46496, fallthrough 46493-46495).
/// The fixture is the committed leaf-ambient map; the points are lit, so the
/// gather returns non-zero on every path.
/// </remarks>
public sealed class T5PropIndirectModeTests : IClassFixture<AmbientFixture>
{
    private readonly AmbientFixture _fixture;

    /// <summary>Takes the shared fixture.</summary>
    /// <param name="fixture">The loaded map.</param>
    public T5PropIndirectModeTests(AmbientFixture fixture) => _fixture = fixture;

    private Vec3 Gather(int mode, ComplianceOptions compliance, bool forceFast = true)
    {
        Vec3 p = _fixture.Ldr.WorldLights[0].Origin;
        DispTestedScratch scratch = _fixture.Ldr.Tracer.Displacements.CreateScratch();
        return PropIndirectLighting.Compute(
            _fixture.Ldr, p, new Vec3(0, 0, 1), forceFast, false, scratch, compliance, mode);
    }

    [Fact]
    public void TheDefaultIsModeZeroExactly()
    {
        // 0x1417194ec's .data word is 0 (all.c:46468 takes the default
        // branch at cold start), and an omitted argument must reproduce
        // today's bytes bit-for-bit -- the default-path freeze depends on it.
        Vec3 omitted = Gather(0, ComplianceOptions.Stock);
        Vec3 explicitZero = PropIndirectLighting.Compute(
            _fixture.Ldr,
            _fixture.Ldr.WorldLights[0].Origin,
            new Vec3(0, 0, 1),
            true,
            false,
            _fixture.Ldr.Tracer.Displacements.CreateScratch(),
            ComplianceOptions.Stock);
        Assert.Equal(explicitZero, omitted);
        Assert.True(explicitZero.X + explicitZero.Y + explicitZero.Z > 0, "the fixture gather must see light");
    }

    [Fact]
    public void ModeOneWeightsFromTheHitPointSoItNeverDarkensTheGather()
    {
        // all.c:46478-46491: d is measured from the ACCUMULATED hit point
        // (fVar21+fVar18 - position) scaled by local_164/128, where the
        // traced ray vector would be (vEnd - position) scaled the same way.
        // Since hit = position + (vEnd - position) * fraction with
        // 0 < fraction <= 1, mode 1's |d| <= mode 0's, so every non-sky hit
        // gets a weight nearer 1: each channel is >= mode 0's, and at least
        // one fixture ray lands short, so the sums differ.
        Vec3 mode0 = Gather(0, ComplianceOptions.Stock);
        Vec3 mode1 = Gather(1, ComplianceOptions.Stock);

        Assert.True(mode1.X >= mode0.X && mode1.Y >= mode0.Y && mode1.Z >= mode0.Z,
            $"mode 1 must not darken: {mode0} vs {mode1}");
        Assert.NotEqual(mode0, mode1);
    }

    [Fact]
    public void ModeOneAlsoMovesTheStockComplianceBytes()
    {
        // Q17 pairing: mode 1 reads state.Fraction, which the reused
        // enumerator (StockQuirk.IndirectSurfaceEnumeratorReused) carries
        // stale from the previous ray -- so the mode-1 shift must show up on
        // BOTH compliance sides, not only the correct one.
        Vec3 stock0 = Gather(0, ComplianceOptions.Stock);
        Vec3 stock1 = Gather(1, ComplianceOptions.Stock);
        Vec3 correct0 = Gather(0, ComplianceOptions.Correct);
        Vec3 correct1 = Gather(1, ComplianceOptions.Correct);

        Assert.NotEqual(stock0, stock1);
        Assert.NotEqual(correct0, correct1);
    }

    [Fact]
    public void ModeTwoDropsTheInverseSquareButKeepsReflectivity()
    {
        // all.c:46496-46502: the lightmap enters as lightmap * reflectivity
        // with weight 1 (fVar18 = the reflectivity scalar triple straight
        // into LAB_14003eece). Dropping the falloff changes the result unless
        // every hit already had weight 1 -- it does not: mode 0's d is
        // non-zero on the fixture (the mode-1 fact proves it), so mode 2
        // moves the bytes off mode 0.
        Vec3 mode0 = Gather(0, ComplianceOptions.Stock);
        Vec3 mode2 = Gather(2, ComplianceOptions.Stock);

        Assert.True(mode2.X + mode2.Y + mode2.Z > 0);
        Assert.NotEqual(mode0, mode2);
    }

    [Fact]
    public void AnOutOfRangeModeAccumulatesTheRawLightmap()
    {
        // all.c:46493-46495 (fallthrough past the ==2 test): fVar18/fVar24/
        // fVar19 = local_178/local_180/fStack_17c -- the UNWEIGHTED,
        // UNTINTED lightmap triple -- no weight, no reflectivity. That is a
        // third distinct observable: it differs from mode 0 (weight was
        // dropped) and from mode 2 (reflectivity also dropped, since the
        // fixture's surfaces do not all have unit reflectivity).
        Vec3 mode0 = Gather(0, ComplianceOptions.Stock);
        Vec3 mode2 = Gather(2, ComplianceOptions.Stock);
        Vec3 outOfRange = Gather(3, ComplianceOptions.Stock);

        Assert.True(outOfRange.X + outOfRange.Y + outOfRange.Z > 0);
        Assert.NotEqual(mode0, outOfRange);
        Assert.NotEqual(mode2, outOfRange);
    }
}
