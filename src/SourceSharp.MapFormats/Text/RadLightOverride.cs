//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// One texlight that a later <c>.rad</c> file redefined.
/// </summary>
/// <param name="Name">The material name.</param>
/// <param name="Previous">What the intensity was.</param>
/// <param name="Current">What it became.</param>
/// <param name="SameFile">
/// True when both definitions came from the SAME file, which vrad reports as an
/// outright error -- <c>"ERROR\a: Duplication of '%s' in file '%s'!"</c>,
/// complete with an embedded BEL. It is
/// a <c>Msg</c>, not an <c>Error</c>, so the compile continues.
/// </param>
/// <param name="Redundant">
/// True when the two definitions agree, which vrad reports as
/// <c>"Warning: Redundant '%s' def in '%s' AND '%s'!"</c>
/// rather than as an override.
/// </param>
public sealed record RadLightOverride(
    string Name,
    Vec3 Previous,
    Vec3 Current,
    bool SameFile,
    bool Redundant);
