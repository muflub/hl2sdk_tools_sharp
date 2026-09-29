//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Gpu;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// The slab batcher's pack helpers: every item runs once, on the caller or a
/// helper and never on the pool, a failure reaches the caller after the
/// running items finish, and dispose leaves no thread behind.
/// </summary>
public sealed class PackCrewTests
{
    /// <summary>
    /// Every item runs once, on the caller or one of the crew's threads. The
    /// caller here is a dedicated thread, as the batcher's drainer is, so no
    /// item may land on the pool.
    /// </summary>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(3, 64)]
    [InlineData(3, 1000)]
    public void EveryItemRunsExactlyOnceOnTheCallerOrTheCrew(int helpers, int count)
    {
        using PackCrew crew = new(helpers);
        int[] runs = new int[count];
        System.Collections.Concurrent.ConcurrentBag<(bool Pool, int Thread)> where = [];
        Exception? failure = null;
        Thread caller = new(() =>
        {
            try
            {
                for (int round = 0; round < 5; round++)
                {
                    crew.Run(count, i =>
                    {
                        Interlocked.Increment(ref runs[i]);
                        where.Add((Thread.CurrentThread.IsThreadPoolThread, Environment.CurrentManagedThreadId));
                    });
                }
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        caller.Start();
        caller.Join();

        Assert.Null(failure);
        Assert.All(runs, r => Assert.Equal(5, r));
        int[] allowed = [caller.ManagedThreadId, .. crew.Threads.Select(t => t.ManagedThreadId)];
        Assert.All(where, w =>
        {
            Assert.False(w.Pool);
            Assert.Contains(w.Thread, allowed);
        });
    }

    [Fact]
    public void AFailureIsRethrownOnTheCallerAndStopsFurtherClaims()
    {
        using PackCrew crew = new(2);
        int ran = 0;

        // Each item takes a moment, so the claims cannot all be made before
        // the throw is seen: a million items would take seconds.
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => crew.Run(1_000_000, i =>
        {
            Interlocked.Increment(ref ran);
            Thread.SpinWait(200);
            if (i == 3)
            {
                throw new InvalidOperationException("item 3");
            }
        }));

        Assert.Equal("item 3", e.Message);
        Assert.InRange(Volatile.Read(ref ran), 4, 999_999);

        // The crew is still usable afterwards.
        int after = 0;
        crew.Run(100, _ => Interlocked.Increment(ref after));
        Assert.Equal(100, after);
    }

    [Fact]
    public void DisposeJoinsEveryHelperAndRefusesMoreWork()
    {
        PackCrew crew = new(3);
        crew.Run(50, _ => Thread.SpinWait(100));
        IReadOnlyList<Thread> threads = crew.Threads;
        Assert.Equal(3, threads.Count);

        crew.Dispose();
        crew.Dispose();

        Assert.All(threads, t => Assert.False(t.IsAlive));
        Assert.Throws<ObjectDisposedException>(() => crew.Run(1, _ => { }));
    }

    [Fact]
    public void ACrewNeedsAHelper() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PackCrew(0));
}
