//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// One door portal: the area portal a level puts in one of its joints when
/// its library asks for door portals (<see cref="RoomLibraryOptions.DoorPortals"/>).
/// </summary>
/// <param name="Placement">The joint's earlier placement in link order, by index into the layout's rooms.</param>
/// <param name="Socket">That placement's socket at the joint.</param>
/// <param name="Neighbour">The joint's later placement.</param>
/// <param name="NeighbourSocket">The later placement's socket at the joint.</param>
/// <param name="Target">
/// The level name of the door the portal follows (the socket furniture the
/// level keeps at the joint, when it is a named <c>func_door</c> or
/// <c>func_door_rotating</c>), or null for a portal that starts open and
/// stays so.
/// </param>
public sealed record LevelDoorPortal(int Placement, string Socket, int Neighbour, string NeighbourSocket, string? Target);

/// <summary>
/// Door portals: an area portal in every joint of a level whose library
/// asks for them (the rooms design, 4.11, "area portals as doors", and open
/// point O10), planned once by the rules the link and the flatten share.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a door portal is.</b> At a joint the link opens a doorway through
/// the two rooms' plugs. With door portals on, the two sides of every
/// doorway stay separate areas, and a <c>func_areaportal</c> between them
/// lets the engine close what lies beyond: its portal is the doorway's
/// rectangle on the cell face the two plugs meet at, the one the door
/// visibility's flows look through (<see cref="LevelDoorVisibility"/>), so
/// the doorway that the PVS says a line crosses is the doorway the portal
/// can close. The PVS itself does not change: vvis sees through an area
/// portal, and so do the flows.
/// </para>
/// <para>
/// <b>Its entity.</b> A <c>func_areaportal</c> with the portal's number and,
/// when the joint's kept socket furniture is a named door, <c>target</c>
/// naming it (the engine then opens and closes the portal with the door);
/// otherwise <c>StartOpen 1</c>. Whether a portal whose entity names no
/// target stays open is on the in-game checklist (15.8). Each costs one
/// entity, which is why door portals are opt-in (decision D7).
/// </para>
/// <para>
/// <b>The flatten</b> writes the same entity with a brush: the doorway's
/// rectangle given <see cref="HalfThickness"/> units either side of the
/// cell face, of the area portal tool material (<see cref="Material"/>),
/// which seals the open doorway where the flatten left both plugs out.
/// vbsp puts the portal on one of that brush's two faces, the one its
/// flood meets second, so the flattened portal lies a unit from the
/// link's, with the same outline; the area of the brush's own thin leaf
/// follows the same flood. That is a known difference, not a refusal: the
/// portal closes the same doorway either way.
/// </para>
/// <para>
/// <b>Order and numbers.</b> A joint is planned once, from its earlier
/// placement in link order, in the order that placement lists its joints;
/// the link numbers the door portals after every placement's own portals,
/// in that order, and writes their entities after every other entity, and
/// the flatten writes them at the end of its entities in the same order,
/// so vbsp numbers them the same way.
/// </para>
/// </remarks>
public static class LevelDoorPortals
{
    /// <summary>The class of a door portal's entity.</summary>
    public const string ClassName = "func_areaportal";

    /// <summary>The material of the brush the flatten gives a door portal: the game's area portal tool, which is never drawn.</summary>
    public const string Material = "tools/toolsareaportal";

    /// <summary>How far the flatten's door portal brush reaches either side of the cell face.</summary>
    public const float HalfThickness = 1;

    /// <summary>The door classes a door portal follows when one is the joint's kept socket furniture.</summary>
    /// <param name="className">An entity's class.</param>
    /// <returns>True for <c>func_door</c> and <c>func_door_rotating</c>.</returns>
    public static bool IsDoor(string? className) =>
        string.Equals(className, "func_door", StringComparison.Ordinal)
        || string.Equals(className, "func_door_rotating", StringComparison.Ordinal);

    /// <summary>
    /// What one placement of a room can bring a level in door portal
    /// entities at most, for a budget made before the level exists
    /// (<c>ssmap layout</c>): half its sockets, rounded up. Every joint's
    /// portal is shared by the two rooms it joins, so the rooms' halves
    /// cover every joint, and a socket that is capped, or joined, pays at
    /// most half an entity; the estimate never under-counts (the rooms
    /// design, 6.2: over-counting is the safe direction).
    /// </summary>
    /// <param name="definition">The room.</param>
    /// <returns>The bound, in edicts (a <c>func_areaportal</c> takes one).</returns>
    public static int EdictsBound(RoomDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return (definition.Sockets.Count + 1) / 2;
    }

    /// <summary>Plans a level's door portals: one per joint, in the order the link and the flatten write them.</summary>
    /// <param name="layout">The level.</param>
    /// <param name="definitionOf">A room's definition, by name.</param>
    /// <param name="furnitureOf">For a placement and a socket, the priority of its socket furniture there, or null (<see cref="SocketFurniture"/>).</param>
    /// <param name="doorOf">For a placement and a socket, the level name of its door furniture there (<see cref="DoorName"/>), or null.</param>
    /// <returns>The door portals.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<LevelDoorPortal> Plan(
        LevelLayout layout,
        Func<string, RoomDefinition> definitionOf,
        Func<int, string, int?> furnitureOf,
        Func<int, string, string?> doorOf)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(definitionOf);
        ArgumentNullException.ThrowIfNull(furnitureOf);
        ArgumentNullException.ThrowIfNull(doorOf);

        Dictionary<(int X, int Y), int> byCell = new(layout.Rooms.Count);
        for (int i = 0; i < layout.Rooms.Count; i++)
        {
            byCell[(layout.Rooms[i].Placement.CellX, layout.Rooms[i].Placement.CellY)] = i;
        }

        RoomDefinition Of(int placement) => definitionOf(layout.Rooms[placement].Placement.Room);
        List<LevelDoorPortal> portals = [];
        for (int p = 0; p < layout.Rooms.Count; p++)
        {
            RoomInstance instance = layout.Rooms[p];
            RoomDefinition definition = Of(p);
            foreach ((string socket, string neighbourSocket) in instance.Joints)
            {
                RoomSocket mine = definition.Sockets.First(s => string.Equals(s.Name, socket, StringComparison.Ordinal));
                (int axis, int sign) = new RoomTransform(instance.Placement, definition.CellSize).WorldNormal(mine.Facing);
                (int X, int Y) there = (instance.Placement.CellX + (axis == 0 ? sign : 0), instance.Placement.CellY + (axis == 1 ? sign : 0));
                if (!byCell.TryGetValue(there, out int q) || q < p)
                {
                    continue;
                }

                string? target = SocketFurniture.Keeps(layout, Of, p, socket, furnitureOf) ? doorOf(p, socket) : doorOf(q, neighbourSocket);
                portals.Add(new LevelDoorPortal(p, socket, q, neighbourSocket, target));
            }
        }

        return portals;
    }

    /// <summary>
    /// The level name of a placement's door furniture on a socket: the first
    /// entity of a door class (<see cref="IsDoor"/>) whose <c>room_socket</c>
    /// names the socket, that has a <c>targetname</c> and no
    /// <c>room_needs</c> (which could drop it), its name resolved for the
    /// placement (<see cref="RoomLocalNames.Resolve"/>); null when there is
    /// none.
    /// </summary>
    /// <param name="entities">The placement's entities, each as a key lookup (null for a key it lacks).</param>
    /// <param name="socket">The socket.</param>
    /// <param name="placement">Where the room stands, for the name.</param>
    /// <returns>The door's name in the level, or null.</returns>
    public static string? DoorName(IEnumerable<Func<string, string?>> entities, string socket, RoomPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(entities);
        foreach (Func<string, string?> get in entities)
        {
            if (IsDoor(get("classname"))
                && string.Equals(get(RoomStaticProps.SocketKey), socket, StringComparison.Ordinal)
                && get("targetname") is { Length: > 0 } name
                && string.IsNullOrEmpty(get(RoomNeeds.Key)))
            {
                return RoomLocalNames.Resolve(name, placement.CellX, placement.CellY, placement.NormalizedRotation);
            }
        }

        return null;
    }

    /// <summary>
    /// A joint's doorway on the cell face, in world coordinates: the axis
    /// and side the face is on, seen from <paramref name="placement"/>, and
    /// the rectangle, a box flat along that axis at the face.
    /// </summary>
    /// <param name="definition">The placement's room.</param>
    /// <param name="placement">The placement.</param>
    /// <param name="socket">Its socket at the joint.</param>
    /// <returns>The face's axis (0 x, 1 y), the sign of the socket's outward normal on it, and the rectangle.</returns>
    public static (int Axis, int Sign, Box Face) Doorway(RoomDefinition definition, RoomPlacement placement, string socket)
    {
        ArgumentNullException.ThrowIfNull(definition);
        RoomSocket found = definition.Sockets.First(s => string.Equals(s.Name, socket, StringComparison.Ordinal));
        RoomTransform transform = new(placement, definition.CellSize);
        Box plug = RoomLinter.SealBox(definition, found, definition.CellSize);
        Vec3 a = transform.Apply(plug.Mins);
        Vec3 b = transform.Apply(plug.Maxs);
        Vec3 mins = new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
        Vec3 maxs = new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
        (int axis, int sign) = transform.WorldNormal(found.Facing);
        float at = sign > 0 ? (axis == 0 ? maxs.X : maxs.Y) : (axis == 0 ? mins.X : mins.Y);
        return axis == 0
            ? (axis, sign, new Box(new Vec3(at, mins.Y, mins.Z), new Vec3(at, maxs.Y, maxs.Z)))
            : (axis, sign, new Box(new Vec3(mins.X, at, mins.Z), new Vec3(maxs.X, at, maxs.Z)));
    }

    /// <summary>
    /// A door portal's entity in the linked lump, with its number: the keys
    /// in the order vbsp writes them for the flattened level's entity
    /// (<see cref="FlatEntity"/>: the loader sets each key at the front, and
    /// the portal number last).
    /// </summary>
    /// <param name="portal">The door portal.</param>
    /// <param name="number">Its portal number.</param>
    /// <returns>The entity.</returns>
    public static BspEntity LinkedEntity(LevelDoorPortal portal, int number)
    {
        ArgumentNullException.ThrowIfNull(portal);
        BspEntity entity = new();
        entity.Pairs.Add(new BspKeyValue(RoomAreaPortals.PortalNumberKey, number.ToString(CultureInfo.InvariantCulture)));
        entity.Pairs.Add(portal.Target is { } target ? new BspKeyValue("target", target) : new BspKeyValue("StartOpen", "1"));
        entity.Pairs.Add(new BspKeyValue("classname", ClassName));
        return entity;
    }

    /// <summary>
    /// A door portal's entity as the flatten writes it: its class, its
    /// target or <c>StartOpen</c>, and one brush of <see cref="Material"/>
    /// filling the doorway's rectangle <see cref="HalfThickness"/> either
    /// side of the cell face; vbsp numbers it.
    /// </summary>
    /// <param name="portal">The door portal.</param>
    /// <param name="layout">The level.</param>
    /// <param name="definitionOf">A room's definition, by name.</param>
    /// <returns>The entity chunk.</returns>
    public static VmfChunk FlatEntity(LevelDoorPortal portal, LevelLayout layout, Func<string, RoomDefinition> definitionOf)
    {
        ArgumentNullException.ThrowIfNull(portal);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(definitionOf);
        RoomPlacement placement = layout.Rooms[portal.Placement].Placement;
        (int axis, _, Box face) = Doorway(definitionOf(placement.Room), placement, portal.Socket);
        Vec3 reach = axis == 0 ? new Vec3(HalfThickness, 0, 0) : new Vec3(0, HalfThickness, 0);
        VmfChunk entity = new(MapFileLoader.EntityChunk);
        entity.AddKey("classname", ClassName);
        if (portal.Target is { } target)
        {
            entity.AddKey("target", target);
        }
        else
        {
            entity.AddKey("StartOpen", "1");
        }

        entity.Children.Add(RoomModel.Slab(Material, face.Mins - reach, face.Maxs + reach, 0));
        return entity;
    }
}
