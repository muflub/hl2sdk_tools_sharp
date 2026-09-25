using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Rad.Light;

namespace SourceSharp.MapTools.Rad.Displacement;

/// <summary>
/// One displacement as vrad lights it: stock's <c>CVRADDispColl</c>
/// Over its base
/// <c>CDispCollTree</c>.
/// </summary>
/// <remarks>
/// <para>
/// Built from the <see cref="CoreDispInfo"/> that
/// <see cref="DispLightingLoader.Load"/> returns: the created, neighbour-sewn
/// core, whose vertices, vertex normals and luxel coordinates are copied here
/// exactly as <c>CVRADDispColl::Create</c> and
/// <c>AABBTree_CopyDispData</c> copy them.
/// Everything vrad reads of a displacement afterwards -- the samples and luxels
/// (<see cref="DispSampleBuilder"/>), the patch tree
/// (<see cref="DispPatchBuilder"/>), the radial filter
/// (<see cref="DispRadial"/>) and the ray test (<see cref="DispCollision"/>) --
/// reads this object and never the core again.
/// </para>
/// <para>
/// Immutable after construction, so every face job may read it from any
/// thread.
/// </para>
/// </remarks>
public sealed class VradDispSurface
{
    /// <summary><c>TRIEDGE_EPSILON</c>.</summary>
    public const float TriEdgeEpsilon = 0.001f;

    private readonly Vec3[] _verts;
    private readonly Vec3[] _vertNormals;
    private readonly DispUv[] _luxelCoords;
    private readonly Vec3[] _surfPoints;
    private readonly DispCollTri[] _tris;

    private VradDispSurface(
        int index,
        int parentFace,
        int power,
        int contents,
        int flags,
        Vec3[] verts,
        Vec3[] vertNormals,
        DispUv[] luxelCoords,
        Vec3[] surfPoints,
        Vec3 stabDirection,
        DispCollTri[] tris,
        bool stockNormalise)
    {
        Index = index;
        ParentFace = parentFace;
        Power = power;
        Contents = contents;
        Flags = flags;
        _verts = verts;
        _vertNormals = vertNormals;
        _luxelCoords = luxelCoords;
        _surfPoints = surfPoints;
        StabDirection = stabDirection;
        _tris = tris;
        StockNormalise = stockNormalise;
        Tree = DispCollisionTree.Build(this);
    }

    /// <summary>This displacement's index in LUMP_DISPINFO.</summary>
    public int Index { get; }

    /// <summary>
    /// <c>m_iParent</c>: the base face, from the core surface's handle
    /// </summary>
    public int ParentFace { get; }

    /// <summary><c>m_nPower</c>.</summary>
    public int Power { get; }

    /// <summary><c>GetWidth()</c>: <c>2^power + 1</c> vertices per side.</summary>
    public int Width => (1 << Power) + 1;

    /// <summary><c>GetSize()</c>: the vertex count.</summary>
    public int Size => Width * Width;

    /// <summary><c>GetTriSize()</c>: the triangle count.</summary>
    public int TriCount => (1 << Power) * (1 << Power) * 2;

    /// <summary><c>m_nContents</c>, from the DISPINFO entry.</summary>
    public int Contents { get; }

    /// <summary>
    /// <c>m_nFlags</c>, the core surface's flags. vrad's
    /// <c>DispBuilderInit</c> never sets them, so they are zero and
    /// <c>SURF_NORAY_COLL</c> never excludes a displacement from a ray test.
    /// </summary>
    public int Flags { get; }

    /// <summary>
    /// <c>m_vecStabDir</c>: the base face normal as <c>CCoreDispSurface::GetNormal</c>
    /// gives it. Kept for completeness: nothing in vrad reads it
    /// (<c>GetParentFaceNormal</c> has no caller).
    /// </summary>
    public Vec3 StabDirection { get; }

    /// <summary>Whether <c>VectorNormalize</c> takes stock's estimate (<see cref="Options.StockQuirk.VradVectorNormalise"/>).</summary>
    public bool StockNormalise { get; }

    /// <summary><c>m_aVerts</c>: the displaced vertices, row by row.</summary>
    public ReadOnlySpan<Vec3> Verts => _verts;

    /// <summary><c>m_aVertNormals</c>: the sewn vertex normals.</summary>
    public ReadOnlySpan<Vec3> VertNormals => _vertNormals;

    /// <summary><c>m_aLuxelCoords</c>: each vertex's lightmap coordinate (bump set 0).</summary>
    public ReadOnlySpan<DispUv> LuxelCoords => _luxelCoords;

    /// <summary>
    /// <c>m_vecSurfPoints</c>: the base quad's four points, in the order
    /// <c>AdjustSurfPointData</c> left them (start corner first).
    /// </summary>
    public ReadOnlySpan<Vec3> SurfPoints => _surfPoints;

    /// <summary><c>m_aTris</c>.</summary>
    public ReadOnlySpan<DispCollTri> Tris => _tris;

    /// <summary>The AABB tree and bounds (<c>AABBTree_CreateLeafs</c>, <c>AABBTree_CalcBounds</c>).</summary>
    public DispCollisionTree Tree { get; }

    /// <summary><c>m_flSampleWidth</c>: one luxel's size in world units.</summary>
    public float SampleWidth { get; private init; }

    /// <summary><c>m_flSampleHeight</c>: the same as <see cref="SampleWidth"/> (stock's "Todo").</summary>
    public float SampleHeight { get; private init; }

    /// <summary><c>m_flSampleRadius2</c>: the squared radius of the luxel radial filter.</summary>
    public float SampleRadius2 { get; private init; }

    /// <summary><c>m_flPatchSampleRadius2</c>: the squared radius of the patch radial filter.</summary>
    public float PatchSampleRadius2 { get; private init; }

    /// <summary>
    /// True when <c>CalcSampleRadius2AndBox</c> clamped the patch radius to
    /// <c>g_MaxDispPatchRadius</c> and printed "Patch Sample Radius Clamped!".
    /// </summary>
    public bool PatchRadiusClamped { get; private init; }

    /// <summary>
    /// <c>CVRADDispColl::Create</c>.
    /// </summary>
    /// <param name="core">The created, sewn core.</param>
    /// <param name="tex">The base face's texinfo.</param>
    /// <param name="settings">The displacement switches.</param>
    /// <param name="stockNormalise">Whether <c>VectorNormalize</c> is stock's estimate.</param>
    /// <returns>The surface.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="core"/> or <paramref name="settings"/> is null.</exception>
    public static VradDispSurface Create(
        CoreDispInfo core, in TexInfo tex, DirectLightingSettings settings, bool stockNormalise)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(settings);

        int size = core.Size;
        Vec3[] verts = new Vec3[size];
        Vec3[] normals = new Vec3[size];
        DispUv[] luxel = new DispUv[size];
        for (int i = 0; i < size; i++)
        {
            verts[i] = core.Vert(i);
            normals[i] = core.Normal(i);
            luxel[i] = core.LuxelCoord(0, i);
        }

        CoreDispSurface surf = core.Surface;
        Vec3[] points = [.. surf.Points];

        // Triangles from the core's index list,
 // each with its plane (CalcPlane).
        ReadOnlySpan<ushort> indices = core.TriIndices;
        int triCount = (1 << core.Power) * (1 << core.Power) * 2;
        DispCollTri[] tris = new DispCollTri[triCount];
        for (int t = 0; t < triCount; t++)
        {
            ushort a = indices[(t * 3) + 0];
            ushort b = indices[(t * 3) + 1];
            ushort c = indices[(t * 3) + 2];
            Vec3 e0 = verts[b] - verts[a];
            Vec3 e1 = verts[c] - verts[a];
            Vec3 n = Normalise(Vec3.Cross(e1, e0), stockNormalise);
            tris[t] = new DispCollTri(a, b, c, n, Vec3.Dot(n, verts[a]));
        }

        (float width, float r2, float pr2, bool clamped) = SampleRadii(tex, settings);

        return new VradDispSurface(
            core.ListIndex,
            surf.Handle,
            core.Power,
            surf.Contents,
            surf.Flags,
            verts,
            normals,
            luxel,
            points,
            surf.GetNormal(),
            tris,
            stockNormalise)
        {
            SampleWidth = width,
            SampleHeight = width,
            SampleRadius2 = r2,
            PatchSampleRadius2 = pr2,
            PatchRadiusClamped = clamped,
        };
    }

    /// <summary>
    /// <c>CalcSampleRadius2AndBox</c>: the luxel
    /// size, the luxel radial radius squared and the patch radial radius
    /// squared.
    /// </summary>
    /// <param name="tex">The base face's texinfo.</param>
    /// <param name="settings">The displacement switches.</param>
    /// <returns>The luxel width, both squared radii, and whether the patch radius was clamped.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    /// <remarks>
    /// The luxel size is <c>1 / |lightmap s axis|</c>, and the height is set to
    /// the width ("Width = Height now"). The radius is
    /// <c>sqrt(w^2 + h^2) * 2.2</c> -- the <c>sqrt</c> is the double one, the
    /// product narrowed -- clamped to <c>-maxdispsamplesize</c>; the patch
    /// radius is <c>max(w, h) * dispchop * 2.2</c> clamped to
    /// <c>g_MaxDispPatchRadius</c>.
    /// </remarks>
    public static (float Width, float SampleRadius2, float PatchSampleRadius2, bool Clamped) SampleRadii(
        in TexInfo tex, DirectLightingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        FloatArray8 lm = tex.LightmapVecsLuxelsPerWorldUnits;
        Vec3 u = new(lm[0], lm[1], lm[2]);
        float width = 1.0f / u.Length();
        float height = width;

        // sqrt of a float argument -> the double sqrt; * 2.2f is a float product.
        float radius = (float)Math.Sqrt((width * width) + (height * height)) * 2.2f;
        if (radius > settings.MaxDispSampleSize)
        {
            radius = settings.MaxDispSampleSize;
        }

        float size = Math.Max(width, height);
        float patchRadius = size * settings.DispChop * 2.2f;
        bool clamped = false;
        if (patchRadius > settings.MaxDispPatchRadius)
        {
            patchRadius = settings.MaxDispPatchRadius;
            clamped = true;
        }

        return (width, radius * radius, patchRadius * patchRadius, clamped);
    }

    /// <summary>
    /// <c>DispUVToSurfPoint</c>: the point on
    /// the displaced surface at a parametric (u, v), optionally pushed off the
    /// surface along the triangle's normal.
    /// </summary>
    /// <param name="u">U in [0, 1].</param>
    /// <param name="v">V in [0, 1].</param>
    /// <param name="pushEps">How far off the surface; 0 for none.</param>
    /// <param name="point">The point; UNCHANGED when (u, v) is outside [0, 1].</param>
    /// <returns>False, leaving <paramref name="point"/> alone, when (u, v) is off the surface.</returns>
    /// <remarks>
    /// The grid is split into the triangles <c>CCoreDispInfo</c> made: a cell
    /// whose flattened index is odd runs top-left to bottom-right, even cells
    /// bottom-left to top-right. The scale is <c>(width - 1.000001)</c>, which
    /// keeps the snapped cell inside the grid at u = 1.
    /// </remarks>
    public bool DispUVToSurfPoint(float u, float v, float pushEps, ref Vec3 point)
    {
        if (u < 0.0f || u > 1.0f || v < 0.0f || v > 1.0f)
        {
            return false;
        }

        int width = Width;
        int height = width;
        float fu = u * ((float)width - 1.000001f);
        float fv = v * ((float)height - 1.000001f);
        int snapU = (int)fu;
        int snapV = (int)fv;

        bool odd = (((snapV * width) + snapU) % 2) == 1;
        point = odd
            ? TriTLToBR(pushEps, fu, fv, snapU, snapV, width, height)
            : TriBLToTR(pushEps, fu, fv, snapU, snapV, width, height);
        return true;
    }

    private Vec3 TriTLToBR(float pushEps, float fu, float fv, int snapU, int snapV, int width, int height)
    {
        int nextU = snapU + 1;
        int nextV = snapV + 1;
        if (nextU == width)
        {
            --nextU;
        }

        if (nextV == height)
        {
            --nextV;
        }

        float fracU = fu - snapU;
        float fracV = fv - snapV;

        Vec3 point;
        Vec3 edgeU;
        Vec3 edgeV;
        if ((fracU + fracV) >= (1.0f + TriEdgeEpsilon))
        {
            int i0 = (nextV * width) + snapU;
            int i1 = (nextV * width) + nextU;
            int i2 = (snapV * width) + nextU;
            edgeU = _verts[i0] - _verts[i1];
            edgeV = _verts[i2] - _verts[i1];
            point = _verts[i1] + (edgeU * (1.0f - fracU)) + (edgeV * (1.0f - fracV));
        }
        else
        {
            int i0 = (snapV * width) + snapU;
            int i1 = (nextV * width) + snapU;
            int i2 = (snapV * width) + nextU;
            edgeU = _verts[i2] - _verts[i0];
            edgeV = _verts[i1] - _verts[i0];
            point = _verts[i0] + (edgeU * fracU) + (edgeV * fracV);
        }

        if (pushEps != 0.0f)
        {
            Vec3 normal = Normalise(Vec3.Cross(edgeU, edgeV), StockNormalise);
            point += normal * pushEps;
        }

        return point;
    }

    private Vec3 TriBLToTR(float pushEps, float fu, float fv, int snapU, int snapV, int width, int height)
    {
        int nextU = snapU + 1;
        int nextV = snapV + 1;
        if (nextU == width)
        {
            --nextU;
        }

        if (nextV == height)
        {
            --nextV;
        }

        float fracU = fu - snapU;
        float fracV = fv - snapV;

        Vec3 point;
        Vec3 edgeU;
        Vec3 edgeV;
        if (fracU < fracV)
        {
            int i0 = (snapV * width) + snapU;
            int i1 = (nextV * width) + snapU;
            int i2 = (nextV * width) + nextU;
            edgeU = _verts[i2] - _verts[i1];
            edgeV = _verts[i0] - _verts[i1];
            point = _verts[i1] + (edgeU * fracU) + (edgeV * (1.0f - fracV));
        }
        else
        {
            int i0 = (snapV * width) + snapU;
            int i1 = (nextV * width) + nextU;
            int i2 = (snapV * width) + nextU;
            edgeU = _verts[i0] - _verts[i2];
            edgeV = _verts[i1] - _verts[i2];
            point = _verts[i2] + (edgeU * (1.0f - fracU)) + (edgeV * fracV);
        }

        if (pushEps != 0.0f)
        {
            // BLToTR crosses edgeV x edgeU, the reverse of TLToBR.
            Vec3 normal = Normalise(Vec3.Cross(edgeV, edgeU), StockNormalise);
            point += normal * pushEps;
        }

        return point;
    }

    /// <summary>
    /// <c>DispUVToSurfNormal</c>: the vertex
    /// normals of the cell around (u, v), blended bilinearly.
    /// </summary>
    /// <param name="u">U in [0, 1].</param>
    /// <param name="v">V in [0, 1].</param>
    /// <param name="normal">The normal; UNCHANGED when (u, v) is outside [0, 1].</param>
    /// <returns>False, leaving <paramref name="normal"/> alone, when (u, v) is off the surface.</returns>
    /// <remarks>
    /// <para>
    /// Two lerps along u, each normalised, then one along v, normalised
 /// A vertex normal that is not unit length therefore
    /// weighs less in the first lerp than its share of u -- which is what stock's
    /// short crease normals do
    /// (<see cref="Options.StockQuirk.DispVertexNormalMeanUnnormalised"/>,
    /// fixed in the loader, not here).
    /// </para>
    /// </remarks>
    public bool DispUVToSurfNormal(float u, float v, ref Vec3 normal)
    {
        if (u < 0.0f || u > 1.0f || v < 0.0f || v > 1.0f)
        {
            return false;
        }

        int width = Width;
        int height = width;
        float fu = u * ((float)width - 1.000001f);
        float fv = v * ((float)height - 1.000001f);
        int snapU = (int)fu;
        int snapV = (int)fv;
        int nextU = snapU + 1;
        int nextV = snapV + 1;
        if (nextU == width)
        {
            --nextU;
        }

        if (nextV == height)
        {
            --nextV;
        }

        float fracU = fu - snapU;
        float fracV = fv - snapV;

        Vec3 n0 = _vertNormals[(snapV * width) + snapU];
        Vec3 n1 = _vertNormals[(nextV * width) + snapU];
        Vec3 n2 = _vertNormals[(nextV * width) + nextU];
        Vec3 n3 = _vertNormals[(snapV * width) + nextU];

        Vec3 blend0 = Normalise((n0 * (1.0f - fracU)) + (n3 * fracU), StockNormalise);
        Vec3 blend1 = Normalise((n1 * (1.0f - fracU)) + (n2 * fracU), StockNormalise);
        normal = Normalise((blend0 * (1.0f - fracV)) + (blend1 * fracV), StockNormalise);
        return true;
    }

    /// <summary><c>VectorNormalize</c>, stock's estimate or an exact divide.</summary>
    /// <param name="v">The vector.</param>
    /// <param name="stockNormalise">True for stock's estimate.</param>
    /// <returns>The unit vector (zero for zero).</returns>
    public static Vec3 Normalise(Vec3 v, bool stockNormalise) => BumpBasis.Normalise(v, stockNormalise);

    /// <summary><c>VectorNormalize</c>'s return value: the length it had.</summary>
    /// <param name="v">The vector.</param>
    /// <param name="stockNormalise">True for stock's estimate.</param>
    /// <returns>The unit vector and the returned length.</returns>
    public static (Vec3 Normalised, float Length) NormaliseWithLength(Vec3 v, bool stockNormalise) =>
        stockNormalise ? v.NormaliseLikeStock() : v.Normalise();
}

/// <summary>
/// <c>CDispCollTri</c>: one triangle's vertex
/// indices and plane.
/// </summary>
/// <param name="V0">Vertex 0.</param>
/// <param name="V1">Vertex 1.</param>
/// <param name="V2">Vertex 2.</param>
/// <param name="Normal"><c>m_vecNormal</c>: <c>(v2-v0) x (v1-v0)</c>, normalised.</param>
/// <param name="Dist"><c>m_flDist</c>.</param>
public readonly record struct DispCollTri(ushort V0, ushort V1, ushort V2, Vec3 Normal, float Dist)
{
    /// <summary><c>GetVert</c>.</summary>
    /// <param name="i">0..2.</param>
    /// <returns>The vertex index.</returns>
    public int Vert(int i) => i switch
    {
        0 => V0,
        1 => V1,
        _ => V2,
    };
}
