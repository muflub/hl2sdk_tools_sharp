//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Text;

using SourceSharp.MapTools.Diagnostics;

namespace SourceSharp.MapTools.Bsp.Collision;

/// <summary>
/// One model's record in <c>LUMP_PHYSCOLLIDE</c>: a <c>dphysmodel_t</c>
/// Its solids and its keydata.
/// </summary>
/// <param name="ModelIndex">The brush model.</param>
/// <param name="Solids">Each solid's cooked blob, without its size prefix.</param>
/// <param name="KeyData">The keydata text, including its terminating NUL.</param>
public sealed record PhysCollideModel(int ModelIndex, IReadOnlyList<byte[]> Solids, byte[] KeyData)
{
    /// <summary><c>dataSize</c>: a four-byte size per solid plus the blobs.</summary>
    public int DataSize => (4 * Solids.Count) + Solids.Sum(s => s.Length);

    /// <summary>The keydata as text, without the NUL.</summary>
    public string KeyText => Encoding.Latin1.GetString(KeyData.AsSpan(0, Math.Max(0, KeyData.Length - (KeyData.Length > 0 && KeyData[^1] == 0 ? 1 : 0))));
}

/// <summary>
/// The framing of <c>LUMP_PHYSCOLLIDE</c>, written and read exactly: the tail
/// of <c>EmitPhysCollision</c> and the walk the
/// Engine does at load.
/// </summary>
/// <remarks>
/// <para>
/// The layout per model is <c>{modelIndex, dataSize, keydataSize, solidCount}</c>,
/// then per solid an <c>int</c> size and the blob, then the keydata; after the
/// last model a terminator <c>{-1, -1, 0, 0}</c>. Stock's write path emits the
/// blobs UNPADDED -- the lump length is exactly the sum, and
/// <c>(size + 3) &amp; ~3</c> only sizes the allocation.
/// </para>
/// <para>
/// The four-byte alignment the plan names (
/// <c>SwapPhyscollideLump</c>) is a different road: stock applies it only
/// when byte-swapping a BSP for another platform, padding every solid and
/// each record's keydata to four bytes. <see cref="AlignForSwap"/> ports it,
/// so the two layouts are both available and neither is mistaken for the
/// other.
/// </para>
/// </remarks>
public static class PhysCollideLump
{
    /// <summary><c>sizeof(dphysmodel_t)</c>.</summary>
    public const int HeaderSize = 16;

    /// <summary>Writes the lump.</summary>
    /// <param name="models">The records, in model order.</param>
    /// <returns>The lump bytes.</returns>
    public static byte[] Write(IReadOnlyList<PhysCollideModel> models)
    {
        ArgumentNullException.ThrowIfNull(models);

        int total = HeaderSize;
        foreach (PhysCollideModel model in models)
        {
            total += HeaderSize + model.DataSize + model.KeyData.Length;
        }

        byte[] lump = new byte[total];
        int at = 0;
        foreach (PhysCollideModel model in models)
        {
            WriteHeader(lump, ref at, model.ModelIndex, model.DataSize, model.KeyData.Length, model.Solids.Count);
            foreach (byte[] solid in model.Solids)
            {
                BinaryPrimitives.WriteInt32LittleEndian(lump.AsSpan(at), solid.Length);
                at += 4;
                solid.CopyTo(lump, at);
                at += solid.Length;
            }

            model.KeyData.CopyTo(lump, at);
            at += model.KeyData.Length;
        }

        WriteHeader(lump, ref at, -1, -1, 0, 0);
        return lump;
    }

    /// <summary>Reads the lump back into records.</summary>
    /// <param name="lump">The lump bytes.</param>
    /// <returns>The records, without the terminator.</returns>
    /// <exception cref="MapCompileException">The framing does not hold.</exception>
    public static IReadOnlyList<PhysCollideModel> Read(ReadOnlySpan<byte> lump)
    {
        List<PhysCollideModel> models = [];
        int at = 0;
        while (true)
        {
            if (at + HeaderSize > lump.Length)
            {
                throw new MapCompileException($"PHYSCOLLIDE runs out at byte {at} with no terminator.");
            }

            int modelIndex = BinaryPrimitives.ReadInt32LittleEndian(lump[at..]);
            int dataSize = BinaryPrimitives.ReadInt32LittleEndian(lump[(at + 4)..]);
            int keySize = BinaryPrimitives.ReadInt32LittleEndian(lump[(at + 8)..]);
            int solidCount = BinaryPrimitives.ReadInt32LittleEndian(lump[(at + 12)..]);
            at += HeaderSize;

            if (dataSize < 0)
            {
                return models;
            }

            if (at + dataSize + keySize > lump.Length)
            {
                throw new MapCompileException($"PHYSCOLLIDE model {modelIndex} overruns the lump.");
            }

            List<byte[]> solids = [];
            int solidAt = at;
            for (int i = 0; i < solidCount; i++)
            {
                int size = BinaryPrimitives.ReadInt32LittleEndian(lump[solidAt..]);
                solidAt += 4;
                solids.Add(lump.Slice(solidAt, size).ToArray());
                solidAt += size;
            }

            if (solidAt != at + dataSize)
            {
                throw new MapCompileException(
                    $"PHYSCOLLIDE model {modelIndex}: {solidCount} solids frame {solidAt - at} bytes, dataSize says {dataSize}.");
            }

            models.Add(new PhysCollideModel(modelIndex, solids, lump.Slice(at + dataSize, keySize).ToArray()));
            at += dataSize + keySize;
        }
    }

    /// <summary>
    /// The aligned form <c>SwapPhyscollideLump</c> produces before swapping
    /// Every solid padded to four bytes (its
    /// size prefix rewritten to the padded size, <c>dataSize</c> grown), and
    /// each record's keydata zero-padded so <c>dataSize + keydataSize</c> is a
    /// multiple of four.
    /// </summary>
    /// <param name="lump">An unpadded lump.</param>
    /// <returns>The aligned lump.</returns>
    public static byte[] AlignForSwap(ReadOnlySpan<byte> lump)
    {
        IReadOnlyList<PhysCollideModel> models = Read(lump);
        List<byte> output = [];
        Span<byte> header = stackalloc byte[HeaderSize];

        foreach (PhysCollideModel model in models)
        {
            int dataSize = model.DataSize;
            List<byte[]> padded = [];
            foreach (byte[] solid in model.Solids)
            {
                int pad = solid.Length % 4 != 0 ? 4 - (solid.Length % 4) : 0;
                dataSize += pad;
                padded.Add([.. solid, .. new byte[pad]]);
            }

            int keySize = model.KeyData.Length;
            int keyPad = (dataSize + keySize) % 4 != 0 ? 4 - ((dataSize + keySize) % 4) : 0;

            // dataSize > 0 guards the whole block in stock: a record with no
            // solids is copied as is.
            if (model.DataSize <= 0)
            {
                dataSize = model.DataSize;
                padded = [.. model.Solids];
                keyPad = 0;
            }

            WriteHeader(header, model.ModelIndex, dataSize, keySize + keyPad, model.Solids.Count);
            output.AddRange(header.ToArray());
            foreach (byte[] solid in padded)
            {
                byte[] size = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(size, solid.Length);
                output.AddRange(size);
                output.AddRange(solid);
            }

            output.AddRange(model.KeyData);
            output.AddRange(new byte[keyPad]);
        }

        WriteHeader(header, -1, -1, 0, 0);
        output.AddRange(header.ToArray());
        return [.. output];
    }

    private static void WriteHeader(Span<byte> target, ref int at, int modelIndex, int dataSize, int keySize, int solidCount)
    {
        WriteHeader(target[at..], modelIndex, dataSize, keySize, solidCount);
        at += HeaderSize;
    }

    private static void WriteHeader(Span<byte> target, int modelIndex, int dataSize, int keySize, int solidCount)
    {
        BinaryPrimitives.WriteInt32LittleEndian(target, modelIndex);
        BinaryPrimitives.WriteInt32LittleEndian(target[4..], dataSize);
        BinaryPrimitives.WriteInt32LittleEndian(target[8..], keySize);
        BinaryPrimitives.WriteInt32LittleEndian(target[12..], solidCount);
    }
}

/// <summary>
/// <c>LUMP_PHYSDISP</c>: a <c>ushort</c> count,
/// a <c>ushort</c> size per displacement (<c>0xFFFF</c> for none -- stock's
/// <c>PutShort( -1 )</c>), then the virtual-mesh hull blobs in order.
/// </summary>
public static class PhysDispLump
{
    /// <summary>Writes the lump.</summary>
    /// <param name="blobs">One entry per displacement; null where it had no mesh.</param>
    /// <returns>The lump bytes.</returns>
    /// <exception cref="MapCompileException">More than 65535 displacements, or a blob that large.</exception>
    public static byte[] Write(IReadOnlyList<byte[]?> blobs)
    {
        ArgumentNullException.ThrowIfNull(blobs);
        if (blobs.Count > ushort.MaxValue)
        {
            throw new MapCompileException($"{blobs.Count} displacements do not fit PHYSDISP's ushort count.");
        }

        int total = 2 + (2 * blobs.Count) + blobs.Sum(b => b?.Length ?? 0);
        byte[] lump = new byte[total];
        BinaryPrimitives.WriteUInt16LittleEndian(lump, (ushort)blobs.Count);
        int at = 2;
        foreach (byte[]? blob in blobs)
        {
            if (blob is not null && blob.Length >= ushort.MaxValue)
            {
                // PutShort( testSize ) would truncate silently; 0xFFFF would read as "none".
                throw new MapCompileException($"a displacement's collision is {blob.Length} bytes, too large for PHYSDISP.");
            }

            BinaryPrimitives.WriteUInt16LittleEndian(lump.AsSpan(at), blob is null ? ushort.MaxValue : (ushort)blob.Length);
            at += 2;
        }

        foreach (byte[]? blob in blobs)
        {
            if (blob is not null)
            {
                blob.CopyTo(lump, at);
                at += blob.Length;
            }
        }

        return lump;
    }

    /// <summary>Reads the size table.</summary>
    /// <param name="lump">The lump bytes.</param>
    /// <returns>One size per displacement, -1 for none.</returns>
    public static IReadOnlyList<int> ReadSizes(ReadOnlySpan<byte> lump)
    {
        if (lump.Length < 2)
        {
            return [];
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(lump);
        int[] sizes = new int[count];
        for (int i = 0; i < count; i++)
        {
            ushort size = BinaryPrimitives.ReadUInt16LittleEndian(lump[(2 + (2 * i))..]);
            sizes[i] = size == ushort.MaxValue ? -1 : size;
        }

        return sizes;
    }
}
