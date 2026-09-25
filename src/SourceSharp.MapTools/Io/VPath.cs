using System.Diagnostics.CodeAnalysis;

namespace SourceSharp.MapTools.Io;

/// <summary>
/// A path inside the library's own world: forward slashes, normalised, and
/// unable to escape upwards.
/// </summary>
/// <remarks>
/// <para>
/// The compilers deal in three path worlds at once -- the host disk, a game's
/// search paths, and the inside of a VPK or an embedded pak -- and only the
/// first of them is a filesystem. Giving all three one path type means the
/// readers cannot tell which they are reading from, which is the point: a
/// compile that runs entirely out of memory uses the same code as one that
/// reads a disk.
/// </para>
/// <para>
/// Two rules are enforced at construction rather than checked at use. Segments
/// are separated by a single forward slash, because that is what Source content
/// paths use everywhere including inside pak files on Windows. And <c>..</c>
/// may not escape past the root -- a content path comes out of a map file or a
/// material, which is to say out of data this process did not write, and a
/// dependency recorder that can be walked up to <c>/etc</c> is not a seam.
/// </para>
/// <para>
/// Comparison is ORDINAL and case-SENSITIVE here, deliberately, even though
/// Source content lookup is case-insensitive. Case folding is a property of a
/// particular mount -- the content filesystem builds a folded index over a
/// case-sensitive Linux disk -- and not of a path. Folding here would make two
/// genuinely different files on disk compare equal in contexts, like the
/// dependency recorder's content hashes, that have nothing to do with content
/// lookup.
/// </para>
/// </remarks>
public readonly struct VPath : IEquatable<VPath>
{
    private readonly string? _value;

    private VPath(string value) => _value = value;

    /// <summary>The empty path, which names nothing.</summary>
    public static VPath Empty => default;

    /// <summary>The path's text, forward-slashed and normalised.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>Whether this path names nothing.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(_value);

    /// <summary>
    /// Normalises <paramref name="path"/> into a <see cref="VPath"/>.
    /// </summary>
    /// <param name="path">
    /// A path using either slash. Repeated separators, <c>.</c> segments and
    /// trailing slashes are removed; <c>..</c> is resolved against the
    /// preceding segment.
    /// </param>
    /// <returns>The normalised path.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> walks above its own root, or contains a NUL.
    /// </exception>
    public static VPath Create(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!TryCreate(path, out VPath result, out string? error))
        {
            throw new ArgumentException(error, nameof(path));
        }

        return result;
    }

    /// <summary>
    /// Normalises <paramref name="path"/> without throwing when it is not a
    /// legal <see cref="VPath"/>.
    /// </summary>
    /// <param name="path">The path to normalise.</param>
    /// <param name="result">The normalised path, or <see cref="Empty"/> on failure.</param>
    /// <returns>True when <paramref name="path"/> is legal.</returns>
    public static bool TryCreate(string? path, out VPath result) =>
        TryCreate(path, out result, out _);

    private static bool TryCreate(
        string? path,
        out VPath result,
        [NotNullWhen(false)] out string? error)
    {
        result = Empty;
        error = null;

        if (path is null)
        {
            error = "a path may not be null";
            return false;
        }

        if (path.Contains('\0', StringComparison.Ordinal))
        {
            error = "a path may not contain a NUL";
            return false;
        }

        if (path.Length == 0)
        {
            return true;
        }

        List<string> segments = [];
        int start = 0;
        for (int i = 0; i <= path.Length; i++)
        {
            bool atEnd = i == path.Length;
            if (!atEnd && path[i] != '/' && path[i] != '\\')
            {
                continue;
            }

            ReadOnlySpan<char> segment = path.AsSpan(start, i - start);
            start = i + 1;

            if (segment.Length == 0 || segment is ".")
            {
                continue;
            }

            if (segment is "..")
            {
                if (segments.Count == 0)
                {
                    error = $"\"{path}\" walks above its own root";
                    return false;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment.ToString());
        }

        result = new VPath(string.Join('/', segments));
        return true;
    }

    /// <summary>
    /// Appends <paramref name="relative"/> beneath this path.
    /// </summary>
    /// <param name="relative">A relative path; it may contain <c>..</c>.</param>
    /// <returns>The combined, normalised path.</returns>
    /// <exception cref="ArgumentException">
    /// The combination walks above this path's root.
    /// </exception>
    public VPath Combine(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        return IsEmpty ? Create(relative) : Create($"{Value}/{relative}");
    }

    /// <summary>The final segment, including any extension.</summary>
    public string FileName
    {
        get
        {
            string value = Value;
            int slash = value.LastIndexOf('/');
            return slash < 0 ? value : value[(slash + 1)..];
        }
    }

    /// <summary>
    /// The extension of the final segment, lower-cased and including the dot,
    /// or the empty string when there is none.
    /// </summary>
    /// <remarks>
    /// Lower-cased because an extension is a format tag rather than content: a
    /// map that references <c>Foo.VMT</c> and a disk that holds <c>foo.vmt</c>
    /// mean the same format, and every caller of this would otherwise fold the
    /// case itself and one of them would forget.
    /// </remarks>
    public string Extension
    {
        get
        {
            string name = FileName;
            int dot = name.LastIndexOf('.');
            return dot < 0 ? string.Empty : name[dot..].ToLowerInvariant();
        }
    }

    /// <summary>
    /// Everything before the final segment, or <see cref="Empty"/> at the root.
    /// </summary>
    public VPath Directory
    {
        get
        {
            string value = Value;
            int slash = value.LastIndexOf('/');
            return slash < 0 ? Empty : new VPath(value[..slash]);
        }
    }

    /// <summary>
    /// This path with its extension replaced.
    /// </summary>
    /// <param name="extension">
    /// The new extension, with or without a leading dot.
    /// </param>
    /// <returns>The path with the new extension.</returns>
    public VPath WithExtension(string extension)
    {
        ArgumentNullException.ThrowIfNull(extension);

        string suffix = extension.Length == 0 || extension[0] == '.' ? extension : "." + extension;
        string value = Value;
        string name = FileName;
        int dot = name.LastIndexOf('.');
        int cut = dot < 0 ? value.Length : value.Length - (name.Length - dot);
        return new VPath(value[..cut] + suffix);
    }

    /// <inheritdoc />
    public bool Equals(VPath other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is VPath other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Compares two paths ordinally.</summary>
    /// <param name="left">The first path.</param>
    /// <param name="right">The second path.</param>
    /// <returns>True when they are the same path.</returns>
    public static bool operator ==(VPath left, VPath right) => left.Equals(right);

    /// <summary>Compares two paths ordinally.</summary>
    /// <param name="left">The first path.</param>
    /// <param name="right">The second path.</param>
    /// <returns>True when they are different paths.</returns>
    public static bool operator !=(VPath left, VPath right) => !left.Equals(right);
}
