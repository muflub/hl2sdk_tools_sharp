//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Light;

/// <summary>
/// <see cref="LightRayLog"/> and its tape over rented storage, and the face
/// lighting whose workers rent it: the same rays, the same replay and the
/// same light as plain arrays give, whatever was in the arrays before, and
/// every array back when the stage ends.
/// </summary>
public sealed class LightRayLogPoolTests
{
    /// <summary>
    /// Everything one batch records -- rays of every kind, deferred sky tests,
    /// tape words -- and everything its replay reads back, answered by a
    /// seeded stand-in tracer that writes every answer it is handed.
    /// </summary>
    private static List<string> RecordAndReplay(LightRayLog log, int n, int seed)
    {
        Random random = new(seed);
        Vec3 Next() => new(random.Next(-500, 500), random.Next(-500, 500), random.Next(-500, 500));
        List<string> seen = [];

        log.Reset();
        for (int i = 0; i < n; i++)
        {
            log.EmitVisibility(Next(), Next());
            log.EmitSky(Next(), Next());
            log.Tape.Int(random.Next());
            log.Tape.Float(random.NextSingle());
            if (i % 3 == 0)
            {
                log.EmitSky2(Next(), Next());
            }

            if (i % 4 == 0)
            {
                Vec3[] start = [Next(), Next(), Next(), Next()];
                Vec3[] stop = [Next(), Next(), Next(), Next()];
                log.Defer(i, 1 + (i % 4), i & 15, start, stop);
            }

            if (i % 7 == 0)
            {
                log.EndItem();
            }
        }

        log.EndSky2Block();
        seen.AddRange(log.VisibilityRays().ToArray().Select(r => r.ToString()));
        seen.AddRange(log.SkyRays().ToArray().Select(r => r.ToString()));
        seen.AddRange(log.Sky2Memory.ToArray().Select(r => r.ToString()));
        seen.AddRange(log.Deferred.ToArray().Select(d => d.ToString()));
        seen.AddRange(log.DeferredPoints.ToArray().Select(p => $"{p.X},{p.Y},{p.Z}"));

        // The tracer writes every word and every hit it is handed.
        (Memory<ulong> bits, Memory<HitId> hits) = log.FirstStageAnswers();
        for (int w = 0; w < bits.Length; w++)
        {
            bits.Span[w] = ((ulong)random.Next() << 32) | (uint)random.Next();
        }

        for (int h = 0; h < hits.Length; h++)
        {
            hits.Span[h] = new HitId(random.Next(2) == 0 ? TraceId.Sky : TraceId.Opaque, random.NextSingle() * 2);
        }

        Span<HitId> hits2 = log.SecondStageAnswers().Span;
        for (int h = 0; h < hits2.Length; h++)
        {
            hits2[h] = new HitId(random.Next(2) == 0 ? TraceId.Sky : TraceId.Opaque, random.NextSingle() * 2);
        }

        log.BeginResolve();
        for (int i = 0; i < log.VisibilityCount; i++)
        {
            seen.Add("v" + log.ReadVisibility());
        }

        for (int i = 0; i < log.SkyCount; i++)
        {
            seen.Add("s" + log.ReadSkyOcclusion());
        }

        for (int i = 0; i < log.Sky2Count; i++)
        {
            seen.Add("k" + log.ReadSky2Occlusion());
        }

        for (int i = 0; i < log.Tape.Length; i++)
        {
            seen.Add("t" + log.Tape.ReadInt());
        }

        Assert.True(log.ReplayComplete);
        return seen;
    }

    /// <summary>
    /// A larger batch after a smaller and a smaller after a larger, on one
    /// log over a pool that poisons and recycles: each batch records and
    /// replays exactly what a plain log does, so nothing an earlier batch
    /// (or an earlier renter) left past the counts is ever read.
    /// </summary>
    [Fact]
    public void ARentedLogRecordsAndReplaysAsAPlainOneWhateverTheOrderOfSizes()
    {
        RecyclingScratchPool pool = new();
        using LightRayLog pooled = new() { Pool = pool, DeferRecursion = true };
        int[] sizes = [5, 3_000, 7, 5_000, 1, 0, 800];

        for (int k = 0; k < sizes.Length; k++)
        {
            using LightRayLog plain = new() { DeferRecursion = true };
            Assert.Equal(RecordAndReplay(plain, sizes[k], seed: k), RecordAndReplay(pooled, sizes[k], seed: k));
        }

        Assert.True(pool.Returned > 0, "growing should hand the outgrown arrays back");
        Assert.Equal(0, pool.BadReturns);
    }

    [Fact]
    public void DisposingReturnsEveryArrayOnceAndRefusesToGrowAgain()
    {
        RecyclingScratchPool pool = new();
        LightRayLog log = new() { Pool = pool };
        RecordAndReplay(log, 300, seed: 1);
        log.ReserveSky(10_000);
        Assert.True(pool.Outstanding > 0);

        log.Dispose();
        log.Dispose();

        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(pool.Rented, pool.Returned);
        Assert.Equal(0, pool.BadReturns);
        Assert.Equal(0, log.TotalCount);
        Assert.Throws<ObjectDisposedException>(() => log.EmitVisibility(Vec3.Zero, new Vec3(1, 0, 0)));
        Assert.Throws<ObjectDisposedException>(() => log.Tape.Int(1));
    }

    [Fact]
    public void APlainLogAllocatesAndHasNothingToReturn()
    {
        LightRayLog log = new();
        RecordAndReplay(log, 100, seed: 2);
        log.ReserveSky(500);

        log.Dispose();

        Assert.Equal(0, log.TotalCount);
        Assert.Throws<ObjectDisposedException>(() => log.EmitSky(Vec3.Zero, new Vec3(1, 0, 0)));
    }

    [Fact]
    public void ALogThatRecordsNothingRentsNothing()
    {
        RecyclingScratchPool pool = new();
        LightRayLog log = new() { Pool = pool };

        log.Reset();
        log.Dispose();

        Assert.Equal(0, pool.Rented);
    }

    private static async Task<(FaceLight?[] Light, int Workers)> LightAsync(
        LightTestMap map, IRayTracer tracer, int threads, int batchRays, RecyclingScratchPool? pool, CancellationToken cancellationToken = default)
    {
        CompileParallelism parallelism = new() { MaxDegree = threads };
        RadWorld world = await RadWorld.StartAsync(
            map.Build(), LightBox.Settings(), new TextureLightTable(new(), "box"), tracer, parallelism, CancellationToken.None);
        world.FacelightBatchRays = batchRays;
        world.FacelightScratchPool = pool;
        await world.LightFacesAsync(tracer, parallelism, cancellationToken);
        return (world.FaceLights, threads);
    }

    private static LightTestMap Box()
    {
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(
            ("classname", "light"), ("origin", "128 128 128"), ("_light", "255 255 255 200")));
        map.AddOccluder(new(96, 96, 64), new(160, 96, 64), new(160, 160, 64), new(96, 160, 64));
        return map;
    }

    private static void AssertSameLight(FaceLight?[] expected, FaceLight?[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int f = 0; f < expected.Length; f++)
        {
            if (expected[f] is null)
            {
                Assert.Null(actual[f]);
                continue;
            }

            Assert.Equal(expected[f]!.Styles, actual[f]!.Styles);
            for (int k = 0; k < expected[f]!.Light.Length; k++)
            {
                Assert.Equal(expected[f]!.Light[k], actual[f]!.Light[k]);
            }
        }
    }

    /// <summary>
    /// Face lighting whose workers' logs are rented from a poisoning,
    /// recycling pool gives the light plain logs give -- one worker and four,
    /// small batches and large, a tracer that answers in the call and one
    /// that answers later -- and every array is back when the stage ends.
    /// </summary>
    [Theory]
    [InlineData(1, 50, false)]
    [InlineData(4, 50, true)]
    [InlineData(4, RadWorld.RaysPerBatch, false)]
    public async Task RentedLogsLightAsPlainOnes(int threads, int batchRays, bool asynchronous)
    {
        LightTestMap map = Box();
        IRayTracer tracer = map.Tracer();
        (FaceLight?[] expected, _) = await LightAsync(map, tracer, 1, RadWorld.RaysPerBatch, pool: null);
        RecyclingScratchPool pool = new();

        (FaceLight?[] actual, _) = await LightAsync(map, new CountingRayTracer(tracer, asynchronous), threads, batchRays, pool);

        AssertSameLight(expected, actual);
        Assert.True(pool.Rented > 0);
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, pool.BadReturns);
    }

    /// <summary>
    /// A face-lighting stage whose tracer faults part way, with other
    /// workers' slabs still in flight, awaits those slabs before it gives
    /// their arrays back -- no call ever finds its rays changed under it --
    /// and returns every array; the next stage on the same pool lights
    /// exactly as a fresh one.
    /// </summary>
    /// <remarks>
    /// The failing calls finish at staggered times, so when the driver meets
    /// the first failure the others are usually still reading; six stages
    /// make it all but certain that one of them is. The first three calls -- one
    /// a worker -- succeed, so the first round to fail fails on every worker.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFaultedStageReturnsEverythingAndLeavesThePoolFit(bool asynchronous)
    {
        LightTestMap map = Box();
        IRayTracer tracer = map.Tracer();
        (FaceLight?[] expected, _) = await LightAsync(map, tracer, 1, RadWorld.RaysPerBatch, pool: null);
        RecyclingScratchPool pool = new();

        for (int attempt = 0; attempt < (asynchronous ? 6 : 1); attempt++)
        {
            FaultAfter faulting = new(tracer, 3, asynchronous);
            await Assert.ThrowsAsync<IOException>(() => LightAsync(map, faulting, 3, 40, pool));
            await faulting.SettleAsync();

            Assert.Equal(0, faulting.RaysChangedWhileReading);
            Assert.True(pool.Rented > 0);
            Assert.Equal(0, pool.Outstanding);
            Assert.Equal(0, pool.BadReturns);
        }

        (FaceLight?[] after, _) = await LightAsync(map, tracer, 3, 40, pool);
        AssertSameLight(expected, after);
        Assert.Equal(0, pool.Outstanding);
    }

    /// <summary>A cancelled face-lighting stage gives every array back too.</summary>
    [Fact]
    public async Task ACancelledStageReturnsEverything()
    {
        LightTestMap map = Box();
        IRayTracer tracer = map.Tracer();
        RecyclingScratchPool pool = new();
        using CancellationTokenSource source = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LightAsync(
            map, new CancelAfter(tracer, 4, source), 3, 40, pool, source.Token));

        Assert.True(pool.Rented > 0);
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, pool.BadReturns);
    }

    // The tracer, until the given number of visibility calls have started;
    // later calls fault. Asynchronously, each call takes a copy of its rays
    // and checks them again just before it answers: a stage that handed the
    // rays back to the pool while the call still read them shows as poison.
    private sealed class FaultAfter(IRayTracer inner, int calls, bool asynchronous) : IRayTracer
    {
        private readonly List<Task> _started = [];
        private int _calls;
        private int _changed;

        public int RaysChangedWhileReading => Volatile.Read(ref _changed);

        public string TracerIdentity => "fault-after";

        /// <summary>Waits for every call this tracer started, whatever it ended in.</summary>
        public async Task SettleAsync()
        {
            Task[] started;
            lock (_started)
            {
                started = [.. _started];
            }

            foreach (Task task in started)
            {
                try
                {
                    await task;
                }
                catch (IOException)
                {
                }
                catch (OperationCanceledException)
                {
                    // A stage that fails cancels the calls it still has in
                    // flight (their token is the stage's run, which the
                    // first failure stops); a call that sees it ends here.
                }
            }
        }

        public ValueTask TraceVisibilityAsync(
            ReadOnlyMemory<Ray> rays, Memory<ulong> hitBits, RayTraceOptions options, CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _calls);
            bool fail = call > calls;
            if (!asynchronous)
            {
                return fail
                    ? ValueTask.FromException(new IOException("device lost"))
                    : inner.TraceVisibilityAsync(rays, hitBits, options, cancellationToken);
            }

            Ray[] given = rays.ToArray();
            Task task = Task.Run(async () =>
            {
                await Task.Delay(fail ? 10 + (25 * (call % 4)) : 1);
                if (!rays.Span.SequenceEqual(given))
                {
                    Interlocked.Increment(ref _changed);
                }

                if (fail)
                {
                    throw new IOException("device lost");
                }

                await inner.TraceVisibilityAsync(rays, hitBits, options, cancellationToken);
            });
            lock (_started)
            {
                _started.Add(task);
            }

            return new ValueTask(task);
        }

        public ValueTask TraceClosestAsync(
            ReadOnlyMemory<Ray> rays, Memory<HitId> hits, RayTraceOptions options, CancellationToken cancellationToken = default) =>
            inner.TraceClosestAsync(rays, hits, options, cancellationToken);
    }

    // The tracer, until the given number of visibility calls; then it
    // cancels the stage's token.
    private sealed class CancelAfter(IRayTracer inner, int calls, CancellationTokenSource source) : IRayTracer
    {
        private int _calls;

        public string TracerIdentity => "cancel-after";

        public ValueTask TraceVisibilityAsync(
            ReadOnlyMemory<Ray> rays, Memory<ulong> hitBits, RayTraceOptions options, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) > calls)
            {
                source.Cancel();
            }

            return inner.TraceVisibilityAsync(rays, hitBits, options, CancellationToken.None);
        }

        public ValueTask TraceClosestAsync(
            ReadOnlyMemory<Ray> rays, Memory<HitId> hits, RayTraceOptions options, CancellationToken cancellationToken = default) =>
            inner.TraceClosestAsync(rays, hits, options, cancellationToken);
    }
}
