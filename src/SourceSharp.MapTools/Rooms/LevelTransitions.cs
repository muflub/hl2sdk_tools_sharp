//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.RoomContracts;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// What a level file says about its transitions (the rooms design, 11.1):
/// which roles it switches off, the maps above and below, and where a level
/// without an up room spawns the player.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keys.</b> <c>up: none</c> and <c>down: none</c> switch a role off for
/// the top or bottom level of a run: the level then holds no room of that
/// role. <c>up_map</c> and <c>down_map</c> name the maps above and below
/// (decision D14, open point O20): a role that is present needs its map, and
/// a role switched off must not have one. <c>spawn: [column, row]</c> names
/// the cell whose room's spawn points a level with <c>up: none</c> starts the
/// player at (open point O21), and <c>spawn_count: K</c> refuses a level
/// whose spawn room has fewer than K spawn points (O23).
/// </para>
/// <para>
/// <b>A format extension.</b> Level files written before transitions never
/// hold these keys, and <see cref="LevelYaml"/> refused every unknown key,
/// so a file with them was never read as something else. A level without
/// any of them (and without a role room) is linked exactly as before.
/// </para>
/// </remarks>
public sealed record LevelTransitions
{
    /// <summary>The level says <c>up: none</c>: it holds no up room (the top level).</summary>
    public bool NoUp { get; init; }

    /// <summary>The level says <c>down: none</c>: it holds no down room (the bottom level).</summary>
    public bool NoDown { get; init; }

    /// <summary>The map above, from <c>up_map</c>, or null.</summary>
    public string? UpMap { get; init; }

    /// <summary>The map below, from <c>down_map</c>, or null.</summary>
    public string? DownMap { get; init; }

    /// <summary>The cell a level with <c>up: none</c> spawns in, from <c>spawn</c>, or null for the default.</summary>
    public (int Column, int Row)? SpawnCell { get; init; }

    /// <summary>The fewest spawn points the level's spawn room must have, from <c>spawn_count</c>, or null.</summary>
    public int? SpawnCount { get; init; }

    /// <summary>Whether a role is switched off.</summary>
    /// <param name="role">The role.</param>
    /// <returns>True for <c>up: none</c> or <c>down: none</c>.</returns>
    public bool IsOff(TransitionDirection role) => role == TransitionDirection.Up ? NoUp : NoDown;

    /// <summary>The map a role leads to, or null.</summary>
    /// <param name="role">The role.</param>
    /// <returns><see cref="UpMap"/> or <see cref="DownMap"/>.</returns>
    public string? MapOf(TransitionDirection role) => role == TransitionDirection.Up ? UpMap : DownMap;

    /// <summary>
    /// Why a map name cannot go into an entity key, or null when it can: it
    /// is written as <c>map</c> and inside landmark names, so it is kept to
    /// the characters a map file name uses.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The problem, worded to follow the name, or null.</returns>
    public static string? MapNameProblem(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "is empty";
        }

        foreach (char c in name)
        {
            if (!(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-' or '.'))
            {
                return "is not a map name: use letters, digits, _, - and . only";
            }
        }

        return null;
    }
}
