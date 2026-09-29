//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SourceSharp.MapTools.Rad.Bounce;

/// <summary>
/// The scratch pool of one vrad compile: arrays handed back by one worker or
/// stage are handed out again to the next that fits, and every array the
/// pool still holds is dropped when the compile ends.
/// </summary>
/// <remarks>
/// <para>
/// WHY NOT <see cref="System.Buffers.ArrayPool{T}.Shared"/>. vrad's scratch
/// used to come from the process's shared pool, and a profile of 2fort showed
/// it handing out far more large arrays than a compile ever holds at once.
/// Three properties of the shared pool were behind that, and each is wrong
/// for this workload:
/// </para>
/// <list type="bullet">
/// <item><description>
/// It rounds every request up to a power of two. A leaf-ambient batch of
/// 217,599 segments got a 262,144-ray array, 20 % more than it asked for, on
/// every worker; the chunk buffers of the transfer build fell just under a
/// bucket and were lucky. Here a fresh array is exactly as long as asked.
/// </description></item>
/// <item><description>
/// It keeps at most a handful of arrays per size per core, and a stage hands
/// its workers' arrays back from ONE thread when it ends, so on a machine
/// with many workers most of them were dropped on the floor and the next
/// stage allocated them again. Here nothing handed back is refused.
/// </description></item>
/// <item><description>
/// It is process-wide and keeps what it holds until a full collection finds
/// it idle for a minute: a long-lived service kept one map's worth of large
/// scratch alive after that compile ended, however small the next map. Here
/// the pool belongs to the compile and is dropped with it.
/// </description></item>
/// </list>
/// <para>
/// ONE SHARD PER WORKER, AND NO LOCKS. The first version of this pool was one
/// list behind one lock, and on a 32-thread 2fort compile that lock was the
/// second most contended in the whole compile: 1,300 waits and 530 to 630 ms
/// of waiting, against about 1 ms for the shared pool it replaced. Rentals
/// are few (a few thousand a compile), but they come in bursts -- every
/// worker of a stage grows its batch or ray log at the same moment, doubling
/// a dozen times -- and a thread holding the lock could be stopped there by
/// a collection that another worker's large allocation had started, so the
/// whole convoy waited out the pause. Now each worker rents through a shard
/// of its own (<see cref="ForWorker"/>); renters that are not a stage's
/// workers (the sky probe, the transfer build) use the pool's shared shard.
/// Splitting the lock per shard was tried first and was not enough: a
/// worker whose own shard has nothing that fits looks through the others,
/// and on a loaded 4-core box those looks queued behind the owners (7 waits,
/// 15 ms at 4 threads, against 9 waits and 2 ms for the single lock). So a
/// shard's idle arrays are an immutable snapshot that a rental or a return
/// replaces with one compare-and-swap: a renter never waits for another, it
/// only retries when someone changed the same shard in between, and a look
/// through someone else's shard is one volatile read. Each snapshot is a
/// small array (a worker's idle arrays are a few dozen at most), so copying
/// it per rental costs less than the lock did.
/// </para>
/// <para>
/// A WORKER'S ARRAYS COME BACK TO ITS OWN SHARD. The shard is chosen by the
/// worker index a stage gives its worker, not by the thread, because the
/// thread that hands a worker's arrays back at the end of a stage is not the
/// worker's thread; keyed by worker, worker 3 of the next stage finds worker
/// 3's arrays of this one where it looks first. Keyed by thread they would
/// all land in the one shard of the thread that disposed the stage, and
/// every worker of the next stage would have to fetch them from there.
/// </para>
/// <para>
/// BEST FIT, ANY SIZE. A rental takes the SHORTEST idle array of the type
/// that is long enough: first from the renter's own shard, and when that has
/// none, the shortest that fits in any other shard (shared or another
/// worker's). No ceiling on how much longer than asked: every renter uses the
/// array's own length as its capacity, so a long array handed to a short
/// request is not waste -- a doubling ray log that is handed a long array on
/// its first rental never grows again. A 2x or 4x ceiling was tried by
/// replaying 2fort's rental log at 16 workers: 719 and 699 misses against
/// 692 without one, for 1 to 2 % fewer bytes, because a ceiling turns those
/// short-circuited growths back into doublings. Every renter treats the
/// array as "at least" the length it asked for; nothing may read what it did
/// not write (an idle array holds whatever its last renter left, and the
/// pool does not clear it -- see <see cref="PoisonByte"/> for the fact that
/// proves nobody depends on its contents).
/// </para>
/// <para>
/// TRIMMED BETWEEN STAGES. Idle arrays cost resident memory, and scratch
/// shapes change from stage to stage: the face-lighting workers' ray logs
/// are no use to the transfer build, whose chunk buffer is no use to the
/// prop and leaf-ambient batches. The compile calls <see cref="Trim"/> where
/// one stage's shapes stop being useful to the next, and the collector gets
/// them back there instead of the pool carrying them to the end. A trim can
/// cost a later allocation, and <see cref="Statistics"/> counts each one
/// (<see cref="ScratchPoolStatistics.TrimRegrets"/>): on 2fort at 16 workers
/// the trim after the face lighting dropped 150 MB and cost the leaf-ambient
/// and prop stages 29 MB of re-allocation, which is the right side of the
/// trade -- keeping the 150 MB would have put it under the bounce, where the
/// transfers make the compile's resident peak.
/// </para>
/// <para>
/// BOUNDED BY THE COMPILE. The pool only ever holds arrays the compile itself
/// allocated and handed back, so it cannot grow past what the compile's
/// scratch needed; the shards are one per worker index a stage used, and the
/// trim log one entry per array a trim dropped. <see cref="Dispose"/> drops
/// all of it and refuses any later rental, and an array returned after that
/// is simply let go. No state is shared with another compile, so two
/// compiles in one process run over two pools that never see each other's
/// arrays.
/// </para>
/// </remarks>
internal sealed class CompileScratchPool : IScratchArrayPool, IDisposable
{
    // Guards only the growth of the worker shard array: made on the thread
    // that sets up a stage, before its workers run.
    private readonly object _grow = new();
    private readonly Shard _shared;

    // Copy-on-grow: readers take the reference once and index it without a
    // lock; a worker index past its end grows it under _grow.
    private volatile Shard[] _workers = [];

    // What the trims let go, for the regret count: a miss that one of these
    // would have served. One entry per dropped array, and a miss claims the
    // entry it matches, so each is counted once. Null until the first trim
    // that drops something, and again once the pool has ended.
    private TrimLog? _trimmed;
    private int _trimmedArrays;
    private long _trimmedBytes;
    private int _trimRegrets;
    private long _trimRegretBytes;

    private volatile bool _disposed;

    /// <summary>Starts an empty pool, with only its shared shard.</summary>
    public CompileScratchPool() => _shared = new Shard(this);

    /// <summary>
    /// When set, every array the pool hands out -- fresh or reused -- is
    /// first filled with this byte, so a renter that reads an element before
    /// writing it reads garbage (0xFF makes every float and vector component
    /// a NaN, 0xCD a large negative number) instead of a lucky zero.
    /// </summary>
    /// <remarks>
    /// For the facts, which light a map with it set and without and compare
    /// the bytes: that is the proof that the pool need not clear what it
    /// hands out. Element types that hold references are never filled (the
    /// collector reads them); vrad's scratch holds none.
    /// </remarks>
    internal byte? PoisonByte { get; init; }

    /// <summary>Bytes of every array this pool has allocated, over its life.</summary>
    public long AllocatedBytes => Sum(static s => s.AllocatedBytes);

    /// <summary>How many arrays this pool has allocated, over its life.</summary>
    public int Allocations => (int)Sum(static s => s.Allocations);

    /// <summary>How many rentals were served from an idle array rather than a new one.</summary>
    public int Reuses => (int)Sum(static s => s.Reuses);

    /// <summary>Arrays rented and not yet returned.</summary>
    /// <remarks>
    /// Summed over the shards: an array rented through one shard and handed
    /// back through another counts up on the first and down on the second.
    /// </remarks>
    public int Outstanding => (int)Sum(static s => s.Outstanding);

    /// <summary>Bytes of the arrays the pool holds idle, waiting for a renter.</summary>
    public long IdleBytes => Sum(static s => s.IdleBytes);

    /// <summary>How many arrays the pool holds idle.</summary>
    public int IdleArrays => (int)Sum(static s => s.IdleCount);

    /// <summary>How many worker shards the pool has made: one per worker index asked for.</summary>
    public int WorkerShards => _workers.Length;

    /// <summary>
    /// Where the pool's rentals went: hits and misses, by element type and
    /// size, what the trims let go and what that cost.
    /// </summary>
    /// <remarks>
    /// Read after the compile (or with no rentals in flight) it is exact;
    /// read during one, each count is a moment's value.
    /// </remarks>
    public ScratchPoolStatistics Statistics
    {
        get
        {
            Dictionary<(Type, int), ScratchRentalCounts> merged = [];
            foreach (Shard shard in AllShards())
            {
                shard.AddCountsTo(merged);
            }

            List<ScratchRentalCounts> byType = [.. merged.Values];
            byType.Sort(static (a, b) =>
            {
                int c = string.CompareOrdinal(a.ElementType.Name, b.ElementType.Name);
                return c != 0 ? c : a.SizeClass.CompareTo(b.SizeClass);
            });

            return new ScratchPoolStatistics(
                Reuses,
                (int)Sum(static s => s.CrossReuses),
                Allocations,
                AllocatedBytes,
                Volatile.Read(ref _trimmedArrays),
                Interlocked.Read(ref _trimmedBytes),
                Volatile.Read(ref _trimRegrets),
                Interlocked.Read(ref _trimRegretBytes),
                byType);
        }
    }

    /// <summary>
    /// The shard worker <paramref name="workerIndex"/> of a stage rents
    /// through: its own arrays first, then everyone else's.
    /// </summary>
    /// <param name="workerIndex">The worker's index in its stage, from zero.</param>
    /// <returns>A pool view whose rentals and returns go to that worker's shard.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="workerIndex"/> is negative.</exception>
    /// <remarks>
    /// The same index in two stages is the same shard, which is the point:
    /// the next stage's worker finds this stage's worker's arrays first. Two
    /// renters that share an index at once are still correct -- every change
    /// to a shard is one compare-and-swap -- they only retry more.
    /// </remarks>
    public IScratchArrayPool ForWorker(int workerIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(workerIndex);
        Shard[] workers = _workers;
        if (workerIndex < workers.Length)
        {
            return workers[workerIndex];
        }

        lock (_grow)
        {
            workers = _workers;
            if (workerIndex >= workers.Length)
            {
                Shard[] grown = new Shard[workerIndex + 1];
                workers.CopyTo(grown, 0);
                for (int i = workers.Length; i < grown.Length; i++)
                {
                    grown[i] = new Shard(this);
                }

                _workers = workers = grown;
            }

            return workers[workerIndex];
        }
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minimumLength"/> is negative.</exception>
    /// <exception cref="ObjectDisposedException">The compile has ended.</exception>
    /// <remarks>
    /// Through the shared shard, for a renter that is not one of a stage's
    /// workers. A zero-length request is served with an empty array and
    /// never counted: an empty array holds nothing to give back.
    /// </remarks>
    public T[] Rent<T>(int minimumLength) => _shared.Rent<T>(minimumLength);

    /// <inheritdoc/>
    /// <remarks>
    /// Into the shared shard. An empty array is ignored. After
    /// <see cref="Dispose"/> the array is let go rather than kept: a batch
    /// that outlived its compile must not hand anything to a pool nobody
    /// will empty.
    /// </remarks>
    public void Return<T>(T[] array) => _shared.Return(array);

    /// <summary>
    /// Lets go of every idle array in every shard, for the collector; rented
    /// arrays are not affected and may still be returned (and kept)
    /// afterwards.
    /// </summary>
    public void Trim()
    {
        List<Dropped> dropped = [];
        foreach (Shard shard in AllShards())
        {
            foreach (Idle idle in shard.Drain())
            {
                dropped.Add(new Dropped(idle.Array.GetType(), idle.Array.Length, idle.Bytes));
            }
        }

        if (dropped.Count == 0 || _disposed)
        {
            return;
        }

        foreach (Dropped d in dropped)
        {
            Interlocked.Increment(ref _trimmedArrays);
            Interlocked.Add(ref _trimmedBytes, d.Bytes);
        }

        TrimLog? log;
        do
        {
            log = Volatile.Read(ref _trimmed);
        }
        while (Interlocked.CompareExchange(ref _trimmed, TrimLog.Append(log, dropped), log) != log);

        // An end that raced the append above must not leave the log behind.
        if (_disposed)
        {
            Volatile.Write(ref _trimmed, null);
        }
    }

    /// <summary>
    /// Ends the pool with the compile: every idle array is let go, later
    /// rentals throw and later returns are let go. Safe to call more than
    /// once.
    /// </summary>
    public void Dispose()
    {
        // Set before any shard is emptied. A return checks it again after
        // adding its array, so either the drain below takes that array or
        // the return sees the end and drains its shard itself.
        _disposed = true;
        foreach (Shard shard in AllShards())
        {
            _ = shard.Drain();
        }

        Volatile.Write(ref _trimmed, null);
    }

    private static long Bytes<T>(int length) => (long)length * Unsafe.SizeOf<T>();

    /// <summary>The size class of a request: the power of two its length rounds up to, as an exponent.</summary>
    private static int SizeClass(int length) => length <= 1 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)(length - 1));

    /// <summary>The index of the shortest idle array of <typeparamref name="T"/> at least that long, or -1.</summary>
    private static int BestFit<T>(Idle[] idle, int minimumLength)
    {
        int best = -1;
        int bestLength = int.MaxValue;
        for (int i = 0; i < idle.Length; i++)
        {
            if (idle[i].Array is T[] candidate && candidate.Length >= minimumLength && candidate.Length < bestLength)
            {
                best = i;
                bestLength = candidate.Length;
            }
        }

        return best;
    }

    /// <summary>A snapshot without its element <paramref name="index"/>.</summary>
    private static TItem[] Without<TItem>(TItem[] items, int index)
    {
        if (items.Length == 1)
        {
            return [];
        }

        TItem[] next = new TItem[items.Length - 1];
        items.AsSpan(0, index).CopyTo(next);
        items.AsSpan(index + 1).CopyTo(next.AsSpan(index));
        return next;
    }

    private long Sum(Func<Shard, long> read)
    {
        long total = 0;
        foreach (Shard shard in AllShards())
        {
            total += read(shard);
        }

        return total;
    }

    private IEnumerable<Shard> AllShards()
    {
        yield return _shared;
        foreach (Shard shard in _workers)
        {
            yield return shard;
        }
    }

    /// <summary>
    /// The slow path of a rental whose own shard had nothing long enough:
    /// the shortest idle array that fits in any other shard, or null.
    /// </summary>
    /// <remarks>
    /// Finds the shard holding the best fit from each shard's current
    /// snapshot, then takes it with that shard's compare-and-swap. Another
    /// renter may take that array in between; then the search starts again,
    /// a few times, before the rental gives up and allocates -- which would
    /// cost memory and never correctness.
    /// </remarks>
    private T[]? Borrow<T>(Shard renter, int minimumLength)
    {
        const int Attempts = 4;
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            Shard? best = null;
            int bestLength = int.MaxValue;
            foreach (Shard shard in AllShards())
            {
                if (!ReferenceEquals(shard, renter))
                {
                    int length = shard.BestFitLength<T>(minimumLength);
                    if (length < bestLength)
                    {
                        best = shard;
                        bestLength = length;
                    }
                }
            }

            if (best is null)
            {
                return null;
            }

            if (best.TakeBestFit<T>(minimumLength) is T[] taken)
            {
                return taken;
            }
        }

        return null;
    }

    /// <summary>Counts a miss that an array some trim dropped would have served.</summary>
    private void NoteMiss<T>(int minimumLength)
    {
        if (Volatile.Read(ref _trimmed)?.Claim(typeof(T[]), minimumLength) == true)
        {
            Interlocked.Increment(ref _trimRegrets);
            Interlocked.Add(ref _trimRegretBytes, Bytes<T>(minimumLength));
        }
    }

    /// <summary>Fills a handed-out array with <see cref="PoisonByte"/>, when that is set.</summary>
    private void Poison<T>(T[] array)
    {
        if (PoisonByte is byte poison && !RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            ref byte start = ref Unsafe.As<T, byte>(ref MemoryMarshal.GetArrayDataReference(array));
            long bytes = Bytes<T>(array.Length);
            for (long done = 0; done < bytes;)
            {
                int chunk = (int)Math.Min(bytes - done, int.MaxValue);
                MemoryMarshal.CreateSpan(ref Unsafe.AddByteOffset(ref start, (nint)done), chunk).Fill(poison);
                done += chunk;
            }
        }
    }

    /// <summary>An idle array and its size in bytes.</summary>
    private readonly record struct Idle(Array Array, long Bytes);

    /// <summary>What a trim let go: the array's type and length, not the array.</summary>
    private readonly record struct Dropped(Type ArrayType, int Length, long Bytes);

    /// <summary>
    /// Every array the trims let go that no miss has claimed yet, each with a
    /// flag a miss sets once to claim it.
    /// </summary>
    /// <remarks>
    /// Replaced whole by a trim (which drops the claimed entries as it
    /// copies), and claimed in place by misses, with one compare-and-swap
    /// on the entry's flag: a claim allocates nothing, which matters because
    /// a stage's first rentals after a trim are misses, all at once. A claim
    /// racing a trim can be lost from the copy and counted again; the
    /// compile never rents while it trims, so that stays theoretical.
    /// </remarks>
    private sealed class TrimLog(Dropped[] entries)
    {
        private readonly int[] _claimed = new int[entries.Length];

        /// <summary>A log of <paramref name="log"/>'s unclaimed entries and <paramref name="dropped"/>.</summary>
        public static TrimLog Append(TrimLog? log, List<Dropped> dropped)
        {
            List<Dropped> entries = [];
            if (log is not null)
            {
                for (int i = 0; i < log._claimed.Length; i++)
                {
                    if (Volatile.Read(ref log._claimed[i]) == 0)
                    {
                        entries.Add(log.Entry(i));
                    }
                }
            }

            entries.AddRange(dropped);
            return new TrimLog([.. entries]);
        }

        /// <summary>Claims the shortest unclaimed entry of the type at least that long; false when there is none.</summary>
        public bool Claim(Type arrayType, int minimumLength)
        {
            while (true)
            {
                int best = -1;
                for (int i = 0; i < entries.Length; i++)
                {
                    Dropped d = entries[i];
                    if (d.ArrayType == arrayType
                        && d.Length >= minimumLength
                        && (best < 0 || d.Length < entries[best].Length)
                        && Volatile.Read(ref _claimed[i]) == 0)
                    {
                        best = i;
                    }
                }

                if (best < 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _claimed[best], 1, 0) == 0)
                {
                    return true;
                }
            }
        }

        private Dropped Entry(int index) => entries[index];
    }

    /// <summary>
    /// One worker's idle arrays (or the shared ones) and the counts of what
    /// was rented through it.
    /// </summary>
    /// <remarks>
    /// The idle arrays are an immutable snapshot replaced by
    /// compare-and-swap, and the counts are interlocked, so nothing here
    /// ever blocks. The counts by type and size are a concurrent map whose
    /// entries are made once per type and size class and then only
    /// incremented.
    /// </remarks>
    private sealed class Shard(CompileScratchPool pool) : IScratchArrayPool
    {
        private readonly ConcurrentDictionary<(Type, int), ScratchRentalCounts> _counts = new();
        private Idle[] _idle = [];
        private long _allocatedBytes;
        private int _allocations;
        private int _reuses;
        private int _crossReuses;
        private int _outstanding;

        public long AllocatedBytes => Interlocked.Read(ref _allocatedBytes);

        public long Allocations => Volatile.Read(ref _allocations);

        public long Reuses => Volatile.Read(ref _reuses);

        public long CrossReuses => Volatile.Read(ref _crossReuses);

        public long Outstanding => Volatile.Read(ref _outstanding);

        public long IdleBytes
        {
            get
            {
                long total = 0;
                foreach (Idle idle in Volatile.Read(ref _idle))
                {
                    total += idle.Bytes;
                }

                return total;
            }
        }

        public long IdleCount => Volatile.Read(ref _idle).Length;

        public T[] Rent<T>(int minimumLength)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);
            if (minimumLength == 0)
            {
                return [];
            }

            ObjectDisposedException.ThrowIf(pool._disposed, pool);

            // The fast path: this worker's own arrays.
            T[]? found = TakeBestFit<T>(minimumLength);
            bool cross = false;
            if (found is null)
            {
                found = pool.Borrow<T>(this, minimumLength);
                cross = found is not null;
            }

            ScratchRentalCounts counts = Count<T>(minimumLength);
            if (found is not null)
            {
                Interlocked.Increment(ref _reuses);
                if (cross)
                {
                    Interlocked.Increment(ref _crossReuses);
                }

                counts.AddHit();
            }
            else
            {
                // Uninitialised: the contract already says a rented array
                // holds unspecified data, and a large one would otherwise be
                // zeroed only to be overwritten. Counted only once it exists,
                // so an allocation that throws leaves the counts as they were.
                found = GC.AllocateUninitializedArray<T>(minimumLength);
                long bytes = Bytes<T>(minimumLength);
                Interlocked.Increment(ref _allocations);
                Interlocked.Add(ref _allocatedBytes, bytes);
                counts.AddMiss(bytes);
                pool.NoteMiss<T>(minimumLength);
            }

            Interlocked.Increment(ref _outstanding);
            pool.Poison(found);
            return found;
        }

        public void Return<T>(T[] array)
        {
            ArgumentNullException.ThrowIfNull(array);
            if (array.Length == 0)
            {
                return;
            }

            Interlocked.Decrement(ref _outstanding);
            if (pool._disposed)
            {
                return;
            }

            Idle idle = new(array, Bytes<T>(array.Length));
            Idle[] snapshot;
            do
            {
                snapshot = Volatile.Read(ref _idle);
            }
            while (Interlocked.CompareExchange(ref _idle, [.. snapshot, idle], snapshot) != snapshot);

            // The end may have drained this shard between the check above and
            // the swap; if so, this array must not stay behind.
            if (pool._disposed)
            {
                _ = Drain();
            }
        }

        /// <summary>Workers share their arrays; a worker's view of a worker's shard is the pool's.</summary>
        public IScratchArrayPool ForWorker(int workerIndex) => pool.ForWorker(workerIndex);

        /// <summary>The length of this shard's best fit, or <see cref="int.MaxValue"/>.</summary>
        public int BestFitLength<T>(int minimumLength)
        {
            Idle[] snapshot = Volatile.Read(ref _idle);
            int best = BestFit<T>(snapshot, minimumLength);
            return best < 0 ? int.MaxValue : snapshot[best].Array.Length;
        }

        /// <summary>Takes this shard's best fit out of it, or null when it has none.</summary>
        public T[]? TakeBestFit<T>(int minimumLength)
        {
            while (true)
            {
                Idle[] snapshot = Volatile.Read(ref _idle);
                int best = BestFit<T>(snapshot, minimumLength);
                if (best < 0)
                {
                    return null;
                }

                if (Interlocked.CompareExchange(ref _idle, Without(snapshot, best), snapshot) == snapshot)
                {
                    return (T[])snapshot[best].Array;
                }
            }
        }

        /// <summary>Lets every idle array go, and says which they were.</summary>
        public Idle[] Drain() => Interlocked.Exchange(ref _idle, []);

        public void AddCountsTo(Dictionary<(Type, int), ScratchRentalCounts> merged)
        {
            foreach (KeyValuePair<(Type, int), ScratchRentalCounts> kv in _counts)
            {
                if (!merged.TryGetValue(kv.Key, out ScratchRentalCounts? into))
                {
                    merged[kv.Key] = into = new ScratchRentalCounts(kv.Value.ElementType, kv.Value.SizeClass);
                }

                into.Add(kv.Value);
            }
        }

        private ScratchRentalCounts Count<T>(int minimumLength) =>
            _counts.GetOrAdd((typeof(T), SizeClass(minimumLength)), static key => new ScratchRentalCounts(key.Item1, key.Item2));
    }
}

/// <summary>Rentals of one element type and size class, over a pool's life.</summary>
/// <param name="elementType">The element type.</param>
/// <param name="sizeClass">
/// The requests counted: those of more than 2^(<paramref name="sizeClass"/>-1)
/// and at most 2^<paramref name="sizeClass"/> elements.
/// </param>
internal sealed class ScratchRentalCounts(Type elementType, int sizeClass)
{
    private int _hits;
    private int _misses;
    private long _allocatedBytes;

    /// <summary>The element type.</summary>
    public Type ElementType { get; } = elementType;

    /// <summary>The size class: requests up to 2^this elements, and more than half that.</summary>
    public int SizeClass { get; } = sizeClass;

    /// <summary>Rentals served from an idle array.</summary>
    public int Hits => Volatile.Read(ref _hits);

    /// <summary>Rentals that allocated.</summary>
    public int Misses => Volatile.Read(ref _misses);

    /// <summary>Bytes those allocations took.</summary>
    public long AllocatedBytes => Interlocked.Read(ref _allocatedBytes);

    /// <summary>Counts a rental served from an idle array.</summary>
    internal void AddHit() => Interlocked.Increment(ref _hits);

    /// <summary>Counts a rental that allocated <paramref name="bytes"/>.</summary>
    internal void AddMiss(long bytes)
    {
        Interlocked.Increment(ref _misses);
        Interlocked.Add(ref _allocatedBytes, bytes);
    }

    /// <summary>Adds another shard's counts of the same type and size class.</summary>
    internal void Add(ScratchRentalCounts other)
    {
        _hits += other.Hits;
        _misses += other.Misses;
        _allocatedBytes += other.AllocatedBytes;
    }
}

/// <summary>What a <see cref="CompileScratchPool"/> did over its life.</summary>
/// <param name="Hits">Rentals served from an idle array.</param>
/// <param name="CrossWorkerHits">Of the hits, those served from another shard than the renter's own.</param>
/// <param name="Misses">Rentals that allocated.</param>
/// <param name="AllocatedBytes">Bytes the misses allocated.</param>
/// <param name="TrimmedArrays">Idle arrays the trims let go.</param>
/// <param name="TrimmedBytes">Their bytes.</param>
/// <param name="TrimRegrets">Misses that an array a trim had let go would have served.</param>
/// <param name="TrimRegretBytes">The bytes those misses allocated.</param>
/// <param name="ByTypeAndSize">Hits, misses and bytes by element type and size class.</param>
internal sealed record ScratchPoolStatistics(
    int Hits,
    int CrossWorkerHits,
    int Misses,
    long AllocatedBytes,
    int TrimmedArrays,
    long TrimmedBytes,
    int TrimRegrets,
    long TrimRegretBytes,
    IReadOnlyList<ScratchRentalCounts> ByTypeAndSize)
{
    /// <summary>Every rental that was not for zero elements.</summary>
    public int Rentals => Hits + Misses;
}

/// <summary>
/// A scratch "pool" that pools nothing: every rental is a new array and every
/// return lets the array go.
/// </summary>
/// <remarks>
/// For scratch made outside a compile -- a one-off <see cref="TestLineBatch"/>
/// a host or a fact makes on its own -- where there is no compile to own a
/// <see cref="CompileScratchPool"/> and so nobody to empty one. It keeps
/// nothing alive, so it needs no disposal and cannot leak.
/// </remarks>
internal sealed class UnpooledScratch : IScratchArrayPool
{
    /// <inheritdoc/>
    public T[] Rent<T>(int minimumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);
        return minimumLength == 0 ? [] : GC.AllocateUninitializedArray<T>(minimumLength);
    }

    /// <inheritdoc/>
    public void Return<T>(T[] array) => ArgumentNullException.ThrowIfNull(array);
}
