//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Immutable;
using System.Globalization;

using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// How a quarter turn of a room treats a brush entity's direction keys: the
/// safe rule of section 4.1 of the rooms design (open point O15), one table
/// the split, the room compile, the flatten and the link all read, so the
/// four agree on every brush entity.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why brush entities are different.</b> A point entity has no geometry of
/// its own, so turning its yaw with the room is all a turn needs. A brush
/// entity's brushes are already turned with the room (the flatten moves the
/// brushes, the link carries the turned model), and what its <c>angles</c>
/// then means is its class's business: a class that applies <c>angles</c> to
/// its model at spawn would be turned twice if the key turned too, while a
/// class that reads <c>angles</c> as a direction must have it turned. Which
/// classes do which is game code, which this repository does not have.
/// </para>
/// <para>
/// <b>The rule.</b> A brush entity's <c>angles</c> (and <c>angle</c>) turn
/// only when its class is in the known-direction table
/// (<see cref="ReadsAnglesAsDirection"/>); for every other class they are
/// carried as written, and a room whose brush entity of such a class has
/// them anything but zero is refused when it is split or compiled, naming
/// the entity (<see cref="Problem"/>): at zero, "not turned" and "applied to a
/// model that is already turned" agree, and anything else would be a guess.
/// </para>
/// <para>
/// <b>Direction keys.</b> The stock brush classes that move or push along a
/// direction take it from a key of its own, a world-space Euler triple:
/// <c>movedir</c> (doors, buttons, conveyors, linear movers), <c>pushdir</c>
/// (<c>trigger_push</c>) and <c>gibdir</c> (<c>func_breakable</c>). Those
/// are directions whatever the class, so their yaw turns with the room on
/// every brush entity, 90 degrees a quarter turn, as a point entity's yaw
/// does (<see cref="DirectionKeys"/>).
/// </para>
/// <para>
/// <b>The table starts empty.</b> As far as the stock game code is known
/// (general Source knowledge, not verifiable here), no stock brush class
/// reads <c>angles</c> as a direction: those that move take <c>movedir</c>,
/// and those that rotate (<c>func_rotating</c>, <c>func_door_rotating</c>)
/// start from <c>angles</c> as the model's orientation. A class joins the
/// table once it has been checked in game (the rooms design's manual
/// checklist, 15.8); until then the refusal names the class so the author
/// can zero its angles or ask for it to be added.
/// </para>
/// <para>
/// <b>Consumed classes</b> (<see cref="IsConsumed"/>) are left alone: vbsp
/// moves their brushes into the world or into a lump of its own and keeps no
/// model for them, so a turn never sees their angles.
/// </para>
/// </remarks>
internal static class BrushEntityDirections
{
    /// <summary>
    /// The brush classes that read <c>angles</c> as a direction rather than
    /// applying it to their model: empty until a class is checked in game
    /// (see the remarks).
    /// </summary>
    public static ImmutableHashSet<string> KnownDirectionClasses { get; } = ImmutableHashSet.Create<string>(StringComparer.Ordinal);

    /// <summary>
    /// The keys that hold a world-space direction as an Euler triple on a
    /// brush entity, whose yaw turns with the room whatever the class.
    /// </summary>
    public static ImmutableArray<string> DirectionKeys { get; } = ["movedir", "pushdir", "gibdir"];

    /// <summary>
    /// The brush classes vbsp consumes: their brushes join the world
    /// (<c>func_detail</c>, <c>func_ladder</c>, <c>func_areaportal</c>) or a
    /// lump of their own (<c>func_occluder</c>), or build nothing the map
    /// keeps (<c>func_viscluster</c>), so they have no model to turn.
    /// </summary>
    public static ImmutableHashSet<string> ConsumedClasses { get; } = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "func_detail", "func_ladder", "func_areaportal", "func_areaportalwindow", "func_occluder", "func_viscluster");

    /// <summary>Whether a brush class's <c>angles</c> is a direction to turn (<see cref="KnownDirectionClasses"/>).</summary>
    /// <param name="className">The class, matched exactly as vbsp matches classes.</param>
    /// <param name="table">The table to read; null for <see cref="KnownDirectionClasses"/>.</param>
    public static bool ReadsAnglesAsDirection(string? className, IReadOnlySet<string>? table = null) =>
        className is not null && (table ?? KnownDirectionClasses).Contains(className);

    /// <summary>Whether vbsp consumes a brush class (<see cref="ConsumedClasses"/>).</summary>
    /// <param name="className">The class.</param>
    public static bool IsConsumed(string? className) => className is not null && ConsumedClasses.Contains(className);

    /// <summary>Whether a key is one of the <see cref="DirectionKeys"/>, ignoring case as entity keys are.</summary>
    /// <param name="key">The key.</param>
    public static bool IsDirectionKey(string key)
    {
        foreach (string name in DirectionKeys)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How a quarter turn treats one key of a brush entity: whether it is
    /// turned as a yaw.
    /// </summary>
    /// <param name="className">The entity's class.</param>
    /// <param name="key">The key.</param>
    /// <param name="table">The known-direction table; null for the built-in one.</param>
    /// <returns>
    /// True for a direction key (<see cref="DirectionKeys"/>), and for
    /// <c>angles</c> and <c>angle</c> on a class in the table; false for
    /// anything else, which is carried as written.
    /// </returns>
    public static bool TurnsKey(string? className, string key, IReadOnlySet<string>? table = null)
    {
        if (IsDirectionKey(key))
        {
            return true;
        }

        bool angles = string.Equals(key, "angles", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "angle", StringComparison.OrdinalIgnoreCase);
        return angles && ReadsAnglesAsDirection(className, table);
    }

    /// <summary>Whether a VMF entity is a brush entity: it carries brushes.</summary>
    /// <param name="entity">The <c>entity</c> chunk.</param>
    public static bool IsBrushEntity(VmfChunk entity) =>
        entity.GetChunks(MapFileLoader.SolidChunk).Any();

    /// <summary>
    /// Why a room's brush entity cannot be turned, or null: its class is not
    /// in the table and its <c>angles</c> (or <c>angle</c>) is not zero. The
    /// text is the rooms design's (15.4).
    /// </summary>
    /// <param name="room">The room's name.</param>
    /// <param name="entity">The entity, as the library or the room writes it.</param>
    /// <param name="table">The known-direction table; null for the built-in one.</param>
    /// <returns>The refusal, or null for an entity the rule accepts (a point entity always is).</returns>
    public static string? Problem(string room, VmfChunk entity, IReadOnlySet<string>? table = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        string? className = entity.GetValue("classname");
        if (!IsBrushEntity(entity) || IsConsumed(className) || ReadsAnglesAsDirection(className, table))
        {
            return null;
        }

        foreach (VmfKey key in entity.Keys)
        {
            bool angles = string.Equals(key.Name, "angles", StringComparison.OrdinalIgnoreCase);
            bool angle = string.Equals(key.Name, "angle", StringComparison.OrdinalIgnoreCase);
            if ((angles || angle) && !IsZero(key.Value, angles ? 3 : 1))
            {
                string id = VmfPlacement.IdOf(entity);
                string name = className ?? "no classname";
                return $"room {room}: brush entity {id} ({name}) has {key.Name} \"{key.Value}\"; a turned room cannot tell whether"
                    + $" {name} applies them to its model. Use 0 0 0, or add {name} to the known-direction table.";
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a value is <paramref name="count"/> numbers, all zero. A value
    /// that does not read as numbers is not zero: the rule refuses what it
    /// cannot read rather than guess.
    /// </summary>
    private static bool IsZero(string value, int count)
    {
        string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != count)
        {
            return false;
        }

        foreach (string part in parts)
        {
            if (!float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) || number != 0f)
            {
                return false;
            }
        }

        return true;
    }
}
