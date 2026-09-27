//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Concurrent;

using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Vis;

/// <summary>
/// <see cref="VisIdleWorkers"/>: the per-worker parking the <c>-tighten</c>
/// schedule's idle workers wait in. Each path of the protocol on its own, then
/// a stress run that would hang (or time out) on a lost wake.
/// </summary>
public class VisIdleWorkersTests
{
    [Fact]
    public void ThereMustBeRoomForAWorker()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VisIdleWorkers(0));
    }

    [Fact]
    public void WakingWithNobodyParkedWakesNobody()
    {
        VisIdleWorkers idle = new(4);

        Assert.Equal(0, idle.Wake(int.MaxValue));
        Assert.Equal(0, idle.Parked);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AskingToWakeNoneWakesNone(int count)
    {
        VisIdleWorkers idle = new(4);
        idle.Announce(2);

        Assert.Equal(0, idle.Wake(count));
        Assert.Equal(1, idle.Parked);
        Assert.False(idle.Wait(2, 0));
    }

    [Fact]
    public void AWakeClaimsAParkedWorkerAndSetsItsEvent()
    {
        VisIdleWorkers idle = new(4);
        idle.Announce(1);
        Assert.Equal(1, idle.Parked);

        Assert.Equal(1, idle.Wake(1));

        Assert.Equal(0, idle.Parked);
        Assert.True(idle.Wait(1, 0));
        idle.Withdraw(1);
        Assert.Equal(0, idle.Parked);
    }

    [Fact]
    public void AWakeWakesAtMostWhatItWasAskedForLowestIndexFirst()
    {
        VisIdleWorkers idle = new(8);
        idle.Announce(6);
        idle.Announce(2);
        idle.Announce(4);

        Assert.Equal(2, idle.Wake(2));

        Assert.True(idle.Wait(2, 0));
        Assert.True(idle.Wait(4, 0));
        Assert.False(idle.Wait(6, 0));
        Assert.Equal(1, idle.Parked);
    }

    [Fact]
    public void WorkersPastTheFirstSixtyFourAreParkedAndWoken()
    {
        // The bits live in words of 64: a degree above that spans words, and
        // a wake has to go on to the next word when the first runs out.
        VisIdleWorkers idle = new(130);
        Assert.Equal(130, idle.Capacity);
        idle.Announce(3);
        idle.Announce(64);
        idle.Announce(129);
        Assert.Equal(3, idle.Parked);

        Assert.Equal(3, idle.Wake(int.MaxValue));

        Assert.True(idle.Wait(3, 0));
        Assert.True(idle.Wait(64, 0));
        Assert.True(idle.Wait(129, 0));
        Assert.Equal(0, idle.Parked);
    }

    [Fact]
    public void AWorkerThatWithdrewIsNotWokenOrCounted()
    {
        // A worker that timed out and withdrew must not absorb a wake that a
        // worker still parked should have had.
        VisIdleWorkers idle = new(4);
        idle.Announce(0);
        Assert.False(idle.Wait(0, 0));
        idle.Withdraw(0);
        idle.Announce(3);

        Assert.Equal(1, idle.Wake(1));

        Assert.True(idle.Wait(3, 0));
        Assert.Equal(0, idle.Parked);
    }

    [Fact]
    public void AWakeFromAnEarlierParkDoesNotCarryIntoTheNext()
    {
        // The event is reset BEFORE the bit goes up: a set that belonged to
        // a park the worker has already left is cleared by the next announce,
        // and only a claim of the new bit sets it again.
        VisIdleWorkers idle = new(2);
        idle.Announce(1);
        Assert.Equal(1, idle.Wake(1));
        idle.Withdraw(1);

        idle.Announce(1);

        Assert.False(idle.Wait(1, 0));
        Assert.Equal(1, idle.Parked);
        Assert.Equal(1, idle.Wake(1));
        Assert.True(idle.Wait(1, 0));
    }

    [Fact]
    public void WaitingTimesOutWithNobodyToWakeIt()
    {
        VisIdleWorkers idle = new(1);
        idle.Announce(0);

        Assert.False(idle.Wait(0, 1));

        idle.Withdraw(0);
        Assert.Equal(0, idle.Parked);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(70)]
    public async Task NoWakeIsLostBetweenAProducerAndManyConsumers(int consumers)
    {
        // The schedule's protocol, by itself: consumers announce, look at the
        // queue again, and only then wait; the producer publishes, then wakes
        // one. A consumer that waits out the long timeout was parked with an
        // item on offer and nobody woke it: a lost wake.
        const int Items = 20_000;
        const int LongWait = 30_000;
        VisIdleWorkers idle = new(consumers);
        ConcurrentQueue<int> queue = new();
        int taken = 0;
        int timedOut = 0;
        int finished = 0;

        Task[] workers = new Task[consumers];
        for (int c = 0; c < consumers; c++)
        {
            int slot = c;
            workers[c] = Task.Factory.StartNew(
                () =>
                {
                    while (true)
                    {
                        if (queue.TryDequeue(out _))
                        {
                            Interlocked.Increment(ref taken);
                            continue;
                        }

                        if (Volatile.Read(ref finished) != 0)
                        {
                            return;
                        }

                        idle.Announce(slot);
                        if (queue.IsEmpty && Volatile.Read(ref finished) == 0 && !idle.Wait(slot, LongWait))
                        {
                            Interlocked.Increment(ref timedOut);
                        }

                        idle.Withdraw(slot);
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        for (int i = 0; i < Items; i++)
        {
            queue.Enqueue(i);
            idle.Wake(1);
            if ((i & 1023) == 0)
            {
                // Let the consumers drain and park now and then, so the
                // producer meets parked workers, not only busy ones.
                await Task.Delay(1);
            }
        }

        while (!queue.IsEmpty)
        {
            await Task.Delay(1);
        }

        Volatile.Write(ref finished, 1);
        idle.Wake(int.MaxValue);
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(Items, taken);
        Assert.Equal(0, timedOut);
        Assert.Equal(0, idle.Parked);
    }
}
