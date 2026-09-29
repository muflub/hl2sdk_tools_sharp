//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.RoomContracts;

/// <summary>Which way a level transition moves the player.</summary>
/// <remarks>
/// A level has one room of each role (the rooms design, section 11): the
/// up room leads to the level above and is where a fresh start puts the
/// player; the down room leads to the level below.
/// </remarks>
public enum TransitionDirection
{
    /// <summary><c>up</c>: to the level above.</summary>
    Up = 0,

    /// <summary><c>down</c>: to the level below.</summary>
    Down = 1,
}

/// <summary>
/// The point-of-interest types the transition contract defines. The other
/// types (cover, vantage, patrol, interaction) come with the navigation
/// design and are open strings until then.
/// </summary>
public enum PoiType
{
    /// <summary><c>arrival</c>: where a player coming from another level appears, facing its yaw.</summary>
    Arrival = 0,

    /// <summary><c>spawn</c>: a further spawn point for a fresh start, in the up room or the level's spawn room.</summary>
    Spawn = 1,
}

/// <summary>
/// <c>logic_level_transition</c>: one per transition room, written by the
/// linker with <c>-mod-entities</c> in place of the room's transition volume.
/// </summary>
/// <remarks>
/// <para>
/// <b>Networking.</b> Server-only: no edict. It stands at the transition
/// volume's centre, which matters only to a reader that wants to show it.
/// </para>
/// <para>
/// <b>Keys.</b> All but <c>targetname</c> are written by the linker:
/// <see cref="DirectionKey"/> (<c>up</c> or <c>down</c>) and
/// <see cref="MapKey"/>, the destination map's name from the level file's
/// <c>up_map</c> or <c>down_map</c>. <see cref="StartDisabledKey"/> is
/// carried from the author's transition volume when it has one. A missing
/// key is its default, never an error.
/// </para>
/// <para>
/// <b>Behaviour.</b> <see cref="TransitionInput"/>, if enabled, fires
/// <see cref="OnTransitionOutput"/> with the activator and then moves the
/// players to <c>map</c>, placing each arriving player at the destination
/// level's arrival point of the opposite role (from <c>down</c>, the
/// destination's up arrival; from <c>up</c>, its down arrival), facing its
/// yaw. The arrival and spawn points are read from the destination map's
/// navigation sidecar (<c>.nav3d</c>); a fresh start uses the up arrival
/// or the spawn points. Which players move is the mod's game rule.
/// </para>
/// <para>
/// <b>Stock fallback.</b> Without <c>-mod-entities</c> the linker writes a
/// <c>trigger_changelevel</c> (the transition volume itself, or the author's
/// hallway <c>trigger_once</c> folded into it) and an <c>info_landmark</c>,
/// so a map built without the mod runs on any Source game.
/// </para>
/// </remarks>
public static class LevelTransition
{
    /// <summary>The class name.</summary>
    public const string ClassName = "logic_level_transition";

    /// <summary>Whether the class takes an edict: it does not (server-only).</summary>
    public const bool Networked = false;

    /// <summary>The name key; the only one an author writes (as <c>cxry_transition</c>).</summary>
    public const string TargetNameKey = "targetname";

    /// <summary>The direction key: <c>up</c> or <c>down</c> (<see cref="Spell(TransitionDirection)"/>).</summary>
    public const string DirectionKey = "direction";

    /// <summary>The destination map's name.</summary>
    public const string MapKey = "map";

    /// <summary>1 to start disabled; 0 by default.</summary>
    public const string StartDisabledKey = "StartDisabled";

    /// <summary>Moves the players to the destination map, if enabled.</summary>
    public const string TransitionInput = "Transition";

    /// <summary>Enables the transition.</summary>
    public const string EnableInput = "Enable";

    /// <summary>Disables the transition.</summary>
    public const string DisableInput = "Disable";

    /// <summary>Fired with the activator just before the level changes.</summary>
    public const string OnTransitionOutput = "OnTransition";

    /// <summary>
    /// The class's FGD entry, every line spelt out: what the mod's FGD holds,
    /// and what a fact holds against these constants and the design's text.
    /// </summary>
    public const string Fgd = """
        @PointClass base(Targetname) = logic_level_transition :
            "Level transition written by ssmap link. Keys other than the name are filled in by the linker."
        [
            direction(choices) : "Direction" : "down" =
            [
                "up" : "Up"
                "down" : "Down"
            ]
            map(string) : "Destination map" : ""
            StartDisabled(choices) : "Start disabled" : 0 =
            [
                0 : "No"
                1 : "Yes"
            ]

            input Transition(void) : "Move the players to the destination map"
            input Enable(void) : "Enable the transition"
            input Disable(void) : "Disable the transition"

            output OnTransition(void) : "Fired just before the level changes"
        ]
        """;

    /// <summary>A direction as the <see cref="DirectionKey"/> spells it: <c>up</c> or <c>down</c>.</summary>
    /// <param name="direction">The direction.</param>
    /// <returns>Its spelling.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Not a direction.</exception>
    public static string Spell(TransitionDirection direction) => direction switch
    {
        TransitionDirection.Up => "up",
        TransitionDirection.Down => "down",
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "a transition goes up or down"),
    };

    /// <summary>A direction from its spelling, ignoring case; false for anything else.</summary>
    /// <param name="text">The spelling.</param>
    /// <param name="direction">The direction.</param>
    /// <returns>Whether it is one.</returns>
    public static bool TryParse(string? text, out TransitionDirection direction)
    {
        direction = default;
        if (string.Equals(text, "up", StringComparison.OrdinalIgnoreCase))
        {
            direction = TransitionDirection.Up;
            return true;
        }

        if (string.Equals(text, "down", StringComparison.OrdinalIgnoreCase))
        {
            direction = TransitionDirection.Down;
            return true;
        }

        return false;
    }

    /// <summary>The opposite role: where a transition in this direction arrives in the destination level.</summary>
    /// <param name="direction">The direction travelled.</param>
    /// <returns>The destination's role whose arrival the player takes.</returns>
    public static TransitionDirection Opposite(TransitionDirection direction) =>
        direction == TransitionDirection.Up ? TransitionDirection.Down : TransitionDirection.Up;

    /// <summary>A point-of-interest type as <c>poi_type</c> spells it: <c>arrival</c> or <c>spawn</c>.</summary>
    /// <param name="type">The type.</param>
    /// <returns>Its spelling.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Not a type of the contract.</exception>
    public static string Spell(PoiType type) => type switch
    {
        PoiType.Arrival => "arrival",
        PoiType.Spawn => "spawn",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "the contract defines arrival and spawn"),
    };
}
