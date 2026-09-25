using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Disp;

using Xunit;

namespace SourceSharp.Tests.MapTools.Disp;

/// <summary>
/// Point queries on the displaced surface: <c>GetPositionOnSurface</c> and
/// <c>DispUVToSurf</c>, plus the two helpers
/// they rest on.
/// </summary>
public sealed class CoreDispInfoSurfaceTests
{
    private static readonly Vec3[] Floor = DispFixtures.UnitFloor();

    /// <summary>
    /// A (u, v) outside 0..1 writes nothing: <c>DispUVToSurf</c>'s early
    /// return.
    /// </summary>
    [Fact]
    public void AQueryOutsideTheQuadWritesNothing()
    {
        CoreDispInfo core = DispFixtures.Core(DispFixtures.Heightfield(2, Floor[0]), Floor);
        Vec3 position = new(1, 2, 3);
        Vec3 normal = new(4, 5, 6);
        float alpha = 7;

        bool landed = core.GetPositionOnSurface(1.5f, 0.5f, ref position, ref normal, ref alpha);

        Assert.False(landed);
        Assert.Equal(new Vec3(1, 2, 3), position);
        Assert.Equal(new Vec3(4, 5, 6), normal);
        Assert.Equal(7, alpha);
    }

    /// <summary>
    /// A query exactly on a grid vertex returns that displaced vertex: its
    /// barycentric weights are (1, 0, 0) exactly
    ///(<c>CalcBarycentricCooefs</c>).
    /// </summary>
    [Fact]
    public void AQueryOnAGridVertexReturnsThatVertex()
    {
        CoreDispInfo core = DispFixtures.Core(
            DispFixtures.Heightfield(2, Floor[0], (x, y) => (x * 7) + (y * 3)), Floor);
        Vec3 position = default, normal = default;
        float alpha = 0;

        core.GetPositionOnSurface(0.5f, 0.5f, ref position, ref normal, ref alpha);

        Assert.Equal(core.Vert(DispFixtures.Index(core, 2, 2)), position);
    }

    /// <summary>
    /// u runs from point 0 towards point 3 and v towards point 1:
    /// <c>PointInQuadFromBarycentric(p0, p3, p2, p1)</c>.
    /// </summary>
    [Fact]
    public void UFollowsXAndVFollowsY()
    {
        CoreDispInfo core = DispFixtures.Core(DispFixtures.Heightfield(2, Floor[0]), Floor);
        Vec3 position = default, normal = default;
        float alpha = 0;

        core.GetPositionOnSurface(0.25f, 0.75f, ref position, ref normal, ref alpha);

        Assert.Equal(64, position.X, 1e-3f);
        Assert.Equal(192, position.Y, 1e-3f);
    }

    /// <summary>
    /// Inside a square on a planar slope the height is the plane's: barycentric
    /// interpolation over the flat triangle.
    /// </summary>
    [Fact]
    public void InsideASquareTheHeightIsInterpolated()
    {
        CoreDispInfo core = DispFixtures.Core(
            DispFixtures.Heightfield(2, Floor[0], (x, _) => x * 16), Floor);
        Vec3 position = default, normal = default;
        float alpha = 0;

        core.GetPositionOnSurface(0.3f, 0.61f, ref position, ref normal, ref alpha);

        Assert.Equal(position.X / 4, position.Z, 1e-3f);
    }

    /// <summary>The alpha is interpolated with the same weights.</summary>
    [Fact]
    public void TheAlphaIsInterpolatedWithTheSameWeights()
    {
        CoreDispInfo core = DispFixtures.Core(
            DispFixtures.Heightfield(2, Floor[0], alpha: (x, _) => x * 64), Floor);
        Vec3 position = default, normal = default;
        float alpha = 0;

        core.GetPositionOnSurface(0.3f, 0.61f, ref position, ref normal, ref alpha);

        Assert.Equal(position.X, alpha, 1e-2f);
    }

    /// <summary>
    /// The normal is the hit triangle's FLAT normal, facing up out of the
    /// displacement: e.g. <c>DispUVToSurf_TriBLToTR_2</c>.
    /// </summary>
    [Fact]
    public void TheNormalIsTheHitTrianglesFaceNormal()
    {
        CoreDispInfo core = DispFixtures.Core(
            DispFixtures.Heightfield(2, Floor[0], (x, _) => x * 16), Floor);
        Vec3 position = default, normal = default;
        float alpha = 0;
        Vec3 expected = new Vec3(-0.25f, 0, 1).Normalise().Normalised;

        core.GetPositionOnSurface(0.55f, 0.3f, ref position, ref normal, ref alpha);

        Assert.Equal(expected.X, normal.X, 1e-6f);
        Assert.Equal(expected.Y, normal.Y, 1e-6f);
        Assert.Equal(expected.Z, normal.Z, 1e-6f);
    }

    /// <summary>
    /// u = v = 1 lands in the last square rather than past it — the
    /// <c>1.000001f</c> — and returns the far
    /// corner.
    /// </summary>
    [Fact]
    public void TheFarCornerIsReachable()
    {
        CoreDispInfo core = DispFixtures.Core(
            DispFixtures.Heightfield(3, Floor[0], (_, _) => 40), Floor);
        Vec3 position = default, normal = default;
        float alpha = 0;

        bool landed = core.GetPositionOnSurface(1, 1, ref position, ref normal, ref alpha);

        Assert.True(landed);
        Assert.Equal(256, position.X, 1e-2f);
        Assert.Equal(256, position.Y, 1e-2f);
        Assert.Equal(40, position.Z, 1e-3f);
    }

    /// <summary>
    /// The four quad corners map to (0,0), (1,0), (1,1), (0,1) in the order
    /// v1, v2, v3, v4: <c>PointInQuadFromBarycentric</c>.
    /// </summary>
    [Fact]
    public void PointInQuadFromBarycentricHitsTheCorners()
    {
        Vec3 v1 = new(0, 0, 0), v2 = new(10, 0, 0), v3 = new(10, 20, 0), v4 = new(0, 20, 0);

        Assert.Equal(v1, CoreDispInfo.PointInQuadFromBarycentric(v1, v2, v3, v4, new DispUv(0, 0)));
        Assert.Equal(v2, CoreDispInfo.PointInQuadFromBarycentric(v1, v2, v3, v4, new DispUv(1, 0)));
        Assert.Equal(v3, CoreDispInfo.PointInQuadFromBarycentric(v1, v2, v3, v4, new DispUv(1, 1)));
        Assert.Equal(v4, CoreDispInfo.PointInQuadFromBarycentric(v1, v2, v3, v4, new DispUv(0, 1)));
    }

    /// <summary>A point inside the triangle gives weights summing to one.</summary>
    [Fact]
    public void BarycentricCoefsOfAnInteriorPointSumToOne()
    {
        bool inside = CoreDispInfo.CalcBarycentricCoefs(
            new Vec3(0, 0, 0), new Vec3(4, 0, 0), new Vec3(0, 4, 0), new Vec3(1, 1, 0),
            out float c0, out float c1, out float c2);

        Assert.True(inside);
        Assert.Equal(0.5f, c0, 1e-6f);
        Assert.Equal(0.25f, c1, 1e-6f);
        Assert.Equal(0.25f, c2, 1e-6f);
    }

    /// <summary>
    /// The areas are unsigned, so an outside point is caught only by the sum
    /// exceeding one:.
    /// </summary>
    [Fact]
    public void BarycentricCoefsOfAnOutsidePointFail()
    {
        bool inside = CoreDispInfo.CalcBarycentricCoefs(
            new Vec3(0, 0, 0), new Vec3(4, 0, 0), new Vec3(0, 4, 0), new Vec3(5, 5, 0),
            out float c0, out _, out _);

        Assert.False(inside);
        Assert.True(c0 > 0);
    }

    /// <summary>
    /// A degenerate triangle has zero area, every weight zero, and fails:
    /// <c>ooTotalArea = totalArea ? 1/totalArea: 0</c>.
    /// </summary>
    [Fact]
    public void BarycentricCoefsOfADegenerateTriangleFail()
    {
        bool inside = CoreDispInfo.CalcBarycentricCoefs(
            new Vec3(0, 0, 0), new Vec3(4, 0, 0), new Vec3(8, 0, 0), new Vec3(1, 0, 0),
            out float c0, out float c1, out float c2);

        Assert.False(inside);
        Assert.Equal(0, c0 + c1 + c2);
    }
}
