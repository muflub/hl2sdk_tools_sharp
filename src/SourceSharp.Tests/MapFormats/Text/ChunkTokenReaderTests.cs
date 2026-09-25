using SourceSharp.MapFormats.Text;
using Xunit;

namespace SourceSharp.Tests.MapFormats.Text;

/// <summary>
/// Facts for the VMF tokenizer, each derived from a line of
/// <c>src/tier1/tokenreader.cpp</c> before the port was written.
/// </summary>
public class ChunkTokenReaderTests
{
    private static (ChunkTokenType Type, string Text) Next(string input)
    {
        ChunkTokenReader reader = new(input);
        ChunkTokenType type = reader.NextToken(out string token);
        return (type, token);
    }

    [Fact]
    public void QuotedStringYieldsItsContents()
    {
        // tokenreader.cpp:279-282 dispatches a '"' to GetString.
        Assert.Equal((ChunkTokenType.String, "hello"), Next("\"hello\""));
    }

    [Fact]
    public void EmptyQuotedStringIsAStringNotAnError()
    {
        // tokenreader.cpp:98-102 -- get() extracting nothing sets failbit,
        // which is cleared and treated as an empty string.
        Assert.Equal((ChunkTokenType.String, ""), Next("\"\""));
    }

    [Fact]
    public void IdentifierIsLettersDigitsAndUnderscores()
    {
        // tokenreader.cpp:324.
        Assert.Equal((ChunkTokenType.Identifier, "solid_1"), Next("solid_1"));
    }

    [Fact]
    public void BraceIsAnOperator()
    {
        // tokenreader.cpp:266.
        Assert.Equal((ChunkTokenType.Operator, "{"), Next("{"));
    }

    [Fact]
    public void BackslashIsAnOperatorOutsideAString()
    {
        // tokenreader.cpp:268 lists '\\' among the operators.
        Assert.Equal((ChunkTokenType.Operator, "\\"), Next("\\"));
    }

    [Fact]
    public void PlusIsNeverReturnedAsAnOperatorBecauseWhitespaceSkippingEatsIt()
    {
        // '+' is in the operator list at tokenreader.cpp:255, but
        // SkipWhiteSpace consumes it as the string-combining character at
        // :442-446 before NextToken's switch ever sees it. So a bare '+'
        // followed by an identifier yields the IDENTIFIER.
        Assert.Equal((ChunkTokenType.Identifier, "world"), Next("+world"));
    }

    [Fact]
    public void NulByteIsWhitespace()
    {
        // tokenreader.cpp:437 lists 0 alongside space, tab and CR.
        Assert.Equal((ChunkTokenType.Identifier, "world"), Next("\0\0world"));
    }

    [Fact]
    public void LoneSlashIsSilentlyEaten()
    {
        // tokenreader.cpp:462-469. The 'if (ch == '/')' branch neither starts a
        // comment (peek is not '/') nor reaches the else that would put the
        // character back, so the loop continues having consumed it.
        Assert.Equal((ChunkTokenType.Identifier, "world"), Next("/world"));
    }

    [Fact]
    public void DoubleSlashStartsACommentToEndOfLine()
    {
        // tokenreader.cpp:464-466.
        Assert.Equal((ChunkTokenType.Identifier, "world"), Next("// a comment\nworld"));
    }

    [Fact]
    public void CommentLongerThanTheStreamBufferLeaksItsTailAsCode()
    {
        // tokenreader.cpp:466 skips a comment with ignore(1024, '\n'), which
        // stops after 1024 characters whether or not it found the newline. The
        // rest of the comment is then tokenized as code.
        //
        // The arithmetic is exact and worth spelling out: the first '/' was
        // consumed by get() at :435, the second is still in the stream when
        // peek() sees it at :464, so ignore() discards that '/' plus 1023 of
        // the padding characters. One character short of the buffer size is
        // therefore the padding that leaves the next word exposed.
        string comment = "//" + new string('x', ChunkTokenReader.StreamBufferSize - 1) +
                         "leaked\nworld";
        Assert.Equal((ChunkTokenType.Identifier, "leaked"), Next(comment));
    }

    [Fact]
    public void LineCounterStartsAtOne()
    {
        // tokenreader.cpp:22.
        ChunkTokenReader reader = new("world");
        Assert.Equal(1, reader.Line);
    }

    [Fact]
    public void NewlineAdvancesTheLineCounter()
    {
        // tokenreader.cpp:448-452.
        ChunkTokenReader reader = new("\n\nworld");
        reader.NextToken(out _);
        Assert.Equal(3, reader.Line);
    }

    [Fact]
    public void CarriageReturnInsideAQuotedStringIsStringTooLong()
    {
        // tokenreader.cpp:110-117 -- the test is for 0x0d specifically.
        Assert.Equal(ChunkTokenType.StringTooLong, Next("\"broken\r\nstring\"").Type);
    }

    [Fact]
    public void BareNewlineInsideAQuotedStringIsNotAnErrorAndIsKept()
    {
        // The other half of tokenreader.cpp:110-117: only CR is checked, so an
        // LF-only file's multi-line string parses and keeps the newline. The
        // same VMF therefore reads differently depending on which platform
        // saved it.
        Assert.Equal((ChunkTokenType.String, "broken\nstring"), Next("\"broken\nstring\""));
    }

    [Fact]
    public void UnterminatedStringAtEndOfFileIsEndOfFileNotStringTooLong()
    {
        // tokenreader.cpp:93-96 -- get() reaching the end sets eofbit and
        // GetString returns TOKENEOF, discarding what it had read.
        Assert.Equal(ChunkTokenType.EndOfFile, Next("\"never closed").Type);
    }

    [Fact]
    public void BackslashNIsDecodedToANewline()
    {
        // tokenreader.cpp:130-133 -- the ONE escape the C++ defines.
        Assert.Equal((ChunkTokenType.String, "a\nb"), Next("\"a\\nb\""));
    }

    [Fact]
    public void UnknownEscapeYieldsTheEscapedCharacter()
    {
        // A DOCUMENTED DEVIATION. tokenreader.cpp:123-136 consumes the
        // backslash and the character after it and advances the destination
        // pointer, but assigns only when the character is 'n' -- so "\t" emits
        // an uninitialised byte. That cannot be reproduced. The structure is:
        // both characters consumed, one produced, defined as the escaped one.
        Assert.Equal((ChunkTokenType.String, "atb"), Next("\"a\\tb\""));
    }

    [Fact]
    public void AdjacentQuotedStringsCombineWhenSeparatedByPlus()
    {
        // tokenreader.cpp:163-175, with the '+' flag set at :442-446.
        Assert.Equal((ChunkTokenType.String, "abcdef"), Next("\"abc\" + \"def\""));
    }

    [Fact]
    public void AdjacentQuotedStringsDoNotCombineWithoutThePlus()
    {
        // The same lines: without the combine flag the first string ends.
        Assert.Equal((ChunkTokenType.String, "abc"), Next("\"abc\" \"def\""));
    }

    [Fact]
    public void StringLongerThanOneStreamChunkIsReadWhole()
    {
        // tokenreader.cpp:92 takes 1023 characters at a time and loops at :142
        // when the closing quote has not been reached.
        string body = new('z', ChunkTokenReader.StreamBufferSize * 3);
        Assert.Equal((ChunkTokenType.String, body), Next("\"" + body + "\""));
    }

    [Fact]
    public void DigitsAreAnIntegerToken()
    {
        // tokenreader.cpp:287-318.
        Assert.Equal((ChunkTokenType.Integer, "512"), Next("512"));
    }

    [Fact]
    public void LeadingMinusIsPartOfAnIntegerToken()
    {
        // tokenreader.cpp:287 admits '-' as a first character.
        Assert.Equal((ChunkTokenType.Integer, "-512"), Next("-512"));
    }

    [Fact]
    public void SecondMinusSignInsideANumberIsAnError()
    {
        // tokenreader.cpp:298-301 -- an error rather than a terminator.
        Assert.Equal(ChunkTokenType.Error, Next("5-3").Type);
    }

    [Fact]
    public void LetterTouchingANumberIsAnError()
    {
        // tokenreader.cpp:307-310 -- "No identifier characters are allowed
        // contiguous with numbers."
        Assert.Equal(ChunkTokenType.Error, Next("12abc").Type);
    }

    [Fact]
    public void ADecimalNumberLexesAsThreeTokensBecauseThereIsNoFloatToken()
    {
        // tokenreader.cpp:287-318 has no '.' and no exponent, and '.' is an
        // operator at :259. This is why every float in a VMF lives inside a
        // quoted string.
        ChunkTokenReader reader = new("1.5");

        Assert.Equal(ChunkTokenType.Integer, reader.NextToken(out string first));
        Assert.Equal("1", first);
        Assert.Equal(ChunkTokenType.Operator, reader.NextToken(out string second));
        Assert.Equal(".", second);
        Assert.Equal(ChunkTokenType.Integer, reader.NextToken(out string third));
        Assert.Equal("5", third);
    }

    [Fact]
    public void StuffedTokenComesBackFromTheNextRead()
    {
        // tokenreader.cpp:376-381 and the early-out at :226-231.
        ChunkTokenReader reader = new("world");
        reader.Stuff(ChunkTokenType.String, "injected");

        Assert.Equal(ChunkTokenType.String, reader.NextToken(out string token));
        Assert.Equal("injected", token);
    }

    [Fact]
    public void StuffedTokenIsOnlyOneDeep()
    {
        // tokenreader.cpp:378-380 overwrites rather than pushing.
        ChunkTokenReader reader = new("world");
        reader.Stuff(ChunkTokenType.String, "first");
        reader.Stuff(ChunkTokenType.String, "second");

        reader.NextToken(out string token);
        Assert.Equal("second", token);
        Assert.Equal(ChunkTokenType.Identifier, reader.NextToken(out _));
    }

    [Fact]
    public void PeekDoesNotConsume()
    {
        // tokenreader.cpp:406-420 reads and stuffs.
        ChunkTokenReader reader = new("world");

        Assert.Equal(ChunkTokenType.Identifier, reader.PeekTokenType(out string peeked));
        Assert.Equal("world", peeked);
        Assert.Equal(ChunkTokenType.Identifier, reader.NextToken(out string read));
        Assert.Equal("world", read);
    }

    [Fact]
    public void EmptyInputIsEndOfFile()
    {
        Assert.Equal(ChunkTokenType.EndOfFile, Next(string.Empty).Type);
    }

    [Fact]
    public void FromBytesDecodesEachByteAsOneCharacter()
    {
        // Latin-1 at the byte seam, so a high-bit byte in a texture name
        // survives a round trip. UTF-8 would merge it with its neighbour.
        ChunkTokenReader reader = ChunkTokenReader.FromBytes([(byte)'"', 0xE9, (byte)'"']);

        Assert.Equal(ChunkTokenType.String, reader.NextToken(out string token));
        Assert.Equal("é", token);
    }
}
