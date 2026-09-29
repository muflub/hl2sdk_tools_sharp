//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.RoomContracts;

namespace SourceSharp.MapTools.Rooms;

/// <summary>One condition of a <c>room_needs</c> key, in the room's authored frame.</summary>
/// <param name="Direction">The direction: any of the eight for a neighbour, a side for a joined socket.</param>
/// <param name="Joined">True for <c>joined_&lt;side&gt;</c> (a socket on that side is joined); false for a neighbour cell.</param>
/// <param name="Negated">True for a leading <c>!</c>: the condition holds when the other does not.</param>
internal readonly record struct RoomNeed(RoomDirection Direction, bool Joined, bool Negated);

/// <summary>
/// The <c>room_needs</c> key: link-time inclusion, the third of the
/// missing-neighbour mechanisms. An entity carrying it is kept in a
/// placement only when every condition holds there, and the key itself never
/// reaches the linked map.
/// </summary>
/// <remarks>
/// <para>
/// <b>Grammar.</b> Conditions joined by commas, all of which must hold;
/// spaces around each are ignored. A condition is a direction
/// (<c>east</c>, <c>north</c>, <c>west</c>, <c>south</c>, or a diagonal
/// such as <c>northeast</c>), true when that cell holds a room, or
/// <c>joined_</c> and a side, true when a socket on that side is joined to
/// a neighbour's; either may be negated with a leading <c>!</c>. Directions
/// are in the room's authored frame and turn with the placement, like the
/// offsets of a local name. Anything else is refused when the room is
/// compiled, naming the direction it did not know.
/// </para>
/// <para>
/// <b>Why the conditions live in the pack, turned.</b> Whether a condition
/// holds depends on the level, but which cell it looks at depends only on
/// the room and its turn, so the room compile stores each condition's
/// level offset per quarter turn, and the link looks one cell up per
/// condition (<see cref="RoomNameTurn"/>).
/// </para>
/// </remarks>
internal static class RoomNeeds
{
    /// <summary>The key.</summary>
    public const string Key = "room_needs";

    /// <summary>A key's conditions, or the first token that is not one.</summary>
    /// <param name="value">The key's value.</param>
    /// <param name="needs">The conditions, in order.</param>
    /// <param name="unknown">The direction that is not one (without <c>!</c> or <c>joined_</c>), or null.</param>
    /// <returns>Whether every token is a condition.</returns>
    public static bool TryParse(string value, out List<RoomNeed> needs, out string? unknown)
    {
        needs = [];
        unknown = null;
        foreach (string raw in value.Split(','))
        {
            string token = raw.Trim(' ', '\t');
            bool negated = token.StartsWith('!');
            string body = negated ? token[1..] : token;
            bool joined = body.StartsWith(RoomLinkerNames.JoinedPrefix, StringComparison.Ordinal);
            string direction = joined ? body[RoomLinkerNames.JoinedPrefix.Length..] : body;
            if (!RoomDirections.TryParse(direction, out RoomDirection parsed) || (joined && !RoomDirections.IsSide(parsed)))
            {
                unknown = direction;
                return false;
            }

            needs.Add(new RoomNeed(parsed, joined, negated));
        }

        return true;
    }

    /// <summary>Whether a key is <c>room_needs</c>, ignoring case as entity keys are.</summary>
    public static bool IsKey(string key) => string.Equals(key, Key, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether every <c>room_needs</c> condition holds for a placement: the
    /// (c) rule the naming resolver applies to entities, for the records
    /// that are no longer entities (static props).
    /// </summary>
    /// <param name="needs">The conditions in the room's authored frame; none always holds.</param>
    /// <param name="turns">The placement's quarter turns.</param>
    /// <param name="column">The placement's column.</param>
    /// <param name="row">The placement's row.</param>
    /// <param name="occupied">Whether a cell of the level holds a room.</param>
    /// <param name="joined">The placement's joined sides, in its authored frame.</param>
    public static bool Hold(
        IReadOnlyList<RoomNeed> needs, int turns, int column, int row, Func<(int X, int Y), bool> occupied, JoinedMask joined)
    {
        bool holds = true;
        foreach (RoomNeed need in needs)
        {
            bool value;
            if (need.Joined)
            {
                value = (joined & RoomDirections.JoinedBit(need.Direction)) != 0;
            }
            else
            {
                (int dx, int dy) = RoomDirections.Offset(need.Direction);
                (int tx, int ty) = RoomNameAnalysis.Turn(dx, dy, turns);
                value = occupied((column + tx, row + ty));
            }

            holds &= value != need.Negated;
        }

        return holds;
    }
}
