//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.IO.Compression;

namespace SourceSharp.MapTools.Rad.Bounce;

/// <summary>
/// A <see cref="TransferSet"/> as bytes: one index (the patch count, the
/// max and every patch's count) and the transfers themselves cut into
/// chunks no larger than a store blob may be.
/// </summary>
/// <remarks>
/// Written through <see cref="TransferSet.For"/> only, so the set's own
/// memory layout is free to change. Every value is little-endian: the patch
/// as an int, the weight as its float bits.
/// </remarks>
public static class TransferSetCodec
{
    private const int TransferBytes = 8;
    private const uint Magic = 0x31465254; // "TRF1"

    /// <summary>The index blob.</summary>
    /// <param name="transfers">The set.</param>
    /// <returns>Magic, patch count, max, total, then each patch's count.</returns>
    public static byte[] Index(TransferSet transfers)
    {
        ArgumentNullException.ThrowIfNull(transfers);
        byte[] index = new byte[20 + (4 * transfers.PatchCount)];
        BinaryPrimitives.WriteUInt32LittleEndian(index, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(index.AsSpan(4), transfers.PatchCount);
        BinaryPrimitives.WriteInt32LittleEndian(index.AsSpan(8), transfers.Max);
        BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(12), transfers.Total);
        for (int p = 0; p < transfers.PatchCount; p++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(index.AsSpan(20 + (4 * p)), transfers.CountFor(p));
        }

        return index;
    }

    /// <summary>The transfers, patch after patch, cut into chunks.</summary>
    /// <param name="transfers">The set.</param>
    /// <param name="maxChunkBytes">The largest chunk (rounded down to whole transfers).</param>
    /// <returns>The chunks, in order.</returns>
    public static IEnumerable<byte[]> Chunks(TransferSet transfers, long maxChunkBytes)
    {
        ArgumentNullException.ThrowIfNull(transfers);
        int perChunk = (int)Math.Clamp(maxChunkBytes / TransferBytes, 1, int.MaxValue / TransferBytes);
        return ChunksIterator(transfers, perChunk);
    }

    private static IEnumerable<byte[]> ChunksIterator(TransferSet transfers, int perChunk)
    {
        long remaining = transfers.Total;
        byte[] chunk = new byte[(int)Math.Min(remaining, perChunk) * TransferBytes];
        int filled = 0;
        for (int p = 0; p < transfers.PatchCount; p++)
        {
            int i = 0;
            while (i < transfers.CountFor(p))
            {
                int take = Copy(transfers, p, i, chunk, filled);
                i += take;
                filled += take;
                remaining -= take;
                if (filled * TransferBytes == chunk.Length)
                {
                    yield return chunk;
                    filled = 0;
                    chunk = remaining > 0 ? new byte[(int)Math.Min(remaining, perChunk) * TransferBytes] : [];
                }
            }
        }
    }

    // Copies as much of one patch's list, from its entry i, as the chunk has room for.
    private static int Copy(TransferSet transfers, int patch, int i, byte[] chunk, int filled)
    {
        ReadOnlySpan<Transfer> list = transfers.For(patch);
        int take = Math.Min(list.Length - i, (chunk.Length / TransferBytes) - filled);
        Span<byte> into = chunk.AsSpan(filled * TransferBytes, take * TransferBytes);
        for (int k = 0; k < take; k++)
        {
            Transfer t = list[i + k];
            BinaryPrimitives.WriteInt32LittleEndian(into[(k * TransferBytes)..], t.Patch);
            BinaryPrimitives.WriteSingleLittleEndian(into[((k * TransferBytes) + 4)..], t.Weight);
        }

        return take;
    }

    /// <summary>
    /// Packs one chunk for storage: each transfer's patch as the difference
    /// from the one before, the four bytes of every patch delta and every
    /// weight grouped by significance, then deflated at the fastest level.
    /// About half the size of the raw chunk, in memory only.
    /// </summary>
    /// <param name="chunk">A raw chunk from <see cref="Chunks"/>.</param>
    /// <returns>The packed bytes.</returns>
    public static byte[] Pack(byte[] chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        int n = chunk.Length / TransferBytes;
        byte[] shuffled = new byte[chunk.Length];
        int previous = 0;
        for (int k = 0; k < n; k++)
        {
            int patch = BinaryPrimitives.ReadInt32LittleEndian(chunk.AsSpan(k * TransferBytes));
            uint delta = unchecked((uint)(patch - previous));
            uint weight = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan((k * TransferBytes) + 4));
            previous = patch;
            for (int b = 0; b < 4; b++)
            {
                shuffled[(b * n) + k] = (byte)(delta >> (8 * b));
                shuffled[((4 + b) * n) + k] = (byte)(weight >> (8 * b));
            }
        }

        using MemoryStream packed = new();
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, n);
        packed.Write(header);
        using (ZLibStream deflate = new(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(shuffled);
        }

        return packed.ToArray();
    }

    /// <summary>Undoes <see cref="Pack"/>; null when the bytes are not a packed chunk.</summary>
    /// <param name="packed">The packed bytes.</param>
    /// <returns>The raw chunk, or null.</returns>
    public static byte[]? Unpack(byte[] packed)
    {
        ArgumentNullException.ThrowIfNull(packed);
        if (packed.Length < 4)
        {
            return null;
        }

        int n = BinaryPrimitives.ReadInt32LittleEndian(packed);
        if (n < 0 || n > Array.MaxLength / TransferBytes)
        {
            return null;
        }

        byte[] shuffled = new byte[(long)n * TransferBytes];
        try
        {
            using ZLibStream inflate = new(new MemoryStream(packed, 4, packed.Length - 4), CompressionMode.Decompress);
            inflate.ReadExactly(shuffled);
            if (inflate.ReadByte() != -1)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            return null;
        }

        byte[] chunk = new byte[shuffled.Length];
        int previous = 0;
        for (int k = 0; k < n; k++)
        {
            uint delta = 0;
            uint weight = 0;
            for (int b = 0; b < 4; b++)
            {
                delta |= (uint)shuffled[(b * n) + k] << (8 * b);
                weight |= (uint)shuffled[((4 + b) * n) + k] << (8 * b);
            }

            int patch = unchecked(previous + (int)delta);
            previous = patch;
            BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(k * TransferBytes), patch);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan((k * TransferBytes) + 4), weight);
        }

        return chunk;
    }

    /// <summary>
    /// Rebuilds a set from its index and its packed chunks, unpacking each
    /// chunk straight into the set's arena and letting go of it once it is
    /// in; null when they do not fit together. The set is the one
    /// <see cref="Read"/> makes from the unpacked chunks.
    /// </summary>
    /// <param name="index">The index blob.</param>
    /// <param name="packed">
    /// The chunks as <see cref="Pack"/> left them, in order. Every entry is
    /// set to null as it is consumed, so the caller's array is empty
    /// afterwards, whatever the outcome.
    /// </param>
    /// <param name="patchCount">The patch count the caller expects.</param>
    /// <param name="parallel">How many chunks unpack at once, and the cancellation.</param>
    /// <returns>The set, or null.</returns>
    /// <remarks>
    /// <para>
    /// A cache hit on a large map replays hundreds of megabytes. Unpacking
    /// every chunk first and then copying them into the arena held three
    /// copies at the peak (packed, raw, arena); this holds the packed bytes
    /// and the arena, and the packed bytes shrink as the arena fills.
    /// </para>
    /// <para>
    /// Each packed chunk states its transfer count in its first four bytes,
    /// so every chunk's place in the arena is known before any is unpacked
    /// and they unpack independently. The checks are <see cref="Read"/>'s:
    /// the index must fit, the counts must add up to its total, every chunk
    /// must inflate to exactly its count, and every patch must be in range.
    /// </para>
    /// </remarks>
    public static TransferSet? ReadPacked(byte[] index, byte[]?[] packed, int patchCount, ParallelOptions parallel)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(packed);
        ArgumentNullException.ThrowIfNull(parallel);
        try
        {
            if (!TryReadIndex(index, patchCount, out int max, out long total, out int[] counts, out int[] offsets))
            {
                return null;
            }

            long[] starts = new long[packed.Length];
            long at = 0;
            for (int k = 0; k < packed.Length; k++)
            {
                if (packed[k] is not { Length: >= 4 } chunk)
                {
                    return null;
                }

                int n = BinaryPrimitives.ReadInt32LittleEndian(chunk);
                if (n < 0 || n > Array.MaxLength / TransferBytes)
                {
                    return null;
                }

                starts[k] = at;
                at += n;
            }

            if (at != total)
            {
                return null;
            }

            Transfer[] arena = new Transfer[total];
            int failed = 0;
            System.Threading.Tasks.Parallel.For(0, packed.Length, parallel, k =>
            {
                byte[] chunk = packed[k]!;
                int n = BinaryPrimitives.ReadInt32LittleEndian(chunk);
                if (!UnpackInto(chunk, n, arena.AsSpan((int)starts[k], n), patchCount))
                {
                    Interlocked.Exchange(ref failed, 1);
                }

                packed[k] = null;
            });

            return failed != 0 ? null : new TransferSet([arena], new int[patchCount], offsets, counts, max);
        }
        finally
        {
            Array.Clear(packed);
        }
    }

    // Undoes Pack into the arena's slice for this chunk; false when the bytes
    // do not inflate to exactly n transfers or name a patch out of range.
    private static bool UnpackInto(byte[] packed, int n, Span<Transfer> into, int patchCount)
    {
        byte[] shuffled = new byte[(long)n * TransferBytes];
        try
        {
            using ZLibStream inflate = new(new MemoryStream(packed, 4, packed.Length - 4), CompressionMode.Decompress);
            inflate.ReadExactly(shuffled);
            if (inflate.ReadByte() != -1)
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            return false;
        }

        int previous = 0;
        for (int k = 0; k < n; k++)
        {
            uint delta = 0;
            uint weight = 0;
            for (int b = 0; b < 4; b++)
            {
                delta |= (uint)shuffled[(b * n) + k] << (8 * b);
                weight |= (uint)shuffled[((4 + b) * n) + k] << (8 * b);
            }

            int patch = unchecked(previous + (int)delta);
            previous = patch;
            if ((uint)patch >= (uint)patchCount)
            {
                return false;
            }

            into[k] = new Transfer(patch, BitConverter.UInt32BitsToSingle(weight));
        }

        return true;
    }

    // The index blob's checks and contents, shared by both readers.
    private static bool TryReadIndex(
        byte[] index, int patchCount, out int max, out long total, out int[] counts, out int[] offsets)
    {
        max = 0;
        total = 0;
        counts = [];
        offsets = [];
        if (index.Length < 20
            || BinaryPrimitives.ReadUInt32LittleEndian(index) != Magic
            || BinaryPrimitives.ReadInt32LittleEndian(index.AsSpan(4)) != patchCount
            || index.Length != 20 + (4L * patchCount))
        {
            return false;
        }

        max = BinaryPrimitives.ReadInt32LittleEndian(index.AsSpan(8));
        total = BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(12));
        if (total < 0 || total > Array.MaxLength)
        {
            return false;
        }

        counts = new int[patchCount];
        offsets = new int[patchCount];
        long at = 0;
        int longest = 0;
        for (int p = 0; p < patchCount; p++)
        {
            int count = BinaryPrimitives.ReadInt32LittleEndian(index.AsSpan(20 + (4 * p)));
            if (count < 0)
            {
                return false;
            }

            counts[p] = count;
            offsets[p] = (int)Math.Min(at, int.MaxValue);
            at += count;
            longest = Math.Max(longest, count);
        }

        return at == total && longest == max;
    }

    /// <summary>Rebuilds a set from its index and chunks; null when they do not fit together.</summary>
    /// <param name="index">The index blob.</param>
    /// <param name="chunks">The chunks, in order.</param>
    /// <param name="patchCount">The patch count the caller expects.</param>
    /// <returns>The set, or null.</returns>
    public static TransferSet? Read(byte[] index, IReadOnlyList<byte[]> chunks, int patchCount)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(chunks);
        if (index.Length < 20
            || BinaryPrimitives.ReadUInt32LittleEndian(index) != Magic
            || BinaryPrimitives.ReadInt32LittleEndian(index.AsSpan(4)) != patchCount
            || index.Length != 20 + (4L * patchCount))
        {
            return null;
        }

        int max = BinaryPrimitives.ReadInt32LittleEndian(index.AsSpan(8));
        long total = BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(12));
        long bytes = 0;
        foreach (byte[] chunk in chunks)
        {
            if (chunk.Length % TransferBytes != 0)
            {
                return null;
            }

            bytes += chunk.Length;
        }

        if (total < 0 || bytes != total * TransferBytes || total > Array.MaxLength)
        {
            return null;
        }

        int[] counts = new int[patchCount];
        int[] offsets = new int[patchCount];
        long at = 0;
        int longest = 0;
        for (int p = 0; p < patchCount; p++)
        {
            int count = BinaryPrimitives.ReadInt32LittleEndian(index.AsSpan(20 + (4 * p)));
            if (count < 0)
            {
                return null;
            }

            counts[p] = count;
            offsets[p] = (int)Math.Min(at, int.MaxValue);
            at += count;
            longest = Math.Max(longest, count);
        }

        if (at != total || longest != max)
        {
            return null;
        }

        Transfer[] arena = new Transfer[total];
        long next = 0;
        foreach (byte[] chunk in chunks)
        {
            for (int k = 0; k < chunk.Length; k += TransferBytes)
            {
                arena[next++] = new Transfer(
                    BinaryPrimitives.ReadInt32LittleEndian(chunk.AsSpan(k)),
                    BinaryPrimitives.ReadSingleLittleEndian(chunk.AsSpan(k + 4)));
            }
        }

        foreach (Transfer t in arena)
        {
            if ((uint)t.Patch >= (uint)patchCount)
            {
                return null;
            }
        }

        // One segment: a stored patch's list may straddle chunks, and total
        // is at most Array.MaxLength, so every offset fits an int.
        return new TransferSet([arena], new int[patchCount], offsets, counts, max);
    }
}
