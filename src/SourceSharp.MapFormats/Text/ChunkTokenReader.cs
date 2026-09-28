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

    // Reused by every quoted string that is not one unbroken slice of the
    // text (an escape, a '+' join, a chunk boundary); see GetString.
    private StringBuilder? _builder;

    // The recently cut short tokens, one per slot, found by hash; see Slice.
    // Allocated on the first short token, owned by this reader, and dropped
    // with it: nothing outlives one document.
    private string?[]? _recent;

    /// <summary>
    /// How many slots <see cref="Slice"/>'s cache of recent short tokens has.
    /// </summary>
    /// <remarks>
    /// A power of two, so a hash picks a slot with a mask. Large enough that
    /// the few hundred distinct key names of a real map and its common values
    /// ("0", "1", material names, the usual texture axes) mostly keep their
    /// slots while the unique ids and coordinates stream past.
    /// </remarks>
    internal const int RecentTokenSlots = 4096;

    /// <summary>
    /// The longest token <see cref="Slice"/> looks for in its cache; longer
    /// ones are cut out fresh every time.
    /// </summary>
    /// <remarks>
    /// A side's <c>plane</c> value, the longest common token at about fifty
    /// characters, is unique to its side, so hashing it would be pure cost.
    /// Key names and the values that repeat are well under this.
    /// </remarks>
    internal const int RecentTokenMaxLength = 32;

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

        if (OperatorText((char)ch) is string operatorText)
        {
            token = operatorText;
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
            token = Slice(tokenStart, length);
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
        token = Slice(tokenStart, identLength);
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
    /// <remarks>
    /// Almost every string in a VMF is one chunk with no escape and no
    /// <c>'+'</c> join, and its value is then exactly one slice of the text,
    /// which is cut out once. Only a string that is not -- which needs its
    /// pieces joined or an escape rewritten -- goes through the reader's one
    /// reused builder. Building every value in a fresh builder, from a chunk
    /// that was itself a fresh string, cost three allocations and two copies
    /// per value and made the tokenizer one of the largest allocators of a
    /// whole compile.
    /// </remarks>
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
                    : haveSlice ? Slice(sliceStart, sliceLength)
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

    /// <summary>
    /// <c>std::istream::get(char *s, streamsize n, char delim)</c>: extract at
    /// most <paramref name="maximum"/> characters, stopping before
    /// <paramref name="delimiter"/> without consuming it.
    /// </summary>
    /// <returns>Where the extracted characters start in the text, and how many there are.</returns>
    /// <remarks>
    /// One vectorised search over the window rather than a peek and a get per
    /// character: a VMF is mostly quoted strings, so this is the loop the
    /// whole file goes through. The stream semantics are unchanged. The
    /// window is <paramref name="maximum"/> characters or what is left,
    /// whichever is less; the delimiter inside it ends the extraction without
    /// being consumed; and eofbit is set exactly when the text ran out before
    /// either the delimiter or the maximum was reached -- so a window that
    /// ends precisely at the end of the text, full, does NOT set it, as the
    /// stream's extraction stops on the count without looking further.
    /// </remarks>
    private (int Start, int Length) GetUntil(int maximum, char delimiter)
    {
        int start = _position;
        int window = Math.Min(maximum, _text.Length - start);
        int length = _text.AsSpan(start, window).IndexOf(delimiter);

        if (length < 0)
        {
            length = window;

            // Reaching the end sets eofbit, which is exactly what the
            // reference relies on to report an unterminated string.
            if (window < maximum)
            {
                _eof = true;
            }
        }

        _position = start + length;
        return (start, length);
    }

    /// <summary>
    /// The token text <c>[start, start + length)</c> as a string, shared with
    /// an equal token cut recently when there is one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A map is hundreds of thousands of key/value pairs over a few hundred
    /// distinct key names, and most values repeat too: a side's
    /// <c>rotation</c>, <c>lightmapscale</c> and <c>smoothing_groups</c>, its
    /// material, often its texture axes. Cutting a fresh string for each was
    /// most of what parsing a document allocated. A token is a string either
    /// way and strings are immutable, so handing back an equal string cut
    /// earlier changes nothing for anything that reads the text -- only
    /// reference identity could tell, and nothing about a token's meaning
    /// hangs on that.
    /// </para>
    /// <para>
    /// The cache is direct-mapped, not a growing set: a slot per hash, the
    /// newest token wins its slot. Its size is fixed however large or
    /// strange the input, and the unique tokens a map is also full of (ids,
    /// origins) only evict and never accumulate. The hash is the runtime's
    /// randomised string hash, which only decides which slot is tried --
    /// never what the token is -- so it cannot change a parse.
    /// </para>
    /// </remarks>
    private string Slice(int start, int length)
    {
        if (length == 0)
        {
            return string.Empty;
        }

        if (length > RecentTokenMaxLength)
        {
            return _text.Substring(start, length);
        }

        ReadOnlySpan<char> text = _text.AsSpan(start, length);
        _recent ??= new string?[RecentTokenSlots];
        int slot = string.GetHashCode(text) & (RecentTokenSlots - 1);

        string? known = _recent[slot];
        if (known is not null && text.SequenceEqual(known))
        {
            return known;
        }

        string cut = text.ToString();
        _recent[slot] = cut;
        return cut;
    }

    /// <summary>
    /// The one-character string for an operator character, or null when the
    /// character is not one of <see cref="OperatorCharacters"/>.
    /// </summary>
    /// <param name="ch">The character just read.</param>
    /// <returns>The operator's text, or null.</returns>
    /// <remarks>
    /// Literals, so every <c>{</c> and <c>}</c> of a document -- two per chunk
    /// -- is the same interned string rather than a new one-character string
    /// each time. Same set, same answers as testing
    /// <see cref="OperatorCharacters"/>; a fact checks the two agree.
    /// </remarks>
    internal static string? OperatorText(char ch) => ch switch
    {
        '@' => "@",
        ',' => ",",
        '!' => "!",
        '+' => "+",
        '&' => "&",
        '*' => "*",
        '$' => "$",
        '.' => ".",
        '=' => "=",
        ':' => ":",
        '[' => "[",
        ']' => "]",
        '(' => "(",
        ')' => ")",
        '{' => "{",
        '}' => "}",
        '\\' => "\\",
        _ => null,
    };

    private StringBuilder Builder()
    {
        _builder ??= new StringBuilder();
        _builder.Clear();
        return _builder;
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
