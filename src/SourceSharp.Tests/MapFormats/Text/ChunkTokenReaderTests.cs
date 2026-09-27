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
    public void AnEscapeInALaterChunkIsDecodedAndTheFirstChunkKept()
    {
        // The first chunk is plain (taken as a slice of the text); the second
        // has an escape, so the value moves into the builder with the slice
        // in front of it.
        string head = new('a', ChunkTokenReader.StreamBufferSize - 1);
        Assert.Equal((ChunkTokenType.String, head + "b\nc"), Next("\"" + head + "b\\nc\""));
    }

    [Fact]
    public void AnEscapeSplitAcrossAChunkBoundaryMatchesTheChunkedReading()
    {
        // The backslash is the last character of the first 1023: the chunk
        // ends there, the backslash has nothing after it in its chunk and is
        // dropped, and the 'n' starts the next chunk as an ordinary 'n'.
        string head = new('a', ChunkTokenReader.StreamBufferSize - 2);
        Assert.Equal((ChunkTokenType.String, head + "nz"), Next("\"" + head + "\\nz\""));
    }

    [Fact]
    public void ACombinedStringJoinsAPlainPieceToAnEscapedOne()
    {
        Assert.Equal((ChunkTokenType.String, "abcd\ne"), Next("\"abc\" + \"d\\ne\""));
        Assert.Equal((ChunkTokenType.String, "a\nbcd"), Next("\"a\\nb\" + \"cd\""));
        Assert.Equal((ChunkTokenType.String, "abc"), Next("\"a\" + \"b\" + \"c\""));
    }

    [Fact]
    public void ACarriageReturnAfterACombinedPieceReturnsEverythingBeforeIt()
    {
        Assert.Equal((ChunkTokenType.StringTooLong, "abcde"), Next("\"abc\" + \"de\rf\""));
        Assert.Equal((ChunkTokenType.StringTooLong, "broken"), Next("\"broken\r\nstring\""));
    }

    [Fact]
    public void TheReusedBuilderCarriesNothingFromOneStringToTheNext()
    {
        ChunkTokenReader reader = new("\"a\\nb\" \"plain\" \"c\\td\" \"x\" + \"y\" \"\" \"q\\\\\"");
        string[] expected = ["a\nb", "plain", "ctd", "xy", "", "q\\"];
        foreach (string value in expected)
        {
            Assert.Equal(ChunkTokenType.String, reader.NextToken(out string token));
            Assert.Equal(value, token);
        }

        Assert.Equal(ChunkTokenType.EndOfFile, reader.NextToken(out _));
    }

    [Fact]
    public void NumbersAndIdentifiersAreCutFromTheTextAtEveryBoundary()
    {
        ChunkTokenReader reader = new("12 -7 abc_1{-;x9");
        (ChunkTokenType, string)[] expected =
        [
            (ChunkTokenType.Integer, "12"),
            (ChunkTokenType.Integer, "-7"),
            (ChunkTokenType.Identifier, "abc_1"),
            (ChunkTokenType.Operator, "{"),
            (ChunkTokenType.Integer, "-"),
        ];
        foreach ((ChunkTokenType type, string text) in expected)
        {
            Assert.Equal(type, reader.NextToken(out string token));
            Assert.Equal(text, token);
        }

        // ';' is an empty identifier that is put back, as before.
        Assert.Equal(ChunkTokenType.Identifier, reader.NextToken(out string empty));
        Assert.Equal(string.Empty, empty);

        Assert.Equal((ChunkTokenType.Integer, "42"), Next("42"));
        Assert.Equal((ChunkTokenType.Identifier, "end"), Next("end"));
    }

    [Fact]
    public void APlainStringAllocatesOnlyItsValue()
    {
        // The common case: one chunk, no escape, no join. The value is cut
        // out of the text once; building it in a fresh builder from a fresh
        // chunk string cost about three times the value.
        string body = new('v', 400);
        ChunkTokenReader reader = new(string.Concat(Enumerable.Repeat("\"" + body + "\" ", 11)));
        reader.NextToken(out _);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++)
        {
            reader.NextToken(out _);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Ten 400-character strings: 2 bytes a character plus a header each.
        Assert.InRange(allocated, 10 * 800, 10 * (800 + 64));
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
