//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Bsp;

/// <summary>
/// One brush as loaded from the VMF: <c>mapbrush_t</c>,
/// </summary>
/// <remarks>
/// <para>
/// Stock's <c>original_sides</c> is a pointer into the one shared
/// <c>brushsides</c> array plus a <c>numsides</c> count, which makes a brush's
/// sides a contiguous RANGE and not a list of its own. Three behaviours depend
/// on that and are reproduced here through
/// <see cref="FirstSide"/>/<see cref="SideCount"/> rather than by giving the
/// brush its own collection:
/// </para>
/// <list type="bullet">
/// <item><c>AddBrushBevels</c> appends new sides at
/// <c>original_sides[numsides]</c> — the next slot of the shared array, which
/// is only free because the brush being beveled is the one being loaded
/// </item>
/// <item><c>side_brushtextures</c> is keyed on the shared index, so a side's
/// placement is found by <c>s - brushsides</c>
/// </item>
/// <item>instance merging rebases the range with pointer arithmetic
/// </item>
/// </list>
/// </remarks>
public sealed class MapBrush
{
    /// <summary>Which entity the brush belongs to: <c>entitynum</c>.</summary>
    public int EntityNumber { get; set; }

    /// <summary>The brush's ordinal within its entity: <c>brushnum</c>.</summary>
    public int BrushNumber { get; set; }

    /// <summary>The brush's VMF <c>id</c>, used only for error messages.</summary>
    public int Id { get; set; }

    /// <summary>The <c>CONTENTS_*</c> mask for the whole brush.</summary>
    /// <remarks>
    /// Derived by <see cref="MapFileLoader.BrushContentsOf"/> once the sides are
    /// loaded, then overwritten to <c>CONTENTS_AREAPORTAL</c> for an areaportal
    /// Entity.
    /// </remarks>
    public int Contents { get; set; }

    /// <summary>The brush's bounding box minimum.</summary>
    public Vec3 Mins { get; set; }

    /// <summary>The brush's bounding box maximum.</summary>
    public Vec3 Maxs { get; set; }

    /// <summary>
    /// The index of the brush's first side in <see cref="MapFile.BrushSides"/>.
    /// </summary>
    public int FirstSide { get; set; }

    /// <summary>How many sides the brush has: <c>numsides</c>.</summary>
    /// <remarks>
    /// Set to zero to discard a brush. Stock does that for origin brushes, for
    /// displacement brushes and for detail or water brushes removed by a switch
    /// — and
    /// because it does not advance <c>nummapbrushes</c> in those cases, the
    /// slot is reused by the next brush. So a zero count is not a brush with no
    /// sides; it is a brush that was never kept.
    /// </remarks>
    public int SideCount { get; set; }
}
