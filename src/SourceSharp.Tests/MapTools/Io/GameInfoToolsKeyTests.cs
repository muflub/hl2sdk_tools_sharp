using SourceSharp.MapTools.Compile;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Io;

/// <summary>
/// The <c>Tools</c> key of a <c>gameinfo.txt</c>: the section form the reference tools read
/// (<c>FindKey("Tools")</c> then <c>ReadString("vbsp", …)</c>), the flat form it
/// silently ignores, and its strtok-style tokenisation of the value.
/// </summary>
public class GameInfoToolsKeyTests
{
    private const string SectionForm = "\"GameInfo\"\n{\n"
        + "    game\t\"probe\"\n"
        + "    FileSystem\n    {\n        SteamAppId\t243750\n    }\n"
        + "    Tools\n    {\n"
        + "        vbsp\t\"-cullall -staticpropformat 9\"\n"
        + "        vvis\t\"-fast\"\n"
        + "    }\n}\n";

    // ---- the section form ----

    [Fact]
    public void TheSectionFormIsReadPerTool()
    {
        GameInfo info = GameInfo.Parse(SectionForm);

        Assert.Equal("-cullall -staticpropformat 9", info.ToolArguments["vbsp"]);
        Assert.Equal("-fast", info.ToolArguments["vvis"]);
        Assert.False(info.HasFlatToolsValue);
    }

    [Fact]
    public void TheChildKeyIsNotRestrictedToTheThreeTools()
    {
        // The reference tools ask for one child by name; the port keeps every child so a host
        // has the whole table (an unrecognised tool key is not a parse error).
        GameInfo info = GameInfo.Parse("\"GameInfo\"\n{\n"
            + "    Tools\n    {\n        vbsp\t\"-v\"\n        bspzip\t\"-repack\"\n    }\n}\n");

        Assert.Equal(["vbsp", "bspzip"], info.ToolArguments.Keys);
    }

    [Fact]
    public void TheSectionAndItsChildKeysAreCaseInsensitive()
    {
        // ++'s KeyValues lookups fold case; so does the port's.
        GameInfo info = GameInfo.Parse("\"GameInfo\"\n{\n"
            + "    TOOLS\n    {\n        VBSP\t\"-cullall\"\n    }\n}\n");

        Assert.Equal("-cullall", info.ToolArguments["vbsp"]);
    }

    [Fact]
    public void AToolsBlockInsideFileSystemIsNeverRead()
    {
        // FindKey walks the folded root's OWN child list and never descends
        // into a subsection; the reference's lookup passes no "a/b" path,
        // and the fold makes the
        // GameInfo section's children the root's children — so a Tools block
        // written inside FileSystem is two levels down and invisible to the
        // splice. This is the spelling mod authors reach for; the finding is
        // that it does nothing.
        GameInfo info = GameInfo.Parse("\"GameInfo\"\n{\n"
            + "    FileSystem\n    {\n        SteamAppId\t243750\n"
            + "        Tools\n        {\n            vbsp\t\"-onlyents\"\n        }\n    }\n}\n");

        Assert.Empty(info.ToolArguments);
        Assert.False(info.HasFlatToolsValue);

        // And the same file with Tools as a sibling of FileSystem — a direct
        // child of the folded GameInfo section — IS read. The two spellings
        // differ by nesting depth alone.
        GameInfo sibling = GameInfo.Parse("\"GameInfo\"\n{\n"
            + "    FileSystem\n    {\n        SteamAppId\t243750\n    }\n"
            + "    Tools\n    {\n        vbsp\t\"-onlyents\"\n    }\n}\n");

        Assert.Equal("-onlyents", sibling.ToolArguments["vbsp"]);
    }

    [Fact]
    public void ARepeatedChildKeyKeepsTheFirstValue()
    {
        // FindKey's peer walk breaks on the FIRST node whose key matches
        //, so ReadString answers from the first
        // duplicate; the second line is dead text.
        GameInfo info = GameInfo.Parse("\"GameInfo\"\n{\n"
            + "    Tools\n    {\n        vbsp\t\"-v\"\n        vbsp\t\"-cullall\"\n    }\n}\n");

        Assert.Equal("-v", info.ToolArguments["vbsp"]);
    }

    [Fact]
    public void NoToolsBlockMeansNoArguments()
    {
        Assert.Empty(GameInfo.Parse("\"GameInfo\"\n{\n    game\t\"x\"\n}\n").ToolArguments);
    }

    // ---- the flat form: a silent no-op, reported ---.

    [Fact]
    public void TheFlatFormYieldsNoArgumentsAndIsFlagged()
    {
        // The shape a mod author writes when they mean the section form. ++
        // walks INTO Tools as a section, so a value on the same line is never
        // read as a tool argument — the flags silently do nothing. The port
        // matches (no arguments land anywhere) and reports the shape.
        GameInfo info = GameInfo.Parse("\"GameInfo\"\n{\n"
            + "    Tools\t\"-cullall -staticpropformat 9\"\n}\n");

        Assert.Empty(info.ToolArguments);
        Assert.True(info.HasFlatToolsValue);
    }

    [Fact]
    public void AToolsBlockNestedInAModBlockIsNeitherReadNorFlagged()
    {
        // Only the folded root's OWN child list is walked. A mod block called
        // Tools nested deeper is not the reference tools' key and must not leak children.
        GameInfo info = GameInfo.Parse("\"GameInfo\"\n{\n"
            + "    SomeModBlock\n    {\n        Tools\n        {\n            vbsp\t\"-cullall\"\n        }\n    }\n}\n");

        Assert.Empty(info.ToolArguments);
        Assert.False(info.HasFlatToolsValue);
    }

    [Fact]
    public void AFlatToolsLineShadowsTheSectionWrittenAfterIt()
    {
        // FindKey stops at the first matching node, so a leftover flat line
        // in front of a real section hides it: nothing is spliced at all, and
        // the file's intended defaults die silently. Pinned because it is the
        // one ordering a mod author can be bitten by without changing a word
        // of the section they wrote.
        GameInfo info = GameInfo.Parse("\"GameInfo\"\n{\n"
            + "    Tools\t\"-flat-ignored\"\n"
            + "    Tools\n    {\n        vbsp\t\"-section-read\"\n    }\n}\n");

        Assert.Empty(info.ToolArguments);
        Assert.True(info.HasFlatToolsValue);
    }

    [Fact]
    public void TheResolverWarnsAboutTheFlatFormInsteadOfStayingSilent()
    {
        // The reference tools say NOTHING here — the flags are simply never read. Saying it is
        // this port's deliberate deviation: the shape is a mod author's
        // mistake, and a silent no-op is the worst outcome for them.
        GameInfo flat = GameInfo.Parse("\"GameInfo\"\n{\n"
            + "    FileSystem\n    {\n        SteamAppId\t243750\n    }\n"
            + "    Tools\t\"-bspformat 21\"\n}\n");

        FormatResolution.Result r = FormatResolution.Resolve(null, null, false, false, flat);

        CompileDiagnostic d = Assert.Single(
            r.Diagnostics, x => x.Code == FormatResolution.FlatToolsCode);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);

        // And the ignored flags really were ignored: no splice happened.
        // Field-wise on the writable fields — the fixture's appid is
        // recorded as provenance (DetectedSteamAppId), which legitimately
        // differs from a hand-built Default and must not fail this fact.
        Assert.True(r.Resolved.IsDefault);
        Assert.Null(BspFormatWriter.ToWriteFormat(r.Resolved));
        Assert.Equal(FormatOptions.Default.BspVersion, r.Resolved.BspVersion);
        Assert.Null(r.Resolved.StaticPropsToken);
        Assert.DoesNotContain(r.Diagnostics, x => x.Code == FormatResolution.ToolsSpliceCode);
    }

    [Fact]
    public void TheSectionFormRaisesNoFlatWarning()
    {
        FormatResolution.Result r = FormatResolution.Resolve(
            null, null, false, false, GameInfo.Parse(SectionForm));

        Assert.DoesNotContain(r.Diagnostics, x => x.Code == FormatResolution.FlatToolsCode);
    }

    // ---- ++'s tokenisation of the value ----

    [Theory]
    // strtok collapses runs of separators; empty runs drop.
    [InlineData("  -a\t-b\r\n-c  ", new[] { "-a", "-b", "-c" })]
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    // The reference tools have NO quote handling here: the quote characters
    // stay INSIDE the tokens, so a Tools line written with quotes hands the
    // parser quoted tokens — which is exactly what they do with them.
    [InlineData("-game \"my game\"", new[] { "-game", "\"my", "game\"" })]
    public void TheTokenizerIsStrtokNotAShell(string value, string[] expected)
    {
        Assert.Equal(expected, FormatResolution.TokenizeToolsArguments(value));
    }

    [Fact]
    public void TheTokenizerRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => FormatResolution.TokenizeToolsArguments(null!));
    }

    [Fact]
    public void AQuotedValueInAToolsLineStopsAtTheFirstInnerQuote()
    {
        // The reader of this file has NO escape handling (GetNoEscChar.
        // Conversion: no character is the escape character, so a backslash is
        // an ordinary character and the NEXT quote closes the token). A
        // mod author who writes vbsp "-staticpropformat \"9\"" therefore does
        // not hand the tool a quoted 9 — the value ends at the backslash, and
        // the splice then sees `\` as the format word. End to end so the
        // reader, the tokeniser and the splice cannot drift apart silently.
        GameInfo info = GameInfo.Parse("\"GameInfo\"\n{\n"
            + "    FileSystem\n    {\n        SteamAppId\t243750\n    }\n"
            + "    Tools\n    {\n        vbsp\t\"-staticpropformat \\\"9\\\"\"\n    }\n}\n");

        Assert.Equal("-staticpropformat \\", info.ToolArguments["vbsp"]);

        FormatResolution.Result r = FormatResolution.Resolve(null, null, false, false, info);

        Assert.Contains(
            r.Diagnostics,
            x => x.Code == FormatResolution.ToolsProblemCode
                && x.Message ==
                    "Unrecognized prop format \\ (from the gameinfo Tools key: \"-staticpropformat \\\")");
        Assert.Null(r.Resolved.StaticPropsToken);
    }
}
