//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.ExceptionServices;

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
/// largest batch the worker has traced and die with the stage, so nothing
/// outlives the compile.
/// </para>
/// </remarks>
public sealed class TestLineBatch
{
    private readonly IRayTracer _tracer;
    private readonly List<RayTraceOptions> _groups = [];
    private readonly List<(int RayStart, int RayCount, int WordStart)> _spans = [];
    private Ray[] _rays = new Ray[64];
    private int[] _group = new int[64];
    private float[] _payload = new float[64];
    private bool[] _blocked = new bool[64];
    private Ray[] _gather = [];
    private int[] _gatherIndex = [];
    private ulong[] _bits = [];
    private int _count;
    private State _state;

    /// <summary>Makes an empty batch over a tracer.</summary>
    /// <param name="tracer">The tracer every trace asks.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tracer"/> is null.</exception>
    public TestLineBatch(IRayTracer tracer)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        _tracer = tracer;
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
    public int Add(in Ray ray, RayTraceOptions options, float payload = 0.0f)
    {
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

        if (_count == _rays.Length)
        {
            int size = _rays.Length * 2;
            Array.Resize(ref _rays, size);
            Array.Resize(ref _group, size);
            Array.Resize(ref _payload, size);
            Array.Resize(ref _blocked, size);
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
    public void BeginTrace(List<Task> pending, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pending);
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
        if (_gather.Length < _count)
        {
            _gather = new Ray[_rays.Length];
            _gatherIndex = new int[_rays.Length];
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
            pending.Add(task.AsTask());
        }
    }

    private bool Bit(int i) => (_bits[i >> 6] & (1UL << (i & 63))) != 0;

    private void EnsureBits(int words)
    {
        if (_bits.Length < words)
        {
            _bits = new ulong[Math.Max(words, (_rays.Length + 63) >> 6) + _groups.Count];
        }
    }
}
