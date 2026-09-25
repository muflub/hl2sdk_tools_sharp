using System.Collections.Immutable;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapTools.Compare;
using SourceSharp.MapTools.Options;

namespace SourceSharp.Tests.MapTools.Options;

/// <summary>What one <c>2 + 2N</c> gate row proves for one quirk (§11a).</summary>
/// <remarks>
/// <para>
/// A row is either a <see cref="Probe"/>, which runs one fixture whose site the
/// quirk decides twice under the two policies and once under each command-line
/// flip, or a <see cref="Refused"/>, which records that no managed-mode fixture
/// can reach the site and names the stock C++ that proves why. REFUSED counts as
/// satisfied-with-provenance in the completeness fact; it is never silent.
/// </para>
/// </remarks>
/// <param name="Quirk">The quirk.</param>
/// <param name="Fixture">What the probe runs — named so the matrix can cite it.</param>
/// <param name="Lumps">
/// The lumps (or, for a pure-site probe, the struct array) the flip is allowed to
/// move. I2 asserts nothing outside this set changes.
/// </param>
/// <param name="WholeStage">
/// True when the probe is a whole-stage compile, so I4 (byte identity at
/// <c>-threads 1</c> and <c>-threads 32</c>) applies to it.
/// </param>
public sealed record FlipGateProbe(
    StockQuirk Quirk,
    string Fixture,
    ImmutableArray<string> Lumps,
    bool WholeStage)
{
    /// <summary>
    /// The observable the gate fingerprints, as a function of the policy. The
    /// four calls a row makes are this at <c>correct</c>, at <c>stock</c>, at the
    /// record <c>-compliance correct,+Q</c> builds, and at the record
    /// <c>-compliance stock,-Q</c> builds.
    /// </summary>
    public required Func<ComplianceOptions, Task<byte[]>> Fingerprint { get; init; }

    /// <summary>
    /// For a whole-stage row, the same observable as a function of policy AND
    /// <c>-threads</c>: I4 requires its bytes to agree at 1 and 32. Pure-site
    /// probes leave it null — the fleet's threading gate is about stage
    /// schedulers, and a pure function has none.
    /// </summary>
    public Func<ComplianceOptions, int, Task<byte[]>>? ThreadsFingerprint { get; init; }

    /// <summary>
    /// For a whole-stage row, the two stage outputs whose lump-level delta I2
    /// constrains: the <see cref="BspDiff"/> verdict must show a non-empty
    /// changed set and nothing changed outside <see cref="Lumps"/>. The
    /// existing product differ is the witness — the gate invents none.
    /// Null for pure-site probes, whose fingerprint IS the named observable.
    /// </summary>
    public Func<ComplianceOptions, ComplianceOptions, Task<(BspData A, BspData B)>>? I2Diff { get; init; }
}

/// <summary>A quirk no managed fixture can reach, with its proof.</summary>
/// <param name="Quirk">The quirk.</param>
/// <param name="Reason">Why no managed-mode fixture reaches the site.</param>
/// <param name="StockSite">The stock C++ (<c>path:line</c>) the defect lives at.</param>
public sealed record FlipGateRefused(StockQuirk Quirk, string Reason, string StockSite);

/// <summary>One row of the gate: exactly one probe or one refusal.</summary>
public abstract record FlipGateRow
{
    public abstract StockQuirk Quirk { get; }

    public sealed override string ToString() => Quirk.ToString()!;
}

public sealed record FlipGateProbeRow(FlipGateProbe Probe) : FlipGateRow
{
    public override StockQuirk Quirk => Probe.Quirk;
}

public sealed record FlipGateRefusedRow(FlipGateRefused Refused) : FlipGateRow
{
    public override StockQuirk Quirk => Refused.Quirk;
}

/// <summary>
/// The registry behind §11a's <c>2 + 2N</c> gate: every <see cref="StockQuirk"/>'s
/// reaching fixture, keyed by quirk, with the lumps each flip is allowed to move.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the fingerprints are the fixture's existing call sequences.</b> Every
/// probe below is the call a per-quirk effect fact already makes, turned into a
/// function of the policy instead of a hardcoded pair of assertions — so the gate
/// does not invent a second, weaker witness for a site the lane has already
/// proven, and a probe that stops reaching its site fails the row rather than
/// passing vacuously.
/// </para>
/// <para>
/// <b>The CLI pairs.</b> The two override comparisons run the fixture under the
/// record each verb's own parser builds for <c>-compliance correct,+Q</c> and
/// <c>-compliance stock,-Q</c> (<see cref="FlipGate.CliOptions"/>), not under a
/// library <see cref="ComplianceOptions.Flipping"/> literal — which is what makes
/// the row's evidence CLI evidence. The library flip is still checked, as the
/// cross-check that the parsed record and the library record fingerprint alike.
/// </para>
/// <para>
/// No static state: <see cref="Rows"/> is rebuilt on every call.
/// </para>
/// </remarks>
internal static class FlipGate
{
    /// <summary>The verb whose command line owns a quirk's site (catalogue tools).</summary>
    internal static string OwningTool(StockQuirk quirk)
    {
        CompileTools tools = ComplianceCatalogue.Describe(quirk).Tools;
        return tools.HasFlag(CompileTools.Vrad)
            ? "vrad"
            : tools.HasFlag(CompileTools.Vbsp) ? "vbsp" : "vvis";
    }

    /// <summary>
    /// The record <c>-compliance correct|stock[,±Q]</c> builds on that quirk's
    /// owning verb's command line, through that verb's real parser entry.
    /// </summary>
    internal static ComplianceOptions CliOptions(StockQuirk quirk, string baseline, char sign)
    {
        string text = $"{baseline},{sign}{quirk}";
        string[] args = ["-compliance", text, "maps/testmap.bsp"];
        return OwningTool(quirk) switch
        {
            "vbsp" => StockArgs.ParseVbsp(args).Options.Compliance,
            "vrad" => StockArgs.ParseVrad(args).Options.Compliance,
            _ => StockArgs.ParseVvis(args).Options.Compliance,
        };
    }

    /// <summary>Every quirk's gate row, in enum order.</summary>
    public static IReadOnlyList<FlipGateRow> Rows => [.. Build()];

    private static IEnumerable<FlipGateRow> Build()
    {
        Dictionary<StockQuirk, FlipGateProbe> probes = ComplianceFlipGateProbes.Probes();

        foreach (StockQuirk quirk in Enum.GetValues<StockQuirk>())
        {
            if (probes.TryGetValue(quirk, out FlipGateProbe? probe))
            {
                yield return new FlipGateProbeRow(probe);
            }
            else if (ComplianceFlipGateProbes.Refused.TryGetValue(quirk, out FlipGateRefused? refused))
            {
                yield return new FlipGateRefusedRow(refused);
            }
            else
            {
                // The completeness fact names this case; nothing reaches here
                // unnoticed.
                yield return new FlipGateRefusedRow(
                    new FlipGateRefused(
                        quirk,
                        "unregistered: no probe and no refusal — the completeness fact fails on this",
                        "unknown"));
            }
        }
    }
}
