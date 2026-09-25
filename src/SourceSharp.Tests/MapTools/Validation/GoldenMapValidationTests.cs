using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapTools.Validation;
using SourceSharp.Tests.MapFormats;

using Xunit;

namespace SourceSharp.Tests.MapTools.Validation;

/// <summary>
/// The control: the committed golden map must pass every rule.
/// </summary>
/// <remarks>
/// <para>
/// This is the half of the gate that stops the rule set drifting into
/// pedantry. Every corruption fact says "this rule CAN fire"; this one says
/// "and it does not fire on a real map the engine loads". Without it a rule
/// that fired on everything would still look proven.
/// </para>
/// <para>
/// <c>dm_lockdown.bsp</c> is deliberately an AWKWARD specimen: BSP version 19,
/// LEAFS at lump version 0 with 56-byte leaves and the ambient cube inline, no
/// HDR lumps, no leaf-ambient lumps, and it carries lump 49 and lump 32, which
/// the current bsplib abandoned. None of that is a defect and no rule here may
/// treat it as one.
/// </para>
/// </remarks>
public class GoldenMapValidationTests
{
    [Fact]
    public async Task GoldenMapHasNoFindingsAtAll()
    {
        BspData bsp = await Corrupted.GoldenAsync();
        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Assert.True(
            report.Diagnostics.IsEmpty,
            "dm_lockdown.bsp is a real map the engine loads, so every rule must pass it. "
            + "Findings:" + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                report.Diagnostics.Select(d => $"  {d.Code} {d.Severity}: {d.Message}")));
    }

    [Fact]
    public async Task GoldenMapIsClean()
    {
        BspData bsp = await Corrupted.GoldenAsync();
        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Assert.True(report.IsClean);
        Assert.Equal(0, report.ErrorCount);
        Assert.Equal(0, report.WarningCount);
    }

    [Fact]
    public async Task GoldenMapPassesTheHeaderGateFromItsBytes()
    {
        await using FileStream stream = File.OpenRead(GoldenBsp.Lockdown());
        ValidationReport report = await BspValidator.CheckFileAsync(stream, CancellationToken.None);

        Assert.True(report.IsClean);
    }

    [Fact]
    public async Task GoldenMapIsVersion19WithVersion0Leaves()
    {
        // Not decoration: the two rules most likely to be written against
        // "what a fresh compile looks like" -- the file version gate and the
        // leaf stride -- are exactly the ones this specimen would catch, so
        // the fact that it IS the awkward case is worth pinning.
        BspData bsp = await Corrupted.GoldenAsync();

        Assert.Equal(19, bsp.FileVersion);
        Assert.Equal(0, bsp[BspLump.Leafs].Version);
        Assert.True(bsp[BspLump.LightingHdr].IsEmpty);
        Assert.True(bsp[BspLump.LeafAmbientLighting].IsEmpty);
    }

    [Fact]
    public async Task CancellationIsObserved()
    {
        BspData bsp = await Corrupted.GoldenAsync();
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BspValidator.CheckAsync(bsp, cts.Token));
    }

    [Fact]
    public async Task ANullContainerIsARejectedArgumentAndNotAFinding()
    {
        // The line the whole diagnostics design turns on: a bad MAP produces
        // findings, a bad CALL throws.
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => BspValidator.CheckAsync(null!, CancellationToken.None));
    }
}
