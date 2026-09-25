using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;

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
    /// <param name="hostPath">An absolute host path at or below <see cref="Root"/>.</param>
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
            throw new ArgumentException(
                $"\"{hostPath}\" is not below the root \"{Root}\"",
                nameof(hostPath));
        }

        return VPath.Create(full[prefix.Length..]);
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

    /// <inheritdoc />
    public async ValueTask<IMemoryOwner<byte>> ReadAllAsync(
        VPath path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string host = ToHostPath(path);
        var info = new FileInfo(host);
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
            await using FileStream stream = new(
                host,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            await stream.ReadExactlyAsync(owner.Memory, cancellationToken).ConfigureAwait(false);
            return owner;
        }
        catch
        {
            owner.Dispose();
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

        var info = new FileInfo(ToHostPath(path));
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
