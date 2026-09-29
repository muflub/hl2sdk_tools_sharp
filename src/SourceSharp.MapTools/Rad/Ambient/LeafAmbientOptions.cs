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
    /// The segments a leaf-ambient batch closes at: a quarter of the other
    /// stages' <see cref="TestLineStage.DefaultBatchSegments"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A worker's batch storage is this bound plus the largest leaf's
    /// segments, because a batch closes only between leaves and one more leaf
    /// may join a batch just short of the bound
    /// (<see cref="LeafAmbientBuilder.BatchSegmentBound"/>). On a real map the
    /// largest leaf alone is most of that: on 2fort it is 128 samples times
    /// 1,188 baked lights, 152,064 segments. With the stages' bound of 65,536
    /// every worker reserved 217,599 segments, 6.1 MB of rays, which at 32
    /// workers was 195 MB of large-object heap for this one stage. At 16,384
    /// the reservation is 168,447 segments, 23 % less, and nothing else
    /// changes: the large leaves fill a batch on their own either way, and
    /// the small ones still go out 16,384 segments or 256 leaves at a time.
    /// </para>
    /// <para>
    /// No answer depends on it: every segment is traced on its own, and each
    /// leaf resolves from its own segments.
    /// </para>
    /// </remarks>
    internal const int DefaultBatchSegments = 1 << 14;

    /// <summary>
    /// How many surface-light segments a worker's batch of leaves closes at
    /// when they are traced through the seam; <see cref="DefaultBatchSegments"/>
    /// unless a fact asks for another (to trace each leaf alone, say). No
    /// answer depends on it.
    /// </summary>
    internal int BatchSegments { get; init; } = DefaultBatchSegments;

    /// <summary>
    /// Where the stage's per-worker scratch arrays are rented from: the
    /// compile's pool, or a fact's that counts what was rented against what
    /// came back and hands out arrays full of junk. Null makes the stage a
    /// pool of its own, dropped when it ends.
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