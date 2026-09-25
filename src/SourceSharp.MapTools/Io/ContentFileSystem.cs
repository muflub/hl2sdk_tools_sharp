using System.Buffers;
using System.Runtime.CompilerServices;

namespace SourceSharp.MapTools.Io;

/// <summary>
/// Game content behind one lookup: an ordered list of mounts, first match wins.
/// </summary>
/// <remarks>
/// <para>
/// The order is the search-path order out of <c>gameinfo.txt</c>, and it is
/// OBSERVABLE: <see cref="ResolveAsync"/> says which mount answered, so a fact
/// can pin the order against a fixture holding the same name in two mounts
/// rather than inferring it from the bytes that came back.
/// </para>
/// <para>
/// Lookups are case-insensitive, because each mount folds case in an index
/// built when it was mounted. See <see cref="IContentMount"/> for why an index
/// and not a probe.
/// </para>
/// </remarks>
public sealed class ContentFileSystem : IContentFileSystem, IAsyncDisposable
{
    private readonly IReadOnlyList<IContentMount> _mounts;
    private readonly bool _ownsMounts;

    /// <summary>Layers a list of mounts, earliest first.</summary>
    /// <param name="mounts">The mounts, in search-path order.</param>
    /// <param name="ownsMounts">Whether disposing this disposes them.</param>
    public ContentFileSystem(IReadOnlyList<IContentMount> mounts, bool ownsMounts = true)
    {
        ArgumentNullException.ThrowIfNull(mounts);

        _mounts = [.. mounts];
        _ownsMounts = ownsMounts;
    }

    /// <summary>The mounts, in the order they are searched.</summary>
    public IReadOnlyList<IContentMount> Mounts => _mounts;

    /// <inheritdoc />
    public ValueTask<ContentSource?> ResolveAsync(
        VPath path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Resolve(path));
    }

    /// <summary>
    /// Finds which mount a content path resolves to, without reading it and
    /// without awaiting.
    /// </summary>
    /// <param name="path">A content-relative path, in any casing.</param>
    /// <returns>Where it resolved, or null when nothing has it.</returns>
    /// <remarks>
    /// Every mount answers from an index already in memory, so the asynchronous
    /// surface is there for the interface's sake and this is the same answer
    /// without the state machine. Useful inside a loop over a map's material
    /// list; the interface method is what a caller holding an
    /// <see cref="IContentFileSystem"/> uses.
    /// </remarks>
    public ContentSource? Resolve(VPath path)
    {
        foreach (IContentMount mount in _mounts)
        {
            if (mount.TryResolve(path, out VPath actual))
            {
                return new ContentSource(actual, mount.Name);
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async ValueTask<IMemoryOwner<byte>?> ReadAsync(
        VPath path,
        CancellationToken cancellationToken = default)
    {
        foreach (IContentMount mount in _mounts)
        {
            if (!mount.TryResolve(path, out VPath actual))
            {
                continue;
            }

            return await mount.ReadAsync(actual, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<VPath> EnumerateAsync(
        VPath directory,
        string searchPattern = "*",
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(searchPattern);

        // De-duplicated on the FOLDED path, so a file present in two mounts
        // under two spellings is one result and not two -- the same rule that
        // makes it one dependency record.
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (IContentMount mount in _mounts)
        {
            foreach (VPath path in mount.Paths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!IsIn(path, directory) || !GlobMatcher.IsMatch(path.FileName, searchPattern))
                {
                    continue;
                }

                if (seen.Add(ContentIndex.Fold(path)))
                {
                    yield return path;
                }
            }
        }

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_ownsMounts)
        {
            return;
        }

        foreach (IContentMount mount in _mounts)
        {
            await mount.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool IsIn(VPath path, VPath directory)
    {
        if (directory.IsEmpty)
        {
            return true;
        }

        // Folded, because the caller's directory came out of a map file and is
        // subject to the same casing rule as everything else here.
        string value = ContentIndex.Fold(path);
        string prefix = ContentIndex.Fold(directory);

        return value.Length > prefix.Length
            && value[prefix.Length] == '/'
            && value.StartsWith(prefix, StringComparison.Ordinal);
    }
}
