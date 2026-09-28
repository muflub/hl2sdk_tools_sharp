//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The 3x3 rooms sweep: the equivalence checks over many more arrangements
/// than the default subset, up to all 85,088 of them. Skipped unless
/// <c>SSMAP_ROOMS3X3_SWEEP</c> asks for it, because it is hours of CPU.
/// </summary>
/// <remarks>
/// <para>
/// The variable is <c>all</c> for every valid arrangement, or a positive
/// count for that many spread evenly through the enumeration (arrangement
/// <c>i · total / count</c> for <c>i</c> from 0). Anything else is not a
/// request, and the fact skips saying so. Documented in the sample's README
/// (<c>samples/rooms-3x3/README.md</c>, "The exhaustive sweep").
/// </para>
/// <para>
/// Each arrangement costs one link, one monolithic vbsp and vvis compile and
/// the checks, about a quarter of a second of one core here: all of them is roughly
/// five to six hours, a count of 1,000 about four minutes.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class Rooms3x3SweepFactAttribute : FactAttribute
{
    /// <summary>The variable that asks for the sweep.</summary>
    public const string Variable = "SSMAP_ROOMS3X3_SWEEP";

    public Rooms3x3SweepFactAttribute()
    {
        if (Requested(Environment.GetEnvironmentVariable(Variable)) is null)
        {
            Skip = $"the 3x3 rooms sweep is hours of CPU: set {Variable}=all, or to a count of arrangements, to run it "
                + "(samples/rooms-3x3/README.md)";
        }
    }

    /// <summary>
    /// How many arrangements a value of the variable asks for:
    /// <see cref="int.MaxValue"/> for <c>all</c>, the count for a positive
    /// integer, null for anything else.
    /// </summary>
    public static int? Requested(string? value) =>
        string.Equals(value?.Trim(), "all", StringComparison.OrdinalIgnoreCase) ? int.MaxValue
        : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int count) && count > 0 ? count
        : null;
}
