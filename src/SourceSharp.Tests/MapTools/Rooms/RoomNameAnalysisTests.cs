//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Rooms;
using SourceSharp.RoomContracts;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.RoomNamingFacts;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The pack-time naming rule (<see cref="RoomRule.LocalNamesWellFormed"/>)
/// and the per-turn tables the room compile stores: every refusal and
/// warning by its exact text (the design's section 15.4), the turned
/// offsets at each of the four turns, the fold candidates, and the
/// <c>NAM</c><i>r</i> section's framing.
/// </summary>
public class RoomNameAnalysisTests
{
    private static RoomNameTurn[] Analyse(params string[][] entities) =>
        RoomNameAnalysis.Analyse("hub", Room(entities)(-1), null);

    private static RoomLintException Refused(params string[][] entities) =>
        Assert.Throws<RoomLintException>(() => Analyse(entities));

    // ---- 15.4 refusals ----------------------------------------------------------

    [Fact]
    public void AMalformedLocalNameIsRefused() =>
        Assert.Equal(
            "room hub: entity 7 (logic_relay) key \"targetname\": \"cx+2ry_door\" is a malformed room-local name; a local name starts with"
            + " cxry_, cx+1ry_, cx-1ry_, cxry+1_, cxry-1_ or a diagonal such as cx+1ry-1_, in lower case, followed by the name.",
            Refused(["classname", "logic_relay", "hammerid", "7", "targetname", "cx+2ry_door"]).Message);

    [Fact]
    public void AGlobalNameThatLooksResolvedIsRefused() =>
        Assert.Equal(
            "room hub: entity 7 (logic_relay) key \"target\": the global name \"C3R5_door\" begins like a room-local or resolved name (c<column>r<row>_); rename it.",
            Refused(["classname", "logic_relay", "hammerid", "7", "target", "C3R5_door"]).Message);

    /// <summary>An output's target and its parameter are read by the same rule, with the output's key in the message.</summary>
    [Fact]
    public void AnOutputsTargetAndParameterAreRead()
    {
        Assert.Contains("key \"OnTrigger\": \"CXRY_door\" is a malformed",
            Refused(["classname", "logic_relay", "hammerid", "7", "OnTrigger", Out("CXRY_door", "Open")]).Message, StringComparison.Ordinal);
        Assert.Contains("key \"OnTrigger\": the global name \"c0r0_x\" begins",
            Refused(["classname", "logic_relay", "hammerid", "7", "OnTrigger", Out("door", "SetParent", "c0r0_x")]).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkerOwnedNameOnTheWrongClassIsRefused()
    {
        Assert.Equal(
            "room hub: entity 7 is a logic_relay named cxry_has_east; that name belongs to a logic_branch.",
            Refused(["classname", "logic_relay", "hammerid", "7", "targetname", "cxry_has_east"]).Message);
        Assert.Equal(
            "room hub: entity 7 is a logic_branch named cxry_room; that name belongs to a logic_room.",
            Refused(["classname", "logic_branch", "hammerid", "7", "targetname", "cxry_room"]).Message);
        Assert.Equal(
            "room hub: entity 7 is a func_button named cxry_transition; that name belongs to a trigger_room_transition.",
            Refused(["classname", "func_button", "hammerid", "7", "targetname", "cxry_transition"]).Message);
        Assert.Equal(
            "room hub: entity 7 is a logic_relay named cxry_room_channel1; that name belongs to the logic_relay the linker writes for a logic_room channel.",
            Refused(["classname", "logic_relay", "hammerid", "7", "targetname", "cxry_room_channel1"]).Message);
        _ = Analyse(["classname", "logic_branch", "hammerid", "7", "targetname", "cxry_joined_west"], ["classname", "logic_room", "targetname", "cxry_room"]);
    }

    [Fact]
    public void AFlagWithAnUnknownDirectionIsRefused() =>
        Assert.Equal(
            "room hub: entity 7 (logic_relay) key \"OnTrigger\": \"cxry_has_up\" names an unknown direction; has_ takes east, north, west, south,"
            + " northeast, northwest, southwest or southeast, and joined_ takes east, north, west or south.",
            Refused(["classname", "logic_relay", "hammerid", "7", "OnTrigger", Out("cxry_has_up", "Test")]).Message);

    [Fact]
    public void AnInputTheHubDoesNotTakeIsRefused() =>
        Assert.Equal(
            "room hub: entity 7 (func_button) output \"OnPressed\" sends Kill to cxry_room, which is not an input of logic_room.",
            Refused(["classname", "func_button", "hammerid", "7", "OnPressed", Out("cxry_room", "Kill")]).Message);

    [Theory]
    [InlineData("up", "up")]
    [InlineData("east,!sky", "sky")]
    [InlineData("joined_northeast", "northeast")]
    [InlineData("East", "East")]
    [InlineData("east,", "")]
    public void ANeedWithAnUnknownDirectionIsRefused(string value, string unknown) =>
        Assert.Equal(
            $"room hub: entity 7 (prop_dynamic) room_needs \"{value}\": unknown direction \"{unknown}\"; use east, west, north, south, a diagonal,"
            + " or joined_ with a side, optionally negated with !.",
            Refused(["classname", "prop_dynamic", "hammerid", "7", "room_needs", value]).Message);

    [Theory]
    [InlineData("light")]
    [InlineData("light_spot")]
    public void ALightWithNeedsIsRefused(string className) =>
        Assert.Equal(
            $"room hub: entity 7 ({className}) has room_needs, but a light's contribution is in the room's baked lighting and cannot be dropped.",
            Refused(["classname", className, "hammerid", "7", "room_needs", "east"]).Message);

    /// <summary>A static prop with <c>room_needs</c> must not cast shadows: refused unless <c>disableshadows</c> is 1.</summary>
    [Fact]
    public void AShadowCastingStaticPropWithNeedsIsRefused()
    {
        Assert.Equal(
            "room hub: prop_static 7 has room_needs and casts shadows; set disableshadows or remove room_needs.",
            Refused(["classname", "prop_static", "id", "7", "room_needs", "east"]).Message);
        Assert.Equal(
            "room hub: prop_static 7 has room_needs and casts shadows; set disableshadows or remove room_needs.",
            Refused(["classname", "prop_static", "id", "7", "disableshadows", "0", "room_needs", "east"]).Message);
        RoomNameTurn[] kept = Analyse(["classname", "prop_static", "id", "7", "disableshadows", "1", "room_needs", "!joined_west"]);
        Assert.Single(kept[0].Needs);
    }

    // ---- 15.4 warnings ----------------------------------------------------------

    [Fact]
    public void AMisplacedPlaceholderIsWarnedOf()
    {
        RoomNameTurn[] turns = Analyse(
            ["classname", "logic_relay", "hammerid", "7", "targetname", "door_cxry"],
            ["classname", "logic_relay", "hammerid", "8", "OnTrigger", Out("!self", "AddOutput", "OnTrigger cxry_door:Open::0:-1")],
            ["classname", "info_target", "hammerid", "9", "message", "see cxry_door"]);
        Assert.Equal(
            [
                "room hub: entity 7 (logic_relay) key \"targetname\": \"door_cxry\" contains cxry after its start; it is a global name, since the placeholder is only read at the start of a name.",
                "room hub: entity 8 (logic_relay) key \"OnTrigger\": \"OnTrigger cxry_door:Open::0:-1\" contains cxry after its start; it is a global name, since the placeholder is only read at the start of a name.",
                "room hub: entity 9 (info_target) key \"message\": \"see cxry_door\" contains cxry after its start; it is a global name, since the placeholder is only read at the start of a name.",
            ],
            turns[0].Warnings.ToArray());
        Assert.True(turns[0].IsEmpty);
    }

    /// <summary>A local name of the room's own that nothing defines is a warning; a flag, a neighbour's, a wildcard and a defined one are not.</summary>
    [Fact]
    public void AnUndefinedLocalNameIsWarnedOf()
    {
        RoomNameTurn[] turns = Analyse(
            ["classname", "func_door", "hammerid", "5", "targetname", "cxry_door"],
            ["classname", "func_button", "hammerid", "7", "OnPressed", Out("cxry_dor", "Open"), "OnPressed", Out("cxry_door", "Open"),
                "OnPressed", Out("cx+1ry_door", "Open"), "OnPressed", Out("cxry_has_east", "Test"), "OnPressed", Out("cxry_d*", "Open")]);
        Assert.Equal(["room hub: entity 7 (func_button) key \"OnPressed\" names cxry_dor, which no entity of the room defines."], turns[0].Warnings.ToArray());
    }

    // ---- tables -----------------------------------------------------------------

    /// <summary>
    /// The design's rotation table (section 5.3), written out: at each turn
    /// every authored offset becomes the level offset the table gives, for
    /// names and for <c>room_needs</c> conditions alike.
    /// </summary>
    [Theory]
    [InlineData(0, "1 0,-1 0,0 1,0 -1,1 1,1 -1,-1 1,-1 -1")]
    [InlineData(1, "0 1,0 -1,-1 0,1 0,-1 1,1 1,-1 -1,1 -1")]
    [InlineData(2, "-1 0,1 0,0 -1,0 1,-1 -1,-1 1,1 -1,1 1")]
    [InlineData(3, "0 -1,0 1,1 0,-1 0,1 -1,-1 -1,1 1,-1 1")]
    public void OffsetsTurnAsTheRotationTableSays(int turn, string table)
    {
        // E, W, N, S, NE, SE, NW, SW, as the design's table orders them.
        string[] names = ["cx+1ry_a", "cx-1ry_a", "cxry+1_a", "cxry-1_a", "cx+1ry+1_a", "cx+1ry-1_a", "cx-1ry+1_a", "cx-1ry-1_a"];
        string[] needs = ["east", "west", "north", "south", "northeast", "southeast", "northwest", "southwest"];
        (int, int)[] expected = [.. table.Split(',').Select(p => p.Split(' ')).Select(p => (int.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture)))];

        List<string> keys = ["classname", "logic_relay", "hammerid", "1"];
        foreach (string name in names)
        {
            keys.AddRange(["OnTrigger", Out(name, "Open")]);
        }

        keys.AddRange(["room_needs", string.Join(',', needs)]);
        RoomNameTurn names0 = Analyse([.. keys])[turn];
        Assert.Equal(expected, names0.Pairs.Select(p => p.Segments.Single(s => s.Literal is null)).Select(s => (s.Dx, s.Dy)));
        Assert.Equal(expected, names0.Needs.Single().Conditions.Select(c => (c.Dx, c.Dy)));
    }

    /// <summary>
    /// An output is kept as pieces: its text around the names, so the link
    /// rebuilds it by concatenation; a whole-key name is one piece; a
    /// targetname is marked as a definition.
    /// </summary>
    [Fact]
    public void AKeyIsStoredAsPieces()
    {
        RoomNameTurn turn = Analyse(
            ["classname", "logic_relay", "targetname", "cxry_relay", "OnTrigger", Out("cx+1ry_door", "SetParent", "cxry_arm", "1.5", "2"),
                "OnTrigger", Out("global", "Open", "cxry_x"), "parentname", "cxry_arm"])[0];
        Assert.Equal(4, turn.Pairs.Length);
        NamePairRef target = turn.Pairs[1];
        Assert.True(target.IsOutput);
        Assert.Equal(
            [
                new NameSegment(null, 1, 0, "door", NameField.Target),
                NameSegment.Text("\u001bSetParent\u001b"),
                new NameSegment(null, 0, 0, "arm", NameField.Parameter),
                NameSegment.Text("\u001b1.5\u001b2"),
            ],
            target.Segments.ToArray());
        Assert.Equal([NameSegment.Text("global\u001bOpen\u001b"), new NameSegment(null, 0, 0, "x", NameField.Parameter), NameSegment.Text("\u001b0\u001b-1")],
            turn.Pairs[2].Segments.ToArray());
        Assert.True(turn.Pairs[0].IsTargetName);
        Assert.False(turn.Pairs[3].IsTargetName);
        Assert.Equal([new NameSegment(null, 0, 0, "arm", NameField.Whole)], turn.Pairs[3].Segments.ToArray());
    }

    /// <summary>Which keys are names: the built-in table whatever its value, any key whose whole value is local, and a library's own keys.</summary>
    [Fact]
    public void NameKeysAreTheTableAndAnyWholeLocalValue()
    {
        Assert.Contains("Template16", RoomNameAnalysis.NameKeys);
        Assert.Contains("filter10", RoomNameAnalysis.NameKeys);
        Assert.Contains("LIGHTINGORIGIN", RoomNameAnalysis.NameKeys);
        RoomNameTurn turn = Analyse(["classname", "npc_x", "squadname", "cxry_squad", "message", "cxry is here"])[0];
        Assert.Single(turn.Pairs);

        // A library's key is read as a name, so a reserved value in it is refused.
        RoomLintException refused = Assert.Throws<RoomLintException>(() => RoomNameAnalysis.Analyse(
            "hub", Room(["classname", "npc_x", "hammerid", "2", "friend", "c1r1_x"])(-1), new HashSet<string>(["friend"], StringComparer.OrdinalIgnoreCase)));
        Assert.Contains("key \"friend\": the global name \"c1r1_x\"", refused.Message, StringComparison.Ordinal);
        Assert.Empty(Analyse(["classname", "npc_x", "friend", "c1r1_x"])[0].Pairs);
    }

    /// <summary>Fold candidates from each entity's own keys: the foldable relay, branch, logic_auto and filter; none of the rest.</summary>
    [Fact]
    public void FoldCandidatesComeFromTheEntitysOwnKeys()
    {
        RoomNameTurn turn = Analyse(
            ["classname", "logic_relay", "targetname", "cxry_a", "spawnflags", "2", "OnTrigger", Out("x", "Open")],
            ["classname", "logic_relay", "targetname", "cxry_b", "spawnflags", "0"],
            ["classname", "logic_relay", "targetname", "global", "spawnflags", "2"],
            ["classname", "logic_branch", "targetname", "cxry_has_east", "OnTrue", Out("x", "Open")],
            ["classname", "logic_branch", "targetname", "cxry_c", "OnTrue", Out("!self", "Kill")],
            ["classname", "logic_auto", "OnMapSpawn", Out("x", "Open")],
            ["classname", "logic_auto", "globalstate", "g"],
            ["classname", "filter_activator_name", "targetname", "cxry_f", "filtername", "player"],
            ["classname", "filter_activator_name", "targetname", "cxry_g", "parentname", "p"],
            ["classname", "logic_relay", "targetname", "cxry_h", "spawnflags", "2", "parentname", "p"])[0];
        Assert.Equal(
            [new FoldCandidate(0, FoldKind.Relay), new FoldCandidate(3, FoldKind.Branch), new FoldCandidate(5, FoldKind.Auto), new FoldCandidate(7, FoldKind.Filter)],
            turn.Candidates.ToArray());
    }

    /// <summary>The worldspawn is never read for names, and a room of global names only is empty.</summary>
    [Fact]
    public void ARoomWithoutNamesIsEmpty()
    {
        RoomNameTurn[] turns = Analyse(["classname", "worldspawn", "targetname", "CXRY_x"], ["classname", "light", "targetname", "lamp"]);
        Assert.All(turns, t => Assert.True(t.IsEmpty));
        Assert.All(turns, t => Assert.Equal(2, t.EntityCount));
    }

    // ---- the section ------------------------------------------------------------

    /// <summary>Every turn's section reads back to what was written, pieces, needs, candidates and warnings.</summary>
    [Fact]
    public void TheSectionRoundTrips()
    {
        RoomNameTurn[] turns = Analyse(
            ["classname", "logic_relay", "targetname", "cxry_a", "spawnflags", "2", "OnTrigger", Out("cx-1ry+1_door", "SetParent", "cxry_b", "0.5", "1")],
            ["classname", "prop_dynamic", "targetname", "cxry_b", "room_needs", "!joined_south,northwest", "message", "a cxry"]);
        for (int turn = 0; turn < 4; turn++)
        {
            RoomPackSectionData section = turns[turn].ToSection();
            Assert.Equal(RoomNameTurn.Tag(turn), section.Tag);
            RoomNameTurn read = RoomNameTurn.Read(section.Bytes.ToArray(), "hub", turn)!;
            Assert.Equal(turns[turn].EntityCount, read.EntityCount);
            Assert.Equal(turns[turn].Pairs.Select(p => (p.Entity, p.Pair, p.IsOutput, p.IsTargetName, string.Join('|', p.Segments))),
                read.Pairs.Select(p => (p.Entity, p.Pair, p.IsOutput, p.IsTargetName, string.Join('|', p.Segments))));
            Assert.Equal(turns[turn].Needs.SelectMany(n => n.Conditions), read.Needs.SelectMany(n => n.Conditions));
            Assert.Equal(turns[turn].Candidates.ToArray(), read.Candidates.ToArray());
            Assert.Equal(turns[turn].Warnings.ToArray(), read.Warnings.ToArray());
            Assert.Equal(section.Bytes.ToArray(), read.ToSection().Bytes.ToArray());
        }

        Assert.Null(RoomNameTurn.Read(null, "hub", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomNameTurn.Tag(4));
    }

    /// <summary>A section of a revision this build does not know reads as absent, so the link reads the names afresh.</summary>
    [Fact]
    public void AnUnknownRevisionReadsAsAbsent()
    {
        byte[] bytes = Analyse(["classname", "info_target", "targetname", "cxry_a"])[0].ToSection().Bytes.ToArray();
        bytes[9 + 3] = 2;
        Assert.Null(RoomNameTurn.Read(bytes, "hub", 0));
    }

    /// <summary>A section out of shape is refused naming the room and the section: an index past the room, an offset past one, bytes after the end.</summary>
    [Fact]
    public void ADamagedSectionIsRefused()
    {
        byte[] good = Analyse(["classname", "info_target", "targetname", "cxry_a"])[0].ToSection().Bytes.ToArray();

        byte[] longer = [.. good, 0];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(longer.AsSpan(1), longer.Length - 9);
        Assert.Contains("\"NAM0\" section holds 1 bytes after its end", Assert.Throws<LinkException>(() => RoomNameTurn.Read(longer, "hub", 0)).Message, StringComparison.Ordinal);

        // After the 9-byte header: revision, entity count, key count, then
        // the first key's entity (payload bytes 12 to 15, big-endian).
        byte[] index = (byte[])good.Clone();
        index[9 + 15] = 5;
        Assert.Contains("a named key of entity 5", Assert.Throws<LinkException>(() => RoomNameTurn.Read(index, "hub", 0)).Message, StringComparison.Ordinal);

        // ... its index (16 to 19), its kind byte (20), its piece count (21
        // to 24), the piece's kind byte (25) and its column offset (26).
        byte[] offset = Analyse(["classname", "info_target", "target", "cx+1ry_a"])[0].ToSection().Bytes.ToArray();
        Assert.Equal(((byte)1, (byte)1), (offset[9 + 25], offset[9 + 26]));
        offset[9 + 26] = 2;
        Assert.Contains("a cell offset of 2", Assert.Throws<LinkException>(() => RoomNameTurn.Read(offset, "hub", 0)).Message, StringComparison.Ordinal);
    }

    // ---- the grammar through RoomLocalNames ------------------------------------

    [Fact]
    public void RoomLocalNamesUsesTheContractGrammar()
    {
        Assert.Equal(RoomNameGrammar.Placeholder, RoomLocalNames.Placeholder);
        Assert.Equal("c3r6_door", RoomLocalNames.Resolve("cx+1ry_door", 3, 5, 1));
        Assert.Equal("c-1r0_x", RoomLocalNames.Resolve("cx-1ry_x", 0, 0, 0));
        Assert.Null(RoomLocalNames.Problem("door"));
    }
}
