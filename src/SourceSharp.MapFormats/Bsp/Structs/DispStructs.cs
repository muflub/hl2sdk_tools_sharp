//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapFormats.Bsp.Structs;

/// <summary>
/// Which corner of a displacement an index refers to.
/// </summary>
public enum DispCorner
{
    /// <summary>The lower left corner.</summary>
    LowerLeft = 0,

    /// <summary>The upper left corner.</summary>
    UpperLeft = 1,

    /// <summary>The upper right corner.</summary>
    UpperRight = 2,

    /// <summary>The lower right corner.</summary>
    LowerRight = 3,
}

/// <summary>
/// Which edge of a displacement an index refers to.
/// </summary>
/// <remarks>
/// These values must match <c>CCoreDispSurface</c>'s own edge indices, which is
/// why the reference layout pins them rather than leaving the order to taste.
/// </remarks>
public enum DispEdge
{
    /// <summary>The left edge.</summary>
    Left = 0,

    /// <summary>The top edge.</summary>
    Top = 1,

    /// <summary>The right edge.</summary>
    Right = 2,

    /// <summary>The bottom edge.</summary>
    Bottom = 3,
}

/// <summary>
/// How much of an edge a neighbouring displacement covers
/// (the reference <c>enum NeighborSpan</c>).
/// </summary>
/// <remarks>
/// The reference layout warns that lookup tables are generated from these
/// indices, so the numbering is load bearing.
/// </remarks>
public enum NeighborSpan
{
    /// <summary>The neighbour fills the whole edge.</summary>
    CornerToCorner = 0,

    /// <summary>The neighbour covers the half from the corner to the midpoint.</summary>
    CornerToMidpoint = 1,

    /// <summary>The neighbour covers the half from the midpoint to the corner.</summary>
    MidpointToCorner = 2,
}

/// <summary>
/// A neighbour's rotation relative to this displacement
/// (the reference <c>enum NeighborOrientation</c>).
/// </summary>
public enum NeighborOrientation
{
    /// <summary>No rotation.</summary>
    Ccw0 = 0,

    /// <summary>90 degrees counter-clockwise.</summary>
    Ccw90 = 1,

    /// <summary>180 degrees.</summary>
    Ccw180 = 2,

    /// <summary>270 degrees counter-clockwise.</summary>
    Ccw270 = 3,
}

/// <summary>
/// One of the up-to-two neighbours along one displacement edge
/// (the reference <c>struct CDispSubNeighbor</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DispSubNeighbor
{
    /// <summary>
    /// The neighbouring displacement's index in LUMP_DISPINFO, or
    /// <see cref="NoNeighbor"/>.
    /// </summary>
    public ushort Neighbor;

    /// <summary>The neighbour's rotation, a <see cref="NeighborOrientation"/>.</summary>
    public byte NeighborOrientation;

    /// <summary>Where the neighbour sits on OUR edge, a <see cref="NeighborSpan"/>.</summary>
    public byte Span;

    /// <summary>Where we sit on THEIR edge, a <see cref="NeighborSpan"/>.</summary>
    public byte NeighborSpan;

    /// <summary>
    /// Trailing padding: the fields total 5 bytes and the leading
    /// <c>unsigned short</c> gives the struct 2-byte alignment, so it is 6 --
    /// which is why <c>ddispinfo_t</c> is 176 bytes and not 168.
    /// </summary>
    public byte Padding;

    /// <summary>The <see cref="Neighbor"/> value meaning "nothing here".</summary>
    public const ushort NoNeighbor = 0xFFFF;

    /// <summary>Whether this slot names a neighbour at all.</summary>
    /// <returns>True unless <see cref="Neighbor"/> is <see cref="NoNeighbor"/>.</returns>
    public readonly bool IsValid() => Neighbor != NoNeighbor;
}

/// <summary>
/// One displacement edge's neighbours (the reference <c>class CDispNeighbor</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DispNeighbor
{
    /// <summary>
    /// The two sub-neighbours. When a single neighbour fills the whole edge it
    /// is ALWAYS in slot 0.
    /// </summary>
    public DispSubNeighborArray2 SubNeighbors;
}

/// <summary>
/// The displacements touching one corner
/// (the reference <c>class CDispCornerNeighbors</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DispCornerNeighbors
{
    /// <summary>
    /// Up to <c>MAX_DISP_CORNER_NEIGHBORS</c> (4) displacement indices.
    /// </summary>
    public UShortArray4 Neighbors;

    /// <summary>How many of <see cref="Neighbors"/> are in use.</summary>
    public byte NumNeighbors;

    /// <summary>
    /// Trailing padding: the fields total 9 bytes and the ushort array gives
    /// the struct 2-byte alignment, so it is 10.
    /// </summary>
    public byte Padding;
}

/// <summary>
/// One displacement vertex's offset from its flat position
/// (the reference <c>class CDispVert</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DispVert
{
    /// <summary>The direction the vertex is displaced along.</summary>
    public Vec3 Vector;

    /// <summary>How far along <see cref="Vector"/> it is displaced.</summary>
    public float Dist;

    /// <summary>The vertex's blend alpha.</summary>
    public float Alpha;
}

/// <summary>
/// One displacement triangle's tags (the reference <c>class CDispTri</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DispTri
{
    /// <summary>A combination of <see cref="DispTriTags"/>.</summary>
    public ushort Tags;
}

/// <summary>
/// The values <see cref="DispTri.Tags"/> takes.
/// </summary>
[Flags]
public enum DispTriTags
{
    /// <summary>No tags.</summary>
    None = 0,

    /// <summary>The triangle is part of the displacement surface.</summary>
    Surface = 1 << 0,

    /// <summary>The triangle is walkable.</summary>
    Walkable = 1 << 1,

    /// <summary>The triangle is buildable.</summary>
    Buildable = 1 << 2,

    /// <summary>The first surface-property override bit.</summary>
    SurfProp1 = 1 << 3,

    /// <summary>The second surface-property override bit.</summary>
    SurfProp2 = 1 << 4,

    /// <summary>The triangle is removed.</summary>
    Remove = 1 << 5,
}

/// <summary>
/// One displacement's header (the reference <c>class ddispinfo_t</c>).
/// 176 bytes.
/// </summary>
/// <remarks>
/// The size is worth stating because it is arrived at by three separate
/// paddings: <see cref="DispSubNeighbor"/>'s trailing byte,
/// <see cref="DispCornerNeighbors"/>'s trailing byte, and the two bytes after
/// <see cref="MapFace"/> here.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DispInfo
{
    /// <summary>
    /// The corner the displacement is oriented from. Added at BSP version 6.
    /// </summary>
    public Vec3 StartPosition;

    /// <summary>The first vertex of this displacement's run in LUMP_DISP_VERTS.</summary>
    public int DispVertStart;

    /// <summary>The first triangle of its run in LUMP_DISP_TRIS.</summary>
    public int DispTriStart;

    /// <summary>
    /// The power: the displacement is <c>(2^power + 1)</c> vertices on a side,
    /// and the power is 2..4.
    /// </summary>
    public int Power;

    /// <summary>The minimum tessellation allowed.</summary>
    public int MinTess;

    /// <summary>The lighting smoothing angle in degrees.</summary>
    public float SmoothingAngle;

    /// <summary>The surface contents flags.</summary>
    public int Contents;

    /// <summary>The LUMP_FACES index this displacement came from.</summary>
    public ushort MapFace;

    /// <summary>
    /// Padding. <see cref="MapFace"/> leaves the struct at offset 38 and the
    /// <c>int</c> that follows needs 4-byte alignment, so C inserts two bytes
    /// here.
    /// </summary>
    public ushort Padding;

    /// <summary>
    /// The first entry of this displacement's run in LUMP_DISP_LIGHTMAP_ALPHAS.
    /// </summary>
    public int LightmapAlphaStart;

    /// <summary>
    /// The first entry of its run in LUMP_DISP_LIGHTMAP_SAMPLE_POSITIONS.
    /// </summary>
    public int LightmapSamplePositionStart;

    /// <summary>The four edges' neighbours, indexed by <see cref="DispEdge"/>.</summary>
    public DispNeighborArray4 EdgeNeighbors;

    /// <summary>The four corners' neighbours, indexed by <see cref="DispCorner"/>.</summary>
    public DispCornerNeighborsArray4 CornerNeighbors;

    /// <summary>
    /// A 320-bit vector saying which of this displacement's vertices may be
    /// active, derived from the neighbours' powers.
    /// </summary>
    /// <remarks>
    /// The declared element type is <c>uint32</c> and NOT <c>unsigned long</c>,
    /// which is the 64-bit-Linux trap this field is famous for: on LP64 a
    /// <c>long</c> array would be 80 bytes and <c>ddispinfo_t</c> would read
    /// 216 instead of 176, shifting every displacement after the first.
    /// </remarks>
    public UIntArray10 AllowedVerts;

    /// <summary>The number of vertices a displacement of this power has.</summary>
    /// <returns><c>(2^power + 1)^2</c>.</returns>
    /// <remarks><c>NUM_DISP_POWER_VERTS</c> in the reference layout.</remarks>
    public readonly int NumVerts() => ((1 << Power) + 1) * ((1 << Power) + 1);

    /// <summary>The number of triangles a displacement of this power has.</summary>
    /// <returns><c>2 * (2^power)^2</c>.</returns>
    /// <remarks><c>NUM_DISP_POWER_TRIS</c> in the reference layout.</remarks>
    public readonly int NumTris() => (1 << Power) * (1 << Power) * 2;
}

/// <summary>
/// One overlay projected onto world faces (the reference
/// <c>struct doverlay_t</c>). 352 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DOverlay
{
    /// <summary>The overlay's id, matching the entity that placed it.</summary>
    public int Id;

    /// <summary>The overlay's <see cref="TexInfo"/> index.</summary>
    public short TexInfo;

    /// <summary>
    /// The face count in the low 14 bits and the render order in the top 2.
    /// Use <see cref="GetFaceCount"/> and <see cref="GetRenderOrder"/>.
    /// </summary>
    public ushort FaceCountAndRenderOrder;

    /// <summary>
    /// The faces the overlay is projected onto, at most
    /// <c>OVERLAY_BSP_FACE_COUNT</c> (64).
    /// </summary>
    public IntArray64 Faces;

    /// <summary>The u extent, minimum then maximum.</summary>
    public FloatArray2 U;

    /// <summary>The v extent, minimum then maximum.</summary>
    public FloatArray2 V;

    /// <summary>The overlay quad's four corners in the basis' space.</summary>
    public Vec3Array4 UvPoints;

    /// <summary>The overlay's world origin.</summary>
    public Vec3 Origin;

    /// <summary>The overlay's basis normal.</summary>
    public Vec3 BasisNormal;

    /// <summary>The mask that separates render order from face count.</summary>
    public const ushort RenderOrderMask = 0xC000;

    /// <summary>The number of faces this overlay touches.</summary>
    /// <returns><see cref="FaceCountAndRenderOrder"/> without the top two bits.</returns>
    /// <remarks>Defined alongside <c>doverlay_t</c> in the reference layout.</remarks>
    public readonly ushort GetFaceCount() => (ushort)(FaceCountAndRenderOrder & ~RenderOrderMask);

    /// <summary>The overlay's render order, 0..3.</summary>
    /// <returns>The top two bits of <see cref="FaceCountAndRenderOrder"/>.</returns>
    /// <remarks>Defined alongside <c>doverlay_t</c> in the reference layout.</remarks>
    public readonly ushort GetRenderOrder() => (ushort)(FaceCountAndRenderOrder >> 14);

    /// <summary>Sets the face count, leaving the render order alone.</summary>
    /// <param name="count">A count below 0x4000.</param>
    /// <exception cref="ArgumentOutOfRangeException">The count would collide with the render order.</exception>
    public void SetFaceCount(ushort count)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, (ushort)0x3FFF);
        FaceCountAndRenderOrder &= RenderOrderMask;
        FaceCountAndRenderOrder |= (ushort)(count & ~RenderOrderMask);
    }

    /// <summary>Sets the render order, leaving the face count alone.</summary>
    /// <param name="order">An order in 0..3.</param>
    /// <exception cref="ArgumentOutOfRangeException">The order does not fit in two bits.</exception>
    public void SetRenderOrder(ushort order)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(order, (ushort)3);
        FaceCountAndRenderOrder &= unchecked((ushort)~RenderOrderMask);
        FaceCountAndRenderOrder |= (ushort)(order << 14);
    }
}

/// <summary>
/// One overlay projected onto a water surface (the reference
/// <c>struct dwateroverlay_t</c>). 1120 bytes.
/// </summary>
/// <remarks>
/// Identical to <see cref="DOverlay"/> except that the face array is
/// <c>WATEROVERLAY_BSP_FACE_COUNT</c> (256) entries
/// rather than 64. The render-order mask is the same 0xC000, so a water
/// overlay's face count has 14 bits for 256 faces.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DWaterOverlay
{
    /// <summary>The overlay's id.</summary>
    public int Id;

    /// <summary>The overlay's <see cref="TexInfo"/> index.</summary>
    public short TexInfo;

    /// <summary>The face count in the low 14 bits and the render order in the top 2.</summary>
    public ushort FaceCountAndRenderOrder;

    /// <summary>The faces the overlay is projected onto, at most 256.</summary>
    public IntArray256 Faces;

    /// <summary>The u extent, minimum then maximum.</summary>
    public FloatArray2 U;

    /// <summary>The v extent, minimum then maximum.</summary>
    public FloatArray2 V;

    /// <summary>The overlay quad's four corners in the basis' space.</summary>
    public Vec3Array4 UvPoints;

    /// <summary>The overlay's world origin.</summary>
    public Vec3 Origin;

    /// <summary>The overlay's basis normal.</summary>
    public Vec3 BasisNormal;

    /// <summary>The number of faces this overlay touches.</summary>
    /// <returns><see cref="FaceCountAndRenderOrder"/> without the top two bits.</returns>
    /// <remarks>Defined alongside <c>dwateroverlay_t</c> in the reference layout.</remarks>
    public readonly ushort GetFaceCount() =>
        (ushort)(FaceCountAndRenderOrder & ~DOverlay.RenderOrderMask);

    /// <summary>The overlay's render order, 0..3.</summary>
    /// <returns>The top two bits of <see cref="FaceCountAndRenderOrder"/>.</returns>
    /// <remarks>Defined alongside <c>dwateroverlay_t</c> in the reference layout.</remarks>
    public readonly ushort GetRenderOrder() => (ushort)(FaceCountAndRenderOrder >> 14);
}
