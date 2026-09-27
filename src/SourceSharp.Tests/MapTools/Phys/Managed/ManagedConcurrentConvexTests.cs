//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Concurrent;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Phys;
using SourceSharp.MapTools.Phys.Managed;
using Xunit;

namespace SourceSharp.Tests.MapTools.Phys.Managed;

/// <summary>
/// The managed session's <see cref="IConcurrentConvexSession"/>: convexes built on
/// worker sessions, adopted in index order, the same bytes as one thread.
/// </summary>
public class ManagedConcurrentConvexTests
{
    // Boxes of assorted proportions, and one set of planes that bounds nothing.
    private static CollisionPlane[] Box(int i)
    {
        if (i % 11 == 10)
        {
            // +X at 4 and -X at -8: empty.
            return
            [
                new(new Vec3(1, 0, 0), 4), new(new Vec3(-1, 0, 0), -8), new(new Vec3(0, 1, 0), 8),
                new(new Vec3(0, -1, 0), 8), new(new Vec3(0, 0, 1), 8), new(new Vec3(0, 0, -1), 8),
            ];
        }

        float x = 4 + (i * 3.25f), y = 2 + ((i * 7) % 13), z = 1 + ((i * 5) % 9) + (i * 0.125f);
        return
        [
            new(new Vec3(1, 0, 0), x + i), new(new Vec3(-1, 0, 0), x - i), new(new Vec3(0, 1, 0), y),
            new(new Vec3(0, -1, 0), y), new(new Vec3(0, 0, 1), z + (2 * i)), new(new Vec3(0, 0, -1), z - i),
        ];
    }

    private static ConvexHandle Build(ICollisionSession s, int i) => s.ConvexFromPlanes(Box(i), 0.01f);

    // Each convex's own collide bytes and volume, read through the session that holds it.
    private static List<(byte[]? Bytes, float Volume)> Describe(ICollisionSession s, ConvexHandle[] convexes)
    {
        List<(byte[]?, float)> described = [];
        foreach (ConvexHandle c in convexes)
        {
            if (c.IsNull)
            {
                described.Add((null, 0f));
                continue;
            }

            float volume = s.ConvexVolume(c);
            CollideHandle collide = s.ConvertConvexToCollide([c]);
            described.Add((s.CollideWrite(collide), volume));
            s.DestroyCollide(collide);
        }

        return described;
    }

    [Theory]
    [InlineData(CompliancePolicy.Correct, 2)]
    [InlineData(CompliancePolicy.Correct, 8)]
    [InlineData(CompliancePolicy.Stock, 3)]
    [InlineData(CompliancePolicy.Stock, 8)]
    public async Task TheConvexesAreTheOnesOneThreadBuildsInIndexOrder(CompliancePolicy policy, int degree)
    {
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create((policy == CompliancePolicy.Stock ? ComplianceOptions.Stock : ComplianceOptions.Correct));
        const int Count = 60;

        List<(byte[]?, float)> serial = await cooker.RunAsync(s =>
            Describe(s, [.. Enumerable.Range(0, Count).Select(i => Build(s, i))]));
        List<(byte[]?, float)> parallel = await cooker.RunAsync(s =>
            Describe(s, ((IConcurrentConvexSession)s).BuildConvexes(Count, Build, degree, CancellationToken.None)));

        Assert.Equal(Count, parallel.Count);
        for (int i = 0; i < Count; i++)
        {
            Assert.Equal(serial[i].Item1, parallel[i].Item1);
            Assert.Equal(serial[i].Item2, parallel[i].Item2);
        }

        // The empty item stays a null handle, in its place.
        Assert.Null(parallel[10].Item1);
    }

    [Fact]
    public async Task TheBuildsRunOnWorkerSessionsNotTheCallersOwn()
    {
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        ConcurrentBag<ICollisionSession> workers = [];

        (bool sawOwn, int adopted) = await cooker.RunAsync(s =>
        {
            ConvexHandle[] built = ((IConcurrentConvexSession)s).BuildConvexes(
                20,
                (w, i) =>
                {
                    workers.Add(w);
                    return Build(w, i);
                },
                4,
                CancellationToken.None);
            return (workers.Contains(s), ((ManagedCollisionSession)s).LiveConvexCount);
        });

        Assert.False(sawOwn);
        Assert.Equal(20 - 1, adopted); // item 10 bounds nothing
        Assert.InRange(workers.Distinct().Count(), 1, 4);
    }

    [Fact]
    public async Task ItemsReallyRunAtOnce()
    {
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        using Barrier barrier = new(2);
        bool[] met = new bool[2];

        await cooker.RunAsync(s => ((IConcurrentConvexSession)s).BuildConvexes(
            2,
            (w, i) =>
            {
                met[i] = barrier.SignalAndWait(TimeSpan.FromSeconds(30));
                return Build(w, i);
            },
            2,
            CancellationToken.None));

        Assert.True(met[0] && met[1]);
    }

    [Fact]
    public async Task AFailureAdoptsNothingAndThrowsTheLowestIndex()
    {
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);

        (Exception? error, int live) = await cooker.RunAsync(s =>
        {
            ConvexHandle before = Build(s, 0);
            try
            {
                ((IConcurrentConvexSession)s).BuildConvexes(
                    40,
                    (w, i) => i is 7 or 30 ? throw new InvalidOperationException(i.ToString(System.Globalization.CultureInfo.InvariantCulture)) : Build(w, i),
                    4,
                    CancellationToken.None);
                return ((Exception?)null, 0);
            }
            catch (InvalidOperationException e)
            {
                return (e, ((ManagedCollisionSession)s).LiveConvexCount);
            }
            finally
            {
                s.ConvexFree(before);
            }
        });

        Assert.Equal("7", error?.Message);
        Assert.Equal(1, live); // only the convex made before the call
    }

    [Fact]
    public async Task CancellingMidBuildAdoptsNothing()
    {
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        using CancellationTokenSource cts = new();

        (bool cancelled, int live) = await cooker.RunAsync(s =>
        {
            try
            {
                ((IConcurrentConvexSession)s).BuildConvexes(
                    500,
                    (w, i) =>
                    {
                        if (i == 12)
                        {
                            cts.Cancel();
                        }

                        return Build(w, i);
                    },
                    4,
                    cts.Token);
                return (false, 0);
            }
            catch (OperationCanceledException)
            {
                return (true, ((ManagedCollisionSession)s).LiveConvexCount);
            }
        });

        Assert.True(cancelled);
        Assert.Equal(0, live);
    }

    [Fact]
    public async Task ABuildThatReturnsAForeignConvexIsRefused()
    {
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);

        await Assert.ThrowsAsync<ArgumentException>(() => cooker.RunAsync(s =>
            ((IConcurrentConvexSession)s).BuildConvexes(3, (_, i) => Build(s, i), 1, CancellationToken.None)));
    }

    [Fact]
    public async Task ASessionWithoutACookerBuildsOnItself()
    {
        // A session built directly has no worker builders: every item runs on it,
        // on the calling thread, and the result is the same.
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Stock);
        List<(byte[]?, float)> expected = await cooker.RunAsync(s =>
            Describe(s, [.. Enumerable.Range(0, 12).Select(i => Build(s, i))]));

        ManagedCollisionSession bare = new(
            new IvpBuild<float, StockPrecision>(new IvpCookContext(new SourceSharp.MapTools.Phys.Managed.Qhull.QhullRunner())),
            new LockedSurfaceProps(new SourceSharp.MapTools.Phys.SurfacePropertyTable()));
        List<ICollisionSession> used = [];
        ConvexHandle[] built = bare.BuildConvexes(
            12,
            (w, i) =>
            {
                used.Add(w);
                return Build(w, i);
            },
            8,
            CancellationToken.None);

        Assert.All(used, w => Assert.Same(bare, w));
        List<(byte[]?, float)> actual = Describe(bare, built);
        Assert.Equal(expected.Select(e => e.Item1), actual.Select(a => a.Item1));
    }

    [Fact]
    public async Task HelpersQueueOnTheCookersScheduler()
    {
        // The workers count against the compile's pool, like the cooks do.
        using CompilePool pool = new(3);
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        cooker.Scheduler = pool.Scheduler;
        ConcurrentBag<bool> onPool = [];

        await cooker.RunAsync(s => ((IConcurrentConvexSession)s).BuildConvexes(
            30,
            (w, i) =>
            {
                onPool.Add(pool.IsPoolThread);
                return Build(w, i);
            },
            3,
            CancellationToken.None));

        Assert.Equal(30, onPool.Count);
        Assert.All(onPool, Assert.True);
    }

    [Fact]
    public async Task ANullBuildIsRefused()
    {
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);

        await Assert.ThrowsAsync<ArgumentNullException>(() => cooker.RunAsync(s =>
            ((IConcurrentConvexSession)s).BuildConvexes(3, null!, 2, CancellationToken.None)));
    }
}
