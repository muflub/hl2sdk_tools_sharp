//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Immutable;

namespace SourceSharp.MapTools.Compare;

/// <summary>
/// One named difference between the two maps.
/// </summary>
/// <param name="Subject">
/// What differs, spelled so it can be found again: <c>entity 41 "light_spot"
/// key "_light" #0</c>, <c>texinfo[912]</c>, <c>pak "materials/x.vmt"</c>.
/// </param>
/// <param name="InA">What map A has there.</param>
/// <param name="InB">What map B has there.</param>
public readonly record struct DiffDifference(string Subject, string InA, string InB)
{
    /// <summary>This difference as one line of the report.</summary>
    /// <returns>The line.</returns>
    public string ToText() => $"{Subject}: A={InA} B={InB}";
}

/// <summary>
/// What one canonicalised multiset has that the other does not.
/// </summary>
/// <remarks>
/// The port's plan §2 asks for what is in one and not the other rather than a
/// count, because a count of 12 says nothing about whether one plane moved or
/// twelve did. Both are carried: the counts are complete, and the samples are
/// bounded by <see cref="DiffOptions.MaxReportedItems"/> so a pair of unrelated
/// maps cannot produce a report the size of the maps.
/// </remarks>
public sealed class SetDifference
{
    internal SetDifference(
        int onlyInACount,
        int onlyInBCount,
        ImmutableArray<string> onlyInA,
        ImmutableArray<string> onlyInB,
        int commonCount)
    {
        OnlyInACount = onlyInACount;
        OnlyInBCount = onlyInBCount;
        OnlyInA = onlyInA;
        OnlyInB = onlyInB;
        CommonCount = commonCount;
    }

    /// <summary>How many elements, counting repeats, A has that B does not.</summary>
    public int OnlyInACount { get; }

    /// <summary>How many elements, counting repeats, B has that A does not.</summary>
    public int OnlyInBCount { get; }

    /// <summary>How many elements, counting repeats, the two share.</summary>
    public int CommonCount { get; }

    /// <summary>
    /// Canonical keys present in A and not in B, up to
    /// <see cref="DiffOptions.MaxReportedItems"/>.
    /// </summary>
    public ImmutableArray<string> OnlyInA { get; }

    /// <summary>
    /// Canonical keys present in B and not in A, up to
    /// <see cref="DiffOptions.MaxReportedItems"/>.
    /// </summary>
    public ImmutableArray<string> OnlyInB { get; }

    /// <summary>Whether the two multisets agree exactly.</summary>
    public bool Identical => OnlyInACount == 0 && OnlyInBCount == 0;

    /// <summary>Whether the sample lists leave some differences unnamed.</summary>
    public bool Truncated => OnlyInA.Length < OnlyInACount || OnlyInB.Length < OnlyInBCount;
}
