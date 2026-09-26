//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Text;
using Xunit;

namespace SourceSharp.Tests.MapFormats.Text;

/// <summary>
/// Facts for the VMF tokenizer, each traced through the reference tokenizer
/// before the port was written.
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
        // Dispatches a '"' to GetString.
        Assert.Equal((ChunkTokenType.String, "hello"), Next("\"hello\""));
    }

    [Fact]
    public void EmptyQuotedStringIsAStringNotAnError()
    {
        // Get extracting nothing sets failbit,
        // which is cleared and treated as an empty string.
        Assert.Equal((ChunkTokenType.String, ""), Next("\"\""));
    }

    [Fact]
    public void IdentifierIsLettersDigitsAndUnderscores()
    {
        Assert.Equal((ChunkTokenType.Identifier, "solid_1"), Next("solid_1"));
    }

    [Fact]
    public void BraceIsAnOperator()
    {
        Assert.Equal((ChunkTokenType.Operator, "{"), Next("{"));
    }

    [Fact]
    public void BackslashIsAnOperatorOutsideAString()
    {
        // Lists '\\' among the operators.
        Assert.Equal((ChunkTokenType.Operator, "\\"), Next("\\"));
    }

    [Fact]
    public void PlusIsNeverReturnedAsAnOperatorBecauseWhitespaceSkippingEatsIt()
    {
        // '+' is in the reference operator list, but
        // SkipWhiteSpace consumes it as the string-combining character
        // before NextToken's switch ever sees it. So a bare '+'
        // followed by an identifier yields the IDENTIFIER.
        Assert.Equal((ChunkTokenType.Identifier, "world"), Next("+world"));
    }

    [Fact]
    public void NulByteIsWhitespace()
    {
        // Lists 0 alongside space, tab and CR.
        Assert.Equal((ChunkTokenType.Identifier, "world"), Next("\0\0world"));
    }

    [Fact]
    public void LoneSlashIsSilentlyEaten()
    {
        // The reference implementation. The 'if (ch == '/')' branch neither starts a
        // comment (peek is not '/') nor reaches the else that would put the
        // character back, so the loop continues having consumed it.
        Assert.Equal((ChunkTokenType.Identifier, "world"), Next("/world"));
    }

    [Fact]
    public void DoubleSlashStartsACommentToEndOfLine()
    {
        Assert.Equal((ChunkTokenType.Identifier, "world"), Next("// a comment\nworld"));
    }

    [Fact]
    public void CommentLongerThanTheStreamBufferLeaksItsTailAsCode()
    {
        // Skips a comment with ignore(1024, '\n'), which
        // stops after 1024 characters whether or not it found the newline. The
        // rest of the comment is then tokenized as code.
        //
        // The arithmetic is exact and worth spelling out: the first '/' was
        // consumed by get, the second is still in the stream when
        // peek sees it, so ignore discards that '/' plus 1023 of
        // the padding characters. One character short of the buffer size is
        // therefore the padding that leaves the next word exposed.
        string comment = "//" + new string('x', ChunkTokenReader.StreamBufferSize - 1) +
                         "leaked\nworld";
        Assert.Equal((ChunkTokenType.Identifier, "leaked"), Next(comment));
    }

    [Fact]
    public void LineCounterStartsAtOne()
    {
        ChunkTokenReader reader = new("world");
        Assert.Equal(1, reader.Line);
    }

    [Fact]
    public void NewlineAdvancesTheLineCounter()
    {
        ChunkTokenReader reader = new("\n\nworld");
        reader.NextToken(out _);
        Assert.Equal(3, reader.Line);
    }

    [Fact]
    public void CarriageReturnInsideAQuotedStringIsStringTooLong()
    {
        // The test is for 0x0d specifically.
        Assert.Equal(ChunkTokenType.StringTooLong, Next("\"broken\r\nstring\"").Type);
    }

    [Fact]
    public void BareNewlineInsideAQuotedStringIsNotAnErrorAndIsKept()
    {
        // The other half of the reference implementation: only CR is checked, so an
        // LF-only file's multi-line string parses and keeps the newline. The
        // same VMF therefore reads differently depending on which platform
        // saved it.
        Assert.Equal((ChunkTokenType.String, "broken\nstring"), Next("\"broken\nstring\""));
    }

    [Fact]
    public void UnterminatedStringAtEndOfFileIsEndOfFileNotStringTooLong()
    {
        // Get reaching the end sets eofbit and
        // GetString returns TOKENEOF, discarding what it had read.
        Assert.Equal(ChunkTokenType.EndOfFile, Next("\"never closed").Type);
    }

    [Fact]
    public void BackslashNIsDecodedToANewline()
    {
        // The ONE escape the reference tokenizer defines.
        Assert.Equal((ChunkTokenType.String, "a\nb"), Next("\"a\\nb\""));
    }

    [Fact]
    public void UnknownEscapeYieldsTheEscapedCharacter()
    {
        // A DOCUMENTED DEVIATION. The reference implementation consumes the
        // backslash and the character after it and advances the destination
        // pointer, but assigns only when the character is 'n' -- so "\t" emits
        // an uninitialised byte. That cannot be reproduced. The structure is:
        // both characters consumed, one produced, defined as the escaped one.
        Assert.Equal((ChunkTokenType.String, "atb"), Next("\"a\\tb\""));
    }

    [Fact]
    public void AdjacentQuotedStringsCombineWhenSeparatedByPlus()
    {
        // Adjacent quoted strings combine when the '+' flag is set.
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
        // Takes 1023 characters at a time and loops
        // when the closing quote has not been reached.
        string body = new('z', ChunkTokenReader.StreamBufferSize * 3);
        Assert.Equal((ChunkTokenType.String, body), Next("\"" + body + "\""));
    }

    [Fact]
    public void DigitsAreAnIntegerToken()
    {
        Assert.Equal((ChunkTokenType.Integer, "512"), Next("512"));
    }

    [Fact]
    public void LeadingMinusIsPartOfAnIntegerToken()
    {
        // Admits '-' as a first character.
        Assert.Equal((ChunkTokenType.Integer, "-512"), Next("-512"));
    }

    [Fact]
    public void SecondMinusSignInsideANumberIsAnError()
    {
        // An error rather than a terminator.
        Assert.Equal(ChunkTokenType.Error, Next("5-3").Type);
    }

    [Fact]
    public void LetterTouchingANumberIsAnError()
    {
        // "No identifier characters are allowed
        // contiguous with numbers."
        Assert.Equal(ChunkTokenType.Error, Next("12abc").Type);
    }

    [Fact]
    public void ADecimalNumberLexesAsThreeTokensBecauseThereIsNoFloatToken()
    {
        // The reference number scanner has no '.' and no exponent, and '.' is an
        // operator in its switch. This is why every float in a VMF lives inside a
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
        // And the early-out.
        ChunkTokenReader reader = new("world");
        reader.Stuff(ChunkTokenType.String, "injected");

        Assert.Equal(ChunkTokenType.String, reader.NextToken(out string token));
        Assert.Equal("injected", token);
    }

    [Fact]
    public void StuffedTokenIsOnlyOneDeep()
    {
        // Overwrites rather than pushing.
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
        // Reads and stuffs.
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
