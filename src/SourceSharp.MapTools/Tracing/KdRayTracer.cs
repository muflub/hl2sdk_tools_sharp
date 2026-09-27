//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;

namespace SourceSharp.MapTools.Tracing;

/// <summary>
/// Stock vrad's <c>g_RtEnv</c>: a KD-tree over loose triangles, traversed four
/// rays at a time.
/// </summary>
/// <remarks>
/// <para>
/// This is the tracer behind <c>TestLine</c>, the shadow rays, the static-prop
/// paths and <c>-textureshadows</c>. It is NOT the one leaf ambient uses --
/// that is <see cref="BspSurfaceTracer"/>, and the distinction is worth
/// 51.6 % of vrad's wall clock.
/// </para>
/// <para>
/// Ported from the reference implementation operation for operation, with
/// <see cref="Vector128{T}"/> standing in for <c>fltx4</c>. That is not
/// decoration: every SSE intrinsic stock uses has a one-to-one managed
/// equivalent, so the arithmetic is the same instructions in the same order,
/// and the comparison against stock can be exact rather than approximate.
/// </para>
/// <para>
/// The code is written against <see cref="Vector128"/> rather than
/// <c>Sse</c>, so it runs on arm64 as well; on x86 the JIT emits the same SSE
/// instructions. Adds, multiplies, divides, compares and bit operations are
/// exact IEEE operations with the same result on either instruction set.
/// <c>minps</c>, <c>maxps</c> and <c>andnps</c> have x86-specific rules and
/// are spelled out in <see cref="SseLanes"/>. The one inexact instruction,
/// the reciprocal estimate, comes from <see cref="FloatEstimate"/>, so traversal on arm64
/// uses ARM's estimate and its hit distances are arm64's own, as they are
/// already per vendor on x86.
/// </para>
/// <para>
/// FOUR RAYS AT A TIME, INCLUDING THE AWKWARD CASE. A packet can only be
/// traversed together if all four directions agree in sign on all three axes,
/// because the front-to-back child order is chosen from the sign. Stock's
/// answer when they do not agree is to re-trace the packet in up to four
/// passes, each time broadcasting one ray into all four lanes and folding in
/// whichever other rays happen to share its signs
/// That is reproduced, and it matters for
/// throughput on real data far more than it looks: leaf-ambient-style fans
/// point every way at once.
/// </para>
/// </remarks>
public sealed class KdRayTracer : IRayTracer
{
    /// <summary><c>MAILBOX_HASH_SIZE</c>.</summary>
    private const int MailboxSize = 256;

    /// <summary><c>MAX_NODE_STACK_LEN</c>: 40 * MAX_TREE_DEPTH.</summary>
    private const int MaxNodeStack = 40 * KdTreeBuilder.MaxTreeDepth;

    private readonly KdNode[] _nodes;
    private readonly int[] _indices;
    private readonly KdTriangle[] _triangles;
    private readonly Vec3 _min;
    private readonly Vec3 _max;

    /// <summary>
    /// What <see cref="ReciprocalSaturate"/> puts in place of a zero direction
    /// component before taking its reciprocal.
    /// </summary>
    /// <remarks>
    /// <see cref="StockZeroSubstitute"/> under
    /// <see cref="StockQuirk.KdZeroDirectionReachCut"/>, otherwise
    /// <see cref="CorrectZeroSubstitute"/>.
    /// </remarks>
    private readonly float _zeroSubstitute;

    private KdRayTracer(KdBuildResult built, float zeroSubstitute)
    {
        _nodes = built.Nodes;
        _indices = built.Indices;
        _triangles = built.Triangles;
        _min = built.Min;
        _max = built.Max;
        _zeroSubstitute = zeroSubstitute;
    }

    /// <summary>
    /// Stock's <c>Four_Epsilons</c>, <c>FLT_EPSILON</c>
    /// What <c>ReciprocalSaturateSIMD</c>
    /// ORs into a zero direction component.
    /// </summary>
    internal const float StockZeroSubstitute = 1.1920929e-7f;

    /// <summary>
    /// 2^-60: the zero substitute under <see cref="CompliancePolicy.Correct"/>.
    /// </summary>
    /// <remarks>
    /// The largest reciprocal the Newton step can take without its
    /// <c>y * y</c> overflowing is about 1.8e19, so this is about as far as the
    /// saturation can be pushed without changing the arithmetic's shape. A
    /// ray is then cut short only when its origin lies within
    /// <c>reach * 2^-60</c> of a plane without lying on it: under 1e-13 for
    /// the longest ray a map can hold, a gap two floats can only have when
    /// both are within about 1e-6 of zero.
    /// </remarks>
    internal const float CorrectZeroSubstitute = 8.6736174e-19f;

    /// <inheritdoc />
    /// <remarks>
    /// The two policies differ in <see cref="StockQuirk.KdZeroDirectionReachCut"/>,
    /// which can change a hit, so they are two identities.
    /// </remarks>
    public string TracerIdentity =>
        _zeroSubstitute == StockZeroSubstitute ? "cpu-kd-sse4-1-stock" : "cpu-kd-sse4-1";

    /// <inheritdoc />
    /// <remarks>
    /// Every option: the traversal takes stock's <c>skip_id</c> natively, the
    /// sky test is one look at the first hit's id, and an isolated ray is a
    /// packet of four copies of itself.
    /// </remarks>
    public bool Supports(RayTraceOptions options) => true;

    /// <summary>How many nodes the built tree has.</summary>
    public int NodeCount => _nodes.Length;

    /// <summary>How long the triangle index list is.</summary>
    public int IndexCount => _indices.Length;

    /// <summary>How many triangles the scene has.</summary>
    public int TriangleCount => _triangles.Length;

    /// <summary>The scene's lower bound.</summary>
    public Vec3 MinBound => _min;

    /// <summary>The scene's upper bound.</summary>
    public Vec3 MaxBound => _max;

    /// <summary>
    /// Builds a tracer over a set of triangles.
    /// </summary>
    /// <param name="triangles">The scene.</param>
    /// <returns>The tracer.</returns>
    /// <exception cref="ArgumentException"><paramref name="triangles"/> is empty.</exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The machine has neither SSE nor AdvSimd, so no reciprocal estimate.
    /// Refused rather than emulated: the traversal reproduces stock's
    /// estimate-based arithmetic, and an exact fallback would silently stop
    /// being the reference implementation it is here to be.
    /// </exception>
    /// <remarks>
    /// Under <see cref="ComplianceOptions.Correct"/>, the library's default.
    /// </remarks>
    public static KdRayTracer Build(ReadOnlySpan<TracedTriangle> triangles) =>
        Build(triangles, ComplianceOptions.Correct);

    /// <summary>
    /// Builds a tracer over a set of triangles, reproducing or correcting
    /// stock's traversal defects as <paramref name="compliance"/> says.
    /// </summary>
    /// <param name="triangles">The scene.</param>
    /// <param name="compliance">
    /// Decides <see cref="StockQuirk.KdZeroDirectionReachCut"/>. The tree is
    /// the same either way.
    /// </param>
    /// <returns>The tracer.</returns>
    /// <exception cref="ArgumentException"><paramref name="triangles"/> is empty.</exception>
    /// <exception cref="PlatformNotSupportedException">The machine has neither SSE nor AdvSimd.</exception>
    public static KdRayTracer Build(ReadOnlySpan<TracedTriangle> triangles, ComplianceOptions compliance)
    {
        ArgumentNullException.ThrowIfNull(compliance);
        RequireEstimate();
        return new KdRayTracer(KdTreeBuilder.Build(triangles), ZeroSubstitute(compliance));
    }

    /// <summary>
    /// <see cref="Build(ReadOnlySpan{TracedTriangle}, ComplianceOptions)"/>
    /// with the tree's subtrees built on a queue's workers: the same tree, node
    /// for node.
    /// </summary>
    /// <param name="triangles">The scene.</param>
    /// <param name="compliance">Decides <see cref="StockQuirk.KdZeroDirectionReachCut"/>.</param>
    /// <param name="queue">The workers to build on.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The tracer.</returns>
    /// <exception cref="ArgumentException"><paramref name="triangles"/> is empty.</exception>
    /// <exception cref="PlatformNotSupportedException">The machine has neither SSE nor AdvSimd.</exception>
    public static async Task<KdRayTracer> BuildAsync(
        ReadOnlyMemory<TracedTriangle> triangles,
        ComplianceOptions compliance,
        WorkQueue queue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compliance);
        ArgumentNullException.ThrowIfNull(queue);
        RequireEstimate();
        KdBuildResult built = await KdTreeBuilder.BuildAsync(triangles, queue, cancellationToken).ConfigureAwait(false);
        return new KdRayTracer(built, ZeroSubstitute(compliance));
    }

    private static float ZeroSubstitute(ComplianceOptions compliance) =>
        compliance.Emulates(StockQuirk.KdZeroDirectionReachCut)
            ? StockZeroSubstitute
            : CorrectZeroSubstitute;

    private static void RequireEstimate()
    {
        if (!FloatEstimate.IsSupported)
        {
            throw FloatEstimate.Unsupported();
        }
    }

    /// <summary>One node, for a fact that compares the tree against stock's.</summary>
    /// <param name="index">Which node.</param>
    /// <returns>Its packed children word and its splitting plane's bits.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such node.</exception>
    public (int Children, int SplitBits) Node(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _nodes.Length);
        return (_nodes[index].Children, BitConverter.SingleToInt32Bits(_nodes[index].Split));
    }

    /// <summary>One entry of the triangle index list.</summary>
    /// <param name="index">Which entry.</param>
    /// <returns>A triangle index.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such entry.</exception>
    public int Index(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _indices.Length);
        return _indices[index];
    }

    /// <summary>One triangle in intersection format.</summary>
    /// <param name="index">Which triangle.</param>
    /// <returns>Its plane, id, two edge equations and projection axes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such triangle.</exception>
    public (Vec3 Normal, float D, int Id, float[] Edges, int Cs0, int Cs1) Triangle(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _triangles.Length);
        ref KdTriangle t = ref _triangles[index];
        return (
            new Vec3(t.Nx, t.Ny, t.Nz),
            t.D,
            t.Id,
            [t.E0, t.E1, t.E2, t.E3, t.E4, t.E5],
            t.CoordSelect0,
            t.CoordSelect1);
    }

    /// <summary>How many bytes one packed node occupies. Stock's is 8.</summary>
    public static int NodeStrideBytes => Unsafe.SizeOf<KdNode>();

    /// <summary>
    /// How many bytes one intersection-format triangle occupies.
    /// </summary>
    /// <remarks>
    /// FORTY-EIGHT. says "16longs=64 bytes" over the struct
    /// and the plan repeats it; a compiled <c>sizeof</c> of stock's own header
    /// in this tree prints 48, and so does this. The comment is stale.
    /// </remarks>
    public static int TriangleStrideBytes => Unsafe.SizeOf<KdTriangle>();

    /// <inheritdoc />
    public ValueTask TraceVisibilityAsync(
        ReadOnlyMemory<Ray> rays,
        Memory<ulong> hitBits,
        RayTraceOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TraceVisibility(rays.Span, hitBits.Span, options);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask TraceClosestAsync(
        ReadOnlyMemory<Ray> rays,
        Memory<HitId> hits,
        RayTraceOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TraceClosest(rays.Span, hits.Span, options);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Closest hit for a batch, four rays at a time.
    /// </summary>
    /// <param name="rays">The rays.</param>
    /// <param name="hits">Receives one result per ray.</param>
    /// <param name="options">The ray epsilon, as this tracer's <c>TMin</c>.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="hits"/> is shorter than <paramref name="rays"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The last packet of an odd batch is padded by repeating the final ray,
    /// which is what stock's callers do and costs at most three wasted lanes
    /// per batch.
    /// </para>
    /// <para>
    /// <b>The fraction can exceed 1.</b> Stock's <c>Trace4Rays</c> bounds the
    /// traversal by <c>TMax</c> but does not clip the hit
    /// (is commented out), so a triangle in the same
    /// KD leaf as the segment's end is reported even when it lies beyond it.
    /// That is kept here, because stock's closest-hit callers see the same
    /// answer; a caller asking about the SEGMENT tests
    /// <c>Fraction &lt; 1</c>, or uses <see cref="TraceVisibility"/>, which
    /// does.
    /// </para>
    /// <para>
    /// <see cref="RayTraceOptions.SkipId"/> and
    /// <see cref="RayTraceOptions.IsolatedRays"/> are honoured as in
    /// <see cref="TraceVisibility"/>. <see cref="RayTraceOptions.SkyDoesNotBlock"/>
    /// is refused: "not blocked" is a visibility answer, and a closest-hit
    /// caller already sees whether the surface it hit is sky.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="options"/> asks for <see cref="RayTraceOptions.SkyDoesNotBlock"/>.
    /// </exception>
    [SkipLocalsInit]
    public void TraceClosest(ReadOnlySpan<Ray> rays, Span<HitId> hits, RayTraceOptions options)
    {
        if (hits.Length < rays.Length)
        {
            throw new ArgumentException(
                $"{rays.Length} rays need {rays.Length} hit slots, and {hits.Length} were given",
                nameof(hits));
        }

        if (options.SkyDoesNotBlock)
        {
            throw new ArgumentException(
                "sky pass-through is a visibility option; a closest-hit trace reports the sky hit itself",
                nameof(options));
        }

        Scratch scratch = new(stackalloc NodeToVisit[MaxNodeStack], stackalloc long[MailboxSize]);
        Vector128<float> tmin = Vector128.Create(options.MinDistance);
        int skipId = options.SkipId ?? -1;
        int step = options.IsolatedRays ? 1 : 4;

        for (int i = 0; i < rays.Length; i += step)
        {
            // The segment is origin + t * Direction for t in [TMin, MaxDistance],
            // traced exactly as stock's Trace4Rays takes it: the direction as
            // given and the length as TMax. A caller that passes the whole
            // segment as the direction and 1 as the length (every caller before
            // p4f) gets the same bits as the earlier Direction*MaxDistance form,
            // because multiplying by 1.0 is exact.
            RayPacket packet;
            Vector128<float> tmax;
            if (options.IsolatedRays)
            {
                DuplicatePacket(in rays[i], out packet, out tmax);
            }
            else
            {
                LoadPacket(rays, i, out packet, out tmax);
            }

            Trace4Rays(in packet, tmin, tmax, skipId, ref scratch, out Vector128<float> distance, out Vector128<int> ids);

            for (int lane = 0; lane < step && i + lane < rays.Length; lane++)
            {
                int id = ids.GetElement(lane);
                float length = tmax.GetElement(lane);
                hits[i + lane] = id < 0
                    ? HitId.Missed
                    : new HitId(
                        _triangles[id].Id,
                        length == 1.0f ? distance.GetElement(lane) : distance.GetElement(lane) / length);
            }
        }
    }

    /// <summary>
    /// <c>TestLine</c> with texture shadows off, in
    /// stock's own parameterisation, for a batch of segments.
    /// </summary>
    /// <param name="starts">Segment starts.</param>
    /// <param name="ends">Segment ends, one per start.</param>
    /// <param name="blocked">Receives, per segment, whether something is hit
    /// strictly before its end.</param>
    /// <param name="stockReciprocal">
    /// Normalise with <c>ReciprocalSIMD</c> -- <c>rcpps</c> plus one Newton step
    /// -- as stock does; false divides exactly.
    /// </param>
    /// <param name="skyDoesNotBlock">
    /// <c>TestLine_DoesHitSky</c>: a hit on a
    /// <c>TRACE_ID_SKY</c> triangle counts as reaching the sky, not as a block.
    /// Its recursion into 3D skyboxes is NOT ported.
    /// </param>
    /// <param name="skipId">
    /// <c>Trace4Rays</c>' <c>skip_id</c>: triangles
    /// with this id are ignored. <c>TestLine</c> passes
    /// <c>TRACE_ID_STATICPROP | prop</c> so a prop does not shadow itself; -1
    /// ignores nothing. Transparent (<c>-textureshadows</c>) triangles are still
    /// treated as opaque here: stock's coverage callback is not ported.
    /// </param>
    /// <remarks>
    /// <para>
    /// ADDED BY LANE p4g for leaf ambient's <c>AddEmitSurfaceLights</c>, and
    /// usable by any <c>TestLine</c> caller. Unlike
    /// <see cref="TraceVisibility"/>, which takes each ray's direction and
    /// reach as given (lane p4d), this rebuilds stock's call exactly: the direction is
    /// <c>(stop - start) * ReciprocalSIMD(len)</c>, the ray runs over
    /// <c>[0, len]</c>, and a segment is blocked when <c>HitIds != -1 &amp;&amp;
    /// HitDistance &lt; len</c>. Each segment is duplicated into all four lanes,
    /// as <c>FourVectors::DuplicateVector</c> does, so its answer cannot depend
    /// on a packet-mate.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The spans differ in length.</exception>
    [SkipLocalsInit]
    public void TestLines(
        ReadOnlySpan<Vec3> starts, ReadOnlySpan<Vec3> ends, Span<bool> blocked, bool stockReciprocal,
        bool skyDoesNotBlock = false, int skipId = -1)
    {
        if (ends.Length != starts.Length || blocked.Length < starts.Length)
        {
            throw new ArgumentException("one end and one result slot per start", nameof(ends));
        }

        TestLinesCore(starts, default, ends, blocked, stockReciprocal, skyDoesNotBlock, skipId);
    }

    /// <summary>
    /// <see cref="TestLines(ReadOnlySpan{Vec3}, ReadOnlySpan{Vec3}, Span{bool}, bool, bool, int)"/>
    /// for segments that all start at one point.
    /// </summary>
    /// <param name="start">Where every segment starts.</param>
    /// <param name="ends">Where each segment ends.</param>
    /// <param name="blocked">Receives, per segment, whether it is blocked.</param>
    /// <param name="stockReciprocal">As for the per-segment overload.</param>
    /// <param name="skyDoesNotBlock">As for the per-segment overload.</param>
    /// <param name="skipId">As for the per-segment overload.</param>
    /// <remarks>
    /// Leaf ambient tests every sample of a leaf against the same point, often
    /// thousands of segments at once. Spelling that as the per-segment overload
    /// means copying the start once per segment into an array of its own,
    /// which on a full-size map was a third of everything a compile allocated.
    /// Each segment is traced with exactly the arithmetic of the per-segment
    /// overload, so the answers are the same bits.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="blocked"/> is shorter than <paramref name="ends"/>.</exception>
    [SkipLocalsInit]
    public void TestLines(
        Vec3 start, ReadOnlySpan<Vec3> ends, Span<bool> blocked, bool stockReciprocal,
        bool skyDoesNotBlock = false, int skipId = -1)
    {
        if (blocked.Length < ends.Length)
        {
            throw new ArgumentException("one result slot per end", nameof(blocked));
        }

        TestLinesCore([], start, ends, blocked, stockReciprocal, skyDoesNotBlock, skipId);
    }

    // Starts come from `starts` when it is not empty, else every segment
    // starts at `oneStart`.
    [SkipLocalsInit]
    private void TestLinesCore(
        ReadOnlySpan<Vec3> starts, Vec3 oneStart, ReadOnlySpan<Vec3> ends, Span<bool> blocked,
        bool stockReciprocal, bool skyDoesNotBlock, int skipId)
    {
        Scratch scratch = new(stackalloc NodeToVisit[MaxNodeStack], stackalloc long[MailboxSize]);
        bool perSegment = !starts.IsEmpty;

        for (int i = 0; i < ends.Length; i++)
        {
            // The ray is built by Ray.Segment, the same code a sampler batching
            // through IRayTracer uses, so the two routes trace the same floats.
            Ray ray = Ray.Segment(perSegment ? starts[i] : oneStart, ends[i], stockReciprocal);
            blocked[i] = TraceIsolated(in ray, Vector128<float>.Zero, skipId, skyDoesNotBlock, ref scratch);
        }
    }

    /// <summary>
    /// One ray traced alone -- duplicated into all four lanes, as
    /// <c>FourVectors::DuplicateVector</c> does for <c>TestLine</c> -- and
    /// whether it is blocked short of its reach.
    /// </summary>
    /// <remarks>
    /// <c>TestLine_DoesHitSky</c> differs from <c>TestLine</c> in one place: a
    /// first hit on a sky triangle does not occlude.
    /// </remarks>
    private bool TraceIsolated(
        in Ray ray, Vector128<float> tmin, int skipId, bool skyDoesNotBlock, ref Scratch scratch)
    {
        DuplicatePacket(in ray, out RayPacket packet, out Vector128<float> reach);
        Trace4Rays(
            in packet, tmin, reach, skipId, ref scratch,
            out Vector128<float> distance, out Vector128<int> ids);

        int hitId = ids.ToScalar();
        return hitId != -1 && distance.ToScalar() < ray.MaxDistance
            && !(skyDoesNotBlock && (_triangles[hitId].Id & Rad.TraceId.Sky) != 0);
    }

    /// <summary>One ray in all four lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DuplicatePacket(in Ray ray, out RayPacket packet, out Vector128<float> reach)
    {
        packet.Ox = Vector128.Create(ray.OriginX);
        packet.Oy = Vector128.Create(ray.OriginY);
        packet.Oz = Vector128.Create(ray.OriginZ);
        packet.Dx = Vector128.Create(ray.DirectionX);
        packet.Dy = Vector128.Create(ray.DirectionY);
        packet.Dz = Vector128.Create(ray.DirectionZ);
        reach = Vector128.Create(ray.MaxDistance);
    }

    /// <summary>
    /// Visibility bits for a batch: whether anything lies on each SEGMENT,
    /// from the origin to <c>Origin + MaxDistance * Direction</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A hit at or beyond the segment's end does not set the bit, which is the
    /// test stock's callers make after an unclipped <c>Trace4Rays</c>
    /// <see cref="TraceClosest"/>
    /// does NOT clip, as stock does not, and can report a fraction above 1.
    /// </para>
    /// <para>
    /// Every <see cref="RayTraceOptions"/> member is honoured. With
    /// <see cref="RayTraceOptions.IsolatedRays"/> each ray is traced in a packet
    /// of its own, which is exactly what
    /// <see cref="TestLines(ReadOnlySpan{Vec3}, ReadOnlySpan{Vec3}, Span{bool}, bool, bool, int)"/>
    /// does for the same ray, so a sampler that builds its rays with
    /// <see cref="Ray.Segment"/> and traces them through the seam gets the
    /// direct call's bits.
    /// </para>
    /// </remarks>
    /// <param name="rays">The rays.</param>
    /// <param name="hitBits">Receives one bit per ray, least-significant first.</param>
    /// <param name="options">The ray epsilon, and which ids and packets the trace sees.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="hitBits"/> is too short.
    /// </exception>
    [SkipLocalsInit]
    public void TraceVisibility(
        ReadOnlySpan<Ray> rays, Span<ulong> hitBits, RayTraceOptions options)
    {
        int words = (rays.Length + 63) / 64;
        if (hitBits.Length < words)
        {
            throw new ArgumentException(
                $"{rays.Length} rays need {words} words of hit bits, and {hitBits.Length} "
                + "were given",
                nameof(hitBits));
        }

        hitBits[..words].Clear();

        Scratch scratch = new(stackalloc NodeToVisit[MaxNodeStack], stackalloc long[MailboxSize]);
        Vector128<float> tmin = Vector128.Create(options.MinDistance);
        int skipId = options.SkipId ?? -1;

        if (options.IsolatedRays)
        {
            for (int i = 0; i < rays.Length; i++)
            {
                if (TraceIsolated(in rays[i], tmin, skipId, options.SkyDoesNotBlock, ref scratch))
                {
                    hitBits[i >> 6] |= 1UL << (i & 63);
                }
            }

            return;
        }

        for (int i = 0; i < rays.Length; i += 4)
        {
            // The direction goes in as given and the reach as TMax, which is
            // exactly how stock's callers trace: TestLine and the transfer
            // stream normalise the direction and pass the length as TMax
            // A caller passing the
            // whole segment with a reach of 1 gets the same packet it always
            // did (x * 1 is x).
            LoadPacket(rays, i, out RayPacket packet, out Vector128<float> reach);

            Trace4Rays(in packet, tmin, reach, skipId, ref scratch, out Vector128<float> distance, out Vector128<int> ids);

            // The segment test stock's callers make themselves, because
            // Trace4Rays does not clip a hit to TMax (is
            // commented out): TestLine keeps a hit only when
            // HitDistance < len, and CTransferMaker makes
            // a transfer when HitDistance >= ray_length.
            // i is a multiple of four, so the packet's four bits never straddle
            // a word.
            Vector128<float> blockedLanes = Vector128.BitwiseAnd(
                Vector128.GreaterThan(ids, Vector128.Create(-1)).AsSingle(),
                Vector128.LessThan(distance, reach));
            int bits = SseLanes.MoveMask(blockedLanes);
            if (options.SkyDoesNotBlock)
            {
                bits &= ~SkyLanes(ids);
            }

            int live = rays.Length - i;
            if (live < 4)
            {
                bits &= (1 << live) - 1;
            }

            hitBits[i >> 6] |= (ulong)(uint)bits << (i & 63);
        }
    }

    /// <summary>The lanes whose first hit is a sky triangle, as a four-bit mask.</summary>
    private int SkyLanes(Vector128<int> ids)
    {
        int sky = 0;
        for (int lane = 0; lane < 4; lane++)
        {
            int id = ids.GetElement(lane);
            if (id >= 0 && (_triangles[id].Id & Rad.TraceId.Sky) != 0)
            {
                sky |= 1 << lane;
            }
        }

        return sky;
    }


    /// <summary>
    /// Four rays starting at <paramref name="first"/>, transposed into lanes;
    /// a short last packet repeats the final ray, as stock's callers pad.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LoadPacket(
        ReadOnlySpan<Ray> rays, int first, out RayPacket packet, out Vector128<float> reach)
    {
        if (rays.Length - first >= 4)
        {
            // Seven floats per ray: loads at 0/7/14/21 read (ox, oy, oz, dx) of
            // each ray, loads at 3/10/17/24 read (dx, dy, dz, reach). Two 4x4
            // transposes turn them into lanes. The last load ends exactly at
            // the fourth ray's last float.
            ref float f = ref Unsafe.As<Ray, float>(ref MemoryMarshal.GetReference(rays.Slice(first, 4)));
            SseLanes.Transpose(
                Vector128.LoadUnsafe(ref f, 0),
                Vector128.LoadUnsafe(ref f, 7),
                Vector128.LoadUnsafe(ref f, 14),
                Vector128.LoadUnsafe(ref f, 21),
                out packet.Ox, out packet.Oy, out packet.Oz, out _);
            SseLanes.Transpose(
                Vector128.LoadUnsafe(ref f, 3),
                Vector128.LoadUnsafe(ref f, 10),
                Vector128.LoadUnsafe(ref f, 17),
                Vector128.LoadUnsafe(ref f, 24),
                out packet.Dx, out packet.Dy, out packet.Dz, out reach);
            return;
        }

        FourFloats ox = default, oy = default, oz = default, dx = default, dy = default, dz = default, m = default;
        for (int lane = 0; lane < 4; lane++)
        {
            Ray r = rays[Math.Min(first + lane, rays.Length - 1)];
            ox[lane] = r.OriginX;
            oy[lane] = r.OriginY;
            oz[lane] = r.OriginZ;
            dx[lane] = r.DirectionX;
            dy[lane] = r.DirectionY;
            dz[lane] = r.DirectionZ;
            m[lane] = r.MaxDistance;
        }

        packet.Ox = ox.AsVector();
        packet.Oy = oy.AsVector();
        packet.Oz = oz.AsVector();
        packet.Dx = dx.AsVector();
        packet.Dy = dy.AsVector();
        packet.Dz = dz.AsVector();
        reach = m.AsVector();
    }

    /// <summary>
    /// <c>Trace4Rays</c> without a direction sign mask,
    /// Works out whether the four can go together and
    /// splits the packet when they cannot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The split is stock's: up to four passes, each
    /// broadcasting ray <c>tryTrace</c>'s direction into every lane and keeping
    /// the own direction of every still-untraced lane whose signs match it, so
    /// a packet whose directions disagree usually costs two traversals rather
    /// than four. Lanes carrying the broadcast direction are traced too, from
    /// their own origins, and their answers thrown away -- they still steer
    /// the walk, which is why they are reproduced rather than masked off.
    /// </para>
    /// <para>
    /// A lane's sign code packs its three sign BITS, so "same code" is exactly
    /// stock's <c>SameSign</c> on all three axes (it compares bits, not values:
    /// -0 and +0 differ), and the pass's own sign mask is its code.
    /// </para>
    /// </remarks>
    private void Trace4Rays(
        in RayPacket rays,
        Vector128<float> tmin,
        Vector128<float> tmax,
        int skipId,
        ref Scratch scratch,
        out Vector128<float> hitDistance,
        out Vector128<int> hitIds)
    {
        int sx = SseLanes.MoveMask(rays.Dx);
        int sy = SseLanes.MoveMask(rays.Dy);
        int sz = SseLanes.MoveMask(rays.Dz);
        if (IsUniform(sx) && IsUniform(sy) && IsUniform(sz))
        {
            int mask = (sx & 1) | ((sy & 1) << 1) | ((sz & 1) << 2);
            TraceSameSigns(in rays, tmin, tmax, mask, skipId, ref scratch, out hitDistance, out hitIds);
            return;
        }

        hitDistance = Vector128.Create(1.0e23f);
        hitIds = Vector128.Create(-1);

        int needTrace = 0b1111;
        for (int tryTrace = 0; tryTrace < 4; tryTrace++)
        {
            if ((needTrace & (1 << tryTrace)) == 0)
            {
                continue;
            }

            int code = SignCode(sx, sy, sz, tryTrace);
            int own = 0;
            for (int lane = tryTrace; lane < 4; lane++)
            {
                if ((needTrace & (1 << lane)) != 0 && SignCode(sx, sy, sz, lane) == code)
                {
                    own |= 1 << lane;
                }
            }

            needTrace &= ~own;
            Vector128<float> keep = LaneMask(own);

            RayPacket tmp;
            tmp.Ox = rays.Ox;
            tmp.Oy = rays.Oy;
            tmp.Oz = rays.Oz;
            tmp.Dx = Select(keep, rays.Dx, Vector128.Create(rays.Dx.GetElement(tryTrace)));
            tmp.Dy = Select(keep, rays.Dy, Vector128.Create(rays.Dy.GetElement(tryTrace)));
            tmp.Dz = Select(keep, rays.Dz, Vector128.Create(rays.Dz.GetElement(tryTrace)));

            TraceSameSigns(
                in tmp, tmin, tmax, code, skipId, ref scratch,
                out Vector128<float> subDistance, out Vector128<int> subIds);

            hitDistance = Select(keep, subDistance, hitDistance);
            hitIds = Select(keep, subIds.AsSingle(), hitIds.AsSingle()).AsInt32();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsUniform(int signBits) => signBits == 0 || signBits == 0b1111;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SignCode(int sx, int sy, int sz, int lane) =>
        ((sx >> lane) & 1) | (((sy >> lane) & 1) << 1) | (((sz >> lane) & 1) << 2);

    /// <summary>All-ones in the lanes whose bit is set in <paramref name="bits"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> LaneMask(int bits) =>
        Vector128.Equals(
            Vector128.BitwiseAnd(Vector128.Create(bits), Vector128.Create(1, 2, 4, 8)),
            Vector128.Create(1, 2, 4, 8)).AsSingle();

    /// <summary>
    /// <c>mask ? a : b</c> as stock's <c>OrSIMD(AndSIMD(a, mask), AndNotSIMD(mask, b))</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> Select(Vector128<float> mask, Vector128<float> a, Vector128<float> b) =>
        Vector128.BitwiseOr(Vector128.BitwiseAnd(a, mask), SseLanes.AndNot(mask, b));

    /// <summary>
    /// <c>Trace4Rays</c> with a known direction sign mask,
    /// The traversal proper.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three axes are held in small structs and INDEXED by the node's split
    /// axis rather than selected by a chain of conditionals: the axis is data,
    /// and a chain turns it into two unpredictable branches per node. Stock
    /// indexes <c>FourVectors</c> the same way:
    /// <c>rays.origin[split_plane_number]</c>. The near child comes straight
    /// from the sign mask's bit for that axis.
    /// </para>
    /// <para>
    /// The mailbox is stamped with a per-traversal
    /// generation instead of being refilled with -1 for every packet, as stock's
    /// <c>memset</c> does: a slot counts only when its generation is this
    /// traversal's, which is exactly "cleared at the start". That is 2 KB of
    /// stores per packet not made.
    /// </para>
    /// </remarks>
    [SkipLocalsInit]
    private void TraceSameSigns(
        in RayPacket rays,
        Vector128<float> tmin,
        Vector128<float> tmax,
        int directionSignMask,
        int skipId,
        ref Scratch scratch,
        out Vector128<float> hitDistance,
        out Vector128<int> hitIds)
    {
        Vector128<float> distance = Vector128.Create(1.0e23f);
        Vector128<float> ids = Vector128.Create(-1).AsSingle();

        Axes origin;
        origin.X = rays.Ox;
        origin.Y = rays.Oy;
        origin.Z = rays.Oz;
        Axes direction;
        direction.X = rays.Dx;
        direction.Y = rays.Dy;
        direction.Z = rays.Dz;
        Vector128<float> zeroSubstitute = Vector128.Create(_zeroSubstitute);
        Axes inverse;
        inverse.X = ReciprocalSaturate(rays.Dx, zeroSubstitute);
        inverse.Y = ReciprocalSaturate(rays.Dy, zeroSubstitute);
        inverse.Z = ReciprocalSaturate(rays.Dz, zeroSubstitute);

        // Clip against the scene's bounding box first; if no
        // lane survives, nothing was hit and there is nothing to walk.
        ClipAxis(_min.X, _max.X, rays.Ox, inverse.X, ref tmin, ref tmax);
        ClipAxis(_min.Y, _max.Y, rays.Oy, inverse.Y, ref tmin, ref tmax);
        ClipAxis(_min.Z, _max.Z, rays.Oz, inverse.Z, ref tmin, ref tmax);

        if (SseLanes.MoveMask(Vector128.LessThanOrEqual(tmin, tmax)) == 0)
        {
            hitDistance = distance;
            hitIds = ids.AsInt32();
            return;
        }

        long generation = (long)++scratch.Generation << 32;

        ref KdNode nodes0 = ref MemoryMarshal.GetArrayDataReference(_nodes);
        ref int indices0 = ref MemoryMarshal.GetArrayDataReference(_indices);
        ref KdTriangle triangles0 = ref MemoryMarshal.GetArrayDataReference(_triangles);
        ref NodeToVisit stack0 = ref MemoryMarshal.GetReference(scratch.Stack);
        ref long mailbox0 = ref MemoryMarshal.GetReference(scratch.Mailbox);
        ref Vector128<float> origin0 = ref origin.X;
        ref Vector128<float> inverse0 = ref inverse.X;
        int stackLength = scratch.Stack.Length;

        int sp = 0;
        int node = 0;
        int pending = -1;

        while (true)
        {
            // One load of the node, not four. Every index here is the tree's
            // own -- a child it wrote, a leaf's run it wrote -- so the bounds
            // are a property of the build and not of the ray.
            KdNode n = Unsafe.Add(ref nodes0, node);
            while ((n.Children & 3) != KdNode.Leaf)
            {
                int axis = n.Children & 3;
                int leftChild = n.Children >> 2;
                int front = (directionSignMask >> axis) & 1;

                Vector128<float> distToPlane = Vector128.Multiply(
                    Vector128.Subtract(Vector128.Create(n.Split), Unsafe.Add(ref origin0, axis)),
                    Unsafe.Add(ref inverse0, axis));

                Vector128<float> active = Vector128.LessThanOrEqual(tmin, tmax);
                Vector128<float> hitsFront = Vector128.BitwiseAnd(
                    active, Vector128.GreaterThanOrEqual(distToPlane, tmin));

                if (SseLanes.MoveMask(hitsFront) == 0)
                {
                    node = leftChild + (1 - front);
                    tmin = SseLanes.Max(tmin, distToPlane);
                    n = Unsafe.Add(ref nodes0, node);
                    continue;
                }

                Vector128<float> hitsBack = Vector128.BitwiseAnd(
                    active, Vector128.LessThanOrEqual(distToPlane, tmax));

                if (SseLanes.MoveMask(hitsBack) == 0)
                {
                    node = leftChild + front;
                    tmax = SseLanes.Min(tmax, distToPlane);
                    n = Unsafe.Add(ref nodes0, node);
                    continue;
                }

                if (sp >= stackLength)
                {
                    ThrowStackOverflow();
                }

                ref NodeToVisit entry = ref Unsafe.Add(ref stack0, sp++);
                entry.Node = leftChild + (1 - front);
                entry.TMin = SseLanes.Max(tmin, distToPlane);
                entry.TMax = tmax;

                node = leftChild + front;
                tmax = SseLanes.Min(tmax, distToPlane);
                n = Unsafe.Add(ref nodes0, node);
            }

            int ntris = BitConverter.SingleToInt32Bits(n.Split);
            if (ntris != 0)
            {
                int start = n.Children >> 2;
                ref int run = ref Unsafe.Add(ref indices0, start);
                for (int t = 0; t < ntris; t++)
                {
                    int tnum = Unsafe.Add(ref run, t);

                    // The mailbox stops a triangle that
                    // straddles several leaves being tested more than once per
                    // ray, which the SAH build makes common: nboth triangles go
                    // into BOTH children.
                    ref long slot = ref Unsafe.Add(ref mailbox0, tnum & (MailboxSize - 1));
                    long stamp = generation | (uint)tnum;
                    if (slot == stamp)
                    {
                        continue;
                    }

                    ref KdTriangle tri = ref Unsafe.Add(ref triangles0, tnum);

                    // A triangle carrying skip_id is passed
                    // over, and NOT mailboxed (the test precedes the store).
                    if (skipId != -1 && tri.Id == skipId)
                    {
                        continue;
                    }

                    slot = stamp;

                    // Two at a time on AVX (IntersectPair): the first
                    // candidate waits for a second, and a leaf's odd one out
                    // is tested alone below.
                    if (!Avx.IsSupported)
                    {
                        IntersectTriangle(ref tri, tnum, ref origin, ref direction, ref distance, ref ids);
                    }
                    else if (pending < 0)
                    {
                        pending = tnum;
                    }
                    else
                    {
                        IntersectPair(
                            ref Unsafe.Add(ref triangles0, pending), pending, ref tri, tnum,
                            ref origin, ref direction, ref distance, ref ids);
                        pending = -1;
                    }
                }

                if (pending >= 0)
                {
                    IntersectTriangle(
                        ref Unsafe.Add(ref triangles0, pending), pending, ref origin, ref direction, ref distance, ref ids);
                    pending = -1;
                }

                // If every lane's best hit is already nearer
                // than this node's far edge, nothing further along can win.
                if (SseLanes.MoveMask(Vector128.LessThanOrEqual(tmax, distance)) == 0)
                {
                    break;
                }
            }

            if (sp == 0)
            {
                break;
            }

            sp--;
            ref NodeToVisit popped = ref Unsafe.Add(ref stack0, sp);
            node = popped.Node;
            tmin = popped.TMin;
            tmax = popped.TMax;
        }

        hitDistance = distance;
        hitIds = ids.AsInt32();
    }

    private static void ThrowStackOverflow() =>
        throw new InvalidOperationException(
            $"the KD walk went deeper than {MaxNodeStack} suspended nodes, which a "
            + "tree capped at depth " + KdTreeBuilder.MaxTreeDepth + " cannot do");

    /// <summary>
    /// The per-triangle test: plane, then two
    /// pre-scaled edge equations in the projection, then the third implied.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IntersectTriangle(
        ref KdTriangle tri,
        int tnum,
        ref Axes origin,
        ref Axes direction,
        ref Vector128<float> hitDistance,
        ref Vector128<float> hitIds)
    {
        Vector128<float> nx = Vector128.Create(tri.Nx);
        Vector128<float> ny = Vector128.Create(tri.Ny);
        Vector128<float> nz = Vector128.Create(tri.Nz);

        Vector128<float> ddotn = Dot(direction.X, direction.Y, direction.Z, nx, ny, nz);

        // Stock masks off rays running parallel to the plane with a
        // two-sided epsilon rather than a comparison against zero, so a
        // grazing ray is rejected instead of producing an enormous t.
        Vector128<float> epsilons = Vector128.Create(1.0e-10f);
        Vector128<float> didHit = Vector128.BitwiseOr(
            Vector128.GreaterThan(ddotn, epsilons),
            Vector128.LessThan(ddotn, Vector128.Create(-1.0e-10f)));

        Vector128<float> numerator =
            Vector128.Subtract(Vector128.Create(tri.D), Dot(origin.X, origin.Y, origin.Z, nx, ny, nz));
        Vector128<float> isectT = Vector128.Divide(numerator, ddotn);

        // Compares against FourZeros, which in stock is
        // declared as {1e-10, 1e-10, 1e-10, 1e-10} and NOT as zeros. That is
        // reproduced rather than corrected: it is the tracer's near clip, and
        // "FourZeros" being 1e-10 is a name, not a value.
        didHit = Vector128.BitwiseAnd(didHit, Vector128.GreaterThan(isectT, epsilons));
        didHit = Vector128.BitwiseAnd(didHit, Vector128.LessThan(isectT, hitDistance));

        if (SseLanes.MoveMask(didHit) == 0)
        {
            return;
        }

        // Indexed, not selected by conditionals: which two coordinates the
        // triangle was projected onto is per-triangle data, so a ternary chain
        // is two unpredictable branches on every candidate. Stock indexes
        // FourVectors here for the same reason.
        int cs0 = tri.CoordSelect0;
        int cs1 = tri.CoordSelect1;
        Vector128<float> hitc1 = Vector128.Add(
            Unsafe.Add(ref origin.X, cs0), Vector128.Multiply(isectT, Unsafe.Add(ref direction.X, cs0)));
        Vector128<float> hitc2 = Vector128.Add(
            Unsafe.Add(ref origin.X, cs1), Vector128.Multiply(isectT, Unsafe.Add(ref direction.X, cs1)));

        Vector128<float> b0 = Vector128.Multiply(Vector128.Create(tri.E0), hitc1);
        b0 = Vector128.Add(b0, Vector128.Multiply(Vector128.Create(tri.E1), hitc2));
        b0 = Vector128.Add(b0, Vector128.Create(tri.E2));
        didHit = Vector128.BitwiseAnd(didHit, Vector128.GreaterThanOrEqual(b0, epsilons));

        Vector128<float> b1 = Vector128.Multiply(Vector128.Create(tri.E3), hitc1);
        b1 = Vector128.Add(b1, Vector128.Multiply(Vector128.Create(tri.E4), hitc2));
        b1 = Vector128.Add(b1, Vector128.Create(tri.E5));
        didHit = Vector128.BitwiseAnd(didHit, Vector128.GreaterThanOrEqual(b1, epsilons));

        // The third edge, without a third equation and without a divide: the
        // two stored equations are pre-scaled to read 1 at the opposite
        // vertex, so inside means their sum is at most 1.
        Vector128<float> b2 = Vector128.Add(b1, b0);
        didHit = Vector128.BitwiseAnd(didHit, Vector128.LessThanOrEqual(b2, Vector128.Create(1.0f)));

        if (SseLanes.MoveMask(didHit) == 0)
        {
            return;
        }

        Vector128<float> replicated = Vector128.Create(tnum).AsSingle();
        hitIds = Vector128.BitwiseOr(Vector128.BitwiseAnd(replicated, didHit), SseLanes.AndNot(didHit, hitIds));
        hitDistance = Vector128.BitwiseOr(Vector128.BitwiseAnd(isectT, didHit), SseLanes.AndNot(didHit, hitDistance));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Pair(float a, float b) =>
        Vector256.Create(Vector128.Create(a), Vector128.Create(b));

    /// <summary>
    /// <see cref="IntersectTriangle"/> for triangle A and then triangle B, in
    /// one eight-lane pass: A in the low four lanes, B in the high four.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every lane runs <see cref="IntersectTriangle"/>'s operations in its
    /// order, so each triangle's plane distance, edge values and verdict are
    /// the four-lane ones bit for bit. The one thing a pass over both cannot do
    /// directly is B's "nearer than the best so far", which in sequence is
    /// against the best AFTER A. That is restored in the merge: both are first
    /// tested against the best before A, then B is kept only where
    /// <c>tB &lt; best-after-A</c> -- which, where A did not hit, is the test B
    /// already passed, and where A did is <c>tB &lt; tA</c>. An equal distance
    /// therefore stays with A, as it does when A is tested first.
    /// </para>
    /// <para>
    /// This halves the arithmetic of the triangle tests, 16 to 45 of which
    /// a real vrad packet makes (p5-trace's counters on 2fort and ss_sandbox).
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IntersectPair(
        ref KdTriangle a,
        int anum,
        ref KdTriangle b,
        int bnum,
        ref Axes origin,
        ref Axes direction,
        ref Vector128<float> hitDistance,
        ref Vector128<float> hitIds)
    {
        Vector256<float> ox = Vector256.Create(origin.X, origin.X);
        Vector256<float> oy = Vector256.Create(origin.Y, origin.Y);
        Vector256<float> oz = Vector256.Create(origin.Z, origin.Z);
        Vector256<float> dx = Vector256.Create(direction.X, direction.X);
        Vector256<float> dy = Vector256.Create(direction.Y, direction.Y);
        Vector256<float> dz = Vector256.Create(direction.Z, direction.Z);
        Vector256<float> nx = Pair(a.Nx, b.Nx);
        Vector256<float> ny = Pair(a.Ny, b.Ny);
        Vector256<float> nz = Pair(a.Nz, b.Nz);

        // Dot's order: x first, then y added to it, then z.
        Vector256<float> ddotn = Avx.Multiply(dx, nx);
        ddotn = Avx.Add(Avx.Multiply(dy, ny), ddotn);
        ddotn = Avx.Add(Avx.Multiply(dz, nz), ddotn);

        Vector256<float> epsilons = Vector256.Create(1.0e-10f);
        Vector256<float> didHit = Avx.Or(
            Avx.CompareGreaterThan(ddotn, epsilons),
            Avx.CompareLessThan(ddotn, Vector256.Create(-1.0e-10f)));

        Vector256<float> odotn = Avx.Multiply(ox, nx);
        odotn = Avx.Add(Avx.Multiply(oy, ny), odotn);
        odotn = Avx.Add(Avx.Multiply(oz, nz), odotn);
        Vector256<float> isectT = Avx.Divide(Avx.Subtract(Pair(a.D, b.D), odotn), ddotn);

        didHit = Avx.And(didHit, Avx.CompareGreaterThan(isectT, epsilons));
        didHit = Avx.And(didHit, Avx.CompareLessThan(isectT, Vector256.Create(hitDistance, hitDistance)));

        if (Avx.MoveMask(didHit) == 0)
        {
            return;
        }

        Vector256<float> hitc1 = Avx.Add(
            Vector256.Create(Unsafe.Add(ref origin.X, a.CoordSelect0), Unsafe.Add(ref origin.X, b.CoordSelect0)),
            Avx.Multiply(
                isectT,
                Vector256.Create(Unsafe.Add(ref direction.X, a.CoordSelect0), Unsafe.Add(ref direction.X, b.CoordSelect0))));
        Vector256<float> hitc2 = Avx.Add(
            Vector256.Create(Unsafe.Add(ref origin.X, a.CoordSelect1), Unsafe.Add(ref origin.X, b.CoordSelect1)),
            Avx.Multiply(
                isectT,
                Vector256.Create(Unsafe.Add(ref direction.X, a.CoordSelect1), Unsafe.Add(ref direction.X, b.CoordSelect1))));

        Vector256<float> b0 = Avx.Multiply(Pair(a.E0, b.E0), hitc1);
        b0 = Avx.Add(b0, Avx.Multiply(Pair(a.E1, b.E1), hitc2));
        b0 = Avx.Add(b0, Pair(a.E2, b.E2));
        didHit = Avx.And(didHit, Avx.CompareGreaterThanOrEqual(b0, epsilons));

        Vector256<float> b1 = Avx.Multiply(Pair(a.E3, b.E3), hitc1);
        b1 = Avx.Add(b1, Avx.Multiply(Pair(a.E4, b.E4), hitc2));
        b1 = Avx.Add(b1, Pair(a.E5, b.E5));
        didHit = Avx.And(didHit, Avx.CompareGreaterThanOrEqual(b1, epsilons));

        Vector256<float> b2 = Avx.Add(b1, b0);
        didHit = Avx.And(didHit, Avx.CompareLessThanOrEqual(b2, Vector256.Create(1.0f)));

        if (Avx.MoveMask(didHit) == 0)
        {
            return;
        }

        Vector128<float> hitA = didHit.GetLower();
        Vector128<float> hitB = didHit.GetUpper();
        Vector128<float> tA = isectT.GetLower();
        Vector128<float> tB = isectT.GetUpper();

        Vector128<float> distanceA = Select(hitA, tA, hitDistance);
        Vector128<float> idsA = Select(hitA, Vector128.Create(anum).AsSingle(), hitIds);
        hitB = Vector128.BitwiseAnd(hitB, Vector128.LessThan(tB, distanceA));
        hitDistance = Select(hitB, tB, distanceA);
        hitIds = Select(hitB, Vector128.Create(bnum).AsSingle(), idsA);
    }

    /// <summary>
    /// <c>FourVectors::operator*</c>: the four-lane dot product, in stock's
    /// accumulation order.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> Dot(
        Vector128<float> ax, Vector128<float> ay, Vector128<float> az,
        Vector128<float> bx, Vector128<float> by, Vector128<float> bz)
    {
        Vector128<float> dot = Vector128.Multiply(ax, bx);
        dot = Vector128.Add(Vector128.Multiply(ay, by), dot);
        return Vector128.Add(Vector128.Multiply(az, bz), dot);
    }

    /// <summary>
    /// <c>ReciprocalSaturateSIMD</c>: zeros become
    /// <paramref name="zeroSubstitute"/>, keeping their sign bit, then
    /// <c>rcpps</c> with one Newton step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NOT a divide. Stock's reciprocal here is an ESTIMATE refined once, and
    /// its last bits differ from an exact <c>1/x</c>; since the result scales
    /// every node distance in the traversal, using a divide would move
    /// borderline rays onto the other side of a split.
    /// </para>
    /// <para>
    /// Stock's substitute is <c>Four_Epsilons</c>, <c>FLT_EPSILON</c>
    /// NOT the reference implementation's file-local
    /// <c>FourEpsilons</c> of 1e-10, which is a different constant used only
    /// by the plane test. This port used 1e-10 here until lane p5-trace;
    /// <see cref="StockQuirk.KdZeroDirectionReachCut"/> says why stock's
    /// value is a defect.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> ReciprocalSaturate(Vector128<float> a, Vector128<float> zeroSubstitute)
    {
        Vector128<float> zeroMask = Vector128.Equals(a, Vector128<float>.Zero);
        Vector128<float> safe = Vector128.BitwiseOr(a, Vector128.BitwiseAnd(zeroSubstitute, zeroMask));
        Vector128<float> est = FloatEstimate.Reciprocal(safe);

        // y(n+1) = 2*y(n) - a*y(n)^2
        return Vector128.Subtract(
            Vector128.Add(est, est), Vector128.Multiply(safe, Vector128.Multiply(est, est)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ClipAxis(
        float min,
        float max,
        Vector128<float> origin,
        Vector128<float> inv,
        ref Vector128<float> tmin,
        ref Vector128<float> tmax)
    {
        Vector128<float> isectMin = Vector128.Multiply(Vector128.Subtract(Vector128.Create(min), origin), inv);
        Vector128<float> isectMax = Vector128.Multiply(Vector128.Subtract(Vector128.Create(max), origin), inv);
        tmin = SseLanes.Max(tmin, SseLanes.Min(isectMin, isectMax));
        tmax = SseLanes.Min(tmax, SseLanes.Max(isectMin, isectMax));
    }

    /// <summary>A suspended subtree, with the ray span it was suspended at.</summary>
    private struct NodeToVisit
    {
        public int Node;
        public Vector128<float> TMin;
        public Vector128<float> TMax;
    }

    /// <summary>Four rays, as six four-lane components.</summary>
    private struct RayPacket
    {
        public Vector128<float> Ox;
        public Vector128<float> Oy;
        public Vector128<float> Oz;
        public Vector128<float> Dx;
        public Vector128<float> Dy;
        public Vector128<float> Dz;
    }

    /// <summary>Three per-axis vectors, laid out to be indexed by axis.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Axes
    {
        public Vector128<float> X;
        public Vector128<float> Y;
        public Vector128<float> Z;
    }

    /// <summary>
    /// One batch's traversal stack and mailbox, on the caller's stack.
    /// </summary>
    /// <remarks>
    /// Mailbox slots hold <c>generation &lt;&lt; 32 | triangle</c>. Every slot
    /// starts at -1, which no generation reaches within a batch (a generation is
    /// one traversal), so a fresh batch reads as empty.
    /// </remarks>
    private ref struct Scratch
    {
        public Span<NodeToVisit> Stack;
        public Span<long> Mailbox;
        public int Generation;

        public Scratch(Span<NodeToVisit> stack, Span<long> mailbox)
        {
            Stack = stack;
            Mailbox = mailbox;
            Mailbox.Fill(-1L);
            Generation = 0;
        }
    }
}

/// <summary>Four floats, addressable by lane.</summary>
[System.Runtime.CompilerServices.InlineArray(4)]
internal struct FourFloats
{
    private float _element0;
}

/// <summary>Four ints, addressable by lane.</summary>
[System.Runtime.CompilerServices.InlineArray(4)]
internal struct FourInts
{
    private int _element0;
}

/// <summary>Turning an inline four into a vector without a copy through memory.</summary>
internal static class FourFloatsExtensions
{
    /// <summary>The four lanes as one vector.</summary>
    /// <param name="f">The lanes.</param>
    /// <returns>A vector over the same four floats.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> AsVector(this in FourFloats f) =>
        Vector128.Create(f[0], f[1], f[2], f[3]);
}
