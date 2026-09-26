//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapTools.Rad;

/// <summary>
/// One displacement tessellated into world-space vertices and triangles, which
/// is all vrad's shadow load needs of it.
/// </summary>
/// <remarks>
/// <para>
/// Stock reaches this through four classes and three files. A BSP
/// <c>ddispinfo_t</c> is unpacked into a <c>CCoreDispInfo</c>
/// (<c>CVRadDispMgr::DispBuilderInit</c>), which
/// <c>CCoreDispInfo::Create</c> turns into a
/// vertex grid; <c>CDispCollTree::AABBTree_CopyDispData</c>
/// Then copies that grid into
/// <c>m_aVerts</c>/<c>m_aTris</c>, and <c>CVRADDispColl</c> adds lightmap data
/// on top. This class is the part of that chain a shadow caster reads, and
/// nothing else: no normals, no tangent frames, no luxel coordinates, no LOD
/// tree, no AABB tree.
/// </para>
/// <para>
/// WHAT MAKES THE SHORT PATH CORRECT is that the vrad path never sets two of
/// the three terms <c>GenerateDispSurf</c> adds to
/// a vertex. <c>m_Elevation</c> is zeroed by the constructor
/// And no caller under <c>src/utils/vrad</c> calls
/// <c>SetElevation</c>, and <c>m_SubdivPos</c> is zeroed by
/// <c>InitDispInfo</c> and only ever filled by
/// Hammer's subdivision, which bakes its result into the lump's field vectors
/// before the map is compiled -- the comment says
/// so in as many words ("offset have been combined with fieldvectors at this
/// point!!!"). So a compiled displacement's vertex is the flat bilinear point
/// plus <c>vec * dist</c>, and the elevation and subdivision terms in stock's
/// loop are dead code for every map vrad has ever lit.
/// </para>
/// </remarks>
public sealed class DisplacementSurface
{
    private readonly Vec3[] _vertices;
    private readonly Vec3[] _cornerPoints;
    private readonly int[] _triangleIndices;

    private DisplacementSurface(
        int index,
        int faceIndex,
        int power,
        BrushContents contents,
        Vec3[] cornerPoints,
        Vec3[] vertices,
        int[] triangleIndices,
        float maxDisplacement)
    {
        Index = index;
        FaceIndex = faceIndex;
        Power = power;
        Contents = contents;
        MaxDisplacement = maxDisplacement;
        _cornerPoints = cornerPoints;
        _vertices = vertices;
        _triangleIndices = triangleIndices;
    }

    /// <summary>
    /// <c>MASK_OPAQUE</c>: the contents bits that make
    /// a surface block light.
    /// </summary>
    /// <remarks>
    /// Worth naming rather than inlining, because it is NOT
    /// <c>MASK_SOLID</c> and the difference is the whole reason a grate casts
    /// no shadow: <c>CONTENTS_GRATE</c> and <c>CONTENTS_WINDOW</c> are solid to
    /// movement and invisible to light.
    /// </remarks>
    public const BrushContents MaskOpaque =
        BrushContents.Solid | BrushContents.Moveable | BrushContents.Opaque;

    /// <summary>This displacement's index in LUMP_DISPINFO.</summary>
    /// <remarks>
    /// The emission order, because <c>CVRadDispMgr::AddPolysForRayTrace</c>
    /// Walks <c>m_DispTrees</c>, which
    /// <c>UnserializeDisps</c> fills one entry per <c>g_dispinfo</c> entry in
    /// that order -- not in face order, which
    /// is a different permutation on most maps.
    /// </remarks>
    public int Index { get; }

    /// <summary>The LUMP_FACES index this displacement's base surface came from.</summary>
    public int FaceIndex { get; }

    /// <summary>The displacement power: 2, 3 or 4.</summary>
    public int Power { get; }

    /// <summary>
    /// The contents inherited from the displacement's own lump entry.
    /// </summary>
    /// <remarks>
    /// FROM <c>ddispinfo_t::contents</c> AND NOT FROM THE FACE'S TEXINFO, which
    /// is the one thing about this path worth checking twice:
    /// <c>DispBuilderInit</c> calls <c>pSurf->SetContents( pDisp->contents )</c>
    /// And <c>AABBTree_CopyDispData</c> reads it
    /// straight back out. vbsp put the
    /// material's contents there when it wrote the lump, so the two normally
    /// agree -- which is exactly why reading the wrong one would go unnoticed.
    /// </remarks>
    public BrushContents Contents { get; }

    /// <summary>
    /// The largest distance any of this displacement's vertices moves off its
    /// flat base surface.
    /// </summary>
    /// <remarks>
    /// Not a stock quantity. It exists so a fact can bound the tessellated
    /// vertices against the base face plus this, which a triangle count cannot
    /// see: a surface built from the wrong four corner points, or with the
    /// field vectors applied in the wrong axis order, has exactly the right
    /// number of triangles somewhere else entirely.
    /// </remarks>
    public float MaxDisplacement { get; }

    /// <summary>
    /// The number of vertices along one edge, <c>2^power + 1</c>.
    /// </summary>
    /// <remarks><c>GetPostSpacing</c>.</remarks>
    public int PostSpacing => (1 << Power) + 1;

    /// <summary>
    /// The world-space vertices, row-major as <c>i * postSpacing + j</c>.
    /// </summary>
    public ReadOnlySpan<Vec3> Vertices => _vertices;

    /// <summary>
    /// The four base-surface corner points, already rotated so that corner 0 is
    /// the one <see cref="DispInfo.StartPosition"/> names.
    /// </summary>
    public ReadOnlySpan<Vec3> CornerPoints => _cornerPoints;

    /// <summary>
    /// Three <see cref="Vertices"/> indices per triangle.
    /// </summary>
    public ReadOnlySpan<int> TriangleIndices => _triangleIndices;

    /// <summary>How many triangles this displacement tessellates into.</summary>
    public int TriangleCount => _triangleIndices.Length / 3;

    /// <summary>Whether this displacement blocks light at all.</summary>
    /// <remarks>
    /// The single early-out of <c>CVRADDispColl::AddPolysForRayTrace</c>
    /// It is per DISPLACEMENT, not per
    /// triangle: a displacement either contributes all of its triangles or none
    /// of them.
    /// </remarks>
    public bool IsOpaque => (Contents & MaskOpaque) != 0;

    /// <summary>
    /// Which of a face's four corner points the displacement starts from.
    /// </summary>
    /// <param name="startPosition">
    /// <see cref="DispInfo.StartPosition"/>, the corner Hammer anchored the
    /// displacement to.
    /// </param>
    /// <param name="points">The face's four winding points, in winding order.</param>
    /// <returns>The index into <paramref name="points"/> of the start corner.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="points"/> does not hold exactly four points.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <c>CCoreDispSurface::FindSurfPointStartIndex</c>
    /// NEAREST corner by squared distance, not an
    /// exact match. Stock is right to be loose about it -- the start position
    /// was written by vbsp from the same vertices, but through a different
    /// float path -- and the looseness is harmless because the four corners of
    /// a displacement face are never close together.
    /// </para>
    /// <para>
    /// Getting this wrong is the failure this whole class is most exposed to,
    /// because it is invisible to a count: rotating the start corner rotates
    /// the entire height field a quarter turn over the same quad, producing
    /// exactly the right number of triangles in exactly the right bounding box
    /// with every vertex in the wrong place.
    /// </para>
    /// </remarks>
    public static int FindStartIndex(Vec3 startPosition, ReadOnlySpan<Vec3> points)
    {
        if (points.Length != 4)
        {
            throw new ArgumentException(
                $"a displacement's base surface has exactly four points, not {points.Length}.",
                nameof(points));
        }

        int minIndex = -1;
        float minDistance = float.PositiveInfinity;
        for (int i = 0; i < 4; i++)
        {
            float distanceSq = (startPosition - points[i]).LengthSquared();

            // Strictly less than, so a tie keeps the EARLIER corner, as stock's
            // own loop does.
            if (distanceSq < minDistance)
            {
                minDistance = distanceSq;
                minIndex = i;
            }
        }

        return minIndex;
    }

    /// <summary>
    /// Tessellates one displacement.
    /// </summary>
    /// <param name="index">Its index in LUMP_DISPINFO.</param>
    /// <param name="faceIndex">The LUMP_FACES index of its base surface.</param>
    /// <param name="info">Its lump entry.</param>
    /// <param name="facePoints">
    /// The base face's four winding points, in winding order and NOT yet
    /// rotated to the start corner.
    /// </param>
    /// <param name="dispVerts">
    /// The whole LUMP_DISP_VERTS span; this displacement's run is sliced out of
    /// it at <see cref="DispInfo.DispVertStart"/>.
    /// </param>
    /// <returns>The tessellated surface.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="facePoints"/> does not hold exactly four points.
    /// </exception>
    /// <exception cref="InvalidBspException">
    /// The power is outside 2..4, or the displacement's vertex run runs off the
    /// end of <paramref name="dispVerts"/>.
    /// </exception>
    public static DisplacementSurface Create(
        int index,
        int faceIndex,
        in DispInfo info,
        ReadOnlySpan<Vec3> facePoints,
        ReadOnlySpan<DispVert> dispVerts)
    {
        if (facePoints.Length != 4)
        {
            throw new ArgumentException(
                $"a displacement's base surface has exactly four points, not "
                + $"{facePoints.Length}.",
                nameof(facePoints));
        }

        // MIN_MAP_DISP_POWER and MAX_MAP_DISP_POWER. Stock
        // asserts this and then carries on in a release
        // build, which for power 0 divides by zero in GenerateDispSurf and for
        // power 5 overruns MAX_DISPVERTS; neither is a thing to reproduce.
        if (info.Power is < 2 or > 4)
        {
            throw new InvalidBspException(
                $"displacement {index} has power {info.Power}; the format allows 2 to 4 "
                + "(MIN_MAP_DISP_POWER).");
        }

        int numVerts = info.NumVerts();
        if (info.DispVertStart < 0 || info.DispVertStart + numVerts > dispVerts.Length)
        {
            throw new InvalidBspException(
                $"displacement {index} claims vertices {info.DispVertStart}.."
                + $"{info.DispVertStart + numVerts - 1} of a LUMP_DISP_VERTS holding "
                + $"{dispVerts.Length}.");
        }

        ReadOnlySpan<DispVert> run = dispVerts.Slice(info.DispVertStart, numVerts);

        int start = FindStartIndex(info.StartPosition, facePoints);

        // CCoreDispSurface::AdjustSurfPointData. It rotates
        // the points in place rather than indexing through the start offset
        // later, so everything downstream can assume corner 0 is the start.
        Vec3[] points = new Vec3[4];
        for (int i = 0; i < 4; i++)
        {
            points[i] = facePoints[(i + start) % 4];
        }

        Vec3[] vertices = GenerateSurface(info.Power, points, run, out float maxDisplacement);
        int[] triangleIndices = GenerateTriangleIndices(info.Power);

        return new DisplacementSurface(
            index,
            faceIndex,
            info.Power,
            (BrushContents)info.Contents,
            points,
            vertices,
            triangleIndices,
            maxDisplacement);
    }

    /// <summary>
    /// <c>CCoreDispInfo::GenerateDispSurf</c>.
    /// </summary>
    /// <remarks>
    /// The arithmetic is stock's, operation for operation, rather than the
    /// algebraically equal bilinear form. It is not the same in floating point:
    /// stock accumulates the row start by scaling an edge interval by the row
    /// number, and re-derives the column interval from that row's two
    /// endpoints, so a straight <c>lerp</c> of the four corners would disagree
    /// in the last bits and put the caster set out of parity with a recorded
    /// dump for no gain.
    /// </remarks>
    private static Vec3[] GenerateSurface(
        int power,
        Vec3[] points,
        ReadOnlySpan<DispVert> run,
        out float maxDisplacement)
    {
        int postSpacing = (1 << power) + 1;
        float ooInt = 1.0f / (postSpacing - 1);

        Vec3 edgeInt0 = (points[1] - points[0]) * ooInt;
        Vec3 edgeInt1 = (points[2] - points[3]) * ooInt;

        Vec3[] vertices = new Vec3[postSpacing * postSpacing];
        float largest = 0.0f;

        for (int i = 0; i < postSpacing; i++)
        {
            Vec3 end0 = (edgeInt0 * i) + points[0];
            Vec3 end1 = (edgeInt1 * i) + points[3];
            Vec3 segInt = (end1 - end0) * ooInt;

            for (int j = 0; j < postSpacing; j++)
            {
                int ndx = (i * postSpacing) + j;

                // m_FlatVert. Stock keeps it on the vertex because the lighting
                // path wants it later; the shadow path never looks at it again.
                Vec3 flat = end0 + (segInt * j);

                DispVert vert = run[ndx];
                Vec3 offset = vert.Vector * vert.Dist;
                vertices[ndx] = flat + offset;

                float length = offset.Length();
                if (length > largest)
                {
                    largest = length;
                }
            }
        }

        maxDisplacement = largest;
        return vertices;
    }

    /// <summary>
    /// <c>CCoreDispInfo::GenerateCollisionSurface</c>,
    /// </summary>
    /// <remarks>
    /// <para>
    /// The diagonal of each quad ALTERNATES, and the thing that decides it is
    /// the flat vertex index of the quad's lower-left post rather than its
    /// row/column parity: <c>bOdd = ( ( ndx % 2 ) == 1 )</c> with
    /// <c>ndx = iV * nWidth + iU</c>. Because
    /// <c>nWidth</c> is odd -- it is <c>2^power + 1</c> -- the parity flips
    /// consistently along each row and also from row to row, which is what
    /// makes the tessellation a checkerboard rather than a set of parallel
    /// stripes. Had the width been even, the same expression would have given
    /// stripes, and nobody writing it that way meant to depend on that.
    /// </para>
    /// <para>
    /// Stock does not run this loop at all until <c>CCoreDispInfo::Create</c>
    /// has already built and discarded an LOD tree
    /// <c>GenerateCollisionSurface</c> then
    /// resets <c>m_RenderIndexCount</c> to zero and
    /// writes the full, un-decimated list over the top. So the LOD tree
    /// contributes nothing to what vrad shadows against.
    /// </para>
    /// </remarks>
    private static int[] GenerateTriangleIndices(int power)
    {
        int nWidth = (1 << power) + 1;
        int[] indices = new int[(1 << power) * (1 << power) * 2 * 3];

        int count = 0;
        for (int iV = 0; iV < nWidth - 1; iV++)
        {
            for (int iU = 0; iU < nWidth - 1; iU++)
            {
                int ndx = (iV * nWidth) + iU;

                if (ndx % 2 == 1)
                {
                    // BuildTriTLtoBR.
                    indices[count++] = ndx;
                    indices[count++] = ndx + nWidth;
                    indices[count++] = ndx + 1;

                    indices[count++] = ndx + 1;
                    indices[count++] = ndx + nWidth;
                    indices[count++] = ndx + nWidth + 1;
                }
                else
                {
                    // BuildTriBLtoTR.
                    indices[count++] = ndx;
                    indices[count++] = ndx + nWidth;
                    indices[count++] = ndx + nWidth + 1;

                    indices[count++] = ndx;
                    indices[count++] = ndx + nWidth + 1;
                    indices[count++] = ndx + 1;
                }
            }
        }

        return indices;
    }
}
