using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SourceSharp.MapTools.Compile.Cache;

/// <summary>
/// The identity of one memoised stage product: a digest over the stage name,
/// the tool that made it, the semantic inputs, the option subset, the recorded
/// file dependencies and the host's context tags (plan_maptools.md 10a).
/// </summary>
/// <remarks>
/// <para>
/// <b>Content, never mtimes.</b> Every component is a content hash. This
/// project has twice had a proof invalidated by a restore that preserved a
/// modification time (<c>copy2-mtime-fakes-a-mutation-proof</c>), so a
/// timestamp never enters a key and never short-circuits a re-hash. Where a
/// caller wants the mtime fast path it is a HINT only: it may skip reading a
/// file whose size and mtime both match, and nothing else may be skipped, and
/// the default implementation takes no hint at all.
/// </para>
/// <para>
/// <b>The parts are carried, not just the digest</b>, because
/// <c>ssmap cache explain</c> answers "which component changed?" and a
/// bare 32-byte digest cannot. <see cref="Parts"/> is the pre-image in the
/// exact order the digest folded it.
/// </para>
/// <para>
/// <see cref="ContextTags"/> is the seam for inputs the chain cannot see
/// into — the host's preset, the cooker selection, anything whose change must
/// invalidate the product but which arrives as opaque strings. The chain
/// hashes them verbatim and NEVER interprets them; distinctness is the host's
/// duty (the contract with <c>ssmap</c>'s option layer: every distinct
/// preset/cooker selection must produce a distinct tag string).
/// </para>
/// </remarks>
public sealed record CacheKey
{
    /// <summary>The schema this key's store rows follow; a mismatch drops the cache rather than migrating it.</summary>
    public const int SchemaVersion = 1;

    private const string KeyPrefix = "sscache1/";

    /// <summary>The stage this product belongs to: <c>vbsp</c>, <c>vvis</c>, <c>vrad</c>, or a sub-stage name.</summary>
    public required string Stage { get; init; }

    /// <summary>
    /// The tool identity — assembly version, commit and the cooker where the
    /// product's bytes came from a cooker (plan 10a: per <c>stage</c> row, not
    /// in <c>meta</c>, so switching worktrees drops only what the other build
    /// made).
    /// </summary>
    public required string ToolId { get; init; }

    /// <summary>
    /// The semantic inputs: for vbsp the canonical digest of the PARSED map
    /// model (never the VMF text, so Hammer's save-time churn invalidates
    /// nothing); for vvis the portal set and the lumps it reads; for vrad the
    /// lumps and rad files it reads, folded in as the dependency digest.
    /// </summary>
    public required string SemanticDigest { get; init; }

    /// <summary>The digest of the option subset this stage reads.</summary>
    public required string OptionsDigest { get; init; }

    /// <summary>
    /// The digest over the verified recorded file dependencies (content hashes
    /// plus recorded misses), or the empty string for a stage with no
    /// dependency set yet (the first, recording run).
    /// </summary>
    public required string DependencyDigest { get; init; }

    /// <summary>
    /// The host's opaque context tags, in the host's order; null/empty when
    /// the host supplies none.
    /// </summary>
    public IReadOnlyList<string> ContextTags { get; init; } = [];

    /// <summary>The named components, in fold order — the data <c>cache explain</c> diffs.</summary>
    public IReadOnlyList<(string Name, string Value)> Parts =>
    [
        ("stage", Stage),
        ("tool", ToolId),
        ("semantic", SemanticDigest),
        ("options", OptionsDigest),
        ("deps", DependencyDigest),
        ("tags", TagDigest),
    ];

    /// <summary>The digest of the context tags alone, so explain can name the tags as the changed component.</summary>
    public string TagDigest => HashComponents(ContextTags);

    /// <summary>The whole key as the store's row identifier: lower-case hex SHA-256.</summary>
    public string Digest
    {
        get
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            // The prefix domain-separates: no other digest in the system folds
            // these components in this order, so a key can never collide with
            // a content hash by construction.
            hash.AppendData(Encoding.UTF8.GetBytes(KeyPrefix));
            foreach ((string name, string value) in Parts)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(name));
                hash.AppendData([0]);
                hash.AppendData(Encoding.UTF8.GetBytes(value));
                hash.AppendData([0]);
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
    }

    /// <summary>The store-level cap on a key string's length; keys are fixed-size digests by construction.</summary>
    internal const int DigestLength = 64;

    /// <summary>Folds ordered string components into one lower-case hex SHA-256.</summary>
    /// <param name="components">The components, in order.</param>
    /// <returns>Lower-case hex SHA-256.</returns>
    public static string HashComponents(IEnumerable<string> components)
    {
        ArgumentNullException.ThrowIfNull(components);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string component in components)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(component));
            hash.AppendData([0]);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Folds arbitrary bytes into one lower-case hex SHA-256.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>Lower-case hex SHA-256.</returns>
    public static string HashBytes(ReadOnlySpan<byte> data)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(data, digest);
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>Whether a store key string is shaped like one of these digests.</summary>
    /// <param name="key">The candidate.</param>
    /// <returns>True when it is 64 lower-case hex characters.</returns>
    public static bool LooksLikeDigest(string? key)
    {
        if (key is not { Length: DigestLength })
        {
            return false;
        }

        foreach (char c in key)
        {
            if (!(c is >= '0' and <= '9') && !(c is >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Formats a millisecond duration the way the report renders costs.</summary>
    /// <param name="costMs">The cost in milliseconds.</param>
    /// <returns>The invariant-culture text.</returns>
    internal static string FormatCost(long costMs) =>
        string.Create(CultureInfo.InvariantCulture, $"{costMs / 1000.0:F1} s");
}
