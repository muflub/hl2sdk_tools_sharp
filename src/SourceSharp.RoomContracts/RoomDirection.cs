//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.RoomContracts;

/// <summary>
/// A direction in a room's own (authored) frame: east is the room's +x, north
/// its +y, as the library VMF is built. The value is the bit the direction
/// has in <see cref="NeighbourMask"/>.
/// </summary>
/// <remarks>
/// Every direction a room names, in a neighbour flag, a <c>room_needs</c>
/// condition or a <c>logic_room</c> input, is in this frame and turns with
/// the placement, so a room reads the same way at every rotation and the
/// game never needs the rotation to answer a test.
/// </remarks>
public enum RoomDirection
{
    /// <summary>The room's +x side.</summary>
    East = 0,

    /// <summary>The room's +y side.</summary>
    North = 1,

    /// <summary>The room's -x side.</summary>
    West = 2,

    /// <summary>The room's -y side.</summary>
    South = 3,

    /// <summary>+x and +y.</summary>
    NorthEast = 4,

    /// <summary>-x and +y.</summary>
    NorthWest = 5,

    /// <summary>-x and -y.</summary>
    SouthWest = 6,

    /// <summary>+x and -y.</summary>
    SouthEast = 7,
}

/// <summary>
/// The rooms around a placed room that hold a room, in the room's authored
/// frame: <c>logic_room</c>'s <c>neighbours</c> key.
/// </summary>
[Flags]
public enum NeighbourMask : byte
{
    /// <summary>No neighbour.</summary>
    None = 0,

    /// <summary>The cell beyond the room's authored east side.</summary>
    East = 1,

    /// <summary>The cell beyond the room's authored north side.</summary>
    North = 2,

    /// <summary>The cell beyond the room's authored west side.</summary>
    West = 4,

    /// <summary>The cell beyond the room's authored south side.</summary>
    South = 8,

    /// <summary>The diagonal cell north-east in the authored frame.</summary>
    NorthEast = 16,

    /// <summary>The diagonal cell north-west in the authored frame.</summary>
    NorthWest = 32,

    /// <summary>The diagonal cell south-west in the authored frame.</summary>
    SouthWest = 64,

    /// <summary>The diagonal cell south-east in the authored frame.</summary>
    SouthEast = 128,
}

/// <summary>
/// The room's sockets that are joined to a neighbour's (an open doorway), by
/// authored side: <c>logic_room</c>'s <c>joined</c> key.
/// </summary>
[Flags]
public enum JoinedMask : byte
{
    /// <summary>No joined socket.</summary>
    None = 0,

    /// <summary>A socket on the authored east wall is joined.</summary>
    East = 1,

    /// <summary>A socket on the authored north wall is joined.</summary>
    North = 2,

    /// <summary>A socket on the authored west wall is joined.</summary>
    West = 4,

    /// <summary>A socket on the authored south wall is joined.</summary>
    South = 8,
}

/// <summary>A <c>logic_room</c> relay channel's settings: its <c>relayflags</c><i>k</i> key.</summary>
[Flags]
public enum RelayFlags
{
    /// <summary>Enabled, fires every time, waits for its longest delay between triggers.</summary>
    None = 0,

    /// <summary>The channel starts disabled.</summary>
    StartDisabled = 1,

    /// <summary>The channel disables itself after it fires once.</summary>
    FireOnce = 2,

    /// <summary>The channel may fire again before its longest delay has elapsed.</summary>
    FastRetrigger = 4,
}

/// <summary>What the directions mean: their spellings and their offsets.</summary>
public static class RoomDirections
{
    /// <summary>How many directions there are: four sides and four diagonals.</summary>
    public const int Count = 8;

    /// <summary>How many of them are sides, which a socket can face (the first four).</summary>
    public const int SideCount = 4;

    /// <summary>A direction's lower-case name, as names and <c>room_needs</c> spell it: <c>east</c> ... <c>southeast</c>.</summary>
    /// <param name="direction">The direction.</param>
    /// <returns>The name.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Not a direction.</exception>
    public static string Name(RoomDirection direction) => direction switch
    {
        RoomDirection.East => "east",
        RoomDirection.North => "north",
        RoomDirection.West => "west",
        RoomDirection.South => "south",
        RoomDirection.NorthEast => "northeast",
        RoomDirection.NorthWest => "northwest",
        RoomDirection.SouthWest => "southwest",
        RoomDirection.SouthEast => "southeast",
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "not a room direction"),
    };

    /// <summary>
    /// A direction's name as <c>logic_room</c>'s inputs and outputs spell it:
    /// <c>East</c> ... <c>SouthEast</c> (<c>TestNorthEast</c>, <c>OnSouthWestTrue</c>).
    /// </summary>
    /// <param name="direction">The direction.</param>
    /// <returns>The name.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Not a direction.</exception>
    public static string Pascal(RoomDirection direction) => direction switch
    {
        RoomDirection.East => "East",
        RoomDirection.North => "North",
        RoomDirection.West => "West",
        RoomDirection.South => "South",
        RoomDirection.NorthEast => "NorthEast",
        RoomDirection.NorthWest => "NorthWest",
        RoomDirection.SouthWest => "SouthWest",
        RoomDirection.SouthEast => "SouthEast",
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "not a room direction"),
    };

    /// <summary>The direction a lower-case name spells, or false for any other text (case matters).</summary>
    /// <param name="name">The name, as <see cref="Name"/> writes it.</param>
    /// <param name="direction">The direction, or <see cref="RoomDirection.East"/> when there is none.</param>
    /// <returns>Whether the name is a direction.</returns>
    public static bool TryParse(string? name, out RoomDirection direction)
    {
        for (int d = 0; d < Count; d++)
        {
            if (string.Equals(name, Name((RoomDirection)d), StringComparison.Ordinal))
            {
                direction = (RoomDirection)d;
                return true;
            }
        }

        direction = RoomDirection.East;
        return false;
    }

    /// <summary>Whether a direction is a side (east, north, west, south) rather than a diagonal.</summary>
    /// <param name="direction">The direction.</param>
    /// <returns>True for a side.</returns>
    public static bool IsSide(RoomDirection direction) => (int)direction < SideCount;

    /// <summary>The cell offset a direction names in the room's authored frame: east is (+1, 0), north (0, +1).</summary>
    /// <param name="direction">The direction.</param>
    /// <returns>The column and row offsets, each -1, 0 or +1.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Not a direction.</exception>
    public static (int Dx, int Dy) Offset(RoomDirection direction) => direction switch
    {
        RoomDirection.East => (1, 0),
        RoomDirection.North => (0, 1),
        RoomDirection.West => (-1, 0),
        RoomDirection.South => (0, -1),
        RoomDirection.NorthEast => (1, 1),
        RoomDirection.NorthWest => (-1, 1),
        RoomDirection.SouthWest => (-1, -1),
        RoomDirection.SouthEast => (1, -1),
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "not a room direction"),
    };

    /// <summary>The direction's bit in <see cref="NeighbourMask"/>.</summary>
    /// <param name="direction">The direction.</param>
    /// <returns>The bit.</returns>
    public static NeighbourMask Bit(RoomDirection direction) => (NeighbourMask)(1 << (int)direction);

    /// <summary>A side's bit in <see cref="JoinedMask"/>.</summary>
    /// <param name="side">The side.</param>
    /// <returns>The bit.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A diagonal, which no socket faces.</exception>
    public static JoinedMask JoinedBit(RoomDirection side) =>
        IsSide(side) ? (JoinedMask)(1 << (int)side) : throw new ArgumentOutOfRangeException(nameof(side), side, "a diagonal has no socket");
}
