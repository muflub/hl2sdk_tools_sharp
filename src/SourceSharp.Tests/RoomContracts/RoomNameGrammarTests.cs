//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text.RegularExpressions;

using SourceSharp.RoomContracts;

using Xunit;

namespace SourceSharp.Tests.RoomContracts;

/// <summary>
/// The room-local naming grammar: every placeholder form, every near miss,
/// the reserved resolved form, and the hand-written parser held to the three
/// patterns that are its single spelling.
/// </summary>
public class RoomNameGrammarTests
{
    /// <summary>Every local form: no offset, dx only (+ and -), dy only (+ and -), both, and a rest that keeps its case.</summary>
    [Theory]
    [InlineData("cxry_door", 0, 0, "door")]
    [InlineData("cx+1ry_door", 1, 0, "door")]
    [InlineData("cx-1ry_door", -1, 0, "door")]
    [InlineData("cxry+1_door", 0, 1, "door")]
    [InlineData("cxry-1_door", 0, -1, "door")]
    [InlineData("cx+1ry+1_a", 1, 1, "a")]
    [InlineData("cx+1ry-1_a", 1, -1, "a")]
    [InlineData("cx-1ry+1_a", -1, 1, "a")]
    [InlineData("cx-1ry-1_a", -1, -1, "a")]
    [InlineData("cxry_Door*", 0, 0, "Door*")]
    [InlineData("cxry__x", 0, 0, "_x")]
    public void EveryLocalFormParses(string name, int dx, int dy, string rest)
    {
        Assert.Equal(RoomNameKind.Local, RoomNameGrammar.Classify(name));
        Assert.True(RoomNameGrammar.TryParseLocal(name, out LocalName local));
        Assert.Equal((dx, dy, rest), (local.Dx, local.Dy, local.Rest));
        Assert.Equal(name.Length - rest.Length, local.PrefixLength);
    }

    /// <summary>Every near miss of the design's list, and every case variant: malformed, never global.</summary>
    [Theory]
    [InlineData("cx+2ry_door")]
    [InlineData("cx+1r_door")]
    [InlineData("cxr_door")]
    [InlineData("cx1ry_door")]
    [InlineData("cx+ 1ry_door")]
    [InlineData("CXRY_door")]
    [InlineData("cXry_door")]
    [InlineData("cxRy_door")]
    [InlineData("cxrydoor")]
    [InlineData("cxry_")]
    [InlineData("cx+1ry_")]
    [InlineData("c4rocket")]
    [InlineData("cxr_panel")]
    [InlineData("c-1r0_a")]
    [InlineData("c+3r5_a")]
    [InlineData("c01r0_a")]
    public void NearMissesAreMalformed(string name) =>
        Assert.Equal(RoomNameKind.Malformed, RoomNameGrammar.Classify(name));

    /// <summary>What the linker writes, in any case, is reserved: a global name may never begin like it.</summary>
    [Theory]
    [InlineData("c0r0_a")]
    [InlineData("c3r5_door")]
    [InlineData("C3R5_door")]
    [InlineData("c12r140_")]
    public void TheResolvedFormIsReserved(string name) =>
        Assert.Equal(RoomNameKind.Reserved, RoomNameGrammar.Classify(name));

    /// <summary>Names that start with the letters but not the family, and names with the placeholder elsewhere, are global.</summary>
    [Theory]
    [InlineData("door")]
    [InlineData("door_cxry")]
    [InlineData("my_cxry_door")]
    [InlineData("!activator")]
    [InlineData("*")]
    [InlineData("c")]
    [InlineData("crate")]
    [InlineData("c1_rx")]
    [InlineData("")]
    public void OtherNamesAreGlobal(string name) =>
        Assert.Equal(RoomNameKind.Global, RoomNameGrammar.Classify(name));

    /// <summary>
    /// The hand-written parser against the three patterns, compiled here:
    /// for every name of a corpus that walks every branch, the kind the
    /// patterns give (local first, then reserved ignoring case, then
    /// suspect) is the kind the parser gives, and a local name's groups are
    /// its offsets and rest.
    /// </summary>
    [Fact]
    public void TheParserAgreesWithThePatterns()
    {
        Regex local = new(RoomNameGrammar.LocalPattern, RegexOptions.CultureInvariant);
        Regex resolved = new(RoomNameGrammar.ResolvedPattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        Regex suspect = new(RoomNameGrammar.SuspectPattern, RegexOptions.CultureInvariant);

        List<string> corpus = [];
        string[] heads = ["c", "C", "x", ""];
        string[] seconds = ["x", "X", "0", "1", "9", "+1", "-1", "+", "-", "r", "_", ""];
        string[] offsets = ["", "+1", "-1", "+2", "1", "+", "-0"];
        string[] mids = ["ry", "rY", "Ry", "r", "y", "", "q"];
        string[] tails = ["_door", "_", "door", "_D*", "__", "_a b", "r_x", "5_x", ""];
        foreach (string head in heads)
        {
            foreach (string second in seconds)
            {
                foreach (string offset in offsets)
                {
                    foreach (string mid in mids)
                    {
                        foreach (string tail in tails)
                        {
                            corpus.Add(head + second + offset + mid + tail);
                        }
                    }
                }
            }
        }

        corpus.AddRange(["c3r5_x", "c0r0_", "c00r1_x", "c3r05_x", "c3r_x", "cr5_x", "c3r5x", "c12345678901r1_x", "c1r2_", "C1R2_Q"]);
        foreach (string name in corpus.Distinct())
        {
            RoomNameKind expected = local.IsMatch(name) ? RoomNameKind.Local
                : resolved.IsMatch(name) ? RoomNameKind.Reserved
                : suspect.IsMatch(name) ? RoomNameKind.Malformed
                : RoomNameKind.Global;
            Assert.True(expected == RoomNameGrammar.Classify(name), $"\"{name}\": the patterns say {expected}, the parser {RoomNameGrammar.Classify(name)}");
            if (expected == RoomNameKind.Local)
            {
                Match match = local.Match(name);
                RoomNameGrammar.TryParseLocal(name, out LocalName parsed);
                Assert.Equal(match.Groups["dx"].Success ? int.Parse(match.Groups["dx"].Value, System.Globalization.CultureInfo.InvariantCulture) : 0, parsed.Dx);
                Assert.Equal(match.Groups["dy"].Success ? int.Parse(match.Groups["dy"].Value, System.Globalization.CultureInfo.InvariantCulture) : 0, parsed.Dy);
                Assert.Equal(match.Groups["rest"].Value, parsed.Rest);
            }
        }
    }

    /// <summary>A line feed is never part of a name: the parser does not take one as local.</summary>
    [Fact]
    public void ALineFeedIsNotLocal()
    {
        Assert.False(RoomNameGrammar.TryParseLocal("cxry_a\nb", out _));
        Assert.False(RoomNameGrammar.TryParseLocal(null, out _));
    }

    /// <summary>The resolved prefix: lower case, no leading zeros, and no negative cell.</summary>
    [Fact]
    public void TheResolvedPrefixIsWrittenAndReadBack()
    {
        Assert.Equal("c3r5_", RoomNameGrammar.ResolvedPrefix(3, 5));
        Assert.Equal("c0r12_door", RoomNameGrammar.Resolve(0, 12, "door"));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomNameGrammar.ResolvedPrefix(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomNameGrammar.ResolvedPrefix(0, -1));

        Assert.True(RoomNameGrammar.TryParseResolved("c3r5_door", out int column, out int row, out string rest));
        Assert.Equal((3, 5, "door"), (column, row, rest));
        Assert.False(RoomNameGrammar.TryParseResolved("C3R5_door", out _, out _, out _));
        Assert.False(RoomNameGrammar.TryParseResolved("c03r5_door", out _, out _, out _));
        Assert.False(RoomNameGrammar.TryParseResolved("cxry_door", out _, out _, out _));
        Assert.False(RoomNameGrammar.TryParseResolved(null, out _, out _, out _));
    }

    /// <summary>A placeholder anywhere but at the start is seen, in any form; at the start, in short values, or nowhere, it is not.</summary>
    [Theory]
    [InlineData("door_cxry", true)]
    [InlineData("OnTrigger cxry_door:Open::0:-1", true)]
    [InlineData("a cx+1ry-1_b", true)]
    [InlineData("cxry_door", false)]
    [InlineData("door", false)]
    [InlineData("", false)]
    [InlineData("c", false)]
    [InlineData("acxr", false)]
    [InlineData("xcx+2ry", false)]
    [InlineData(null, false)]
    public void APlaceholderAfterTheStartIsFound(string? value, bool found) =>
        Assert.Equal(found, RoomNameGrammar.HasPlaceholderAfterStart(value));
}
