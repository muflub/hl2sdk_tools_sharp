//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// Which socket furniture a level keeps: the rule of open point O5 of the
/// rooms design, one function the link and the flatten both call so the
/// two maps keep the same pieces.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a rule is needed.</b> Furniture is something hung in a doorway
/// (a door frame, a door) and marked with <c>room_socket</c> naming the
/// socket. Both rooms of a joint may carry some for the same doorway, and
/// two frames in one opening overlap; a capped socket stays a wall, and
/// furniture in it would be buried in the plug or stick out of the wall.
/// </para>
/// <para>
/// <b>The rule.</b> At a cap the furniture is dropped. At a joint one side
/// keeps its furniture: the side whose furniture has the higher
/// <c>socket_priority</c> (a side's priority is the highest of its pieces
/// on that socket, 0 when unset), and on a tie the side earlier in link
/// order; a side with no furniture on the joint never takes it from one
/// that has some. Only <c>room_socket</c> and <c>socket_priority</c> are
/// read, not <c>room_needs</c>, so which side wins depends on the layout
/// alone, and a piece the (c) mechanism drops is simply not drawn.
/// </para>
/// <para>
/// Static props and brush entities are the furniture this reads; a side's
/// priority is the highest of all its pieces on the socket, of both kinds
/// (<c>LevelLinker.LevelFurniture</c>, and the flatten's own tally).
/// </para>
/// </remarks>
internal static class SocketFurniture
{
    /// <summary>Whether a placement keeps its furniture on one of its sockets.</summary>
    /// <param name="layout">The level.</param>
    /// <param name="definitionOf">Each placement's room definition, by placement index.</param>
    /// <param name="placement">The placement, by index into <see cref="LevelLayout.Rooms"/>.</param>
    /// <param name="socket">The socket the furniture names.</param>
    /// <param name="furniture">
    /// For a placement and a socket name, the priority of the placement's
    /// furniture on that socket, or null when it has none there.
    /// </param>
    /// <returns>True when the placement's furniture on <paramref name="socket"/> stays in the level.</returns>
    public static bool Keeps(
        LevelLayout layout, Func<int, RoomDefinition> definitionOf, int placement, string socket, Func<int, string, int?> furniture)
    {
        RoomInstance instance = layout.Rooms[placement];
        string? neighbourSocket = null;
        foreach ((string mine, string theirs) in instance.Joints)
        {
            if (string.Equals(mine, socket, StringComparison.Ordinal))
            {
                neighbourSocket = theirs;
                break;
            }
        }

        if (neighbourSocket is null)
        {
            return false;
        }

        RoomDefinition definition = definitionOf(placement);
        RoomSocket found = definition.Sockets.First(s => string.Equals(s.Name, socket, StringComparison.Ordinal));
        (int axis, int sign) = new RoomTransform(instance.Placement, definition.CellSize).WorldNormal(found.Facing);
        int nx = instance.Placement.CellX + (axis == 0 ? sign : 0);
        int ny = instance.Placement.CellY + (axis == 0 ? 0 : sign);
        int other = -1;
        for (int i = 0; i < layout.Rooms.Count && other < 0; i++)
        {
            RoomPlacement at = layout.Rooms[i].Placement;
            other = at.CellX == nx && at.CellY == ny ? i : -1;
        }

        if (other < 0 || furniture(other, neighbourSocket) is not { } theirPriority)
        {
            return true;
        }

        int ourPriority = furniture(placement, socket) ?? int.MinValue;
        return ourPriority > theirPriority || (ourPriority == theirPriority && placement < other);
    }
}
