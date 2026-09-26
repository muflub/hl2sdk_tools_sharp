//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Bsp;

/// <summary>
/// Which brushes a stage is interested in: <c>detailscreen_e</c>.
/// </summary>
/// <remarks>
/// <para>
/// Lives in <c>Bsp</c> rather than in <c>Bsp.Csg</c> or <c>Bsp.Portals</c>
/// because both of them screen brush lists with it and stock declares it once,
/// In the reference implementation, above either stage.
/// </para>
/// <para>
/// Phases 3b and 3c each defined their own copy, in their own namespace, with
/// the same three values under different member names. That compiled cleanly --
/// two namespaces, two distinct types, no error anywhere -- which is exactly
/// why it is worth a remark: a duplicated enum does not announce itself, and
/// the first symptom would have been a cast or an overload resolving to the
/// wrong stage's type. The member names here are stock's
/// (<c>FULL_DETAIL</c>, <c>ONLY_DETAIL</c>, <c>NO_DETAIL</c>).
/// </para>
/// </remarks>
public enum DetailScreen
{
    /// <summary>Every brush, detail or not: <c>FULL_DETAIL</c>, 0.</summary>
    FullDetail = 0,

    /// <summary>Only <c>CONTENTS_DETAIL</c> brushes: <c>ONLY_DETAIL</c>, 1.</summary>
    OnlyDetail = 1,

    /// <summary>Everything except detail: <c>NO_DETAIL</c>, 2.</summary>
    NoDetail = 2,
}
