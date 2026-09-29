//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Vis;

namespace SourceSharp.MapTools.Options;

/// <summary>
/// What vvis was asked to do. Defaults are stock's defaults.
/// </summary>
/// <remarks>
/// <para>
/// A typed immutable record rather than an <c>argv</c>, so a host states its
/// intent instead of assembling a command line and a stage cannot read an
/// option nobody passed. The stock-spelling parser is a separate, public
/// function, so a host that genuinely has a Hammer command line can still use
/// one.
/// </para>
/// <para>
/// The options stock advertises in its usage text but does not implement
/// (<c>-tmpout</c>, <c>-x360</c>) are absent, as are the ones this port drops
/// with reasons in the option audit: everything <c>-mpi*</c> (Windows-only
/// cluster mode), <c>-FullMinidumps</c>, <c>-allowdebug</c>/<c>-steam</c>.
/// <c>-low</c> is a process priority, which belongs to whoever owns the
/// process and not to a library.
/// </para>
/// </remarks>
public sealed record VvisOptions
{
    /// <summary>
    /// Skip the full portal flow and keep only the flood-fill approximation.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-fast</c>. Produces a conservative superset of the real PVS:
    /// more overdraw at runtime, never missing geometry. It is what the RPG
    /// plan currently specifies for its generated levels.
    /// </remarks>
    public bool Fast { get; init; }

    /// <summary>Emit the per-stage commentary stock's <c>-v</c> emits.</summary>
    public bool Verbose { get; init; }

    /// <summary>
    /// Override the visibility radius, in world units, instead of taking it
    /// from the map.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-radius_override</c>. Null means the radius comes from the
    /// map's <c>env_fog_controller</c> <c>farz</c>, which is what
    /// <c>DetermineVisRadius</c> does.
    /// </remarks>
    public float? RadiusOverride { get; init; }

    /// <summary>
    /// Trace visibility between two specific clusters and report the result.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-trace a b</c>: a debugging aid that answers "why can these
    /// two clusters see each other?". Null when not tracing.
    /// </remarks>
    public (int From, int To)? Trace { get; init; }

    /// <summary>
    /// Process portals in their original order rather than sorted by how much
    /// each might see.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stock's <c>-nosort</c>. Sorting exists because portal cost varies
    /// enormously and stock processes cheapest first, which leaves the
    /// expensive portals as a serial tail.
    /// </para>
    /// <para>
    /// It is TEMPTING to call this a free race detector -- "order cannot change
    /// the answer, so sorted and unsorted must agree" -- and this port's notes
    /// said exactly that until it was measured. It is false of stock. Spike 0c
    /// ran stock vvis on a frozen 2fort and found <c>-nosort</c> moves 120 PVS
    /// and 782 PAS rows, gaining AND losing bits, and that even plain
    /// <c>-threads 1</c> versus <c>-threads 16</c> differs. The cause is the
    /// pruning optimisation: a portal's flow reads a neighbour's COMPLETED
    /// visibility when it has one and its flood set otherwise, so how much
    /// pruning has happened by then depends on the order and on how many
    /// threads are running.
    /// </para>
    /// <para>
    /// So this flag is a gate on THIS port and a measured non-property of
    /// stock. Managed vvis must give byte-identical output sorted and unsorted,
    /// and at every thread count, which means its pruning may not depend on
    /// what happens to have finished -- see <see cref="Tighten"/>, whose
    /// fixed-point form is order-independent by construction. Comparisons
    /// against stock must pin <c>(threads, sort)</c> or assert containment
    /// instead.
    /// </para>
    /// </remarks>
    public bool NoSort { get; init; }

    /// <summary>
    /// Tighten <c>mightsee</c> from completed portals, pruning candidates
    /// earlier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a stock option. Re-implemented from ericw-tools' description of the
    /// idea and none of its code, which is GPL. It can legitimately change the
    /// PVS very slightly: a chain that floating-point clipping would have let
    /// through can be pruned first. So it shipped behind <c>-tighten</c>,
    /// "reported with its per-cluster PVS delta on every corpus map, and
    /// [to be] promoted to default in Phase 5 only if that delta never ADDS
    /// a bit anywhere" (the tightening gate; the promotion ruling repeats
    /// that condition). The promotion condition held: on the 31-map catalogue and
    /// the L4 corpus the tightened walk is BIT-IDENTICAL to stock at
    /// <c>-threads 1</c>, sorted — the delta never adds a bit, it removes
    /// exactly what stock's own finished-neighbour read removes — so it is
    /// ON by default since the vis-repair work (post-Phase-12). The
    /// untightened walk stays reachable: <c>-loose</c> on the command line,
    /// <see cref="Untightened"/> in the library, for the comparisons that
    /// want the conservative superset arm.
    /// </para>
    /// <para>
    /// It must be implemented as an iteration to a FIXED POINT -- repeat until
    /// nothing changes -- rather than as stock's opportunistic "use a
    /// neighbour's answer if it happens to be ready". A fixed point does not
    /// depend on the order work completed in, which is what keeps the output
    /// byte-identical across thread counts. Spike 0c measured that stock's
    /// opportunistic form does not have that property. Stock's own default
    /// therefore only has the tightening's pruning WHEN ITS SCHEDULE STUMBLES
    /// INTO IT; this option always has it, deterministically, at the cost of
    /// the untightened arm's ~4x chains (measured on 2fort: 569.6M chains vs
    /// 145.4M, and the untightened arm matches no stock thread count).
    /// </para>
    /// </remarks>
    public bool Tighten { get; init; } = true;

    /// <summary>
    /// <see cref="FastFlowSteps"/> when <c>-fastflow</c> is given without a
    /// value: the measured knee of the speed/accuracy curve on 2fort.
    /// </summary>
    public const int DefaultFastFlowSteps = 1000;

    private readonly int? _fastFlowSteps;

    /// <summary>
    /// Run a faster portal flow whose PVS is knowingly approximate, letting
    /// each portal's walk take this many exact steps before it may stop
    /// early; null (the default) for the exact flow.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a stock option (<c>-fastflow</c> for
    /// <see cref="DefaultFastFlowSteps"/>, <c>-fastflow=N</c> for any other
    /// N, parsed by <see cref="StockArgs.ParseVvis"/> like
    /// <c>-compliance</c>), and off by default: with it off nothing about the
    /// flow changes and the output is byte-identical to a compile that never
    /// heard of it. It is in the spirit of Tools++'s faster vvis, and unlike
    /// Tools++ it is deterministic: the same bytes at every thread count and
    /// on every run, for any N.
    /// </para>
    /// <para>
    /// After N steps the walk stops a chain once everything it could still
    /// reach leads into clusters the portal already sees (the mechanism and
    /// the measurement are on the internal <c>VisClusterStop</c>). Each
    /// portal's own row is unchanged by that, but the shortened vectors are
    /// what later portals prune with, so they prune chains the exact flow
    /// keeps: the PVS is a SUBSET of the exact one, whatever N is. Walks
    /// shorter than N are exact, so a larger N is slower and loses less;
    /// zero stops everywhere. On 2fort, against the exact flow's 146.3M
    /// chains: N = 1,000 walks 71.1M chains (about half the CPU) and loses
    /// 1.7 % of the visible cluster pairs, 3.1 % after the symmetric pass;
    /// N = 5,000 walks 91.6M and loses 0.7 % (1.3 %); zero walks 61.5M and
    /// loses 5.8 % (10.6 %). A lost pair is geometry the engine culls while
    /// it is in view, so this is for iterating on a layout, not for a
    /// release.
    /// </para>
    /// <para>
    /// Not a compliance quirk: the catalogue lists where this port's DEFAULT
    /// output differs from stock's, switchable between the two, and this is
    /// neither -- an opt-in approximation under either policy, like stock's own
    /// <see cref="Fast"/>, which it does nothing under (<c>-fast</c> skips the
    /// flow altogether). Every host is told through
    /// <see cref="Vis.Vvis.OptionWarnings"/>, which <c>ssmap</c> prints.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int? FastFlowSteps
    {
        get => _fastFlowSteps;
        init
        {
            if (value is int steps)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(steps, nameof(FastFlowSteps));
            }

            _fastFlowSteps = value;
        }
    }

    /// <summary>
    /// Which implementation of the portal flow's separator clip to run:
    /// <see cref="VisSeparatorPath.Auto"/> (the default) picks per CPU.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a stock option, and not an option of the OUTPUT: every value
    /// writes the same bytes, which the equivalence facts hold on random
    /// portal sets, the vvis fixtures, the sandbox map and 2fort. It chooses
    /// only how fast the flow runs, and which is faster depends on the CPU:
    /// the <see cref="VisSeparatorPath.Vector512"/> path was 4.6 % less wall
    /// time on 2fort on a Zen 5 and about 10 % more on an Ice Lake-class Xeon
    /// (the measurements and <see cref="VisSeparatorPath.Auto"/>'s rule are on
    /// <see cref="VisSeparatorPath"/> and <see cref="VisSeparatorPaths.Resolve"/>).
    /// </para>
    /// <para>
    /// Because it cannot change a byte it is left out of the vvis stage
    /// cache's key: a map compiled on one path is served to a compile that
    /// asked for the other. <c>ssmap</c> spells it <c>-separator
    /// auto|256|512</c> and lets the per-machine config set its default.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined path.</exception>
    public VisSeparatorPath SeparatorPath
    {
        get => _separatorPath;
        init
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(SeparatorPath), value, "not a separator path");
            }

            _separatorPath = value;
        }
    }

    private readonly VisSeparatorPath _separatorPath;

    /// <summary>
    /// Whether to reproduce the stock tools' defects or do the right thing.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="CompliancePolicy.Correct"/>. Note that this is
    /// not the only option on this record whose default is NOT stock's
    /// behaviour: <see cref="Tighten"/> also defaults on, because at one
    /// thread the tightened walk reproduces stock bit for bit and everywhere
    /// else it removes stock's schedule-dependent nondeterminism. Everything
    /// else here defaults to what stock does because
    /// stock's behaviour at those sites is a defect. Byte-exact comparisons
    /// against stock output must set <see cref="ComplianceOptions.Stock"/>.
    /// </remarks>
    public ComplianceOptions Compliance { get; init; } = ComplianceOptions.Correct;

    /// <summary>
    /// The stock-default options: full flow, quiet, sorted, tightened (see
    /// <see cref="Tighten"/>).
    /// </summary>
    public static VvisOptions Default { get; } = new();

    /// <summary>The flood-fill approximation, as <c>-fast</c>.</summary>
    public static VvisOptions FastDefault { get; } = new() { Fast = true };

    /// <summary>
    /// The conservative walk: prune with <c>portalflood</c> only, never with a
    /// neighbour's <c>portalvis</c>. The pre-promotion default, kept for the
    /// comparisons that want the superset arm (<c>-loose</c>).
    /// </summary>
    public static VvisOptions Untightened { get; } = new() { Tighten = false };
}
