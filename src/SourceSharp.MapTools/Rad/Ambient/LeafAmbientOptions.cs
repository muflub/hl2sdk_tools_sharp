//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Bounce;

namespace SourceSharp.MapTools.Rad.Ambient;

/// <summary>
/// What a leaf-ambient pass is allowed to do, and what it must refuse.
/// </summary>
/// <remarks>
/// <para>
/// The two <c>Require</c> members are not configuration in the usual sense.
/// They exist because this lane's port is INCOMPLETE in two named ways --
/// displacement ray clipping and surface-light visibility -- and both gaps
/// fail SILENTLY: the output is a plausible ambient cube that is merely wrong.
/// A default of "refuse" turns each into a loud failure on the maps it affects,
/// and leaves a deliberate opt-out for the gate maps where the gap provably
/// cannot bite.
/// </para>
/// <para>
/// This project has been bitten repeatedly by checks that could not fail. A
/// missing input that reads as a pass is the same failure wearing a different
/// hat, so the refusal is the default rather than a warning.
/// </para>
/// </remarks>
public sealed record LeafAmbientOptions
{
    /// <summary>Which defects this compile reproduces.</summary>
    public ComplianceOptions Compliance { get; init; } = ComplianceOptions.Correct;

    /// <summary>
    /// Whether to take only one sample per leaf, whatever its size
    /// (<c>-fastambient</c>).
    /// </summary>
    /// <remarks>
    /// <c>g_bFastAmbient</c>. It short-circuits the volume heuristic entirely,
    /// so a map compiled with it also never exercises
    /// <see cref="StockQuirk.LeafAmbientSampleCountAxes"/>.
    /// </remarks>
    public bool FastAmbient { get; init; }

    /// <summary>
    /// Refuse a map that has <c>emit_surface</c> lights folded into its cubes
    /// when no <see cref="IAmbientLightVisibility"/> is given.
    /// </summary>
    /// <remarks>
    /// <c>AddEmitSurfaceLights</c> drops a light the sample cannot SEE, using
    /// <c>TestLine</c> against the ray-trace environment. Without a visibility
    /// every flagged light would be added unoccluded, making cubes behind walls
    /// too bright. Set false only when that is what is wanted.
    /// </remarks>
    public bool RequireSurfaceLightVisibility { get; init; } = true;

    /// <summary>How many leaves to compute at once. 1 is serial; 0 is every core.</summary>
    /// <remarks>
    /// SAFE AT ANY VALUE: each leaf's sampler is its own seeded-zero stream, the
    /// scene is read-only, each worker's <see cref="AmbientSampler"/> holds only
    /// buffers every sample overwrites, and each leaf writes only its own slot
    /// of the result. <c>-threads 1</c> against <c>-threads N</c> is byte-exact.
    /// </remarks>
    public int Parallelism { get; init; }

    /// <summary>The compile's shared thread pool, or null for threads of this stage's own.</summary>
    public CompilePool? Pool { get; init; }

    /// <summary>
    /// How many surface-light segments a worker's batch of leaves closes at
    /// when they are traced through the seam; see
    /// <see cref="TestLineStage.DefaultBatchSegments"/>. Internal so the facts
    /// can trace each leaf alone; no answer depends on it.
    /// </summary>
    internal int BatchSegments { get; init; } = TestLineStage.DefaultBatchSegments;

    /// <summary>
    /// Where the stage's per-worker scratch arrays are rented from, or null for
    /// the process's shared array pool. Internal so the facts can count what
    /// was rented against what came back, and hand out arrays full of junk.
    /// </summary>
    /// <remarks>
    /// Null rather than a default instance so that two options that say the
    /// same thing stay equal as records. No answer depends on it.
    /// </remarks>
    internal IScratchArrayPool? ScratchPool { get; init; }

    /// <summary>Everything stock does, serially: what every byte-exact gate selects.</summary>
    public static LeafAmbientOptions StockParity { get; } =
        new() { Compliance = ComplianceOptions.Stock, Parallelism = 1 };
}