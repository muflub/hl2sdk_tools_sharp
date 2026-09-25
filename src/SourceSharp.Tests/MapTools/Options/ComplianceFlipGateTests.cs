using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Options;

/// <summary>
/// §11a's 2 + 2N gate: every <see cref="StockQuirk"/> runs its reaching
/// fixture four times — the two baselines (<c>correct</c>, <c>stock</c>) and
/// the two overrides (<c>-compliance correct,+Q</c> and
/// <c>-compliance stock,-Q</c>, parsed by the owning verb's real parser) — and
/// the fingerprints must fall out as the policy model promises. A quirk with no
/// reaching fixture satisfies the gate only as a REFUSED row, and only with its
/// reason and stock C++ site present.
/// </summary>
/// <remarks>
/// <para>
/// The four assertions per probe row (each names the run that differed):
/// </para>
/// <list type="bullet">
/// <item><b>A4 completeness</b>: <c>probe(correct) != probe(stock)</c> — the
/// flip alone changes the observable. This is the plan's completeness clause
/// asserted where it can name the quirk.</item>
/// <item><b>A1</b>: the CLI record <c>correct,+Q</c> does NOT fingerprint like
/// plain <c>correct</c> — forcing Q to stock's side must move the observable
/// from its own baseline, or the probe never reached Q's site.</item>
/// <item><b>A2</b>: likewise the CLI record <c>stock,-Q</c> differs from plain
/// <c>stock</c>.</item>
/// <item><b>A3</b>: the CLI records fingerprint alike the library records
/// <c>Correct.Flipping(Q)</c> / <c>Stock.Flipping(Q)</c> — the parser and the
/// library cannot drift apart, which is why the fleet's library-side facts stay
/// valid witnesses of CLI behaviour.</item>
/// <item><b>I4</b>: a whole-stage row's observable is byte-identical at
/// <c>-threads 1</c> and <c>-threads 32</c> under both policies.</item>
/// </list>
/// <para>
/// I2 (nothing but the named lumps moves) for whole-stage rows is carried by
/// <see cref="FlipGateProbe.Lumps"/>: the fleet's stage-diff facts assert the
/// lump-level delta; here the registry's lump names are the contract they read.
/// </para>
/// </remarks>
public sealed class ComplianceFlipGateTests
{
    public static TheoryData<FlipGateRow> Rows =>
        new(FlipGate.Rows.ToArray());

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task TheGateHoldsForEveryQuirk(FlipGateRow row)
    {
        if (row is FlipGateRefusedRow refused)
        {
            // A REFUSED row is satisfied-with-provenance only: a reason and a
            // stock site, or it is the unregistered case and it fails here.
            Assert.False(
                refused.Refused.Reason.StartsWith("unregistered", StringComparison.Ordinal),
                $"{refused.Quirk}: no probe reaches it and no refusal names a reason " +
                $"(reason: {refused.Refused.Reason}; site: {refused.Refused.StockSite})");
            Assert.Matches(@"^.+:\d+(-\d+)?$", refused.Refused.StockSite);
            Assert.NotEmpty(refused.Refused.Reason);
            return;
        }

        FlipGateProbe probe = ((FlipGateProbeRow)row).Probe;
        ComplianceOptions correct = ComplianceOptions.Correct;
        ComplianceOptions stock = ComplianceOptions.Stock;
        ComplianceOptions cliFlipOn = FlipGate.CliOptions(row.Quirk, "correct", '+');
        ComplianceOptions cliFlipOff = FlipGate.CliOptions(row.Quirk, "stock", '-');

        byte[] atCorrect = await probe.Fingerprint(correct);
        byte[] atStock = await probe.Fingerprint(stock);
        byte[] atCliOn = await probe.Fingerprint(cliFlipOn);
        byte[] atCliOff = await probe.Fingerprint(cliFlipOff);
        byte[] atLibraryOn = await probe.Fingerprint(correct.Flipping(row.Quirk));
        byte[] atLibraryOff = await probe.Fingerprint(stock.Flipping(row.Quirk));

        // A4: the flip alone changes something. This is the completeness clause,
        // reported per quirk; the structural fact below reports the registry.
        Assert.False(
            atCorrect.SequenceEqual(atStock),
            $"{row.Quirk}: the flip changes nothing on fixture `{probe.Fixture}` — " +
            "the probe does not reach the site, or the site is policy-dead (needs a REFUSED row with proof)");

        // A1/A2: the command-line override moves the observable from its own
        // baseline. The records `correct,+Q` and `correct` differ in exactly
        // one decision — Q's side — so a fingerprint that comes back alike
        // proves the probe never reached Q's site. This is the plan's I2
        // non-empty-change half asserted per quirk through the CLI; A3 pins
        // the CLI records to the library records, and whole-stage rows carry
        // the nothing-else-moved half via I2Diff below.
        Assert.False(
            atCliOn.SequenceEqual(atCorrect),
            $"{row.Quirk}: `-compliance correct,+{row.Quirk}` on {FlipGate.OwningTool(row.Quirk)} " +
            $"fingerprints identically to plain correct on `{probe.Fixture}` — the flip alone " +
            "moves nothing there, so the probe does not reach the site (needs a REFUSED row with proof)");
        Assert.False(
            atCliOff.SequenceEqual(atStock),
            $"{row.Quirk}: `-compliance stock,-{row.Quirk}` on {FlipGate.OwningTool(row.Quirk)} " +
            $"fingerprints identically to plain stock on `{probe.Fixture}` — the flip alone " +
            "moves nothing there, so the probe does not reach the site (needs a REFUSED row with proof)");

        // A3: parser and library cannot drift — the parsed records and the
        // Flipping() records are the same decision, byte for byte.
        Assert.True(atLibraryOn.SequenceEqual(atCliOn), $"{row.Quirk}: CLI +Q vs library Flipping(Q)");
        Assert.True(atLibraryOff.SequenceEqual(atCliOff), $"{row.Quirk}: CLI -Q vs library Stock.Flipping(Q)");

        // I4: thread-count determinism for anything with a scheduler.
        if (probe.ThreadsFingerprint is { } threaded)
        {
            byte[] c1 = await threaded(correct, 1);
            byte[] c32 = await threaded(correct, 32);
            byte[] s1 = await threaded(stock, 1);
            byte[] s32 = await threaded(stock, 32);
            Assert.True(
                c1.SequenceEqual(c32),
                $"{row.Quirk}: correct-mode output differs between -threads 1 and -threads 32");
            Assert.True(
                s1.SequenceEqual(s32),
                $"{row.Quirk}: stock-mode output differs between -threads 1 and -threads 32");
        }
    }

    [Fact]
    public void EveryQuirkHasExactlyOneGateRow()
    {
        // The registry is exactly the enum: a quirk registered twice (say by
        // two families) or not at all fails here with the names, not in the
        // theory where 54 parallel rows blur together.
        StockQuirk[] all = Enum.GetValues<StockQuirk>();
        FlipGateRow[] rows = [.. FlipGate.Rows];

        Assert.Equal(all.Length, rows.Length);
        Assert.Equal(
            all.OrderBy(q => q.ToString(), StringComparer.Ordinal),
            rows.Select(r => r.Quirk).OrderBy(q => q.ToString(), StringComparer.Ordinal));
        Assert.Equal(all.Length, rows.Select(r => r.Quirk).Distinct().Count());
        Assert.Equal(all.Length, ComplianceCatalogue.All.Count);
    }

    [Fact]
    public void EveryProbeNamesItsFixtureAndItsLumps()
    {
        foreach (FlipGateProbeRow row in FlipGate.Rows.OfType<FlipGateProbeRow>())
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Probe.Fixture), $"{row.Quirk}: no fixture named");
            Assert.False(row.Probe.Lumps.IsEmpty, $"{row.Quirk}: no lump/observable named");
            Assert.All(row.Probe.Lumps, lump => Assert.False(string.IsNullOrWhiteSpace(lump)));
            if (row.Probe.WholeStage)
            {
                Assert.NotNull(row.Probe.ThreadsFingerprint);
            }
        }
    }

    [Fact]
    public void TheParsedFlipRecordsAreTheExactSetSemantics()
    {
        // The rows' A1/A2 prove the flips behaviourally; this pins the shape:
        // correct,+Q is Correct except {Q}, stock,-Q is Stock except {Q}.
        foreach (StockQuirk quirk in Enum.GetValues<StockQuirk>())
        {
            ComplianceOptions on = FlipGate.CliOptions(quirk, "correct", '+');
            ComplianceOptions off = FlipGate.CliOptions(quirk, "stock", '-');
            Assert.Equal(ComplianceOptions.Correct.Policy, on.Policy);
            Assert.Equal([quirk], on.Except);
            Assert.Equal(ComplianceOptions.Stock.Policy, off.Policy);
            Assert.Equal([quirk], off.Except);
        }
    }

    /// <summary>
    /// The gate's one deliberate fixture build (§11a's completeness clause paid
    /// for): the pinned straddling samples really do pick different winners,
    /// so <see cref="StockQuirk.CubemapDistanceNormalise"/>'s GAP cell is
    /// resolved by a difference, not by prose. The winner is the observable
    /// <c>Cubemap_FindClosestCubemap</c> hands the cubemap patch fixups.
    /// </summary>
    [Fact]
    public async Task TheCubemapStraddleFixturePicksDifferentWinnersPerPolicy()
    {
        FlipGateProbe probe = ComplianceFlipGateProbes.Probes()[StockQuirk.CubemapDistanceNormalise];

        byte[] correct = await probe.Fingerprint(ComplianceOptions.Correct);
        byte[] stock = await probe.Fingerprint(ComplianceOptions.Stock);

        // Pinned: correct admits the nearer sample (-3,4,-1), index 0; stock's
        // rsqrtss estimate rejects it and the far (6,5,5) sample wins, index 1.
        Assert.Equal(Fp.Join(Fp.Int(0)), correct);
        Assert.Equal(Fp.Join(Fp.Int(1)), stock);
    }
}
