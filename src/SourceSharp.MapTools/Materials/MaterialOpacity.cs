//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Materials;

/// <summary>
/// <c>UTILMATLIB_OPACITY</c>'s three answers
/// </summary>
/// <remarks>
/// The order matters and is the reference build's: translucent is tested FIRST, so a
/// material that is both translucent and alpha-tested reports translucent.
/// </remarks>
public enum MaterialOpacity
{
    /// <summary><c>UTILMATLIB_OPAQUE</c>.</summary>
    Opaque = 0,

    /// <summary><c>UTILMATLIB_ALPHATEST</c>.</summary>
    AlphaTest,

    /// <summary><c>UTILMATLIB_TRANSLUCENT</c>.</summary>
    Translucent,
}
