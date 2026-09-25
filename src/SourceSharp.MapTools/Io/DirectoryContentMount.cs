using System.Buffers;

namespace SourceSharp.MapTools.Io;

/// <summary>
/// A loose directory mounted as content: <c>hl2/</c>, a mod folder, a
/// <c>custom/</c> subdirectory.
/// </summary>
/// <remarks>
/// The whole subtree is walked ONCE, at mount time, to build the case-folded
/// index. That costs one directory traversal of a game install and buys every
/// subsequent lookup for the price of a dictionary probe — against a compile
/// that resolves tens of thousands of materials and models, the traversal is
/// the cheaper half by a long way, and it is the only way to answer "which file
/// did I get?" consistently.
/// </remarks>
public sealed class DirectoryContentMount : IContentMount
{
    private readonly IFileSystem _fileSystem;
    private readonly ContentIndex _index;
    private readonly VPath _root;

    private DirectoryContentMount(IFileSystem fileSystem, VPath root, ContentIndex index)
    {
        _fileSystem = fileSystem;
        _root = root;
        _index = index;
        Name = root.IsEmpty ? "/" : root.Value;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<VPath> Paths => _index.Paths;

    /// <summary>
    /// Pairs of files in this directory whose paths differ only by case.
    /// </summary>
    /// <remarks>
    /// Empty on a Windows install, because the filesystem could not hold both.
    /// A non-empty list means content that resolves differently on the two
    /// platforms, which is worth a linter saying out loud.
    /// </remarks>
    public IReadOnlyList<(VPath Kept, VPath Shadowed)> Collisions => _index.Collisions;

    /// <summary>Walks a directory and indexes what is in it.</summary>
    /// <param name="fileSystem">Where the directory lives.</param>
    /// <param name="root">The directory to mount; content paths are relative to it.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>The mount.</returns>
    public static async ValueTask<DirectoryContentMount> MountAsync(
        IFileSystem fileSystem,
        VPath root,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        ContentIndex index = new();
        string prefix = root.IsEmpty ? string.Empty : root.Value + "/";

        await foreach (VPath path in fileSystem
            .EnumerateAsync(root, GlobMatcher.MatchAll, recursive: true, cancellationToken)
            .ConfigureAwait(false))
        {
            // EnumerateAsync answers in the file system's own coordinates; the
            // index is keyed on paths relative to the mount, because that is
            // what a map references.
            index.Add(prefix.Length == 0 ? path : VPath.Create(path.Value[prefix.Length..]));
        }

        return new DirectoryContentMount(fileSystem, root, index);
    }

    /// <inheritdoc />
    public bool TryResolve(VPath path, out VPath actual) => _index.TryResolve(path, out actual);

    /// <inheritdoc />
    public async ValueTask<IMemoryOwner<byte>?> ReadAsync(
        VPath actual,
        CancellationToken cancellationToken = default)
    {
        if (!_index.TryResolve(actual, out VPath resolved))
        {
            return null;
        }

        return await _fileSystem
            .ReadAllAsync(_root.IsEmpty ? resolved : _root.Combine(resolved.Value), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
