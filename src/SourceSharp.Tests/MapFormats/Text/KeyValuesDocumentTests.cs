using System.Text;
using SourceSharp.MapFormats.Text;
using Xunit;

namespace SourceSharp.Tests.MapFormats.Text;

/// <summary>
/// Facts for the KeyValues text format, from <c>src/tier1/KeyValues.cpp</c>
/// and <c>src/tier1/utlbuffer.cpp</c>.
/// </summary>
public class KeyValuesDocumentTests
{
    private static KeyValuesDocument Parse(
        string text,
        KeyValuesParseOptions? options = null) =>
        KeyValuesDocument.ParseAsync(text, options, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

    [Fact]
    public async Task RootNameIsTheFirstTokenInTheFile()
    {
        // KeyValues.cpp:2274,2326. For a VMT that token is the shader name, and
        // nothing validates it against a shader list.
        KeyValuesDocument document = await KeyValuesDocument.ParseAsync(
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"brick/brick01\"\n}\n",
            null, CancellationToken.None);

        Assert.Equal("LightmappedGeneric", document.Root!.Name);
    }

    [Fact]
    public void RootMayBeUnquoted()
    {
        // ReadToken's bare-token branch, KeyValues.cpp:584-618. wasQuoted is
        // consulted only for the brace at :2340, never for the name.
        KeyValuesDocument document = Parse("LightmappedGeneric\n{\n}\n");

        Assert.Equal("LightmappedGeneric", document.Root!.Name);
    }

    [Fact]
    public void LineCommentsAreStrippedAnywhereATokenMayStart()
    {
        // KeyValues.cpp:547-556 loops EatWhiteSpace / EatCPPComment.
        KeyValuesDocument document = Parse(
            "// a header comment\nShader\n{\n\t// and another\n\t\"$a\" \"1\"\n}\n");

        Assert.Equal("1", document.Root!.GetString("$a"));
    }

    [Fact]
    public void BlockCommentsAreNotSupported()
    {
        // EatCPPComment (utlbuffer.cpp:421-441) accepts ONLY '//'. A '/*' is
        // ordinary token text, so a C-style comment between a key and its
        // value silently BECOMES the value.
        KeyValuesDocument document = Parse("Shader\n{\n\t\"$a\" /* comment */ \"1\"\n}\n");

        Assert.Equal("/*", document.Root!.GetString("$a"));
    }

    [Fact]
    public void CommentMarkerInsideATokenIsNotAComment()
    {
        // KeyValues.cpp:584-617 -- the bare-token loop has no '//' check, so a
        // comment is only recognised at the START of a token.
        KeyValuesDocument document = Parse("Shader\n{\n\tkey//notacomment value\n}\n");

        Assert.Equal("key//notacomment", document.Root!.Children[0].Name);
    }

    [Fact]
    public void QuotedBraceIsNotAnOpeningBrace()
    {
        // KeyValues.cpp:2340 -- `*s == '{' && !wasQuoted`. A quoted "{" fails
        // the brace test, so the section is never opened and the whole file
        // yields nothing.
        KeyValuesDocument document = Parse("Shader\n\"{\"\n\"$a\" \"1\"\n}\n");

        Assert.Null(document.Root);
    }

    [Fact]
    public void QuotedClosingBraceIsAnOrdinaryKeyName()
    {
        // KeyValues.cpp:2464-2465 -- the same !wasQuoted guard on the way out.
        KeyValuesDocument document = Parse("Shader\n{\n\t\"}\" \"value\"\n}\n");

        Assert.Equal("value", document.Root!.GetString("}"));
    }

    [Fact]
    public void KeyWithNoValueEndsTheEnclosingBlock()
    {
        // KeyValues.cpp:2490-2494 -- a '}' where a value belongs is a hard
        // parse error that breaks out of the block. There is NO valueless key
        // in this format, unlike a VMF chunk name.
        KeyValuesDocument document = Parse("Shader\n{\n\t\"$a\"\n}\n");

        Assert.Empty(document.Root!.Children);
    }

    [Fact]
    public void DuplicateKeysAreBothKept()
    {
        // KeyValues.cpp:2467-2469 -- "this could potentially cause some
        // duplication, but that's what we want sometimes". A surfaceproperties
        // manifest depends on it.
        KeyValuesDocument document = Parse(
            "manifest\n{\n\t\"file\" \"a.txt\"\n\t\"file\" \"b.txt\"\n}\n");

        Assert.Equal(["a.txt", "b.txt"], document.Root!.FindAll("file").Select(n => n.Value));
    }

    [Fact]
    public void KeyLookupIgnoresCase()
    {
        // Names are interned through a case-insensitive symbol table
        // (vstdlib/KeyValuesSystem.cpp:233,238).
        KeyValuesDocument document = Parse("Shader\n{\n\t\"$BaseTexture\" \"x\"\n}\n");

        Assert.Equal("x", document.Root!.GetString("$basetexture"));
    }

    [Fact]
    public void EscapeSequencesAreOffByDefaultSoABackslashIsLiteral()
    {
        // KeyValues.cpp:462 -- m_bHasEscapeSequences defaults to false, and no
        // loader in the map pipeline turns it on. A VMT's "\n" is a backslash
        // and an n.
        KeyValuesDocument document = Parse("Shader\n{\n\t\"$a\" \"models\\props\\x\"\n}\n");

        Assert.Equal("models\\props\\x", document.Root!.GetString("$a"));
    }

    [Fact]
    public void EscapeSequencesDecodeWhenTheFlagIsOn()
    {
        // The C escape table at utlbuffer.cpp:57-69.
        KeyValuesDocument document = Parse(
            "Shader\n{\n\t\"$a\" \"one\\ttwo\\nthree\"\n}\n",
            new KeyValuesParseOptions(EscapeSequences: true));

        Assert.Equal("one\ttwo\nthree", document.Root!.GetString("$a"));
    }

    [Fact]
    public void UnknownEscapeEmitsANulAndDoesNotConsumeTheCharacter()
    {
        // utlbuffer.cpp:100-105 -- FindConversion returns '\0' with a LENGTH OF
        // ZERO for an unrecognised escape, so GetDelimitedCharInternal writes a
        // NUL and leaves the character in place.
        KeyValuesDocument document = Parse(
            "Shader\n{\n\t\"$a\" \"x\\qy\"\n}\n",
            new KeyValuesParseOptions(EscapeSequences: true));

        Assert.Equal("x\0qy", document.Root!.GetString("$a"));
    }

    [Fact]
    public void QuotedStringMayContainANewline()
    {
        // utlbuffer.cpp:773-788 -- only the delimiter ends the string.
        KeyValuesDocument document = Parse("Shader\n{\n\t\"$a\" \"one\ntwo\"\n}\n");

        Assert.Equal("one\ntwo", document.Root!.GetString("$a"));
    }

    [Fact]
    public void UnterminatedQuoteAtEndOfFileIsNotAnError()
    {
        // utlbuffer.cpp:773 -- the loop just exits when the buffer runs out,
        // and the partial text is returned.
        KeyValuesDocument document = Parse("Shader\n{\n\t\"$a\" \"never closed");

        Assert.Equal("never closed", document.Root!.GetString("$a"));
    }

    [Fact]
    public void SeveralRootSectionsAreAllKept()
    {
        // KeyValues.cpp:2269,2363 loop over the whole buffer chaining peers.
        // SaveToFile writes only the FIRST subtree (:835,848), so stock cannot
        // round-trip such a file; this document can.
        KeyValuesDocument document = Parse("A\n{\n}\nB\n{\n}\n");

        Assert.Equal(["A", "B"], document.Roots.Select(r => r.Name));
    }

    [Fact]
    public void BaseDirectiveIsRecordedRatherThanResolved()
    {
        // KeyValues.cpp:2294-2309. Recorded, because resolving it means opening
        // a file and this assembly's readers never touch a path.
        KeyValuesDocument document = Parse("#base \"common.res\"\nShader\n{\n}\n");

        Assert.Equal(["common.res"], document.BaseFiles);
    }

    [Fact]
    public void IncludeDirectiveIsRecorded()
    {
        // KeyValues.cpp:2278-2293.
        KeyValuesDocument document = Parse("#include \"extra.res\"\nShader\n{\n}\n");

        Assert.Equal(["extra.res"], document.IncludeFiles);
    }

    [Fact]
    public void BaseInsideABlockIsAnOrdinaryKeyName()
    {
        // The directives are handled only in LoadFromBuffer, at the TOP LEVEL.
        // RecursiveLoadFromBuffer (:2425-2618) has no such check.
        KeyValuesDocument document = Parse("Shader\n{\n\t\"#base\" \"nope.res\"\n}\n");

        Assert.Empty(document.BaseFiles);
        Assert.Equal("nope.res", document.Root!.GetString("#base"));
    }

    [Fact]
    public void BaseMergeKeepsOurValue()
    {
        // KeyValues.cpp:2152 -- "we always want to keep our value, so nothing
        // to do here".
        KeyValuesDocument mine = Parse("Shader\n{\n\t\"$a\" \"mine\"\n}\n");
        KeyValuesDocument theirs = Parse("Shader\n{\n\t\"$a\" \"theirs\"\n}\n");

        mine.MergeBase(theirs);

        Assert.Equal("mine", mine.Root!.GetString("$a"));
    }

    [Fact]
    public void BaseMergeAppendsKeysWeDoNotHave()
    {
        // KeyValues.cpp:2173-2178, appended at the END.
        KeyValuesDocument mine = Parse("Shader\n{\n\t\"$a\" \"mine\"\n}\n");
        KeyValuesDocument theirs = Parse("Shader\n{\n\t\"$b\" \"theirs\"\n}\n");

        mine.MergeBase(theirs);

        Assert.Equal(["$a", "$b"], mine.Root!.Children.Select(c => c.Name));
    }

    [Fact]
    public void WithTwoBasesTheEarlierOneWins()
    {
        // A consequence of never overwriting: the later base finds the key
        // already present and leaves it.
        KeyValuesDocument mine = Parse("Shader\n{\n}\n");
        KeyValuesDocument first = Parse("Shader\n{\n\t\"$a\" \"first\"\n}\n");
        KeyValuesDocument second = Parse("Shader\n{\n\t\"$a\" \"second\"\n}\n");

        mine.MergeBase(first);
        mine.MergeBase(second);

        Assert.Equal("first", mine.Root!.GetString("$a"));
    }

    [Fact]
    public void IncludeIsASiblingAppendNotAMerge()
    {
        // KeyValues.cpp:2049-2066 links the included root onto the peer chain.
        KeyValuesDocument mine = Parse("A\n{\n}\n");
        KeyValuesDocument other = Parse("B\n{\n}\n");

        mine.AppendInclude(other);

        Assert.Equal(["A", "B"], mine.Roots.Select(r => r.Name));
    }

    [Fact]
    public void ConditionalMatchingThePlatformKeepsTheKey()
    {
        // KeyValues.cpp:2581-2591 -- a trailing conditional on a scalar.
        KeyValuesDocument document = Parse("Shader\n{\n\t\"$a\" \"1\" [$WIN32]\n}\n");

        Assert.Equal("1", document.Root!.GetString("$a"));
    }

    [Fact]
    public void ConditionalNotMatchingThePlatformDropsTheKey()
    {
        KeyValuesDocument document = Parse("Shader\n{\n\t\"$a\" \"1\" [$X360]\n}\n");

        Assert.Null(document.Root!.GetString("$a"));
    }

    [Fact]
    public void Win32MeansIsPcRatherThanIsWindows()
    {
        // KeyValues.cpp:2237 -- "hack hack - for now WIN32 really means IsPC".
        // So the Linux tools satisfy [$WIN32], which is the opposite of what
        // the name suggests.
        KeyValuesDocument document = Parse(
            "Shader\n{\n\t\"$a\" \"1\" [$WIN32]\n}\n",
            new KeyValuesParseOptions(Platform: KeyValuesPlatform.Pc));

        Assert.Equal("1", document.Root!.GetString("$a"));
    }

    [Fact]
    public void NegatedConditionalIsHonouredOnlyAsTheFirstCharacter()
    {
        // KeyValues.cpp:2226-2228 -- bNot is set from the character right after
        // the '[' and nowhere else.
        KeyValuesDocument document = Parse("Shader\n{\n\t\"$a\" \"1\" [!$X360]\n}\n");

        Assert.Equal("1", document.Root!.GetString("$a"));
    }

    [Fact]
    public void ConditionalsAreAnOrderedSubstringSearchWithNoBooleanAlgebra()
    {
        // KeyValues.cpp:2230-2249 -- the seven names are searched for in a
        // FIXED ORDER and the first hit answers. $X360 precedes $WIN32 in that
        // list, so "[$WIN32||$X360]" on a PC is answered by $X360 and is
        // FALSE -- not what the author of such a line intends.
        KeyValuesDocument document = Parse(
            "Shader\n{\n\t\"$a\" \"1\" [$WIN32||$X360]\n}\n",
            new KeyValuesParseOptions(Platform: KeyValuesPlatform.Pc));

        Assert.Null(document.Root!.GetString("$a"));
    }

    [Fact]
    public void UnrecognisedConditionalIsFalse()
    {
        // KeyValues.cpp:2251.
        KeyValuesDocument document = Parse("Shader\n{\n\t\"$a\" \"1\" [$PS3]\n}\n");

        Assert.Null(document.Root!.GetString("$a"));
    }

    [Fact]
    public void ConditionalsAreIgnoredWhenEvaluationIsOff()
    {
        // KeyValues.cpp:2334,2478,2586 -- `!m_bEvaluateConditionals || ...`.
        KeyValuesDocument document = Parse(
            "Shader\n{\n\t\"$a\" \"1\" [$X360]\n}\n",
            new KeyValuesParseOptions(EvaluateConditionals: false));

        Assert.Equal("1", document.Root!.GetString("$a"));
    }

    [Fact]
    public void SerialiserIndentsWithTabsAndSeparatesWithTwoTabsInQuotes()
    {
        // KeyValues.cpp:765-771 for the indent, and :876 for the separator,
        // which is the four literal bytes quote-tab-tab-quote written as one
        // call.
        KeyValuesDocument document = Parse("Shader\n{\n\t\"$a\" \"1\"\n}\n");

        Assert.Equal("\"Shader\"\n{\n\t\"$a\"\t\t\"1\"\n}\n", document.ToText());
    }

    [Fact]
    public void SerialiserQuotesTheSectionNameAndUsesLineFeedsOnly()
    {
        // KeyValues.cpp:743 opens the file "wb", so there is no CRLF
        // translation -- unlike the VMF writer, which emits CRLF explicitly.
        KeyValuesDocument document = Parse("Shader\n{\n\tsub\n\t{\n\t\t\"$a\" \"1\"\n\t}\n}\n");

        Assert.DoesNotContain('\r', document.ToText());
        Assert.StartsWith("\"Shader\"\n{\n", document.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyStringValuesAreDroppedOnSave()
    {
        // KeyValues.cpp:871 -- bAllowEmptyString defaults to false at :511,
        // :740 and :820, so `"k" ""` DISAPPEARS. A round-trip hazard in the
        // format itself.
        KeyValuesDocument document = Parse("Shader\n{\n\t\"$a\" \"\"\n\t\"$b\" \"1\"\n}\n");

        Assert.DoesNotContain("$a", document.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyStringValuesSurviveWhenExplicitlyAllowed()
    {
        KeyValuesDocument document = Parse("Shader\n{\n\t\"$a\" \"\"\n}\n");

        Assert.Contains(
            "\"$a\"\t\t\"\"",
            document.ToText(new KeyValuesWriteOptions(AllowEmptyString: true)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SerialiserAlwaysEscapesADoubleQuoteEvenWithEscapesOff()
    {
        // KeyValues.cpp:785-789 is unconditional; only the backslash case at
        // :790-794 is gated on m_bHasEscapeSequences.
        KeyValuesNode root = new("Shader");
        root.SetString("$a", "say \"hi\"");

        KeyValuesDocument document = new();
        document.Roots.Add(root);

        Assert.Contains("\\\"hi\\\"", document.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void SerialiserLeavesBackslashesRawWithEscapesOff()
    {
        KeyValuesNode root = new("Shader");
        root.SetString("$a", "models\\props\\x");

        KeyValuesDocument document = new();
        document.Roots.Add(root);

        Assert.Contains("models\\props\\x", document.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ChildrenAreWrittenInInsertionOrder()
    {
        // sortKeys defaults to false (KeyValues.cpp:511,740,820), and vbsp's
        // patch writer takes the default (materialpatch.cpp:141).
        KeyValuesNode root = new("patch");
        root.SetString("include", "materials/a.vmt");
        KeyValuesNode insert = root.FindOrCreate("insert");
        insert.SetString("$waterdepth", "128");

        KeyValuesDocument document = new();
        document.Roots.Add(root);

        Assert.Equal(
            "\"patch\"\n{\n\t\"include\"\t\t\"materials/a.vmt\"\n\t\"insert\"\n\t{\n\t\t\"$waterdepth\"\t\t\"128\"\n\t}\n}\n",
            document.ToText());
    }

    [Fact]
    public void ParseThenSerialiseIsAFixedPoint()
    {
        const string canonical =
            "\"patch\"\n{\n\t\"include\"\t\t\"materials/a.vmt\"\n\t\"replace\"\n\t{\n\t\t\"$envmap\"\t\t\"env_cubemap\"\n\t}\n}\n";

        KeyValuesDocument once = Parse(canonical);
        KeyValuesDocument twice = Parse(once.ToText());

        Assert.Equal(canonical, once.ToText());
        Assert.Equal(canonical, twice.ToText());
    }

    [Fact]
    public async Task Utf16ByteOrderMarkIsTranscoded()
    {
        // KeyValues.cpp:2407-2413.
        byte[] utf16 = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("Shader\n{\n\t\"$a\" \"1\"\n}\n")];

        KeyValuesDocument document =
            await KeyValuesDocument.ParseAsync(utf16, null, CancellationToken.None);

        Assert.Equal("1", document.Root!.GetString("$a"));
    }

    [Fact]
    public async Task Utf8ByteOrderMarkIsNotHandledAndPoisonsTheRootName()
    {
        // The other half: there is NO UTF-8 BOM branch, so the three bytes
        // become part of the first token. This is why a VMT saved as "UTF-8
        // with BOM" names a shader nothing recognises.
        byte[] utf8Bom = [0xEF, 0xBB, 0xBF, .. Encoding.Latin1.GetBytes("Shader\n{\n}\n")];

        KeyValuesDocument document =
            await KeyValuesDocument.ParseAsync(utf8Bom, null, CancellationToken.None);

        Assert.NotEqual("Shader", document.Root!.Name);
    }

    [Fact]
    public async Task ReadAsyncOverAStreamMatchesParse()
    {
        const string text = "Shader\n{\n\t\"$a\" \"1\"\n}\n";
        using MemoryStream stream = new(Encoding.Latin1.GetBytes(text));

        KeyValuesDocument document =
            await KeyValuesDocument.ReadAsync(stream, null, CancellationToken.None);

        Assert.Equal("1", document.Root!.GetString("$a"));
    }
}
