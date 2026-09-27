//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Parallel;

using Xunit;

namespace SourceSharp.Tests.MapTools.Parallel;

/// <summary>
/// <see cref="JobSlots"/>: a pooled job's slots and inside-count, handed out
/// without a lock. The single-threaded facts pin each rule; the racing ones
/// hammer it from more threads than the machine has cores so that the
/// interleavings a lock used to exclude actually happen.
/// </summary>
public class JobSlotsTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    // More threads than cores, so threads are preempted mid-operation.
    private static readonly int Racers = Math.Max(8, Environment.ProcessorCount * 2);

    // ---- Unbuilt slots ------------------------------------------------------

    [Fact]
    public void UnbuiltSlotsAreHandedOutLowestFirstAndOnlyOnce()
    {
        JobSlots slots = new(3);
        Assert.Equal(3, slots.UnbuiltLeft);
        Assert.Equal(0, slots.TryTakeUnbuilt());
        Assert.Equal(1, slots.TryTakeUnbuilt());
        Assert.Equal(1, slots.UnbuiltLeft);
        Assert.Equal(2, slots.TryTakeUnbuilt());
        Assert.Equal(0, slots.UnbuiltLeft);
        Assert.Equal(-1, slots.TryTakeUnbuilt());
        Assert.Equal(-1, slots.TryTakeUnbuilt());
        Assert.Equal(0, slots.UnbuiltLeft);
    }

    [Fact]
    public void TheDegreeIsFlooredAtOne()
    {
        JobSlots slots = new(0);
        Assert.Equal(1, slots.Degree);
        Assert.Equal(0, slots.TryTakeUnbuilt());
        Assert.Equal(-1, slots.TryTakeUnbuilt());
    }

    [Fact]
    public async Task RacingBuildersTakeEveryUnbuiltSlotExactlyOnce()
    {
        for (int round = 0; round < 200; round++)
        {
            const int Degree = 16;
            JobSlots slots = new(Degree);
            int[] taken = new int[Degree];
            using Barrier start = new(Racers);
            Task[] racers = Enumerable.Range(0, Racers).Select(_ => Task.Factory.StartNew(
                () =>
                {
                    start.SignalAndWait();
                    for (int k = 0; k < 4; k++)
                    {
                        int slot = slots.TryTakeUnbuilt();
                        if (slot >= 0)
                        {
                            Interlocked.Increment(ref taken[slot]);
                        }
                    }
                },
                TaskCreationOptions.LongRunning)).ToArray();
            await Task.WhenAll(racers).WaitAsync(Patience);

            Assert.All(taken, t => Assert.Equal(1, t));
            Assert.Equal(0, slots.UnbuiltLeft);
        }
    }

    // ---- Free slots ---------------------------------------------------------

    [Fact]
    public void NoSlotIsFreeUntilOneIsReturned()
    {
        JobSlots slots = new(2);
        Assert.Equal(-1, slots.TryTakeFree());
        int slot = slots.TryTakeUnbuilt();
        Assert.Equal(-1, slots.TryTakeFree());
        slots.Return(slot);
        Assert.Equal(slot, slots.TryTakeFree());
        Assert.Equal(-1, slots.TryTakeFree());
    }

    [Fact]
    public void AReturnedSlotIsLentToOneBorrowerOnly()
    {
        JobSlots slots = new(3);
        slots.Return(slots.TryTakeUnbuilt());
        slots.Return(slots.TryTakeUnbuilt());
        int first = slots.TryTakeFree();
        int second = slots.TryTakeFree();
        Assert.NotEqual(first, second);
        Assert.Equal([0, 1], new[] { first, second }.Order().ToArray());
        Assert.Equal(-1, slots.TryTakeFree());
    }

    [Fact]
    public async Task RacingBorrowersNeverHoldOneSlotTogether()
    {
        // Fewer slots than racers, so most attempts find the job full and the
        // ones that do not are fighting over the same few flags.
        const int Degree = 3;
        JobSlots slots = new(Degree);
        for (int i = 0; i < Degree; i++)
        {
            slots.Return(slots.TryTakeUnbuilt());
        }

        int[] holders = new int[Degree];
        int[] uses = new int[Degree];
        int clashes = 0;
        using Barrier start = new(Racers);
        Task[] racers = Enumerable.Range(0, Racers).Select(_ => Task.Factory.StartNew(
            () =>
            {
                start.SignalAndWait();
                for (int k = 0; k < 20_000; k++)
                {
                    int slot = slots.TryTakeFree();
                    if (slot < 0)
                    {
                        continue;
                    }

                    if (Interlocked.Increment(ref holders[slot]) != 1)
                    {
                        Interlocked.Increment(ref clashes);
                    }

                    Interlocked.Increment(ref uses[slot]);
                    Interlocked.Decrement(ref holders[slot]);
                    slots.Return(slot);
                }
            },
            TaskCreationOptions.LongRunning)).ToArray();
        await Task.WhenAll(racers).WaitAsync(Patience);

        Assert.Equal(0, clashes);
        Assert.True(uses.Sum() > 0);

        // Every slot came home: all of them can be borrowed again, and no more.
        int[] after = [slots.TryTakeFree(), slots.TryTakeFree(), slots.TryTakeFree()];
        Assert.Equal([0, 1, 2], after.Order().ToArray());
        Assert.Equal(-1, slots.TryTakeFree());
    }

    // ---- Inside count and finishing ----------------------------------------

    [Fact]
    public void OnlyTheLastThreadOutIsToldSo()
    {
        JobSlots slots = new(2);
        Assert.True(slots.TryEnter());
        Assert.True(slots.TryEnter());
        Assert.Equal(2, slots.InFlight);
        Assert.False(slots.Leave());
        Assert.True(slots.Leave());
        Assert.Equal(0, slots.InFlight);
    }

    [Fact]
    public void AJobCannotFinishWithAThreadInside()
    {
        JobSlots slots = new(1);
        Assert.True(slots.TryEnter());
        Assert.False(slots.TryFinishIdle());
        Assert.False(slots.IsFinished);
        Assert.True(slots.Leave());
        Assert.True(slots.TryFinishIdle());
        Assert.True(slots.IsFinished);
    }

    [Fact]
    public void AJobFinishesOnceAndCannotBeEnteredAfterwards()
    {
        JobSlots slots = new(1);
        Assert.True(slots.TryFinishIdle());
        Assert.False(slots.TryFinishIdle());
        Assert.False(slots.ForceFinish());
        Assert.False(slots.TryEnter());
        Assert.True(slots.IsFinished);
    }

    [Fact]
    public void AnAbandonedJobFinishesWhoeverIsCountedInsideAndOnlyOnce()
    {
        JobSlots slots = new(1);
        Assert.True(slots.TryEnter());
        Assert.True(slots.ForceFinish());
        Assert.True(slots.IsFinished);
        Assert.False(slots.ForceFinish());
        Assert.False(slots.TryFinishIdle());
        Assert.False(slots.TryEnter());
    }

    [Fact]
    public async Task RacingThreadsFinishAJobExactlyOnceAndNeverEnterItAfterwards()
    {
        for (int round = 0; round < 100; round++)
        {
            JobSlots slots = new(4);
            int finishers = 0;
            int enteredAfterFinish = 0;
            using Barrier start = new(Racers);
            Task[] racers = Enumerable.Range(0, Racers).Select(_ => Task.Factory.StartNew(
                () =>
                {
                    start.SignalAndWait();
                    for (int k = 0; k < 500; k++)
                    {
                        if (!slots.TryEnter())
                        {
                            return;
                        }

                        if (slots.IsFinished)
                        {
                            Interlocked.Increment(ref enteredAfterFinish);
                        }

                        // Each round's job is "done" from the start, so every
                        // last-out thread tries to finish it.
                        if (slots.Leave() && slots.TryFinishIdle())
                        {
                            Interlocked.Increment(ref finishers);
                        }
                    }
                },
                TaskCreationOptions.LongRunning)).ToArray();
            await Task.WhenAll(racers).WaitAsync(Patience);

            Assert.Equal(1, finishers);
            Assert.Equal(0, enteredAfterFinish);
            Assert.True(slots.IsFinished);
        }
    }

    // ---- Turned-away scans --------------------------------------------------

    [Fact]
    public void ATurnedAwayScanIsAnsweredByOneReturnedSlot()
    {
        JobSlots slots = new(1);
        Assert.False(slots.TakeWake(finishing: false, stopping: false, exhausted: false));
        slots.TurnAway();
        slots.TurnAway();
        Assert.Equal(2, slots.TurnedAway);
        Assert.False(slots.TakeWake(finishing: false, stopping: false, exhausted: true));
        Assert.Equal(2, slots.TurnedAway);
        Assert.True(slots.TakeWake(finishing: false, stopping: false, exhausted: false));
        Assert.True(slots.TakeWake(finishing: false, stopping: false, exhausted: false));
        Assert.False(slots.TakeWake(finishing: false, stopping: false, exhausted: false));
        Assert.Equal(0, slots.TurnedAway);
    }

    [Fact]
    public async Task RacingReturnsAnswerEachTurnedAwayScanOnceAndNeverGoNegative()
    {
        for (int round = 0; round < 100; round++)
        {
            const int Entries = 50;
            int turnedAway = Entries;
            int wakes = 0;
            using Barrier start = new(Racers);
            Task[] racers = Enumerable.Range(0, Racers).Select(_ => Task.Factory.StartNew(
                () =>
                {
                    start.SignalAndWait();
                    for (int k = 0; k < 20; k++)
                    {
                        if (WorkQueue.TakeWakeForFreedSlot(false, false, false, ref turnedAway))
                        {
                            Interlocked.Increment(ref wakes);
                        }
                    }
                },
                TaskCreationOptions.LongRunning)).ToArray();
            await Task.WhenAll(racers).WaitAsync(Patience);

            Assert.Equal(Entries, wakes);
            Assert.Equal(0, turnedAway);
        }
    }
}
