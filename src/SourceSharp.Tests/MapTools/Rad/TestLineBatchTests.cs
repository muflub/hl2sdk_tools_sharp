//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad;

/// <summary>
/// <see cref="TestLineBatch"/>: one tracer call per distinct options, the
/// answers scattered back to the order the segments were added, and each
/// the answer <c>KdRayTracer.TestLines</c> gives the segment alone.
/// </summary>
public sealed class TestLineBatchTests
{
    private const int PropId = TraceId.StaticProp | 3;

    private static readonly KdRayTracer Tracer = KdRayTracer.Build(
    [
        new TracedTriangle(TraceId.Opaque, new Vec3(-200, -200, 0), new Vec3(200, -200, 0), new Vec3(0, 200, 0), 0),
        new TracedTriangle(TraceId.Opaque, new Vec3(50, -100, -50), new Vec3(50, 100, -50), new Vec3(50, 0, 150), 0),
        new TracedTriangle(TraceId.Sky, new Vec3(-300, -300, 120), new Vec3(300, -300, 120), new Vec3(0, 300, 120), 0),
        new TracedTriangle(PropId, new Vec3(-60, -20, 20), new Vec3(-60, 20, 20), new Vec3(-60, 0, 80), 0),
    ]);

    private static readonly RayTraceOptions[] Kinds =
    [
        RayTraceOptions.TestLine(),
        RayTraceOptions.TestLine(PropId),
        RayTraceOptions.TestLine(skyDoesNotBlock: true),
        RayTraceOptions.TestLine(PropId, skyDoesNotBlock: true),
    ];

    private static bool Direct(Vec3 start, Vec3 end, RayTraceOptions options)
    {
        Span<bool> blocked = [false];
        Tracer.TestLines([start], [end], blocked, stockReciprocal: true, options.SkyDoesNotBlock, options.SkipId ?? -1);
        return blocked[0];
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedSegmentsAreOneCallPerKindAndAnswerAsAlone(bool asynchronous)
    {
        // Held until the calls in flight are counted: otherwise an
        // asynchronous batch may already have finished, and rightly not be
        // handed back as pending.
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CountingRayTracer counting = new(Tracer, asynchronous, release: release.Task);
        TestLineBatch batch = new(counting);
        Random random = new(17);
        List<(int Index, Vec3 Start, Vec3 End, RayTraceOptions Options)> added = [];
        for (int i = 0; i < 700; i++)
        {
            Vec3 start = new((float)(random.NextDouble() * 300) - 150, (float)(random.NextDouble() * 300) - 150, (float)(random.NextDouble() * 200) - 50);
            Vec3 end = new((float)(random.NextDouble() * 300) - 150, (float)(random.NextDouble() * 300) - 150, (float)(random.NextDouble() * 200) - 50);
            RayTraceOptions options = Kinds[random.Next(Kinds.Length)];
            added.Add((batch.Add(Ray.Segment(start, end, true), options, i), start, end, options));
        }

        List<Task> pending = [];
        batch.BeginTrace(pending, CancellationToken.None);
        Assert.Equal(asynchronous ? Kinds.Length : 0, pending.Count);
        release.SetResult();
        await Task.WhenAll(pending);
        batch.EndTrace();

        Assert.Equal(Kinds.Length, counting.VisibilityCalls.Count);
        Assert.Equal(700, counting.VisibilityCalls.Sum(c => c.Rays));
        Assert.Equal(Kinds.Length, counting.VisibilityCalls.Select(c => c.Options).Distinct().Count());
        foreach ((int index, Vec3 start, Vec3 end, RayTraceOptions options) in added)
        {
            Assert.Equal(Direct(start, end, options), batch.IsBlocked(index));
            Assert.Equal(index, batch.Payload(index));
        }

        Assert.Contains(added, a => batch.IsBlocked(a.Index));
        Assert.Contains(added, a => !batch.IsBlocked(a.Index));
    }

    [Fact]
    public void OneKindTracesTheRaysWhereTheyAre()
    {
        CountingRayTracer counting = new(Tracer);
        TestLineBatch batch = new(counting);
        int blocked = batch.Add(Ray.Segment(new Vec3(0, 0, 10), new Vec3(0, 0, -10), false), RayTraceOptions.TestLine());
        int open = batch.Add(Ray.Segment(new Vec3(0, 0, 10), new Vec3(0, 0, 50), false), RayTraceOptions.TestLine());

        batch.Trace(CancellationToken.None);

        Assert.Single(counting.VisibilityCalls);
        Assert.True(batch.IsBlocked(blocked));
        Assert.False(batch.IsBlocked(open));
    }

    [Fact]
    public void AnEmptyBatchAsksNothing()
    {
        CountingRayTracer counting = new(Tracer);
        TestLineBatch batch = new(counting);

        batch.Trace(CancellationToken.None);

        Assert.Empty(counting.VisibilityCalls);
        Assert.Equal(0, batch.Count);
    }

    [Fact]
    public void ClearingKeepsTheBatchUsable()
    {
        CountingRayTracer counting = new(Tracer);
        TestLineBatch batch = new(counting);
        batch.Add(Ray.Segment(new Vec3(0, 0, 10), new Vec3(0, 0, -10), false), RayTraceOptions.TestLine(PropId));
        batch.Trace(CancellationToken.None);
        batch.Clear();

        int open = batch.Add(Ray.Segment(new Vec3(0, 0, 10), new Vec3(0, 0, 50), false), RayTraceOptions.TestLine());
        batch.Trace(CancellationToken.None);

        Assert.Equal(1, batch.Count);
        Assert.False(batch.IsBlocked(open));
        Assert.Equal([PropId, null], counting.VisibilityCalls.Select(c => c.Options.SkipId));
    }

    [Fact]
    public void TheBatchGrowsPastItsFirstStorage()
    {
        TestLineBatch batch = new(Tracer);
        for (int i = 0; i < 1000; i++)
        {
            batch.Add(Ray.Segment(new Vec3(0, 0, 10), new Vec3(0, 0, i % 2 == 0 ? -10 : 50), false), RayTraceOptions.TestLine(), i);
        }

        batch.Trace(CancellationToken.None);

        Assert.All(Enumerable.Range(0, 1000), i => Assert.Equal(i % 2 == 0, batch.IsBlocked(i)));
        Assert.Equal(999, batch.Payload(999));
    }

    [Fact]
    public void AddingAfterTracingIsRefused()
    {
        TestLineBatch batch = new(Tracer);
        batch.Trace(CancellationToken.None);

        Assert.Throws<InvalidOperationException>(() => batch.Add(Ray.Segment(Vec3.Zero, new Vec3(1, 0, 0), false), RayTraceOptions.TestLine()));
        Assert.Throws<InvalidOperationException>(() => batch.Trace(CancellationToken.None));
    }

    [Fact]
    public void AnswersBeforeTracingOrOutOfRangeAreRefused()
    {
        TestLineBatch batch = new(Tracer);
        int i = batch.Add(Ray.Segment(Vec3.Zero, new Vec3(1, 0, 0), false), RayTraceOptions.TestLine());

        Assert.Throws<InvalidOperationException>(() => batch.IsBlocked(i));
        batch.Trace(CancellationToken.None);
        Assert.Throws<ArgumentOutOfRangeException>(() => batch.IsBlocked(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => batch.IsBlocked(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => batch.Payload(1));
    }

    [Fact]
    public void OptionsTheTracerCannotHonourAreRefusedNotTraced()
    {
        CountingRayTracer plain = new(Tracer, plainOnly: true);
        TestLineBatch batch = new(plain);
        batch.Add(Ray.Segment(Vec3.Zero, new Vec3(1, 0, 0), false), RayTraceOptions.TestLine(PropId));

        List<Task> pending = [];
        Assert.Throws<NotSupportedException>(() => batch.BeginTrace(pending, CancellationToken.None));
        Assert.Empty(plain.VisibilityCalls);
        Assert.Empty(pending);
    }

    [Fact]
    public void AFailedSynchronousBatchRethrowsItsFailure()
    {
        TestLineBatch batch = new(new Failing());
        batch.Add(Ray.Segment(Vec3.Zero, new Vec3(1, 0, 0), false), RayTraceOptions.TestLine());

        Assert.Throws<IOException>(() => batch.Trace(CancellationToken.None));
    }

    [Fact]
    public void ACancelledTaskIsReportedAsCancellation()
    {
        using CancellationTokenSource source = new();
        source.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            TestLineBatch.RethrowIfFailed(Task.FromCanceled(source.Token), source.Token));
        TestLineBatch.RethrowIfFailed(Task.CompletedTask, CancellationToken.None);
    }

    [Fact]
    public async Task AnAsynchronousTracerIsNeverWaitedForSynchronously()
    {
        // Trace refuses to block on a batch in flight; the staged driver
        // parks instead. The call it started still completes on its own.
        // Held until after the check: a batch that finished on its own
        // thread first would leave nothing to refuse.
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CountingRayTracer counting = new(Tracer, asynchronous: true, release: release.Task);
        TestLineBatch batch = new(counting);
        batch.Add(Ray.Segment(Vec3.Zero, new Vec3(1, 0, 0), false), RayTraceOptions.TestLine());

        Assert.Throws<InvalidOperationException>(() => batch.Trace(CancellationToken.None));
        Assert.Single(counting.VisibilityCalls);
        release.SetResult();
        await Task.Yield();
    }

    [Fact]
    public async Task AFailedAsynchronousBatchFailsItsTask()
    {
        TestLineBatch batch = new(new CountingRayTracer(new Failing(), asynchronous: true));
        batch.Add(Ray.Segment(Vec3.Zero, new Vec3(1, 0, 0), false), RayTraceOptions.TestLine());
        List<Task> pending = [];

        batch.BeginTrace(pending, CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => Assert.Single(pending));
    }

    [Fact]
    public void EndingATraceThatWasNotBegunIsRefused()
    {
        TestLineBatch batch = new(Tracer);

        Assert.Throws<InvalidOperationException>(batch.EndTrace);
        batch.Trace(CancellationToken.None);
        Assert.Throws<InvalidOperationException>(batch.EndTrace);
        Assert.Throws<ArgumentNullException>(() => new TestLineBatch(Tracer).BeginTrace(null!, CancellationToken.None));
    }

    [Fact]
    public void ACancelledBatchThrows()
    {
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        TestLineBatch batch = new(Tracer);
        batch.Add(Ray.Segment(Vec3.Zero, new Vec3(1, 0, 0), false), RayTraceOptions.TestLine());

        Assert.Throws<OperationCanceledException>(() => batch.Trace(cancelled.Token));
    }

    [Fact]
    public void ANullTracerIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => new TestLineBatch(null!));
    }

    private sealed class Failing : IRayTracer
    {
        public string TracerIdentity => "failing";

        public bool Supports(RayTraceOptions options) => true;

        public ValueTask TraceVisibilityAsync(
            ReadOnlyMemory<Ray> rays, Memory<ulong> hitBits, RayTraceOptions options, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("device lost"));

        public ValueTask TraceClosestAsync(
            ReadOnlyMemory<Ray> rays, Memory<HitId> hits, RayTraceOptions options, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("device lost"));
    }
}
