//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// What a room name may be: the one rule shared by the library VMF that
/// names a room, the <c>.room</c> file it is compiled to, and the level
/// file that places it.
/// </summary>
/// <remarks>
/// <para>
/// A room name travels three ways, and each constrains it. It becomes a file
/// name, <c>&lt;name&gt;.room</c>, joined onto an output directory, so it
/// must be exactly one path segment on every host: no separator of either
/// platform, no drive or stream colon, no control character, and not
/// <c>.</c> or <c>..</c>, which would write outside the directory the user
/// named. It is a cell of a level's grid, written <c>name</c> or
/// <c>name@90</c> inside a YAML flow sequence, so it cannot hold the
/// rotation's <c>@</c>, YAML's flow punctuation or whitespace, and it
/// cannot be the empty cell's <c>~</c>.
/// </para>
/// <para>
/// Rather than list what is forbidden, the rule says what is allowed, so a
/// character nobody thought of is refused instead of let through: a name
/// starts with a letter, a digit or an underscore, and continues with
/// letters, digits, underscores, hyphens and dots. Letters and digits are
/// any script's (<c>salle-é</c> is a name); the rule is the same on every
/// host, so a library that compiles on Linux also compiles on Windows.
/// </para>
/// </remarks>
public static class RoomNames
{
    /// <summary>Why a string cannot be a room name, or null when it can.</summary>
    /// <param name="name">The candidate name.</param>
    /// <returns>The problem, worded to follow "the room name ... ", or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public static string? Problem(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0)
        {
            return "is empty";
        }

        if (!char.IsLetterOrDigit(name[0]) && name[0] != '_')
        {
            return $"starts with '{Printable(name[0])}'; a room name starts with a letter, a digit or '_'";
        }

        foreach (char c in name)
        {
            if (!char.IsLetterOrDigit(c) && c is not ('_' or '-' or '.'))
            {
                return $"contains '{Printable(c)}'; a room name holds only letters, digits, '_', '-' and '.'";
            }
        }

        return null;
    }

    /// <summary>A character as a message can show it: control characters by code.</summary>
    private static string Printable(char c) =>
        char.IsControl(c) || char.IsWhiteSpace(c)
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"U+{(int)c:X4}")
            : c.ToString();
}
