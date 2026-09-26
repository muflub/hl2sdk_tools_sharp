//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Parallel;

/// <summary>
/// How much of the machine a compile may use, and whose threads it uses.
/// </summary>
/// <remarks>
/// <para>
/// The default is dedicated threads, one per logical processor, and NOT the
/// host's thread pool. A map compile saturates every core it is given for
/// minutes at a time; parked on an ASP.NET service's pool that is
/// indistinguishable from an outage. The library is a guest in someone else's
/// process and behaves like one.
/// </para>
/// <para>
/// There is deliberately no cap of the kind stock has. sets
/// <c>MAX_TOOL_THREADS</c> to 16 and <c>ThreadSetDefault</c> drops to ONE
/// thread when the CPU count exceeds 32 -- so on a 32-thread box the stock
/// tools are at their cliff edge, and on anything larger they fall off it.
/// </para>
/// </remarks>
public sealed record CompileParallelism
{
    /// <summary>
    /// How many work items may run at once. Defaults to
    /// <see cref="Environment.ProcessorCount"/>.
    /// </summary>
    /// <remarks>
    /// A value of 1 is a first-class mode, not a degenerate one: every parallel
    /// stage must produce byte-identical output at degree 1 and degree N, and a
    /// fact runs both in the same test. That is a race detector needing no
    /// reference output.
    /// </remarks>
    public int MaxDegree { get; init; } = Environment.ProcessorCount;

    /// <summary>
    /// A scheduler to borrow instead of creating dedicated threads.
    /// </summary>
    /// <remarks>
    /// For a host that already owns a pool sized for this work and would rather
    /// the compile used it. Null, the default, means the compile runs on
    /// threads it creates and owns.
    /// </remarks>
    public TaskScheduler? Scheduler { get; init; }

    /// <summary>
    /// A thread pool every stage shares, or null for each queue to create its own.
    /// </summary>
    /// <remarks>
    /// <see cref="Compile.MapCompiler"/> sets one for the whole chain, so that
    /// <see cref="MaxDegree"/> is a ceiling for the run and overlapping stages
    /// share cores chunk by chunk (<see cref="CompilePool"/>). When set, it wins
    /// over <see cref="Scheduler"/> for <see cref="WorkQueue"/> runs; a queue's
    /// degree is capped at the pool's.
    /// </remarks>
    public CompilePool? Pool { get; init; }

    /// <summary>
    /// How often a long-running inner loop checks for cancellation, in
    /// iterations.
    /// </summary>
    /// <remarks>
    /// Checking between work items is not enough: one vvis portal's
    /// <c>RecursiveLeafFlow</c>, a KD-tree build, or one huge face's
    /// supersampling can each run for a long time on their own. The check is a
    /// volatile read, and this interval is chosen so that it does not show up
    /// in the benchmarks.
    /// </remarks>
    public int CancellationPollInterval { get; init; } = 4096;

    /// <summary>The defaults: every processor, own threads.</summary>
    public static CompileParallelism Default { get; } = new();

    /// <summary>
    /// One work item at a time, for the determinism half of a parallel fact.
    /// </summary>
    public static CompileParallelism Serial { get; } = new() { MaxDegree = 1 };
}
