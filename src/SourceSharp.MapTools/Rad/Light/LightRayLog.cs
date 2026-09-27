//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// The rays one piece of lighting work needs, recorded in the order it asks
/// for them, then answered in that order.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why record and answer later rather than trace in place.</b> Stock traces
/// four rays at a time from the middle of the gather loop (<c>TestLine</c>,
///). This port's tracer is the batch-only
/// <see cref="IRayTracer"/> seam, which wants thousands of rays at once and may
/// be a GPU that answers asynchronously -- and a worker in the middle of a
/// face cannot usefully <c>await</c>. So the gather is split at its rays: the
/// EMIT half computes everything a ray's question depends on, records the ray
/// here and writes what the rest of the arithmetic needs to <see cref="Tape"/>;
/// after the batch is traced the RESOLVE half reads the tape back with the
/// answers and finishes. The gather arithmetic runs once.
/// </para>
/// <para>
/// That is sound only because the gather's control flow never depends on a ray
/// answer before it asks for the next ray, with one exception: the 3D-skybox
/// recursion in <c>TestLine_DoesHitSky</c> runs only
/// when the first rays were not all occluded. Those rays go to a list of their
/// own; with <see cref="DeferRecursion"/> they are asked for in a SECOND
/// stage, only for the tests that need them, exactly as stock traces them;
/// without it they are recorded unconditionally and used conditionally, which
/// gives the same answers for more rays.
/// </para>
/// <para>
/// <b>Packets.</b> The tracer answers four consecutive rays as one packet
/// (<c>Trace4Rays</c>), and a packet's answer for one ray can depend on the
/// other three: which KD cells the packet walks decides which of two hits at
/// the same distance is found first, and a sky test turns on that. So every
/// ITEM of work (<see cref="EndItem"/>) and every skybox camera's rays start
/// a fresh packet, padded with copies of their last ray: what one piece of
/// work is answered cannot depend on what was laid out before it.
/// </para>
/// <para>
/// Two kinds of ray, because stock asks two questions. <c>TestLine</c> wants
/// "is anything in the way" (a visibility bit); <c>TestLine_DoesHitSky</c>
/// wants "is the first thing in the way something other than sky", which is a
/// closest hit and a <see cref="TraceId.Sky"/> test on its surface id.
/// </para>
/// <para>
/// The storage is pooled: a log is reset and reused for every batch, so after
/// the first few batches it allocates nothing.
/// </para>
/// </remarks>
public sealed class LightRayLog
{
    private Ray[] _visibility = new Ray[16];
    private Ray[] _sky = new Ray[16];
    private Ray[] _sky2 = new Ray[4];
    private int _visibilityCount;
    private int _skyCount;
    private int _sky2Count;

    private DeferredSkyTest[] _deferred = new DeferredSkyTest[4];
    private Vec3[] _deferredPoints = new Vec3[8];
    private int _deferredCount;
    private int _deferredPointCount;

    private ReadOnlyMemory<ulong> _visibilityBits;
    private int _visibilityBase;
    private ReadOnlyMemory<HitId> _skyHits;
    private int _skyBase;
    private ReadOnlyMemory<HitId> _sky2Hits;
    private int _visibilityCursor;
    private int _skyCursor;
    private int _sky2Cursor;

    private ulong[] _ownBits = [];
    private HitId[] _ownHits = [];
    private HitId[] _ownHits2 = [];

    /// <summary>True while rays are being recorded; false while answers are replayed.</summary>
    public bool Collecting { get; private set; } = true;

    /// <summary>The visibility rays recorded so far.</summary>
    public int VisibilityCount => _visibilityCount;

    /// <summary>The sky rays recorded so far.</summary>
    public int SkyCount => _skyCount;

    /// <summary>The second-stage (skybox recursion) sky rays recorded so far.</summary>
    internal int Sky2Count => _sky2Count;

    /// <summary>Every ray recorded so far, all kinds.</summary>
    internal int TotalCount => _visibilityCount + _skyCount;

    /// <summary>
    /// Ask for the 3D-skybox recursion rays in a second stage, only for the
    /// tests whose first rays were not all occluded (stock's own condition,
    ///), instead of recording them unconditionally.
    /// </summary>
    internal bool DeferRecursion { get; set; }

    /// <summary>The emit/resolve intermediate values, reset with the rays.</summary>
    internal GatherTape Tape { get; } = new();

    /// <summary>Tests waiting for the second stage.</summary>
    internal ReadOnlySpan<DeferredSkyTest> Deferred => _deferred.AsSpan(0, _deferredCount);

    /// <summary>The start and stop points of the deferred tests.</summary>
    internal ReadOnlySpan<Vec3> DeferredPoints => _deferredPoints.AsSpan(0, _deferredPointCount);

    /// <summary>The recorded visibility rays, in request order.</summary>
    /// <returns>A view of the storage.</returns>
    public ReadOnlySpan<Ray> VisibilityRays() => _visibility.AsSpan(0, _visibilityCount);

    /// <summary>The recorded sky rays, in request order.</summary>
    /// <returns>A view of the storage.</returns>
    public ReadOnlySpan<Ray> SkyRays() => _sky.AsSpan(0, _skyCount);

    /// <summary>The recorded visibility rays, as memory for the tracer.</summary>
    internal ReadOnlyMemory<Ray> VisibilityMemory => _visibility.AsMemory(0, _visibilityCount);

    /// <summary>The recorded sky rays, as memory for the tracer.</summary>
    internal ReadOnlyMemory<Ray> SkyMemory => _sky.AsMemory(0, _skyCount);

    /// <summary>The second-stage sky rays, as memory for the tracer.</summary>
    internal ReadOnlyMemory<Ray> Sky2Memory => _sky2.AsMemory(0, _sky2Count);

    /// <summary>Forgets every ray and the tape, and returns to collecting. Keeps the storage.</summary>
    public void Reset()
    {
        _visibilityCount = 0;
        _skyCount = 0;
        _sky2Count = 0;
        _deferredCount = 0;
        _deferredPointCount = 0;
        _visibilityBits = default;
        _skyHits = default;
        _sky2Hits = default;
        _visibilityCursor = 0;
        _skyCursor = 0;
        _sky2Cursor = 0;
        Tape.Reset();
        Collecting = true;
    }

    /// <summary>
    /// Pads the visibility and first-stage sky rays to whole packets of four,
    /// with copies of the last ray, so the next item starts a packet of its own.
    /// </summary>
    internal void EndItem()
    {
        while ((_visibilityCount & 3) != 0)
        {
            EmitRay(ref _visibility, ref _visibilityCount, _visibility[_visibilityCount - 1]);
        }

        while ((_skyCount & 3) != 0)
        {
            EmitRay(ref _sky, ref _skyCount, _sky[_skyCount - 1]);
        }
    }

    /// <summary>
    /// Pad every four-lane call (one light's <c>TestLine</c>, one sky test's
    /// first rays, one ambient direction) to a packet of its own, as stock's
    /// <c>Trace4Rays</c> call traces it, instead of packing the traced lanes
    /// of consecutive calls together.
    /// </summary>
    internal bool PadCalls { get; init; }

    /// <summary>With <see cref="PadCalls"/>, ends a call's rays: <see cref="EndItem"/>.</summary>
    internal void EndCall()
    {
        if (PadCalls)
        {
            EndItem();
        }
    }

    /// <summary>With <see cref="PadCalls"/>, skips a call's padding: <see cref="SkipItemPadding"/>.</summary>
    internal void SkipCallPadding()
    {
        if (PadCalls)
        {
            SkipItemPadding();
        }
    }

    /// <summary>Skips the answers to <see cref="EndItem"/>'s padding.</summary>
    internal void SkipItemPadding()
    {
        _visibilityCursor = (_visibilityCursor + 3) & ~3;
        _skyCursor = (_skyCursor + 3) & ~3;
    }

    /// <summary>Pads the second-stage rays to a whole packet: one skybox camera's rays are one packet.</summary>
    internal void EndSky2Block()
    {
        while ((_sky2Count & 3) != 0)
        {
            EmitRay(ref _sky2, ref _sky2Count, _sky2[_sky2Count - 1]);
        }
    }

    /// <summary>Skips the answers to <see cref="EndSky2Block"/>'s padding.</summary>
    internal void SkipSky2BlockPadding() => _sky2Cursor = (_sky2Cursor + 3) & ~3;

    /// <summary>
    /// Switches to replay, reading answers from slices of a larger batch.
    /// </summary>
    /// <param name="visibilityBits">
    /// The whole batch's visibility bits, least-significant first.
    /// </param>
    /// <param name="visibilityBase">Where this log's first ray sits in that batch.</param>
    /// <param name="skyHits">The whole batch's sky answers.</param>
    /// <param name="skyBase">Where this log's first sky ray sits in that batch.</param>
    /// <remarks>
    /// A log holding skybox-recursion rays needs their answers too; this
    /// overload has none to give, so replaying such a log fails loudly.
    /// </remarks>
    public void BeginReplay(
        ReadOnlyMemory<ulong> visibilityBits,
        int visibilityBase,
        ReadOnlyMemory<HitId> skyHits,
        int skyBase) =>
        BeginReplay(visibilityBits, visibilityBase, skyHits, skyBase, default);

    /// <summary>Switches to replay, with answers for the skybox-recursion rays too.</summary>
    /// <param name="visibilityBits">The visibility bits, from this log's first ray.</param>
    /// <param name="visibilityBase">Where this log's first ray sits in them.</param>
    /// <param name="skyHits">The first-stage sky answers.</param>
    /// <param name="skyBase">Where this log's first sky ray sits in them.</param>
    /// <param name="sky2Hits">The recursion rays' answers, from this log's first.</param>
    internal void BeginReplay(
        ReadOnlyMemory<ulong> visibilityBits,
        int visibilityBase,
        ReadOnlyMemory<HitId> skyHits,
        int skyBase,
        ReadOnlyMemory<HitId> sky2Hits)
    {
        _visibilityBits = visibilityBits;
        _visibilityBase = visibilityBase;
        _skyHits = skyHits;
        _skyBase = skyBase;
        _sky2Hits = sky2Hits;
        _visibilityCursor = 0;
        _skyCursor = 0;
        _sky2Cursor = 0;
        Tape.Rewind();
        Collecting = false;
    }

    /// <summary>
    /// Answer buffers owned by this log, sized for what it recorded: the
    /// visibility bits and the first-stage sky hits.
    /// </summary>
    /// <returns>Where the tracer writes.</returns>
    internal (Memory<ulong> Bits, Memory<HitId> Hits) FirstStageAnswers()
    {
        int words = (_visibilityCount + 63) >> 6;
        if (_ownBits.Length < words)
        {
            _ownBits = new ulong[Math.Max(words, _ownBits.Length * 2)];
        }

        if (_ownHits.Length < _skyCount)
        {
            _ownHits = new HitId[Math.Max(_skyCount, _ownHits.Length * 2)];
        }

        return (_ownBits.AsMemory(0, words), _ownHits.AsMemory(0, _skyCount));
    }

    /// <summary>The first-stage sky answers, once traced.</summary>
    internal ReadOnlySpan<HitId> FirstStageSkyHits => _ownHits.AsSpan(0, _skyCount);

    /// <summary>Answer buffer for the second-stage sky rays.</summary>
    /// <returns>Where the tracer writes.</returns>
    internal Memory<HitId> SecondStageAnswers()
    {
        if (_ownHits2.Length < _sky2Count)
        {
            _ownHits2 = new HitId[Math.Max(_sky2Count, _ownHits2.Length * 2)];
        }

        return _ownHits2.AsMemory(0, _sky2Count);
    }

    /// <summary>
    /// Traces everything this log recorded into its own answer buffers: the
    /// visibility and sky rays, and -- when recursion is not deferred -- the
    /// skybox rays. Follow with <see cref="BeginResolve"/>.
    /// </summary>
    /// <param name="tracer">The tracer.</param>
    /// <param name="cancellationToken">Cancels the trace.</param>
    /// <returns>Completes when every answer is in.</returns>
    internal async ValueTask TraceOwnAsync(IRayTracer tracer, CancellationToken cancellationToken)
    {
        (Memory<ulong> bits, Memory<HitId> hits) = FirstStageAnswers();
        if (_visibilityCount > 0)
        {
            await tracer.TraceVisibilityAsync(VisibilityMemory, bits, RayTraceOptions.StockExact, cancellationToken)
                .ConfigureAwait(false);
        }

        if (_skyCount > 0)
        {
            await tracer.TraceClosestAsync(SkyMemory, hits, RayTraceOptions.StockExact, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!DeferRecursion && _sky2Count > 0)
        {
            await tracer.TraceClosestAsync(Sky2Memory, SecondStageAnswers(), RayTraceOptions.StockExact, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Switches to resolving from this log's own answer buffers.</summary>
    internal void BeginResolve()
    {
        _visibilityBits = _ownBits;
        _visibilityBase = 0;
        _skyHits = _ownHits;
        _skyBase = 0;
        _sky2Hits = _ownHits2;
        _visibilityCursor = 0;
        _skyCursor = 0;
        _sky2Cursor = 0;
        Tape.Rewind();
        Collecting = false;
    }

    /// <summary>
    /// True once every recorded ray has been read back exactly once -- the
    /// check that the two halves really did ask the same questions.
    /// </summary>
    public bool ReplayComplete =>
        !Collecting && _visibilityCursor == _visibilityCount && _skyCursor == _skyCount
        && _sky2Cursor == _sky2Count && Tape.FullyRead;

    /// <summary>
    /// <c>TestLine</c> for one ray: 1 when nothing lies
    /// between the two points, 0 when something does.
    /// </summary>
    /// <param name="start">Where the ray starts.</param>
    /// <param name="stop">Where it must reach.</param>
    /// <returns>The visible fraction; 1 while collecting.</returns>
    /// <exception cref="InvalidOperationException">
    /// Replay asked for more rays than were recorded.
    /// </exception>
    public float TestLine(Vec3 start, Vec3 stop)
    {
        if (Collecting)
        {
            EmitVisibility(start, stop);
            return 1.0f;
        }

        return ReadVisibility();
    }

    /// <summary>
    /// One ray of <c>TestLine_DoesHitSky</c> before any skybox recursion
    /// 1 when the first thing hit is not sky, else 0.
    /// </summary>
    /// <param name="start">Where the ray starts.</param>
    /// <param name="stop">Its far end.</param>
    /// <returns>The occlusion; 0 while collecting.</returns>
    /// <exception cref="InvalidOperationException">
    /// Replay asked for more rays than were recorded.
    /// </exception>
    public float SkyOcclusion(Vec3 start, Vec3 stop)
    {
        if (Collecting)
        {
            EmitSky(start, stop);
            return 0.0f;
        }

        return ReadSkyOcclusion();
    }

    /// <summary>
    /// Makes room for at least <paramref name="count"/> first-stage sky rays
    /// without growing.
    /// </summary>
    /// <param name="count">How many sky rays the caller is about to record, at most.</param>
    /// <remarks>
    /// For a caller that records one large batch whose size it can bound up
    /// front, such as the radial sky-leaf probe: growing from the default by
    /// doubling allocates about twice the final storage and copies it on the
    /// way, all of it on the large-object heap for a map-sized batch. Rays
    /// already recorded are kept.
    /// </remarks>
    internal void ReserveSky(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > _sky.Length)
        {
            Array.Resize(ref _sky, count);
        }
    }

    /// <summary>The storage the first-stage sky rays have without growing.</summary>
    internal int SkyCapacity => _sky.Length;

    /// <summary>Records a visibility ray.</summary>
    /// <param name="start">The start.</param>
    /// <param name="stop">The end.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EmitVisibility(Vec3 start, Vec3 stop) => EmitRay(ref _visibility, ref _visibilityCount, Make(start, stop));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitRay(ref Ray[] rays, ref int count, Ray ray)
    {
        if (count == rays.Length)
        {
            Array.Resize(ref rays, rays.Length * 2);
        }

        rays[count++] = ray;
    }

    /// <summary>Records a first-stage sky ray.</summary>
    /// <param name="start">The start.</param>
    /// <param name="stop">The end.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EmitSky(Vec3 start, Vec3 stop) => EmitRay(ref _sky, ref _skyCount, Make(start, stop));

    /// <summary>Records a second-stage sky ray.</summary>
    /// <param name="start">The start.</param>
    /// <param name="stop">The end.</param>
    internal void EmitSky2(Vec3 start, Vec3 stop) => EmitRay(ref _sky2, ref _sky2Count, Make(start, stop));

    /// <summary>
    /// Records a sky test whose skybox recursion waits for the second stage.
    /// </summary>
    /// <param name="primaryBase">The index of its first sky ray.</param>
    /// <param name="count">Its real lanes.</param>
    /// <param name="recurseMask">Which lanes recurse.</param>
    /// <param name="start">Four starts.</param>
    /// <param name="stop">Four far ends.</param>
    internal void Defer(int primaryBase, int count, int recurseMask, ReadOnlySpan<Vec3> start, ReadOnlySpan<Vec3> stop)
    {
        if (_deferredCount == _deferred.Length)
        {
            Array.Resize(ref _deferred, _deferred.Length * 2);
        }

        while (_deferredPointCount + (2 * count) > _deferredPoints.Length)
        {
            Array.Resize(ref _deferredPoints, _deferredPoints.Length * 2);
        }

        _deferred[_deferredCount++] = new DeferredSkyTest(primaryBase, count, recurseMask, _deferredPointCount);
        for (int lane = 0; lane < count; lane++)
        {
            _deferredPoints[_deferredPointCount++] = start[lane];
            _deferredPoints[_deferredPointCount++] = stop[lane];
        }
    }

    /// <summary>The next visibility answer: 1 visible, 0 blocked.</summary>
    /// <returns>The visible fraction.</returns>
    /// <exception cref="InvalidOperationException">No ray is left to read.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal float ReadVisibility()
    {
        if (_visibilityCursor >= _visibilityCount)
        {
            throw new InvalidOperationException(
                "the replay asked for a visibility ray the collection never recorded");
        }

        int index = _visibilityBase + _visibilityCursor++;
        bool hit = (_visibilityBits.Span[index >> 6] & (1UL << (index & 63))) != 0;
        return hit ? 0.0f : 1.0f;
    }

    /// <summary>The next first-stage sky answer, as an occlusion.</summary>
    /// <returns>1 when the first thing hit is not sky, else 0.</returns>
    /// <exception cref="InvalidOperationException">No ray is left to read.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal float ReadSkyOcclusion()
    {
        if (_skyCursor >= _skyCount)
        {
            throw new InvalidOperationException(
                "the replay asked for a sky ray the collection never recorded");
        }

        // A hit counts only SHORT of the segment's end
        // (`HitDistance < len`); the tracer reports the nearest triangle
        // along the whole line, including past the end.
        return Occlusion(_skyHits.Span[_skyBase + _skyCursor++]);
    }

    /// <summary>The next second-stage sky answer, as an occlusion.</summary>
    /// <returns>1 when the first thing hit is not sky, else 0.</returns>
    /// <exception cref="InvalidOperationException">No ray is left to read.</exception>
    internal float ReadSky2Occlusion()
    {
        if (_sky2Cursor >= _sky2Count)
        {
            throw new InvalidOperationException(
                "the resolve asked for a skybox ray the second stage never recorded");
        }

        return Occlusion(_sky2Hits.Span[_sky2Cursor++]);
    }

    /// <summary>A sky answer as <c>TestLine_DoesHitSky</c>'s per-lane occlusion.</summary>
    /// <param name="hit">The closest hit.</param>
    /// <returns>1 when something other than sky blocks the segment, else 0.</returns>
    internal static float Occlusion(HitId hit) => IsBlocking(hit) && !TraceId.IsSky(hit.Surface) ? 1.0f : 0.0f;

    /// <summary>
    /// Whether a closest hit lies within the segment: stock's
    /// <c>HitIds != -1 &amp;&amp; HitDistance &lt; len</c>.
    /// </summary>
    /// <param name="hit">The tracer's answer for a ray made by <see cref="MakeRay"/>.</param>
    /// <returns>True when something blocks the segment.</returns>
    /// <remarks>
    /// Stock's <c>Trace4Rays</c> does not clip hits to <c>TMax</c>: its
    /// callers test the distance themselves, and <see cref="Tracing.KdRayTracer"/>
    /// reproduces that on its CLOSEST-hit path, reporting fractions above 1.
    /// This is that test, for the sky rays, which need the surface as well as
    /// the answer. Visibility rays do not come through here: the seam's
    /// visibility operation honours the segment's end itself.
    /// </remarks>
    public static bool IsBlocking(HitId hit) => hit.IsHit && hit.Fraction < 1.0f;

    /// <summary>
    /// The ray <c>TestLine</c> builds: from <paramref name="start"/>, towards
    /// <paramref name="stop"/>, counting hits short of it.
    /// </summary>
    /// <param name="start">The start.</param>
    /// <param name="stop">The end.</param>
    /// <returns>A ray whose direction is the whole segment and whose reach is 1.</returns>
    /// <remarks>
    /// Stock normalises the direction with a reciprocal ESTIMATE and traces to
    /// <c>len</c>; the seam takes the unnormalised
    /// segment and a reach of 1, which is the same segment with no estimate in
    /// it.
    /// </remarks>
    public static Ray MakeRay(Vec3 start, Vec3 stop)
    {
        Vec3 d = stop - start;
        return new Ray(start.X, start.Y, start.Z, d.X, d.Y, d.Z, 1.0f);
    }

    /// <summary>
    /// A segment as stock's <c>TestLine</c> and <c>TestLine_DoesHitSky</c>
    /// Build it: the direction divided by
    /// its length with <c>ReciprocalSIMD</c> (<c>rcpps</c> plus one Newton
    /// step), and the length itself as the far limit.
    /// </summary>
    /// <param name="start">Where the segment starts.</param>
    /// <param name="stop">Where it ends.</param>
    /// <returns>The ray.</returns>
    /// <remarks>
    /// <c>length()</c> is <c>SqrtEstSIMD</c>, which on the PC is an exact
    /// <c>sqrtps</c>. The reciprocal is the estimate, so
    /// this is part of <see cref="Options.StockQuirk.GatherReciprocalEstimate"/>:
    /// on axis-aligned maps a ray that grazes a brush edge is decided by these
    /// last bits, which is what moved single supersamples at shadow edges.
    /// </remarks>
    public static Ray MakeStockRay(Vec3 start, Vec3 stop)
    {
        Vec3 d = stop - start;
        float len = MathF.Sqrt((d.X * d.X) + (d.Y * d.Y) + (d.Z * d.Z));
        float inv = StockSimd.Reciprocal(len, estimate: true);
        return new Ray(start.X, start.Y, start.Z, d.X * inv, d.Y * inv, d.Z * inv, len);
    }

    /// <summary>
    /// Build rays as stock does (<see cref="MakeStockRay"/>) rather than as the
    /// exact segment (<see cref="MakeRay"/>).
    /// </summary>
    public bool StockRays { get; init; }

    private Ray Make(Vec3 start, Vec3 stop) => StockRays ? MakeStockRay(start, stop) : MakeRay(start, stop);
}

/// <summary>
/// A <c>TestLine_DoesHitSky</c> whose skybox recursion waits for the second
/// stage (<see cref="LightRayLog.DeferRecursion"/>).
/// </summary>
/// <param name="PrimaryBase">The index of its first first-stage sky ray.</param>
/// <param name="Count">Its real lanes, 1..4.</param>
/// <param name="RecurseMask">Which lanes recurse; nonzero.</param>
/// <param name="PointBase">Where its (start, stop) pairs sit in <see cref="LightRayLog.DeferredPoints"/>.</param>
internal readonly record struct DeferredSkyTest(int PrimaryBase, int Count, int RecurseMask, int PointBase);
