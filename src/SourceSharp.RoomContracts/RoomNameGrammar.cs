//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

namespace SourceSharp.RoomContracts;

/// <summary>What a name is, by the room-local naming grammar.</summary>
public enum RoomNameKind
{
    /// <summary>A global name: left exactly as written.</summary>
    Global = 0,

    /// <summary>A room-local name (<see cref="RoomNameGrammar.LocalPattern"/>), resolved at link.</summary>
    Local = 1,

    /// <summary>A name that begins like one the linker writes (<see cref="RoomNameGrammar.ResolvedPattern"/>): refused.</summary>
    Reserved = 2,

    /// <summary>A near miss of a placeholder (<see cref="RoomNameGrammar.SuspectPattern"/>): refused.</summary>
    Malformed = 3,
}

/// <summary>A room-local name taken apart.</summary>
/// <param name="Dx">The authored column offset: -1, 0 or +1 (<c>cx+1</c> is +1).</param>
/// <param name="Dy">The authored row offset: -1, 0 or +1 (<c>ry-1</c> is -1).</param>
/// <param name="PrefixLength">The placeholder's length, up to and including its underscore.</param>
/// <param name="Rest">What follows the placeholder, as written (never empty).</param>
public readonly record struct LocalName(int Dx, int Dy, int PrefixLength, string Rest);

/// <summary>
/// The room-local naming grammar: how a library author marks a name as
/// belonging to the room it is in, and what the linker turns it into.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule.</b> A name that starts with the placeholder <c>cxry_</c>
/// is the room's own: <c>cxry_door</c> in the room at column 3, row 5
/// becomes <c>c3r5_door</c>. Offsets of one name a neighbour's:
/// <c>cx+1ry_door</c> is the room beyond this room's authored east side,
/// <c>cxry-1_door</c> the one beyond its south side, and the diagonals
/// combine both (<c>cx+1ry-1_door</c>). The offsets are in the room's own
/// frame and turn with its placement. Columns and rows count from the
/// south-west cell, from 0. Any other name is global and left as written.
/// </para>
/// <para>
/// <b>The reserved family.</b> Three regular expressions (.NET syntax) are
/// the single spelling of the rule: <see cref="LocalPattern"/> (case
/// matters), <see cref="ResolvedPattern"/> (applied ignoring case to
/// authored names) and <see cref="SuspectPattern"/>. A name matching the
/// first is local; else one matching the second is refused as reserved,
/// because a global name that begins like a resolved one could collide with
/// it; else one matching the third is refused as malformed, because a typo
/// in a placeholder (<c>cx+2ry_</c>, <c>CXRY_</c>, <c>cxr_</c>) must never
/// quietly become a global name. The price is that a few ordinary names are
/// refused too (<c>c4rocket</c>), and the author renames them.
/// </para>
/// <para>
/// <b>One widening.</b> <see cref="SuspectPattern"/> also takes a sign
/// before the digit (<c>c-1r0_</c>, <c>c+3r5_</c>): those are the forms an
/// offset would produce off the grid or spelt by hand, and refusing them
/// keeps every prefix with a sign in it out of the global names.
/// </para>
/// <para>
/// <b>Parsed by hand.</b> The patterns are constants for anyone who wants
/// them (the mod's debug tools, the facts); the methods here are a
/// hand-written parser that facts hold to the patterns over a corpus, so
/// this assembly carries no regular-expression state and costs nothing to
/// load. Only lower-case <c>cx</c>/<c>ry</c> and offsets <c>+1</c> and
/// <c>-1</c> are local; the rest of a name keeps its case. A line feed is
/// never part of a name (entity values cannot hold one), and the parser
/// treats a name that holds one as not local.
/// </para>
/// </remarks>
public static class RoomNameGrammar
{
    /// <summary>The placeholder a room-local name starts with, with no offsets.</summary>
    public const string Placeholder = "cxry_";

    /// <summary>A room-local name, case-sensitive: the placeholder with optional offsets, then a non-empty rest.</summary>
    public const string LocalPattern = @"^cx(?<dx>[+-]1)?ry(?<dy>[+-]1)?_(?<rest>.+)$";

    /// <summary>What the linker writes: <c>c</c>, the column, <c>r</c>, the row, <c>_</c>; no leading zeros.</summary>
    public const string ResolvedPattern = @"^c(?<col>0|[1-9][0-9]*)r(?<row>0|[1-9][0-9]*)_";

    /// <summary>Anything that starts like a placeholder or a resolved name: <c>c</c>, then <c>x</c> or a (signed) digit, then an <c>r</c> before the first underscore.</summary>
    public const string SuspectPattern = @"(?i)^c(?:x|[+-]?[0-9])[^_]*r";

    /// <summary>What a name is.</summary>
    /// <param name="name">The name as the author wrote it.</param>
    /// <returns>Its kind, by the three patterns in order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public static RoomNameKind Classify(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (TryParseLocal(name, out _))
        {
            return RoomNameKind.Local;
        }

        if (IsResolvedForm(name))
        {
            return RoomNameKind.Reserved;
        }

        return IsSuspect(name) ? RoomNameKind.Malformed : RoomNameKind.Global;
    }

    /// <summary>A room-local name taken apart, or false when the name is not one (<see cref="LocalPattern"/>).</summary>
    /// <param name="name">The name.</param>
    /// <param name="local">The offsets, the placeholder's length and the rest.</param>
    /// <returns>Whether the name is local.</returns>
    public static bool TryParseLocal(string? name, out LocalName local)
    {
        local = default;
        if (name is null || name.Length < 2 || name[0] != 'c' || name[1] != 'x')
        {
            return false;
        }

        int at = 2;
        int dx = Offset(name, ref at);
        if (at + 1 >= name.Length || name[at] != 'r' || name[at + 1] != 'y')
        {
            return false;
        }

        at += 2;
        int dy = Offset(name, ref at);
        if (at >= name.Length || name[at] != '_')
        {
            return false;
        }

        at++;
        if (at >= name.Length || name.IndexOf('\n', at) >= 0)
        {
            return false;
        }

        local = new LocalName(dx, dy, at, name[at..]);
        return true;
    }

    /// <summary>Whether a name begins like a resolved name, ignoring case (<see cref="ResolvedPattern"/>).</summary>
    /// <param name="name">The name.</param>
    /// <returns>True when it does.</returns>
    public static bool IsResolvedForm(string? name) => ResolvedPrefix(name, ignoreCase: true, out _, out _) > 0;

    /// <summary>Whether a name begins like a placeholder or a resolved name (<see cref="SuspectPattern"/>).</summary>
    /// <param name="name">The name.</param>
    /// <returns>True when it does.</returns>
    public static bool IsSuspect(string? name)
    {
        if (name is null || name.Length < 2 || !Is(name[0], 'c'))
        {
            return false;
        }

        int at;
        if (Is(name[1], 'x') || char.IsAsciiDigit(name[1]))
        {
            at = 2;
        }
        else if (name[1] is '+' or '-' && name.Length > 2 && char.IsAsciiDigit(name[2]))
        {
            at = 3;
        }
        else
        {
            return false;
        }

        for (; at < name.Length && name[at] != '_'; at++)
        {
            if (Is(name[at], 'r'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a value holds a placeholder after its start (<c>door_cxry</c>,
    /// <c>OnTrigger cxry_door:Open::0:-1</c>): the grammar only reads a
    /// placeholder at the start of a name, so this is a global name or text
    /// that looks like a misplaced one, which the pack warns of.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>True when <c>cx</c>, an optional offset, <c>ry</c> appear anywhere but at the start.</returns>
    public static bool HasPlaceholderAfterStart(string? value)
    {
        // Shorter than "?cxry" holds no placeholder after its start (and an
        // empty value has no index 1 to search from).
        if (value is null || value.Length < 5)
        {
            return false;
        }

        for (int i = value.IndexOf("cx", 1, StringComparison.Ordinal); i > 0; i = value.IndexOf("cx", i + 1, StringComparison.Ordinal))
        {
            int at = i + 2;
            _ = Offset(value, ref at);
            if (at + 1 < value.Length && value[at] == 'r' && value[at + 1] == 'y')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The prefix the linker writes for a cell: <c>c&lt;column&gt;r&lt;row&gt;_</c>.</summary>
    /// <param name="column">The cell's column, from the west, 0-based.</param>
    /// <param name="row">The cell's row, from the south, 0-based.</param>
    /// <returns>The prefix.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A negative column or row, which no cell has.</exception>
    public static string ResolvedPrefix(int column, int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        return string.Create(CultureInfo.InvariantCulture, $"c{column}r{row}_");
    }

    /// <summary>A resolved name: the cell's prefix and the rest (<c>c3r5_door</c>).</summary>
    /// <param name="column">The cell's column.</param>
    /// <param name="row">The cell's row.</param>
    /// <param name="rest">What followed the placeholder.</param>
    /// <returns>The name.</returns>
    public static string Resolve(int column, int row, string rest)
    {
        ArgumentNullException.ThrowIfNull(rest);
        return ResolvedPrefix(column, row) + rest;
    }

    /// <summary>A name as the linker writes it taken apart, or false when it is not one: lower case, no leading zeros.</summary>
    /// <param name="name">The name.</param>
    /// <param name="column">The column.</param>
    /// <param name="row">The row.</param>
    /// <param name="rest">What follows the prefix (may be empty).</param>
    /// <returns>Whether the name is a resolved one.</returns>
    public static bool TryParseResolved(string? name, out int column, out int row, out string rest)
    {
        int length = ResolvedPrefix(name, ignoreCase: false, out column, out row);
        rest = length > 0 ? name![length..] : string.Empty;
        return length > 0;
    }

    /// <summary>The length of a resolved prefix at the start of the name, or 0.</summary>
    private static int ResolvedPrefix(string? name, bool ignoreCase, out int column, out int row)
    {
        column = row = 0;
        if (name is null || name.Length == 0 || !(ignoreCase ? Is(name[0], 'c') : name[0] == 'c'))
        {
            return 0;
        }

        int at = 1;
        if (!Number(name, ref at, out column)
            || at >= name.Length || !(ignoreCase ? Is(name[at], 'r') : name[at] == 'r'))
        {
            return 0;
        }

        at++;
        if (!Number(name, ref at, out row) || at >= name.Length || name[at] != '_')
        {
            return 0;
        }

        return at + 1;
    }

    /// <summary>A number with no leading zero (<c>0</c> alone is one), at most nine digits so it fits.</summary>
    private static bool Number(string name, ref int at, out int value)
    {
        value = 0;
        int start = at;
        while (at < name.Length && char.IsAsciiDigit(name[at]))
        {
            at++;
        }

        int digits = at - start;
        if (digits == 0 || (digits > 1 && name[start] == '0'))
        {
            return false;
        }

        // A longer run is still the resolved form (the pattern has no
        // bound); it names no cell a level can have, so its value saturates.
        value = digits > 9 ? int.MaxValue : int.Parse(name.AsSpan(start, digits), NumberStyles.None, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>An optional <c>+1</c> or <c>-1</c> at <paramref name="at"/>: its value, advancing past it; 0 and no advance otherwise.</summary>
    private static int Offset(string name, ref int at)
    {
        if (at + 1 < name.Length && name[at] is '+' or '-' && name[at + 1] == '1')
        {
            int offset = name[at] == '+' ? 1 : -1;
            at += 2;
            return offset;
        }

        return 0;
    }

    /// <summary>A letter compared ignoring case, as the pattern's <c>(?i)</c> does for these three.</summary>
    private static bool Is(char c, char lower) => c == lower || c == char.ToUpperInvariant(lower);
}
