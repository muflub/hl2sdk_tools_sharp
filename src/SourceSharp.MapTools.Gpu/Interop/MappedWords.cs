//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;

namespace SourceSharp.MapTools.Gpu.Interop;

/// <summary>
/// A slot's mapped ray buffer as <see cref="Memory{T}"/> of words, so the
/// batcher can hand pieces of one slab to several packing threads (a span
/// cannot cross into a lambda).
/// </summary>
/// <remarks>
/// <para>
/// It owns nothing. The mapping belongs to the slot's buffer, which lives
/// for as long as the device, and the batcher stops packing before the
/// device is released (<see cref="SlabBatcher.Close"/> waits out the
/// drainer under the device lock first), so no memory handed out from here
/// is written after the unmap. Made once per slot when the slot is
/// allocated, so a slab allocates nothing for it.
/// </para>
/// <para>
/// Pinning is a no-op: the memory is the driver's, outside the managed
/// heap, and never moves.
/// </para>
/// </remarks>
/// <param name="address">The mapped address.</param>
/// <param name="words">How many 32-bit words the mapping holds.</param>
internal sealed unsafe class MappedWords(nint address, int words) : MemoryManager<uint>
{
    /// <inheritdoc/>
    public override Span<uint> GetSpan() => new((void*)address, words);

    /// <inheritdoc/>
    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(elementIndex, words);
        return new MemoryHandle((uint*)address + elementIndex);
    }

    /// <inheritdoc/>
    public override void Unpin()
    {
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
    }
}
