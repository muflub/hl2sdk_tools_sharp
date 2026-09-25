using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Disp;

/// <summary>
/// One tessellated displacement: <c>CCoreDispInfo</c>,
/// <c>public/builddisp.h:705</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this port leaves out, and why.</b> Stock's <c>Create</c>
/// (<c>builddisp.cpp:2003</c>) runs eight passes; this runs all but the
/// quad-tree's per-node error terms.
/// </para>
/// <para>
/// <c>GenerateLODTree</c> builds the node array, and
/// <c>GenerateCollisionSurface</c> — which runs after it — sets
/// <c>m_RenderIndexCount</c> back to zero (<c>builddisp.cpp:943</c>) and
/// writes the full un-decimated index list over the top. The tree contributes
/// nothing to the triangles. The ONE thing any caller reads from it is the
/// root node's bounding box (<c>Disp_GridIndex</c>, <c>disp_ivp.cpp:56</c>),
/// which is the axis-aligned box of every vertex as it stood when
/// <c>Create</c> ran — exposed as <see cref="RootBounds"/>.
/// </para>
/// <para>
/// Normals and tangents (<c>GenerateDispSurfNormals</c>,
/// <c>GenerateDispSurfTangentSpaces</c>) reach no lump vbsp writes, but vrad
/// reads the normals and then sews them across neighbours
/// (<see cref="DispNormalSmoother"/>), so they are built here.
/// </para>
/// </remarks>
public sealed partial class CoreDispInfo : IDispUtils
{
    private readonly Vec3[] _fieldVectors;
    private readonly float[] _fieldDistances;
    private readonly Vec3[] _subdivPositions;
    private readonly Vec3[] _verts;
    private readonly Vec3[] _flatVerts;
    private readonly DispUv[] _texCoords;
    private readonly DispUv[] _luxelCoords;
    private readonly float[] _alphas;
    private readonly Vec3[] _normals;
    private readonly Vec3[] _tangentS;
    private readonly Vec3[] _tangentT;
    private readonly ushort[] _triIndices;
    private readonly ushort[] _triTags;
    private readonly uint[] _allowedVerts = new uint[AllowedVertsDWords];
    private IReadOnlyList<CoreDispInfo>? _listBase;

    /// <summary>
    /// The number of 32-bit words <c>ddispinfo_t::m_AllowedVerts</c> holds.
    /// </summary>
    /// <remarks>
    /// <c>PAD_NUMBER(MAX_DISPVERTS, 32) / 32</c> with <c>MAX_DISPVERTS</c> 289,
    /// so 320 bits for a power-4's 289 vertices. The 31 spare bits are SET by
    /// <see cref="AllowedVertsSetAll"/> and never cleared, so a power-2
    /// displacement — which uses 25 of the 320 — still ships nine words of
    /// <c>0xFFFFFFFF</c>. That is visible in any stock BSP and is what a gate
    /// over this field must expect.
    /// </remarks>
    public const int AllowedVertsDWords = 10;

    /// <summary>Builds a displacement with no geometry yet.</summary>
    /// <param name="power">2, 3 or 4.</param>
    /// <exception cref="ArgumentOutOfRangeException">The power is out of range.</exception>
    public CoreDispInfo(int power)
    {
        PowerInfo = PowerInfo.Get(power);

        int size = PowerInfo.MaxVerts;

        _fieldVectors = new Vec3[size];
        _fieldDistances = new float[size];
        _subdivPositions = new Vec3[size];
        _verts = new Vec3[size];
        _flatVerts = new Vec3[size];
        _texCoords = new DispUv[size];
        _luxelCoords = new DispUv[size * (CoreDispSurface.NumBumpVects + 1)];
        _alphas = new float[size];
        _normals = new Vec3[size];
        _tangentS = new Vec3[size];
        _tangentT = new Vec3[size];

        int triCount = TriCount;
        _triIndices = new ushort[triCount * 3];
        _triTags = new ushort[triCount];
    }

    /// <summary>The precalculated tables for this displacement's power.</summary>
    public PowerInfo PowerInfo { get; }

    /// <summary>The flat quad this displacement sits on.</summary>
    public CoreDispSurface Surface { get; } = new();

    /// <summary>The displacement power: 2, 3 or 4.</summary>
    public int Power => PowerInfo.Power;

    /// <summary>Vertices along one side.</summary>
    public int PostSpacing => PowerInfo.SideLength;

    /// <summary>The total vertex count.</summary>
    public int Size => PowerInfo.MaxVerts;

    /// <summary>
    /// The number of triangles: <c>GetTriCount</c>, <c>builddisp.cpp:2946</c>.
    /// </summary>
    public int TriCount => (PostSpacing - 1) * (PostSpacing - 1) * 2;

    /// <summary>
    /// This displacement's index in the list neighbour finding walks, which is
    /// also its LUMP_DISPINFO index.
    /// </summary>
    public int ListIndex { get; set; } = -1;

    /// <summary>The displaced world positions, row-major as <c>y * side + x</c>.</summary>
    public ReadOnlySpan<Vec3> Verts => _verts;

    /// <summary>The undisplaced positions on the base quad.</summary>
    public ReadOnlySpan<Vec3> FlatVerts => _flatVerts;

    /// <summary>The per-vertex blend alphas, straight from the VMF.</summary>
    public ReadOnlySpan<float> Alphas => _alphas;

    /// <summary>
    /// The per-vertex normals <see cref="Create"/> generated, possibly since
    /// sewn by <see cref="DispNormalSmoother"/>.
    /// </summary>
    public ReadOnlySpan<Vec3> Normals => _normals;

    /// <summary>The per-vertex S tangents: <c>m_TangentS</c>.</summary>
    /// <remarks>
    /// ZERO for every vertex in every stock run. The tangent space starts from
    /// the surface's <c>tAxis</c>, which <c>CCoreDispSurface::Init</c> clears
    /// (<c>builddisp.cpp:214</c>) and nothing under <c>src/utils</c> ever
    /// sets (<c>SetTAxis</c> has no caller). <c>VectorNormalize</c> of zero is
    /// zero, and every cross product that follows has a zero operand.
    /// </remarks>
    public ReadOnlySpan<Vec3> TangentS => _tangentS;

    /// <summary>The per-vertex T tangents: <c>m_TangentT</c>. See <see cref="TangentS"/>.</summary>
    public ReadOnlySpan<Vec3> TangentT => _tangentT;

    /// <summary>
    /// The quad-tree root's bounding box: <c>GetNode(0)->GetBoundingBox</c>,
    /// read by <c>Disp_GridIndex</c> (<c>disp_ivp.cpp:56</c>).
    /// </summary>
    /// <remarks>
    /// <c>CalcBoundingBoxAtNode</c> (<c>builddisp.cpp:1197</c>) unions each
    /// leaf's 3x3 block of vertices upward, so the root's box is the exact
    /// min/max of every vertex. It is computed ONCE, inside
    /// <see cref="Create"/>, so it describes the vertices BEFORE
    /// <c>SnapRemainingVertsToSurface</c> moved any — and it stays that way,
    /// because <c>SetVert</c> does not touch the tree.
    /// </remarks>
    public DispBox RootBounds { get; private set; }

    /// <summary>
    /// Reproduce stock's <c>VectorNormalize</c> estimate (the
    /// <see cref="Options.StockQuirk.DispVertNormalise"/> quirk) in the normals
    /// <see cref="Create"/> and <see cref="GetPositionOnSurface"/> produce,
    /// rather than dividing exactly.
    /// </summary>
    public bool StockNormalise { get; set; }

    /// <summary>
    /// Leave each vertex normal as the unnormalised MEAN of its fan, as stock
    /// does (<see cref="Options.StockQuirk.DispVertexNormalMeanUnnormalised"/>,
    /// <c>builddisp.cpp:1835</c>). False, the default, normalises it.
    /// </summary>
    public bool StockVertexNormalMean { get; set; }

    /// <summary>One vertex's normal.</summary>
    /// <param name="index">Its flattened grid index.</param>
    /// <returns>The normal.</returns>
    public Vec3 Normal(int index) => _normals[index];

    /// <summary>One vertex's normal, by grid position.</summary>
    /// <param name="index">The grid position.</param>
    /// <returns>The normal.</returns>
    public Vec3 Normal(VertIndex index) => _normals[PowerInfo.VertIndexToInt(index)];

    /// <summary>Replaces one vertex's normal: <c>SetNormal</c>.</summary>
    /// <param name="index">Its flattened grid index.</param>
    /// <param name="normal">The new normal.</param>
    public void SetNormal(int index, Vec3 normal) => _normals[index] = normal;

    /// <summary>Replaces one vertex's normal, by grid position.</summary>
    /// <param name="index">The grid position.</param>
    /// <param name="normal">The new normal.</param>
    public void SetNormal(VertIndex index, Vec3 normal) =>
        _normals[PowerInfo.VertIndexToInt(index)] = normal;

    private Vec3 Normalise(Vec3 v) =>
        StockNormalise ? v.NormaliseLikeStock().Normalised : v.Normalise().Normalised;

    /// <summary>The per-vertex texture coordinates.</summary>
    public ReadOnlySpan<DispUv> TexCoords => _texCoords;

    /// <summary>Three vertex indices per triangle, in the lump's order.</summary>
    public ReadOnlySpan<ushort> TriIndices => _triIndices;

    /// <summary>One tag word per triangle.</summary>
    public ReadOnlySpan<ushort> TriTags => _triTags;

    /// <summary>The allowed-vertex bit vector, ten words of 32 bits.</summary>
    public ReadOnlySpan<uint> AllowedVerts => _allowedVerts;

    /// <summary>One vertex's world position.</summary>
    /// <param name="index">Its flattened grid index.</param>
    /// <returns>The displaced position.</returns>
    public Vec3 Vert(int index) => _verts[index];

    /// <summary>Moves one vertex, as <c>SetVert</c> does.</summary>
    /// <param name="index">Its flattened grid index.</param>
    /// <param name="vert">The new position.</param>
    public void SetVert(int index, Vec3 vert) => _verts[index] = vert;

    /// <summary>One vertex's luxel coordinate for one bump set.</summary>
    /// <param name="bumpIndex">0..<see cref="CoreDispSurface.NumBumpVects"/>.</param>
    /// <param name="index">The flattened grid index.</param>
    /// <returns>The coordinate.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The bump index is out of range.</exception>
    public DispUv LuxelCoord(int bumpIndex, int index)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bumpIndex, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            bumpIndex, CoreDispSurface.NumBumpVects);

        return _luxelCoords[(bumpIndex * Size) + index];
    }

    /// <summary>The corner vertex at one <see cref="DispCorner"/>.</summary>
    /// <param name="corner">0..3.</param>
    /// <returns>Its DISPLACED position.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Not 0..3.</exception>
    /// <remarks>
    /// <b>Displaced, not flat</b>, and that is the whole reason corner
    /// neighbours behave differently from edge neighbours:
    /// <c>SetupEdgeNeighbors</c> matches on <c>GetSurface()-&gt;GetPoint()</c>,
    /// which is the base quad, while <c>SetupCornerNeighbors</c> matches on
    /// this, which has the displacement applied. Two displacements that share a
    /// corner of their base quads are NOT corner neighbours unless their
    /// displaced corners also agree to within 0.001 — measured on
    /// <c>p3f_disp_corner_only</c>, which produces no corner neighbours with a
    /// bumped field and one per corner with a flat one.
    /// </remarks>
    public Vec3 CornerPoint(int corner)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(corner, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(corner, 3);

        return _verts[PowerInfo.VertIndexToInt(PowerInfo.CornerPointIndex(corner))];
    }

    /// <inheritdoc />
    public ref DispNeighbor EdgeNeighbor(int index) => ref Surface.EdgeNeighbor(index);

    /// <inheritdoc />
    public ref DispCornerNeighbors CornerNeighbors(int index) =>
        ref Surface.CornerNeighbors(index);

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// <see cref="SetListBase"/> has not been called.
    /// </exception>
    public IDispUtils? ByIndex(int index)
    {
        if (_listBase is null)
        {
            throw new InvalidOperationException(
                "this displacement has no neighbour list; call SetListBase first "
                + "(CCoreDispInfo::SetDispUtilsHelperInfo, builddisp.cpp:866).");
        }

        return index == DispSubNeighbor.NoNeighbor ? null : _listBase[index];
    }

    /// <summary>
    /// Gives this displacement the list its neighbour indices refer to:
    /// <c>SetDispUtilsHelperInfo</c>, <c>builddisp.cpp:866</c>.
    /// </summary>
    /// <param name="list">Every displacement in the map, in LUMP_DISPINFO order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="list"/> is null.</exception>
    public void SetListBase(IReadOnlyList<CoreDispInfo> list)
    {
        ArgumentNullException.ThrowIfNull(list);
        _listBase = list;
    }

    /// <summary>Sets every allowed-vertex bit, spare bits included.</summary>
    /// <remarks><c>CBitVec::SetAll</c>, which memsets every word to 0xFF.</remarks>
    public void AllowedVertsSetAll() => Array.Fill(_allowedVerts, 0xFFFFFFFFu);

    /// <summary>Whether one vertex is allowed.</summary>
    /// <param name="index">Its flattened grid index.</param>
    /// <returns>True when the bit is set.</returns>
    public bool AllowedVertsGet(int index) =>
        (_allowedVerts[index >> 5] & (1u << (index & 31))) != 0;

    /// <summary>Clears one vertex's allowed bit.</summary>
    /// <param name="index">Its flattened grid index.</param>
    public void AllowedVertsClear(int index) =>
        _allowedVerts[index >> 5] &= ~(1u << (index & 31));

    /// <summary>
    /// Loads the per-vertex field and tags:
    /// <c>InitDispInfo</c>, <c>builddisp.cpp:752</c>.
    /// </summary>
    /// <param name="minTess">
    /// The VMF's <c>mintess</c>, or — as vbsp always passes — the surface flags
    /// with the top bit set.
    /// </param>
    /// <param name="alphas">One alpha per vertex.</param>
    /// <param name="fieldVectors">One unit direction per vertex.</param>
    /// <param name="fieldDistances">One distance per vertex.</param>
    /// <exception cref="ArgumentException">A span is the wrong length.</exception>
    /// <remarks>
    /// The top bit of <paramref name="minTess"/> is a TYPE TAG, not a value:
    /// set, the remaining 31 bits are <c>SURF_*</c> flags and go to the
    /// surface; clear, the whole thing was a tessellation level and is dropped
    /// on the floor. vbsp always sets it (<c>disp_vbsp.cpp:327</c>), so
    /// <c>ddispinfo_t::minTess</c> in every modern BSP is <c>0x80000000 |
    /// flags</c> and never a tessellation level.
    /// </remarks>
    public void InitDispInfo(
        int minTess,
        ReadOnlySpan<float> alphas,
        ReadOnlySpan<Vec3> fieldVectors,
        ReadOnlySpan<float> fieldDistances)
    {
        if (alphas.Length < Size || fieldVectors.Length < Size ||
            fieldDistances.Length < Size)
        {
            throw new ArgumentException(
                $"a power-{Power} displacement has {Size} vertices; got "
                + $"{alphas.Length} alphas, {fieldVectors.Length} vectors and "
                + $"{fieldDistances.Length} distances.",
                nameof(alphas));
        }

        if ((minTess & unchecked((int)0x80000000)) != 0)
        {
            Surface.Flags = minTess & ~unchecked((int)0x80000000);
        }

        for (int i = 0; i < Size; i++)
        {
            _fieldVectors[i] = fieldVectors[i];
            _fieldDistances[i] = fieldDistances[i];
            _alphas[i] = alphas[i];
        }

        Array.Clear(_triTags);
    }

    /// <summary>
    /// Loads the field and tags from a BSP's own lumps: the
    /// <c>CDispVert</c>/<c>CDispTri</c> overload of <c>InitDispInfo</c>,
    /// <c>builddisp.cpp:841</c>, which is how vrad rebuilds a displacement
    /// (<c>vraddisps.cpp:418</c>).
    /// </summary>
    /// <param name="minTess"><c>ddispinfo_t::minTess</c>.</param>
    /// <param name="verts">This displacement's run of LUMP_DISP_VERTS.</param>
    /// <param name="tris">Its run of LUMP_DISP_TRIS.</param>
    /// <exception cref="ArgumentException">A run is shorter than the power needs.</exception>
    /// <remarks>
    /// The lump's <c>m_vVector</c> is the field direction and
    /// <c>m_flDist</c> the distance, so the displaced position is rebuilt as
    /// <c>flat + vector * dist</c> exactly as the VMF path does — including for
    /// the vertices <c>SnapRemainingVertsToSurface</c> rewrote, whose vector
    /// is no longer unit and whose distance is 1.
    /// </remarks>
    public void InitDispInfo(int minTess, ReadOnlySpan<DispVert> verts, ReadOnlySpan<DispTri> tris)
    {
        if (verts.Length < Size || tris.Length < TriCount)
        {
            throw new ArgumentException(
                $"a power-{Power} displacement has {Size} vertices and {TriCount} triangles; "
                + $"got {verts.Length} and {tris.Length}.",
                nameof(verts));
        }

        float[] alphas = new float[Size];
        Vec3[] vectors = new Vec3[Size];
        float[] dists = new float[Size];

        for (int i = 0; i < Size; i++)
        {
            vectors[i] = verts[i].Vector;
            dists[i] = verts[i].Dist;
            alphas[i] = verts[i].Alpha;
        }

        InitDispInfo(minTess, alphas, vectors, dists);

        for (int t = 0; t < TriCount; t++)
        {
            _triTags[t] = tris[t].Tags;
        }
    }

    /// <summary>Sets the tag word of one triangle.</summary>
    /// <param name="triangle">The triangle index.</param>
    /// <param name="tags">The tags.</param>
    public void SetTriTagValue(int triangle, ushort tags) => _triTags[triangle] = tags;

    /// <summary>
    /// Builds the vertex grid, the coordinates and the triangle list:
    /// <c>Create</c>, <c>builddisp.cpp:2003</c>.
    /// </summary>
    /// <remarks>
    /// Stock returns false when the surface has other than four points; there
    /// is no such state here, because <see cref="CoreDispSurface"/> is four
    /// points by construction.
    /// </remarks>
    public void Create()
    {
        GenerateDispSurf();
        GenerateDispSurfNormals();
        GenerateDispSurfTangentSpaces();

        CalcDispSurfCoords(lightMap: false, 0);
        for (int bump = 0; bump < CoreDispSurface.NumBumpVects + 1; bump++)
        {
            CalcDispSurfCoords(lightMap: true, bump);
        }

        RootBounds = CalcRootBounds();

        GenerateCollisionSurface();
    }

    /// <summary>
    /// The root node's box, as <see cref="RootBounds"/> describes: the exact
    /// min/max of every vertex, which is what <c>CalcBoundingBoxAtNode</c>'s
    /// union of 3x3 leaf blocks comes to (<c>builddisp.cpp:1155-1265</c>).
    /// </summary>
    private DispBox CalcRootBounds()
    {
        Vec3 min = _verts[0];
        Vec3 max = _verts[0];

        for (int i = 1; i < Size; i++)
        {
            Vec3 v = _verts[i];
            min = new Vec3(
                min.X > v.X ? v.X : min.X,
                min.Y > v.Y ? v.Y : min.Y,
                min.Z > v.Z ? v.Z : min.Z);
            max = new Vec3(
                max.X < v.X ? v.X : max.X,
                max.Y < v.Y ? v.Y : max.Y,
                max.Z < v.Z ? v.Z : max.Z);
        }

        return new DispBox(min, max);
    }

    /// <summary>
    /// Whether the grid has a neighbour in one direction:
    /// <c>DoesEdgeExist</c>, <c>builddisp.cpp:1854</c>.
    /// </summary>
    /// <param name="row">The column index, <c>x</c> (stock calls it the row).</param>
    /// <param name="col">The row index, <c>y</c>.</param>
    /// <param name="direction">0 left, 1 top, 2 right, 3 bottom.</param>
    /// <param name="postSpacing">Vertices along a side.</param>
    /// <returns>True when a vertex exists that way.</returns>
    public static bool DoesEdgeExist(int row, int col, int direction, int postSpacing) =>
        direction switch
        {
            0 => row - 1 >= 0,
            1 => col + 1 <= postSpacing - 1,
            2 => row + 1 <= postSpacing - 1,
            3 => col - 1 >= 0,
            _ => false,
        };

    /// <summary>
    /// <c>GenerateDispSurfNormals</c>, <c>builddisp.cpp:1886</c>.
    /// </summary>
    private void GenerateDispSurfNormals()
    {
        int postSpacing = PostSpacing;
        Span<bool> isEdge = stackalloc bool[4];

        for (int i = 0; i < postSpacing; i++)
        {
            for (int j = 0; j < postSpacing; j++)
            {
                for (int k = 0; k < 4; k++)
                {
                    isEdge[k] = DoesEdgeExist(j, i, k, postSpacing);
                }

                _normals[(i * postSpacing) + j] = CalcNormalFromEdges(j, i, isEdge);
            }
        }
    }

    /// <summary>
    /// The mean of the unit normals of up to eight triangles around one
    /// vertex: <c>CalcNormalFromEdges</c>, <c>builddisp.cpp:1732</c>.
    /// </summary>
    /// <remarks>
    /// Stock does NOT renormalise the mean (<c>:1835</c> scales by
    /// <c>1/normalCount</c> and stops), so a vertex on a crease carries a
    /// normal shorter than one; <see cref="StockVertexNormalMean"/> chooses. Each quadrant contributes its two triangles
    /// whatever the tessellation's diagonal actually is there: this is a
    /// fixed fan, not the rendered triangles.
    /// </remarks>
    private Vec3 CalcNormalFromEdges(int row, int col, ReadOnlySpan<bool> isEdge)
    {
        Vec3 accum = Vec3.Zero;
        int count = 0;

        if (isEdge[1] && isEdge[2])
        {
            AddFanNormal(ref accum, ref count, V(col + 1, row), V(col, row), V(col, row + 1));
            AddFanNormal(ref accum, ref count, V(col + 1, row), V(col, row + 1), V(col + 1, row + 1));
        }

        if (isEdge[0] && isEdge[1])
        {
            AddFanNormal(ref accum, ref count, V(col + 1, row - 1), V(col, row - 1), V(col, row));
            AddFanNormal(ref accum, ref count, V(col + 1, row - 1), V(col, row), V(col + 1, row));
        }

        if (isEdge[0] && isEdge[3])
        {
            AddFanNormal(ref accum, ref count, V(col, row - 1), V(col - 1, row - 1), V(col - 1, row));
            AddFanNormal(ref accum, ref count, V(col, row - 1), V(col - 1, row), V(col, row));
        }

        if (isEdge[2] && isEdge[3])
        {
            AddFanNormal(ref accum, ref count, V(col, row), V(col - 1, row), V(col - 1, row + 1));
            AddFanNormal(ref accum, ref count, V(col, row), V(col - 1, row + 1), V(col, row + 1));
        }

        Vec3 mean = accum * (1.0f / count);

        // builddisp.cpp:1835 stops at the mean; every consumer uses the result
        // as a direction (see StockQuirk.DispVertexNormalMeanUnnormalised).
        return StockVertexNormalMean ? mean : Normalise(mean);
    }

    private Vec3 V(int col, int row) => _verts[(col * PostSpacing) + row];

    /// <summary>
    /// One fan triangle: <c>tmpVect[0] = a - origin</c>,
    /// <c>tmpVect[1] = b - origin</c>, normal
    /// <c>CrossProduct(tmpVect[1], tmpVect[0])</c> normalised and summed.
    /// </summary>
    private void AddFanNormal(ref Vec3 accum, ref int count, Vec3 a, Vec3 origin, Vec3 b)
    {
        Vec3 t0 = a - origin;
        Vec3 t1 = b - origin;
        accum += Normalise(Vec3.Cross(t1, t0));
        count++;
    }

    /// <summary>
    /// <c>GenerateDispSurfTangentSpaces</c>, <c>builddisp.cpp:1692</c>.
    /// </summary>
    /// <remarks>
    /// Written out in full although its output is always zero (see
    /// <see cref="TangentS"/>): the zero is a consequence of the axes, and a
    /// caller that sets them gets stock's tangents rather than silence.
    /// </remarks>
    private void GenerateDispSurfTangentSpaces()
    {
        Vec3 sAxis = Surface.SAxis;
        Vec3 tAxis = Surface.TAxis;

        for (int i = 0; i < Size; i++)
        {
            Vec3 t = Normalise(tAxis);
            Vec3 s = Normalise(Vec3.Cross(_normals[i], t));
            t = Normalise(Vec3.Cross(s, _normals[i]));

            Vec3 planeNormal = Surface.GetNormal();
            if (Vec3.Dot(planeNormal, Vec3.Cross(sAxis, tAxis)) > 0.0f)
            {
                s *= -1.0f;
            }

            _tangentS[i] = s;
            _tangentT[i] = t;
        }
    }

    /// <summary>
    /// <c>GenerateDispSurf</c>, <c>builddisp.cpp:1918</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The arithmetic is stock's operation for operation rather than the
    /// algebraically equal bilinear form, because the two disagree in the last
    /// bits: the row start is an edge interval SCALED BY THE ROW NUMBER and
    /// added to a corner, and the column interval is re-derived from that row's
    /// two endpoints.
    /// </para>
    /// <para>
    /// Two of stock's three additive terms are dead in vbsp. The elevation is
    /// zero — nothing under <c>src/utils/vbsp</c> calls
    /// <c>SetElevation</c> — and the subdivision position is zero because
    /// Hammer bakes subdivision into the field vectors before writing the VMF,
    /// which stock's own comment says in as many words at
    /// <c>builddisp.cpp:816</c> ("offset have been combined with fieldvectors
    /// at this point!!!"). They are kept as terms so that the arithmetic is
    /// stock's, and their arrays stay zero.
    /// </para>
    /// </remarks>
    private void GenerateDispSurf()
    {
        ReadOnlySpan<Vec3> points = Surface.Points;

        int postSpacing = PostSpacing;
        float ooInt = 1.0f / (postSpacing - 1);

        Vec3 edgeInt0 = (points[1] - points[0]) * ooInt;
        Vec3 edgeInt1 = (points[2] - points[3]) * ooInt;

        for (int i = 0; i < postSpacing; i++)
        {
            Vec3 end0 = (edgeInt0 * i) + points[0];
            Vec3 end1 = (edgeInt1 * i) + points[3];
            Vec3 segInt = (end1 - end0) * ooInt;

            for (int j = 0; j < postSpacing; j++)
            {
                int ndx = (i * postSpacing) + j;

                _flatVerts[ndx] = end0 + (segInt * j);

                _verts[ndx] = _flatVerts[ndx]
                    + _subdivPositions[ndx]
                    + (_fieldVectors[ndx] * _fieldDistances[ndx]);
            }
        }
    }

    /// <summary>
    /// <c>CalcDispSurfCoords</c>, <c>builddisp.cpp:1549</c>: the same bilinear
    /// walk in 2D, over texture or luxel coordinates.
    /// </summary>
    private void CalcDispSurfCoords(bool lightMap, int lightmapId)
    {
        Span<DispUv> corners = stackalloc DispUv[4];
        for (int i = 0; i < 4; i++)
        {
            corners[i] = lightMap ? Surface.LuxelCoord(lightmapId, i) : Surface.TexCoords[i];
        }

        int postSpacing = PostSpacing;
        float ooInt = 1.0f / (postSpacing - 1);

        DispUv edgeInt0 = (corners[1] - corners[0]) * ooInt;
        DispUv edgeInt1 = (corners[2] - corners[3]) * ooInt;

        for (int i = 0; i < postSpacing; i++)
        {
            DispUv end0 = (edgeInt0 * i) + corners[0];
            DispUv end1 = (edgeInt1 * i) + corners[3];
            DispUv segInt = (end1 - end0) * ooInt;

            for (int j = 0; j < postSpacing; j++)
            {
                DispUv value = end0 + (segInt * j);

                if (lightMap)
                {
                    _luxelCoords[(lightmapId * Size) + (i * postSpacing) + j] = value;
                }
                else
                {
                    _texCoords[(i * postSpacing) + j] = value;
                }
            }
        }
    }

    /// <summary>
    /// <c>GenerateCollisionSurface</c> plus <c>CreateTris</c>,
    /// <c>builddisp.cpp:934</c> and <c>:3033</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The diagonal of each quad ALTERNATES, and what decides it is the flat
    /// index of the quad's lower-left post rather than its row and column:
    /// <c>bOdd = ((ndx % 2) == 1)</c> with <c>ndx = iV * nWidth + iU</c>
    /// (<c>builddisp.cpp:948</c>). Because <c>nWidth</c> is <c>2^power + 1</c>
    /// and therefore ODD, the parity flips along each row and again from row to
    /// row, which is what makes the result a checkerboard rather than stripes.
    /// An even width would have given stripes from the same expression, and
    /// nothing in the code says that was intended.
    /// </para>
    /// <para>
    /// Stock builds this into <c>m_RenderIndices</c> and then copies it into
    /// <c>m_pTris</c> in a second pass. There is one array here, because the
    /// only thing the two-step buys stock is the LOD tree's chance to have
    /// written a shorter list first — and it resets the count to zero before
    /// this loop, so it never does.
    /// </para>
    /// </remarks>
    private void GenerateCollisionSurface()
    {
        int width = PostSpacing;
        int count = 0;

        for (int v = 0; v < width - 1; v++)
        {
            for (int u = 0; u < width - 1; u++)
            {
                int ndx = (v * width) + u;

                if (ndx % 2 == 1)
                {
                    // BuildTriTLtoBR, builddisp.cpp:896.
                    _triIndices[count++] = (ushort)ndx;
                    _triIndices[count++] = (ushort)(ndx + width);
                    _triIndices[count++] = (ushort)(ndx + 1);

                    _triIndices[count++] = (ushort)(ndx + 1);
                    _triIndices[count++] = (ushort)(ndx + width);
                    _triIndices[count++] = (ushort)(ndx + width + 1);
                }
                else
                {
                    // BuildTriBLtoTR, builddisp.cpp:915.
                    _triIndices[count++] = (ushort)ndx;
                    _triIndices[count++] = (ushort)(ndx + width);
                    _triIndices[count++] = (ushort)(ndx + width + 1);

                    _triIndices[count++] = (ushort)ndx;
                    _triIndices[count++] = (ushort)(ndx + width + 1);
                    _triIndices[count++] = (ushort)(ndx + 1);
                }
            }
        }
    }
}
