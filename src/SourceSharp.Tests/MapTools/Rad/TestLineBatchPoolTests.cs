//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Bounce;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad;

/// <summary>
/// <see cref="TestLineBatch"/>'s pooled storage: arrays rented, reused and
/// returned exactly once, and never an answer that depends on what an
/// earlier renter left in them.
/// </summary>
public sealed class TestLineBatchPoolTests
{
    private const int PropId = TraceId.StaticProp | 3;

    private static readonly KdRayTracer Tracer = KdRayTracer.Build(
    [
        new TracedTriangle(TraceId.Opaque, new Vec3(-200, -200, 0), new Vec3(200, -200, 0), new Vec3(0, 200, 0), 0),
        new TracedTriangle(TraceId.Opaque, new Vec3(50, -100, -50), new Vec3(50, 100, -50), new Vec3(50, 0, 150), 0),
        new TracedTriangle(TraceId.Sky, new Vec3(-300, -300, 120), new Vec3(300, -300, 120), new Vec3(0, 300, 120), 0),
        new TracedTriangle(PropId, new Vec3(-60, -20, 20), new Vec3(-60, 20, 20), new Vec3(-60, 0, 80), 0),
    ]);

    /// <summary>One batch's segments: a seeded mix of kinds, some blocked and some not.</summary>
    private static List<(Ray Ray, RayTraceOptions Options, float Payload)> Segments(int count, int seed, bool mixed)
    {
        Random random = new(seed);
        List<(Ray, RayTraceOptions, float)> segments = [];
        for (int i = 0; i < count; i++)
        {
            Vec3 start = new(random.Next(-150, 150), random.Next(-150, 150), random.Next(1, 100));
            Vec3 end = new(random.Next(-150, 150), random.Next(-150, 150), random.Next(-80, 200));
            RayTraceOptions options = !mixed ? RayTraceOptions.TestLine() : (i % 3) switch
            {
                0 => RayTraceOptions.TestLine(),
                1 => RayTraceOptions.TestLine(PropId),
                _ => RayTraceOptions.TestLine(skyDoesNotBlock: true),
            };
            segments.Add((Ray.Segment(start, end, true), options, i * 0.5f));
        }

        return segments;
    }

    /// <summary>What a batch answers for the segments: blocked flags and payloads, in order.</summary>
    private static (bool[] Blocked, float[] Payload) Answer(
        TestLineBatch batch, List<(Ray Ray, RayTraceOptions Options, float Payload)> segments)
    {
        batch.Clear();
        foreach ((Ray ray, RayTraceOptions options, float payload) in segments)
        {
            batch.Add(ray, options, payload);
        }

        batch.Trace(CancellationToken.None);
        return ([.. Enumerable.Range(0, segments.Count).Select(batch.IsBlocked)],
                [.. Enumerable.Range(0, segments.Count).Select(batch.Payload)]);
    }

    /// <summary>The same segments' answers from a batch of its own over plain new arrays.</summary>
    private static (bool[] Blocked, float[] Payload) Fresh(List<(Ray Ray, RayTraceOptions Options, float Payload)> segments)
    {
        using TestLineBatch fresh = new(Tracer, new FreshPool());
        return Answer(fresh, segments);
    }

    /// <summary>
    /// A larger batch after a smaller one, then a smaller after a larger, on
    /// one reused batch over a pool that poisons and recycles: every answer
    /// is what a fresh batch gives, so nothing past the used length -- the
    /// last batch's rays, bits or flags -- leaks in.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReusedStorageAnswersAsFreshWhateverTheOrderOfSizes(bool mixed)
    {
        RecyclingScratchPool pool = new();
        using TestLineBatch reused = new(Tracer, pool);
        int[] sizes = [3, 700, 5, 1_300, 64, 65, 1, 0, 200];

        for (int k = 0; k < sizes.Length; k++)
        {
            List<(Ray, RayTraceOptions, float)> segments = Segments(sizes[k], seed: 100 + k, mixed);
            (bool[] blocked, float[] payload) = Answer(reused, segments);
            (bool[] expectedBlocked, float[] expectedPayload) = Fresh(segments);

            Assert.Equal(expectedBlocked, blocked);
            Assert.Equal(expectedPayload, payload);
            if (sizes[k] > 50)
            {
                Assert.Contains(true, blocked);
                Assert.Contains(false, blocked);
            }
        }

        Assert.True(pool.Returned > 0, "growing should hand the outgrown arrays back");
        Assert.Equal(0, pool.BadReturns);
    }

    /// <summary>Two batches over one pool, used by two threads at once, answer as fresh batches do.</summary>
    [Fact]
    public async Task BatchesOnConcurrentWorkersShareNoStorage()
    {
        RecyclingScratchPool pool = new();
        List<(Ray, RayTraceOptions, float)>[] work =
            [.. Enumerable.Range(0, 24).Select(k => Segments(40 + (k * 97 % 900), seed: k, mixed: k % 2 == 0))];
        (bool[], float[])[] expected = [.. work.Select(Fresh)];

        async Task<(bool[], float[])[]> Worker(int first)
        {
            await Task.Yield();
            using TestLineBatch batch = new(Tracer, pool);
            List<(bool[], float[])> answers = [];
            for (int k = first; k < work.Length; k += 4)
            {
                answers.Add(Answer(batch, work[k]));
            }

            return [.. answers];
        }

        (bool[], float[])[][] results = await Task.WhenAll(Enumerable.Range(0, 4).Select(w => Task.Run(() => Worker(w))));

        for (int w = 0; w < 4; w++)
        {
            for (int j = 0, k = w; k < work.Length; j++, k += 4)
            {
                Assert.Equal(expected[k].Item1, results[w][j].Item1);
                Assert.Equal(expected[k].Item2, results[w][j].Item2);
            }
        }

        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, pool.BadReturns);
    }

    [Fact]
    public void DisposingReturnsEveryArrayOnceAndRefusesFurtherUse()
    {
        RecyclingScratchPool pool = new();
        TestLineBatch batch = new(Tracer, pool);
        Answer(batch, Segments(300, seed: 5, mixed: true));
        Assert.True(pool.Outstanding > 0);

        batch.Dispose();
        batch.Dispose();

        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, pool.BadReturns);
        Assert.Equal(pool.Rented, pool.Returned);
        Assert.Equal(0, batch.Count);
        Assert.Throws<ObjectDisposedException>(() => batch.Add(Ray.Segment(Vec3.Zero, new Vec3(1, 0, 0), false), RayTraceOptions.TestLine()));
        Assert.Throws<ObjectDisposedException>(() => batch.BeginTrace([], CancellationToken.None));
    }

    [Fact]
    public void ABatchThatNeverAddsRentsNothing()
    {
        RecyclingScratchPool pool = new();
        TestLineBatch batch = new(Tracer, pool);

        batch.Trace(CancellationToken.None);
        batch.Dispose();

        Assert.Equal(0, pool.Rented);
        Assert.Equal(0, pool.BadReturns);
    }

    /// <summary>
    /// A call still in flight when the batch is disposed keeps the arrays: it
    /// may still read the rays or write the bits, so they go to the collector,
    /// never back to a pool that would lend them out meanwhile.
    /// </summary>
    [Fact]
    public async Task StorageACallStillReadsIsNeverReturned()
    {
        RecyclingScratchPool pool = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TestLineBatch batch = new(new CountingRayTracer(Tracer, asynchronous: true, release: release.Task), pool);
        batch.Add(Ray.Segment(Vec3.Zero, new Vec3(1, 0, 0), false), RayTraceOptions.TestLine());
        List<Task> pending = [];
        batch.BeginTrace(pending, CancellationToken.None);
        Task call = Assert.Single(pending);

        batch.Dispose();

        Assert.Equal(0, pool.Returned);
        release.SetResult();
        await call;
        Assert.Equal(0, pool.Returned);
        Assert.Equal(0, pool.BadReturns);
    }

    /// <summary>Once its calls have completed, a batch that was traced and never ended returns everything.</summary>
    [Fact]
    public async Task StorageOfACompletedCallIsReturnedWithoutEndingTheTrace()
    {
        RecyclingScratchPool pool = new();
        TestLineBatch batch = new(new CountingRayTracer(Tracer, asynchronous: true), pool);
        batch.Add(Ray.Segment(Vec3.Zero, new Vec3(1, 0, 0), false), RayTraceOptions.TestLine());
        List<Task> pending = [];
        batch.BeginTrace(pending, CancellationToken.None);
        await Assert.Single(pending);

        batch.Dispose();

        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(pool.Rented, pool.Returned);
    }

    /// <summary>
    /// A rental that fails while the batch grows gives back what the growth
    /// had already rented, and leaves the batch as it was: its segments
    /// still trace, and disposing it returns the rest.
    /// </summary>
    /// <param name="failAt">The 1-based rental that fails.</param>
    /// <param name="heldAfter">The arrays the batch holds once it has failed.</param>
    /// <remarks>
    /// The segments are of mixed kinds with payloads, so the batch holds all
    /// three segment arrays. Rental 1 is the rays; the second segment brings
    /// the second kind (rental 2, the kinds) and the first payload that is
    /// not zero (rental 3); the first growth, when the rays array is full,
    /// rents the three again (4 to 6). A failure among 2 and 3 is the lazy
    /// start of an array, and leaves the batch holding what it had before.
    /// </remarks>
    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(5, 3)]
    [InlineData(6, 3)]
    public void AFailedRentalWhileGrowingLeaksNothing(int failAt, int heldAfter)
    {
        RecyclingScratchPool pool = new() { FailRentNumber = failAt };
        TestLineBatch batch = new(Tracer, pool);
        List<(Ray Ray, RayTraceOptions Options, float Payload)> segments = Segments(300, seed: 9, mixed: true);
        int added = 0;
        OutOfMemoryException? failure = null;
        for (; added < segments.Count && failure is null; added++)
        {
            failure = Record.Exception(() => batch.Add(segments[added].Ray, segments[added].Options, segments[added].Payload))
                as OutOfMemoryException;
        }

        Assert.NotNull(failure);
        int kept = added - 1;
        Assert.Equal(kept, batch.Count);
        Assert.Equal(heldAfter, pool.Outstanding);
        batch.Trace(CancellationToken.None);
        (bool[] blocked, float[] payload) = Fresh(segments.GetRange(0, kept));
        Assert.Equal(blocked, Enumerable.Range(0, kept).Select(batch.IsBlocked));
        Assert.Equal(payload, Enumerable.Range(0, kept).Select(batch.Payload));
        batch.Dispose();
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, pool.BadReturns);
    }

    /// <summary>
    /// A reservation is one rental of each array at the size asked, keeping
    /// what was added; filling up to it rents nothing more, a smaller one
    /// does nothing, and the answers are a fresh batch's.
    /// </summary>
    [Fact]
    public void AReservationIsOneRentalAndKeepsTheSegments()
    {
        RecyclingScratchPool pool = new();
        using TestLineBatch batch = new(Tracer, pool);
        List<(Ray Ray, RayTraceOptions Options, float Payload)> segments = Segments(1_000, seed: 21, mixed: false);
        for (int i = 0; i < 10; i++)
        {
            batch.Add(segments[i].Ray, segments[i].Options, segments[i].Payload);
        }

        Assert.Equal(1, pool.RentedOf<Ray>());
        batch.Reserve(1_000);
        batch.Reserve(500);
        Assert.Equal(2, pool.RentedOf<Ray>());
        for (int i = 10; i < 1_000; i++)
        {
            batch.Add(segments[i].Ray, segments[i].Options, segments[i].Payload);
        }

        Assert.Equal(2, pool.RentedOf<Ray>());
        Assert.Equal(2, pool.RentedOf<float>());
        batch.Trace(CancellationToken.None);
        (bool[] blocked, float[] payload) = Fresh(segments);
        Assert.Equal(blocked, Enumerable.Range(0, 1_000).Select(batch.IsBlocked));
        Assert.Equal(payload, Enumerable.Range(0, 1_000).Select(batch.Payload));
    }

    [Fact]
    public void ReservingZeroRentsNothing()
    {
        using CompileScratchPool pool = new();
        using TestLineBatch batch = new(Tracer, pool);

        batch.Reserve(0);

        Assert.Equal(0, pool.Allocations);
    }

    /// <summary>
    /// Growth edges over the compile's pool: filling the first rental exactly
    /// rents nothing more, one segment past it grows once and hands the
    /// outgrown array back, and a reservation exactly at the capacity does
    /// nothing.
    /// </summary>
    [Fact]
    public void ExactlyAtCapacityRentsNothingAndOnePastGrowsOnce()
    {
        using CompileScratchPool pool = new();
        using TestLineBatch batch = new(Tracer, pool);
        List<(Ray Ray, RayTraceOptions Options, float Payload)> segments = Segments(65, seed: 3, mixed: false);
        for (int i = 0; i < 64; i++)
        {
            batch.Add(segments[i].Ray, segments[i].Options);
        }

        batch.Reserve(64);
        Assert.Equal(1, pool.Allocations);
        Assert.Equal(0, pool.IdleArrays);

        batch.Add(segments[64].Ray, segments[64].Options);
        Assert.Equal(2, pool.Allocations);
        Assert.Equal(1, pool.IdleArrays);
        batch.Trace(CancellationToken.None);
        Assert.Equal(
            Fresh([.. segments.Select(s => (s.Ray, s.Options, 0.0f))]).Blocked,
            Enumerable.Range(0, 65).Select(batch.IsBlocked));
    }

    /// <summary>
    /// A longer idle array the pool lends is used whole: the batch's
    /// capacity is the array's length, not the length it asked for, so it
    /// does not grow again until that is full.
    /// </summary>
    [Fact]
    public void ALongerIdleArrayIsUsedWhole()
    {
        using CompileScratchPool pool = new();
        pool.Return(pool.Rent<Ray>(1_000));
        using TestLineBatch batch = new(Tracer, pool);
        List<(Ray Ray, RayTraceOptions Options, float Payload)> segments = Segments(1_001, seed: 4, mixed: false);

        for (int i = 0; i < 1_000; i++)
        {
            batch.Add(segments[i].Ray, segments[i].Options);
        }

        Assert.Equal(1, pool.Allocations);
        batch.Add(segments[1_000].Ray, segments[1_000].Options);
        Assert.Equal(2, pool.Allocations);
    }

    /// <summary>
    /// A batch of one kind whose payloads are all zero keeps only its rays
    /// and the tracer's hit bits: no kinds, no payloads, no blocked flags.
    /// </summary>
    [Fact]
    public void OneKindWithoutPayloadsKeepsOnlyTheRays()
    {
        RecyclingScratchPool pool = new();
        using TestLineBatch batch = new(Tracer, pool);
        List<(Ray Ray, RayTraceOptions Options, float Payload)> segments =
            [.. Segments(500, seed: 6, mixed: false).Select(s => (s.Ray, s.Options, 0.0f))];

        (bool[] blocked, float[] payload) = Answer(batch, segments);

        Assert.Equal(Fresh(segments), (blocked, payload), new AnswerComparer());
        Assert.Equal(0, pool.RentedOf<int>());
        Assert.Equal(0, pool.RentedOf<float>());
        Assert.Equal(0, pool.RentedOf<bool>());
        Assert.True(pool.RentedOf<Ray>() > 0);
        Assert.True(pool.RentedOf<ulong>() > 0);
    }

    /// <summary>
    /// A payload or a second kind that turns up part-way through a batch, in
    /// arrays full of junk: the segments before it read payload 0 and kind 0,
    /// a payload of -0 comes back as -0, and every answer is a fresh batch's.
    /// </summary>
    [Fact]
    public void APayloadOrKindThatTurnsUpMidBatchLeavesTheEarlierSegmentsAtZero()
    {
        RecyclingScratchPool pool = new();
        using TestLineBatch batch = new(Tracer, pool);

        // Junk in the pool first: a batch that used payloads and kinds.
        Answer(batch, Segments(300, seed: 7, mixed: true));
        List<(Ray Ray, RayTraceOptions Options, float Payload)> plain = Segments(200, seed: 8, mixed: false);
        List<(Ray Ray, RayTraceOptions Options, float Payload)> later = Segments(100, seed: 9, mixed: true);
        List<(Ray, RayTraceOptions, float)> segments =
        [
            .. plain.Select(s => (s.Ray, s.Options, 0.0f)),
            (later[0].Ray, later[0].Options, -0.0f),
            .. later.Skip(1),
        ];

        (bool[] blocked, float[] payload) = Answer(batch, segments);

        Assert.Equal(Fresh(segments), (blocked, payload), new AnswerComparer());
        Assert.All(payload.Take(200), p => Assert.Equal(0, BitConverter.SingleToInt32Bits(p)));
        Assert.Equal(BitConverter.SingleToInt32Bits(-0.0f), BitConverter.SingleToInt32Bits(payload[200]));
        Assert.Equal(0, pool.BadReturns);
    }

    /// <summary>Answers compared element by element, payloads by their bits.</summary>
    private sealed class AnswerComparer : IEqualityComparer<(bool[] Blocked, float[] Payload)>
    {
        public bool Equals((bool[] Blocked, float[] Payload) x, (bool[] Blocked, float[] Payload) y) =>
            x.Blocked.SequenceEqual(y.Blocked)
            && x.Payload.Select(BitConverter.SingleToInt32Bits).SequenceEqual(y.Payload.Select(BitConverter.SingleToInt32Bits));

        public int GetHashCode((bool[] Blocked, float[] Payload) obj) => obj.Blocked.Length;
    }

    [Fact]
    public void BadReservationsAreRefused()
    {
        TestLineBatch batch = new(Tracer, new RecyclingScratchPool());

        Assert.Throws<ArgumentOutOfRangeException>(() => batch.Reserve(-1));
        batch.Trace(CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => batch.Reserve(10));
        batch.Dispose();
        Assert.Throws<ObjectDisposedException>(() => batch.Reserve(10));
    }

    [Fact]
    public void ANullPoolIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => new TestLineBatch(Tracer, null!));
    }

    /// <summary>A pool that always allocates and never reuses: the fresh-buffer baseline.</summary>
    private sealed class FreshPool : IScratchArrayPool
    {
        public T[] Rent<T>(int minimumLength) => new T[minimumLength];

        public void Return<T>(T[] array)
        {
        }
    }
}
