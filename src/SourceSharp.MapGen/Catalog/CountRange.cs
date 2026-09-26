//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapGen.Catalog;

/// <summary>
/// A declared count, which is sometimes exact and sometimes only bounded.
///
/// <para>
/// Both halves earn their keep. "This map has four world brushes" is exact and
/// the generator can be held to it today. "This map has at least three clusters"
/// is the honest form of a vvis expectation that nobody has measured yet:
/// writing a made-up exact number would produce a fact that fails for the wrong
/// reason the day the compiler is right.
/// </para>
/// </summary>
/// <param name="Min">The smallest acceptable count.</param>
/// <param name="Max">The largest acceptable count, or null for unbounded.</param>
public readonly record struct CountRange(int Min, int? Max)
{
    /// <summary>A count that is known exactly.</summary>
    /// <param name="n">The count.</param>
    public static CountRange Exactly(int n) => new(n, n);

    /// <summary>A lower bound and nothing more.</summary>
    /// <param name="n">The smallest acceptable count.</param>
    public static CountRange AtLeast(int n) => new(n, null);

    /// <summary>A closed interval.</summary>
    /// <param name="min">The smallest acceptable count.</param>
    /// <param name="max">The largest acceptable count.</param>
    public static CountRange Between(int min, int max) => new(min, max);

    /// <summary>Whether the declaration fixes one value.</summary>
    public bool IsExact => Max == Min;

    /// <summary>Whether a measured count satisfies the declaration.</summary>
    /// <param name="value">What was measured.</param>
    public bool Contains(int value) => value >= Min && (Max is null || value <= Max);

    /// <summary>How the coverage report spells it.</summary>
    public override string ToString()
        => IsExact ? Min.ToString(System.Globalization.CultureInfo.InvariantCulture)
         : Max is null ? $">= {Min}"
         : $"{Min}..{Max}";
}
