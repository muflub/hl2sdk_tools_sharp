namespace SourceSharp.MapFormats.Text;

/// <summary>
/// One term inside a chunk: either a key/value pair or a nested chunk.
/// </summary>
/// <remarks>
/// Keys and chunks live in ONE ordered list rather than two, because the round
/// trip is gated on bytes. Hammer writes every key before every sub-chunk, but
/// the reader does not require it (<c>CChunkFile::ReadChunk</c> dispatches each
/// term as it arrives, <c>src/public/chunkfile.cpp:583-608</c>), a hand-edited
/// VMF may interleave them, and separating the two lists would silently reorder
/// such a file on write.
/// </remarks>
public abstract class VmfNode
{
    /// <summary>Creates a node with a name.</summary>
    /// <param name="name">The key name or chunk name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    protected VmfNode(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
    }

    /// <summary>The key name, or the chunk name.</summary>
    public string Name { get; set; }
}
