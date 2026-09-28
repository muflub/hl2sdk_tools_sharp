//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;

using Microsoft.Win32.SafeHandles;

namespace SourceSharp.MapTools.Io;

/// <summary>
/// The one implementation that touches a disk.
/// </summary>
/// <remarks>
/// <para>
/// This type is the ONLY place in the tree permitted to use
/// <c>System.IO.File</c>, <c>FileStream</c>, <c>Directory</c>,
/// <c>FileInfo</c>, <c>DirectoryInfo</c>, <c>Path.GetTempPath</c> or
/// <c>Environment.CurrentDirectory</c>, and an IL scan over the built
/// assemblies is what enforces that rather than this comment. Everything else —
/// compilers, readers, the content layer — goes through
/// <see cref="IFileSystem"/>, which is what lets a whole compile run in memory
/// and what makes the dependency recorder impossible to bypass.
/// </para>
/// <para>
/// A file system is ROOTED at a host directory and every <see cref="VPath"/> is
/// relative to it. <see cref="VPath"/> cannot walk above its own root, so a
/// content path that came out of a map file cannot address anything outside the
/// root: containment is a property of the path type, checked at construction,
/// not a string test here that someone forgets on one code path.
/// </para>
/// <para>
/// Large reads are served by a memory-mapped view rather than a copy. Callers
/// cannot tell — both cases are an <see cref="IMemoryOwner{T}"/> whose
/// <c>Memory</c> is exactly the file's length.
/// </para>
/// </remarks>
public sealed class PhysicalFileSystem : IFileSystem
{
    /// <summary>
    /// Reads at or above this many bytes are memory-mapped instead of copied.
    /// </summary>
    /// <remarks>
    /// One megabyte: below it the mapping's syscalls cost more than the copy,
    /// and above it the copy is the thing that shows up in a compile's
    /// allocation profile. Measured properly in the perf pass; a threshold is
    /// not something to be precious about before there are numbers.
    /// </remarks>
    public const long DefaultMemoryMapThreshold = 1L << 20;

    private const int CopyBufferSize = 128 * 1024;

    private readonly long _memoryMapThreshold;

    /// <summary>Creates a file system rooted at a host directory.</summary>
    /// <param name="rootDirectory">
    /// An absolute host path. It need not exist yet; writing creates it.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="rootDirectory"/> is not absolute.</exception>
    public PhysicalFileSystem(string rootDirectory)
        : this(rootDirectory, DefaultMemoryMapThreshold)
    {
    }

    /// <summary>Creates a file system rooted at a host directory.</summary>
    /// <param name="rootDirectory">An absolute host path.</param>
    /// <param name="memoryMapThreshold">
    /// Reads at or above this size are memory-mapped. <see cref="long.MaxValue"/>
    /// turns mapping off, which is what a fact that wants the copy path asks for.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="rootDirectory"/> is not absolute.</exception>
    public PhysicalFileSystem(string rootDirectory, long memoryMapThreshold)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        ArgumentOutOfRangeException.ThrowIfNegative(memoryMapThreshold);

        if (!Path.IsPathRooted(rootDirectory))
        {
            throw new ArgumentException(
                $"\"{rootDirectory}\" is not an absolute path",
                nameof(rootDirectory));
        }

        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        _memoryMapThreshold = memoryMapThreshold;
    }

    /// <summary>
    /// A file system rooted at the host's filesystem root, so a
    /// <see cref="VPath"/> is an absolute host path with its leading separator
    /// removed.
    /// </summary>
    /// <returns>The file system.</returns>
    /// <remarks>
    /// What mounting an installed game needs: content lives at
    /// <c>/home/…/steamapps/common/…</c>, nowhere near the map being compiled,
    /// and inventing a second "absolute path" concept for it would mean two
    /// path types again.
    /// </remarks>
    public static PhysicalFileSystem AtHostRoot() =>
        new(Path.GetPathRoot(Path.GetFullPath("/")) ?? "/");

    /// <summary>The absolute host directory every path is relative to.</summary>
    public string Root { get; }

    /// <summary>Maps a path in this file system to its host path.</summary>
    /// <param name="path">A path in this file system.</param>
    /// <returns>The absolute host path.</returns>
    public string ToHostPath(VPath path) =>
        path.IsEmpty ? Root : Path.Combine(Root, path.Value.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Maps an absolute host path into this file system.</summary>
    /// <param name="hostPath">
    /// An absolute host path at or below <see cref="Root"/>, or on another
    /// drive when <see cref="Root"/> is a drive root.
    /// </param>
    /// <returns>The path as this file system sees it.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="hostPath"/> is not below <see cref="Root"/>.
    /// </exception>
    public VPath ToVirtualPath(string hostPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostPath);

        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(hostPath));
        if (string.Equals(full, Root, StringComparison.Ordinal))
        {
            return VPath.Empty;
        }

        string prefix = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal))
        {
            if (TryMapAcrossDrives(Root, full, out VPath elsewhere))
            {
                return elsewhere;
            }

            throw new ArgumentException(
                $"\"{hostPath}\" is not below the root \"{Root}\"",
                nameof(hostPath));
        }

        return VPath.Create(full[prefix.Length..]);
    }

    /// <summary>
    /// Maps a host path on another Windows drive for a file system rooted at a
    /// drive root, keeping the drive in the path.
    /// </summary>
    /// <param name="root">The file system's <see cref="Root"/>.</param>
    /// <param name="full">A full host path that is not below it.</param>
    /// <param name="path">The path with its drive (<c>C:/Users/…</c>), when mapped.</param>
    /// <returns>True when <paramref name="root"/> is a bare drive root and <paramref name="full"/> is on another drive.</returns>
    /// <remarks>
    /// <para>
    /// A file system rooted at <c>/</c> stands for the whole host: that is
    /// what the CLI mounts games through. On Windows <c>/</c> is the current
    /// drive's root, so a game on another drive (a Steam library on <c>E:</c>
    /// while the command runs from <c>D:</c>) came back from a directory
    /// listing as "not below the root" and the mount failed. A
    /// <see cref="VPath"/> can carry a drive, and <see cref="ToHostPath"/>
    /// already maps one back unchanged (a rooted second operand wins in
    /// <see cref="Path.Combine(string, string)"/>), so the listing answers in
    /// that form and the round trip holds.
    /// </para>
    /// <para>
    /// Only a bare drive root answers for other drives. A file system rooted
    /// at a directory still refuses anything outside it: containment is the
    /// point of rooting one there. The test is on the strings alone, not on
    /// the host's path rules, so it is the same decision on every OS.
    /// </para>
    /// </remarks>
    internal static bool TryMapAcrossDrives(string root, string full, out VPath path)
    {
        path = VPath.Empty;
        bool rootIsDrive = root.Length is 2 or 3
            && char.IsAsciiLetter(root[0])
            && root[1] == ':'
            && (root.Length == 2 || root[2] is '\\' or '/');
        bool fullHasDrive = full.Length >= 3
            && char.IsAsciiLetter(full[0])
            && full[1] == ':'
            && full[2] is '\\' or '/';
        if (!rootIsDrive || !fullHasDrive || char.ToUpperInvariant(root[0]) == char.ToUpperInvariant(full[0]))
        {
            return false;
        }

        return VPath.TryCreate(full.Replace('\\', '/'), out path);
    }

    /// <inheritdoc />
    public ValueTask<Stream> OpenReadAsync(VPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string host = ToHostPath(path);
        Stream stream = new FileStream(
            host,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return ValueTask.FromResult(stream);
    }

    /// <summary>
    /// The file a host path names, with any symbolic link followed to its
    /// final target.
    /// </summary>
    /// <param name="host">A host path.</param>
    /// <returns>The target's info; one that does not exist for a dangling link.</returns>
    /// <remarks>
    /// <see cref="FileSystemInfo"/> describes the link itself, not what it
    /// points to: on Linux its <see cref="FileInfo.Length"/> is the length of
    /// the target PATH. Opening, reading and mapping the file all follow the
    /// link, so the size was the one thing taken from the wrong file, and
    /// <see cref="ReadAllAsync"/> read exactly that many bytes. A symlinked
    /// <c>gameinfo.txt</c> (a mod folder linking its gameinfo from a source
    /// tree) came back as its first 59 bytes, parsed as a gameinfo with no
    /// search paths, and a whole compile then ran with no game content. Cache
    /// stamps from <see cref="GetInfoAsync"/> described the link the same
    /// way, so an edit to the target did not change them.
    /// </remarks>
    private static FileInfo Resolved(string host)
    {
        var info = new FileInfo(host);
        return info.LinkTarget is null
            ? info
            : info.ResolveLinkTarget(returnFinalTarget: true) as FileInfo ?? info;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// A file at or over the memory-map threshold is mapped; a smaller one is
    /// read straight into a pooled buffer of its exact size, synchronously,
    /// with positioned reads on an unbuffered handle.
    /// </para>
    /// <para>
    /// Synchronous on purpose. Loading a map reads a few hundred small
    /// content files (every VMT and the texture each names), and the
    /// asynchronous stream this used did two things per file that cost more
    /// than the read: it allocated its own 128 KB copy buffer -- on the large
    /// object heap, per file -- and, file IO on Linux being blocking calls
    /// run on the thread pool, it queued every read to another thread and
    /// waited for it. On a busy host that queueing was most of the time a
    /// material lookup took. A read of a file this small is a copy out of the
    /// page cache or one short disk read, which is cheaper done where the
    /// caller already is. Large files never come here: they are mapped.
    /// </para>
    /// <para>
    /// The result is still delivered the way an async method delivers it --
    /// a failure faults the returned task and a cancelled token cancels it --
    /// so callers see no difference but the timing.
    /// </para>
    /// </remarks>
    public ValueTask<IMemoryOwner<byte>> ReadAllAsync(
        VPath path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return ValueTask.FromResult(ReadAll(path, cancellationToken));
        }
        catch (OperationCanceledException exception)
        {
            return ValueTask.FromCanceled<IMemoryOwner<byte>>(
                exception.CancellationToken.IsCancellationRequested
                    ? exception.CancellationToken
                    : cancellationToken.IsCancellationRequested ? cancellationToken : new CancellationToken(true));
        }
#pragma warning disable CA1031 // Every failure is handed to the caller through the task, as an async method would.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return ValueTask.FromException<IMemoryOwner<byte>>(exception);
        }
    }

    private IMemoryOwner<byte> ReadAll(VPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string host = ToHostPath(path);
        FileInfo info = Resolved(host);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"no such file: {path}", host);
        }

        long length = info.Length;
        if (length > int.MaxValue)
        {
            throw new IOException(
                $"{path} is {length} bytes, which does not fit one buffer");
        }

        if (length >= _memoryMapThreshold && length > 0)
        {
            // Mapping does no I/O here, so there is nothing to await and
            // nothing for the token to interrupt.
            return MappedMemoryOwner.Map(host, length);
        }

        PooledMemoryOwner owner = PooledMemoryOwner.Rent((int)length);
        try
        {
            using SafeFileHandle handle = File.OpenHandle(
                host, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);

            Span<byte> destination = owner.Memory.Span;
            int filled = 0;
            while (filled < destination.Length)
            {
                int read = RandomAccess.Read(handle, destination[filled..], filled);
                if (read == 0)
                {
                    // Shorter than it was a moment ago: the same failure the
                    // exact-length stream read reported.
                    throw new EndOfStreamException(
                        $"{path} ended at {filled} of the {destination.Length} bytes it had");
                }

                filled += read;
            }

            return owner;
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The length comes from <see cref="Resolved"/>, the same symlink-following
    /// lookup <see cref="ReadAllAsync"/> and <see cref="GetInfoAsync"/> use, so a
    /// linked file reports its target's length and a range read of it clips
    /// against the target, not against the length of the link's own path.
    /// Opening the handle follows the link too, so the bytes and the length
    /// always describe one file.
    /// </para>
    /// <para>
    /// Never memory-mapped, whatever the size: a range read is for a small
    /// piece of a file, and mapping a whole texture to read its header is the
    /// cost this method exists to avoid. The read is synchronous for the
    /// reasons <see cref="ReadAllAsync"/> gives, and failures and cancellation
    /// are still delivered through the returned task.
    /// </para>
    /// </remarks>
    public ValueTask<FileRange> ReadRangeAsync(
        VPath path,
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return ValueTask.FromResult(ReadRange(path, offset, length, cancellationToken));
        }
        catch (OperationCanceledException exception)
        {
            return ValueTask.FromCanceled<FileRange>(
                exception.CancellationToken.IsCancellationRequested
                    ? exception.CancellationToken
                    : cancellationToken.IsCancellationRequested ? cancellationToken : new CancellationToken(true));
        }
#pragma warning disable CA1031 // Every failure is handed to the caller through the task, as an async method would.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return ValueTask.FromException<FileRange>(exception);
        }
    }

    private FileRange ReadRange(VPath path, long offset, int length, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        cancellationToken.ThrowIfCancellationRequested();

        string host = ToHostPath(path);
        FileInfo info = Resolved(host);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"no such file: {path}", host);
        }

        long fileLength = info.Length;
        FileRange range = FileRange.Rent(FileRange.Available(offset, length, fileLength), offset, fileLength);
        try
        {
            Span<byte> destination = range.Memory.Span;
            if (destination.IsEmpty)
            {
                // Nothing in range: the file exists and the answer is its
                // length, which needs no handle.
                return range;
            }

            using SafeFileHandle handle = File.OpenHandle(
                host, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None);

            int filled = 0;
            while (filled < destination.Length)
            {
                int read = RandomAccess.Read(handle, destination[filled..], offset + filled);
                if (read == 0)
                {
                    // The file shrank between the length and the read: the
                    // same failure a whole read reports, rather than a range
                    // that claims bytes it does not hold.
                    throw new EndOfStreamException(
                        $"{path} ended at {offset + filled} of the {fileLength} bytes it had");
                }

                filled += read;
            }

            return range;
        }
        catch
        {
            range.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask<Stream> OpenWriteAsync(VPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string host = ToHostPath(path);
        EnsureParentDirectory(host);

        Stream stream = new FileStream(
            host,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous);

        return ValueTask.FromResult(stream);
    }

    /// <inheritdoc />
    public async ValueTask ReplaceAsync(
        VPath path,
        Func<Stream, CancellationToken, ValueTask> write,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        cancellationToken.ThrowIfCancellationRequested();

        string host = ToHostPath(path);
        EnsureParentDirectory(host);

        // Beside the target, never in the system temp directory: a rename is
        // only atomic within one filesystem, and /tmp is routinely a different
        // one. A temp file on tmpfs would turn this into copy-then-truncate,
        // which is exactly the half-written .bsp the method exists to prevent.
        string temporary = host + ".ssmap-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8] + ".tmp";

        try
        {
            await using (FileStream stream = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous))
            {
                await write(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // Checked after the writer finished, not before: a cancelled write
            // must leave the previous file, and a token that fires while the
            // last buffer is flushing is still a cancelled write.
            cancellationToken.ThrowIfCancellationRequested();

            File.Move(temporary, host, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> ExistsAsync(VPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(File.Exists(ToHostPath(path)));
    }

    /// <inheritdoc />
    public ValueTask<FileInfoSnapshot?> GetInfoAsync(
        VPath path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        FileInfo info = Resolved(ToHostPath(path));
        FileInfoSnapshot? snapshot = info.Exists
            ? new FileInfoSnapshot(info.Length, info.LastWriteTimeUtc)
            : null;

        return ValueTask.FromResult(snapshot);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<VPath> EnumerateAsync(
        VPath directory,
        string searchPattern = "*",
        bool recursive = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(searchPattern);

        string host = ToHostPath(directory);
        if (!Directory.Exists(host))
        {
            yield break;
        }

        EnumerationOptions options = new()
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            MatchType = MatchType.Simple,
            ReturnSpecialDirectories = false,
        };

        // The platform's own pattern is deliberately not used: see GlobMatcher
        // for why every implementation has to agree about what "*.vmt" means.
        foreach (string file in Directory.EnumerateFiles(host, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!GlobMatcher.IsMatch(Path.GetFileName(file.AsSpan()), searchPattern))
            {
                continue;
            }

            yield return ToVirtualPath(file);
        }

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(VPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(ToHostPath(path));
        return ValueTask.CompletedTask;
    }

    private static void EnsureParentDirectory(string hostPath)
    {
        string? parent = Path.GetDirectoryName(hostPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }

    private static void TryDelete(string hostPath)
    {
        try
        {
            File.Delete(hostPath);
        }
        catch (IOException)
        {
            // The write already failed; losing a temp file on top of that is
            // not worth replacing the caller's exception with this one.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
