using System.Globalization;
using System.Security.Cryptography;

namespace SourceSharp.MapTools.Io;

/// <summary>
/// How a path took part in a stage's input set.
/// </summary>
/// <remarks>
/// Ordered by how much it constrains a later run, which is what makes merging
/// two observations of one path a <c>max</c>: reading a file subsumes having
/// resolved it, and both subsume having missed it.
/// </remarks>
public enum DependencyKind
{
    /// <summary>
    /// The lookup found nothing. Still an input: a file later added to an
    /// earlier mount changes what the same lookup returns, so a cached product
    /// made under this miss is no longer valid.
    /// </summary>
    Missing = 0,

    /// <summary>
    /// The file was found but its bytes were never read. Its EXISTENCE was an
    /// input — code branched on it — so it is recorded, without a hash.
    /// </summary>
    Resolved = 1,

    /// <summary>The file's bytes were read, and are hashed.</summary>
    Read = 2,
}

/// <summary>
/// One path in a stage's recorded input set.
/// </summary>
/// <param name="Path">
/// The path as it RESOLVED, not as it was asked for, so that two spellings of
/// one material are one dependency.
/// </param>
/// <param name="Kind">How the path took part.</param>
/// <param name="ContentHash">
/// Lower-case hex SHA-256 of the file's bytes, or null when the file was not
/// read. Content, never a modification time: this project has twice had a
/// proof invalidated by a restore that preserved an mtime.
/// </param>
public readonly record struct FileDependency(VPath Path, DependencyKind Kind, string? ContentHash);

/// <summary>
/// The input set a compile actually touched, collected at the
/// <see cref="IFileSystem"/> and <see cref="IContentFileSystem"/> seams.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole mechanism the incremental cache rests on. Because the
/// recorder is a DECORATOR at the one seam every byte passes through, there is
/// no code path that can read a file the cache does not know about — which is
/// the difference between an incremental build that is sound and one that is
/// hopeful. Being wired now, during the port, costs nothing; retrofitting it
/// later would mean auditing every reader.
/// </para>
/// <para>
/// Misses are recorded, and that is not an optimisation detail. Resolution is
/// first-match-wins over an ordered list of mounts, so a file added to an
/// EARLIER mount changes what a later resolve returns even though every file
/// the last run read is untouched. A recorder that dropped misses would report
/// that input set as unchanged, and the cache would hand back a product
/// compiled against content that is no longer what the game would load.
/// </para>
/// <para>
/// Thread-safe, because compile stages read in parallel.
/// </para>
/// </remarks>
public sealed class DependencyRecorder
{
    private readonly Dictionary<VPath, FileDependency> _entries = [];
    private readonly Lock _gate = new();

    /// <summary>How many distinct paths have been recorded.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Hashes <paramref name="content"/> the way a dependency is hashed.</summary>
    /// <param name="content">The bytes to hash.</param>
    /// <returns>Lower-case hex SHA-256.</returns>
    public static string Hash(ReadOnlySpan<byte> content)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(content, digest);
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>Records that a file's bytes were read.</summary>
    /// <param name="path">The path as it resolved.</param>
    /// <param name="content">The bytes that were read.</param>
    public void RecordRead(VPath path, ReadOnlySpan<byte> content) =>
        Merge(new FileDependency(path, DependencyKind.Read, Hash(content)));

    /// <summary>Records that a file's bytes were read, with a hash already in hand.</summary>
    /// <param name="path">The path as it resolved.</param>
    /// <param name="contentHash">Lower-case hex SHA-256 of the bytes.</param>
    public void RecordRead(VPath path, string contentHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentHash);
        Merge(new FileDependency(path, DependencyKind.Read, contentHash));
    }

    /// <summary>Records that a file was found but not read.</summary>
    /// <param name="path">The path as it resolved.</param>
    public void RecordResolved(VPath path) =>
        Merge(new FileDependency(path, DependencyKind.Resolved, null));

    /// <summary>Records a lookup that found nothing.</summary>
    /// <param name="path">The path that was asked for.</param>
    public void RecordMiss(VPath path) =>
        Merge(new FileDependency(path, DependencyKind.Missing, null));

    /// <summary>
    /// The recorded input set, ordered by path so that two runs that touched the
    /// same files produce the same sequence and therefore the same key.
    /// </summary>
    /// <returns>One entry per distinct path.</returns>
    public IReadOnlyList<FileDependency> Snapshot()
    {
        lock (_gate)
        {
            List<FileDependency> entries = [.. _entries.Values];
            entries.Sort(static (a, b) => string.CompareOrdinal(a.Path.Value, b.Path.Value));
            return entries;
        }
    }

    /// <summary>The entry recorded for a path, if any.</summary>
    /// <param name="path">The path to look up.</param>
    /// <returns>Its entry, or null when the path was never touched.</returns>
    public FileDependency? Find(VPath path)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(path, out FileDependency entry) ? entry : null;
        }
    }

    /// <summary>
    /// A stable digest of the whole recorded input set, for use as one component
    /// of a cache key.
    /// </summary>
    /// <returns>Lower-case hex SHA-256 over the ordered entries.</returns>
    /// <remarks>
    /// A miss contributes its path and the fact that it missed, so adding the
    /// file that was missing changes this digest — which is the point.
    /// </remarks>
    public string ComputeDigest()
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (FileDependency entry in Snapshot())
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(entry.Path.Value));
            hash.AppendData([(byte)entry.Kind]);
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(
                entry.ContentHash ?? string.Empty));
            hash.AppendData([0]);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>A human-readable listing, for <c>cache explain</c> and for a failed fact.</summary>
    /// <returns>One line per dependency.</returns>
    public override string ToString()
    {
        System.Text.StringBuilder text = new();

        foreach (FileDependency entry in Snapshot())
        {
            text.Append(CultureInfo.InvariantCulture, $"{entry.Kind,-8} {entry.Path}");

            if (entry.ContentHash is not null)
            {
                text.Append(CultureInfo.InvariantCulture, $"  {entry.ContentHash[..16]}");
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    private void Merge(FileDependency observation)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(observation.Path, out FileDependency existing))
            {
                _entries[observation.Path] = observation;
                return;
            }

            if (observation.Kind > existing.Kind)
            {
                _entries[observation.Path] = observation;
            }
            else if (observation.Kind == existing.Kind && existing.ContentHash is null)
            {
                _entries[observation.Path] = observation;
            }
        }
    }
}
