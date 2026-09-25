namespace SourceSharp.MapFormats.Text;

/// <summary>
/// What one successful read step produced: a port of <c>ChunkType_t</c>
/// (<c>src/public/chunkfile.h:57-61</c>).
/// </summary>
public enum ChunkTermType
{
    /// <summary><c>ChunkType_Key</c>: a <c>"key" "value"</c> pair.</summary>
    Key = 0,

    /// <summary>
    /// <c>ChunkType_Chunk</c>: a chunk name followed by <c>{</c>. The chunk's
    /// body has not been read yet and the depth has already increased.
    /// </summary>
    Chunk,
}
