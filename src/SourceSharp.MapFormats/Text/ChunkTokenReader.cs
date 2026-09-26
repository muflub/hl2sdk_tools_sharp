//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// The VMF tokenizer: reads chunked keyvalue text the way the reference
/// tokenizer's <c>TokenReader</c> does, from memory rather than from a file.
/// </summary>
/// <remarks>
/// <para>
/// The reference class derives privately from a file input stream and its
/// behaviour is written in terms of <c>get</c>, <c>peek</c>, <c>putback</c>,
/// <c>ignore</c> and the stream's <c>eofbit</c>. Every one of those is
/// reproduced here over a character buffer, because several of this format's
/// quirks ARE the stream semantics: the 1024-character cap on skipping a
/// comment, the 1024-character chunking of a quoted string, and end-of-file
/// being noticed only after the character tests.
/// </para>
/// <para>
/// Memory, never a path or a stream: the reader needs <c>putback</c> and a
/// one-character <c>peek</c>, and a file is read once in whole by every caller
/// anyway (<c>CChunkFile::Open</c>). <see cref="VmfDocument"/> is the seam that
/// turns a <see cref="System.IO.Stream"/> into this.
/// </para>
/// <para>
/// Decoding is Latin-1 at the <see cref="System.IO.Stream"/> seam, so every
/// input byte becomes exactly one character and every character becomes exactly
/// that byte again on the way out. The reference opens its file in binary mode
/// and does byte arithmetic; a UTF-8 decode would silently merge byte pairs and
/// break the byte-exact round trip that Phase 1c is gated on.
/// </para>
/// <para>
/// No mutable statics: line number, position and the stuffed token are all
/// instance state, so two readers can run on two threads over two maps.
/// </para>
/// </remarks>
public sealed class ChunkTokenReader
{
    /// <summary>
    /// The size of the reference tokenizer's intermediate string buffer, and so
    /// the number of characters it consumes from a quoted string at a time and
    /// the cap on skipping a <c>//</c> comment.
    /// </summary>
    /// <remarks>
    /// <c>std::istream::get(s, n, delim)</c> extracts at most <c>n - 1</c>
    /// characters, so a string chunk is 1023 characters; <c>ignore(n, delim)</c>
    /// extracts at most <c>n</c>, so a comment scan is 1024. The two differ by
    /// one and both are reproduced.
    /// </remarks>
    public const int StreamBufferSize = 1024;

    /// <summary>
    /// The single characters the reference tokenizer returns as
    /// <see cref="ChunkTokenType.Operator"/>, in the order the reference lists
    /// them.
    /// </summary>
    /// <remarks>
    /// <c>'+'</c> is in the reference list but is unreachable:
    /// <c>SkipWhiteSpace</c> consumes it as the string-combining character
    /// before <c>NextToken</c> ever looks at it. It is kept here so the list
    /// matches the reference list, and a fact pins the unreachability.
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
    /// The current line, counting from 1 as the reference's <c>m_nLine</c>
    /// does.
    /// </summary>
    public int Line { get; private set; } = 1;

    /// <summary>
    /// True once the input has been read past its end, mirroring the stream's
    /// <c>eofbit</c>.
    /// </summary>
    /// <remarks>
    /// Sticky, like the stream bit: it is set by a read that runs off the end
    /// and is never cleared, which is what makes <c>SkipWhiteSpace</c>'s late
    /// EOF test terminate.
    /// </remarks>
    public bool EndOfFile => _eof;

    /// <summary>
    /// Pushes a token back so the next <see cref="NextToken"/> returns it, as
    /// the reference's <c>Stuff</c> does.
    /// </summary>
    /// <param name="type">The token's type.</param>
    /// <param name="token">The token's text.</param>
    /// <exception cref="ArgumentNullException"><paramref name="token"/> is null.</exception>
    /// <remarks>
    /// Exactly one token deep, as in the reference: a second call overwrites the
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
    /// Returns the type and text of the next token without consuming it, as the
    /// reference's <c>PeekTokenType</c> does.
    /// </summary>
    /// <param name="token">Receives the token's text.</param>
    /// <returns>The token's type.</returns>
    /// <remarks>
    /// The reference implements this by reading a token and stuffing it, so a
    /// peek advances the line counter past any whitespace and comments before
    /// the token. That is observable and is reproduced.
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
    /// Reads the next token, following the reference <c>NextToken</c>.
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
    /// Unbounded, where the reference takes a destination size. Truncation is a
    /// property of the caller's buffer in the reference and the two places it
    /// is observable are reproduced where they are observable --
    /// <see cref="ChunkFileReader"/> applies
    /// <see cref="ChunkFileReader.MaxKeyValueLength"/> -- rather than here,
    /// because the tokenizer's own limits differ per call site (8192 in
    /// <c>NextTokenDynamic</c>, 1024 in <c>IgnoreTill</c>).
    /// </para>
    /// </remarks>
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
        if (char.IsAsciiDigit((char)ch) || ch == '-')
        {
            StringBuilder number = new();
            do
            {
                number.Append((char)ch);
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
            token = number.ToString();
            return ChunkTokenType.Integer;
        }

        // An unrecognised character -- ';' or '#', say -- matches neither this
        // loop nor anything above, so it comes back as an EMPTY identifier
        // having been consumed. The reference does not treat that as an error,
        // and neither does this.
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
    /// Skips whitespace and comments, following the reference tokenizer's skip
    /// pass.
    /// </summary>
    /// <returns>
    /// True when a <c>'+'</c> was passed over: the string-combining character,
    /// which makes two adjacent quoted strings one token.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Three quirks live in this function and all three are reproduced:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// A NUL byte is whitespace, so a VMF with embedded NULs parses.
    /// </description></item>
    /// <item><description>
    /// A lone <c>'/'</c> not followed by another is SILENTLY EATEN: the
    /// reference's <c>if (ch == '/')</c> branch neither starts a comment nor
    /// falls into the <c>else</c> that would put the character back, so the
    /// loop simply continues having consumed it.
    /// </description></item>
    /// <item><description>
    /// A comment is skipped with <c>ignore(1024, '\n')</c>, so a comment longer
    /// than 1024 characters is not fully skipped -- the remainder is tokenized
    /// as code -- and the line counter is incremented either way, so a long
    /// comment MISCOUNTS the line.
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

    /// <summary>
    /// Reads the body of a quoted string, following the reference tokenizer's
    /// string pass. The opening quote has been consumed.
    /// </summary>
    private ChunkTokenType GetString(out string token)
    {
        StringBuilder result = new();

        while (true)
        {
            // The reference's get(szBuf, 1024, '"') takes at most 1023
            // characters and stops BEFORE the quote without consuming it.
            string chunk = GetUntil(StreamBufferSize - 1, '"');

            if (_eof)
            {
                // An unterminated string that runs into end of file is
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
                    // CARRIAGE RETURN, not newline. A string broken
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
                    result.Append(chunk[index] == 'n' ? '\n' : chunk[index]);
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

                token = result.ToString();
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
                // Reaching the end sets eofbit, which is exactly what the
                // reference relies on to report an unterminated string.
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
