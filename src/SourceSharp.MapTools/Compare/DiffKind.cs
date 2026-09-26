//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Compare;

/// <summary>
/// How one lump is compared between two maps.
/// </summary>
/// <remarks>
/// <para>
/// The BSP comparison instrument names three kinds, and the reason there are three is
/// that "the same map" means a different thing per lump. A lump with no
/// floating-point freedom must match byte for byte; a lump whose INDEX ORDER is
/// an artefact of the compiler's insertion sequence must match as a set; and a
/// lump the reference tool cannot even reproduce against itself can only be
/// described by statistics.
/// </para>
/// <para>
/// <see cref="NotCompared"/> is the fourth, and it exists so the report is TOTAL
/// over all 64 lump slots. A lump this instrument has no semantic comparison for
/// is said out loud, with a reason, rather than being left out -- an instrument
/// that silently skips a lump reads exactly like one that found no difference.
/// </para>
/// </remarks>
public enum DiffKind
{
    /// <summary>
    /// No semantic comparison. The lump's raw bytes are still compared, and
    /// <see cref="LumpDiff.Note"/> says why nothing more was done.
    /// </summary>
    NotCompared = 0,

    /// <summary>
    /// The lump has no float freedom: any difference at all is a difference.
    /// </summary>
    Exact,

    /// <summary>
    /// The lump's element ORDER is an output of the compiler, not a property of
    /// the map, so it is compared as a multiset of canonical keys.
    /// </summary>
    CanonicalSet,

    /// <summary>
    /// The lump can only be described: a bit-difference count, or an error
    /// histogram.
    /// </summary>
    Distributional,
}
