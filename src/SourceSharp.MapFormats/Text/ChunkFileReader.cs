namespace SourceSharp.MapFormats.Text;

/// <summary>
/// The reading half of <c>CChunkFile</c>
/// (<c>src/public/chunkfile.cpp:473-566</c>): a flat stream of chunk-opens,
/// chunk-closes and key/value pairs over a <see cref="ChunkTokenReader"/>.
/// </summary>
/// <remarks>
/// <para>
/// The C++ dispatches through a stack of handler maps and function pointers
/// (<c>CChunkHandlerMap</c>, <c>chunkfile.cpp:56-160</c>). That structure
/// exists to let vbsp build its own objects as it reads, with a callback per
/// chunk name; it is not part of the FILE FORMAT. This port keeps the reading
/// rule -- <c>ReadNext</c>, exactly -- and drops the dispatch, because
/// <see cref="VmfDocument"/> builds a tree and anything that wants a handler
/// per chunk name can walk the tree. Nothing in the format is lost: the handler
/// map only ever decided which callback saw a chunk, never how it was parsed.
/// </para>
/// <para>
/// The one behaviour the dispatch DID contribute is that an unhandled chunk is
/// skipped whole rather than being an error (<c>chunkfile.cpp:344-399</c>), and
/// a tree has that for free.
/// </para>
/// </remarks>
public sealed class ChunkFileReader
{
    /// <summary>
    /// The length of the C++ name and value buffers, <c>MAX_KEYVALUE_LEN</c>
    /// (<c>src/public/chunkfile.h:20</c>).
    /// </summary>
    /// <remarks>
    /// <c>ReadChunk</c> declares <c>szName</c> and <c>szValue</c> at this size
    /// (<c>chunkfile.cpp:585-586</c>) and passes <c>sizeof(szValue)</c> as the
    /// value limit, so a value is silently truncated to 1023 characters plus a
    /// terminator by <c>Q_strncpy</c> at <c>:517</c>. A name is passed
    /// <c>MAX_KEYVALUE_LEN</c> directly at <c>:476</c>, and the tokenizer
    /// truncates an over-long IDENT without complaint
    /// (<c>tokenreader.cpp:326-330</c>) while reporting an over-long STRING as
    /// <see cref="ChunkFileResult.StringTooLong"/> -- an asymmetry this port
    /// reproduces.
    /// </remarks>
    public const int MaxKeyValueLength = 1024;

    /// <summary>
    /// The C++ handler-stack and indent-string depth, <c>MAX_INDENT_DEPTH</c>
    /// (<c>src/public/chunkfile.h:19</c>).
    /// </summary>
    public const int MaxIndentDepth = 80;

    private readonly ChunkTokenReader _tokens;

    /// <summary>Creates a reader over a tokenizer.</summary>
    /// <param name="tokens">The tokenizer, positioned at the start of a chunk body.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tokens"/> is null.</exception>
    public ChunkFileReader(ChunkTokenReader tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        _tokens = tokens;
    }

    /// <summary>The tokenizer this reader is reading from.</summary>
    public ChunkTokenReader Tokens => _tokens;

    /// <summary>
    /// The nesting depth, <c>m_nCurrentDepth</c>. Incremented by a chunk open
    /// and decremented by a chunk close, inside <c>ReadNext</c> itself
    /// (<c>chunkfile.cpp:500,541</c>).
    /// </summary>
    /// <remarks>
    /// It is what decides whether end of input is
    /// <see cref="ChunkFileResult.EndOfFile"/> or
    /// <see cref="ChunkFileResult.UnexpectedEndOfFile"/>
    /// (<c>chunkfile.cpp:559-565</c>), and the C++ shares this one counter
    /// between reading and writing.
    /// </remarks>
    public int CurrentDepth { get; private set; }

    /// <summary>
    /// The token that caused the last
    /// <see cref="ChunkFileResult.UnexpectedSymbol"/>, <c>m_szErrorToken</c>.
    /// </summary>
    public string ErrorToken { get; private set; } = string.Empty;

    /// <summary>
    /// Reads the next term, a port of <c>CChunkFile::ReadNext</c>
    /// (<c>chunkfile.cpp:473-566</c>).
    /// </summary>
    /// <param name="name">Receives the key name or the chunk name.</param>
    /// <param name="value">
    /// Receives the value when the result is
    /// <see cref="ChunkFileResult.Ok"/> and <paramref name="termType"/> is
    /// <see cref="ChunkTermType.Key"/>; empty otherwise.
    /// </param>
    /// <param name="termType">Whether a key or a chunk was read.</param>
    /// <returns>The outcome.</returns>
    public ChunkFileResult ReadNext(out string name, out string value, out ChunkTermType termType)
    {
        value = string.Empty;
        termType = ChunkTermType.Key;

        ChunkTokenType tokenType = _tokens.NextToken(out name);
        name = Truncate(name, MaxKeyValueLength);

        if (tokenType != ChunkTokenType.EndOfFile)
        {
            if (tokenType is ChunkTokenType.Identifier or ChunkTokenType.String)
            {
                ChunkTokenType nextType = _tokens.NextToken(out string next);

                switch (nextType)
                {
                    case ChunkTokenType.Operator:
                        // chunkfile.cpp:497-510. stricmp against "{", which has
                        // no case, so the comparison is just equality.
                        if (next == "{")
                        {
                            CurrentDepth++;
                            termType = ChunkTermType.Chunk;
                            value = string.Empty;
                            return ChunkFileResult.Ok;
                        }

                        ErrorToken = next;
                        return ChunkFileResult.UnexpectedSymbol;

                    case ChunkTokenType.String:
                    case ChunkTokenType.Identifier:
                        // chunkfile.cpp:513-520.
                        value = Truncate(next, MaxKeyValueLength);
                        termType = ChunkTermType.Key;
                        return ChunkFileResult.Ok;

                    case ChunkTokenType.EndOfFile:
                        // chunkfile.cpp:522-526. A name with no value is an
                        // unexpected EOF even at depth zero -- unlike a name
                        // with no NAME, which is a clean EOF at :565.
                        return ChunkFileResult.UnexpectedEndOfFile;

                    case ChunkTokenType.StringTooLong:
                        // chunkfile.cpp:528-532.
                        return ChunkFileResult.StringTooLong;

                    default:
                        // THE FALL-THROUGH. The C++ inner switch has no default
                        // and the outer `case IDENT: case STRING:` block ends
                        // without a break, so an INTEGER or a TOKENERROR here
                        // drops into `case OPERATOR:` at chunkfile.cpp:536 --
                        // which then tests szNAME, not szNext, against "}".
                        //
                        // So `"key" 12` is reported as an unexpected symbol
                        // naming "key", and `mykey` followed by a malformed
                        // number is too. Reproduced deliberately: it is the
                        // diagnostic a stock user sees.
                        break;
                }
            }

            // chunkfile.cpp:536-550, reached directly for an operator and by
            // fall-through for the cases above.
            if (tokenType == ChunkTokenType.Operator ||
                tokenType is ChunkTokenType.Identifier or ChunkTokenType.String)
            {
                if (name == "}")
                {
                    CurrentDepth--;
                    return ChunkFileResult.EndOfChunk;
                }

                ErrorToken = name;
                return ChunkFileResult.UnexpectedSymbol;
            }

            if (tokenType == ChunkTokenType.StringTooLong)
            {
                // chunkfile.cpp:552-555.
                return ChunkFileResult.StringTooLong;
            }

            // TOKENERROR as the FIRST token matches no case at all in the C++
            // switch, so control reaches the depth test below -- which reports
            // EOF or UnexpectedEOF for what is really a malformed number.
        }

        // chunkfile.cpp:559-565.
        return CurrentDepth != 0
            ? ChunkFileResult.UnexpectedEndOfFile
            : ChunkFileResult.EndOfFile;
    }

    /// <summary>
    /// <c>Q_strncpy</c> into a fixed buffer: at most
    /// <paramref name="limit"/> - 1 characters survive.
    /// </summary>
    private static string Truncate(string text, int limit) =>
        text.Length <= limit - 1 ? text : text[..(limit - 1)];
}
