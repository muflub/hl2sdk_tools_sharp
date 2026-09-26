//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Bsp;

/// <summary>
/// The part of <c>mapdispinfo_t</c> that the map loader itself touches.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately minimal, and owned by the displacement lane.</b> Stock's
/// <c>mapdispinfo_t</c> carries a face, power,
/// distances, normals, offsets, alphas and triangle tags — none of which
/// Reads. What does with a displacement is:
/// </para>
/// <list type="bullet">
/// <item>test whether a side has one at all
/// (<c>HasDispInfo</c>, and);</item>
/// <item>reassign <see cref="EntityNumber"/> when a brush moves to worldspawn
/// (<c>MoveBrushesToWorldGeneral</c>);</item>
/// <item>offset <see cref="EntityNumber"/> and set
/// <see cref="BrushSideId"/> when an instance is merged
/// (<c>MergeBrushSides</c>).</item>
/// </list>
/// <para>
/// Those three are this interface. Phase 3f owns the real type and will
/// implement it; nothing here constructs one, and this file is the minimum
/// needed for the loader to compile and behave, not a design for
/// displacements.
/// </para>
/// </remarks>
public interface IMapDisplacement
{
    /// <summary>
    /// Which entity the displacement belongs to: <c>mapdispinfo_t.entitynum</c>.
    /// </summary>
    int EntityNumber { get; set; }

    /// <summary>
    /// The VMF id of the brush side it sits on:
    /// <c>mapdispinfo_t.brushSideID</c>.
    /// </summary>
    int BrushSideId { get; set; }
}
