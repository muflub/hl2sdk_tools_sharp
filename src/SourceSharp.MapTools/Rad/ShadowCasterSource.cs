//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Rad;

/// <summary>
/// Which of vrad's five load-path calls contributed a caster triangle.
/// </summary>
/// <remarks>
/// <para>
/// NOT DERIVABLE FROM THE TRIANGLE, which is why it is recorded rather than
/// computed. World brushes and displacements both carry
/// <see cref="TraceId.Opaque"/> and nothing else, so a finished caster set
/// cannot say which produced a given triangle -- and those two are the pair
/// most worth separating, because they are built by completely different code
/// (winding clipping against brush planes, versus a tessellated height field).
/// </para>
/// <para>
/// Stock can get away without it because stock never checks its own load
/// against anything. This port does: the per-source counts and bounds below
/// are the 4b gate, and they are compared against stock's own
/// <c>-dumptrace</c> output, which separates the same five runs by their
/// position in the file (the add order at <c>, 2277, 2278,
/// 2279</c> is fixed).
/// </para>
/// </remarks>
public enum ShadowCasterSource
{
    /// <summary>
    /// A brush of an entity carrying <c>vrad_brush_cast_shadows</c>
    /// Transformed by the entity's origin and angles.
    /// </summary>
    /// <remarks>
    /// First in the list, because <c>ExtractBrushEntityShadowCasters</c> runs
    /// -- thirty-seven lines before the world's own
    /// brushes.
    /// </remarks>
    BrushEntity,

    /// <summary>A brush of model 0, clipped to its own sides.</summary>
    WorldBrush,

    /// <summary>A <c>SURF_SKY</c> face of model 0.</summary>
    Sky,

    /// <summary>A displacement triangle.</summary>
    Displacement,

    /// <summary>A static prop triangle.</summary>
    StaticProp,
}
