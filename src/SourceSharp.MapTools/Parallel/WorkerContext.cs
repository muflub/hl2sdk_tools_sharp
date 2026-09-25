namespace SourceSharp.MapTools.Parallel;

/// <summary>
/// What a work item is told about the worker running it.
/// </summary>
/// <remarks>
/// One of these is created per worker per run and handed to every item that
/// worker claims, so it is also the natural place to hang a worker's scratch —
/// see the scratch overload of
/// <see cref="WorkQueue.RunAsync{TScratch, TResult}"/>.
/// </remarks>
public sealed class WorkerContext
{
    private readonly int _pollInterval;
    private int _pollCounter;

    internal WorkerContext(int workerIndex, int pollInterval, CancellationToken cancellationToken)
    {
        WorkerIndex = workerIndex;
        _pollInterval = pollInterval < 1 ? 1 : pollInterval;
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// Which worker this is, from zero to one less than the degree.
    /// </summary>
    /// <remarks>
    /// Stock passes the same number as <c>iThread</c>
    /// And the tools use it to index
    /// per-thread arrays. It is a WORKER index, not a thread identity: with a
    /// host-supplied <see cref="TaskScheduler"/> the same OS thread may run
    /// several of them over a process's life, so nothing may key thread-local
    /// state on it beyond the run.
    /// </remarks>
    public int WorkerIndex { get; }

    /// <summary>The run's cancellation token.</summary>
    /// <remarks>
    /// Here for the rare item that wants to pass it on to something else.
    /// Inside a loop use <see cref="ShouldStop"/>, which is much cheaper.
    /// </remarks>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// A cheap cancellation check for the inside of a long item.
    /// </summary>
    /// <returns>True when the run is being torn down.</returns>
    /// <remarks>
    /// <para>
    /// Checking between items is not enough. One vvis portal's
    /// <c>RecursiveLeafFlow</c> can run for minutes by itself on a dense map,
    /// and a vrad patch's supersampling can do the same, so a compile whose
    /// cancellation only took effect at an item boundary would look wedged.
    /// </para>
    /// <para>
    /// The real check is only done every
    /// <see cref="CompileParallelism.CancellationPollInterval"/> calls; in
    /// between, this returns the last answer. So the common case is an
    /// increment, a compare and a field read, which is cheap enough to put
    /// inside a loop that runs millions of times. The cost of that is latency:
    /// cancellation is noticed within one polling interval of the loop, not
    /// immediately.
    /// </para>
    /// </remarks>
    public bool ShouldStop()
    {
        if (Stopping)
        {
            return true;
        }

        if (++_pollCounter < _pollInterval)
        {
            return false;
        }

        _pollCounter = 0;
        if (CancellationToken.IsCancellationRequested)
        {
            Stopping = true;
        }

        return Stopping;
    }

    /// <summary>
    /// Throws if the run is being torn down, checked as cheaply as
    /// <see cref="ShouldStop"/>.
    /// </summary>
    /// <exception cref="OperationCanceledException">The run was cancelled.</exception>
    public void ThrowIfShouldStop()
    {
        if (ShouldStop())
        {
            throw new OperationCanceledException(CancellationToken);
        }
    }

    internal bool Stopping { get; set; }
}
