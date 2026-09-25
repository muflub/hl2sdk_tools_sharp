using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapTools.Compare;

using Xunit;

namespace SourceSharp.Tests.MapTools.Compare;

/// <summary>
/// The rule that this instrument invents no tolerances.
/// </summary>
/// <remarks>
/// <para>
/// plan_maptools.md 2: a threshold is frozen from the first measurement in the
/// phase that owns the lump, and this instrument is built before those phases.
/// Phase 0 made it sharper still: in the threaded regime there is no per-sample
/// lightmap maximum that can be set at all, because stock vrad's own worst case
/// against itself is 3.396078 linear -- a factor of 5.5 on one real sample.
/// </para>
/// <para>
/// So an unset threshold has to be a VISIBLE state. A report that rendered it
/// as "within limits" would claim an agreement nobody has measured, which is
/// this project's "check that cannot fail" in its quietest form.
/// </para>
/// </remarks>
public sealed class BspDiffThresholdTests : IClassFixture<LockdownDiffFixture>
{
    private readonly LockdownDiffFixture _map;

    /// <summary>Takes the loaded golden map.</summary>
    /// <param name="map">The fixture.</param>
    public BspDiffThresholdTests(LockdownDiffFixture map) => _map = map;

    private static (BspData A, BspData B) Perturbed(BspData source)
    {
        BspData a = DiffMaps.Clone(source);
        BspData b = DiffMaps.Clone(source);
        Span<ColorRgbExp32> samples = DiffMaps.MutableLump<ColorRgbExp32>(b, BspLump.Lighting);
        for (int i = 0; i < samples.Length; i += 1000)
        {
            samples[i].R = (byte)(samples[i].R ^ 0x01);
        }

        return (a, b);
    }

    [Fact]
    public async Task EveryThresholdIsUnsetByDefault()
    {
        (BspData a, BspData b) = Perturbed(_map.Bsp);

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        Assert.NotEmpty(report.Judgements);
        Assert.All(report.Judgements, j => Assert.Equal(ThresholdVerdict.Unset, j.Verdict));
        Assert.All(report.Judgements, j => Assert.Null(j.Limit));
    }

    [Fact]
    public async Task AnUnsetThresholdNeverReadsAsAPass()
    {
        (BspData a, BspData b) = Perturbed(_map.Bsp);

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        Assert.False(report.ExceedsThreshold);
        Assert.DoesNotContain(report.Judgements, j => j.Verdict == ThresholdVerdict.Within);
        Assert.Contains("threshold unset", report.ToText(false), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AThresholdBelowTheMeasurementIsExceeded()
    {
        (BspData a, BspData b) = Perturbed(_map.Bsp);
        DiffOptions strict = new() { LightmapMaxLinear = 0.0 };

        DiffReport report = await BspDiff.CompareAsync(a, b, strict, CancellationToken.None);

        ThresholdJudgement max = Assert.Single(
            report.Judgements, j => string.Equals(j.Name, "Lighting.max", StringComparison.Ordinal));
        Assert.Equal(ThresholdVerdict.Exceeded, max.Verdict);
        Assert.True(report.ExceedsThreshold);
    }

    [Fact]
    public async Task AThresholdAboveTheMeasurementIsWithin()
    {
        (BspData a, BspData b) = Perturbed(_map.Bsp);
        DiffOptions loose = new() { LightmapMaxLinear = 1000.0 };

        DiffReport report = await BspDiff.CompareAsync(a, b, loose, CancellationToken.None);

        ThresholdJudgement max = Assert.Single(
            report.Judgements, j => string.Equals(j.Name, "Lighting.max", StringComparison.Ordinal));
        Assert.Equal(ThresholdVerdict.Within, max.Verdict);
        Assert.False(report.ExceedsThreshold);
    }

    [Fact]
    public async Task AThresholdSetToTheMeasuredValueItselfIsWithin()
    {
        // A threshold frozen AT a measured maximum has to accept that maximum,
        // or the very run that set it would fail.
        (BspData a, BspData b) = Perturbed(_map.Bsp);

        DiffReport first = await BspDiff.CompareAsync(a, b, CancellationToken.None);
        double measured = Assert.IsType<LightmapStatistics>(
            first.For(BspLump.Lighting).Lightmap).MaxLinear;

        DiffReport second = await BspDiff.CompareAsync(
            a, b, new DiffOptions { LightmapMaxLinear = measured }, CancellationToken.None);

        Assert.Equal(
            ThresholdVerdict.Within,
            Assert.Single(
                second.Judgements,
                j => string.Equals(j.Name, "Lighting.max", StringComparison.Ordinal)).Verdict);
    }

    [Fact]
    public async Task TheSupersetPropertyIsReportedAndNotJudgedByDefault()
    {
        DiffReport report = await BspDiff.CompareAsync(
            _map.Bsp, _map.Bsp, CancellationToken.None);

        ThresholdJudgement superset = Assert.Single(
            report.Judgements,
            j => string.Equals(j.Name, "VISIBILITY.pvs.bSupersetOfA", StringComparison.Ordinal));

        Assert.Equal(ThresholdVerdict.Unset, superset.Verdict);
        Assert.Equal(1.0, superset.Measured);
    }

    [Fact]
    public async Task TheSupersetPropertyCanBeRequired()
    {
        DiffReport report = await BspDiff.CompareAsync(
            _map.Bsp,
            _map.Bsp,
            new DiffOptions { RequireVisibilitySupersetOfA = true },
            CancellationToken.None);

        Assert.Equal(
            ThresholdVerdict.Within,
            Assert.Single(
                report.Judgements,
                j => string.Equals(j.Name, "VISIBILITY.pvs.bSupersetOfA", StringComparison.Ordinal))
                .Verdict);
    }

    [Fact]
    public async Task TheFivePhaseZeroStatisticsAreAllReported()
    {
        // The set is fixed by what Phase 0 measured -- 2.90 % of samples moved,
        // p99 0.000245, p99.9 0.003922, p99.99 0.041176, max 3.396078. A report
        // missing one of them cannot be read against that measurement.
        (BspData a, BspData b) = Perturbed(_map.Bsp);

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);
        string[] names = [.. report.For(BspLump.Lighting).Judgements.Select(j => j.Name)];

        Assert.Contains("Lighting.max", names);
        Assert.Contains("Lighting.mean", names);
        Assert.Contains("Lighting.p99", names);
        Assert.Contains("Lighting.p99.9", names);
        Assert.Contains("Lighting.p99.99", names);
        Assert.Contains("Lighting.differingFraction", names);
    }

    [Fact]
    public async Task ThePercentilesAreOrderedAndBoundedByTheMaximum()
    {
        (BspData a, BspData b) = Perturbed(_map.Bsp);

        LightmapStatistics stats = Assert.IsType<LightmapStatistics>(
            (await BspDiff.CompareAsync(a, b, CancellationToken.None))
                .For(BspLump.Lighting).Lightmap);

        Assert.True(stats.P99Linear <= stats.P999Linear);
        Assert.True(stats.P999Linear <= stats.P9999Linear);
        Assert.True(stats.P9999Linear <= stats.MaxLinear);
        Assert.True(stats.MeanLinear <= stats.MaxLinear);
    }

    [Fact]
    public async Task TheDifferingFractionIsTheShareOfSamplesThatMoved()
    {
        (BspData a, BspData b) = Perturbed(_map.Bsp);

        LightmapStatistics stats = Assert.IsType<LightmapStatistics>(
            (await BspDiff.CompareAsync(a, b, CancellationToken.None))
                .For(BspLump.Lighting).Lightmap);

        Assert.Equal(
            (double)stats.DifferingSampleCount / stats.SampleCount,
            stats.DifferingFraction,
            15);
        Assert.True(stats.DifferingSampleCount > 0);
    }

    [Fact]
    public void TheLinearDecodeIsTheOneMathlibDocuments()
    {
        // linear = c * 2^e / 255 (with the reference implementation's
        // power2_n = 2**(index-128)/255). Phase 0's headline maximum is the
        // green channel of 2fort sample #538369 moving from rgbe(_,182,_,0) to
        // rgbe(_,131,_,3), and reproducing that number here is what says the
        // two are in the same scale.
        double before = LinearLight.Channel(182, 0);
        double after = LinearLight.Channel(131, 3);

        Assert.Equal(3.396078, after - before, 6);
    }
}
