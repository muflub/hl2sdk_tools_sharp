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
