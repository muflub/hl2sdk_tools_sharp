//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;

namespace SourceSharp.MapTools.Io;

/// <summary>
/// An <see cref="IMemoryOwner{T}"/> over a pooled array, exposing exactly the
/// requested length rather than the pool's rounded-up block.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IFileSystem.ReadAllAsync"/> hands back an owner rather than a
/// <c>byte[]</c> so the caller cannot tell whether it got a pooled array, a
/// fresh allocation or a memory-mapped view. This is the pooled case, which is
/// what every implementation but <see cref="PhysicalFileSystem"/>'s large-file
/// path returns.
/// </para>
/// <para>
/// The exact-length slice matters more than it looks: a caller that trusted
/// <c>Memory.Length</c> and got the pool's block size would read whatever the
/// previous tenant left behind, which for a BSP lump reader is a silent wrong
/// answer rather than a crash.
/// </para>
/// </remarks>
internal sealed class PooledMemoryOwner : IMemoryOwner<byte>
{
    private readonly int _length;
    private byte[]? _array;

    private PooledMemoryOwner(byte[] array, int length)
    {
        _array = array;
        _length = length;
    }

    /// <summary>Rents uninitialised storage of exactly <paramref name="length"/> bytes.</summary>
    /// <param name="length">How many bytes the owner exposes.</param>
    /// <returns>The owner; the caller disposes it.</returns>
    public static PooledMemoryOwner Rent(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return new PooledMemoryOwner(ArrayPool<byte>.Shared.Rent(length), length);
    }

    /// <summary>Copies <paramref name="source"/> into pooled storage.</summary>
    /// <param name="source">The bytes to copy.</param>
    /// <returns>The owner; the caller disposes it.</returns>
    public static PooledMemoryOwner Copy(ReadOnlySpan<byte> source)
    {
        PooledMemoryOwner owner = Rent(source.Length);
        source.CopyTo(owner.Memory.Span);
        return owner;
    }

    /// <inheritdoc />
    public Memory<byte> Memory
    {
        get
        {
            byte[] array = _array ?? throw new ObjectDisposedException(nameof(PooledMemoryOwner));
            return array.AsMemory(0, _length);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        byte[]? array = Interlocked.Exchange(ref _array, null);
        if (array is not null)
        {
            ArrayPool<byte>.Shared.Return(array);
        }
    }
}
