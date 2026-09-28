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
/// The mod entity contract's constants: <c>logic_room</c>'s keys, inputs and
/// outputs, its FGD held to them and to the design's text, the directions and
/// masks, the linker-owned names, and the contract's version and classes.
/// </summary>
public class LogicRoomContractTests
{
    /// <summary>Every direction's spellings, offset and bits, in the mask order the contract gives.</summary>
    [Theory]
    [InlineData(RoomDirection.East, "east", "East", 1, 0, 1)]
    [InlineData(RoomDirection.North, "north", "North", 0, 1, 2)]
    [InlineData(RoomDirection.West, "west", "West", -1, 0, 4)]
    [InlineData(RoomDirection.South, "south", "South", 0, -1, 8)]
    [InlineData(RoomDirection.NorthEast, "northeast", "NorthEast", 1, 1, 16)]
    [InlineData(RoomDirection.NorthWest, "northwest", "NorthWest", -1, 1, 32)]
    [InlineData(RoomDirection.SouthWest, "southwest", "SouthWest", -1, -1, 64)]
    [InlineData(RoomDirection.SouthEast, "southeast", "SouthEast", 1, -1, 128)]
    public void EveryDirectionIsSpeltAndPlaced(RoomDirection direction, string name, string pascal, int dx, int dy, int bit)
    {
        Assert.Equal(name, RoomDirections.Name(direction));
        Assert.Equal(pascal, RoomDirections.Pascal(direction));
        Assert.Equal((dx, dy), RoomDirections.Offset(direction));
        Assert.Equal((NeighbourMask)bit, RoomDirections.Bit(direction));
        Assert.True(RoomDirections.TryParse(name, out RoomDirection parsed));
        Assert.Equal(direction, parsed);
        Assert.Equal(dx == 0 || dy == 0, RoomDirections.IsSide(direction));
        if (RoomDirections.IsSide(direction))
        {
            Assert.Equal((JoinedMask)bit, RoomDirections.JoinedBit(direction));
        }
        else
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => RoomDirections.JoinedBit(direction));
        }
    }

    /// <summary>An unknown direction, a case variant, or a number is not a direction, and every spelling throws for a value out of range.</summary>
    [Fact]
    public void OnlyTheEightNamesAreDirections()
    {
        Assert.False(RoomDirections.TryParse("East", out _));
        Assert.False(RoomDirections.TryParse("up", out _));
        Assert.False(RoomDirections.TryParse(null, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomDirections.Name((RoomDirection)8));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomDirections.Pascal((RoomDirection)8));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomDirections.Offset((RoomDirection)(-1)));
    }

    /// <summary>Every linker-owned rest parses to its kind, with the class an author's entity of that name must be.</summary>
    [Theory]
    [InlineData("has_east", LinkerNameKind.Has, "logic_branch")]
    [InlineData("has_southwest", LinkerNameKind.Has, "logic_branch")]
    [InlineData("joined_north", LinkerNameKind.Joined, "logic_branch")]
    [InlineData("room", LinkerNameKind.Room, "logic_room")]
    [InlineData("transition", LinkerNameKind.Transition, "trigger_room_transition")]
    [InlineData("room_channel3", LinkerNameKind.Channel, "logic_relay")]
    [InlineData("has_up", LinkerNameKind.UnknownDirection, null)]
    [InlineData("joined_northeast", LinkerNameKind.UnknownDirection, null)]
    [InlineData("joined_", LinkerNameKind.UnknownDirection, null)]
    [InlineData("door", LinkerNameKind.None, null)]
    [InlineData("rooms", LinkerNameKind.None, null)]
    [InlineData("room_channel9", LinkerNameKind.None, null)]
    [InlineData("room_channel", LinkerNameKind.None, null)]
    [InlineData(null, LinkerNameKind.None, null)]
    public void LinkerOwnedRestsParse(string? rest, LinkerNameKind kind, string? expected)
    {
        LinkerName parsed = RoomLinkerNames.Parse(rest);
        Assert.Equal(kind, parsed.Kind);
        Assert.Equal(expected, RoomLinkerNames.ExpectedClass(parsed));
    }

    /// <summary>The rests the linker writes read back as what they name.</summary>
    [Fact]
    public void LinkerRestsRoundTrip()
    {
        for (int d = 0; d < RoomDirections.Count; d++)
        {
            RoomDirection direction = (RoomDirection)d;
            Assert.Equal(new LinkerName(LinkerNameKind.Has, direction, 0), RoomLinkerNames.Parse(RoomLinkerNames.HasRest(direction)));
            if (RoomDirections.IsSide(direction))
            {
                Assert.Equal(new LinkerName(LinkerNameKind.Joined, direction, 0), RoomLinkerNames.Parse(RoomLinkerNames.JoinedRest(direction)));
            }
            else
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => RoomLinkerNames.JoinedRest(direction));
            }
        }

        for (int channel = 1; channel <= LogicRoom.Channels; channel++)
        {
            Assert.Equal(channel, RoomLinkerNames.Parse(RoomLinkerNames.ChannelRest(channel)).Channel);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => RoomLinkerNames.ChannelRest(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomLinkerNames.ChannelRest(9));
    }

    /// <summary>Every input and output the contract names parses back to what it is, ignoring case; nothing else does.</summary>
    [Fact]
    public void EveryInputAndOutputParses()
    {
        for (int d = 0; d < RoomDirections.Count; d++)
        {
            RoomDirection direction = (RoomDirection)d;
            Assert.True(LogicRoom.TryParseInput(LogicRoom.TestInput(direction).ToUpperInvariant(), out LogicRoomInput test));
            Assert.Equal(new LogicRoomInput(LogicRoomInputKind.Test, direction, 0), test);
            foreach (bool value in new[] { true, false })
            {
                Assert.True(LogicRoom.TryParseOutput(LogicRoom.NeighbourOutput(direction, value), out LogicRoomOutput output));
                Assert.Equal(new LogicRoomOutput(value ? LogicRoomOutputKind.NeighbourTrue : LogicRoomOutputKind.NeighbourFalse, direction, 0), output);
                if (RoomDirections.IsSide(direction))
                {
                    Assert.True(LogicRoom.TryParseOutput(LogicRoom.JoinedOutput(direction, value), out output));
                    Assert.Equal(new LogicRoomOutput(value ? LogicRoomOutputKind.JoinedTrue : LogicRoomOutputKind.JoinedFalse, direction, 0), output);
                }
            }

            if (RoomDirections.IsSide(direction))
            {
                Assert.True(LogicRoom.TryParseInput(LogicRoom.TestJoinedInput(direction), out LogicRoomInput joined));
                Assert.Equal(new LogicRoomInput(LogicRoomInputKind.TestJoined, direction, 0), joined);
            }
            else
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => LogicRoom.TestJoinedInput(direction));
                Assert.Throws<ArgumentOutOfRangeException>(() => LogicRoom.JoinedOutput(direction, true));
            }
        }

        for (int channel = 1; channel <= LogicRoom.Channels; channel++)
        {
            for (LogicRoomInputKind kind = LogicRoomInputKind.Trigger; kind <= LogicRoomInputKind.Toggle; kind++)
            {
                Assert.True(LogicRoom.TryParseInput(LogicRoom.ChannelInput(kind, channel), out LogicRoomInput input));
                Assert.Equal(new LogicRoomInput(kind, default, channel), input);
            }

            Assert.True(LogicRoom.TryParseOutput(LogicRoom.TriggerOutput(channel), out LogicRoomOutput fired));
            Assert.Equal(new LogicRoomOutput(LogicRoomOutputKind.Trigger, default, channel), fired);
            Assert.Equal($"relayflags{channel}", LogicRoom.RelayFlagsKey(channel));
        }

        Assert.False(LogicRoom.TryParseInput("Trigger", out _));
        Assert.False(LogicRoom.TryParseInput("Trigger9", out _));
        Assert.False(LogicRoom.TryParseInput(null, out _));
        Assert.False(LogicRoom.TryParseOutput("OnTrigger", out _));
        Assert.False(LogicRoom.TryParseOutput(null, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => LogicRoom.ChannelInput(LogicRoomInputKind.Test, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => LogicRoom.TriggerOutput(9));
    }

    /// <summary>
    /// The FGD entry and the constants agree: every key, input and output the
    /// constants spell has its line in the FGD, and the FGD has no line the
    /// constants do not spell.
    /// </summary>
    [Fact]
    public void TheFgdAndTheConstantsAgree()
    {
        HashSet<string> keys = [LogicRoom.NeighboursKey, LogicRoom.JoinedKey, LogicRoom.RotationKey, LogicRoom.ColumnKey, LogicRoom.RowKey, LogicRoom.RoomKey];
        HashSet<string> inputs = [];
        HashSet<string> outputs = [];
        for (int d = 0; d < RoomDirections.Count; d++)
        {
            RoomDirection direction = (RoomDirection)d;
            inputs.Add(LogicRoom.TestInput(direction));
            outputs.Add(LogicRoom.NeighbourOutput(direction, true));
            outputs.Add(LogicRoom.NeighbourOutput(direction, false));
            if (RoomDirections.IsSide(direction))
            {
                inputs.Add(LogicRoom.TestJoinedInput(direction));
                outputs.Add(LogicRoom.JoinedOutput(direction, true));
                outputs.Add(LogicRoom.JoinedOutput(direction, false));
            }
        }

        for (int channel = 1; channel <= LogicRoom.Channels; channel++)
        {
            keys.Add(LogicRoom.RelayFlagsKey(channel));
            outputs.Add(LogicRoom.TriggerOutput(channel));
            for (LogicRoomInputKind kind = LogicRoomInputKind.Trigger; kind <= LogicRoomInputKind.Toggle; kind++)
            {
                inputs.Add(LogicRoom.ChannelInput(kind, channel));
            }
        }

        (HashSet<string> fgdKeys, HashSet<string> fgdInputs, HashSet<string> fgdOutputs) = Parse(LogicRoom.Fgd);
        Assert.Equal(keys.Order(), fgdKeys.Order());
        Assert.Equal(inputs.Order(), fgdInputs.Order());
        Assert.Equal(outputs.Order(), fgdOutputs.Order());
        Assert.Contains($"= {LogicRoom.ClassName} :", LogicRoom.Fgd, StringComparison.Ordinal);
    }

    /// <summary>
    /// The design's FGD text (its elided lines left out) is in the contract's
    /// FGD, line for line: the prose is the specification, the constant its
    /// spelling, and they cannot drift apart unnoticed.
    /// </summary>
    [RepoSourceFact("docs/rooms-full-features.md")]
    public void TheDesignsFgdIsTheContracts()
    {
        string doc = File.ReadAllText(RepoSourceFactAttribute.Find("docs/rooms-full-features.md")!);
        int start = doc.IndexOf("= logic_room :", StringComparison.Ordinal);
        Assert.True(start > 0);
        int end = doc.IndexOf("```", start, StringComparison.Ordinal);
        string[] fgd = [.. LogicRoom.Fgd.Split('\n').Select(l => l.Trim())];
        foreach (string line in doc[start..end].Split('\n').Select(l => l.Trim()))
        {
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("= logic_room", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.Contains(line, fgd);
        }
    }

    /// <summary>The contract's version, worldspawn keys and class list: logic_room, server-only.</summary>
    [Fact]
    public void TheContractDeclaresItsClassesAndVersion()
    {
        Assert.Equal(1, ModEntityContract.Version);
        Assert.Equal("ssmap_entities", ModEntityContract.EntitiesKey);
        Assert.Equal("ssmap_entities_version", ModEntityContract.VersionKey);
        Assert.Equal(("mod", "stock"), (ModEntityContract.Mod, ModEntityContract.Stock));
        ModEntityClass room = Assert.Single(ModEntityContract.Classes);
        Assert.Equal(new ModEntityClass("logic_room", false), room);
    }

    private static (HashSet<string> Keys, HashSet<string> Inputs, HashSet<string> Outputs) Parse(string fgd)
    {
        HashSet<string> keys = [], inputs = [], outputs = [];
        foreach (Match match in Regex.Matches(fgd, @"^\s*(?:(?<io>input|output)\s+)?(?<name>\w+)\((?<type>\w+)\)", RegexOptions.Multiline))
        {
            string name = match.Groups["name"].Value;
            (match.Groups["io"].Value switch
            {
                "input" => inputs,
                "output" => outputs,
                _ => keys,
            }).Add(name);
        }

        return (keys, inputs, outputs);
    }
}
