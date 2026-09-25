using SourceSharp.MapTools.Diagnostics;

namespace SourceSharp.MapTools.Parallel;

/// <summary>
/// How one run of <see cref="WorkQueue"/> should be scheduled and reported.
/// </summary>
public sealed record WorkQueueOptions
{
    /// <summary>
    /// The stage name that goes into every <see cref="CompileProgress"/>.
    /// </summary>
    public string Stage { get; init; } = string.Empty;

    /// <summary>Where to report progress, or null for none.</summary>
    /// <remarks>
    /// <para>
    /// Reported from the worker that finished the item, holding NO lock. Stock
    /// calls <c>UpdatePacifier</c> from inside the one global
    /// <c>CRITICAL_SECTION</c> it takes to hand out work
    /// (<c>utils/common/threads.cpp:65</c>), so on stock every worker that
    /// wants its next item waits behind whatever the pacifier is doing —
    /// which, when the output is a pipe or a log file rather than a console, is
    /// a write syscall.
    /// </para>
    /// <para>
    /// The consequence for an implementor: this is called concurrently from
    /// several threads and must be thread safe. It is not called in item order.
    /// </para>
    /// </remarks>
    public IProgress<CompileProgress>? Progress { get; init; }

    /// <summary>
    /// An estimate of how expensive each item is, used to run the expensive
    /// ones first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called once per item during setup, on the calling thread, before any
    /// work starts. The units do not matter, only the order.
    /// </para>
    /// <para>
    /// Work items vary by about a hundredfold in both vvis and vrad — one
    /// portal in the middle of a large open area floods vastly more than one in
    /// a corridor, and one displacement face can carry more luxels than a
    /// hundred world faces. Stock hands items out strictly in index order
    /// (<c>threads.cpp:67</c>), which is roughly cheapest first because of how
    /// maps are authored, so the expensive items are what is left at the end
    /// and they run with most of the machine idle. Sorting longest-first turns
    /// that tail into a head, where there is still work to overlap it with.
    /// </para>
    /// <para>
    /// Ties keep ascending index order, and the sort is stable, so the claim
    /// order is a pure function of the costs. It does not affect results: the
    /// merge order is the item index either way.
    /// </para>
    /// </remarks>
    public Func<int, long>? ItemCost { get; init; }

    /// <summary>
    /// How many items a worker claims at once, or zero to choose per run.
    /// </summary>
    /// <remarks>
    /// Zero, the default, means one item at a time when
    /// <see cref="ItemCost"/> is supplied — claiming in bulk would undo the
    /// longest-first ordering — and a guided chunk otherwise, shrinking towards
    /// one as the queue drains so the run does not end with one worker holding
    /// a big block.
    /// </remarks>
    public int ChunkSize { get; init; }
}
