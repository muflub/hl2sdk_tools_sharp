//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapGen.Catalog;

/// <summary>
/// Which stock map compiler owns a feature.
///
/// <para>
/// The catalogue is graded by the tool a failure would land in, because that is
/// what decides which porting phase has to have arrived before an
/// entry can be asserted at all. A vvis feature is assertable in Phase 2; a vrad
/// one is not, and saying so is the difference between a listed gap and silence.
/// </para>
/// </summary>
public enum CompileTool
{
    /// <summary>Geometry, structure, surfaces, content and collision — `vbsp`.</summary>
    Vbsp,

    /// <summary>Visibility — `vvis`.</summary>
    Vvis,

    /// <summary>Lighting — `vrad`.</summary>
    Vrad,
}
