namespace SourceSharp.MapFormats.Text;

/// <summary>
/// The reading half of the reference chunk-file format: a flat stream of
/// chunk-opens, chunk-closes and key/value pairs over a
/// <see cref="ChunkTokenReader"/>.
/// </summary>
/// <remarks>
/// <para>
/// The reference reader dispatches through a stack of handler maps and
/// function pointers (<c>CChunkHandlerMap</c>). That structure
/// exists to let the original toolchain build its own objects as it reads, with a callback per
/// chunk name; it is not part of the FILE FORMAT. This port keeps the reading
/// rule -- <c>ReadNext</c>, exactly -- and drops the dispatch, because
/// <see cref="VmfDocument"/> builds a tree and anything that wants a handler
/// per chunk name can walk the tree. Nothing in the format is lost: the handler
/// map only ever decided which callback saw a chunk, never how it was parsed.
/// </para>
/// <para>
/// The one behaviour the dispatch DID contribute is that an unhandled chunk is
/// skipped whole rather than being an error, and
/// a tree has that for free.
/// </para>
/// </remarks>
public sealed class ChunkFileReader
{
    /// <summary>
    /// The length of the reference name and value buffers,
    /// <c>MAX_KEYVALUE_LEN</c>.
    /// </summary>
    /// <remarks>
    /// <c>ReadChunk</c> declares <c>szName</c> and <c>szValue</c> at this size
    /// and passes <c>sizeof(szValue)</c> as the
    /// value limit, so a value is silently truncated to 1023 characters plus a
    /// terminator by <c>Q_strncpy</c>. A name is passed
    /// <c>MAX_KEYVALUE_LEN</c> directly, and the reference tokenizer
    /// truncates an over-long IDENT without complaint
    /// while reporting an over-long STRING as
    /// <see cref="ChunkFileResult.StringTooLong"/> -- an asymmetry this port
    /// reproduces.
    /// </remarks>
    public const int MaxKeyValueLength = 1024;

    /// <summary>
    /// The reference handler-stack and indent-string depth,
    /// <c>MAX_INDENT_DEPTH</c>.
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
    /// and decremented by a chunk close, inside <c>ReadNext</c> itself.
    /// </summary>
    /// <remarks>
    /// It is what decides whether end of input is
    /// <see cref="ChunkFileResult.EndOfFile"/> or
    /// <see cref="ChunkFileResult.UnexpectedEndOfFile"/>
    /// and the reference implementation shares this one counter
    /// between reading and writing.
    /// </remarks>
    public int CurrentDepth { get; private set; }

    /// <summary>
    /// The token that caused the last
    /// <see cref="ChunkFileResult.UnexpectedSymbol"/>, <c>m_szErrorToken</c>.
    /// </summary>
    public string ErrorToken { get; private set; } = string.Empty;

    /// <summary>
    /// Reads the next term, following the reference <c>CChunkFile::ReadNext</c>.
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
                        // The reference compares with stricmp against "{", which has
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
                        // The plain key/value case of the reference ReadNext.
                        value = Truncate(next, MaxKeyValueLength);
                        termType = ChunkTermType.Key;
                        return ChunkFileResult.Ok;

                    case ChunkTokenType.EndOfFile:
                        // A name with no value is an
                        // unexpected EOF even at depth zero -- unlike a name
                        // with no NAME, which is a clean EOF.
                        return ChunkFileResult.UnexpectedEndOfFile;

                    case ChunkTokenType.StringTooLong:
                        // The reference's over-long string case.
                        return ChunkFileResult.StringTooLong;

                    default:
                        // THE FALL-THROUGH. The reference inner switch has no default
                        // and the outer `case IDENT: case STRING:` block ends
                        // without a break, so an INTEGER or a TOKENERROR here
                        // drops into `case OPERATOR:` --
                        // which then tests szNAME, not szNext, against "}".
                        //
                        // So `"key" 12` is reported as an unexpected symbol
                        // naming "key", and `mykey` followed by a malformed
                        // number is too. Reproduced deliberately: it is the
                        // diagnostic a stock user sees.
                        break;
                }
            }

            // Reached directly for an operator and by
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
                // The reference's leading over-long string case.
                return ChunkFileResult.StringTooLong;
            }

            // TOKENERROR as the FIRST token matches no case at all in the
            // reference switch, so control reaches the depth test below --
            // which reports EOF or UnexpectedEOF for what is really a
            // malformed number.
        }

        // The reference's depth test at end of input.
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
