//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;

using SourceSharp.MapTools.Phys.Managed;

namespace SourceSharp.MapTools.Compile.Cache;

/// <summary>
/// The brush numbers a cooked brush model carries: the emitter stores each
/// convex's absolute brush index as its game data (<c>SetConvexGameData</c>),
/// so a cooked blob replayed after the BSP was renumbered must have those
/// numbers moved with it to equal a fresh cook.
/// </summary>
/// <remarks>
/// The blob is the <c>VPHY</c> serialisation: a 28-byte header, then the IVP
/// compact surface, whose leaf ledges each hold their game data four bytes
/// in. Anything else (another collide format) is reported as unreadable and
/// the cache treats it as a miss.
/// </remarks>
public static class CollisionGameData
{
    private const int HeaderSize = 28;

    /// <summary>Reads every leaf convex's game data, in ledge order.</summary>
    /// <param name="blob">One cooked solid.</param>
    /// <param name="gameData">The values, or null when the blob is not a readable compact surface.</param>
    /// <returns>Whether the blob could be read.</returns>
    public static bool TryRead(byte[] blob, out List<uint>? gameData)
    {
        ArgumentNullException.ThrowIfNull(blob);
        gameData = null;
        if (!TryLedges(blob, out List<int>? ledges))
        {
            return false;
        }

        gameData = [.. ledges!.Select(at => BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(at + 4)))];
        return true;
    }

    /// <summary>
    /// A copy of <paramref name="blob"/> with every leaf convex's game data
    /// moved by <paramref name="delta"/>.
    /// </summary>
    /// <param name="blob">One cooked solid.</param>
    /// <param name="delta">What to add to each brush number.</param>
    /// <returns>The moved copy, or null when the blob is not a readable compact surface.</returns>
    public static byte[]? Rebase(byte[] blob, int delta)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (!TryLedges(blob, out List<int>? ledges))
        {
            return null;
        }

        byte[] moved = (byte[])blob.Clone();
        foreach (int at in ledges!)
        {
            Span<byte> slot = moved.AsSpan(at + 4, 4);
            BinaryPrimitives.WriteUInt32LittleEndian(slot, unchecked((uint)((int)BinaryPrimitives.ReadUInt32LittleEndian(slot) + delta)));
        }

        return moved;
    }

    // The leaf ledges' absolute offsets in the blob, or false when the blob
    // is not a VPHY compact surface the walk can trust.
    private static bool TryLedges(byte[] blob, out List<int>? ledges)
    {
        ledges = null;
        if (blob.Length < HeaderSize + 0x24
            || blob[0] != (byte)'V' || blob[1] != (byte)'P' || blob[2] != (byte)'H' || blob[3] != (byte)'Y'
            || BinaryPrimitives.ReadInt16LittleEndian(blob.AsSpan(6)) != 0)
        {
            return false;
        }

        int size = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(8));
        if (size <= 0 || size > blob.Length - HeaderSize)
        {
            return false;
        }

        List<int> offsets;
        try
        {
            offsets = IvpCollideQueries.LeafOffsets(blob.AsSpan(HeaderSize, size));
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return false;
        }

        foreach (int offset in offsets)
        {
            if (offset < 0 || offset + 8 > size)
            {
                return false;
            }
        }

        ledges = [.. offsets.Select(o => o + HeaderSize)];
        return true;
    }
}
