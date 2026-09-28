//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

namespace SourceSharp.RoomContracts;

/// <summary>What a <c>logic_room</c> input does.</summary>
public enum LogicRoomInputKind
{
    /// <summary><c>Test&lt;Dir&gt;</c>: fire the direction's true or false output from <c>neighbours</c>.</summary>
    Test = 0,

    /// <summary><c>TestJoined&lt;Side&gt;</c>: fire the side's joined true or false output from <c>joined</c>.</summary>
    TestJoined = 1,

    /// <summary><c>Trigger&lt;k&gt;</c>: fire channel <i>k</i>.</summary>
    Trigger = 2,

    /// <summary><c>Enable&lt;k&gt;</c>: enable channel <i>k</i>.</summary>
    Enable = 3,

    /// <summary><c>Disable&lt;k&gt;</c>: disable channel <i>k</i>.</summary>
    Disable = 4,

    /// <summary><c>Toggle&lt;k&gt;</c>: flip channel <i>k</i>'s enabled state.</summary>
    Toggle = 5,
}

/// <summary>What a <c>logic_room</c> output reports.</summary>
public enum LogicRoomOutputKind
{
    /// <summary><c>On&lt;Dir&gt;True</c>: the direction's cell holds a room.</summary>
    NeighbourTrue = 0,

    /// <summary><c>On&lt;Dir&gt;False</c>: it does not.</summary>
    NeighbourFalse = 1,

    /// <summary><c>OnJoined&lt;Side&gt;True</c>: the side's socket is joined.</summary>
    JoinedTrue = 2,

    /// <summary><c>OnJoined&lt;Side&gt;False</c>: it is not.</summary>
    JoinedFalse = 3,

    /// <summary><c>OnTrigger&lt;k&gt;</c>: channel <i>k</i> fired.</summary>
    Trigger = 4,
}

/// <summary>A <c>logic_room</c> input taken apart.</summary>
/// <param name="Kind">What it does.</param>
/// <param name="Direction">The direction of a test.</param>
/// <param name="Channel">The channel of a channel input, 1 to <see cref="LogicRoom.Channels"/>.</param>
public readonly record struct LogicRoomInput(LogicRoomInputKind Kind, RoomDirection Direction, int Channel);

/// <summary>A <c>logic_room</c> output taken apart.</summary>
/// <param name="Kind">What it reports.</param>
/// <param name="Direction">The direction of a neighbour or joined output.</param>
/// <param name="Channel">The channel of an <c>OnTrigger</c> output.</param>
public readonly record struct LogicRoomOutput(LogicRoomOutputKind Kind, RoomDirection Direction, int Channel);

/// <summary>
/// <c>logic_room</c>: one per placed room that uses it, holding the room's
/// neighbour flags and eight relay channels in one server-only entity.
/// </summary>
/// <remarks>
/// <para>
/// <b>Networking.</b> Server-only: no edict. The mod derives it from its
/// logical-entity base. Its position does not matter; the linker places one
/// it writes at the room's cell centre.
/// </para>
/// <para>
/// <b>Keys.</b> All but <c>targetname</c> are written by the linker: the
/// author places a <c>logic_room</c> named <c>cxry_room</c> (with its
/// outputs) or names <c>cxry_room</c> in outputs, and the linker fills the
/// rest. The masks are in the room's authored frame, so a test never needs
/// the rotation; <c>rotation</c>, <c>column</c>, <c>row</c> and <c>room</c>
/// are informational. A missing key is its default, never an error.
/// </para>
/// <para>
/// <b>Behaviour.</b> <c>Test&lt;Dir&gt;</c> fires <c>On&lt;Dir&gt;True</c>
/// when the direction's <c>neighbours</c> bit is set, else
/// <c>On&lt;Dir&gt;False</c>; <c>TestJoined&lt;Side&gt;</c> likewise from
/// <c>joined</c>. <c>Trigger&lt;k&gt;</c>: if channel <i>k</i> is enabled
/// and not waiting, fire <c>OnTrigger&lt;k&gt;</c>; if fire-once, disable
/// the channel; if not fast retrigger, ignore <c>Trigger&lt;k&gt;</c> until
/// the longest delay among <c>OnTrigger&lt;k&gt;</c>'s connections has
/// elapsed. <c>Enable</c>, <c>Disable</c> and <c>Toggle</c> set, clear or
/// flip a channel. Every output fires with the activator of the input that
/// caused it and the <c>logic_room</c> as caller. The masks never change
/// at runtime; channel state is saved and restored like a
/// <c>logic_relay</c>'s, and starts enabled unless
/// <c>relayflags&lt;k&gt; &amp; 1</c>.
/// </para>
/// <para>
/// <b>Stock fallback.</b> Without <c>-mod-entities</c> the linker writes a
/// <c>logic_branch</c> per direction a room tests (named for the flag,
/// <c>c3r5_has_east</c>) and a <c>logic_relay</c> per channel it uses
/// (<c>c3r5_room_channel1</c>), so a map built without the mod runs on any
/// Source game.
/// </para>
/// </remarks>
public static class LogicRoom
{
    /// <summary>The class name.</summary>
    public const string ClassName = "logic_room";

    /// <summary>Whether the class takes an edict: it does not (server-only).</summary>
    public const bool Networked = false;

    /// <summary>The number of relay channels.</summary>
    public const int Channels = 8;

    /// <summary>The name key; the only one an author writes.</summary>
    public const string TargetNameKey = "targetname";

    /// <summary>The 8-bit neighbour mask, authored frame (<see cref="NeighbourMask"/>).</summary>
    public const string NeighboursKey = "neighbours";

    /// <summary>The 4-bit joined-socket mask, authored frame (<see cref="JoinedMask"/>).</summary>
    public const string JoinedKey = "joined";

    /// <summary>The placement's quarter turns, counter-clockwise from above, 0 to 3.</summary>
    public const string RotationKey = "rotation";

    /// <summary>The level column, from the west, 0-based.</summary>
    public const string ColumnKey = "column";

    /// <summary>The level row, from the south, 0-based.</summary>
    public const string RowKey = "row";

    /// <summary>The library room's name.</summary>
    public const string RoomKey = "room";

    /// <summary>The prefix of a channel's flags key, before its number (<see cref="RelayFlags"/>).</summary>
    public const string RelayFlagsKeyPrefix = "relayflags";

    /// <summary>
    /// The class's FGD entry, every line spelt out: what the mod's FGD holds,
    /// and what a fact holds against these constants and the design's text.
    /// </summary>
    public const string Fgd = """
        @PointClass base(Targetname) iconsprite("editor/logic_relay.vmt") = logic_room :
            "Per-room hub written by ssmap link: neighbour flags and eight relay channels. Keys other than the name are filled in by the linker."
        [
            neighbours(integer) : "Neighbours (authored frame: 1 E, 2 N, 4 W, 8 S, 16 NE, 32 NW, 64 SW, 128 SE)" : 0
            joined(integer) : "Joined sockets (authored frame: 1 E, 2 N, 4 W, 8 S)" : 0
            rotation(integer) : "Placement rotation, quarter turns counter-clockwise" : 0
            column(integer) : "Level column, from the west" : 0
            row(integer) : "Level row, from the south" : 0
            room(string) : "Library room name" : ""
            relayflags1(integer) : "Channel 1 flags (1 start disabled, 2 fire once, 4 fast retrigger)" : 0
            relayflags2(integer) : "Channel 2 flags" : 0
            relayflags3(integer) : "Channel 3 flags" : 0
            relayflags4(integer) : "Channel 4 flags" : 0
            relayflags5(integer) : "Channel 5 flags" : 0
            relayflags6(integer) : "Channel 6 flags" : 0
            relayflags7(integer) : "Channel 7 flags" : 0
            relayflags8(integer) : "Channel 8 flags" : 0

            input TestEast(void) : "Fire OnEastTrue or OnEastFalse"
            input TestNorth(void) : "Fire OnNorthTrue or OnNorthFalse"
            input TestWest(void) : "Fire OnWestTrue or OnWestFalse"
            input TestSouth(void) : "Fire OnSouthTrue or OnSouthFalse"
            input TestNorthEast(void) : "Fire OnNorthEastTrue or OnNorthEastFalse"
            input TestNorthWest(void) : "Fire OnNorthWestTrue or OnNorthWestFalse"
            input TestSouthWest(void) : "Fire OnSouthWestTrue or OnSouthWestFalse"
            input TestSouthEast(void) : "Fire OnSouthEastTrue or OnSouthEastFalse"
            input TestJoinedEast(void) : "Fire OnJoinedEastTrue or OnJoinedEastFalse"
            input TestJoinedNorth(void) : "Fire OnJoinedNorthTrue or OnJoinedNorthFalse"
            input TestJoinedWest(void) : "Fire OnJoinedWestTrue or OnJoinedWestFalse"
            input TestJoinedSouth(void) : "Fire OnJoinedSouthTrue or OnJoinedSouthFalse"
            input Trigger1(void) : "Fire channel 1"
            input Enable1(void) : "Enable channel 1"
            input Disable1(void) : "Disable channel 1"
            input Toggle1(void) : "Toggle channel 1"
            input Trigger2(void) : "Fire channel 2"
            input Enable2(void) : "Enable channel 2"
            input Disable2(void) : "Disable channel 2"
            input Toggle2(void) : "Toggle channel 2"
            input Trigger3(void) : "Fire channel 3"
            input Enable3(void) : "Enable channel 3"
            input Disable3(void) : "Disable channel 3"
            input Toggle3(void) : "Toggle channel 3"
            input Trigger4(void) : "Fire channel 4"
            input Enable4(void) : "Enable channel 4"
            input Disable4(void) : "Disable channel 4"
            input Toggle4(void) : "Toggle channel 4"
            input Trigger5(void) : "Fire channel 5"
            input Enable5(void) : "Enable channel 5"
            input Disable5(void) : "Disable channel 5"
            input Toggle5(void) : "Toggle channel 5"
            input Trigger6(void) : "Fire channel 6"
            input Enable6(void) : "Enable channel 6"
            input Disable6(void) : "Disable channel 6"
            input Toggle6(void) : "Toggle channel 6"
            input Trigger7(void) : "Fire channel 7"
            input Enable7(void) : "Enable channel 7"
            input Disable7(void) : "Disable channel 7"
            input Toggle7(void) : "Toggle channel 7"
            input Trigger8(void) : "Fire channel 8"
            input Enable8(void) : "Enable channel 8"
            input Disable8(void) : "Disable channel 8"
            input Toggle8(void) : "Toggle channel 8"

            output OnEastTrue(void) : "East neighbour present"
            output OnEastFalse(void) : "East neighbour absent"
            output OnNorthTrue(void) : "North neighbour present"
            output OnNorthFalse(void) : "North neighbour absent"
            output OnWestTrue(void) : "West neighbour present"
            output OnWestFalse(void) : "West neighbour absent"
            output OnSouthTrue(void) : "South neighbour present"
            output OnSouthFalse(void) : "South neighbour absent"
            output OnNorthEastTrue(void) : "NorthEast neighbour present"
            output OnNorthEastFalse(void) : "NorthEast neighbour absent"
            output OnNorthWestTrue(void) : "NorthWest neighbour present"
            output OnNorthWestFalse(void) : "NorthWest neighbour absent"
            output OnSouthWestTrue(void) : "SouthWest neighbour present"
            output OnSouthWestFalse(void) : "SouthWest neighbour absent"
            output OnSouthEastTrue(void) : "SouthEast neighbour present"
            output OnSouthEastFalse(void) : "SouthEast neighbour absent"
            output OnJoinedEastTrue(void) : "East socket joined"
            output OnJoinedEastFalse(void) : "East socket not joined"
            output OnJoinedNorthTrue(void) : "North socket joined"
            output OnJoinedNorthFalse(void) : "North socket not joined"
            output OnJoinedWestTrue(void) : "West socket joined"
            output OnJoinedWestFalse(void) : "West socket not joined"
            output OnJoinedSouthTrue(void) : "South socket joined"
            output OnJoinedSouthFalse(void) : "South socket not joined"
            output OnTrigger1(void) : "Channel 1 fired"
            output OnTrigger2(void) : "Channel 2 fired"
            output OnTrigger3(void) : "Channel 3 fired"
            output OnTrigger4(void) : "Channel 4 fired"
            output OnTrigger5(void) : "Channel 5 fired"
            output OnTrigger6(void) : "Channel 6 fired"
            output OnTrigger7(void) : "Channel 7 fired"
            output OnTrigger8(void) : "Channel 8 fired"
        ]
        """;

    /// <summary>A channel's flags key: <c>relayflags3</c>.</summary>
    /// <param name="channel">The channel, 1 to <see cref="Channels"/>.</param>
    /// <returns>The key.</returns>
    public static string RelayFlagsKey(int channel) => RelayFlagsKeyPrefix + Channel(channel);

    /// <summary>A direction's test input: <c>TestEast</c>.</summary>
    /// <param name="direction">The direction.</param>
    /// <returns>The input.</returns>
    public static string TestInput(RoomDirection direction) => "Test" + RoomDirections.Pascal(direction);

    /// <summary>A side's joined test input: <c>TestJoinedEast</c>.</summary>
    /// <param name="side">The side.</param>
    /// <returns>The input.</returns>
    public static string TestJoinedInput(RoomDirection side) => "TestJoined" + Side(side);

    /// <summary>A channel input: <c>Trigger1</c>, <c>Enable1</c>, <c>Disable1</c> or <c>Toggle1</c>.</summary>
    /// <param name="kind">Which of the four (not a test).</param>
    /// <param name="channel">The channel, 1 to <see cref="Channels"/>.</param>
    /// <returns>The input.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A test, or not a channel.</exception>
    public static string ChannelInput(LogicRoomInputKind kind, int channel) => kind switch
    {
        LogicRoomInputKind.Trigger => "Trigger",
        LogicRoomInputKind.Enable => "Enable",
        LogicRoomInputKind.Disable => "Disable",
        LogicRoomInputKind.Toggle => "Toggle",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a channel input"),
    } + Channel(channel);

    /// <summary>A direction's output: <c>OnEastTrue</c> or <c>OnEastFalse</c>.</summary>
    /// <param name="direction">The direction.</param>
    /// <param name="value">Which of the two.</param>
    /// <returns>The output.</returns>
    public static string NeighbourOutput(RoomDirection direction, bool value) =>
        "On" + RoomDirections.Pascal(direction) + (value ? "True" : "False");

    /// <summary>A side's joined output: <c>OnJoinedEastTrue</c> or <c>OnJoinedEastFalse</c>.</summary>
    /// <param name="side">The side.</param>
    /// <param name="value">Which of the two.</param>
    /// <returns>The output.</returns>
    public static string JoinedOutput(RoomDirection side, bool value) => "OnJoined" + Side(side) + (value ? "True" : "False");

    /// <summary>A channel's output: <c>OnTrigger1</c>.</summary>
    /// <param name="channel">The channel.</param>
    /// <returns>The output.</returns>
    public static string TriggerOutput(int channel) => "OnTrigger" + Channel(channel);

    /// <summary>
    /// An input taken apart, or false when it is not one of the class's.
    /// Matched ignoring case, as the engine matches input names.
    /// </summary>
    /// <param name="input">The input's name.</param>
    /// <param name="parsed">What it does.</param>
    /// <returns>Whether it is an input of the class.</returns>
    public static bool TryParseInput(string? input, out LogicRoomInput parsed)
    {
        parsed = default;
        if (input is null)
        {
            return false;
        }

        for (int d = 0; d < RoomDirections.Count; d++)
        {
            RoomDirection direction = (RoomDirection)d;
            if (Same(input, TestInput(direction)))
            {
                parsed = new LogicRoomInput(LogicRoomInputKind.Test, direction, 0);
                return true;
            }

            if (RoomDirections.IsSide(direction) && Same(input, TestJoinedInput(direction)))
            {
                parsed = new LogicRoomInput(LogicRoomInputKind.TestJoined, direction, 0);
                return true;
            }
        }

        for (int channel = 1; channel <= Channels; channel++)
        {
            for (LogicRoomInputKind kind = LogicRoomInputKind.Trigger; kind <= LogicRoomInputKind.Toggle; kind++)
            {
                if (Same(input, ChannelInput(kind, channel)))
                {
                    parsed = new LogicRoomInput(kind, default, channel);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>An output taken apart, or false when it is not one of the class's; matched ignoring case.</summary>
    /// <param name="output">The output's name.</param>
    /// <param name="parsed">What it reports.</param>
    /// <returns>Whether it is an output of the class.</returns>
    public static bool TryParseOutput(string? output, out LogicRoomOutput parsed)
    {
        parsed = default;
        if (output is null)
        {
            return false;
        }

        for (int d = 0; d < RoomDirections.Count; d++)
        {
            RoomDirection direction = (RoomDirection)d;
            foreach (bool value in (ReadOnlySpan<bool>)[true, false])
            {
                if (Same(output, NeighbourOutput(direction, value)))
                {
                    parsed = new LogicRoomOutput(value ? LogicRoomOutputKind.NeighbourTrue : LogicRoomOutputKind.NeighbourFalse, direction, 0);
                    return true;
                }

                if (RoomDirections.IsSide(direction) && Same(output, JoinedOutput(direction, value)))
                {
                    parsed = new LogicRoomOutput(value ? LogicRoomOutputKind.JoinedTrue : LogicRoomOutputKind.JoinedFalse, direction, 0);
                    return true;
                }
            }
        }

        for (int channel = 1; channel <= Channels; channel++)
        {
            if (Same(output, TriggerOutput(channel)))
            {
                parsed = new LogicRoomOutput(LogicRoomOutputKind.Trigger, default, channel);
                return true;
            }
        }

        return false;
    }

    private static string Side(RoomDirection side) =>
        RoomDirections.IsSide(side)
            ? RoomDirections.Pascal(side)
            : throw new ArgumentOutOfRangeException(nameof(side), side, "a diagonal has no socket");

    private static string Channel(int channel) =>
        channel is >= 1 and <= Channels
            ? channel.ToString(CultureInfo.InvariantCulture)
            : throw new ArgumentOutOfRangeException(nameof(channel), channel, "a channel is 1 to 8");

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
