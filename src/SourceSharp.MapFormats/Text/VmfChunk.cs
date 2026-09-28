//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// A named block of keys and nested chunks: <c>world</c>, <c>entity</c>,
/// <c>solid</c>, <c>side</c>, <c>dispinfo</c> and the rest.
/// </summary>
/// <remarks>
/// Deliberately untyped. vbsp gives each chunk name a handler that builds a
/// specific object, but that is vbsp's model of a map, not the FILE's: the
/// format itself is name, keys, sub-chunks, and an unrecognised chunk is
/// skipped whole rather than rejected.
/// Keeping the tree untyped is what lets this reader round-trip a VMF from a
/// newer Hammer, or one carrying a mod's own chunks, without losing them.
/// </remarks>
public sealed class VmfChunk : VmfNode
{
    /// <summary>Creates an empty chunk.</summary>
    /// <param name="name">The chunk name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public VmfChunk(string name)
        : base(name)
    {
    }

    /// <summary>
    /// The chunk's contents, keys and sub-chunks together, in file order.
    /// </summary>
    public IList<VmfNode> Children { get; } = [];

    /// <summary>The key/value pairs, in file order.</summary>
    public IEnumerable<VmfKey> Keys => Children.OfType<VmfKey>();

    /// <summary>The nested chunks, in file order.</summary>
    public IEnumerable<VmfChunk> Chunks => Children.OfType<VmfChunk>();

    /// <summary>
    /// The value of the first key with this name, or null when there is none.
    /// </summary>
    /// <param name="name">The key name.</param>
    /// <returns>The value, or null.</returns>
    /// <remarks>
    /// Case-insensitive, because every key handler in the reference
    /// implementation compares with <c>stricmp</c>.
    /// FIRST and not last: a duplicate key is a malformed VMF, and vbsp's
    /// handlers act on each occurrence as it arrives, so the first is the one
    /// that decided the outcome for the keys that are read once.
    /// </remarks>
    public string? GetValue(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        foreach (VmfNode node in Children)
        {
            if (node is VmfKey key && string.Equals(key.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return key.Value;
            }
        }

        return null;
    }

    /// <summary>Every nested chunk with this name, in file order.</summary>
    /// <param name="name">The chunk name.</param>
    /// <returns>The matching chunks.</returns>
    public IEnumerable<VmfChunk> GetChunks(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Chunks.Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The first nested chunk with this name, or null.</summary>
    /// <param name="name">The chunk name.</param>
    /// <returns>The chunk, or null.</returns>
    /// <remarks>
    /// A plain loop rather than <c>GetChunks(name).FirstOrDefault()</c>: the
    /// map loader asks every side for its <c>dispinfo</c>, and the LINQ
    /// spelling built a closure, two iterators and a boxed enumerator per
    /// question -- tens of thousands of small objects for a question whose
    /// answer is almost always "none". The answer is the same: the first child
    /// that is a chunk with the name, compared case-insensitively.
    /// </remarks>
    public VmfChunk? GetChunk(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        IList<VmfNode> children = Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is VmfChunk chunk && string.Equals(chunk.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return chunk;
            }
        }

        return null;
    }

    /// <summary>Appends a key/value pair.</summary>
    /// <param name="name">The key name.</param>
    /// <param name="value">The value.</param>
    /// <returns>The key that was added.</returns>
    public VmfKey AddKey(string name, string value)
    {
        VmfKey key = new(name, value);
        Children.Add(key);
        return key;
    }

    /// <summary>Appends an empty nested chunk.</summary>
    /// <param name="name">The chunk name.</param>
    /// <returns>The chunk that was added.</returns>
    public VmfChunk AddChunk(string name)
    {
        VmfChunk chunk = new(name);
        Children.Add(chunk);
        return chunk;
    }
}
