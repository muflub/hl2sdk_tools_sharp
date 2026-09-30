//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// The water sockets a level joins: which joints carry water through their
/// doorway, and the rule that the water on the two sides of a joint is at
/// one level (the rooms design, 4.6, "water sockets").
/// </summary>
/// <remarks>
/// <para>
/// A room declares a socket's water (<see cref="RoomWaterSocket"/>): its
/// water may then reach that socket's plug, at the height declared. A joint
/// opens the two plugs into one doorway, so the water on its two sides is one
/// body of water once linked: the flattened level's compile finds one volume
/// through it, and a volume has one surface. So the two sockets of a joint
/// are both dry or both water at the same level, compared exactly (the
/// heights are room-local and a turn is about +z, so a placement never moves
/// them). A capped socket keeps its plug, and its water stays in its room.
/// </para>
/// <para>
/// The link reads each socket's level from its room's compile
/// (<see cref="RoomWater.Doors"/>), the flatten from the library's
/// declarations (<see cref="LibraryRoom.WaterSockets"/>); the room compile
/// holds the two to each other, so both refuse the same levels with the
/// same text.
/// </para>
/// </remarks>
internal static class LevelWaterJoints
{
    /// <summary>
    /// Every joint of the layout, once, from its earlier placement in link
    /// order: the two placements and sockets, and the water level of each
    /// side (null for a dry socket).
    /// </summary>
    /// <param name="layout">The level.</param>
    /// <param name="definitionOf">A room's definition by name.</param>
    /// <param name="levelAt">A placement's socket's water level, or null for a dry one.</param>
    /// <returns>The joints, in link order.</returns>
    public static List<(int A, string SocketA, float? LevelA, int B, string SocketB, float? LevelB)> Joints(
        LevelLayout layout, Func<string, RoomDefinition> definitionOf, Func<int, string, float?> levelAt)
    {
        Dictionary<(int X, int Y), int> byCell = new(layout.Rooms.Count);
        for (int i = 0; i < layout.Rooms.Count; i++)
        {
            byCell[(layout.Rooms[i].Placement.CellX, layout.Rooms[i].Placement.CellY)] = i;
        }

        List<(int, string, float?, int, string, float?)> joints = [];
        for (int p = 0; p < layout.Rooms.Count; p++)
        {
            RoomInstance instance = layout.Rooms[p];
            RoomDefinition definition = definitionOf(instance.Placement.Room);
            foreach ((string socket, string neighbourSocket) in instance.Joints)
            {
                RoomSocket mine = definition.Sockets.First(s => string.Equals(s.Name, socket, StringComparison.Ordinal));
                (int axis, int sign) = new RoomTransform(instance.Placement, definition.CellSize).WorldNormal(mine.Facing);
                (int X, int Y) there = (instance.Placement.CellX + (axis == 0 ? sign : 0), instance.Placement.CellY + (axis == 1 ? sign : 0));
                if (!byCell.TryGetValue(there, out int q) || q < p)
                {
                    continue;
                }

                joints.Add((p, socket, levelAt(p, socket), q, neighbourSocket, levelAt(q, neighbourSocket)));
            }
        }

        return joints;
    }

    /// <summary>
    /// Refuses a level whose joint has water at another level on each side
    /// (dry being a level of its own), naming the first such joint in link
    /// order.
    /// </summary>
    /// <param name="layout">The level.</param>
    /// <param name="definitionOf">A room's definition by name.</param>
    /// <param name="levelAt">A placement's socket's water level, or null for a dry one.</param>
    /// <exception cref="LinkException">A joint's two sides disagree.</exception>
    public static void Check(LevelLayout layout, Func<string, RoomDefinition> definitionOf, Func<int, string, float?> levelAt)
    {
        foreach ((int a, string socketA, float? levelA, int b, string socketB, float? levelB) in Joints(layout, definitionOf, levelAt))
        {
            if (levelA != levelB)
            {
                RoomPlacement pa = layout.Rooms[a].Placement;
                RoomPlacement pb = layout.Rooms[b].Placement;
                throw new LinkException(
                    $"room {pa.Room} at cell ({pa.CellX}, {pa.CellY}) and room {pb.Room} at cell ({pb.CellX}, {pb.CellY})"
                    + $" meet with water at {Level(levelA)} at socket \"{socketA}\" and {Level(levelB)} at socket \"{socketB}\";"
                    + " the water on the two sides of a joint is at one level.");
            }
        }
    }

    /// <summary>A water level as the refusal writes it; <c>none</c> for a dry socket.</summary>
    internal static string Level(float? level) =>
        level is { } value ? value.ToString("0.###", CultureInfo.InvariantCulture) : "none";
}
