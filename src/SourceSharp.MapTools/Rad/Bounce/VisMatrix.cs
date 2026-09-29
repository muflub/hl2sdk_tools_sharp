//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rad.Bounce;

/// <summary>Counts from one transfer build.</summary>
public sealed class VisMatrixStatistics
{
    /// <summary>Patches that shot rays: every leaf patch in a cluster.</summary>
    public int Receivers { get; internal set; }

    /// <summary>Visibility rays traced: one per candidate patch pair.</summary>
    public long Rays { get; internal set; }

    /// <summary>How many rays came back blocked.</summary>
    public long Blocked { get; internal set; }

    /// <summary>How many chunks the build ran in.</summary>
    public int Chunks { get; internal set; }

    /// <summary>The largest chunk, in rays: what the ray buffers were sized to.</summary>
    public int LargestChunk { get; internal set; }

    /// <summary>
    /// How many candidate enumerators the build made: one per worker slot
    /// that ran, however many passes and chunks there were.
    /// </summary>
    internal int Enumerators { get; set; }
}

/// <summary>
/// Which patches can see each other, and the transfers
/// that say how much light each passes on (<c>BuildVisMatrix</c> and the
/// <c>MakeTransfer</c>/<c>MakeScales</c> of the reference implementation).
/// </summary>
/// <remarks>
/// <para>
/// <b>Shape.</b> Stock runs one thread work item per cluster; for each leaf
/// patch in the cluster (<c>clusterChildren</c>) it walks the cluster's PVS,
/// pushes one ray per candidate patch into a four-wide stream, flushes, makes a
/// transfer for every ray that got through, and scales the list. Here the
/// same patches, in the same order (cluster, then the cluster's child list),
/// are the work items, and the rays go to the batch <see cref="IRayTracer"/>:
/// </para>
/// <list type="number">
/// <item><description>a COUNT pass enumerates every patch's candidates in
/// parallel and keeps only how many there are;</description></item>
/// <item><description>patches are cut into chunks of consecutive patches of at
/// most <see cref="RaysPerChunk"/> rays -- boundaries that depend only on the
/// map;</description></item>
/// <item><description>per chunk, a FILL pass enumerates again and writes each
/// patch's rays at its offset, the rays are traced in slabs, and a TRANSFER
/// pass makes and scales each patch's transfers in place over its own ray
/// range;</description></item>
/// <item><description>each chunk is compacted, on the workers, into its own
/// segment, which <see cref="TransferSet"/> keeps as it is.</description></item>
/// </list>
/// <para>
/// <b>Why no per-worker staging.</b> Stock gives each thread three
/// <c>MAX_PATCHES</c>-sized arrays and a fourth for
/// the unscaled transfers, because a patch's transfer count is
/// unknown until its rays are traced. Here each patch already owns a disjoint
/// range of the chunk -- its rays -- and a patch never has more transfers than
/// rays, so its unscaled transfers are staged in that same range -- of the
/// ray buffer itself, whose rays are dead by then. Nothing is
/// shared between workers, nothing is merged, and every result sits at an
/// index fixed before the pass began: the output is byte-identical at any
/// thread count by construction.
/// </para>
/// </remarks>
public sealed class VisMatrix
{
    /// <summary>
    /// The most rays one chunk holds: 2M rays is 56 MB of rays and 8 MB of
    /// receiver indices, the transfers being staged over the rays.
    /// </summary>
    public const int RaysPerChunk = 2 * 1024 * 1024;

    /// <summary>
    /// The chunk bound <see cref="BuildAsync"/> uses, <see cref="RaysPerChunk"/>
    /// unless a test asks for another: the transfers must not depend on it.
    /// </summary>
    internal int ChunkRays { get; init; } = RaysPerChunk;

    /// <summary>Rays per tracer call. A multiple of 64, so a slab owns whole words of bits.</summary>
    public const int RaysPerTraceSlab = 16 * 1024;

    /// <summary>
    /// The slab <see cref="BuildAsync"/> traces in, <see cref="RaysPerTraceSlab"/>
    /// unless a test asks for a smaller one to get several slabs out of a
    /// small map. A positive multiple of 64.
    /// </summary>
    /// <remarks>
    /// Checked here because nothing downstream would notice: slabs are traced
    /// concurrently, each writing its rays' hit bits into one shared array of
    /// 64-bit words. A slab that ends inside a word shares that word with the
    /// next slab, and two tracer calls writing it at once can lose each other's
    /// bits, so a transfer would come and go with the thread timing rather
    /// than fail.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a positive multiple of 64.</exception>
    internal int TraceSlabRays
    {
        get => _traceSlabRays;
        init
        {
            if (value <= 0 || value % 64 != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(TraceSlabRays), value, "a trace slab is a positive multiple of 64 rays, so it owns whole words of hit bits");
            }

            _traceSlabRays = value;
        }
    }

    private readonly int _traceSlabRays = RaysPerTraceSlab;

    /// <summary>
    /// Where the build's scratch comes from and goes back to: the compile's
    /// <see cref="CompileScratchPool"/>, or a test's that counts it. Null
    /// makes the build a pool of its own, dropped when the build ends.
    /// </summary>
    internal IScratchArrayPool? ScratchPool { get; init; }

    /// <summary><c>PLANE_TEST_EPSILON</c>, a double.</summary>
    public const double PlaneTestEpsilon = 0.01;

    /// <summary><c>TRANSFER_EPSILON</c>, a double.</summary>
    public const double TransferEpsilon = 0.0000001;

    /// <summary><c>MAX_PATCHES</c>: the cap on one patch's transfer count.</summary>
    public const int MaxPatches = 4 * 65536;

    private readonly BounceContext _context;
    private readonly ClusterTables _tables;
    private readonly bool _stockNormalise;

    /// <summary>Prepares a build.</summary>
    /// <param name="context">The lit world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public VisMatrix(BounceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _tables = ClusterTables.Build(context.Geometry, context.Patches, context.Visibility.ClusterCount);
        _stockNormalise = context.Settings.StockNormalise;
    }

    /// <summary>The cluster tables the enumeration walks.</summary>
    public ClusterTables Tables => _tables;

    /// <summary>
    /// True when <c>TestPatchToPatch</c>'s plane test takes the receiver's
    /// shading normal, as stock's does (<see cref="StockQuirk.VisPlaneTestPhongNormal"/>).
    /// </summary>
    public bool StockPlaneTest => _context.Settings.Compliance.Emulates(StockQuirk.VisPlaneTestPhongNormal);

    /// <summary>Counts from the last <see cref="BuildAsync"/>.</summary>
    public VisMatrixStatistics Statistics { get; } = new();

    /// <summary>
    /// The receiving patches in stock's order: clusters ascending, each
    /// Cluster's <c>clusterChildren</c> list.
    /// </summary>
    /// <returns>Patch indices.</returns>
    public int[] ReceiverOrder()
    {
        // Two walks of the child lists, one to count and one to fill, so the
        // result is allocated once at its exact size: on a real map it is
        // past the large-object threshold, and growing a list to it would
        // leave a trail of discarded doublings on that heap.
        PatchSet patches = _context.Patches;
        int count = 0;
        for (int c = 0; c < patches.ClusterChildren.Length; c++)
        {
            for (int p = patches.ClusterChildren[c]; p != Patch.Invalid; p = patches.At(p).NextClusterChild)
            {
                count++;
            }
        }

        int[] order = new int[count];
        int at = 0;
        for (int c = 0; c < patches.ClusterChildren.Length; c++)
        {
            for (int p = patches.ClusterChildren[c]; p != Patch.Invalid; p = patches.At(p).NextClusterChild)
            {
                order[at++] = p;
            }
        }

        return order;
    }

    /// <summary>
    /// <c>BuildVisMatrix</c> plus <c>MakeScales</c> for every patch
    /// (<c>MakeAllScales</c>).
    /// </summary>
    /// <param name="tracer">The tracer.</param>
    /// <param name="queue">The workers.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>Every patch's transfers.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public async Task<TransferSet> BuildAsync(IRayTracer tracer, WorkQueue queue, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(queue);
        cancellationToken.ThrowIfCancellationRequested();

        int patchCount = _context.Patches.Count;
        int[] receivers = ReceiverOrder();

        // Everything below sized by the map and dropped when the build ends
        // is rented, and goes back when this scope exits, however it exits.
        using CompileScratchPool? own = ScratchPool is null ? new() : null;
        using ScratchArrays scratch = new(ScratchPool ?? own!);
        int[] clusterOf = scratch.Rent<int>(receivers.Length);
        for (int k = 0; k < receivers.Length; k++)
        {
            clusterOf[k] = _context.Patches.At(receivers[k]).ClusterNumber;
        }

        Statistics.Receivers = receivers.Length;
        WorkQueueOptions stage = new() { Stage = "BuildVisLeafs" };

        // One enumerator per worker slot for the whole build: the count pass
        // and every chunk's fill pass hand a slot the enumerator it had last
        // time, instead of each pass building a fresh pair of face-sized
        // stamp arrays per worker. A slot runs on one thread at a time and
        // the passes run one after another, so no enumerator is ever used by
        // two threads at once.
        Enumerator?[] enumerators = new Enumerator?[queue.Degree];
        Func<int, Enumerator> enumerator = slot => enumerators[slot] ??= new Enumerator(this);

        // Count.
        int[] counts = await queue.RunAsync<Enumerator, int>(
            receivers.Length,
            (k, e, _) => e.Run(receivers[k], clusterOf[k], []),
            enumerator,
            stage,
            cancellationToken).ConfigureAwait(false);

        // Chunks of consecutive receivers, bounded in rays.
        List<(int Start, int End, int Rays)> chunks = [];
        int largest = 0;
        for (int start = 0; start < receivers.Length;)
        {
            int end = start;
            long rays = 0;
            while (end < receivers.Length && (end == start || rays + counts[end] <= ChunkRays))
            {
                rays += counts[end];
                end++;
            }

            chunks.Add((start, end, checked((int)rays)));
            largest = Math.Max(largest, (int)rays);
            start = end;
        }

        Statistics.Chunks = chunks.Count;
        Statistics.LargestChunk = largest;

        // Sized to the largest chunk and reused by every chunk. The transfers
        // are staged over the ray buffer (see StagingOver), not in an array
        // of their own.
        Ray[] rayBuffer = scratch.Rent<Ray>(largest);
        int[] receiverBuffer = scratch.Rent<int>(largest);
        ulong[] bits = scratch.Rent<ulong>((largest + 63) >> 6);
        int[] localBase = scratch.Rent<int>(receivers.Length + 1);

        int[] transferCount = new int[patchCount];
        int[] segmentOf = new int[patchCount];
        int[] offsets = new int[patchCount];
        Transfer[][] segments = new Transfer[chunks.Count][];
        int[] segmentBase = new int[receivers.Length + 1];
        int max = 0;

        for (int c = 0; c < chunks.Count; c++)
        {
            (int start, int end, int chunkRays) = chunks[c];
            int n = end - start;
            localBase[0] = 0;
            for (int i = 0; i < n; i++)
            {
                localBase[i + 1] = localBase[i] + counts[start + i];
            }

            // 3a. Fill: each receiver writes its candidates and rays at its offset.
            await queue.RunAsync<Enumerator, int>(
                n,
                (i, e, _) =>
                {
                    int k = start + i;
                    Span<int> mine = receiverBuffer.AsSpan(localBase[i], counts[k]);
                    int written = e.Run(receivers[k], clusterOf[k], mine);
                    if (written != mine.Length)
                    {
                        throw new InvalidOperationException(
                            $"patch {receivers[k]} enumerated {written} candidates after counting {mine.Length}");
                    }

                    MakeRays(receivers[k], mine, rayBuffer.AsSpan(localBase[i], mine.Length));
                    return 0;
                },
                enumerator,
                stage,
                cancellationToken).ConfigureAwait(false);

            // 3b. Trace. The chunk's words are cleared first: the tracer
            // contract leaves the padding bits past the last ray of a slab
            // unspecified, and a pooled array (or the previous chunk) may have
            // left anything there, which the blocked count below would see.
            int words = (chunkRays + 63) >> 6;
            bits.AsSpan(0, words).Clear();
            await TraceAsync(queue, tracer, rayBuffer.AsMemory(0, chunkRays), bits, TraceSlabRays, cancellationToken)
                .ConfigureAwait(false);
            Statistics.Rays += chunkRays;
            for (int w = 0; w < words; w++)
            {
                Statistics.Blocked += System.Numerics.BitOperations.PopCount(bits[w]);
            }

            // 3c. Transfers, staged in each receiver's own range of the traced,
            // and so dead, ray buffer.
            int[] made = await queue.RunAsync<int, int>(
                n,
                (i, _, _) => MakeTransfers(
                    receivers[start + i],
                    receiverBuffer.AsSpan(localBase[i], counts[start + i]),
                    bits,
                    localBase[i],
                    StagingOver(rayBuffer).Slice(localBase[i], counts[start + i])),
                _ => 0,
                stage,
                cancellationToken).ConfigureAwait(false);

            // Compact the chunk: every receiver's run moves from its staging
            // range to its place in the chunk's segment, which is where the
            // set keeps it. The placement is a prefix sum; the moves are
            // independent, so they run on the workers.
            segmentBase[0] = 0;
            for (int i = 0; i < n; i++)
            {
                segmentBase[i + 1] = segmentBase[i] + made[i];
                max = Math.Max(max, made[i]);
            }

            // Uninitialised: the moves below write every element, so zeroing
            // it first would only touch the memory twice.
            Transfer[] segment = GC.AllocateUninitializedArray<Transfer>(segmentBase[n]);
            int chunk = c;
            await queue.RunAsync(
                n,
                (i, _) =>
                {
                    int patch = receivers[start + i];
                    StagingOver(rayBuffer).Slice(localBase[i], made[i]).CopyTo(segment.AsSpan(segmentBase[i]));
                    transferCount[patch] = made[i];
                    segmentOf[patch] = chunk;
                    offsets[patch] = segmentBase[i];
                },
                stage,
                cancellationToken).ConfigureAwait(false);

            segments[c] = segment;
        }

        int built = 0;
        foreach (Enumerator? e in enumerators)
        {
            built += e is null ? 0 : 1;
        }

        Statistics.Enumerators = built;
        return new TransferSet(segments, segmentOf, offsets, transferCount, max);
    }

    /// <summary>
    /// The chunk's transfer staging, laid over its ray buffer.
    /// </summary>
    /// <param name="rays">The chunk's ray buffer.</param>
    /// <returns>The same memory, as transfers: 3.5 of them per ray.</returns>
    /// <remarks>
    /// A chunk's rays are dead once it is traced, which is before its first
    /// transfer is made, and a transfer (8 bytes) is smaller than a ray (28).
    /// Receiver <c>i</c> stages its transfers at the same element range
    /// <c>[base, base + count)</c> it had in the ray buffer, so every
    /// receiver's staging stays disjoint from every other's and inside the
    /// buffer. That replaces a separate staging array as long as the largest
    /// chunk: 16 MB more of large-object heap per build.
    /// </remarks>
    internal static Span<Transfer> StagingOver(Ray[] rays) =>
        MemoryMarshal.Cast<Ray, Transfer>(rays.AsSpan());

    /// <summary>
    /// The patches one receiver tests, in stock's order: what
    /// <c>BuildVisRow</c> would push into the ray stream for it.
    /// </summary>
    /// <param name="receiver">A leaf patch in a cluster.</param>
    /// <returns>Candidate source patches.</returns>
    public int[] Candidates(int receiver)
    {
        Enumerator e = new(this);
        int cluster = _context.Patches.At(receiver).ClusterNumber;
        int[] result = new int[e.Run(receiver, cluster, [])];
        e.Run(receiver, cluster, result);
        return result;
    }

    /// <summary>
    /// The candidate rays of one receiver (<c>TestPatchToPatch</c>,
    ///): from each patch's origin pushed one unit
    /// along its normal, "so that don't intersect their owners".
    /// </summary>
    /// <param name="receiver">The receiving patch.</param>
    /// <param name="sources">Its candidates.</param>
    /// <param name="rays">Receives one ray per candidate.</param>
    public void MakeRays(int receiver, ReadOnlySpan<int> sources, Span<Ray> rays)
    {
        bool estimate = _context.Settings.Compliance.Emulates(StockQuirk.TransferRayReciprocalEstimate);
        PatchSet patches = _context.Patches;
        ref Patch patch = ref patches.At(receiver);
        Vec3 p1 = patch.Origin + patch.Normal;
        for (int i = 0; i < sources.Length; i++)
        {
            ref Patch patch2 = ref patches.At(sources[i]);
            rays[i] = TransferRay(p1, patch2.Origin + patch2.Normal, estimate);
        }
    }

    /// <summary>
    /// The ray stock's transfer stream traces from <paramref name="start"/> to
    /// <paramref name="end"/>: <c>AddToRayStream</c> and
    /// <c>FlushStreamEntry</c>.
    /// </summary>
    /// <param name="start">The receiver's pushed-out origin.</param>
    /// <param name="end">The source's pushed-out origin.</param>
    /// <param name="estimate">
    /// <see cref="StockQuirk.TransferRayReciprocalEstimate"/>: normalise with
    /// <c>rcpps</c> plus a Newton step rather than a divide.
    /// </param>
    /// <returns>
    /// A unit-ish direction and the segment's length as its reach, which is the
    /// parametrisation stock's tracer sees; the length is also stock's
    /// <c>ray_length</c>, against which a hit is tested.
    /// </returns>
    /// <remarks>
    /// The direction's scale is not cosmetic: the tracer's plane and edge
    /// tests round differently for a unit direction than for the whole
    /// segment, and on the corpus the difference moved up to 73 of 195,455
    /// transfers on <c>l1_func_detail</c> before this was matched.
    /// <c>ReciprocalSaturateSIMD</c> turns a zero length into
    /// <c>FLT_EPSILON</c> first.
    /// </remarks>
    public static Ray TransferRay(Vec3 start, Vec3 end, bool estimate)
    {
        Vec3 delta = end - start;
        float len = delta.Length();
        float safe = len == 0f ? 1.1920929E-07f : len;
        Vec3 direction = estimate
            ? delta * StockSimd.Reciprocal(safe, estimate: true)
            : new Vec3(delta.X / safe, delta.Y / safe, delta.Z / safe);
        return new Ray(start.X, start.Y, start.Z, direction.X, direction.Y, direction.Z, len);
    }

    /// <summary>
    /// <c>CTransferMaker::Finish</c> and
    /// <c>MakeScales</c> for one receiver.
    /// </summary>
    /// <param name="receiver">The receiving patch.</param>
    /// <param name="sources">Its candidates, in test order.</param>
    /// <param name="bits">The chunk's visibility bits.</param>
    /// <param name="firstBit">Where this receiver's rays start in them.</param>
    /// <param name="staging">
    /// Where the transfers are built: at least as long as
    /// <paramref name="sources"/>.
    /// </param>
    /// <returns>How many transfers were made; they are the prefix of <paramref name="staging"/>, scaled.</returns>
    public int MakeTransfers(
        int receiver, ReadOnlySpan<int> sources, ReadOnlySpan<ulong> bits, int firstBit, Span<Transfer> staging)
    {
        int made = 0;
        for (int i = 0; i < sources.Length; i++)
        {
            int bit = firstBit + i;
            if ((bits[bit >> 6] & (1UL << (bit & 63))) != 0)
            {
                continue;
            }

            // The overflow check, ahead of the rest.
            if (made >= MaxPatches)
            {
                continue;
            }

            if (MakeTransfer(receiver, sources[i], out float trans))
            {
                staging[made++] = new Transfer(sources[i], trans);
            }
        }

        MakeScales(staging[..made]);
        return made;
    }

    /// <summary>
    /// <c>MakeTransfer</c>: the unscaled transfer from
    /// <paramref name="source"/> to <paramref name="receiver"/>, or none.
    /// </summary>
    /// <param name="receiver"><c>ndxPatch1</c>, whose list it goes on.</param>
    /// <param name="source"><c>ndxPatch2</c>, whose light it carries.</param>
    /// <param name="trans">The form factor times the source's area.</param>
    /// <returns>False when stock makes no transfer.</returns>
    public bool MakeTransfer(int receiver, int source, out float trans)
    {
        trans = 0f;
        PatchSet patches = _context.Patches;
        ref Patch patch1 = ref patches.At(receiver);
        ref Patch patch2 = ref patches.At(source);

        // Light is never taken from the sky.
        if (_context.Geometry.IsSky(patch2.FaceNumber))
        {
            return false;
        }

        // 1141. "hack for patch areas that area <= 0 (degenerate)".
        if (patch2.Area <= 0)
        {
            return false;
        }

        // FormFactorDiffToDiff( pPatch2, pPatch1 ).
        float scale = FormFactors.DiffToDiff(
            patch2.Origin, patch2.Normal, patch1.Origin, patch1.Normal, _stockNormalise);
        if (scale <= 0)
        {
            return false;
        }

        // 1158-1167, the five-times rule: `float flThreshold = (M_PI * 0.04)
        // * DotProduct(...)` is a double product narrowed into a float.
        Vec3 delta = patch1.Origin - patch2.Origin;
        float threshold = (float)((Math.PI * 0.04) * Vec3.Dot(delta, delta));
        if (threshold < patch2.Area)
        {
            scale = FormFactors.PolyToDiff(
                patches.Arena.Points(patch2.Winding),
                patch2.Area,
                patch1.Origin,
                patch1.Normal,
                _stockNormalise,
                _context.Settings.Compliance);
            if (scale <= 0.0)
            {
                return false;
            }
        }

        trans = patch2.Area * scale;

        // A double compare.
        return trans > TransferEpsilon;
    }

    /// <summary>
    /// <c>MakeScales</c>: normalise one receiver's
    /// transfers so that they sum to at most 1.
    /// </summary>
    /// <param name="transfers">The receiver's unscaled transfers, scaled in place.</param>
    /// <remarks>
    /// The sum is a float, in list order. "The total transfer should be PI,
    /// but we need to correct errors due to overlaping surfaces": a total
    /// above pi divides by itself, anything else by pi -- where
    /// <c>1.0f/M_PI</c> is a double quotient narrowed.
    /// </remarks>
    public static void MakeScales(Span<Transfer> transfers)
    {
        if (transfers.Length == 0)
        {
            return;
        }

        float total = 0;
        for (int j = 0; j < transfers.Length; j++)
        {
            total += transfers[j].Weight;
        }

        total = total > Math.PI ? 1.0f / total : (float)(1.0f / Math.PI);

        for (int j = 0; j < transfers.Length; j++)
        {
            transfers[j] = new Transfer(transfers[j].Patch, transfers[j].Weight * total);
        }
    }

    private static async Task TraceAsync(
        WorkQueue queue,
        IRayTracer tracer,
        ReadOnlyMemory<Ray> rays,
        ulong[] bits,
        int slabRays,
        CancellationToken cancellationToken)
    {
        int slabs = (rays.Length + slabRays - 1) / slabRays;
        Task?[] pending = new Task?[slabs];

        // One slab per work item: a CPU tracer runs on every worker; an
        // asynchronous one (a GPU) returns an incomplete task, kept and awaited
        // here, outside the workers.
        ExceptionDispatchInfo? failure = null;
        try
        {
            await queue.RunAsync<int, int>(
                slabs,
                (slab, _, _) =>
                {
                    int start = slab * slabRays;
                    int count = Math.Min(slabRays, rays.Length - start);
                    ValueTask task = tracer.TraceVisibilityAsync(
                        rays.Slice(start, count),
                        bits.AsMemory(start >> 6, (count + 63) >> 6),
                        RayTraceOptions.StockExact,
                        cancellationToken);
                    if (!task.IsCompletedSuccessfully)
                    {
                        pending[slab] = task.AsTask();
                    }

                    return 0;
                },
                _ => 0,
                new WorkQueueOptions { Stage = "BuildVisLeafs", ChunkSize = 1 },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ExceptionDispatchInfo.Capture(ex);
        }

        // Every slab still in flight is awaited, whatever the others ended
        // in: they read these rays and write these bits, and both go back to
        // the scratch pool once the build unwinds. A slab left running there
        // would write into whatever compile rented the bits next. The first
        // failure is the one reported.
        //
        // While this waits, no worker of the stage has anything to do: the
        // chunk's transfers need every bit. So the wait is parked worker time
        // for all of them, the span times the queue's degree
        // (RayTraceMeter's remarks).
        long waitStart = Stopwatch.GetTimestamp();
        bool waited = false;
        foreach (Task? task in pending)
        {
            if (task is null)
            {
                continue;
            }

            waited = true;
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure ??= ExceptionDispatchInfo.Capture(ex);
            }
        }

        if (waited)
        {
            RayTraceMeter.Of(tracer)?.AddParked(
                TraceWaitStage.Bounce, (Stopwatch.GetTimestamp() - waitStart) * queue.Degree);
        }

        failure?.Throw();
    }

    /// <summary>
    /// Stock's <c>BuildVisRow</c> walk over one PVS row, without the receiver:
    /// the faces it tests, in the order it tests them, before the receiver's
    /// own face is skipped.
    /// </summary>
    /// <param name="pvs">The row: bit <c>j</c> set when cluster <c>j</c> is visible.</param>
    /// <param name="clusters">How many clusters the row covers.</param>
    /// <param name="tables">Each cluster's leaves and displacement faces.</param>
    /// <param name="leaves">The map's leaves.</param>
    /// <param name="leafFaces">LUMP_LEAFFACES.</param>
    /// <param name="faceStamp">Per face, the stamp of the walk that last took it from a leaf.</param>
    /// <param name="dispStamp">Per face, the stamp of the walk that last took it as a displacement.</param>
    /// <param name="stamp">This walk's stamp, never used by an earlier walk over these arrays.</param>
    /// <param name="faces">Receives the faces; at least twice the face count long.</param>
    /// <returns>How many faces were written.</returns>
    /// <remarks>
    /// Each visible cluster in order: its leaves' faces, each face the first
    /// time the walk meets it, then its displacement faces, each the first
    /// time it meets them as a displacement. The two "seen" sets are
    /// separate, as stock's <c>face_tested</c> and <c>disp_tested</c> are, so
    /// a face can come twice. The stamps stand in for stock's per-call
    /// <c>memset</c> of both.
    /// </remarks>
    internal static int FacesInRow(
        ReadOnlySpan<byte> pvs,
        int clusters,
        ClusterTables tables,
        ReadOnlySpan<LeafInfo> leaves,
        ReadOnlySpan<ushort> leafFaces,
        int[] faceStamp,
        int[] dispStamp,
        int stamp,
        Span<int> faces)
    {
        int count = 0;
        for (int j = 0; j < clusters; j++)
        {
            if ((pvs[j >> 3] & (1 << (j & 7))) == 0)
            {
                continue;
            }

            foreach (int leafIndex in tables.Leaves(j))
            {
                LeafInfo leaf = leaves[leafIndex];
                for (int k = 0; k < leaf.NumLeafFaces; k++)
                {
                    int l = leafFaces[leaf.FirstLeafFace + k];
                    if (faceStamp[l] == stamp)
                    {
                        continue;
                    }

                    faceStamp[l] = stamp;
                    faces[count++] = l;
                }
            }

            foreach (int face in tables.DispFaces(j))
            {
                if (dispStamp[face] == stamp)
                {
                    continue;
                }

                dispStamp[face] = stamp;
                faces[count++] = face;
            }
        }

        return count;
    }

    /// <summary>
    /// <c>BuildVisRow</c>, <c>TestPatchToFace</c> and <c>TestPatchToPatch</c>
    /// For one worker: which patches one
    /// receiver tests, in stock's order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>face_tested</c>/<c>disp_tested</c> are stock's per-call
    /// <c>memset</c> byte arrays; here they are stamps, so nothing is cleared.
    /// </para>
    /// <para>
    /// THE FACE LIST IS THE CLUSTER'S, NOT THE RECEIVER'S. Stock walks the
    /// receiver's PVS row for every receiver: each visible cluster's leaves,
    /// each leaf's faces (skipping a face already seen), then the cluster's
    /// displacement faces (skipping one already seen among those). Nothing in
    /// that walk depends on the receiver except the row, which is its
    /// cluster's, and the one face it then skips, its own -- and that skip
    /// comes after the face is marked seen, so it changes nothing else. So the
    /// walk is made once per cluster, into <see cref="_faces"/>, and every
    /// receiver of the cluster runs down that list, skipping its own face:
    /// the same faces in the same order. Receivers come in cluster order
    /// (<see cref="ReceiverOrder"/>) and a worker takes consecutive ones, so
    /// the list is rebuilt about once per cluster per worker instead of the
    /// walk being made once per patch, twice over (the count pass and the
    /// fill pass). On 2fort the walk was most of this class's time.
    /// </para>
    /// </remarks>
    private sealed class Enumerator
    {
        private readonly VisMatrix _owner;
        private readonly int[] _faceStamp;
        private readonly int[] _dispStamp;
        private readonly byte[] _pvs;
        private readonly bool _phongPlaneTest;
        private int _stamp;
        private int _pvsCluster = -1;

        /// <summary>
        /// The faces a receiver in <see cref="_pvsCluster"/> tests, in walk
        /// order, before its own face is skipped. A face can be here twice,
        /// once from the leaves and once as a displacement, exactly as the
        /// walk tests it twice. At most every face from each source.
        /// </summary>
        private readonly int[] _faces;
        private int _faceCount;

        public Enumerator(VisMatrix owner)
        {
            _owner = owner;
            _phongPlaneTest = owner.StockPlaneTest;
            int faces = owner._context.Geometry.Faces.Length;
            _faceStamp = new int[faces];
            _dispStamp = new int[faces];
            _faces = new int[2 * faces];
            _pvs = new byte[Math.Max(owner._context.Visibility.RowBytes, 1)];
        }

        /// <summary>Enumerates, writing into <paramref name="output"/> when it is not empty.</summary>
        public int Run(int receiver, int cluster, Span<int> output)
        {
            if (cluster != _pvsCluster)
            {
                // BuildVisLeafs_Cluster decompresses the cluster's own row
                _owner._context.Visibility.GetVisCache(cluster, _pvs);
                _pvsCluster = cluster;
                CollectFaces();
            }

            Sink sink = new(output);
            int faceNumber = _owner._context.Patches.At(receiver).FaceNumber;
            ReadOnlySpan<int> faces = _faces.AsSpan(0, _faceCount);
            for (int i = 0; i < faces.Length; i++)
            {
                // "don't check patches on the same face".
                if (faces[i] != faceNumber)
                {
                    TestPatchToFace(receiver, faces[i], ref sink);
                }
            }

            return sink.Count;
        }

        private void CollectFaces()
        {
            BounceContext ctx = _owner._context;
            _faceCount = FacesInRow(
                _pvs, ctx.Visibility.ClusterCount, _owner._tables, ctx.Geometry.Leaves, ctx.Geometry.LeafFaces,
                _faceStamp, _dispStamp, ++_stamp, _faces);
        }

        private void TestPatchToFace(int receiver, int face, ref Sink sink)
        {
            PatchSet patches = _owner._context.Patches;
            int head = patches.FaceParents[face];
            if (head == Patch.Invalid)
            {
                return;
            }

            // "if emitter is behind that face plane, skip all
            // patches" -- the receiver's origin against the first ROOT patch's
            // normal and plane, a double compare.
            Vec3 origin = patches.At(receiver).Origin;
            ref Patch first = ref patches.At(head);
            if (!(Vec3.Dot(origin, first.Normal) > first.CachedPlaneDist + PlaneTestEpsilon))
            {
                return;
            }

            for (int p = head; p != Patch.Invalid; p = patches.At(p).NextParent)
            {
                TestPatchToPatch(receiver, p, ref sink);
            }
        }

        private void TestPatchToPatch(int receiver, int source, ref Sink sink)
        {
            PatchSet patches = _owner._context.Patches;
            ref Patch patch = ref patches.At(receiver);
            ref Patch patch2 = ref patches.At(source);

            if (patch2.Child1 != Patch.Invalid)
            {
                // Near enough that the patch subtends a
                // large angle: test its children instead. A double compare.
                Vec3 tmp = patch.Origin - patch2.Origin;
                if (Vec3.Dot(tmp, tmp) * 0.0625 < patch2.Area)
                {
                    int child1 = patch2.Child1;
                    int child2 = patch2.Child2;
                    TestPatchToPatch(receiver, child1, ref sink);
                    TestPatchToPatch(receiver, child2, ref sink);
                    return;
                }
            }

            // The source must be in front of the receiver's plane. Stock
            // takes the receiver's SHADING normal -- the phong normal on a
            // child of a smoothed face -- against its FLAT plane's distance
            // (StockQuirk.VisPlaneTestPhongNormal); correct takes the plane's
            // own normal. The two agree wherever the face is not smoothed.
            Vec3 normal = _phongPlaneTest ? patch.Normal : patch.PlaneNormal;
            if (Vec3.Dot(patch2.Origin, normal) > patch.CachedPlaneDist + PlaneTestEpsilon)
            {
                sink.Add(source);
            }
        }
    }

    private ref struct Sink
    {
        private readonly Span<int> _output;

        public Sink(Span<int> output) => _output = output;

        public int Count { get; private set; }

        public void Add(int source)
        {
            if (!_output.IsEmpty)
            {
                _output[Count] = source;
            }

            Count++;
        }
    }
}
