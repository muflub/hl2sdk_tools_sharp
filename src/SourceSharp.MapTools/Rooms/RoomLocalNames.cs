//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// Room-local names: a name that starts with the placeholder <c>cxry_</c>
/// belongs to the room it is in, and becomes <c>c&lt;column&gt;r&lt;row&gt;_…</c>
/// when a level places the room.
/// </summary>
/// <remarks>
/// <para>
/// The rule the rooms design settles for entity names, applied here to the
/// names of points of interest (which are not entities, so nothing else
/// resolves them). A name <c>cxry_guard</c> in the room at column 3, row 5
/// is <c>c3r5_guard</c>; <c>cx+1ry_guard</c> is the room east of it in the
/// room's own frame, so the offset turns with the room: placed at 90°, the
/// room's east is the level's north and the name is <c>c3r6_guard</c>.
/// Columns and rows count from the south-west cell, from 0, as the level
/// file does.
/// </para>
/// <para>
/// Grammar: <c>c x [±1] r y [±1] _ rest</c>, lower case, offsets only
/// <c>+1</c> or <c>-1</c>, and <c>rest</c> non-empty and kept as written. A
/// name that starts with the placeholder in another case or with another
/// offset (<c>CXRY_a</c>, <c>cx+2ry_a</c>) is refused as malformed rather
/// than taken as a global name, because it was almost certainly meant as a
/// local one. A global name may not look like a resolved one
/// (<c>c3r5_…</c>), so a resolved name never collides with a global one.
/// Any other name is global and left exactly as written. Parsed by hand,
/// not by a regular expression, so the library carries no regex state.
/// </para>
/// </remarks>
public static class RoomLocalNames
{
    /// <summary>The placeholder a room-local name starts with.</summary>
    public const string Placeholder = "cxry_";

    /// <summary>Why a name is not usable, or null when it is (global, or local and well formed).</summary>
    /// <param name="name">The name as the author wrote it.</param>
    /// <returns>The problem, worded to follow "the name ...", or null.</returns>
    public static string? Problem(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (Parse(name, strict: false, out _, out _) is int looseLength)
        {
            if (Parse(name, strict: true, out _, out _) is null)
            {
                return $"starts with a room-local placeholder that is not {Placeholder} with optional +1 or -1 offsets in lower case";
            }

            return name.Length == looseLength ? "has nothing after its room-local placeholder" : null;
        }

        return LooksResolved(name)
            ? "looks like a resolved room-local name (c<column>r<row>_); a global name may not, so it cannot collide with one"
            : null;
    }

    /// <summary>Whether a name is room-local (starts with a well-formed placeholder).</summary>
    /// <param name="name">The name.</param>
    /// <returns>True for a local name.</returns>
    public static bool IsLocal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Parse(name, strict: true, out _, out _) is not null;
    }

    /// <summary>Resolves a name for a room placed in a cell at a turn.</summary>
    /// <param name="name">The name; a global one comes back unchanged.</param>
    /// <param name="column">The room's column, from the west, 0-based.</param>
    /// <param name="row">The room's row, from the south, 0-based.</param>
    /// <param name="quarterTurns">The placement's counter-clockwise quarter turns.</param>
    /// <returns>The level's name.</returns>
    /// <exception cref="ArgumentException">The name is malformed (<see cref="Problem"/>).</exception>
    public static string Resolve(string name, int column, int row, int quarterTurns)
    {
        if (Problem(name) is { } problem)
        {
            throw new ArgumentException($"the name \"{name}\" {problem}.", nameof(name));
        }

        if (Parse(name, strict: true, out int dx, out int dy) is not int length)
        {
            return name;
        }

        for (int t = 0; t < ((quarterTurns % 4) + 4) % 4; t++)
        {
            (dx, dy) = (-dy, dx);
        }

        return string.Create(CultureInfo.InvariantCulture, $"c{column + dx}r{row + dy}_{name[length..]}");
    }

    /// <summary>
    /// The placeholder's length when the name starts with one: <c>c x [off] r y [off] _</c>.
    /// Strictly, lower case with offsets +1 or -1; loosely, any case and any
    /// signed number, which is how a malformed placeholder is recognised.
    /// </summary>
    private static int? Parse(string name, bool strict, out int dx, out int dy)
    {
        dx = dy = 0;
        int at = 0;
        if (!Letter(name, ref at, 'c', strict) || !Letter(name, ref at, 'x', strict)
            || !Offset(name, ref at, strict, out dx)
            || !Letter(name, ref at, 'r', strict) || !Letter(name, ref at, 'y', strict)
            || !Offset(name, ref at, strict, out dy)
            || !Letter(name, ref at, '_', strict))
        {
            return null;
        }

        return at;
    }

    private static bool Letter(string name, ref int at, char letter, bool exactCase)
    {
        if (at < name.Length && (name[at] == letter || (!exactCase && char.ToLowerInvariant(name[at]) == letter)))
        {
            at++;
            return true;
        }

        return false;
    }

    private static bool Offset(string name, ref int at, bool strict, out int offset)
    {
        offset = 0;
        if (at >= name.Length || name[at] is not ('+' or '-'))
        {
            return true;
        }

        int sign = name[at] == '+' ? 1 : -1;
        int end = at + 1;
        while (end < name.Length && char.IsAsciiDigit(name[end]))
        {
            end++;
        }

        string number = name[(at + 1)..end];
        if (strict && number != "1")
        {
            return false;
        }

        offset = sign;
        at = end;
        return true;
    }

    /// <summary>Whether a name starts like a resolved one: <c>c</c>, digits, <c>r</c>, digits, <c>_</c>, any case, digits possibly negative.</summary>
    private static bool LooksResolved(string name)
    {
        int at = 0;
        return Letter(name, ref at, 'c', false) && Digits(name, ref at) && Letter(name, ref at, 'r', false) && Digits(name, ref at)
            && Letter(name, ref at, '_', false);
    }

    private static bool Digits(string name, ref int at)
    {
        if (at < name.Length && name[at] == '-')
        {
            at++;
        }

        int start = at;
        while (at < name.Length && char.IsAsciiDigit(name[at]))
        {
            at++;
        }

        return at > start;
    }
}
