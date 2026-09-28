//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Nav;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>The brush's corners from its planes, and the exact overlap test on every family of axis.</summary>
public sealed class NavBrushTests
{
    private static NavBrush Wedge()
    {
        // A triangular prism along y: z ≤ x, x ≤ 10, z ≥ 0, 0 ≤ y ≤ 10.
        float r = MathF.Sqrt(0.5f);
        return NavBrush.FromPlanes([
            (new Vec3(-r, 0, r), 0f), (new Vec3(1, 0, 0), 10f), (new Vec3(0, 0, -1), 0f),
            (new Vec3(0, 1, 0), 10f), (new Vec3(0, -1, 0), 0f)], 1)!;
    }

    [Fact]
    public void ABrushsCornersComeFromItsPlanes()
    {
        NavBrush wedge = Wedge();
        Assert.Equal(6, wedge.VertexCount);
        Assert.Equal(5, wedge.PlaneCount);
        Assert.Equal((0.0, 0.0, 0.0, 10.0, 10.0, 10.0), (wedge.MinX, wedge.MinY, wedge.MinZ, wedge.MaxX, wedge.MaxY, wedge.MaxZ), new ToleranceComparer());
        Assert.Equal(1, wedge.Contents);
    }

    [Fact]
    public void PlanesThatBoundNothingMakeNoBrush()
    {
        Assert.Null(NavBrush.FromPlanes([(new Vec3(1, 0, 0), 0f), (new Vec3(-1, 0, 0), -5f)], 1));
        Assert.Null(NavBrush.FromPlanes([(new Vec3(0, 0, 1), 0f)], 1));
        Assert.Throws<ArgumentException>(() => NavBrush.Box(new Vec3(0, 0, 0), new Vec3(0, 1, 1), 1));
    }

    [Theory]
    [InlineData(6, 1, 0, 8, 9, 2, true)]     // inside
    [InlineData(10, 1, 0, 12, 9, 2, false)]  // touching the back face
    [InlineData(1, 1, 3, 3, 9, 5, false)]    // above the slope, clear of it by its plane
    [InlineData(4, 1, 3, 6, 9, 5, true)]     // straddling the slope
    [InlineData(2, 11, 0, 4, 12, 1, false)]  // beside it along y
    public void TheOverlapTestIsExact(double x0, double y0, double z0, double x1, double y1, double z1, bool overlaps) =>
        Assert.Equal(overlaps, Wedge().Overlaps(new NavBox(x0, y0, z0, x1, y1, z1)));

    [Fact]
    public void ABoxSeparatedOnlyAcrossAnEdgeDoesNotOverlap()
    {
        // The tetrahedron x, y, z ≥ 0, x + y + z ≤ 10, and a box beyond its
        // edge from (10, 0, 0) to (0, 10, 0): x + y ≥ 11 over the box, so they
        // are apart, but no face plane and no box axis says so; only the
        // edge's direction crossed with z does.
        float third = 1f / MathF.Sqrt(3f);
        NavBrush tetra = NavBrush.FromPlanes([
            (new Vec3(-1, 0, 0), 0f), (new Vec3(0, -1, 0), 0f), (new Vec3(0, 0, -1), 0f),
            (new Vec3(third, third, third), 10f * third)], 1)!;
        NavBox box = new(5.5, 5.5, -5, 7, 7, 5);
        Assert.False(tetra.Overlaps(box));
        Assert.True(tetra.Overlaps(box with { MinX = 4, MinY = 4 }));
    }

    [Fact]
    public void ABoxIsAxialAndItsBoundsDecideAlone()
    {
        NavBrush box = NavBrush.Box(new Vec3(0, 0, 0), new Vec3(10, 10, 10), 1);
        Assert.True(box.IsAxial);
        Assert.False(Wedge().IsAxial);
        Assert.True(box.Overlaps(new NavBox(9, 9, 9, 11, 11, 11)));
        Assert.False(box.Overlaps(new NavBox(10, 0, 0, 11, 1, 1)));
    }

    [Fact]
    public void ABrushTurnsExactly()
    {
        NavBrush box = NavBrush.Box(new Vec3(10, 20, 0), new Vec3(30, 40, 5), 1).Turned(1, 100);
        Assert.Equal((60.0, 10.0, 80.0, 30.0), (box.MinX, box.MinY, box.MaxX, box.MaxY));
        Assert.Equal((0.0, 1.0, 0.0, 30.0), box.Plane(0));
        Assert.Equal((-1.0, 0.0, 0.0, -60.0), box.Plane(2));
        NavBrush back = box.Turned(3, 100);
        Assert.Equal((10.0, 20.0, 30.0, 40.0), (back.MinX, back.MinY, back.MaxX, back.MaxY));
    }

    private sealed class ToleranceComparer : IEqualityComparer<(double, double, double, double, double, double)>
    {
        public bool Equals((double, double, double, double, double, double) a, (double, double, double, double, double, double) b) =>
            Math.Abs(a.Item1 - b.Item1) < 1e-4 && Math.Abs(a.Item2 - b.Item2) < 1e-4 && Math.Abs(a.Item3 - b.Item3) < 1e-4
            && Math.Abs(a.Item4 - b.Item4) < 1e-4 && Math.Abs(a.Item5 - b.Item5) < 1e-4 && Math.Abs(a.Item6 - b.Item6) < 1e-4;

        public int GetHashCode((double, double, double, double, double, double) obj) => 0;
    }
}
