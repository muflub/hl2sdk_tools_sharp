//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;

namespace SourceSharp.MapTools.Vis;

/// <summary>
/// Everything one <see cref="Vvis.ComputeAsync"/> call needs beyond the map
/// itself: what was asked for, how much of the machine to use, and where
/// progress goes.
/// </summary>
/// <remarks>
/// <para>
/// This is the <c>ctx</c> of the core seam's
/// <c>Vvis.ComputeAsync(bsp, portals, ctx, ct)</c>, narrowed to what vvis
/// actually has: no content filesystem, because vvis reads no materials and no
/// models, and its only inputs are a <c>.bsp</c> and a <c>.prt</c> the caller
/// has already loaded. A stage that cannot reach a disk cannot be made to read
/// one by a host holding it wrong.
/// </para>
/// <para>
/// A record, immutable, with stock's defaults -- so a host states its intent
/// instead of mutating a context between stages.
/// </para>
/// </remarks>
public sealed record VisContext
{
    /// <summary>What vvis was asked to do.</summary>
    public VvisOptions Options { get; init; } = VvisOptions.Default;

    /// <summary>How much of the machine to use.</summary>
    /// <remarks>
    /// Stock's <c>-threads</c>. It does not live in
    /// <see cref="VvisOptions"/> because it is not a property of the compile: it
    /// is a property of the host, shared with every other stage, and the
    /// output must not depend on it (see <see cref="VvisOptions.NoSort"/>).
    /// </remarks>
    public CompileParallelism Parallelism { get; init; } = CompileParallelism.Default;

    /// <summary>Where per-stage progress goes, or null.</summary>
    public IProgress<CompileProgress>? Progress { get; init; }

    /// <summary>
    /// For the facts: runs on a <c>-tighten</c> worker that has just claimed a
    /// portal's run, before it flows it, with the portal's rank.
    /// </summary>
    /// <remarks>
    /// Null in every real compile. A fact that blocks here holds a claimed
    /// and unsettled unit, so the flow provably cannot finish while it waits,
    /// which is what makes "another job ran beside the flow" something a fact
    /// can tell from "another job ran after it".
    /// </remarks>
    internal Action<int>? TighteningClaimProbe { get; init; }

    /// <summary>
    /// For the facts: runs on a <c>-tighten</c> worker once every piece of a
    /// portal's run has finished and the run has been settled, with the
    /// portal's rank and whether the run read a neighbour that had not
    /// finished (and so waits to be judged rather than being done).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null in every real compile. It is the other half of
    /// <see cref="TighteningClaimProbe"/>: a claim says a run is ABOUT to
    /// flow, a settlement says it HAS. With both, a fact can dictate the
    /// schedule instead of hoping for one -- let one run at a time through
    /// (take a baton at the claim, give it back at the settlement), and hold
    /// one portal's claim until every other portal has settled -- so every
    /// read of the held portal is a read of an unfinished neighbour, on every
    /// run of the fact.
    /// </para>
    /// <para>
    /// The flag is what makes "the run speculated" a fact's premise rather
    /// than an inference from the work counters. The counters are a poor
    /// witness: a speculative read that prunes nothing the exact read would
    /// not leaves them unchanged, and a frame split off to an idle worker
    /// moves them by a chain whether anything speculated or not.
    /// </para>
    /// </remarks>
    internal Action<int, bool>? TighteningSettleProbe { get; init; }

    /// <summary>
    /// For the facts and the measurement: what a <c>-fastflow</c> portal
    /// publishes for the portals ranked above it. Only the default,
    /// <see cref="VisFastFlowFilter.Truncated"/>, is reachable from
    /// <see cref="VvisOptions.FastFlowSteps"/>; the other arms are the ones it was
    /// chosen against, kept so the containments between them stay pinned.
    /// </summary>
    internal VisFastFlowFilter FastFlowFilter { get; init; } = VisFastFlowFilter.Truncated;

    /// <summary>
    /// Which bit-vector implementation the inner loop runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="BitVectorPath.Auto"/> takes the widest the machine has. The
    /// knob is here so a fact can run the same map through the scalar path and
    /// the vector one and require the two lumps to be identical -- which turns
    /// "the paths are proven bit-equal on seeded input" into "they are equal on
    /// a whole real compile", and would catch a vector path that is only
    /// correct for the shapes the seeded test happened to generate.
    /// </para>
    /// <para>
    /// It is NOT a performance option for hosts to tune, and there is nothing
    /// to tune: the widest path available is always the one to use.
    /// </para>
    /// </remarks>
    public BitVectorPath Path { get; init; } = BitVectorPath.Auto;

    /// <summary>Stock's defaults, on every core.</summary>
    public static VisContext Default { get; } = new();
}
