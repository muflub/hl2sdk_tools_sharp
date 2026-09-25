using SourceSharp.MapTools.Geometry;

namespace SourceSharp.MapTools.Bsp.Csg;

/// <summary>
/// One side of a brush being carved: the fields of <c>side_t</c>
/// (<c>utils/vbsp/vbsp.h:65</c>) that the CSG and BSP stages read or write.
/// </summary>
/// <remarks>
/// <para>
/// <b>A struct, where <see cref="MapBrushSide"/> is a class, and the difference
/// is stock's.</b> <c>CreateClippedBrush</c> fills a new brush's sides with
/// <c>memcpy (newbrush-&gt;sides, mb-&gt;original_sides,
/// nNumSides*sizeof(side_t))</c> (<c>csg.cpp:235</c>) and <c>SplitBrush</c>
/// copies one side into another with <c>*cs = *s</c>
/// (<c>brushbsp.cpp:1170</c>). Those are value copies: setting
/// <see cref="Tested"/> or <see cref="TexInfo"/> on a carved side does NOT
/// reach the <see cref="MapBrushSide"/> it was copied from. Reference
/// semantics here would silently join the two, and the join would show up as
/// map brush sides mysteriously carrying <c>TEXINFO_NODE</c>.
/// </para>
/// <para>
/// <b>There is no <c>original</c> back-pointer, because stock's is always
/// null.</b> <c>side_t::original</c> exists (<c>vbsp.h:71</c>) and
/// <c>CopyMatchingTexinfos</c> reads it (<c>csg.cpp:325</c>), but the one line
/// that would set it is commented out (<c>csg.cpp:250</c>) and
/// <c>CMapFile::Init</c>'s memset (<c>map.cpp:70</c>) zeroes the field it is
/// copied from. So <c>pSide-&gt;original</c> is null at every reachable call
/// and that branch of <c>CopyMatchingTexinfos</c> is dead code. Reproducing a
/// pointer that is never non-null would only invite someone to set it.
/// </para>
/// <para>
/// The overlay id lists, the smoothing groups, the side id and the dynamic
/// shadow flag are absent for the same reason in reverse: they live on
/// <see cref="MapBrushSide"/>, nothing between <c>MakeBspBrushList</c> and
/// <c>BuildTree_r</c> reads them, and stock's <c>memcpy</c> of a
/// <c>CUtlVector</c> copies the pointer rather than the contents — an alias
/// the freeing path never notices only because nothing ever looks.
/// </para>
/// </remarks>
public struct BspBrushSide : IEquatable<BspBrushSide>
{
    /// <summary>
    /// The side's plane, as an index into the map's
    /// <see cref="MapFile.Planes"/>.
    /// </summary>
    public int PlaneNumber { get; set; }

    /// <summary>
    /// The side's texinfo index, or <see cref="TexInfoNode"/> once the BSP has
    /// put the side on a node.
    /// </summary>
    public int TexInfo { get; set; }

    /// <summary>The displacement on this side, or null. Phase 3f owns the type.</summary>
    /// <remarks>
    /// <c>pMapDisp</c>. Copied by the <c>memcpy</c> and cleared by hand on a
    /// midwinding side (<c>brushbsp.cpp:1229</c>), which is why it is here and
    /// not left to the map side.
    /// </remarks>
    public IMapDisplacement? Displacement { get; set; }

    /// <summary>The side's polygon, or <see cref="Winding.Null"/>.</summary>
    public Winding Winding { get; set; }

    /// <summary>The <c>CONTENTS_*</c> bits this side contributes.</summary>
    public int Contents { get; set; }

    /// <summary>The <c>SURF_*</c> bits this side contributes.</summary>
    public int Surface { get; set; }

    /// <summary>Whether the side may be chosen as a BSP splitter first.</summary>
    public bool Visible { get; set; }

    /// <summary>Whether <c>SelectSplitSide</c> has already scored this plane.</summary>
    public bool Tested { get; set; }

    /// <summary>Whether the side is a bevel rather than an authored face.</summary>
    public bool Bevel { get; set; }

    /// <summary>
    /// <c>TEXINFO_NODE</c>, <c>utils/vbsp/vbsp.h:32</c>: "side is allready on a
    /// node".
    /// </summary>
    public const int TexInfoNode = -1;

    /// <summary>Copies the fields <c>CreateClippedBrush</c>'s memcpy copies.</summary>
    /// <param name="side">The map side to copy from.</param>
    /// <returns>The carved side.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="side"/> is null.</exception>
    /// <remarks>
    /// The winding handle is copied as-is, NOT duplicated — stock's memcpy
    /// copies the pointer and <c>CreateClippedBrush</c> then replaces it with
    /// <c>CopyWinding</c> on the next line (<c>csg.cpp:241</c>). Keeping the two
    /// steps apart keeps that order visible.
    /// </remarks>
    public static BspBrushSide From(MapBrushSide side)
    {
        ArgumentNullException.ThrowIfNull(side);

        return new BspBrushSide
        {
            PlaneNumber = side.PlaneNumber,
            TexInfo = side.TexInfo,
            Displacement = side.Displacement,
            Winding = side.Winding,
            Contents = side.Contents,
            Surface = side.Surface,
            Visible = side.Visible,
            Tested = side.Tested,
            Bevel = side.Bevel,
        };
    }

    /// <inheritdoc />
    public readonly bool Equals(BspBrushSide other) =>
        PlaneNumber == other.PlaneNumber
        && TexInfo == other.TexInfo
        && ReferenceEquals(Displacement, other.Displacement)
        && Winding == other.Winding
        && Contents == other.Contents
        && Surface == other.Surface
        && Visible == other.Visible
        && Tested == other.Tested
        && Bevel == other.Bevel;

    /// <inheritdoc />
    public readonly override bool Equals(object? obj) => obj is BspBrushSide other && Equals(other);

    /// <inheritdoc />
    public readonly override int GetHashCode() =>
        HashCode.Combine(PlaneNumber, TexInfo, Winding, Contents, Surface, Visible, Tested, Bevel);

    /// <summary>Compares two sides.</summary>
    /// <param name="left">The first side.</param>
    /// <param name="right">The second side.</param>
    /// <returns>True when every field matches.</returns>
    public static bool operator ==(BspBrushSide left, BspBrushSide right) => left.Equals(right);

    /// <summary>Compares two sides.</summary>
    /// <param name="left">The first side.</param>
    /// <param name="right">The second side.</param>
    /// <returns>True when any field differs.</returns>
    public static bool operator !=(BspBrushSide left, BspBrushSide right) => !left.Equals(right);
}
