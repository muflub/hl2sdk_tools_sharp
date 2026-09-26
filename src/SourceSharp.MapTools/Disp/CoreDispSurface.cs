//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Disp;

/// <summary>
/// The flat quad a displacement is built on, plus the neighbour records that
/// live on it: <c>CCoreDispSurface</c>.
/// </summary>
/// <remarks>
/// <para>
/// A displacement is always four points — <c>SetPointCount</c> silently
/// ignores any other value and <c>Create</c> returns
/// false if the count is not four — so the arrays here are fixed at four and
/// there is no count.
/// </para>
/// <para>
/// The neighbour records sit on the SURFACE rather than on the displacement,
/// which looks like an accident of where the BSP struct's fields go and turns
/// out to matter: <c>CCoreDispInfo</c>'s <c>CDispUtilsHelper</c> accessors
/// forward straight through to here, so the traversal code and the lump writer
/// are reading the same storage and a neighbour found is a neighbour exported.
/// </para>
/// </remarks>
public sealed class CoreDispSurface
{
    private readonly Vec3[] _points = new Vec3[4];
    private readonly Vec3[] _normals = new Vec3[4];
    private readonly DispUv[] _texCoords = new DispUv[4];
    private readonly DispUv[] _luxelCoords = new DispUv[(NumBumpVects + 1) * 4];
    private readonly float[] _alphas = [1.0f, 1.0f, 1.0f, 1.0f];
    private DispNeighbor[] _edgeNeighbors = new DispNeighbor[4];
    private DispCornerNeighbors[] _cornerNeighbors = new DispCornerNeighbors[4];

    /// <summary>The number of bump-mapped lightmap sets past the base one.</summary>
    /// <remarks><c>NUM_BUMP_VECTS</c>.</remarks>
    public const int NumBumpVects = 3;

    /// <summary>
    /// The widest a displacement's lightmap may be, borders excluded.
    /// </summary>
    /// <remarks>
    /// <c>MAX_DISP_LIGHTMAP_DIM_WITHOUT_BORDER</c>.
    /// </remarks>
    public const int MaxLightmapDimWithoutBorder = 125;

    /// <summary>Builds an empty surface, cleared as <c>Init</c> clears one.</summary>
    public CoreDispSurface()
    {
        PointStartIndex = -1;

        for (int i = 0; i < 4; i++)
        {
            _edgeNeighbors[i].SetInvalid();
            _cornerNeighbors[i].SetInvalid();
        }
    }

    /// <summary>The quad's four points, in winding order after rotation.</summary>
    public ReadOnlySpan<Vec3> Points => _points;

    /// <summary>The four corner normals, which vbsp never fills.</summary>
    public ReadOnlySpan<Vec3> Normals => _normals;

    /// <summary>The four corner texture coordinates.</summary>
    public ReadOnlySpan<DispUv> TexCoords => _texCoords;

    /// <summary>The four corner alphas.</summary>
    public ReadOnlySpan<float> Alphas => _alphas;

    /// <summary>The lightmap width in luxels, from <see cref="CalcLuxelCoords"/>.</summary>
    public int LuxelU { get; set; }

    /// <summary>The lightmap height in luxels.</summary>
    public int LuxelV { get; set; }

    /// <summary>The <c>SURF_*</c> flags inherited from the base face.</summary>
    public int Flags { get; set; }

    /// <summary>The <c>CONTENTS_*</c> bits inherited from the base brush.</summary>
    public int Contents { get; set; }

    /// <summary>
    /// The owner's handle: <c>m_Index</c>, set through <c>SetHandle</c>
    /// Vrad stores the base face's LUMP_FACES index
    /// here; vbsp never sets it, so it stays -1
    /// (<c>CCoreDispSurface::Init</c>).
    /// </summary>
    public int Handle { get; set; } = -1;

    /// <summary>
    /// The texture S axis: <c>sAxis</c>, <c>SetSAxis</c>/<c>GetSAxis</c>
    /// </summary>
    /// <remarks>
    /// Cleared by <c>Init</c> and set by nothing
    /// under <c>src/utils</c>, so zero in every stock run; which is why the
    /// tangents <see cref="CoreDispInfo.Create"/> derives from it are zero.
    /// </remarks>
    public Vec3 SAxis { get; set; }

    /// <summary>The texture T axis: <c>tAxis</c>. See <see cref="SAxis"/>.</summary>
    public Vec3 TAxis { get; set; }

    /// <summary>
    /// Sets one corner's normal: <c>SetPointNormal</c>.
    /// </summary>
    /// <param name="index">0..3.</param>
    /// <param name="normal">The normal.</param>
    /// <exception cref="ArgumentOutOfRangeException">Not 0..3.</exception>
    /// <remarks>
    /// Rotated with the points by <see cref="AdjustSurfPointData"/>, and read
    /// by nothing in <see cref="CoreDispInfo.Create"/>: the vertex normals are
    /// regenerated from the displaced grid. vrad sets all four to the quad's
    /// plane normal before the rotation.
    /// </remarks>
    public void SetPointNormal(int index, Vec3 normal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, 3);
        _normals[index] = normal;
    }

    /// <summary>
    /// Which of <see cref="Points"/> the displacement starts from, or -1 before
    /// <see cref="FindSurfPointStartIndex"/> has run.
    /// </summary>
    public int PointStartIndex { get; set; }

    /// <summary>
    /// The world point the VMF's <c>startposition</c> named.
    /// </summary>
    public Vec3 PointStart { get; set; }

    /// <summary>Sets one of the quad's points.</summary>
    /// <param name="index">0..3.</param>
    /// <param name="point">The point.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not 0..3.</exception>
    public void SetPoint(int index, Vec3 point)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, 3);
        _points[index] = point;
    }

    /// <summary>Sets one of the quad's corner texture coordinates.</summary>
    /// <param name="index">0..3.</param>
    /// <param name="coord">The coordinate.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not 0..3.</exception>
    public void SetTexCoord(int index, DispUv coord)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, 3);
        _texCoords[index] = coord;
    }

    /// <summary>One corner's luxel coordinate for one bump set.</summary>
    /// <param name="bumpIndex">0..<see cref="NumBumpVects"/>.</param>
    /// <param name="index">0..3.</param>
    /// <returns>The coordinate.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Either index is out of range.</exception>
    public DispUv LuxelCoord(int bumpIndex, int index)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bumpIndex, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bumpIndex, NumBumpVects);
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, 3);
        return _luxelCoords[(bumpIndex * 4) + index];
    }

    /// <summary>The neighbours along one edge, by reference.</summary>
    /// <param name="edge">A <see cref="DispEdge"/>.</param>
    /// <returns>That edge's record.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="edge"/> is not 0..3.</exception>
    public ref DispNeighbor EdgeNeighbor(int edge)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(edge, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(edge, 3);
        return ref _edgeNeighbors[edge];
    }

    /// <summary>The neighbours at one corner, by reference.</summary>
    /// <param name="corner">A <see cref="DispCorner"/>.</param>
    /// <returns>That corner's record.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="corner"/> is not 0..3.</exception>
    public ref DispCornerNeighbors CornerNeighbors(int corner)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(corner, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(corner, 3);
        return ref _cornerNeighbors[corner];
    }

    /// <summary>
    /// Copies neighbour data in wholesale:
    /// <c>CCoreDispSurface::SetNeighborData</c>.
    /// </summary>
    /// <param name="edges">Four edge records.</param>
    /// <param name="corners">Four corner records.</param>
    /// <exception cref="ArgumentException">Either span is not four long.</exception>
    public void SetNeighborData(
        ReadOnlySpan<DispNeighbor> edges, ReadOnlySpan<DispCornerNeighbors> corners)
    {
        if (edges.Length != 4 || corners.Length != 4)
        {
            throw new ArgumentException(
                "a displacement has four edges and four corners.",
                edges.Length != 4 ? nameof(edges) : nameof(corners));
        }

        _edgeNeighbors = edges.ToArray();
        _cornerNeighbors = corners.ToArray();
    }

    /// <summary>
    /// The quad's plane normal: <c>CCoreDispSurface::GetNormal</c>,
    /// </summary>
    /// <returns>The unit normal.</returns>
    /// <remarks>
    /// The cross product's operand order is <c>(p3-p0) x (p1-p0)</c>, which is
    /// the REVERSE of the usual, and the normalise is stock's estimate. Neither
    /// reaches the BSP from vbsp: the only caller is the elevation term of
    /// <c>GenerateDispSurf</c>, and vbsp never sets an elevation.
    /// </remarks>
    public Vec3 GetNormal()
    {
        Vec3 a = _points[1] - _points[0];
        Vec3 b = _points[3] - _points[0];

        return Vec3.Cross(b, a).NormaliseLikeStock().Normalised;
    }

    /// <summary>
    /// Picks the quad point nearest <see cref="PointStart"/>:
    /// <c>FindSurfPointStartIndex</c>.
    /// </summary>
    /// <returns>The index, which is also stored in <see cref="PointStartIndex"/>.</returns>
    /// <remarks>
    /// <para>
    /// Returns the stored index unchanged if one was already set, so calling it
    /// twice is not the same as calling it once with different points.
    /// </para>
    /// <para>
    /// NEAREST rather than equal, and rightly: the VMF's <c>startposition</c>
    /// was written by Hammer from the same corner but through a different float
    /// path, and the four corners of a displacement quad are never close
    /// together. A strict equality here would reject almost every real map.
    /// </para>
    /// </remarks>
    public int FindSurfPointStartIndex()
    {
        if (PointStartIndex != -1)
        {
            return PointStartIndex;
        }

        int minIndex = -1;
        float minDistance = 999999999.0f;

        for (int i = 0; i < 4; i++)
        {
            float distanceSq = (PointStart - _points[i]).LengthSquared();

            // Strictly less than: a tie keeps the earlier point.
            if (distanceSq < minDistance)
            {
                minDistance = distanceSq;
                minIndex = i;
            }
        }

        PointStartIndex = minIndex;
        return minIndex;
    }

    /// <summary>
    /// Rotates the quad so that the start point is point 0:
    /// <c>AdjustSurfPointData</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Points, normals and texture coordinates rotate.
    /// <b>The alphas do not.</b> Stock's loop reads
    /// <c>tmpAlphas[i]</c> where every other line of the same loop reads
    /// <c>tmp...[(i + m_PointStartIndex) % 4]</c>,
    /// so the corner alphas are copied back unrotated.
    /// </para>
    /// <para>
    /// It is reproduced verbatim and is NOT a compliance quirk, because it has
    /// no consequence to name: <c>m_Alphas</c> is written only by
    /// <c>SetAlpha</c>, which vbsp never calls, and read only by
    /// <c>CalcDispSurfAlphas</c>, which is inside an <c>#if 0</c>
    /// The four values are 1.0 throughout a vbsp
    /// run, and rotating four equal numbers is the identity. A future lane that
    /// starts feeding real corner alphas in would be the first to make this
    /// visible, and this comment is for them.
    /// </para>
    /// </remarks>
    public void AdjustSurfPointData()
    {
        Vec3[] tmpPoints = (Vec3[])_points.Clone();
        Vec3[] tmpNormals = (Vec3[])_normals.Clone();
        DispUv[] tmpTexCoords = (DispUv[])_texCoords.Clone();
        float[] tmpAlphas = (float[])_alphas.Clone();

        for (int i = 0; i < 4; i++)
        {
            int from = (i + PointStartIndex) % 4;

            _points[i] = tmpPoints[from];
            _normals[i] = tmpNormals[from];
            _texCoords[i] = tmpTexCoords[from];

            // Stock's index, unrotated. See the remarks.
            _alphas[i] = tmpAlphas[i];
        }
    }

    /// <summary>
    /// Whether the quad is longer along the lightmap's u axis than its v:
    /// <c>LongestInU</c>.
    /// </summary>
    /// <param name="u">The lightmap u axis, unnormalised.</param>
    /// <param name="v">The lightmap v axis.</param>
    /// <returns>True when u wins, including on a tie.</returns>
    /// <remarks>
    /// The "edges" it measures are the four consecutive pairs of quad points
    /// projected onto each axis, and it takes the LARGEST of the four rather
    /// than a pair of opposite ones — so for a quad whose points are not in
    /// winding order it measures diagonals. vbsp's points always are.
    /// </remarks>
    public bool LongestInU(Vec3 u, Vec3 v)
    {
        Vec3 normU = u.NormaliseLikeStock().Normalised;
        Vec3 normV = v.NormaliseLikeStock().Normalised;

        Span<float> distU = stackalloc float[4];
        Span<float> distV = stackalloc float[4];

        for (int i = 0; i < 4; i++)
        {
            distU[i] = Vec3.Dot(normU, _points[i]);
            distV[i] = Vec3.Dot(normV, _points[i]);
        }

        float uLength = 0.0f;
        float vLength = 0.0f;

        for (int i = 0; i < 4; i++)
        {
            float test = MathF.Abs(distU[(i + 1) % 4] - distU[i]);
            if (test > uLength)
            {
                uLength = test;
            }

            test = MathF.Abs(distV[(i + 1) % 4] - distV[i]);
            if (test > vLength)
            {
                vLength = test;
            }
        }

        return uLength >= vLength;
    }

    /// <summary>
    /// Sizes the lightmap and lays the corner luxel coordinates out on it:
    /// <c>CalcLuxelCoords</c>.
    /// </summary>
    /// <param name="luxels">
    /// World units per luxel, which is <c>1 / |lightmapVecs[0]|</c> TRUNCATED
    /// to an int by the caller.
    /// </param>
    /// <param name="adjust">
    /// Whether to lay the coordinates out from the start corner rather than
    /// from point 0. vbsp passes false.
    /// </param>
    /// <param name="u">The lightmap u axis.</param>
    /// <param name="v">The lightmap v axis.</param>
    /// <returns>
    /// True if the face's texinfo needs its two lightmap axes SWAPPED, which
    /// the caller must then do by cloning the texinfo.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The size is <c>(int)(length / luxels) + 1</c> per axis, clamped to
    /// <see cref="MaxLightmapDimWithoutBorder"/>. That <c>+1</c> is why a
    /// 512-unit displacement at 16 units per luxel gets 33 and not 32: a
    /// displacement's lightmap samples its POSTS, and there is one more post
    /// than interval.
    /// </para>
    /// <para>
    /// The returned swap flag compares the computed sizes against the
    /// GEOMETRIC answer from <see cref="LongestInU"/>, so it fires when the
    /// texinfo's axes disagree with the quad's own long direction. The fix at
    /// Makes a NEW texinfo rather than editing the
    /// existing one, and its comment names d2_prison_08, where editing in place
    /// turned unrelated non-displacement surfaces black.
    /// </para>
    /// <para>
    /// The <c>0.5</c> constants in the corner coordinates are DOUBLES in stock
    /// (<c>flVValue + 0.5</c> with no <c>f</c>), and
    /// the first component of each pair is a float <c>0.5f</c>. Both round to
    /// the same float for every value these can take, since
    /// <c>flUValue</c> and <c>flVValue</c> are small integers; the promotion is
    /// noted so nobody has to work it out twice.
    /// </para>
    /// </remarks>
    public bool CalcLuxelCoords(int luxels, bool adjust, Vec3 u, Vec3 v)
    {
        if (luxels <= 0)
        {
            return false;
        }

        int offset = adjust ? PointStartIndex : 0;

        bool longU = LongestInU(u, v);

        float uLength = (_points[(3 + offset) % 4] - _points[(0 + offset) % 4]).Length();
        float lengthTemp = (_points[(2 + offset) % 4] - _points[(1 + offset) % 4]).Length();
        if (lengthTemp > uLength)
        {
            uLength = lengthTemp;
        }

        float vLength = (_points[(1 + offset) % 4] - _points[(0 + offset) % 4]).Length();
        lengthTemp = (_points[(2 + offset) % 4] - _points[(3 + offset) % 4]).Length();
        if (lengthTemp > vLength)
        {
            vLength = lengthTemp;
        }

        float ooLuxelScale = 1.0f / luxels;

        float uValue = (int)(uLength * ooLuxelScale) + 1;
        if (uValue > MaxLightmapDimWithoutBorder)
        {
            uValue = MaxLightmapDimWithoutBorder;
        }

        float vValue = (int)(vLength * ooLuxelScale) + 1;
        if (vValue > MaxLightmapDimWithoutBorder)
        {
            vValue = MaxLightmapDimWithoutBorder;
        }

        bool swapped = longU ? vValue > uValue : uValue > vValue;

        LuxelU = (int)uValue;
        LuxelV = (int)vValue;

        for (int bump = 0; bump < NumBumpVects + 1; bump++)
        {
            int b = bump * 4;
            _luxelCoords[b + ((0 + offset) % 4)] = new DispUv(0.5f, 0.5f);
            _luxelCoords[b + ((1 + offset) % 4)] = new DispUv(0.5f, vValue + 0.5f);
            _luxelCoords[b + ((2 + offset) % 4)] = new DispUv(uValue + 0.5f, vValue + 0.5f);
            _luxelCoords[b + ((3 + offset) % 4)] = new DispUv(uValue + 0.5f, 0.5f);
        }

        return swapped;
    }
}
