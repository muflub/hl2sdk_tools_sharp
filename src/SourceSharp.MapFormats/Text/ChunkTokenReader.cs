using System.Text;

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// The VMF tokenizer: a port of <c>TokenReader</c>
/// (<c>src/tier1/tokenreader.cpp</c>, <c>src/public/tier1/tokenreader.h</c>),
/// reading from memory rather than from a file.
/// </summary>
/// <remarks>
/// <para>
/// The C++ class derives privately from <c>std::ifstream</c> and its behaviour
/// is written in terms of <c>get</c>, <c>peek</c>, <c>putback</c>, <c>ignore</c>
/// and the stream's <c>eofbit</c>. Every one of those is reproduced here over a
/// character buffer, because several of this format's quirks ARE the stream
/// semantics: the 1024-character cap on skipping a comment
/// (<c>tokenreader.cpp:466</c>), the 1024-character chunking of a quoted string
/// (<c>tokenreader.cpp:92</c>), and end-of-file being noticed only after the
/// character tests (<c>tokenreader.cpp:454</c>).
/// </para>
/// <para>
/// Memory, never a path or a stream: the reader needs <c>putback</c> and a
/// one-character <c>peek</c>, and a file is read once in whole by every caller
/// in the tree anyway (<c>CChunkFile::Open</c>,
/// <c>chunkfile.cpp:401-433</c>). <see cref="VmfDocument"/> is the seam that
/// turns a <see cref="System.IO.Stream"/> into this.
/// </para>
/// <para>
/// Decoding is Latin-1 at the <see cref="System.IO.Stream"/> seam, so every
/// input byte becomes exactly one character and every character becomes exactly
/// that byte again on the way out. The C++ opens the file
/// <c>std::ios::binary</c> (<c>tokenreader.cpp:35</c>) and does byte
/// arithmetic; a UTF-8 decode would silently merge byte pairs and break the
/// byte-exact round trip that Phase 1c is gated on.
/// </para>
/// <para>
/// No mutable statics: line number, position and the stuffed token are all
/// instance state, so two readers can run on two threads over two maps.
/// </para>
/// </remarks>
public sealed class ChunkTokenReader
{
    /// <summary>
    /// The size of the C++ tokenizer's intermediate string buffer, and so the
    /// number of characters it consumes from a quoted string at a time
    /// (<c>tokenreader.cpp:81,92</c>) and the cap on skipping a
    /// <c>//</c> comment (<c>tokenreader.cpp:466</c>).
    /// </summary>
    /// <remarks>
    /// <c>std::istream::get(s, n, delim)</c> extracts at most <c>n - 1</c>
    /// characters, so a string chunk is 1023 characters; <c>ignore(n, delim)</c>
    /// extracts at most <c>n</c>, so a comment scan is 1024. The two differ by
    /// one and both are reproduced.
    /// </remarks>
    public const int StreamBufferSize = 1024;

    /// <summary>
    /// The single characters the C++ returns as
    /// <see cref="ChunkTokenType.Operator"/>, in the order they are listed at
    /// <c>src/tier1/tokenreader.cpp:252-268</c>.
    /// </summary>
    /// <remarks>
    /// <c>'+'</c> is in the C++ list but is unreachable: <c>SkipWhiteSpace</c>
    /// consumes it as the string-combining character before <c>NextToken</c>
    /// ever looks at it (<c>tokenreader.cpp:442-446</c>). It is kept here so
    /// the list matches its original, and a fact pins the unreachability.
    /// </remarks>
    public const string OperatorCharacters = "@,!+&*$.=:[](){}\\";

    private readonly string _text;
    private int _position;
    private bool _eof;
    private bool _stuffed;
    private string _stuffedToken = string.Empty;
    private ChunkTokenType _stuffedType;

    /// <summary>Creates a reader over already-decoded text.</summary>
    /// <param name="text">The whole file's text.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public ChunkTokenReader(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _text = text;
    }

    /// <summary>
    /// Creates a reader over file bytes, decoded as Latin-1.
    /// </summary>
    /// <param name="bytes">The whole file's bytes.</param>
    /// <returns>A reader positioned at the first character.</returns>
    /// <remarks>
    /// Latin-1 and not UTF-8, for the reason given on the class: one byte in,
    /// one character out, one byte back.
    /// </remarks>
    public static ChunkTokenReader FromBytes(ReadOnlySpan<byte> bytes) =>
        new(Encoding.Latin1.GetString(bytes));

    /// <summary>
    /// The current line, counting from 1 as <c>m_nLine</c> does
    /// (<c>tokenreader.cpp:22</c>).
    /// </summary>
    public int Line { get; private set; } = 1;

    /// <summary>
    /// True once the input has been read past its end, mirroring the stream's
    /// <c>eofbit</c>.
    /// </summary>
    /// <remarks>
    /// Sticky, like the stream bit: it is set by a read that runs off the end
    /// and is never cleared, which is what makes
    /// <c>SkipWhiteSpace</c>'s late test at <c>tokenreader.cpp:454</c>
    /// terminate.
    /// </remarks>
    public bool EndOfFile => _eof;

    /// <summary>
    /// Pushes a token back so the next <see cref="NextToken"/> returns it, as
    /// <c>Stuff</c> does (<c>tokenreader.cpp:376-381</c>).
    /// </summary>
    /// <param name="type">The token's type.</param>
    /// <param name="token">The token's text.</param>
    /// <exception cref="ArgumentNullException"><paramref name="token"/> is null.</exception>
    /// <remarks>
    /// Exactly one token deep, as in the C++: a second call overwrites the
    /// first rather than making a stack.
    /// </remarks>
    public void Stuff(ChunkTokenType type, string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        _stuffedType = type;
        _stuffedToken = token;
        _stuffed = true;
    }

    /// <summary>
    /// Returns the type and text of the next token without consuming it, as
    /// <c>PeekTokenType</c> does (<c>tokenreader.cpp:406-420</c>).
    /// </summary>
    /// <param name="token">Receives the token's text.</param>
    /// <returns>The token's type.</returns>
    /// <remarks>
    /// The C++ implements this by reading a token and stuffing it, so a peek
    /// advances the line counter past any whitespace and comments before the
    /// token. That is observable and is reproduced.
    /// </remarks>
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

    /// <summary>
    /// Reads the next token, a port of <c>NextToken</c>
    /// (<c>tokenreader.cpp:214-341</c>).
    /// </summary>
    /// <param name="token">
    /// Receives the token's text: the operator character, the digits, the
    /// identifier, or the decoded contents of the quoted string.
    /// </param>
    /// <returns>The token's type.</returns>
    /// <remarks>
    /// <para>
    /// Not <c>…Async</c> and deliberately so: it is one step over memory that
    /// already exists, does no IO, and cannot be cancelled usefully. The
    /// long-running operation is parsing a whole document, and THAT is
    /// <see cref="VmfDocument.ReadAsync"/>, which is async and takes a token.
    /// </para>
    /// <para>
    /// Unbounded, where the C++ takes a destination size. Truncation is a
    /// property of the caller's buffer in the C++ and the two places it is
    /// observable are reproduced where they are observable --
    /// <see cref="ChunkFileReader"/> applies
    /// <see cref="ChunkFileReader.MaxKeyValueLength"/> -- rather than here,
    /// because the tokenizer's own limits differ per call site (8192 in
    /// <c>NextTokenDynamic</c>, <c>tokenreader.cpp:197</c>; 1024 in
    /// <c>IgnoreTill</c>, <c>:352</c>).
    /// </para>
    /// </remarks>
    public ChunkTokenType NextToken(out string token)
    {
        // tokenreader.cpp:226-231 -- a stuffed token short-circuits everything,
        // whitespace skipping included.
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

        // tokenreader.cpp:250-274.
        if (OperatorCharacters.Contains((char)ch, StringComparison.Ordinal))
        {
            token = ((char)ch).ToString();
            return ChunkTokenType.Operator;
        }

        // tokenreader.cpp:279-282.
        if (ch == '"')
        {
            return GetString(out token);
        }

        // tokenreader.cpp:287-318. Note what is NOT here: no '.', no exponent,
        // no leading '+'. "1.5" lexes as INTEGER "1", OPERATOR ".",
        // INTEGER "5" -- which is why every float in a VMF lives inside a
        // quoted string.
        if (char.IsAsciiDigit((char)ch) || ch == '-')
        {
            StringBuilder number = new();
            do
            {
                number.Append((char)ch);
                ch = Get();

                // A second minus sign anywhere in the number is an error, not a
                // terminator (tokenreader.cpp:298-301).
                if (ch == '-')
                {
                    token = string.Empty;
                    return ChunkTokenType.Error;
                }
            }
            while (ch >= 0 && char.IsAsciiDigit((char)ch));

            // tokenreader.cpp:307-310 -- "12abc" is an error rather than two
            // tokens.
            if (ch >= 0 && (char.IsAsciiLetter((char)ch) || ch == '_'))
            {
                token = string.Empty;
                return ChunkTokenType.Error;
            }

            PutBack(ch);
            token = number.ToString();
            return ChunkTokenType.Integer;
        }

        // tokenreader.cpp:324-340. An unrecognised character -- ';' or '#',
        // say -- matches neither this loop nor anything above, so it comes back
        // as an EMPTY identifier having been consumed. That is not an error in
        // the C++ and is not one here.
        StringBuilder ident = new();
        while (ch >= 0 && (char.IsAsciiLetterOrDigit((char)ch) || ch == '_'))
        {
            ident.Append((char)ch);
            ch = Get();
        }

        PutBack(ch);
        token = ident.ToString();
        return ChunkTokenType.Identifier;
    }

    /// <summary>
    /// Skips whitespace and comments, a port of <c>SkipWhiteSpace</c>
    /// (<c>tokenreader.cpp:429-479</c>).
    /// </summary>
    /// <returns>
    /// True when a <c>'+'</c> was passed over: the string-combining character,
    /// which makes two adjacent quoted strings one token.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Three quirks live in this function and all three are ported:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// A NUL byte is whitespace (<c>tokenreader.cpp:437</c>), so a VMF with
    /// embedded NULs parses.
    /// </description></item>
    /// <item><description>
    /// A lone <c>'/'</c> not followed by another is SILENTLY EATEN: the
    /// <c>if (ch == '/')</c> branch at <c>:462</c> neither starts a comment nor
    /// falls into the <c>else</c> that would put the character back, so the
    /// loop simply continues having consumed it.
    /// </description></item>
    /// <item><description>
    /// A comment is skipped with <c>ignore(1024, '\n')</c> at <c>:466</c>, so a
    /// comment longer than 1024 characters is not fully skipped -- the
    /// remainder is tokenized as code -- and the line counter is incremented at
    /// <c>:467</c> either way, so a long comment MISCOUNTS the line.
    /// </description></item>
    /// </list>
    /// </remarks>
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

            // tokenreader.cpp:454 -- tested AFTER the character cases, which is
            // why the EOF sentinel has to fail all of them first.
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

    /// <summary>
    /// Reads the body of a quoted string, a port of <c>GetString</c>
    /// (<c>tokenreader.cpp:74-186</c>). The opening quote has been consumed.
    /// </summary>
    private ChunkTokenType GetString(out string token)
    {
        StringBuilder result = new();

        while (true)
        {
            // tokenreader.cpp:92 -- get(szBuf, 1024, '"') takes at most 1023
            // characters and stops BEFORE the quote without consuming it.
            string chunk = GetUntil(StreamBufferSize - 1, '"');

            if (_eof)
            {
                // :93-96. An unterminated string that runs into end of file is
                // reported as EOF, NOT as StringTooLong -- the two malformed
                // cases have different codes and callers act on the difference.
                token = string.Empty;
                return ChunkTokenType.EndOfFile;
            }

            int index = 0;
            while (index < chunk.Length)
            {
                char c = chunk[index];

                if (c == '\r')
                {
                    // :110-117. CARRIAGE RETURN, not newline. A string broken
                    // across a line in a CRLF file is caught here; the same
                    // string in an LF-only file is NOT caught and the newline
                    // becomes part of the value.
                    token = result.ToString();
                    return ChunkTokenType.StringTooLong;
                }

                if (c != '\\')
                {
                    result.Append(c);
                    index++;
                    continue;
                }

                // :123-136. The escape handling, and the one place this port
                // deliberately DEFINES what the C++ leaves indeterminate.
                //
                // The C++ advances past the backslash, assigns the destination
                // byte only when the next character is 'n', and advances the
                // destination pointer regardless. So "\t" writes whatever
                // happened to be in the caller's uninitialised buffer, and a
                // backslash as the last character of a 1023-character chunk
                // reads past the buffer's terminator entirely.
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
                    result.Append(chunk[index] == 'n' ? '\n' : chunk[index]);
                    index++;
                }
            }

            // :156-184. Closing quote?
            if (Peek() == '"')
            {
                Get();

                bool combineStrings = SkipWhiteSpace();

                // :169-175 -- "abc" + "def" is one token, "abcdef".
                if (combineStrings && Peek() == '"')
                {
                    Get();
                    continue;
                }

                token = result.ToString();
                return ChunkTokenType.String;
            }

            // Not at the quote, so the chunk filled up: go round for the next
            // 1023 characters. The C++ reaches the same place from
            // :142-151 when the DESTINATION ran out instead, which is where its
            // StringTooLong for an over-long string comes from; this port has no
            // destination limit at the tokenizer, so a long string simply keeps
            // going and the limit is applied by ChunkFileReader instead.
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

    /// <summary>
    /// <c>std::istream::get(char *s, streamsize n, char delim)</c>: extract at
    /// most <paramref name="maximum"/> characters, stopping before
    /// <paramref name="delimiter"/> without consuming it.
    /// </summary>
    private string GetUntil(int maximum, char delimiter)
    {
        StringBuilder chunk = new();

        while (chunk.Length < maximum)
        {
            int next = Peek();
            if (next < 0)
            {
                // Reaching the end sets eofbit, which is exactly what
                // tokenreader.cpp:93 tests.
                _eof = true;
                break;
            }

            if (next == delimiter)
            {
                break;
            }

            chunk.Append((char)Get());
        }

        return chunk.ToString();
    }

    /// <summary>
    /// <c>std::istream::ignore(streamsize n, int delim)</c>: discard at most
    /// <paramref name="maximum"/> characters, stopping after
    /// <paramref name="delimiter"/> is discarded.
    /// </summary>
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
