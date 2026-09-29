//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Bounce;
using SourceSharp.MapTools.Tracing;
using SourceSharp.MapTools.Vis;

using Xunit;

using static SourceSharp.Tests.MapTools.Compile.MapCompilerTests;

namespace SourceSharp.Tests.MapTools.Rad;

/// <summary>
/// The compile's scratch pool, as a long-lived service sees it: every array
/// a compile rents goes back however the compile ends -- lit, failed in the
/// middle of a stage, cancelled in the middle of one -- the pool ends empty,
/// and the next compile starts a pool of its own that allocates no more than
/// the first did.
/// </summary>
public sealed class VradScratchPoolTests
{
    [Theory]
    [InlineData(VradLightingRange.Ldr)]
    [InlineData(VradLightingRange.Both)]
    public async Task ALitCompileReturnsEveryArrayAndEndsItsPoolEmpty(VradLightingRange range)
    {
        List<CompileScratchPool> pools = [];
        (BspData bsp, IContentFileSystem content) = await RoomAsync();

        _ = await Vrad.LightAsync(bsp, await ContextAsync(bsp, content, pools, null) with
        {
            Options = VradOptions.Default with { Bounces = 1, Range = range },
        });

        CompileScratchPool pool = Assert.Single(pools);
        Assert.True(pool.Allocations > 0, "the face lighting and the transfer build rent from the compile's pool");
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, pool.IdleArrays);
        Assert.Throws<ObjectDisposedException>(() => pool.Rent<int>(1));
        Assert.Throws<ObjectDisposedException>(() => pool.ForWorker(0).Rent<int>(1));

        // The stages' workers rented through shards of their own, one per
        // worker index (the compile runs two), and the counts agree.
        Assert.Equal(2, pool.WorkerShards);
        ScratchPoolStatistics stats = pool.Statistics;
        Assert.Equal(pool.Allocations, stats.Misses);
        Assert.Equal(pool.Reuses, stats.Hits);
        Assert.Equal(pool.AllocatedBytes, stats.AllocatedBytes);
        Assert.Equal(stats.Rentals, stats.ByTypeAndSize.Sum(c => c.Hits + c.Misses));
        Assert.Equal(stats.AllocatedBytes, stats.ByTypeAndSize.Sum(c => c.AllocatedBytes));
        Assert.True(stats.TrimmedArrays > 0, "the compile trims between its stages");
    }

    /// <summary>
    /// The pool hands out arrays as their last renter left them, never
    /// cleared. This is the proof that no renter depends on what is in one:
    /// a compile whose every rented array -- fresh or reused, in every stage
    /// that rents -- is first filled with garbage (0xCD, or 0xFF, which makes
    /// every float a NaN) lights exactly the same bytes as one that is not,
    /// in both compliance modes, with the workers sharded and borrowing.
    /// </summary>
    [Theory]
    [InlineData((byte)0xCD, false, 2)]
    [InlineData((byte)0xFF, false, 3)]
    [InlineData((byte)0xCD, true, 3)]
    [InlineData((byte)0xFF, true, 2)]
    public async Task APoisonedPoolLightsTheSameBytes(byte poison, bool stock, int degree)
    {
        VradOptions options = VradOptions.Default with
        {
            Bounces = 1,
            Range = VradLightingRange.Both,
            Compliance = stock ? ComplianceOptions.Stock : ComplianceOptions.Correct,
        };
        List<CompileScratchPool> pools = [];
        BspData[] lit = new BspData[2];
        for (int run = 0; run < 2; run++)
        {
            (BspData bsp, IContentFileSystem content) = await RoomAsync();
            VradContext context = await ContextAsync(bsp, content, pools, null, degree, run == 1 ? poison : null);
            _ = await Vrad.LightAsync(bsp, context with { Options = options });
            lit[run] = bsp;
        }

        Assert.Null(pools[0].PoisonByte);
        Assert.Equal(poison, pools[1].PoisonByte);
        Assert.True(pools[1].Statistics.Rentals > 0);
        for (int lump = 0; lump < BspData.HeaderLumps; lump++)
        {
            Assert.True(
                lit[0][lump].Data.Span.SequenceEqual(lit[1][lump].Data.Span),
                $"lump {(BspLump)lump} differs when every rented array starts as 0x{poison:X2}");
        }
    }

    /// <summary>
    /// A tracer call that fails while a stage's workers hold their rented
    /// logs and buffers: the failure is the one reported, and every array
    /// still goes back.
    /// </summary>
    [Theory]
    [InlineData(Vrad.FacelightsStage)]
    [InlineData(Vrad.BounceStage)]
    public async Task AFailureInsideAStageReturnsEveryArray(string stage)
    {
        List<CompileScratchPool> pools = [];
        (BspData bsp, IContentFileSystem content) = await RoomAsync();
        InvalidDataException planted = new("planted failure");
        StageFlag inStage = new(stage);
        VradContext context = await ContextAsync(
            bsp, content, pools, inner => new TrippingTracer(inner, () => inStage.Started, () => throw planted));
        TrippingTracer tracer = (TrippingTracer)context.Tracer!;

        Exception thrown = await Assert.ThrowsAnyAsync<Exception>(
            () => Vrad.LightAsync(bsp, context with { Progress = inStage }));

        Assert.Same(planted, thrown);
        Assert.True(tracer.Tripped);
        CompileScratchPool pool = Assert.Single(pools);
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, pool.IdleArrays);
    }

    /// <summary>A compile cancelled from inside a stage's tracer call returns every array too.</summary>
    [Theory]
    [InlineData(Vrad.FacelightsStage)]
    [InlineData(Vrad.BounceStage)]
    public async Task ACancellationInsideAStageReturnsEveryArray(string stage)
    {
        List<CompileScratchPool> pools = [];
        (BspData bsp, IContentFileSystem content) = await RoomAsync();
        using CancellationTokenSource cts = new();
        StageFlag inStage = new(stage);
        VradContext context = await ContextAsync(
            bsp, content, pools, inner => new TrippingTracer(inner, () => inStage.Started, cts.Cancel));
        TrippingTracer tracer = (TrippingTracer)context.Tracer!;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Vrad.LightAsync(bsp, context with { Progress = inStage }, cts.Token));

        Assert.True(tracer.Tripped);
        CompileScratchPool pool = Assert.Single(pools);
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, pool.IdleArrays);
    }

    /// <summary>
    /// Two compiles back to back in one process, as a service runs them: the
    /// second has a pool of its own, allocates no more through it than the
    /// first did, and both end empty -- nothing one compile rented is kept
    /// for, or by, the next.
    /// </summary>
    [Fact]
    public async Task BackToBackCompilesEachStartAFreshPoolAndTheSecondAllocatesNoMore()
    {
        List<CompileScratchPool> pools = [];
        byte[][] lit = new byte[2][];
        for (int run = 0; run < 2; run++)
        {
            (BspData bsp, IContentFileSystem content) = await RoomAsync();
            _ = await Vrad.LightAsync(bsp, await ContextAsync(bsp, content, pools, null, degree: 1));
            lit[run] = bsp[BspLump.Lighting].Data.ToArray();
        }

        Assert.Equal(2, pools.Count);
        Assert.NotSame(pools[0], pools[1]);
        Assert.Equal(lit[0], lit[1]);
        Assert.True(pools[0].AllocatedBytes > 0);
        Assert.True(
            pools[1].AllocatedBytes <= pools[0].AllocatedBytes,
            $"second compile allocated {pools[1].AllocatedBytes} bytes, the first {pools[0].AllocatedBytes}");
        Assert.True(pools[1].Allocations <= pools[0].Allocations);
        Assert.All(pools, p => Assert.Equal(0, p.Outstanding));
        Assert.All(pools, p => Assert.Equal(0, p.IdleArrays));
        Assert.All(pools, p => Assert.Equal(0, p.IdleBytes));

        // At one worker the rentals are the same sequence both times, so the
        // second compile is not merely no worse than the first: it is the
        // first again, rental for rental -- nothing of the first was kept
        // for it, and nothing it did depended on the first having run.
        Assert.Equal(pools[0].Statistics.Misses, pools[1].Statistics.Misses);
        Assert.Equal(pools[0].Statistics.Hits, pools[1].Statistics.Hits);
        Assert.Equal(pools[0].AllocatedBytes, pools[1].AllocatedBytes);
    }

    /// <summary>
    /// The same, with the workers sharded: the first compile's pool, shards
    /// and all, is ended before the second starts, the second makes shards
    /// of its own, and both end empty with the same lighting. (How much each
    /// allocates depends on which worker claims which face, which is the
    /// scheduler's choice at more than one worker, so the byte bound above
    /// is checked at one.)
    /// </summary>
    [Fact]
    public async Task BackToBackShardedCompilesShareNothing()
    {
        List<CompileScratchPool> pools = [];
        byte[][] lit = new byte[2][];
        for (int run = 0; run < 2; run++)
        {
            (BspData bsp, IContentFileSystem content) = await RoomAsync();
            _ = await Vrad.LightAsync(bsp, await ContextAsync(bsp, content, pools, null, degree: 3));
            lit[run] = bsp[BspLump.Lighting].Data.ToArray();
            Assert.Throws<ObjectDisposedException>(() => pools[run].ForWorker(0).Rent<int>(1));
        }

        Assert.Equal(2, pools.Count);
        Assert.NotSame(pools[0], pools[1]);
        Assert.NotSame(pools[0].ForWorker(0), pools[1].ForWorker(0));
        Assert.Equal(lit[0], lit[1]);
        Assert.All(pools, p => Assert.Equal(3, p.WorkerShards));
        Assert.All(pools, p => Assert.True(p.Statistics.Misses > 0));
        Assert.All(pools, p => Assert.Equal(0, p.Outstanding));
        Assert.All(pools, p => Assert.Equal(0, p.IdleArrays));
        Assert.All(pools, p => Assert.Equal(0, p.IdleBytes));
    }

    /// <summary>
    /// The sealed room with its pillar, through vbsp and vvis, so it has
    /// clusters and the bounce runs, and a real tree for the leaf ambient.
    /// </summary>
    internal static async Task<(BspData Bsp, IContentFileSystem Content)> RoomAsync()
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        VbspContext context = new(VbspOptions.Default, content) { MapBase = "room" };
        MapFile map = await new MapFileReader(context, files).LoadAsync(VPath.Create("maps/room.vmf"));
        VbspResult vbsp = await Vbsp.CompileAsync(map, context);
        BspData bsp = vbsp.Bsp!;
        PortalFile prt = await PortalFile.ParseAsync(vbsp.Portals!.ToBytes(PortalLineEnding.CrLf));
        _ = await Vvis.ComputeAsync(
            bsp, PortalSet.FromPortalFile(prt), new VisContext { Parallelism = new CompileParallelism { MaxDegree = 2 } });
        return (bsp, content);
    }

    /// <summary>
    /// A compile over the room with a tracer of the test's (so a fact can
    /// wrap it), whose every scratch pool is added to <paramref name="pools"/>.
    /// </summary>
    private static async Task<VradContext> ContextAsync(
        BspData bsp,
        IContentFileSystem content,
        List<CompileScratchPool> pools,
        Func<IRayTracer, IRayTracer>? wrap,
        int degree = 2,
        byte? poison = null)
    {
        ShadowCasterLoadReport casters = await ShadowCasterLoader.LoadAsync(
            bsp, VradOptions.Default, content, NullPropCollisionSource.Instance);
        IRayTracer tracer = casters.Set.BuildTracer();
        return new VradContext
        {
            Options = VradOptions.Default with { Bounces = 1 },
            MapName = "room",
            Content = content,
            Tracer = wrap is null ? tracer : wrap(tracer),
            Parallelism = new CompileParallelism { MaxDegree = degree },
            ScratchPoolFactory = () =>
            {
                CompileScratchPool pool = new() { PoisonByte = poison };
                lock (pools)
                {
                    pools.Add(pool);
                }

                return pool;
            },
        };
    }

    /// <summary>Remembers that a stage has started, from the compile's progress reports.</summary>
    private sealed class StageFlag(string stage) : IProgress<SourceSharp.MapTools.Diagnostics.CompileProgress>
    {
        private volatile bool _started;

        public bool Started => _started;

        public void Report(SourceSharp.MapTools.Diagnostics.CompileProgress value)
        {
            if (value.Stage == stage)
            {
                _started = true;
            }
        }
    }

    /// <summary>
    /// A tracer that runs <c>trip</c> on its first call once <c>armed</c> says
    /// so, then answers as the tracer it wraps.
    /// </summary>
    private sealed class TrippingTracer(IRayTracer inner, Func<bool> armed, Action trip) : IRayTracer
    {
        private int _tripped;

        public bool Tripped => Volatile.Read(ref _tripped) != 0;

        public string TracerIdentity => "tripping+" + inner.TracerIdentity;

        public bool Supports(RayTraceOptions options) => inner.Supports(options);

        public ValueTask TraceVisibilityAsync(
            ReadOnlyMemory<Ray> rays, Memory<ulong> hitBits, RayTraceOptions options, CancellationToken cancellationToken = default)
        {
            Trip();
            return inner.TraceVisibilityAsync(rays, hitBits, options, cancellationToken);
        }

        public ValueTask TraceClosestAsync(
            ReadOnlyMemory<Ray> rays, Memory<HitId> hits, RayTraceOptions options, CancellationToken cancellationToken = default)
        {
            Trip();
            return inner.TraceClosestAsync(rays, hits, options, cancellationToken);
        }

        private void Trip()
        {
            if (armed() && Interlocked.Exchange(ref _tripped, 1) == 0)
            {
                trip();
            }
        }
    }
}
