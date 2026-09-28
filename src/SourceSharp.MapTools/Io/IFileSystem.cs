//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;

namespace SourceSharp.MapTools.Io;

/// <summary>
/// What a file looks like from outside: its size and when it last changed.
/// </summary>
/// <param name="Length">The file's length in bytes.</param>
/// <param name="LastWriteTimeUtc">
/// When the file was last written, as the underlying store reports it.
/// </param>
/// <remarks>
/// The timestamp is here because a user interface may want to show it. Nothing
/// in the compiler may make a CORRECTNESS decision from it: the incremental
/// cache keys on content hashes, never on modification times, because this
/// project has twice had a mutation proof silently invalidated by a restore
/// that preserved an mtime and so convinced the build nothing had changed.
/// </remarks>
public readonly record struct FileInfoSnapshot(long Length, DateTimeOffset LastWriteTimeUtc);

/// <summary>
/// Every byte the compilers read or write passes through here.
/// </summary>
/// <remarks>
/// <para>
/// One seam, so that a compile can run entirely in memory, so that the
/// dependency recorder can capture opens and misses without any code path
/// being able to bypass it, and so that fault injection is a decorator rather
/// than a test harness that has to break a real disk.
/// </para>
/// <para>
/// <c>PhysicalFileSystem</c> is the ONLY implementation permitted to
/// touch <c>System.IO.File</c>, <c>FileStream</c>, <c>Directory</c>,
/// <c>FileInfo</c> or <c>DirectoryInfo</c>. That is enforced by an IL scan over
/// the built assemblies rather than by convention, because the rule is only
/// worth anything if it cannot be forgotten in one file.
/// </para>
/// </remarks>
public interface IFileSystem
{
    /// <summary>Opens a file for reading.</summary>
    /// <param name="path">The file to read.</param>
    /// <param name="cancellationToken">Cancels opening the file.</param>
    /// <returns>A readable stream the caller disposes.</returns>
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    ValueTask<Stream> OpenReadAsync(VPath path, CancellationToken cancellationToken = default);

    /// <summary>Reads a whole file.</summary>
    /// <param name="path">The file to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The file's bytes in memory the caller disposes. An
    /// <see cref="IMemoryOwner{T}"/> rather than a <c>byte[]</c> so an
    /// implementation may hand back pooled or memory-mapped storage, and a
    /// 200 MB lump need not become garbage.
    /// </returns>
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    ValueTask<IMemoryOwner<byte>> ReadAllAsync(VPath path, CancellationToken cancellationToken = default);

    /// <summary>Reads part of a file.</summary>
    /// <param name="path">The file to read.</param>
    /// <param name="offset">Where the range starts, in bytes from the start of the file.</param>
    /// <param name="length">How many bytes to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The bytes of the range in memory the caller disposes, and the whole
    /// file's length. A range that runs past the end of the file comes back
    /// SHORT -- the bytes that exist, not an error -- and one that starts at or
    /// past the end comes back empty, which is what a positioned read of a
    /// real file does. See <see cref="FileRange"/>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="offset"/> or <paramref name="length"/> is negative.
    /// </exception>
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    /// <remarks>
    /// <para>
    /// Exists because a map compile reads hundreds of textures only to learn
    /// what is in their first few hundred bytes -- a VTF's size, flags and
    /// reflectivity -- and <see cref="ReadAllAsync"/> made that tens of
    /// megabytes of reads, pooled buffers and memory maps per compile.
    /// </para>
    /// <para>
    /// No default implementation, on purpose. The only default the interface
    /// could give is "read everything, then copy the range", which is correct
    /// and would silently keep every cost the method exists to remove for an
    /// implementation that forgot to override it. A missing member is a build
    /// error instead, and each implementation says how it reads a range.
    /// </para>
    /// <para>
    /// A missing file fails exactly as it does for <see cref="ReadAllAsync"/>,
    /// so a caller that already handles one handles both.
    /// </para>
    /// </remarks>
    ValueTask<FileRange> ReadRangeAsync(
        VPath path,
        long offset,
        int length,
        CancellationToken cancellationToken = default);

    /// <summary>Opens a file for writing, replacing anything already there.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="cancellationToken">Cancels opening the file.</param>
    /// <returns>A writable stream the caller disposes.</returns>
    ValueTask<Stream> OpenWriteAsync(VPath path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a file so that it either appears complete or does not appear at all.
    /// </summary>
    /// <param name="path">The file to replace.</param>
    /// <param name="write">
    /// Fills the stream with the new contents. It is called exactly once, and
    /// writes to a temporary the implementation renames into place afterwards.
    /// </param>
    /// <param name="cancellationToken">Cancels before the rename.</param>
    /// <returns>A task that completes once the new contents are in place.</returns>
    /// <remarks>
    /// The reason this exists rather than being left to callers: a compile that
    /// is killed, cancelled, or runs out of disk part-way through
    /// <c>WriteBSPFile</c> must not leave a half-written <c>.bsp</c> that looks
    /// loadable. If <paramref name="write"/> throws or the token fires, the
    /// previous file is still there, untouched.
    /// </remarks>
    ValueTask ReplaceAsync(
        VPath path,
        Func<Stream, CancellationToken, ValueTask> write,
        CancellationToken cancellationToken = default);

    /// <summary>Whether a file exists.</summary>
    /// <param name="path">The file to look for.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>True when the file exists.</returns>
    ValueTask<bool> ExistsAsync(VPath path, CancellationToken cancellationToken = default);

    /// <summary>Reads a file's size and timestamp.</summary>
    /// <param name="path">The file to describe.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The file's details, or null when there is no such file.</returns>
    ValueTask<FileInfoSnapshot?> GetInfoAsync(VPath path, CancellationToken cancellationToken = default);

    /// <summary>Lists the files under a directory.</summary>
    /// <param name="directory">The directory to walk.</param>
    /// <param name="searchPattern">
    /// A glob matched against each file name, for example <c>*.vmt</c>.
    /// </param>
    /// <param name="recursive">Whether to descend into subdirectories.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>The matching files, in an order the implementation defines.</returns>
    IAsyncEnumerable<VPath> EnumerateAsync(
        VPath directory,
        string searchPattern = "*",
        bool recursive = false,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a file, succeeding when it is already absent.</summary>
    /// <param name="path">The file to delete.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>A task that completes once the file is gone.</returns>
    ValueTask DeleteAsync(VPath path, CancellationToken cancellationToken = default);
}
