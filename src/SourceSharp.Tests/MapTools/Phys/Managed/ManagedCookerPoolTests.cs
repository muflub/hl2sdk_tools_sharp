//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys;
using SourceSharp.MapTools.Phys.Managed;
using Xunit;

namespace SourceSharp.Tests.MapTools.Phys.Managed;

/// <summary>
/// The cooker's reused qhull storage, seen from the cook: a cook after a failed or abandoned
/// cook gives what a fresh context gives, contexts on different threads never meet, and
/// disposing the cooker lets every thread's storage go.
/// </summary>
public class ManagedCookerPoolTests
{
    private static List<CookerFixture.Job> Jobs() =>
        [.. CookerFixture.Jobs("clouds").Take(40), .. CookerFixture.Jobs("brushes").Take(40), .. CookerFixture.Jobs("multi").Take(20)];

    /// <summary>Each job cooked on a context of its own: the bytes with no reuse at all.</summary>
    private static byte[]?[] FreshBytes(List<CookerFixture.Job> jobs, CookMode mode) =>
        [.. jobs.Select(j => CookerFixture.Cook(j, mode, CookerFixture.Context(mode)))];

    [Theory]
    [InlineData(CookMode.Stock, 0)]
    [InlineData(CookMode.Correct, 0)]
    [InlineData(CookMode.Correct, 5)]
    [InlineData(CookMode.Stock, 30)]
    public void CooksAfterAnAbandonedCookMatchFreshContexts(CookMode mode, int abortAtFacet)
    {
        List<CookerFixture.Job> jobs = Jobs();
        byte[]?[] expected = FreshBytes(jobs, mode);

        // Warm the context, then abandon a cook part-way through one of its hulls (as a
        // cancelled or crashed cook would), leaving half-built objects in the pool.
        IvpCookContext context = CookerFixture.Context(mode);
        for (int i = 0; i < 5; i++)
            _ = CookerFixture.Cook(jobs[i], mode, context);
        context.Hulls.Pool.FacetTaken = n =>
        {
            if (n == abortAtFacet)
                throw new OperationCanceledException("abandoned at facet " + n);
        };
        Assert.Throws<OperationCanceledException>(() => CookerFixture.Cook(jobs[^1], mode, context));
        context.Hulls.Pool.FacetTaken = null;

        for (int i = 0; i < jobs.Count; i++)
            Assert.Equal(expected[i], CookerFixture.Cook(jobs[i], mode, context));
    }

    [Fact]
    public void TheQueriesShareTheRunnersStorage()
    {
        // One pool per thread, not two: the collide queries build their leaf hulls on the
        // runner's own storage. A stand-in runner gets storage of its own.
        IvpCookContext context = CookerFixture.Context(CookMode.Correct);
        Assert.Same(((SourceSharp.MapTools.Phys.Managed.Qhull.QhullRunner)context.Qhull).Session, context.Hulls);
        var standIn = new IvpCookContext(new NoQhull());
        Assert.NotNull(standIn.Hulls);
    }

    [Fact]
    public async Task ConcurrentCooksOnSeparateContextsMatchFreshContexts()
    {
        List<CookerFixture.Job> jobs = Jobs();
        byte[]?[] expected = FreshBytes(jobs, CookMode.Correct);
        Task<byte[]?[]>[] runs = [.. Enumerable.Range(0, 6).Select(t => Task.Run(() =>
        {
            // each thread its own context, walking the jobs in its own order, so every pool
            // holds a different history when it meets a given job
            IvpCookContext context = CookerFixture.Context(CookMode.Correct);
            var got = new byte[]?[jobs.Count];
            for (int k = 0; k < jobs.Count; k++)
            {
                int i = (k + (t * 17)) % jobs.Count;
                got[i] = CookerFixture.Cook(jobs[i], CookMode.Correct, context);
            }

            return got;
        }))];
        foreach (byte[]?[] got in await Task.WhenAll(runs))
            Assert.Equal(expected, got);
    }

    [Fact]
    public async Task DisposingTheCookerReleasesEveryThreadsStorage()
    {
        ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        List<WeakReference> pools = await CookOnSeveralThreadsAsync(cooker);
        Assert.NotEmpty(pools);

        // While the cooker lives, its threads keep their storage (that is the point of it).
        Collect();
        Assert.All(pools, p => Assert.True(p.IsAlive));

        cooker.Dispose();
        Collect();
        Assert.All(pools, p => Assert.False(p.IsAlive, "a thread's qhull storage outlived the cooker"));
        Assert.Throws<ObjectDisposedException>(() => cooker.CookPlanes([(1f, 0f, 0f, 16f), (-1f, 0f, 0f, 16f), (0f, 1f, 0f, 16f), (0f, -1f, 0f, 16f), (0f, 0f, 1f, 16f), (0f, 0f, -1f, 16f)], 0f));
        GC.KeepAlive(cooker);
    }

    /// <summary>
    /// Cooks on several pool threads at once and returns weak references to the storage each
    /// thread's context kept. Not inlined, so no strong reference survives in the caller's frame.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<List<WeakReference>> CookOnSeveralThreadsAsync(ManagedCollisionCooker cooker)
    {
        using var gate = new Barrier(4);
        WeakReference?[] refs = await Task.WhenAll(Enumerable.Range(0, 4).Select(w => cooker.RunAsync(s =>
        {
            // Hold every worker until all four have started, so they are four threads.
            gate.SignalAndWait(TimeSpan.FromSeconds(10));
            _ = s.ConvexFromPlanes(CubePlanes(), 0f);
            var pool = cooker.ContextOfCurrentThread().Hulls.Pool;
            return pool.Retained.Facets > 0 ? new WeakReference(pool) : null;
        })));
        return [.. refs.OfType<WeakReference>().DistinctBy(r => r.Target)];
    }

    private static CollisionPlane[] CubePlanes() =>
    [
        new(new Vec3(1, 0, 0), 16), new(new Vec3(-1, 0, 0), 16), new(new Vec3(0, 1, 0), 16),
        new(new Vec3(0, -1, 0), 16), new(new Vec3(0, 0, 1), 16), new(new Vec3(0, 0, -1), 16),
    ];

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>A runner that is not the qhull port's: the context must give the queries storage of their own.</summary>
    private sealed class NoQhull : IQhullRunner
    {
        public int FacetCount => 0;

        public int Run(ReadOnlySpan<double> coords, int pointCount, string options) => 1;

        public (double X, double Y, double Z) Normal(int facet) => default;

        public ReadOnlySpan<int> Vertices(int facet) => [];
    }
}
