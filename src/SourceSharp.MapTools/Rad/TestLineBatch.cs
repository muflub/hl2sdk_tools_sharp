//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.ExceptionServices;

using SourceSharp.MapTools.Rad.Bounce;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rad;

/// <summary>
/// Stock's <c>TestLine</c> calls, gathered so that a worker's segments reach
/// the tracer as a few batches instead of one call each.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS EXISTS. The prop and leaf-ambient samplers ask one segment at a
/// time: a static prop vertex asks up to forty sky directions per sky light,
/// one segment per point light, and so on. Asked of <see cref="KdRayTracer"/>
/// directly that is free; asked of a GPU through <see cref="IRayTracer"/>,
/// every call is a round trip. So a worker ADDS its segments here, each with
/// the options it would have traced with, starts the trace once
/// (<see cref="BeginTrace"/>), and once the tracer has answered
/// (<see cref="EndTrace"/>) reads every answer back by the index
/// <see cref="Add"/> returned.
/// </para>
/// <para>
/// ONE TRACER CALL PER DISTINCT OPTIONS. Segments with the same
/// <see cref="RayTraceOptions"/> go out in one
/// <see cref="IRayTracer.TraceVisibilityAsync"/>, in the order they were
/// added; a prop that skips its own id and traces both plain and sky segments
/// makes two calls however many vertices it has. The options always carry
/// <see cref="RayTraceOptions.IsolatedRays"/> (<see cref="RayTraceOptions.TestLine"/>
/// sets it), so an answer does not depend on which other segments shared its
/// batch, and the batch answers exactly what the one-segment call answered.
/// </para>
/// <para>
/// NEVER WAITED ON HERE. A GPU tracer answers asynchronously, and blocking a
/// compile worker on it is forbidden (it takes a dedicated thread out of
/// circulation). <see cref="BeginTrace"/> hands back the batches still in
/// flight and the caller -- <see cref="TestLineStage"/> -- parks the worker,
/// awaits them outside the workers, and resumes. <see cref="Trace"/> is the
/// synchronous convenience for a tracer that answers inside the call, the
/// CPU one, and refuses to wait for any other.
/// </para>
/// <para>
/// ONE PER WORKER, never shared: it is scratch. Its arrays grow to the
/// largest batch the worker has traced and are handed back when the batch is
/// disposed -- <see cref="TestLineStage"/> disposes every worker's batch when
/// the stage ends, however it ends -- so nothing outlives the compile.
/// </para>
/// <para>
/// POOLED, BECAUSE THE GROWTH WAS THE ALLOCATION. A leaf-ambient batch holds
/// every sample of up to 256 leaves times every light baked into the cubes:
/// on a map like ctf_2fort that is over a hundred thousand segments, so
/// doubling from 64 put a dozen arrays per worker per stage on the
/// large-object heap, and every large-object allocation past the budget is a
/// gen-2 collection. A profile of vrad on that map charged 74 MB of its
/// allocations to this growth in the leaf-ambient stage alone. The arrays now
/// come from an <see cref="IScratchArrayPool"/> (the process's
/// <see cref="System.Buffers.ArrayPool{T}.Shared"/> in a real compile): a
/// grown-out array goes back as soon as it is copied, and the next stage and
/// the next compile in a long-lived service rent the same arrays again. The
/// shared pool trims what sits unused under memory pressure, so nothing
/// accumulates across compiles.
/// </para>
/// <para>
/// A rented array may hold an earlier renter's data past what this batch has
/// written. Nothing here reads an element it did not write in the current
/// batch: rays, kinds and payloads are written by <see cref="Add"/> below
/// <see cref="Count"/>, the blocked flags by <see cref="EndTrace"/> for every
/// segment, the gather arrays before the calls that read them, and every
/// tracer clears each word of hit bits it is handed before it sets any
/// (<see cref="IRayTracer.TraceVisibilityAsync"/> writes every bit).
/// </para>
/// <para>
/// Storage that a tracer call may still be reading is never handed back: a
/// batch disposed while one of its calls has not completed (possible only
/// after <see cref="Trace"/> refused an asynchronous tracer) abandons its
/// arrays to the collector instead, because the pool would give them to the
/// next renter while the device still read or wrote them.
/// </para>
/// </remarks>
public sealed class TestLineBatch : IDisposable
{
    /// <summary>The capacity of the first rental, in segments.</summary>
    private const int InitialCapacity = 64;

    private readonly IRayTracer _tracer;
    private readonly IScratchArrayPool _pool;
    private readonly List<RayTraceOptions> _groups = [];
    private readonly List<(int RayStart, int RayCount, int WordStart)> _spans = [];
    private readonly List<Task> _inFlight = [];
    private Ray[] _rays = [];
    private int[] _group = [];
    private float[] _payload = [];
    private bool[] _blocked = [];
    private int _capacity;
    private Ray[] _gather = [];
    private int[] _gatherIndex = [];
    private int _gatherCapacity;
    private ulong[] _bits = [];
    private int _bitsCapacity;
    private int _count;
    private State _state;
    private bool _disposed;

    /// <summary>Makes an empty batch over a tracer, its storage rented from the shared array pool.</summary>
    /// <param name="tracer">The tracer every trace asks.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tracer"/> is null.</exception>
    public TestLineBatch(IRayTracer tracer)
        : this(tracer, new SharedScratchArrayPool())
    {
    }

    /// <summary>Makes an empty batch over a tracer, its storage rented from <paramref name="pool"/>.</summary>
    /// <param name="tracer">The tracer every trace asks.</param>
    /// <param name="pool">Where the batch's arrays come from and go back to.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// Nothing is rented until the first segment is added, so a worker that
    /// never plans a segment costs the pool nothing.
    /// </remarks>
    internal TestLineBatch(IRayTracer tracer, IScratchArrayPool pool)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(pool);
        _tracer = tracer;
        _pool = pool;
    }

    private enum State
    {
        Adding,
        Tracing,
        Traced,
    }

    /// <summary>How many segments have been added since the last <see cref="Clear"/>.</summary>
    public int Count => _count;

    /// <summary>Adds a segment.</summary>
    /// <param name="ray">The segment as a ray, from <see cref="Ray.Segment"/>.</param>
    /// <param name="options">What it is traced with, from <see cref="RayTraceOptions.TestLine"/>.</param>
    /// <param name="payload">A number the caller wants back with the answer, by the same index.</param>
    /// <returns>The segment's index, for <see cref="IsBlocked"/> and <see cref="Payload"/>.</returns>
    /// <exception cref="InvalidOperationException">The batch was traced and not cleared.</exception>
    /// <exception cref="ObjectDisposedException">The batch was disposed.</exception>
    public int Add(in Ray ray, RayTraceOptions options, float payload = 0.0f)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != State.Adding)
        {
            throw new InvalidOperationException("the batch has been traced; clear it before adding more segments");
        }

        int group = _groups.IndexOf(options);
        if (group < 0)
        {
            group = _groups.Count;
            _groups.Add(options);
        }

        if (_count == _capacity)
        {
            Grow();
        }

        _rays[_count] = ray;
        _group[_count] = group;
        _payload[_count] = payload;
        return _count++;
    }

    /// <summary>
    /// Starts tracing every segment added: one tracer call per distinct
    /// options.
    /// </summary>
    /// <param name="cancellationToken">Cancels the trace.</param>
    /// <param name="pending">
    /// Receives each call the tracer has not finished inside the call. The
    /// batch's storage belongs to those calls until every one has completed;
    /// only then may <see cref="EndTrace"/> or <see cref="Clear"/> be called.
    /// </param>
    /// <exception cref="InvalidOperationException">The batch was already traced.</exception>
    /// <exception cref="NotSupportedException">
    /// The tracer does not honour a segment's options. Checked for every
    /// kind of segment before any call is made, so nothing is in flight.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The batch was disposed.</exception>
    public void BeginTrace(List<Task> pending, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != State.Adding)
        {
            throw new InvalidOperationException("the batch has already been traced");
        }

        foreach (RayTraceOptions options in _groups)
        {
            if (!_tracer.Supports(options))
            {
                throw new NotSupportedException(
                    $"{_tracer.TracerIdentity} cannot trace segments with {options}");
            }
        }

        _state = State.Tracing;
        _spans.Clear();
        _inFlight.Clear();
        if (_count == 0)
        {
            return;
        }

        if (_groups.Count == 1)
        {
            // The common case -- one kind of segment -- traces the rays where
            // they are.
            _spans.Add((0, _count, 0));
            EnsureBits((_count + 63) >> 6);
            Start(_rays.AsMemory(0, _count), 0, _groups[0], pending, cancellationToken);
            return;
        }

        // Several kinds: each kind's rays contiguous in the gather array, in
        // the order they were added, and its bits in words of its own so the
        // calls in flight never share a word.
        if (_gatherCapacity < _count)
        {
            // Nothing in the old gather arrays is kept: every element below
            // _count is written by the loop below before a call reads it.
            ReturnGather();
            _gather = _pool.Rent<Ray>(_capacity);
            _gatherIndex = _pool.Rent<int>(_capacity);
            _gatherCapacity = _capacity;
        }

        int at = 0;
        int words = 0;
        for (int g = 0; g < _groups.Count; g++)
        {
            int start = at;
            for (int i = 0; i < _count; i++)
            {
                if (_group[i] == g)
                {
                    _gather[at] = _rays[i];
                    _gatherIndex[at++] = i;
                }
            }

            _spans.Add((start, at - start, words));
            words += (at - start + 63) >> 6;
        }

        EnsureBits(words);
        for (int g = 0; g < _groups.Count; g++)
        {
            (int start, int count, int word) = _spans[g];
            Start(_gather.AsMemory(start, count), word, _groups[g], pending, cancellationToken);
        }
    }

    /// <summary>
    /// Reads the answers of a trace whose calls have all completed
    /// successfully.
    /// </summary>
    /// <exception cref="InvalidOperationException">No trace was begun, or it was already ended.</exception>
    public void EndTrace()
    {
        if (_state != State.Tracing)
        {
            throw new InvalidOperationException("no trace of this batch is waiting to be read");
        }

        _state = State.Traced;
        if (_count == 0)
        {
            return;
        }

        if (_groups.Count == 1)
        {
            for (int i = 0; i < _count; i++)
            {
                _blocked[i] = Bit(i);
            }

            return;
        }

        foreach ((int start, int count, int word) in _spans)
        {
            for (int k = 0; k < count; k++)
            {
                _blocked[_gatherIndex[start + k]] = Bit((word << 6) + k);
            }
        }
    }

    /// <summary>
    /// <see cref="BeginTrace"/> and <see cref="EndTrace"/> for a tracer that
    /// answers inside the call.
    /// </summary>
    /// <param name="cancellationToken">Cancels the trace.</param>
    /// <exception cref="InvalidOperationException">
    /// The batch was already traced, or the tracer answers asynchronously: a
    /// GPU batch is never waited for on the calling thread, so it must be
    /// traced through <see cref="TestLineStage"/>, which parks the worker
    /// instead. The batch's storage then belongs to the call in flight and
    /// the batch must not be used again.
    /// </exception>
    /// <exception cref="NotSupportedException">The tracer does not honour a segment's options.</exception>
    public void Trace(CancellationToken cancellationToken)
    {
        List<Task> pending = [];
        BeginTrace(pending, cancellationToken);
        foreach (Task task in pending)
        {
            if (!task.IsCompleted)
            {
                throw new InvalidOperationException(
                    $"{_tracer.TracerIdentity} answers asynchronously; trace this batch through the staged driver");
            }

            RethrowIfFailed(task, cancellationToken);
        }

        EndTrace();
    }

    /// <summary>Whether a traced segment is blocked short of its end.</summary>
    /// <param name="index">What <see cref="Add"/> returned.</param>
    /// <returns>True when blocked.</returns>
    /// <exception cref="InvalidOperationException">The batch's trace has not been read.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a segment of this batch.</exception>
    public bool IsBlocked(int index)
    {
        if (_state != State.Traced)
        {
            throw new InvalidOperationException("the batch has not been traced");
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)_count, nameof(index));
        return _blocked[index];
    }

    /// <summary>The number <see cref="Add"/> was given with a segment.</summary>
    /// <param name="index">What <see cref="Add"/> returned.</param>
    /// <returns>The payload.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a segment of this batch.</exception>
    public float Payload(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)_count, nameof(index));
        return _payload[index];
    }

    /// <summary>Empties the batch for the next work item, keeping its storage.</summary>
    public void Clear()
    {
        _count = 0;
        _state = State.Adding;
        _groups.Clear();
        _inFlight.Clear();
    }

    /// <summary>
    /// Hands every array back to the pool, once; the batch cannot be used
    /// again. Safe to call more than once.
    /// </summary>
    /// <remarks>
    /// If a tracer call this batch started has not completed, the arrays are
    /// NOT returned: the call may still be reading the rays or writing the
    /// bits, and the pool would hand them to the next renter meanwhile. They
    /// are dropped for the collector instead, which costs an allocation later
    /// and can corrupt nothing. <see cref="TestLineStage"/> awaits every call
    /// before it disposes, so in a stage this never happens.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        bool settled = true;
        foreach (Task task in _inFlight)
        {
            settled &= task.IsCompleted;
        }

        _inFlight.Clear();
        if (settled)
        {
            ReturnSegments();
            ReturnGather();
            ReturnBits();
        }

        _rays = [];
        _group = [];
        _payload = [];
        _blocked = [];
        _capacity = 0;
        _gather = [];
        _gatherIndex = [];
        _gatherCapacity = 0;
        _bits = [];
        _bitsCapacity = 0;
        _count = 0;
    }

    /// <summary>A completed task's failure, rethrown as itself.</summary>
    /// <param name="task">A completed task.</param>
    /// <param name="cancellationToken">The token a cancelled task is reported with.</param>
    internal static void RethrowIfFailed(Task task, CancellationToken cancellationToken)
    {
        if (task.IsCanceled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (task.Exception is { } failure)
        {
            ExceptionDispatchInfo.Throw(failure.InnerExceptions.Count == 1 ? failure.InnerException! : failure);
        }
    }

    private void Start(
        ReadOnlyMemory<Ray> rays, int word, RayTraceOptions options, List<Task> pending, CancellationToken cancellationToken)
    {
        ValueTask task = _tracer.TraceVisibilityAsync(
            rays, _bits.AsMemory(word, (rays.Length + 63) >> 6), options, cancellationToken);
        if (!task.IsCompletedSuccessfully)
        {
            Task call = task.AsTask();
            pending.Add(call);
            _inFlight.Add(call);
        }
    }

    /// <summary>Doubles the segment storage.</summary>
    private void Grow() => GrowTo(_capacity == 0 ? InitialCapacity : _capacity * 2);

    /// <summary>
    /// Makes room for at least <paramref name="segments"/> segments in all,
    /// in one step, keeping those already added.
    /// </summary>
    /// <param name="segments">The most segments the caller will have added before the next <see cref="Clear"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="segments"/> is negative.</exception>
    /// <exception cref="ObjectDisposedException">The batch was disposed.</exception>
    /// <exception cref="InvalidOperationException">The batch has been traced and not cleared.</exception>
    /// <remarks>
    /// For a worker that can bound its largest batch up front, as leaf
    /// ambient can: doubling from 64 rents a dozen arrays on the way to a
    /// hundred thousand segments, about twice the final storage, and the
    /// intermediate sizes are rarely wanted again, so the pool seldom has
    /// them. One rental of the bound is the storage the worker ends up with
    /// anyway. A bound at or below the current capacity does nothing.
    /// </remarks>
    public void Reserve(int segments)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(segments);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != State.Adding)
        {
            throw new InvalidOperationException("the batch has been traced; clear it before reserving");
        }

        if (segments > _capacity)
        {
            GrowTo(segments);
        }
    }

    /// <summary>
    /// Grows the segment storage to <paramref name="size"/>: new arrays
    /// rented, the segments added so far copied across, the old arrays
    /// returned.
    /// </summary>
    /// <remarks>
    /// Only the adding state grows, so the blocked flags carry nothing worth
    /// copying: <see cref="EndTrace"/> writes one for every segment. All four
    /// new arrays are rented before anything is swapped, so a rental that
    /// throws leaves the batch as it was and gives back what it had rented.
    /// </remarks>
    private void GrowTo(int size)
    {
        Ray[]? rays = null;
        int[]? group = null;
        float[]? payload = null;
        bool[] blocked;
        try
        {
            rays = _pool.Rent<Ray>(size);
            group = _pool.Rent<int>(size);
            payload = _pool.Rent<float>(size);
            blocked = _pool.Rent<bool>(size);
        }
        catch
        {
            ReturnIfRented(rays);
            ReturnIfRented(group);
            ReturnIfRented(payload);
            throw;
        }

        _rays.AsSpan(0, _count).CopyTo(rays);
        _group.AsSpan(0, _count).CopyTo(group);
        _payload.AsSpan(0, _count).CopyTo(payload);
        ReturnSegments();
        _rays = rays;
        _group = group;
        _payload = payload;
        _blocked = blocked;
        _capacity = size;
    }

    private void ReturnIfRented<T>(T[]? array)
    {
        if (array is not null)
        {
            _pool.Return(array);
        }
    }

    /// <summary>The four segment arrays back to the pool, if any were rented.</summary>
    private void ReturnSegments()
    {
        if (_capacity == 0)
        {
            return;
        }

        _pool.Return(_rays);
        _pool.Return(_group);
        _pool.Return(_payload);
        _pool.Return(_blocked);
    }

    /// <summary>The gather arrays back to the pool, if any were rented, and forgotten.</summary>
    private void ReturnGather()
    {
        if (_gatherCapacity == 0)
        {
            return;
        }

        _pool.Return(_gather);
        _pool.Return(_gatherIndex);
        _gather = [];
        _gatherIndex = [];
        _gatherCapacity = 0;
    }

    /// <summary>The hit bits back to the pool, if any were rented, and forgotten.</summary>
    private void ReturnBits()
    {
        if (_bitsCapacity == 0)
        {
            return;
        }

        _pool.Return(_bits);
        _bits = [];
        _bitsCapacity = 0;
    }

    private bool Bit(int i) => (_bits[i >> 6] & (1UL << (i & 63))) != 0;

    private void EnsureBits(int words)
    {
        if (_bitsCapacity < words)
        {
            // The old words go back first: nothing is in flight between the
            // adding and tracing states, and a tracer clears every word it is
            // handed before it sets any, so their contents do not matter.
            int size = Math.Max(words, (_capacity + 63) >> 6) + _groups.Count;
            ReturnBits();
            _bits = _pool.Rent<ulong>(size);
            _bitsCapacity = size;
        }
    }
}
