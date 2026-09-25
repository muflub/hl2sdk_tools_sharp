using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Tracing;

/// <summary>
/// One triangle, as a caller hands it over before the tree is built.
/// </summary>
/// <param name="Id">
/// The caller's own identity for it. This is what a <see cref="HitId"/>
/// reports, NOT the index the tree happens to store it at -- stock keeps the
/// same distinction (<c>m_nTriangleID</c> against the list position) because
/// vrad gives several triangles the same id on purpose, one per quad.
/// </param>
/// <param name="V0">First vertex.</param>
/// <param name="V1">Second vertex.</param>
/// <param name="V2">Third vertex.</param>
/// <param name="Flags">
/// <c>FCACHETRI_*</c>: bit 0 is transparent, bit 1 is a negative normal.
/// </param>
public readonly record struct TracedTriangle(
    int Id, Vec3 V0, Vec3 V1, Vec3 V2, byte Flags)
{
    /// <summary><c>FCACHETRI_TRANSPARENT</c>: alpha-tested, needs a candidate test.</summary>
    public const byte Transparent = 0x01;

    /// <summary><c>FCACHETRI_NEGATIVE_NORMAL</c>.</summary>
    public const byte NegativeNormal = 0x02;
}

/// <summary>
/// One node of the packed KD-tree: EIGHT BYTES, by three tricks stock plays.
/// </summary>
/// <remarks>
/// <para>
/// <c>CacheOptimizedKDNode</c>, reproduced exactly
/// because the size is the design: (a) the right child always sits
/// immediately after the left, so one index serves both; (b) the node type
/// lives in the bottom two bits of that index; and (c) a leaf has no splitting
/// plane, so its triangle COUNT is stored in the same four bytes, as an int
/// reinterpreted over the float.
/// </para>
/// <para>
/// That last one is why <see cref="Split"/> is read through
/// <see cref="TriangleCount"/> with a bit reinterpretation rather than a cast:
/// the bytes are an integer, and converting the float would give a different
/// number or a NaN.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct KdNode
{
    /// <summary>The left child index shifted up two, with the type in the low bits.</summary>
    public int Children;

    /// <summary>The splitting plane, or the leaf's triangle count in its bits.</summary>
    public float Split;

    /// <summary>This node is an x split (<c>KDNODE_STATE_XSPLIT</c>).</summary>
    public const int XSplit = 0;

    /// <summary>This node is a y split.</summary>
    public const int YSplit = 1;

    /// <summary>This node is a z split.</summary>
    public const int ZSplit = 2;

    /// <summary>This node is a leaf (<c>KDNODE_STATE_LEAF</c>).</summary>
    public const int Leaf = 3;

    /// <summary>Which of the four kinds this node is.</summary>
    public readonly int NodeType => Children & 3;

    /// <summary>The left child's index; the right child is the next one.</summary>
    public readonly int LeftChild => Children >> 2;

    /// <summary>Where a leaf's triangles start in the index list.</summary>
    public readonly int TriangleIndexStart => Children >> 2;

    /// <summary>How many triangles a leaf holds.</summary>
    public int TriangleCount
    {
        readonly get => BitConverter.SingleToInt32Bits(Split);
        set => Split = BitConverter.Int32BitsToSingle(value);
    }
}

/// <summary>
/// A triangle in the form the intersection test wants: a plane and two
/// projected edge equations, with the third implied.
/// </summary>
/// <remarks>
/// <para>
/// FORTY-EIGHT BYTES, and that number is worth saying out loud because both
/// Stock's own comment and the plan say 64. declares
/// "this structure is 16longs=64 bytes for cache line packing" over a struct
/// whose fields total four floats, an int, six floats and four bytes -- 48 --
/// and the compiler agrees: a build of stock's header in this tree prints
/// <c>sizeof(CacheOptimizedTriangle)=48</c>. The comment has been wrong since
/// whenever a field was removed. Matching the real layout is what matters, and
/// this does.
/// </para>
/// <para>
/// NO BARYCENTRIC DIVIDE, which is the actual trick. The three edge equations
/// are pre-scaled at build time so each evaluates to 1 at the opposite vertex
/// (<c>GetEdgeEquation</c> divides by <c>trial_dist</c>), which turns "is the
/// point inside" into two dot products in 2D plus one add, with the third edge
/// recovered as <c>b0 + b1 &lt;= 1</c>. Only two of the three are stored.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct KdTriangle
{
    /// <summary>Plane normal, x.</summary>
    public float Nx;

    /// <summary>Plane normal, y.</summary>
    public float Ny;

    /// <summary>Plane normal, z.</summary>
    public float Nz;

    /// <summary>Plane distance: <c>N . p1</c>.</summary>
    public float D;

    /// <summary>The caller's triangle id.</summary>
    public int Id;

    /// <summary>First edge equation, a.</summary>
    public float E0;

    /// <summary>First edge equation, b.</summary>
    public float E1;

    /// <summary>First edge equation, c.</summary>
    public float E2;

    /// <summary>Second edge equation, a.</summary>
    public float E3;

    /// <summary>Second edge equation, b.</summary>
    public float E4;

    /// <summary>Second edge equation, c.</summary>
    public float E5;

    /// <summary>Which coordinate the projection's first axis is (0..2).</summary>
    public byte CoordSelect0;

    /// <summary>Which coordinate the projection's second axis is.</summary>
    public byte CoordSelect1;

    /// <summary><c>FCACHETRI_*</c>.</summary>
    public byte Flags;

    /// <summary>Stock's <c>m_unused0</c>, kept so the layout is the layout.</summary>
    public byte Unused;
}

/// <summary>
/// The geometry-format triangle the BUILD works on, before the tree is
/// finished.
/// </summary>
/// <remarks>
/// Stock unions this over <see cref="KdTriangle"/> and converts in place
/// (<c>ChangeIntoIntersectionFormat</c>). Two arrays is the same thing without
/// a union, and it costs nothing: the build's array is dropped when the tree
/// is done.
/// </remarks>
internal struct KdBuildTriangle
{
    /// <summary>The three vertices, flat: <c>[v * 3 + c]</c>.</summary>
    public InlineNine V;

    /// <summary>The caller's triangle id.</summary>
    public int Id;

    /// <summary><c>FCACHETRI_*</c>.</summary>
    public byte Flags;

    // Stock's m_nTmpData0/1 (a triangle's side of the trial split, and of the
    // best one) are per RefineNode call in KdTreeBuilder, not stored here:
    // a straddling triangle is in both halves of a split, and a parallel build
    // refines the halves at the same time.

    /// <summary>One coordinate of one vertex.</summary>
    /// <param name="vertex">0, 1 or 2.</param>
    /// <param name="coord">0, 1 or 2.</param>
    /// <returns>The coordinate.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float Get(int vertex, int coord) => V[(vertex * 3) + coord];
}

/// <summary>Nine floats, the three build vertices.</summary>
[System.Runtime.CompilerServices.InlineArray(9)]
internal struct InlineNine
{
    private float _element0;
}
