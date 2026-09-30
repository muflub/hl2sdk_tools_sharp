//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Immutable;
using System.Text;

namespace SourceSharp.RoomContracts;

/// <summary>
/// The level map overlay's side of the contract (the rooms design, 18.5):
/// the keys an author writes for the map, the marker kinds the linker
/// writes itself, and the rules a kind and a label follow.
/// </summary>
/// <remarks>
/// <para>
/// <b>The file.</b> <c>ssmap link</c> writes <c>&lt;map&gt;.map2d</c>
/// beside the <c>.bsp</c> (the format is <c>docs/map2d-format.md</c>); the
/// mod loads it when the map loads, checks that it was made for that map
/// (its checksum), and draws the overlay. Whether and how rooms are
/// revealed is the game's choice: the file names each polygon's, door's and
/// marker's placement.
/// </para>
/// <para>
/// <b>Author keys.</b> An <c>info_poi</c> with <see cref="MarkerKey"/> is a
/// marker on the map, of that kind, at the point's position and yaw, with
/// its optional <see cref="LabelKey"/>; a point without the key stays a
/// navigation point only. An <c>info_room</c> may give its room a
/// <see cref="LabelKey"/>, which the map carries per placement.
/// </para>
/// <para>
/// <b>Kinds</b> are short identifiers (<see cref="IsKind"/>) the game maps
/// to icons; the linker's own (<see cref="LinkerKinds"/>) are reserved, so
/// an author cannot write a marker the game would take for the level spawn.
/// </para>
/// </remarks>
public static class LevelMap
{
    /// <summary>The <c>info_poi</c> key naming a marker's kind.</summary>
    public const string MarkerKey = "map_marker";

    /// <summary>The <c>info_poi</c> and <c>info_room</c> key holding a display label.</summary>
    public const string LabelKey = "map_label";

    /// <summary>The marker at the level spawn: where a fresh start puts the player.</summary>
    public const string SpawnKind = "spawn";

    /// <summary>A marker at a transition room's arrival point: where a player from another level appears.</summary>
    public const string ArrivalKind = "arrival";

    /// <summary>The marker at the up room's transition: the way to the level above.</summary>
    public const string ExitUpKind = "exit_up";

    /// <summary>The marker at the down room's transition: the way to the level below.</summary>
    public const string ExitDownKind = "exit_down";

    /// <summary>The longest kind, in characters.</summary>
    public const int MaxKindLength = 32;

    /// <summary>The longest label, in bytes of UTF-8.</summary>
    public const int MaxLabelBytes = 64;

    /// <summary>The file extension of the map.</summary>
    public const string Extension = ".map2d";

    /// <summary>The kinds the linker writes itself, which an author may not use.</summary>
    public static ImmutableArray<string> LinkerKinds { get; } = [SpawnKind, ArrivalKind, ExitUpKind, ExitDownKind];

    /// <summary>
    /// Whether a text is a marker kind: a lower-case ASCII letter, then up to
    /// <see cref="MaxKindLength"/> - 1 more lower-case letters, digits and
    /// underscores.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>Whether it is a kind.</returns>
    public static bool IsKind(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxKindLength || text[0] is < 'a' or > 'z')
        {
            return false;
        }

        foreach (char c in text)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a text is a label: no NUL, and at most <see cref="MaxLabelBytes"/> bytes of UTF-8.</summary>
    /// <param name="text">The text.</param>
    /// <returns>Whether it is a label.</returns>
    public static bool IsLabel(string? text) =>
        text is not null && !text.Contains('\0', StringComparison.Ordinal) && Encoding.UTF8.GetByteCount(text) <= MaxLabelBytes;
}
