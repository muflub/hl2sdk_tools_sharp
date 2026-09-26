//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Bsp.Tree;

/// <summary>
/// Where a brush sits relative to a plane: <c>PSIDE_*</c>.
/// </summary>
/// <remarks>
/// <para>
/// Plain <see cref="int"/> constants rather than a <c>[Flags]</c> enum, and
/// that is on purpose. Stock stores these in <c>bspbrush_t::side</c> and
/// <c>::testside</c>, which are <c>int</c>, and compares them with both
/// <c>&amp;</c> AND <c>==</c> — <c>SelectSplitSide</c> counts
/// <c>s == PSIDE_BOTH</c> while
/// <c>SplitBrushList</c> tests <c>sides == PSIDE_BOTH</c> and then
/// <c>sides &amp; PSIDE_FRONT</c>.
/// The equality comparisons mean the value is a four-state code as often as it
/// is a bit set, and wrapping that in an enum would invite someone to add a
/// fifth bit and quietly change what <c>== Both</c> means.
/// </para>
/// <para>
/// <c>PSIDE_FACING</c> is the odd one out: it says the brush HAS the plane as
/// one of its own sides, so it is orthogonal to front/back rather than a third
/// position. A facing brush always comes back with exactly one of front or back
/// set, never both, and <c>SelectSplitSide</c> errors out if a facing result
/// Also reported splits.
/// </para>
/// </remarks>
public static class PlaneSideFlags
{
    /// <summary>The brush is in front of the plane: <c>PSIDE_FRONT</c>, 1.</summary>
    public const int Front = 1;

    /// <summary>The brush is behind the plane: <c>PSIDE_BACK</c>, 2.</summary>
    public const int Back = 2;

    /// <summary>The brush crosses the plane: <c>PSIDE_BOTH</c>, 3.</summary>
    public const int Both = Front | Back;

    /// <summary>
    /// The brush has this plane as one of its own sides: <c>PSIDE_FACING</c>,
    /// </summary>
    public const int Facing = 4;
}
