//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;

namespace SourceSharp.MapTools.Io;

/// <summary>
/// Part of a file, read by <see cref="IFileSystem.ReadRangeAsync"/> or
/// <see cref="IContentFileSystem.ReadRangeAsync"/>, together with how long the
/// whole file is.
/// </summary>
/// <remarks>
/// <para>
/// The whole-file length travels with the bytes because a reader that asks for
/// a prefix still has to know where the file ENDS. A VTF header names offsets
/// into the rest of the file (where the image data starts, how far the
/// resource table reaches) and the parser rejects a file whose offsets point
/// past its end. Given only the prefix, those checks would either have to be
/// dropped or would reject every valid texture; given the real length, they
/// stay exactly the checks a whole-file parse makes. Taking the length from a
/// separate <see cref="IFileSystem.GetInfoAsync"/> would be a second lookup
/// and a window in which the file could change between the two answers, so
/// the implementation reports the length it read against.
/// </para>
/// <para>
/// <see cref="Memory"/> is exactly the bytes that exist in the requested
/// range: the requested length, or fewer when the range runs past the end of
/// the file (a short read), or none when it starts at or beyond the end. That
/// matches what a positioned read of a real file returns, and it is the
/// caller's business to compare <see cref="Memory"/>'s length with what it
/// needed -- which is why <see cref="FileLength"/> is there to compare with.
/// </para>
/// <para>
/// Pooled storage, returned by <see cref="Dispose"/>, like the owner
/// <see cref="IFileSystem.ReadAllAsync"/> hands back. The exact-length slice
/// matters for the same reason it does there: a caller that trusted the
/// pool's block size would read what the previous tenant left behind.
/// </para>
/// </remarks>
public sealed class FileRange : IMemoryOwner<byte>
{
    private readonly int _length;
    private byte[]? _array;

    private FileRange(byte[] array, int length, long offset, long fileLength)
    {
        _array = array;
        _length = length;
        Offset = offset;
        FileLength = fileLength;
    }

    /// <summary>Where in the file <see cref="Memory"/> starts.</summary>
    public long Offset { get; }

    /// <summary>The length of the WHOLE file the range was read from.</summary>
    public long FileLength { get; }

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException">The range has been disposed.</exception>
    public Memory<byte> Memory
    {
        get
        {
            byte[] array = _array ?? throw new ObjectDisposedException(nameof(FileRange));
            return array.AsMemory(0, _length);
        }
    }

    /// <summary>
    /// How many bytes a range read returns: <paramref name="length"/>, clipped
    /// to what the file holds from <paramref name="offset"/> on.
    /// </summary>
    /// <param name="offset">Where the range starts; not negative.</param>
    /// <param name="length">How many bytes were asked for; not negative.</param>
    /// <param name="fileLength">The whole file's length.</param>
    /// <returns>The count, zero when the range starts at or past the end.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="offset"/>, <paramref name="length"/> or
    /// <paramref name="fileLength"/> is negative.
    /// </exception>
    /// <remarks>
    /// Public so that every implementation of the two seams -- including a
    /// third-party host's -- clips the same way rather than each deciding what
    /// a range past the end means.
    /// </remarks>
    public static int Available(long offset, int length, long fileLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfNegative(fileLength);

        return offset >= fileLength ? 0 : (int)Math.Min(length, fileLength - offset);
    }

    /// <summary>
    /// Copies the requested range out of a file already in memory.
    /// </summary>
    /// <param name="file">The whole file.</param>
    /// <param name="offset">Where the range starts; not negative.</param>
    /// <param name="length">How many bytes were asked for; not negative.</param>
    /// <returns>The range, clipped to the file; the caller disposes it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="offset"/> or <paramref name="length"/> is negative.
    /// </exception>
    /// <remarks>
    /// What an implementation whose storage is already a buffer (the
    /// in-memory file system, a pak lump, a whole read a decorator had to
    /// make anyway) uses, so that the clipping rule is written once.
    /// </remarks>
    public static FileRange Copy(ReadOnlySpan<byte> file, long offset, int length)
    {
        int count = Available(offset, length, file.Length);
        FileRange range = Rent(count, offset, file.Length);
        if (count > 0)
        {
            // Only sliced when something is there: an offset past the end may
            // not even fit an int, and there is nothing to copy from it.
            file.Slice((int)offset, count).CopyTo(range.Memory.Span);
        }

        return range;
    }

    /// <summary>
    /// Rents uninitialised storage for a range the caller fills in.
    /// </summary>
    /// <param name="count">
    /// How many bytes <see cref="Memory"/> exposes: already clipped with
    /// <see cref="Available"/>.
    /// </param>
    /// <param name="offset">Where the range starts in the file.</param>
    /// <param name="fileLength">The whole file's length.</param>
    /// <returns>The range; the caller fills it, and disposes it on failure.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A value is negative, or <paramref name="count"/> does not fit between
    /// <paramref name="offset"/> and the end of the file.
    /// </exception>
    /// <remarks>
    /// For an implementation that reads straight into the buffer (a positioned
    /// read of a disk file, an archive part), so the bytes are not copied a
    /// second time. The bounds are checked here because a count that ran past
    /// <paramref name="fileLength"/> would be a range claiming bytes the file
    /// does not have.
    /// </remarks>
    public static FileRange Rent(int count, long offset, long fileLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(fileLength);
        if (count > 0 && offset + count > fileLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                $"{count} bytes at offset {offset} run past the end of a {fileLength}-byte file");
        }

        return new FileRange(ArrayPool<byte>.Shared.Rent(count), count, offset, fileLength);
    }

    /// <summary>Returns the storage to the pool. Safe to call more than once.</summary>
    public void Dispose()
    {
        byte[]? array = Interlocked.Exchange(ref _array, null);
        if (array is not null)
        {
            ArrayPool<byte>.Shared.Return(array);
        }
    }
}
