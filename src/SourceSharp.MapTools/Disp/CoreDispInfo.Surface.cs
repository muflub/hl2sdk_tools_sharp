using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Disp;

/// <summary>
/// Point queries on the displaced surface: <c>GetPositionOnSurface</c> and
/// the <c>DispUVToSurf</c> family, <c>builddisp.cpp:2219-2765</c>.
/// </summary>
/// <remarks>
/// vbsp's one caller is detail-prop placement on displacements
/// (<c>detailobjects.cpp:761</c>).
/// </remarks>
public sealed partial class CoreDispInfo
{
    /// <summary>
    /// <c>TRIEDGE_EPSILON</c>, <c>builddisp.cpp:2459</c>: how far past the
    /// diagonal a point must be before the top-left-to-bottom-right split
    /// counts it in the far triangle.
    /// </summary>
    public const float TriEdgeEpsilon = 0.00001f;

    /// <summary>
    /// The displaced position, normal and alpha at a point of the base quad
    /// given in its own (u, v): <c>GetPositionOnSurface</c>,
    /// <c>builddisp.cpp:2219</c>.
    /// </summary>
    /// <param name="u">0..1 across the quad from point 0 towards point 3.</param>
    /// <param name="v">0..1 from point 0 towards point 1.</param>
    /// <param name="position">Overwritten when the query lands.</param>
    /// <param name="normal">Overwritten when the query lands.</param>
    /// <param name="alpha">Overwritten when the query lands.</param>
    /// <returns>
    /// False when nothing was written: outside 0..1, or the rare case in which
    /// neither triangle of the grid square accepts the point (see remarks).
    /// </returns>
    /// <remarks>
    /// <para>
    /// Stock writes through out-pointers and, on both failure paths, simply
    /// does not — so the caller's previous values survive. The <c>ref</c>
    /// parameters reproduce that exactly; the return value only reports it.
    /// </para>
    /// <para>
    /// The normal is the FLAT normal of the grid triangle hit, not an
    /// interpolation of <see cref="Normals"/>.
    /// </para>
    /// </remarks>
    public bool GetPositionOnSurface(
        float u, float v, ref Vec3 position, ref Vec3 normal, ref float alpha) =>
        DispUvToSurf(new DispUv(u, v), ref position, ref normal, ref alpha);

    /// <summary><c>DispUVToSurf</c>, <c>builddisp.cpp:2727</c>.</summary>
    /// <param name="dispUv">The quad-space point.</param>
    /// <param name="position">Overwritten when the query lands.</param>
    /// <param name="normal">Overwritten when the query lands.</param>
    /// <param name="alpha">Overwritten when the query lands.</param>
    /// <returns>Whether anything was written.</returns>
    public bool DispUvToSurf(DispUv dispUv, ref Vec3 position, ref Vec3 normal, ref float alpha)
    {
        if (dispUv.X < 0.0f || dispUv.X > 1.0f || dispUv.Y < 0.0f || dispUv.Y > 1.0f)
        {
            return false;
        }

        ReadOnlySpan<Vec3> p = Surface.Points;
        Vec3 intersect = PointInQuadFromBarycentric(p[0], p[3], p[2], p[1], dispUv);

        int width = PostSpacing;

        // 1.000001f keeps u == 1 inside the last square rather than on a
        // phantom column past it.
        float flU = dispUv.X * (width - 1.000001f);
        float flV = dispUv.Y * (width - 1.000001f);

        int snapU = (int)flU;
        int snapV = (int)flV;

        // The same checkerboard GenerateCollisionSurface uses.
        bool odd = ((snapV * width) + snapU) % 2 == 1;

        int nextU = snapU + 1;
        int nextV = snapV + 1;
        if (nextU == width)
        {
            --nextU;
        }

        if (nextV == width)
        {
            --nextV;
        }

        float fracU = flU - snapU;
        float fracV = flV - snapV;

        Square sq = new(snapU, nextU, snapV, nextV);

        if (odd)
        {
            // DispUVToSurf_TriTLToBR, builddisp.cpp:2456.
            return fracU + fracV >= 1.0f + TriEdgeEpsilon
                ? Tri(TriCase.TlBr1, intersect, sq, ref position, ref normal, ref alpha, false)
                : Tri(TriCase.TlBr2, intersect, sq, ref position, ref normal, ref alpha, false);
        }

        // DispUVToSurf_TriBLToTR, builddisp.cpp:2699.
        return fracU < fracV
            ? Tri(TriCase.BlTr1, intersect, sq, ref position, ref normal, ref alpha, false)
            : Tri(TriCase.BlTr2, intersect, sq, ref position, ref normal, ref alpha, false);
    }

    /// <summary>
    /// <c>PointInQuadFromBarycentric</c>, <c>collisionutils.cpp:2046</c>:
    /// two <c>VectorLerp</c>s along v then one along u.
    /// </summary>
    /// <param name="v1">Quad point one.</param>
    /// <param name="v2">Quad point two.</param>
    /// <param name="v3">Quad point three.</param>
    /// <param name="v4">Quad point four.</param>
    /// <param name="uv">The quad-space point.</param>
    /// <returns>The world point.</returns>
    public static Vec3 PointInQuadFromBarycentric(Vec3 v1, Vec3 v2, Vec3 v3, Vec3 v4, DispUv uv)
    {
        Vec3 a = Lerp(v1, v4, uv.Y);
        Vec3 b = Lerp(v2, v3, uv.Y);
        return Lerp(a, b, uv.X);
    }

    /// <summary>
    /// <c>CalcBarycentricCooefs</c>, <c>builddisp.cpp:134</c>: barycentric
    /// weights from three sub-triangle AREAS.
    /// </summary>
    /// <param name="v0">Triangle point 0.</param>
    /// <param name="v1">Triangle point 1.</param>
    /// <param name="v2">Triangle point 2.</param>
    /// <param name="pt">The point.</param>
    /// <param name="c0">Weight of <paramref name="v0"/>.</param>
    /// <param name="c1">Weight of <paramref name="v1"/>.</param>
    /// <param name="c2">Weight of <paramref name="v2"/>.</param>
    /// <returns>True when the weights sum to 1 within 1e-3.</returns>
    /// <remarks>
    /// Areas are unsigned, so the weights are never negative: a point OUTSIDE
    /// the triangle is detected only because its three areas then sum to more
    /// than the whole. The 1e-3 is a <c>double</c> literal in stock, so the
    /// comparison is made in double (<c>:170</c>). A degenerate triangle has
    /// area zero and every weight zero, which fails.
    /// </remarks>
    public static bool CalcBarycentricCoefs(
        Vec3 v0, Vec3 v1, Vec3 v2, Vec3 pt, out float c0, out float c1, out float c2)
    {
        float totalArea = Vec3.Cross(v1 - v0, v2 - v0).Length() * 0.5f;
        float ooTotalArea = totalArea != 0.0f ? 1.0f / totalArea : 0.0f;

        c0 = Vec3.Cross(v1 - pt, v2 - pt).Length() * 0.5f * ooTotalArea;
        c1 = Vec3.Cross(v2 - pt, v0 - pt).Length() * 0.5f * ooTotalArea;
        c2 = Vec3.Cross(v0 - pt, v1 - pt).Length() * 0.5f * ooTotalArea;

        float total = c0 + c1 + c2;
        return MathF.Abs(1.0f - total) < 1e-3;
    }

    private static Vec3 Lerp(Vec3 a, Vec3 b, float t) =>
        new(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t), a.Z + ((b.Z - a.Z) * t));

    private readonly record struct Square(int SnapU, int NextU, int SnapV, int NextV);

    private enum TriCase
    {
        TlBr1,
        TlBr2,
        BlTr1,
        BlTr2,
    }

    /// <summary>
    /// The four <c>DispUVToSurf_Tri*_1/_2</c> functions
    /// (<c>builddisp.cpp:2244</c>, <c>:2350</c>, <c>:2487</c>, <c>:2593</c>),
    /// which are one body with different vertex choices, folded together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per case: which three grid vertices; which of them the fraction runs
    /// towards on a degenerate (last-column or last-row) square; and the two
    /// edges and cross order of the normal.
    /// </para>
    /// <para>
    /// A quirk kept as found: <c>TriBLToTR_1</c>'s degenerate-in-u branch
    /// crosses <c>(edgeU, edgeV)</c> (<c>:2528</c>) where its other two
    /// branches cross <c>(edgeV, edgeU)</c> — so on the displacement's last
    /// column that triangle's normal points the other way.
    /// </para>
    /// <para>
    /// When the barycentric test fails, the partner triangle is tried once
    /// (<c>bBackup</c>); when that fails too, nothing is written.
    /// </para>
    /// </remarks>
    private bool Tri(
        TriCase which,
        Vec3 intersect,
        Square sq,
        ref Vec3 position,
        ref Vec3 normal,
        ref float alpha,
        bool backup)
    {
        int w = PostSpacing;
        int i0, i1, i2;
        int fracU, fracV;

        switch (which)
        {
            case TriCase.TlBr1:
                i0 = (sq.NextV * w) + sq.SnapU;
                i1 = (sq.NextV * w) + sq.NextU;
                i2 = (sq.SnapV * w) + sq.NextU;
                fracU = 2;
                fracV = 2;
                break;
            case TriCase.TlBr2:
                i0 = (sq.SnapV * w) + sq.SnapU;
                i1 = (sq.NextV * w) + sq.SnapU;
                i2 = (sq.SnapV * w) + sq.NextU;
                fracU = 1;
                fracV = 2;
                break;
            case TriCase.BlTr1:
                i0 = (sq.SnapV * w) + sq.SnapU;
                i1 = (sq.NextV * w) + sq.SnapU;
                i2 = (sq.NextV * w) + sq.NextU;
                fracU = 2;
                fracV = 2;
                break;
            default:
                i0 = (sq.SnapV * w) + sq.SnapU;
                i1 = (sq.NextV * w) + sq.NextU;
                i2 = (sq.SnapV * w) + sq.NextU;
                fracU = 1;
                fracV = 2;
                break;
        }

        Span<Vec3> flat = [_flatVerts[i0], _flatVerts[i1], _flatVerts[i2]];
        Span<Vec3> verts = [_verts[i0], _verts[i1], _verts[i2]];
        Span<float> alphas = [_alphas[i0], _alphas[i1], _alphas[i2]];

        if (sq.SnapU == sq.NextU || sq.SnapV == sq.NextV)
        {
            bool degenerateU = sq.SnapU == sq.NextU;

            if (degenerateU && sq.SnapV == sq.NextV)
            {
                position = verts[0];
                alpha = alphas[0];
            }
            else
            {
                int to = degenerateU ? fracU : fracV;
                float frac = (intersect - flat[0]).Length() / (flat[to] - flat[0]).Length();
                position = verts[0] + (frac * (verts[to] - verts[0]));
                alpha = alphas[0] + (frac * (alphas[to] - alphas[0]));
            }

            normal = TriNormal(which, verts, flipBlTr1: degenerateU);
            return true;
        }

        if (CalcBarycentricCoefs(flat[0], flat[1], flat[2], intersect,
                out float c0, out float c1, out float c2))
        {
            position = (verts[0] * c0) + (verts[1] * c1) + (verts[2] * c2);
            alpha = (alphas[0] * c0) + (alphas[1] * c1) + (alphas[2] * c2);
            normal = TriNormal(which, verts, flipBlTr1: false);
            return true;
        }

        if (backup)
        {
            return false;
        }

        TriCase partner = which switch
        {
            TriCase.TlBr1 => TriCase.TlBr2,
            TriCase.TlBr2 => TriCase.TlBr1,
            TriCase.BlTr1 => TriCase.BlTr2,
            _ => TriCase.BlTr1,
        };

        return Tri(partner, intersect, sq, ref position, ref normal, ref alpha, backup: true);
    }

    private Vec3 TriNormal(TriCase which, ReadOnlySpan<Vec3> verts, bool flipBlTr1)
    {
        Vec3 edgeU, edgeV;
        Vec3 n;

        switch (which)
        {
            case TriCase.TlBr1:
                edgeU = verts[0] - verts[1];
                edgeV = verts[2] - verts[1];
                n = Vec3.Cross(edgeU, edgeV);
                break;
            case TriCase.TlBr2:
                edgeU = verts[2] - verts[0];
                edgeV = verts[1] - verts[0];
                n = Vec3.Cross(edgeU, edgeV);
                break;
            case TriCase.BlTr1:
                edgeU = verts[2] - verts[1];
                edgeV = verts[0] - verts[1];
                n = flipBlTr1 ? Vec3.Cross(edgeU, edgeV) : Vec3.Cross(edgeV, edgeU);
                break;
            default:
                edgeU = verts[0] - verts[2];
                edgeV = verts[1] - verts[2];
                n = Vec3.Cross(edgeV, edgeU);
                break;
        }

        return Normalise(n);
    }
}
