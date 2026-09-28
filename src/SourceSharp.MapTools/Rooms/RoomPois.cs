//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;

namespace SourceSharp.MapTools.Rooms;

/// <summary>A room's transition role: whether it moves the player between levels, and which way.</summary>
public enum RoomRole
{
    /// <summary>An ordinary room.</summary>
    None = 0,

    /// <summary><c>room_role up</c>: the room's transition leads up.</summary>
    Up = 1,

    /// <summary><c>room_role down</c>: the room's transition leads down.</summary>
    Down = 2,
}

/// <summary>A point of interest as the author placed it, read from its entity.</summary>
/// <param name="Id">The entity's id in the library, for messages.</param>
/// <param name="Origin">Where it stands, room-local: where an agent's origin (its feet) would be.</param>
/// <param name="Yaw">Its facing in degrees, 0 to 360, from its <c>angles</c>.</param>
/// <param name="HasFacing">Whether the entity has <c>angles</c>.</param>
/// <param name="Radius">Its <c>poi_radius</c>, 0 when absent.</param>
/// <param name="Type">Its <c>poi_type</c>.</param>
/// <param name="Tags">Its <c>poi_tags</c> as written.</param>
/// <param name="Name">Its <c>targetname</c>, or null.</param>
/// <param name="Agents">The agent names of its <c>poi_agents</c>; empty for every agent.</param>
public sealed record AuthoredPoi(
    string Id, Vec3 Origin, float Yaw, bool HasFacing, float Radius, string Type, string Tags, string? Name,
    IReadOnlyList<string> Agents);

/// <summary>
/// Points of interest in a room library: <c>info_poi</c> point entities, read
/// out of a room before it compiles so they cost no entity in the map.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authoring.</b> An <c>info_poi</c> point entity inside a room's cell:
/// </para>
/// <list type="table">
/// <listheader><term>key</term><description>meaning</description></listheader>
/// <item><term><c>poi_type</c></term><description>
/// What it is: an open string. Well-known values: <c>cover</c>,
/// <c>vantage</c>, <c>spawn</c>, <c>patrol</c>, <c>interaction</c>,
/// <c>arrival</c> and <c>custom</c> (the default). <c>door</c> is reserved
/// for the points the compile makes at doors.
/// </description></item>
/// <item><term><c>poi_tags</c></term><description>Free-form tags, comma-separated, kept as written.</description></item>
/// <item><term><c>poi_radius</c></term><description>Optional radius in units, 0 or more.</description></item>
/// <item><term><c>angles</c></term><description>Optional facing: its yaw is the point's yaw.</description></item>
/// <item><term><c>poi_agents</c></term><description>Optional: the agent names it applies to, comma-separated; default every agent.</description></item>
/// <item><term><c>targetname</c></term><description>Optional name; <c>cxry_</c> names are room-local (<see cref="RoomLocalNames"/>).</description></item>
/// </list>
/// <para>
/// <b>Arrival.</b> An <c>arrival</c> point is where a player appears on
/// arriving from another level (and, in the room whose role is up, where a
/// fresh start spawns). It must have a facing, it applies to the player's
/// agent alone, and it must stand on a floor where the player fits.
/// </para>
/// <para>
/// <b>Removed from the map.</b> Points of interest live in the navigation
/// file, not the entity lump: the owner's entity budget is tight, and the AI
/// reads them from the file anyway. So the room compiles from its VMF with
/// the <c>info_poi</c> entities taken out (<see cref="Extract"/>), and the
/// flattened level leaves them out too, so the linked map and the reference
/// agree.
/// </para>
/// </remarks>
public static class RoomPois
{
    /// <summary>The classname of a point of interest.</summary>
    public const string Entity = "info_poi";

    /// <summary>The type of an arrival point.</summary>
    public const string ArrivalType = "arrival";

    /// <summary>The type of the points the compile makes at doors.</summary>
    public const string DoorType = "door";

    /// <summary>The type of a point with no <c>poi_type</c>.</summary>
    public const string DefaultType = "custom";

    /// <summary>The key naming the room's transition role on its <c>info_room</c>.</summary>
    public const string RoleKey = "room_role";

    /// <summary>Whether an entity chunk is a point of interest.</summary>
    /// <param name="entity">The chunk.</param>
    /// <returns>True for an <c>info_poi</c>.</returns>
    public static bool IsPoi(VmfChunk entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return string.Equals(entity.GetValue("classname"), Entity, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Takes a room's points of interest out of its VMF.</summary>
    /// <param name="room">The room's VMF, room-local.</param>
    /// <returns>
    /// The VMF without its <c>info_poi</c> entities (the same document when
    /// it had none; otherwise a new one sharing the other chunks), and the
    /// points, in document order.
    /// </returns>
    /// <exception cref="RoomLintException">A point is malformed; the message names its entity.</exception>
    public static (VmfDocument Stripped, IReadOnlyList<AuthoredPoi> Pois) Extract(VmfDocument room)
    {
        ArgumentNullException.ThrowIfNull(room);
        List<AuthoredPoi> pois = [];
        VmfDocument stripped = new();
        foreach (VmfChunk chunk in room.Chunks)
        {
            if (string.Equals(chunk.Name, MapFileLoader.EntityChunk, StringComparison.OrdinalIgnoreCase) && IsPoi(chunk))
            {
                pois.Add(Read(chunk));
                continue;
            }

            stripped.Chunks.Add(chunk);
        }

        return (pois.Count == 0 ? room : stripped, pois);
    }

    /// <summary>Reads a room's role from its <c>info_room</c>'s <see cref="RoleKey"/>.</summary>
    /// <param name="value">The key's value, or null when absent.</param>
    /// <returns>The role.</returns>
    /// <exception cref="RoomLibraryException">The value is not <c>up</c>, <c>down</c> or <c>none</c>.</exception>
    public static RoomRole ParseRole(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        null or "" or "NONE" => RoomRole.None,
        "UP" => RoomRole.Up,
        "DOWN" => RoomRole.Down,
        _ => throw new RoomLibraryException($"\"{RoleKey}\" is \"{value}\"; a room's role is up, down or none."),
    };

    private static AuthoredPoi Read(VmfChunk entity)
    {
        string id = entity.GetValue("id") ?? "?";
        string who = $"{Entity} {id}";
        Vec3 origin = VmfPlacement.Origin(entity)
            ?? throw new RoomLintException($"{who} has no origin; a point of interest stands somewhere.");

        bool hasFacing = false;
        float yaw = 0;
        if (entity.GetValue("angles") is { } angles)
        {
            string[] parts = angles.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out yaw)
                || !float.IsFinite(yaw))
            {
                throw new RoomLintException($"{who} has angles \"{angles}\", not pitch, yaw and roll.");
            }

            hasFacing = true;
            yaw %= 360f;
            if (yaw < 0)
            {
                yaw += 360f;
            }
        }

        string type = (entity.GetValue("poi_type") ?? DefaultType).Trim();
        if (type.Length == 0 || type.Contains('\0', StringComparison.Ordinal))
        {
            type = DefaultType;
        }

        if (string.Equals(type, DoorType, StringComparison.Ordinal))
        {
            throw new RoomLintException($"{who} has poi_type \"{DoorType}\", which is reserved for the points the compile makes at doors.");
        }

        float radius = 0;
        if (entity.GetValue("poi_radius") is { } radiusText
            && (!float.TryParse(radiusText, NumberStyles.Float, CultureInfo.InvariantCulture, out radius) || !float.IsFinite(radius) || radius < 0))
        {
            throw new RoomLintException($"{who} has poi_radius \"{radiusText}\", not a number of units, 0 or more.");
        }

        string? name = entity.GetValue("targetname") is { Length: > 0 } n ? RoomLibraryVmf.Utf8(n) : null;
        if (name is not null && RoomLocalNames.Problem(name) is { } problem)
        {
            throw new RoomLintException($"{who}: the name \"{name}\" {problem}.");
        }

        List<string> agents = [.. (entity.GetValue("poi_agents") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

        if (type == ArrivalType && !hasFacing)
        {
            throw new RoomLintException($"{who} is an arrival point without angles; an arrival faces the way the player arrives.");
        }

        return new AuthoredPoi(
            id, origin, yaw, hasFacing, radius, RoomLibraryVmf.Utf8(type),
            RoomLibraryVmf.Utf8(entity.GetValue("poi_tags") ?? string.Empty), name, agents);
    }
}
