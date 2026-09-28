//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.RoomContracts;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// Room-local names: a name that starts with the placeholder <c>cxry_</c>
/// belongs to the room it is in, and becomes <c>c&lt;column&gt;r&lt;row&gt;_…</c>
/// when a level places the room.
/// </summary>
/// <remarks>
/// <para>
/// The rule the rooms design settles for entity names (the grammar is
/// <see cref="RoomNameGrammar"/>, the contract's single spelling of it),
/// applied here to one name at a time: the names of points of interest,
/// which are not entities, so the entity resolver does not see them. A name
/// <c>cxry_guard</c> in the room at column 3, row 5 is <c>c3r5_guard</c>;
/// <c>cx+1ry_guard</c> is the room east of it in the room's own frame, so
/// the offset turns with the room: placed at 90°, the room's east is the
/// level's north and the name is <c>c3r6_guard</c>. Columns and rows count
/// from the south-west cell, from 0, as the level file does.
/// </para>
/// <para>
/// A name that matches the reserved family without being a local name is
/// refused: one that begins like a resolved name (<c>c3r5_…</c>) because it
/// could collide with one, and a near miss of a placeholder
/// (<c>CXRY_a</c>, <c>cx+2ry_a</c>, <c>c1rx_a</c>) because it was almost
/// certainly meant as one. Any other name is global and left exactly as
/// written.
/// </para>
/// </remarks>
public static class RoomLocalNames
{
    /// <summary>The placeholder a room-local name starts with.</summary>
    public const string Placeholder = RoomNameGrammar.Placeholder;

    /// <summary>Why a name is not usable, or null when it is (global, or local and well formed).</summary>
    /// <param name="name">The name as the author wrote it.</param>
    /// <returns>The problem, worded to follow "the name ...", or null.</returns>
    public static string? Problem(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return RoomNameGrammar.Classify(name) switch
        {
            RoomNameKind.Reserved =>
                "looks like a resolved room-local name (c<column>r<row>_); a global name may not, so it cannot collide with one",
            RoomNameKind.Malformed when RoomNameGrammar.TryParseLocal(name + "x", out LocalName bare) && bare.Rest == "x" =>
                "has nothing after its room-local placeholder",
            RoomNameKind.Malformed =>
                $"starts with a room-local placeholder that is not {Placeholder} with optional +1 or -1 offsets in lower case",
            _ => null,
        };
    }

    /// <summary>Whether a name is room-local (starts with a well-formed placeholder).</summary>
    /// <param name="name">The name.</param>
    /// <returns>True for a local name.</returns>
    public static bool IsLocal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return RoomNameGrammar.TryParseLocal(name, out _);
    }

    /// <summary>Resolves a name for a room placed in a cell at a turn.</summary>
    /// <param name="name">The name; a global one comes back unchanged.</param>
    /// <param name="column">The room's column, from the west, 0-based.</param>
    /// <param name="row">The room's row, from the south, 0-based.</param>
    /// <param name="quarterTurns">The placement's counter-clockwise quarter turns.</param>
    /// <returns>The level's name; a neighbour off the grid gets its (negative) cell as it falls.</returns>
    /// <exception cref="ArgumentException">The name is malformed or reserved (<see cref="Problem"/>).</exception>
    public static string Resolve(string name, int column, int row, int quarterTurns)
    {
        if (Problem(name) is { } problem)
        {
            throw new ArgumentException($"the name \"{name}\" {problem}.", nameof(name));
        }

        if (!RoomNameGrammar.TryParseLocal(name, out LocalName local))
        {
            return name;
        }

        // The offset turns as geometry turns, by the one rotation the
        // placement's transform uses, so names and positions cannot disagree.
        (int dx, int dy) = RoomNameAnalysis.Turn(local.Dx, local.Dy, quarterTurns);
        return string.Create(CultureInfo.InvariantCulture, $"c{column + dx}r{row + dy}_{local.Rest}");
    }
}
