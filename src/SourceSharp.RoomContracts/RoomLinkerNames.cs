//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

namespace SourceSharp.RoomContracts;

/// <summary>What a linker-owned rest names.</summary>
public enum LinkerNameKind
{
    /// <summary>Not a linker-owned rest: an ordinary local name.</summary>
    None = 0,

    /// <summary><c>has_&lt;direction&gt;</c>: a neighbour flag, a <c>logic_branch</c> set to whether that cell holds a room.</summary>
    Has = 1,

    /// <summary><c>joined_&lt;side&gt;</c>: a joined flag, a <c>logic_branch</c> set to whether that side's socket is joined.</summary>
    Joined = 2,

    /// <summary><c>room</c>: the placement's <c>logic_room</c>.</summary>
    Room = 3,

    /// <summary><c>transition</c>: a transition room's volume, a <c>trigger_room_transition</c>.</summary>
    Transition = 4,

    /// <summary>
    /// <c>room_channel&lt;k&gt;</c>: the <c>logic_relay</c> the linker writes
    /// for a <c>logic_room</c> channel when it emits stock entities.
    /// </summary>
    Channel = 5,

    /// <summary>A rest that starts like <see cref="Has"/> or <see cref="Joined"/> with a direction that is not one: refused.</summary>
    UnknownDirection = 6,
}

/// <summary>A linker-owned rest taken apart.</summary>
/// <param name="Kind">What it names.</param>
/// <param name="Direction">The direction of a <see cref="LinkerNameKind.Has"/> or <see cref="LinkerNameKind.Joined"/> flag.</param>
/// <param name="Channel">The channel of a <see cref="LinkerNameKind.Channel"/> relay, 1 to <see cref="LogicRoom.Channels"/>.</param>
public readonly record struct LinkerName(LinkerNameKind Kind, RoomDirection Direction, int Channel);

/// <summary>
/// The rests the linker fills in or emits: a room-local name whose rest is
/// one of these names something the linker owns, and follows the same
/// grammar (<c>cx+1ry_has_north</c> is the east neighbour's north flag).
/// </summary>
/// <remarks>
/// An author entity that carries one must be of the class the linker
/// expects (<see cref="ExpectedClass"/>); anything else is refused when the
/// room is compiled, and so is an unknown direction (<c>cxry_has_up</c>).
/// </remarks>
public static class RoomLinkerNames
{
    /// <summary>The prefix of a neighbour flag's rest.</summary>
    public const string HasPrefix = "has_";

    /// <summary>The prefix of a joined flag's rest.</summary>
    public const string JoinedPrefix = "joined_";

    /// <summary>The rest of a placement's <c>logic_room</c>.</summary>
    public const string Room = "room";

    /// <summary>The rest of a transition room's volume.</summary>
    public const string Transition = "transition";

    /// <summary>The prefix of a stock channel relay's rest, before its channel number.</summary>
    public const string ChannelPrefix = "room_channel";

    /// <summary>The class a flag must be: a stock branch.</summary>
    public const string FlagClass = "logic_branch";

    /// <summary>The class of a transition volume.</summary>
    public const string TransitionClass = "trigger_room_transition";

    /// <summary>The class of a stock channel relay.</summary>
    public const string ChannelClass = "logic_relay";

    /// <summary>A rest taken apart; <see cref="LinkerNameKind.None"/> for an ordinary one.</summary>
    /// <param name="rest">The rest, after the placeholder.</param>
    /// <returns>What it names.</returns>
    public static LinkerName Parse(string? rest)
    {
        if (rest is null)
        {
            return default;
        }

        if (rest.StartsWith(HasPrefix, StringComparison.Ordinal))
        {
            return RoomDirections.TryParse(rest[HasPrefix.Length..], out RoomDirection direction)
                ? new LinkerName(LinkerNameKind.Has, direction, 0)
                : new LinkerName(LinkerNameKind.UnknownDirection, default, 0);
        }

        if (rest.StartsWith(JoinedPrefix, StringComparison.Ordinal))
        {
            return RoomDirections.TryParse(rest[JoinedPrefix.Length..], out RoomDirection side) && RoomDirections.IsSide(side)
                ? new LinkerName(LinkerNameKind.Joined, side, 0)
                : new LinkerName(LinkerNameKind.UnknownDirection, default, 0);
        }

        if (string.Equals(rest, Room, StringComparison.Ordinal))
        {
            return new LinkerName(LinkerNameKind.Room, default, 0);
        }

        if (string.Equals(rest, Transition, StringComparison.Ordinal))
        {
            return new LinkerName(LinkerNameKind.Transition, default, 0);
        }

        if (rest.StartsWith(ChannelPrefix, StringComparison.Ordinal)
            && rest.Length == ChannelPrefix.Length + 1
            && rest[^1] is >= '1' and <= '8')
        {
            return new LinkerName(LinkerNameKind.Channel, default, rest[^1] - '0');
        }

        return default;
    }

    /// <summary>The class an author entity named with this rest must be, or null when it may be any.</summary>
    /// <param name="name">The parsed rest.</param>
    /// <returns><c>logic_branch</c>, <c>logic_room</c>, <c>trigger_room_transition</c>, <c>logic_relay</c>, or null.</returns>
    public static string? ExpectedClass(LinkerName name) => name.Kind switch
    {
        LinkerNameKind.Has or LinkerNameKind.Joined => FlagClass,
        LinkerNameKind.Room => LogicRoom.ClassName,
        LinkerNameKind.Transition => TransitionClass,
        LinkerNameKind.Channel => ChannelClass,
        _ => null,
    };

    /// <summary>A neighbour flag's rest: <c>has_east</c>.</summary>
    /// <param name="direction">The direction, in the room's authored frame.</param>
    /// <returns>The rest.</returns>
    public static string HasRest(RoomDirection direction) => HasPrefix + RoomDirections.Name(direction);

    /// <summary>A joined flag's rest: <c>joined_east</c>.</summary>
    /// <param name="side">The side, in the room's authored frame.</param>
    /// <returns>The rest.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A diagonal, which no socket faces.</exception>
    public static string JoinedRest(RoomDirection side) =>
        RoomDirections.IsSide(side)
            ? JoinedPrefix + RoomDirections.Name(side)
            : throw new ArgumentOutOfRangeException(nameof(side), side, "a diagonal has no socket");

    /// <summary>A stock channel relay's rest: <c>room_channel3</c>.</summary>
    /// <param name="channel">The channel, 1 to <see cref="LogicRoom.Channels"/>.</param>
    /// <returns>The rest.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Not a channel.</exception>
    public static string ChannelRest(int channel) =>
        channel is >= 1 and <= LogicRoom.Channels
            ? ChannelPrefix + channel.ToString(CultureInfo.InvariantCulture)
            : throw new ArgumentOutOfRangeException(nameof(channel), channel, "a channel is 1 to 8");
}
