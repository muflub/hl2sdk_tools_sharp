//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Geometry;

namespace SourceSharp.MapTools.Bsp;

/// <summary>
/// One side of a brush as loaded from the VMF: <c>side_t</c>,
/// </summary>
/// <remarks>
/// <para>
/// A class, not a struct, and that is a deliberate departure. Stock's
/// <c>side_t</c> lives in one global array and is addressed by pointer from
/// four directions at once — <c>mapbrush_t::original_sides</c> points into it,
/// <c>bspbrush_t</c> sides carry an <c>original</c> back-pointer to it
/// <c>mapdispinfo_t::face.originalface</c> is one of them
/// And the CSG stage mutates <c>visible</c> and
/// <c>tested</c> through whichever of those it has. Copying by value would
/// silently fork that identity. Reference semantics here mean the aliasing is
/// the same aliasing, without the pointer arithmetic.
/// </para>
/// <para>
/// The consequence stock's pointer arithmetic also has is an INDEX: a side's
/// position in <see cref="MapFile.BrushSides"/> is what
/// <c>side_brushtextures</c> is keyed on (<c>s - brushsides</c>,
///) and what <c>WorldVertexTransitionFixup</c> walks. That
/// index is preserved by never removing a side once it is appended.
/// </para>
/// </remarks>
public sealed class MapBrushSide
{
    /// <summary>
    /// The side's plane, as an index into its <see cref="MapFile"/>'s
    /// <see cref="PlaneTable"/>.
    /// </summary>
    /// <remarks>
    /// <c>planenum ^ 1</c> is the opposite plane, always, because the table
    /// appends in pairs.
    /// </remarks>
    public int PlaneNumber { get; set; }

    /// <summary>
    /// The side's texinfo index, or <see cref="TexInfoTable.TexInfoNode"/> for a
    /// side the BSP has consumed.
    /// </summary>
    /// <remarks>
    /// Defaults to 0, not to <see cref="TexInfoTable.TexInfoNode"/>, because
    /// stock's <c>side_t</c> is zeroed by <c>CMapFile::Init</c>'s memset
    /// And an <c>-onlyents</c> compile never assigns it
    /// (is guarded). A side loaded under that switch
    /// therefore carries texinfo 0.
    /// </remarks>
    public int TexInfo { get; set; }

    /// <summary>
    /// The displacement on this side, or null. Phase 3f owns the type.
    /// </summary>
    public IMapDisplacement? Displacement { get; set; }

    /// <summary>The side's polygon, or <see cref="Winding.Null"/>.</summary>
    /// <remarks>
    /// Built by <c>MakeBrushWindings</c> and re-built when an origin brush
    /// shifts the entity. Bevel sides never get one.
    /// </remarks>
    public Winding Winding { get; set; } = Winding.Null;

    /// <summary>The <c>CONTENTS_*</c> bits this side contributes.</summary>
    public int Contents { get; set; }

    /// <summary>The <c>SURF_*</c> bits this side contributes.</summary>
    public int Surface { get; set; }

    /// <summary>Whether the side has a winding, so may be used as a splitter.</summary>
    public bool Visible { get; set; }

    /// <summary>Whether the BSP builder has already tried this side as a split.</summary>
    public bool Tested { get; set; }

    /// <summary>
    /// Whether the side is a bevel added by <c>AddBrushBevels</c> rather than
    /// authored.
    /// </summary>
    /// <remarks>
    /// A bevel is never used as a BSP splitter and is skipped when the other
    /// sides are chopped against it, which is why it must
    /// be a property of the side and not inferred from its position.
    /// </remarks>
    public bool Bevel { get; set; }

    /// <summary>The side's id from the VMF's <c>id</c> key.</summary>
    public int Id { get; set; }

    /// <summary>The side's smoothing group mask, from <c>smoothing_groups</c>.</summary>
    public uint SmoothingGroups { get; set; }

    /// <summary>The overlays that sit on this side.</summary>
    public List<int> OverlayIds { get; } = [];

    /// <summary>The water overlays that sit on this side.</summary>
    public List<int> WaterOverlayIds { get; } = [];

    /// <summary>
    /// Whether the side casts dynamic shadows: <c>m_bDynamicShadowsEnabled</c>.
    /// </summary>
    /// <remarks>
    /// The loader never writes it: <c>CMapFile::Init</c> memsets the whole
    /// <c>brushsides</c> array so it starts false, and
    /// <c>MarkNoDynamicShadowSides</c> then sets EVERY
    /// side true and clears only the ones an <c>info_no_dynamic_shadow</c>
    /// named. <see cref="MapFile.MarkNoDynamicShadowSides"/> is that pass;
    /// until a caller runs it, this value means nothing, which is why the
    /// default is the memset's and not a guess at the right answer.
    /// </remarks>
    public bool DynamicShadowsEnabled { get; set; }
}
