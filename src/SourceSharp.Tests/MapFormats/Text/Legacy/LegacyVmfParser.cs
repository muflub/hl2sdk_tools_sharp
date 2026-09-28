//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

using SourceSharp.MapFormats.Text;

namespace SourceSharp.Tests.MapFormats.Text.Legacy;

// A frozen copy of the VMF tokenizer, chunk reader and document parse as they
// were before the load-time performance work, kept so facts can run the old
// and the new side by side on the same input and demand the same answer:
// the same tokens, the same tree, the same failure at the same line. The
// copies are verbatim apart from their names and the doc comments; do not
// "fix" them -- their whole value is that they do not change.

/// <summary>The document parse, as it was.</summary>
internal static class LegacyVmfParser
{
    /// <summary>Latin-1 decodes the bytes and parses them, as the old <c>ParseAsync(bytes)</c> did.</summary>
    /// <param name="bytes">The file.</param>
    /// <returns>The document.</returns>
    public static VmfDocument Parse(ReadOnlySpan<byte> bytes) => Parse(Encoding.Latin1.GetString(bytes));

    private const int CancellationCheckInterval = 4096;

    public static VmfDocument Parse(string text, CancellationToken cancellationToken = default)
    {
        LegacyChunkTokenReader tokens = new(text);
        LegacyChunkFileReader reader = new(tokens);
        VmfDocument document = new();

        // The stack of chunks we are inside. Empty means depth zero.
        Stack<VmfChunk> open = new();
        int sinceCheck = 0;

        while (true)
        {
            if (++sinceCheck >= CancellationCheckInterval)
            {
                sinceCheck = 0;
                cancellationToken.ThrowIfCancellationRequested();
            }

            ChunkFileResult result =
                reader.ReadNext(out string name, out string value, out ChunkTermType termType);

            switch (result)
            {
                case ChunkFileResult.Ok when termType == ChunkTermType.Chunk:
                {
                    VmfChunk chunk = new(name);
                    if (open.Count > 0)
                    {
                        open.Peek().Children.Add(chunk);
                    }
                    else
                    {
                        document.Chunks.Add(chunk);
                    }

                    open.Push(chunk);
                    break;
                }

                case ChunkFileResult.Ok:
                {
                    if (open.Count == 0)
                    {
                        // A key at depth zero. The reference loop passes no key
                        // handler at the top level, so
                        // the pair is read and dropped; this port refuses it,
                        // because silently discarding data from a file it was
                        // asked to round-trip is worse than a diagnostic.
                        throw new ChunkFileException(
                            ChunkFileResult.UnexpectedSymbol, tokens.Line, name);
                    }

                    open.Peek().Children.Add(new VmfKey(name, value));
                    break;
                }

                case ChunkFileResult.EndOfChunk:
                {
                    if (open.Count == 0)
                    {
                        // A '}' with nothing open. ReadNext has already taken
                        // the depth negative, which is how
                        // stock then turns a clean EOF into UnexpectedEOF.
                        throw new ChunkFileException(
                            ChunkFileResult.UnexpectedSymbol, tokens.Line, "}");
                    }

                    open.Pop();
                    break;
                }

                case ChunkFileResult.EndOfFile:
                    return document;

                default:
                    throw new ChunkFileException(result, tokens.Line, reader.ErrorToken);
            }
        }
    }
}

internal sealed class LegacyChunkTokenReader
{
    public const int StreamBufferSize = 1024;

    public const string OperatorCharacters = "@,!+&*$.=:[](){}\\";

    private readonly string _text;
    private int _position;
    private bool _eof;
    private bool _stuffed;
    private string _stuffedToken = string.Empty;
    private ChunkTokenType _stuffedType;

    // Reused by every quoted string that is not one unbroken slice of the
    // text (an escape, a '+' join, a chunk boundary); see GetString.
    private StringBuilder? _builder;

    public LegacyChunkTokenReader(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _text = text;
    }

    public static LegacyChunkTokenReader FromBytes(ReadOnlySpan<byte> bytes) =>
        new(Encoding.Latin1.GetString(bytes));

    public int Line { get; private set; } = 1;

    public bool EndOfFile => _eof;

    public void Stuff(ChunkTokenType type, string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        _stuffedType = type;
        _stuffedToken = token;
        _stuffed = true;
    }

    public ChunkTokenType PeekTokenType(out string token)
    {
        if (!_stuffed)
        {
            _stuffedType = NextToken(out _stuffedToken);
            _stuffed = true;
        }

        token = _stuffedToken;
        return _stuffedType;
    }

    public ChunkTokenType NextToken(out string token)
    {
        // A stuffed token short-circuits everything, whitespace skipping
        // included.
        if (_stuffed)
        {
            _stuffed = false;
            token = _stuffedToken;
            return _stuffedType;
        }

        SkipWhiteSpace();

        if (_eof)
        {
            token = string.Empty;
            return ChunkTokenType.EndOfFile;
        }

        int ch = Get();
        if (ch < 0)
        {
            token = string.Empty;
            return ChunkTokenType.EndOfFile;
        }

        if (OperatorCharacters.Contains((char)ch, StringComparison.Ordinal))
        {
            token = ((char)ch).ToString();
            return ChunkTokenType.Operator;
        }

        if (ch == '"')
        {
            return GetString(out token);
        }

        // Note what is NOT here: no '.', no exponent, no leading '+'. "1.5"
        // lexes as INTEGER "1", OPERATOR ".", INTEGER "5" -- which is why every
        // float in a VMF lives inside a quoted string.
        // The characters a number or an identifier is made of are consecutive
        // in the text, so the token is cut out of it rather than built up.
        int tokenStart = _position - 1;
        if (char.IsAsciiDigit((char)ch) || ch == '-')
        {
            int length = 0;
            do
            {
                length++;
                ch = Get();

                // A second minus sign anywhere in the number is an error, not a
                // terminator.
                if (ch == '-')
                {
                    token = string.Empty;
                    return ChunkTokenType.Error;
                }
            }
            while (ch >= 0 && char.IsAsciiDigit((char)ch));

            // "12abc" is an error rather than two tokens.
            if (ch >= 0 && (char.IsAsciiLetter((char)ch) || ch == '_'))
            {
                token = string.Empty;
                return ChunkTokenType.Error;
            }

            PutBack(ch);
            token = _text.Substring(tokenStart, length);
            return ChunkTokenType.Integer;
        }

        // An unrecognised character -- ';' or '#', say -- matches neither this
        // loop nor anything above, so it comes back as an EMPTY identifier
        // having been consumed. The reference does not treat that as an error,
        // and neither does this.
        int identLength = 0;
        while (ch >= 0 && (char.IsAsciiLetterOrDigit((char)ch) || ch == '_'))
        {
            identLength++;
            ch = Get();
        }

        PutBack(ch);
        token = _text.Substring(tokenStart, identLength);
        return ChunkTokenType.Identifier;
    }

    public bool SkipWhiteSpace()
    {
        bool combineStrings = false;

        while (true)
        {
            int ch = Get();

            if (ch == ' ' || ch == '\t' || ch == '\r' || ch == 0)
            {
                continue;
            }

            if (ch == '+')
            {
                combineStrings = true;
                continue;
            }

            if (ch == '\n')
            {
                Line++;
                continue;
            }

            // Tested AFTER the character cases, which is why the EOF sentinel
            // has to fail all of them first.
            if (_eof)
            {
                return combineStrings;
            }

            if (ch == '/')
            {
                if (Peek() == '/')
                {
                    Ignore(StreamBufferSize, '\n');
                    Line++;
                }

                // No else, and no putback: see the remarks.
            }
            else
            {
                PutBack(ch);
                return combineStrings;
            }
        }
    }

    private ChunkTokenType GetString(out string token)
    {
        // The value so far is either the slice [sliceStart, +sliceLength) of
        // the text (while `result` is null) or the builder's contents.
        StringBuilder? result = null;
        int sliceStart = 0;
        int sliceLength = 0;
        bool haveSlice = false;

        while (true)
        {
            // The reference's get(szBuf, 1024, '"') takes at most 1023
            // characters and stops BEFORE the quote without consuming it.
            (int chunkStart, int chunkLength) = GetUntil(StreamBufferSize - 1, '"');
            ReadOnlySpan<char> chunk = _text.AsSpan(chunkStart, chunkLength);

            if (_eof)
            {
                // An unterminated string that runs into end of file is
                // reported as EOF, NOT as StringTooLong -- the two malformed
                // cases have different codes and callers act on the difference.
                token = string.Empty;
                return ChunkTokenType.EndOfFile;
            }

            if (result is null && !haveSlice && chunk.IndexOfAny('\r', '\\') < 0)
            {
                // Nothing to rewrite: the chunk is the value so far.
                haveSlice = true;
                sliceStart = chunkStart;
                sliceLength = chunkLength;
                chunk = [];
            }
            else if (result is null)
            {
                result = Builder();
                if (haveSlice)
                {
                    result.Append(_text, sliceStart, sliceLength);
                    haveSlice = false;
                }
            }

            int index = 0;
            while (index < chunk.Length)
            {
                char c = chunk[index];

                if (c == '\r')
                {
                    // CARRIAGE RETURN, not newline. A string broken
                    // across a line in a CRLF file is caught here; the same
                    // string in an LF-only file is NOT caught and the newline
                    // becomes part of the value.
                    token = result!.ToString();
                    return ChunkTokenType.StringTooLong;
                }

                if (c != '\\')
                {
                    result!.Append(c);
                    index++;
                    continue;
                }

                // The escape handling, and the one place this reader
                // deliberately DEFINES what the reference leaves indeterminate.
                //
                // The reference advances past the backslash, assigns the
                // destination byte only when the next character is 'n', and
                // advances the destination pointer regardless. So "\t" writes
                // whatever happened to be in the caller's uninitialised buffer,
                // and a backslash as the last character of a 1023-character
                // chunk reads past the buffer's terminator entirely.
                //
                // Reproducing undefined behaviour is not possible and would not
                // be worth it: Hammer writes forward slashes in the material
                // and model paths that are the only place a backslash could
                // plausibly appear, so no VMF in the corpus exercises it. What
                // is reproduced is the STRUCTURE -- backslash and the character
                // after it are both consumed, and one character is produced --
                // with the produced character defined as the escaped character
                // itself for anything but 'n'.
                index++;
                if (index < chunk.Length)
                {
                    result!.Append(chunk[index] == 'n' ? '\n' : chunk[index]);
                    index++;
                }
            }

            // Closing quote?
            if (Peek() == '"')
            {
                Get();

                bool combineStrings = SkipWhiteSpace();

                // "abc" + "def" is one token, "abcdef".
                if (combineStrings && Peek() == '"')
                {
                    Get();
                    continue;
                }

                token = result is not null ? result.ToString()
                    : haveSlice ? _text.Substring(sliceStart, sliceLength)
                    : string.Empty;
                return ChunkTokenType.String;
            }

            // Not at the quote, so the chunk filled up: go round for the next
            // 1023 characters. The reference reaches the same place when its
            // DESTINATION ran out instead, which is where its StringTooLong for
            // an over-long string comes from; this reader has no destination
            // limit at the tokenizer, so a long string simply keeps going and
            // the limit is applied by ChunkFileReader instead.
        }
    }

    private int Get()
    {
        if (_position >= _text.Length)
        {
            _eof = true;
            return -1;
        }

        return _text[_position++];
    }

    private int Peek() => _position >= _text.Length ? -1 : _text[_position];

    private void PutBack(int ch)
    {
        // Putting back the EOF sentinel is a no-op, as it is for the stream:
        // the position never moved.
        if (ch >= 0 && _position > 0)
        {
            _position--;
        }
    }

    private (int Start, int Length) GetUntil(int maximum, char delimiter)
    {
        int start = _position;
        int length = 0;

        while (length < maximum)
        {
            int next = Peek();
            if (next < 0)
            {
                // Reaching the end sets eofbit, which is exactly what the
                // reference relies on to report an unterminated string.
                _eof = true;
                break;
            }

            if (next == delimiter)
            {
                break;
            }

            Get();
            length++;
        }

        return (start, length);
    }

    private StringBuilder Builder()
    {
        _builder ??= new StringBuilder();
        _builder.Clear();
        return _builder;
    }

    private void Ignore(int maximum, char delimiter)
    {
        for (int i = 0; i < maximum; i++)
        {
            int ch = Get();
            if (ch < 0 || ch == delimiter)
            {
                return;
            }
        }
    }
}

internal sealed class LegacyChunkFileReader
{
    public const int MaxKeyValueLength = 1024;

    public const int MaxIndentDepth = 80;

    private readonly LegacyChunkTokenReader _tokens;

    public LegacyChunkFileReader(LegacyChunkTokenReader tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        _tokens = tokens;
    }

    public LegacyChunkTokenReader Tokens => _tokens;

    public int CurrentDepth { get; private set; }

    public string ErrorToken { get; private set; } = string.Empty;

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

    private static string Truncate(string text, int limit) =>
        text.Length <= limit - 1 ? text : text[..(limit - 1)];
}
