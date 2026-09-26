//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// One cordon from a <c>.vmm_prefs</c> file: its name, whether it is active,
/// and the boxes it selects.
/// </summary>
/// <param name="Name">The cordon's name.</param>
/// <param name="Active">
/// The <c>active</c> key, read the way the reference reads a boolean
/// (<c>ReadKeyValueBool</c>) -- so <c>atoi(value) &gt; 0</c>, and a value of
/// <c>-1</c> is FALSE.
/// </param>
/// <param name="Boxes">
/// The bounding boxes, whose <c>mins</c> and <c>maxs</c> are POINTS in the
/// parenthesised <c>(x y z)</c> spelling, not the bracketed vector one
/// (each corner comes from <c>ReadKeyValuePoint</c>).
/// </param>
public sealed record VmfCordon(
    string Name,
    bool Active,
    IReadOnlyList<(Vec3 Mins, Vec3 Maxs)> Boxes);
