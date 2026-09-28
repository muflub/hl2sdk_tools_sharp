//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// One entity output, as an entity key's value holds it:
/// <c>target,input,parameter,delay,times</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>How an output is recognised.</b> In the VMF an output is a key of the
/// entity's <c>connections</c> chunk; in a compiled entity lump it is an
/// ordinary pair, and nothing but the game's own tables (an FGD, which this
/// repository neither has nor reads) says which keys are outputs. So both the
/// link and the flatten recognise an output by its value's shape, with the
/// same function, which is what keeps the two in agreement: five fields,
/// separated by the ESC character (0x1B) when the value holds one, else by
/// commas (the older spelling); a delay that reads as a number and a fire
/// count that reads as a whole number. A value of another shape is an
/// ordinary key. A key that happens to have exactly that shape and is not an
/// output would be read as one; no stock key does.
/// </para>
/// <para>
/// <b>Bytes are kept.</b> The delay and the fire count keep the text they
/// were written with, and <see cref="Format"/> writes the separator the
/// value used, so an output that is only renamed changes only in its name.
/// A delay the fold composes is written shortest-round-trip.
/// </para>
/// </remarks>
/// <param name="Target">The target entity's name (or a special name, a wildcard, a class).</param>
/// <param name="Input">The input the target receives.</param>
/// <param name="Parameter">The parameter, possibly empty.</param>
/// <param name="Delay">The delay in seconds, as written.</param>
/// <param name="Times">How many times the output fires (-1 for always), as written.</param>
/// <param name="Separator">The field separator the value used: ESC or a comma.</param>
internal readonly record struct RoomOutput(string Target, string Input, string Parameter, string Delay, string Times, char Separator)
{
    /// <summary>The separator Hammer writes today.</summary>
    public const char Escape = '\u001b';

    /// <summary>The delay as a number (a value <see cref="TryParse"/> accepted always reads).</summary>
    public float DelaySeconds => float.Parse(Delay, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>The fire count as a number: -1 for always.</summary>
    public int TimesToFire => int.Parse(Times, NumberStyles.Integer, CultureInfo.InvariantCulture);

    /// <summary>Reads an output from a key's value, or false when the value is not shaped like one.</summary>
    /// <param name="value">The value.</param>
    /// <param name="output">The output.</param>
    /// <returns>Whether the value is an output.</returns>
    public static bool TryParse(string? value, out RoomOutput output)
    {
        output = default;
        if (value is null)
        {
            return false;
        }

        char separator = value.Contains(Escape, StringComparison.Ordinal) ? Escape : ',';
        string[] fields = value.Split(separator);
        if (fields.Length != 5
            || !float.TryParse(fields[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float delay)
            || !float.IsFinite(delay)
            || !int.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        output = new RoomOutput(fields[0], fields[1], fields[2], fields[3], fields[4], separator);
        return true;
    }

    /// <summary>The value this output is written as.</summary>
    /// <returns>The five fields joined by the separator.</returns>
    public string Format() => string.Join(Separator, Target, Input, Parameter, Delay, Times);

    /// <summary>A delay composed of two, written shortest-round-trip: what a fold writes for a caller's delay plus a callee's.</summary>
    /// <param name="first">The caller's delay.</param>
    /// <param name="second">The callee's delay.</param>
    /// <returns>The sum's text; the second's own text when the first is zero, so an undelayed call keeps its bytes.</returns>
    public static string AddDelays(RoomOutput first, RoomOutput second)
    {
        float a = first.DelaySeconds;
        if (a == 0)
        {
            return second.Delay;
        }

        float b = second.DelaySeconds;
        return b == 0 ? first.Delay : (a + b).ToString(CultureInfo.InvariantCulture);
    }
}
