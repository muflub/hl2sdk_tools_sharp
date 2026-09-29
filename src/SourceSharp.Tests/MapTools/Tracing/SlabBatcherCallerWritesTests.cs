//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Concurrent;

using SourceSharp.MapTools.Gpu;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// <see cref="SlabBatcher"/> with <see cref="SlabWrites.Callers"/>: each
/// caller writes its own rays into an open slab when it hands them over, so
/// the drainer copies nothing. Every ray gets the words and the answer the
/// drainer's pack gives it; a full ring leaves the rest to the drainer and
/// never makes a caller wait; cancellation, failure and closing release
/// every reservation and every slot.
/// </summary>
public sealed partial class SlabBatcherTests
{
    /// <summary>What one request asks: its tag (written into every ray), size, mode, shared reach (null for mixed) and epsilon.</summary>
    private sealed record Spec(int Tag, int Count, int Mode, float? Reach, uint Tmin);

    /// <summary>
    /// Rays like <see cref="Rays"/>, each carrying its request's tag in
    /// <c>OriginZ</c> and its own index in <c>DirectionY</c>, so a record read
    /// back from a slab says whose ray it is; with a reach, every ray has it,
    /// and without one, the first two rays differ in it, so a request of two
    /// rays or more always goes wide.
    /// </summary>
    private static Ray[] TaggedRays(int n, int tag, float? reach)
    {
        Random random = new((tag * 7919) + n);
        Ray[] rays = new Ray[n];
        for (int i = 0; i < n; i++)
        {
            rays[i] = new Ray(
                random.Next(0, 6), (float)random.NextDouble(), tag,
                random.Next(2) == 0 ? -1 : 1, i, 0,
                reach ?? (i switch
                {
                    0 => 1f,
                    1 => 3f,
                    _ => random.Next(2) == 0 ? 1f : random.Next(1, 10),
                }));
        }

        return rays;
    }

    /// <summary>
    /// A mix of every kind: both modes, a shared reach that hits (6), one
    /// that misses (2) or none, and two epsilons, in sizes that cross slabs.
    /// </summary>
    private static List<Spec> Specs(int count, int seed, int maxRays)
    {
        Random random = new(seed);
        List<Spec> specs = [];
        for (int k = 0; k < count; k++)
        {
            float? reach = random.Next(3) switch
            {
                0 => null,
                1 => 6f,
                _ => 2f,
            };
            specs.Add(new Spec(k + 1, random.Next(1, maxRays + 1), random.Next(2), reach, random.Next(2) == 0 ? 0u : 7u));
        }

        return specs;
    }

    /// <summary>What a run handed over and got back, per request.</summary>
    private sealed record Answers(Ray[][] Rays, ulong[][] Bits, HitId[][] Hits);

    /// <summary>
    /// Hands every request to the batcher from <paramref name="threads"/>
    /// dedicated threads at once, round robin, and waits for all of them.
    /// </summary>
    private static Answers Run(SlabBatcher batcher, IReadOnlyList<Spec> specs, int threads)
    {
        Ray[][] rays = [.. specs.Select(s => TaggedRays(s.Count, s.Tag, s.Reach))];
        ulong[][] bits = [.. specs.Select(s =>
        {
            // Every word the rays cover must be rewritten; the one past must not be.
            ulong[] b = new ulong[s.Mode == 0 ? ((s.Count + 63) / 64) + 1 : 0];
            Array.Fill(b, ulong.MaxValue);
            return b;
        })];
        HitId[][] hits = [.. specs.Select(s => s.Mode == 1 ? Sentinels(s.Count) : [])];
        Task[] tasks = new Task[specs.Count];
        Thread[] workers = [.. Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            for (int k = t; k < specs.Count; k += threads)
            {
                Spec s = specs[k];
                tasks[k] = s.Mode == 0
                    ? batcher.TraceVisibilityAsync(rays[k], bits[k], s.Tmin, CancellationToken.None)
                    : batcher.TraceClosestAsync(rays[k], hits[k], s.Tmin, CancellationToken.None);
            }
        })
        { IsBackground = true, Name = $"fact caller {t}" })];
        foreach (Thread w in workers)
        {
            w.Start();
        }

        foreach (Thread w in workers)
        {
            Assert.True(w.Join(Patience), "a caller never returned from handing its rays over");
        }

        Assert.True(Task.WaitAll(tasks, Patience), "a request was never answered");
        return new Answers(rays, bits, hits);
    }

    /// <summary>Every ray has exactly its own answer, and nothing past a request was touched.</summary>
    private static void AssertAnswered(IReadOnlyList<Spec> specs, Answers answers)
    {
        for (int k = 0; k < specs.Count; k++)
        {
            Ray[] rays = answers.Rays[k];
            if (specs[k].Mode == 1)
            {
                Assert.Equal(rays.Select(Closest), answers.Hits[k]);
                continue;
            }

            ulong[] bits = answers.Bits[k];
            for (int i = 0; i < (bits.Length - 1) * 64; i++)
            {
                Assert.Equal(i < rays.Length && Hit(rays[i]), (bits[i / 64] & (1UL << (i % 64))) != 0);
            }

            Assert.Equal(ulong.MaxValue, bits[^1]);
        }
    }

    /// <summary>
    /// The words every ray went to the device in, by (tag, index), over
    /// every slab submitted; a padding copy must be the same words as the
    /// ray it copies. A word the batcher never wrote is still the fake's
    /// poison and fails here.
    /// </summary>
    private static Dictionary<(int Tag, int Index), uint[]> WordsByRay(FakeDevice device)
    {
        Dictionary<(int, int), uint[]> map = [];
        for (int d = 0; d < device.StagedPerDispatch.Count; d++)
        {
            RayRecord record = device.RecordPerDispatch[d];
            uint[] staged = device.StagedPerDispatch[d];
            int words = record.Words;
            Assert.Equal(device.RaysPerDispatch[d] * words, staged.Length);
            Assert.DoesNotContain(0xDEADBEEFu, staged);
            for (int at = 0; at < device.RaysPerDispatch[d]; at++)
            {
                uint[] own = staged.AsSpan(at * words, words).ToArray();
                Ray ray = record.Decode(own);
                (int, int) key = ((int)ray.OriginZ, (int)ray.DirectionY);
                if (map.TryGetValue(key, out uint[]? seen))
                {
                    Assert.Equal(seen, own);
                }
                else
                {
                    map[key] = own;
                }
            }
        }

        return map;
    }

    private static FakeDevice CallersDevice(int maxSlabRays, int inFlight, bool jitter = false) =>
        new(maxSlabRays, inFlight + SlabBatcher.OpenSlabs, inFlight) { Jitter = jitter };

    /// <summary>
    /// The same requests, from four threads at once, through the drainer's
    /// pack and through the callers' own writes: every ray goes to the
    /// device in the same words (wide or narrow, gaps included) and gets
    /// the same answer, one slab in flight or three.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public void CallerWritesGiveEveryRayTheWordsAndAnswersOfTheDrainersPack(int inFlight, bool narrowOnly)
    {
        List<Spec> specs = Specs(60, 17 + inFlight, 700);
        if (narrowOnly)
        {
            specs = [.. specs.Select(s => s with { Reach = s.Reach ?? 6f })];
        }
        else
        {
            specs = [.. specs.Select(s => s with { Count = Math.Max(2, s.Count), Reach = null })];
        }

        FakeDevice packedDevice = new(256, inFlight) { Jitter = true };
        SlabBatcher packed = new(packedDevice, Ids, 0);
        FakeDevice callersDevice = CallersDevice(256, inFlight, jitter: true);
        SlabBatcher callers = new(callersDevice, Ids, 0, SlabWrites.Callers);
        Answers byDrainer;
        Answers byCallers;
        try
        {
            byDrainer = Run(packed, specs, 4);
            byCallers = Run(callers, specs, 4);
        }
        finally
        {
            packed.Close(() => { });
            callers.Close(() => { });
        }

        AssertAnswered(specs, byDrainer);
        AssertAnswered(specs, byCallers);
        for (int k = 0; k < specs.Count; k++)
        {
            Assert.Equal(byDrainer.Bits[k], byCallers.Bits[k]);
            Assert.Equal(byDrainer.Hits[k], byCallers.Hits[k]);
        }

        Dictionary<(int Tag, int Index), uint[]> drainerWords = WordsByRay(packedDevice);
        Dictionary<(int Tag, int Index), uint[]> callerWords = WordsByRay(callersDevice);
        Assert.Equal(specs.Sum(s => s.Count), drainerWords.Count);
        Assert.Equal(drainerWords.Keys.Order(), callerWords.Keys.Order());
        foreach (((int Tag, int Index) key, uint[] words) in drainerWords)
        {
            Assert.Equal(words, callerWords[key]);
        }

        // Narrow requests went on the wire in 24 bytes, whoever wrote them.
        int wordsPerRay = narrowOnly ? RayRecord.UniformReachWords : RayRecord.WideWords;
        Assert.All(callerWords.Values, w => Assert.Equal(wordsPerRay, w.Length));
        Assert.InRange(callersDevice.MaxInFlight, 1, inFlight);
    }

    /// <summary>
    /// With room in the ring (two kinds, a slot for each beyond the ones on
    /// the device), the drainer copies no ray: it stages nothing, packs no
    /// chunk and starts no helper, and every slab position was written by a
    /// caller, on the caller's own thread, before its call returned.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void WithRoomInTheRingTheDrainerCopiesNoRay(int inFlight)
    {
        FakeDevice device = CallersDevice(1 << 16, inFlight, jitter: true);
        SlabBatcher batcher = new(device, Ids, 0, SlabWrites.Callers);
        ConcurrentBag<int> packed = [];
        ConcurrentBag<(string? Thread, int Positions)> writes = [];
        batcher.ObservePackChunk = packed.Add;
        batcher.ObserveCallerWrite = n => writes.Add((Thread.CurrentThread.Name, n));

        // Two kinds only: visibility and closest, one epsilon, mixed reach.
        // Two rays at least, so no request is narrow by chance: a narrow one
        // would be a third kind, which the ring has no open slot for.
        List<Spec> specs = [.. Specs(160, 5, 300).Select(s => s with { Count = Math.Max(2, s.Count), Reach = null, Tmin = 0 })];
        Answers answers;
        IReadOnlyList<Thread> threads;
        try
        {
            answers = Run(batcher, specs, 8);
            threads = batcher.Threads;
        }
        finally
        {
            batcher.Close(() => { });
        }

        AssertAnswered(specs, answers);
        Assert.Equal(0, device.StageCalls);
        Assert.Empty(packed);
        Assert.Single(threads); // the drainer, and no pack helper
        Assert.All(writes, w => Assert.StartsWith("fact caller", w.Thread, StringComparison.Ordinal));
        _ = WordsByRay(device); // no position was left unwritten

        GpuTraceStatistics stats = batcher.Statistics;
        Assert.Equal(TimeSpan.Zero, stats.Pack);
        Assert.Equal(stats.SlabRays, stats.CallerRays);
        Assert.Equal(stats.SlabRays, writes.Sum(w => w.Positions));
        Assert.Equal(device.RaysPerDispatch.Sum(), stats.SlabRays);
        Assert.True(stats.Write > TimeSpan.Zero);
        Assert.Equal(inFlight, stats.Slots);
    }

    /// <summary>
    /// A full ring makes no caller wait: what fits is written, the rest of
    /// the request and a request of a kind with no slot left are queued, the
    /// calls return at once, and the drainer packs the rest when slots come
    /// back, oldest first.
    /// </summary>
    [Fact]
    public async Task WhenTheRingIsFullTheRestIsLeftToTheDrainerAndNoCallerWaits()
    {
        // One slot on the device and one open: 64 rays each.
        FakeDevice device = new(64, 2, 1);
        device.Gate.Reset();
        SlabBatcher batcher = new(device, Ids, 0, SlabWrites.Callers);

        Ray[] blockerRays = Rays(10, 1);
        HitId[] blockerHits = new HitId[10];
        Task blocker = batcher.TraceClosestAsync(blockerRays, blockerHits, 0, CancellationToken.None);
        await device.DrainerWaiting.Task.WaitAsync(Patience);

        // The open slot takes 64 of these; 36 are left over.
        Ray[] bigRays = Rays(100, 2);
        HitId[] bigHits = new HitId[100];
        Task big = batcher.TraceClosestAsync(bigRays, bigHits, 0, CancellationToken.None);

        // No slot at all for another kind.
        Ray[] visRays = RaysWithReach(10, 3, 6f);
        ulong[] visBits = new ulong[1];
        Task vis = batcher.TraceVisibilityAsync(visRays, visBits, 0, CancellationToken.None);

        Assert.False(big.IsCompleted);
        Assert.False(vis.IsCompleted);
        Assert.Equal(1, batcher.OpenSlabCount);
        Assert.Equal(0, batcher.PendingWriters);

        device.Gate.Set();
        await Task.WhenAll(blocker, big, vis).WaitAsync(Patience);

        Assert.Equal(blockerRays.Select(Closest), blockerHits);
        Assert.Equal(bigRays.Select(Closest), bigHits);
        Assert.Equal((1UL << 10) - 1, visBits[0]);

        // The written part first (it is as old as the rest of its request),
        // then the drainer's two packs.
        Assert.Equal([10, 64, 36, 10], device.RaysPerDispatch);
        Assert.Equal(2, device.StageCalls);
        GpuTraceStatistics stats = batcher.Statistics;
        Assert.Equal(74, stats.CallerRays);
        Assert.Equal(120, stats.SlabRays);
        batcher.Close(() => { });
    }

    /// <summary>
    /// Rays left over from an older request wait for a slot, but when every
    /// slot off the device has become a younger open slab, the oldest of
    /// those goes out first, since only its landing frees a slot for them.
    /// The drainer once went to sleep there, with nothing on the device and
    /// nobody left to wake it; a fact that makes callers take each slot the
    /// moment it comes back reaches that state every time.
    /// </summary>
    [Fact]
    public async Task AnOpenSlabGoesOutWhenOlderLeftoverRaysHaveNoSlot()
    {
        // Two slots, one of them on the device at a time.
        FakeDevice device = new(64, 2, 1);
        device.Gate.Reset();
        SlabBatcher batcher = new(device, Ids, 0, SlabWrites.Callers);

        Task x = batcher.TraceClosestAsync(Rays(10, 1), new HitId[10], 0, CancellationToken.None);
        await device.DrainerWaiting.Task.WaitAsync(Patience);
        Task a = batcher.TraceVisibilityAsync(RaysWithReach(10, 2, 6f), new ulong[1], 0, CancellationToken.None);

        // No slot left for this kind: all of it is left to the drainer.
        Ray[] sRays = Rays(10, 3);
        HitId[] sHits = new HitId[10];
        Task s = batcher.TraceClosestAsync(sRays, sHits, 7, CancellationToken.None);
        Assert.Equal(1, batcher.OpenSlabCount);

        // As each of the first two slots comes back, a caller of a new kind
        // takes it, before the drainer looks for its next slab.
        Ray[] uRays = Rays(20, 4);
        ulong[] uBits = new ulong[1];
        Ray[] vRays = RaysWithReach(30, 5, 6f);
        HitId[] vHits = new HitId[30];
        Task? u = null;
        Task? v = null;
        int freed = 0;
        batcher.ObserveSlotFreed = _ =>
        {
            switch (++freed)
            {
                case 1:
                    u = batcher.TraceVisibilityAsync(uRays, uBits, 7, CancellationToken.None);
                    break;
                case 2:
                    v = batcher.TraceClosestAsync(vRays, vHits, 0, CancellationToken.None);
                    break;
            }
        };

        device.Gate.Set();
        await Task.WhenAll(x, a, s).WaitAsync(Patience);
        Assert.True(SpinWait.SpinUntil(() => v is not null, Patience));
        await Task.WhenAll(u!, v!).WaitAsync(Patience);

        // X, then A (older than S); then U, the older open slab, since S has
        // no slot; then S, packed into the slot U's landing freed; then V.
        Assert.Equal([10, 10, 20, 10, 30], device.RaysPerDispatch);
        Assert.Equal(1, device.StageCalls);
        Assert.Equal(sRays.Select(Closest), sHits);
        Assert.Equal(uRays.Select(r => Hit(r)), Enumerable.Range(0, 20).Select(i => (uBits[0] & (1UL << i)) != 0));
        Assert.Equal(vRays.Select(Closest), vHits);
        batcher.Close(() => { });
    }

    /// <summary>
    /// Many callers on a tiny ring with every kind of request: slabs fill,
    /// requests split across open slabs and the drainer's packs, callers
    /// race the drainer's claims, and still every request is answered
    /// exactly, no caller is ever held up, and nothing is left reserved,
    /// open or in flight afterwards.
    /// </summary>
    [Theory]
    [InlineData(1, 16)]
    [InlineData(2, 12)]
    public void ManyCallersOnATinyRingAllGetTheirAnswersWithoutDeadlock(int inFlight, int threads)
    {
        FakeDevice device = CallersDevice(128, inFlight, jitter: true);
        SlabBatcher batcher = new(device, Ids, 0, SlabWrites.Callers);
        List<Spec> specs = Specs(400, 23 + inFlight, 300);
        Answers answers;
        try
        {
            answers = Run(batcher, specs, threads);
            Assert.Equal(0, batcher.PendingWriters);
            Assert.True(SpinWait.SpinUntil(() => batcher.OpenSlabCount == 0 && device.InFlight == 0, Patience));
        }
        finally
        {
            batcher.Close(() => { });
        }

        AssertAnswered(specs, answers);
        _ = WordsByRay(device);

        // Both ways of writing were exercised.
        GpuTraceStatistics stats = batcher.Statistics;
        Assert.InRange(stats.CallerRays, 1, stats.SlabRays - 1);
        Assert.True(device.StageCalls > 0);
        Assert.All(batcher.Threads, t => Assert.False(t.IsAlive));
    }

    /// <summary>
    /// The answers do not depend on how many callers hand the rays over or
    /// how many slabs may be on the device: the same requests from 1, 4 and
    /// 16 threads, with one slab in flight and with three, answer alike.
    /// </summary>
    [Fact]
    public void AnswersDoNotDependOnHowManyCallersOrSlotsThereAre()
    {
        List<Spec> specs = Specs(120, 31, 500);
        Answers? first = null;
        foreach ((int threads, int inFlight) in new[] { (1, 1), (4, 3), (16, 1), (16, 3) })
        {
            SlabBatcher batcher = new(CallersDevice(192, inFlight, jitter: true), Ids, 0, SlabWrites.Callers);
            Answers answers;
            try
            {
                answers = Run(batcher, specs, threads);
            }
            finally
            {
                batcher.Close(() => { });
            }

            AssertAnswered(specs, answers);
            first ??= answers;
            for (int k = 0; k < specs.Count; k++)
            {
                Assert.Equal(first.Bits[k], answers.Bits[k]);
                Assert.Equal(first.Hits[k], answers.Hits[k]);
            }
        }
    }

    /// <summary>
    /// The drainer closes an open slab before it waits for the callers
    /// still writing into it: a caller of the same kind that comes along
    /// meanwhile opens a slab of its own, and the claimed slab goes out once
    /// its last writer is done, with only the rays reserved before the claim.
    /// </summary>
    [Fact]
    public async Task TheDrainerSealsAClaimedSlabAndWaitsForItsWriters()
    {
        FakeDevice device = CallersDevice(256, 1);
        SlabBatcher batcher = new(device, Ids, 0, SlabWrites.Callers);
        using ManualResetEventSlim writing = new();
        using ManualResetEventSlim finish = new();
        int calls = 0;
        batcher.ObserveCallerWrite = _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                writing.Set();
                Assert.True(finish.Wait(Patience));
            }
        };

        Ray[] aRays = Rays(10, 1);
        HitId[] aHits = new HitId[10];
        Task? a = null;
        Thread writer = new(() => a = batcher.TraceClosestAsync(aRays, aHits, 0, CancellationToken.None)) { IsBackground = true };
        writer.Start();
        Assert.True(writing.Wait(Patience));

        // Another kind wakes the drainer, which claims the oldest open slab,
        // A's, and waits for A's writer.
        Ray[] bRays = RaysWithReach(20, 2, 6f);
        ulong[] bBits = new ulong[1];
        Task b = batcher.TraceVisibilityAsync(bRays, bBits, 0, CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => batcher.AwaitingWriters, Patience));
        Assert.Equal(2, batcher.OpenSlabCount);

        // A's kind again: the claimed slab takes no more, so this opens the third slot.
        Ray[] cRays = Rays(30, 3);
        HitId[] cHits = new HitId[30];
        Task c = batcher.TraceClosestAsync(cRays, cHits, 0, CancellationToken.None);
        Assert.Equal(3, batcher.OpenSlabCount);
        Assert.Empty(device.RaysPerDispatch);

        finish.Set();
        Assert.True(writer.Join(Patience));
        await Task.WhenAll(a!, b, c).WaitAsync(Patience);

        Assert.Equal([10, 20, 30], device.RaysPerDispatch);
        Assert.Equal(aRays.Select(Closest), aHits);
        Assert.Equal((1UL << 20) - 1, bBits[0]);
        Assert.Equal(cRays.Select(Closest), cHits);
        Assert.False(batcher.AwaitingWriters);
        batcher.Close(() => { });
    }

    /// <summary>
    /// A request cancelled while its rays sit alone in an open slab is
    /// dropped with the slab: nothing is dispatched for it, and the slot is
    /// free again for the next request.
    /// </summary>
    [Fact]
    public async Task ACancelledRequestAloneInAnOpenSlabIsDroppedWithoutADispatch()
    {
        FakeDevice device = CallersDevice(256, 1);
        device.Gate.Reset();
        SlabBatcher batcher = new(device, Ids, 0, SlabWrites.Callers);
        using CancellationTokenSource cancel = new();

        Task blocker = batcher.TraceClosestAsync(Rays(10, 1), new HitId[10], 0, CancellationToken.None);
        await device.DrainerWaiting.Task.WaitAsync(Patience);
        HitId[] doomedHits = Sentinels(20);
        Task doomed = batcher.TraceClosestAsync(Rays(20, 2), doomedHits, 0, cancel.Token);
        Assert.Equal(1, batcher.OpenSlabCount);
        await cancel.CancelAsync();
        device.Gate.Set();

        await blocker.WaitAsync(Patience);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => doomed.WaitAsync(Patience));
        Assert.True(SpinWait.SpinUntil(() => batcher.OpenSlabCount == 0, Patience));
        Assert.Equal([10], device.RaysPerDispatch);
        Assert.All(doomedHits, h => Assert.Equal(Sentinel, h));

        // Every slot is usable: three requests of three kinds each open one.
        Ray[] next = Rays(5, 3);
        HitId[] nextHits = new HitId[5];
        await Task.WhenAll(
            batcher.TraceClosestAsync(next, nextHits, 0, CancellationToken.None),
            batcher.TraceClosestAsync(Rays(5, 4), new HitId[5], 7, CancellationToken.None),
            batcher.TraceVisibilityAsync(Rays(5, 5), new ulong[1], 0, CancellationToken.None)).WaitAsync(Patience);
        Assert.Equal(next.Select(Closest), nextHits);
        Assert.Equal(0, device.StageCalls);
        batcher.Close(() => { });
    }

    /// <summary>
    /// A request cancelled while it shares an open slab goes out with the
    /// slab (its neighbour needs it), but its answers are never written.
    /// </summary>
    [Fact]
    public async Task ARequestCancelledInASharedOpenSlabIsNeverWritten()
    {
        FakeDevice device = CallersDevice(256, 1);
        device.Gate.Reset();
        SlabBatcher batcher = new(device, Ids, 0, SlabWrites.Callers);
        using CancellationTokenSource cancel = new();

        Task blocker = batcher.TraceClosestAsync(Rays(10, 1), new HitId[10], 0, CancellationToken.None);
        await device.DrainerWaiting.Task.WaitAsync(Patience);
        HitId[] doomedHits = Sentinels(20);
        Task doomed = batcher.TraceClosestAsync(Rays(20, 2), doomedHits, 0, cancel.Token);
        Ray[] keptRays = Rays(30, 3);
        HitId[] keptHits = new HitId[30];
        Task kept = batcher.TraceClosestAsync(keptRays, keptHits, 0, CancellationToken.None);
        await cancel.CancelAsync();
        device.Gate.Set();

        await Task.WhenAll(blocker, kept).WaitAsync(Patience);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => doomed.WaitAsync(Patience));
        Assert.Equal([10, 50], device.RaysPerDispatch);
        Assert.Equal(keptRays.Select(Closest), keptHits);
        Assert.All(doomedHits, h => Assert.Equal(Sentinel, h));
        batcher.Close(() => { });
    }

    /// <summary>A request already cancelled when it is handed over reserves and writes nothing.</summary>
    [Fact]
    public async Task AnAlreadyCancelledRequestReservesNothing()
    {
        FakeDevice device = CallersDevice(256, 1);
        SlabBatcher batcher = new(device, Ids, 0, SlabWrites.Callers);
        int writes = 0;
        batcher.ObserveCallerWrite = _ => Interlocked.Increment(ref writes);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => batcher.TraceClosestAsync(Rays(10, 1), new HitId[10], 0, new CancellationToken(true)).WaitAsync(Patience));

        Assert.Equal(0, writes);
        Assert.Equal(0, batcher.OpenSlabCount);
        Assert.Empty(device.RaysPerDispatch);
        batcher.Close(() => { });
    }

    /// <summary>
    /// A failed submit or a failed completion of a slab the callers wrote
    /// faults exactly its requests; its slot is handed out again, and the
    /// next request is written and answered as usual.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedCallersSlabFaultsOnlyItsRequestsAndItsSlotIsReused(bool atCompletion)
    {
        FakeDevice device = CallersDevice(256, 1);
        InvalidOperationException error = new("device lost");
        if (atCompletion)
        {
            device.FailComplete = (0, error);
        }
        else
        {
            device.FailSubmit = (0, error);
        }

        SlabBatcher batcher = new(device, Ids, 0, SlabWrites.Callers);
        int opened = device.OpenCalls;
        Assert.Equal(device.SlotCount, opened);

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => batcher.TraceClosestAsync(Rays(10, 1), new HitId[10], 0, CancellationToken.None).WaitAsync(Patience));
        Assert.Same(error, thrown);

        Ray[] rays = Rays(10, 2);
        HitId[] hits = new HitId[10];
        await batcher.TraceClosestAsync(rays, hits, 0, CancellationToken.None).WaitAsync(Patience);
        Assert.Equal(rays.Select(Closest), hits);
        Assert.Equal(0, device.StageCalls);
        Assert.True(SpinWait.SpinUntil(() => device.OpenCalls == opened + 2, Patience)); // both slots came back
        Assert.Equal(0, device.InFlight);
        batcher.Close(() => { });
    }

    /// <summary>
    /// A slot whose memory cannot be handed out again (its wait failed, so
    /// the device may still be using it) is never given to a caller: the
    /// next request is left to the drainer, whose staging call waits the
    /// slot out, and once that slab lands the slot is handed out again.
    /// </summary>
    [Fact]
    public async Task ASlotThatCannotBeHandedOutIsLeftToTheDrainersPack()
    {
        // One slot, which is also the only one that may be on the device.
        FakeDevice device = new(256, 1, 1) { FailOpen = (1, new InvalidOperationException("still busy")) };
        SlabBatcher batcher = new(device, Ids, 0, SlabWrites.Callers);

        Ray[][] rays = [Rays(10, 1), Rays(10, 2), Rays(10, 3)];
        foreach (Ray[] r in rays)
        {
            HitId[] hits = new HitId[r.Length];
            await batcher.TraceClosestAsync(r, hits, 0, CancellationToken.None).WaitAsync(Patience);
            Assert.Equal(r.Select(Closest), hits);
            Assert.True(SpinWait.SpinUntil(() => device.InFlight == 0 && device.OpenCalls >= 2, Patience));
        }

        // Written by the caller, then packed by the drainer on the suspect
        // slot, then written by the caller again once the slot came back.
        Assert.Equal(1, device.StageCalls);
        Assert.Equal(20, batcher.Statistics.CallerRays);
        Assert.Equal(30, batcher.Statistics.SlabRays);
        batcher.Close(() => { });
    }

    /// <summary>
    /// A caller whose own write fails faults its own request, releases its
    /// reservation, and leaves the batcher serving the next one.
    /// </summary>
    [Fact]
    public async Task ACallersFailedWriteFaultsItsRequestAlone()
    {
        FakeDevice device = CallersDevice(256, 1);
        SlabBatcher batcher = new(device, Ids, 0, SlabWrites.Callers);
        InvalidOperationException error = new("write failed");
        int calls = 0;
        batcher.ObserveCallerWrite = _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw error;
            }
        };

        Task failed = batcher.TraceClosestAsync(Rays(10, 1), new HitId[10], 0, CancellationToken.None);
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => failed.WaitAsync(Patience)));
        Assert.Equal(0, batcher.PendingWriters);

        Ray[] rays = Rays(10, 2);
        HitId[] hits = new HitId[10];
        await batcher.TraceClosestAsync(rays, hits, 0, CancellationToken.None).WaitAsync(Patience);
        Assert.Equal(rays.Select(Closest), hits);
        batcher.Close(() => { });
    }

    /// <summary>
    /// Closing drops the open slabs and fails their requests, lands the slab
    /// on the device, and releases the device once, with no slot, reservation
    /// or thread left behind.
    /// </summary>
    [Fact]
    public async Task ClosingDropsOpenSlabsAndFailsTheirRequests()
    {
        FakeDevice device = CallersDevice(256, 1);
        device.Gate.Reset();
        SlabBatcher batcher = new(device, Ids, 0, SlabWrites.Callers);
        int released = 0;

        Ray[] inFlightRays = Rays(10, 1);
        HitId[] inFlightHits = new HitId[10];
        Task inFlight = batcher.TraceClosestAsync(inFlightRays, inFlightHits, 0, CancellationToken.None);
        await device.DrainerWaiting.Task.WaitAsync(Patience);
        Task open = batcher.TraceClosestAsync(Rays(20, 2), new HitId[20], 0, CancellationToken.None);
        Task split = batcher.TraceVisibilityAsync(Rays(300, 3), new ulong[5], 0, CancellationToken.None);
        Assert.Equal(2, batcher.OpenSlabCount);

        Task closing = Task.Run(() => batcher.Close(() => released++));
        Assert.True(SpinWait.SpinUntil(() => batcher.IsClosed, Patience));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => open.WaitAsync(Patience));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => split.WaitAsync(Patience));
        Assert.False(closing.IsCompleted, "the device was released with a slab still on it");

        device.Gate.Set();
        await closing.WaitAsync(Patience);
        await inFlight.WaitAsync(Patience);
        Assert.Equal(inFlightRays.Select(Closest), inFlightHits);
        Assert.Equal([10], device.RaysPerDispatch);
        Assert.Equal(1, released);
        Assert.Equal(0, batcher.OpenSlabCount);
        Assert.Equal(0, batcher.PendingWriters);
        Assert.All(batcher.Threads, t => Assert.False(t.IsAlive));
    }

    /// <summary>
    /// Close waits for a caller still writing into a slot before it releases
    /// the device (releasing it unmaps that memory), then fails the caller's
    /// request: the slab it wrote never goes out.
    /// </summary>
    [Fact]
    public async Task CloseWaitsForACallerStillWritingBeforeReleasingTheDevice()
    {
        FakeDevice device = CallersDevice(256, 1);
        SlabBatcher batcher = new(device, Ids, 0, SlabWrites.Callers);
        using ManualResetEventSlim writing = new();
        using ManualResetEventSlim finish = new();
        batcher.ObserveCallerWrite = _ =>
        {
            writing.Set();
            Assert.True(finish.Wait(Patience));
        };
        int released = 0;

        Task? request = null;
        Thread writer = new(() => request = batcher.TraceClosestAsync(Rays(10, 1), new HitId[10], 0, CancellationToken.None))
        {
            IsBackground = true,
        };
        writer.Start();
        Assert.True(writing.Wait(Patience));

        Task closing = Task.Run(() => batcher.Close(() => released++));
        Assert.True(SpinWait.SpinUntil(() => batcher.IsClosed, Patience));
        Thread.Sleep(50);
        Assert.False(closing.IsCompleted, "Close released the device under a caller's write");
        Assert.Equal(0, released);

        finish.Set();
        Assert.True(writer.Join(Patience));
        await closing.WaitAsync(Patience);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => request!.WaitAsync(Patience));
        Assert.Equal(1, released);
        Assert.Empty(device.RaysPerDispatch);
        Assert.Equal(0, batcher.PendingWriters);
        Assert.Equal(0, batcher.OpenSlabCount);
    }

    /// <summary>
    /// The callers' path starts one thread of the batcher's own, the drainer,
    /// which is not a pool thread, and Close joins it whether the slabs
    /// succeeded or failed.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TheCallersPathLeavesNoThreadOrReservationAfterClose(int failure)
    {
        FakeDevice device = CallersDevice(1024, 1);
        if (failure == 1)
        {
            device.FailSubmit = (1, new InvalidOperationException("submit"));
        }
        else if (failure == 2)
        {
            device.FailComplete = (1, new InvalidOperationException("complete"));
        }

        SlabBatcher batcher = new(device, Ids, 0, SlabWrites.Callers);
        await batcher.TraceClosestAsync(Rays(300, 5), new HitId[300], 0, CancellationToken.None).WaitAsync(Patience);
        Task second = batcher.TraceClosestAsync(Rays(300, 6), new HitId[300], 0, CancellationToken.None);
        if (failure == 0)
        {
            await second.WaitAsync(Patience);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => second.WaitAsync(Patience));
        }

        IReadOnlyList<Thread> threads = batcher.Threads;
        Thread drainer = Assert.Single(threads);
        Assert.True(drainer.IsAlive);
        Assert.False(drainer.IsThreadPoolThread);

        batcher.Close(() => { });

        Assert.False(drainer.IsAlive, "the drainer outlived Close");
        Assert.Equal(0, batcher.PendingWriters);
        Assert.Equal(0, batcher.OpenSlabCount);
    }

    /// <summary>
    /// Callers on pool threads write on those threads, inside their own
    /// call, and nothing of the batcher's runs on the pool: no pack chunk,
    /// no write, no drainer.
    /// </summary>
    [Fact]
    public async Task ACallersWriteRunsInsideItsOwnCallAndNothingElseOnThePool()
    {
        FakeDevice device = CallersDevice(4096, 1);
        SlabBatcher batcher = new(device, Ids, 0, SlabWrites.Callers);
        ConcurrentBag<int> packed = [];
        ConcurrentBag<int> writerThreads = [];
        batcher.ObservePackChunk = packed.Add;
        batcher.ObserveCallerWrite = _ => writerThreads.Add(Environment.CurrentManagedThreadId);
        ConcurrentBag<int> callerThreads = [];
        bool drainerOnPool;
        try
        {
            Task[] calls = [.. Enumerable.Range(0, 32).Select(k => Task.Run(() =>
            {
                callerThreads.Add(Environment.CurrentManagedThreadId);
                int before = writerThreads.Count(t => t == Environment.CurrentManagedThreadId);
                Task call = batcher.TraceClosestAsync(Rays(100, k), new HitId[100], 0, CancellationToken.None);

                // The write is done before the call returns, on this thread.
                Assert.Equal(before + 1, writerThreads.Count(t => t == Environment.CurrentManagedThreadId));
                return call;
            }))];
            await Task.WhenAll(calls).WaitAsync(Patience);
            drainerOnPool = Assert.Single(batcher.Threads).IsThreadPoolThread;
        }
        finally
        {
            batcher.Close(() => { });
        }

        Assert.Empty(packed);
        Assert.Equal(32, writerThreads.Count);
        Assert.All(writerThreads, t => Assert.Contains(t, callerThreads));
        Assert.False(drainerOnPool);
    }

    [Fact]
    public void FitStartsVisibilitySegmentsOnAWorkgroupAndSplitsThemOnOne()
    {
        // Closest: right after what is there, as much as fits.
        Assert.Equal(30, SlabBatcher.Fit(10, 256, 1, 30, out int offset));
        Assert.Equal(10, offset);
        Assert.Equal(246, SlabBatcher.Fit(10, 256, 1, 300, out offset));
        Assert.Equal(10, offset);
        Assert.Equal(0, SlabBatcher.Fit(256, 256, 1, 5, out _));

        // Visibility: on the next workgroup; whole, or cut on a workgroup.
        Assert.Equal(30, SlabBatcher.Fit(10, 256, 0, 30, out offset));
        Assert.Equal(64, offset);
        Assert.Equal(192, SlabBatcher.Fit(10, 256, 0, 300, out offset));
        Assert.Equal(64, offset);
        Assert.Equal(40, SlabBatcher.Fit(130, 256, 0, 40, out offset)); // whole, into the last workgroup
        Assert.Equal(192, offset);
        Assert.Equal(64, SlabBatcher.Fit(130, 256, 0, 100, out offset)); // the last workgroup, and the rest waits
        Assert.Equal(192, offset);
        Assert.True(SlabBatcher.Fit(200, 256, 0, 10, out offset) <= 0); // the rest of the last workgroup is padding
        Assert.Equal(256, offset);
        Assert.Equal(0, SlabBatcher.Fit(0, 256, 0, 0, out offset));
        Assert.Equal(0, offset);
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(2, 3)]
    public void ADeviceWhoseInFlightLimitIsOutsideItsSlotsIsRefused(int slots, int inFlight) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlabBatcher(new FakeDevice(64, slots, inFlight), Ids, 0));
}
