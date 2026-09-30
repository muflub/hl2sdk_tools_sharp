//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The integer union of a room's walkable faces (the rooms design, 18.2):
/// touching faces merge, overlapping ones too, holes come out as holes,
/// bands group, points on a line and slivers go, regions touching at a point
/// stay apart, crossings meet at their rounded point, and the result is the
/// same whatever order the faces come in.
/// </summary>
public sealed class MapPolygonUnionTests
{
    private static MapFacePolygon Rect(int x0, int y0, int x1, int y1, int z = 0, int zHigh = int.MinValue) =>
        new([new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)], z, zHigh == int.MinValue ? z : zHigh);

    private static MapFacePolygon Poly(int z, params (int X, int Y)[] points) => new([.. points.Select(p => new MapPoint(p.X, p.Y))], z, z);

    private static List<(int, int)> Ring(IReadOnlyList<MapPoint> ring) => [.. ring.Select(p => (p.X, p.Y))];

    /// <summary>Two squares sharing an edge are one rectangle: the shared edge and its end points on the straight sides go.</summary>
    [Fact]
    public void TouchingFacesMergeIntoOneRing()
    {
        MapPolygon only = Assert.Single(MapPolygonUnion.Union([Rect(0, 0, 10, 10), Rect(10, 0, 20, 10)]));
        Assert.Equal([(0, 0), (20, 0), (20, 10), (0, 10)], Ring(only.Outer));
        Assert.Empty(only.Holes);
    }

    /// <summary>A face meeting another's edge in its middle (a T-junction) merges as cleanly.</summary>
    [Fact]
    public void AFaceMeetingAnEdgeMidwayMerges()
    {
        MapPolygon only = Assert.Single(MapPolygonUnion.Union([Rect(0, 0, 20, 10), Rect(5, 10, 15, 20)]));
        Assert.Equal([(0, 0), (20, 0), (20, 10), (15, 10), (15, 20), (5, 20), (5, 10), (0, 10)], Ring(only.Outer));
    }

    /// <summary>Overlapping faces are unioned, not counted twice: the outline is the union's.</summary>
    [Fact]
    public void OverlappingFacesUnion()
    {
        MapPolygon only = Assert.Single(MapPolygonUnion.Union([Rect(0, 0, 10, 10), Rect(5, 5, 15, 15)]));
        Assert.Equal([(0, 0), (10, 0), (10, 5), (15, 5), (15, 15), (5, 15), (5, 10), (0, 10)], Ring(only.Outer));
        Assert.Equal(350, MapPolygonUnion.Area2(only.Outer));
    }

    /// <summary>Four faces around a gap make a ring with a hole, the hole clockwise and after its outer ring.</summary>
    [Fact]
    public void FacesAroundAGapMakeAHole()
    {
        MapPolygon only = Assert.Single(MapPolygonUnion.Union(
            [Rect(0, 0, 30, 10), Rect(0, 20, 30, 30), Rect(0, 10, 10, 20), Rect(20, 10, 30, 20)]));
        Assert.Equal([(0, 0), (30, 0), (30, 30), (0, 30)], Ring(only.Outer));
        IReadOnlyList<MapPoint> hole = Assert.Single(only.Holes);
        Assert.Equal([(10, 10), (10, 20), (20, 20), (20, 10)], Ring(hole));
        Assert.True(MapPolygonUnion.Area2(hole) < 0);
    }

    /// <summary>An island in a hole is its own polygon, and its hole goes to it, not to the ring around both.</summary>
    [Fact]
    public void AnIslandInAHoleIsItsOwnPolygon()
    {
        List<MapFacePolygon> faces =
        [
            Rect(0, 0, 50, 10), Rect(0, 40, 50, 50), Rect(0, 10, 10, 40), Rect(40, 10, 50, 40),
            Rect(15, 15, 35, 20), Rect(15, 30, 35, 35), Rect(15, 20, 20, 30), Rect(30, 20, 35, 30),
        ];
        IReadOnlyList<MapPolygon> polygons = MapPolygonUnion.Union(faces);
        Assert.Equal(2, polygons.Count);
        Assert.Equal([(0, 0), (50, 0), (50, 50), (0, 50)], Ring(polygons[0].Outer));
        Assert.Equal([(10, 10), (10, 40), (40, 40), (40, 10)], Ring(Assert.Single(polygons[0].Holes)));
        Assert.Equal([(15, 15), (35, 15), (35, 35), (15, 35)], Ring(polygons[1].Outer));
        Assert.Equal([(20, 20), (20, 30), (30, 30), (30, 20)], Ring(Assert.Single(polygons[1].Holes)));
    }

    /// <summary>Two squares touching at a corner stay two simple rings: the leftmost turn at the shared point keeps them apart.</summary>
    [Fact]
    public void RegionsTouchingAtAPointStayTwoRings()
    {
        IReadOnlyList<MapPolygon> polygons = MapPolygonUnion.Union([Rect(0, 0, 10, 10), Rect(10, 10, 20, 20)]);
        Assert.Equal(2, polygons.Count);
        Assert.Equal([(0, 0), (10, 0), (10, 10), (0, 10)], Ring(polygons[0].Outer));
        Assert.Equal([(10, 10), (20, 10), (20, 20), (10, 20)], Ring(polygons[1].Outer));
    }

    /// <summary>
    /// Bands group faces: overlapping bands and touching areas are one group
    /// with the widest band (a ramp joins the floor it starts on), disjoint
    /// bands stay apart even where the areas overlap (a gallery over a hall),
    /// and bands meeting at one z overlap (closed intervals).
    /// </summary>
    [Fact]
    public void BandsGroupTheFaces()
    {
        IReadOnlyList<MapPolygon> polygons = MapPolygonUnion.Union(
        [
            Rect(0, 0, 10, 10, 16),
            Rect(10, 0, 20, 10, 16, 64),
            Rect(0, 0, 20, 10, 128),
            Rect(20, 0, 30, 10, 64),
        ]);
        Assert.Equal(2, polygons.Count);
        Assert.Equal((16, 64), (polygons[0].ZLow, polygons[0].ZHigh));
        Assert.Equal([(0, 0), (30, 0), (30, 10), (0, 10)], Ring(polygons[0].Outer));
        Assert.Equal((128, 128), (polygons[1].ZLow, polygons[1].ZHigh));
        Assert.Equal([(0, 0), (20, 0), (20, 10), (0, 10)], Ring(polygons[1].Outer));
    }

    /// <summary>Faces of one band that do not touch are two polygons of that band.</summary>
    [Fact]
    public void FacesApartAreTwoPolygons()
    {
        IReadOnlyList<MapPolygon> polygons = MapPolygonUnion.Union([Rect(20, 0, 30, 10), Rect(0, 0, 10, 10)]);
        Assert.Equal([(0, 0), (20, 0)], polygons.Select(p => (p.Outer[0].X, p.Outer[0].Y)));
    }

    /// <summary>
    /// Crossing edges meet at a rational point, rounded to the nearest whole
    /// unit (halves up) only once the rings are chained: two thin diagonal
    /// bars crossing make one ring through the rounded crossing points.
    /// </summary>
    [Fact]
    public void CrossingEdgesMeetAtTheirRoundedPoint()
    {
        MapPolygon only = Assert.Single(MapPolygonUnion.Union([Poly(0, (0, 0), (10, 5), (10, 7), (0, 2)), Poly(0, (0, 7), (10, 0), (10, 3), (0, 10))]));

        // The bars' outlines cross four times, at (5 5/6, 2 11/12),
        // (8 1/3, 4 1/6), (6 2/3, 5 1/3) and (4 1/6, 4 1/12); the outline of
        // the union turns at each, rounded, and at the bars' own corners.
        Assert.Equal(
            [(0, 0), (6, 3), (10, 0), (10, 3), (8, 4), (10, 5), (10, 7), (7, 5), (0, 10), (0, 7), (4, 4), (0, 2)],
            Ring(only.Outer));
    }

    /// <summary>A face snapped to nothing (fewer than three points, no area, turned over) is dropped; repeated points are harmless.</summary>
    [Fact]
    public void FacesSnappedToNothingAreDropped()
    {
        Assert.Empty(MapPolygonUnion.Union([Poly(0, (0, 0), (5, 0)), Poly(0, (0, 0), (5, 0), (10, 0)), Poly(0, (0, 0), (0, 5), (5, 0))]));
        MapPolygon only = Assert.Single(MapPolygonUnion.Union([Poly(0, (0, 0), (0, 0), (10, 0), (10, 10), (0, 10), (0, 0))]));
        Assert.Equal([(0, 0), (10, 0), (10, 10), (0, 10)], Ring(only.Outer));
    }

    /// <summary>
    /// A face the snap made a little concave still counts once inside: its
    /// fan's turned-over triangle cancels, so the union is the polygon itself.
    /// </summary>
    [Fact]
    public void AConcaveFaceCountsOnceInside()
    {
        MapPolygon only = Assert.Single(MapPolygonUnion.Union([Poly(0, (0, 0), (10, 0), (10, 10), (5, 4), (0, 10))]));
        Assert.Equal([(0, 0), (10, 0), (10, 10), (5, 4), (0, 10)], Ring(only.Outer));
    }

    /// <summary>The union is a function of the set of faces: every order of the same faces gives the same polygons.</summary>
    [Fact]
    public void TheOrderOfTheFacesDoesNotMatter()
    {
        List<MapFacePolygon> faces =
        [
            Rect(0, 0, 30, 10), Rect(0, 20, 30, 30), Rect(0, 10, 10, 20), Rect(20, 10, 30, 20),
            Rect(12, 12, 18, 18, 32), Poly(0, (30, 0), (40, 5), (30, 10)), Rect(40, 40, 50, 50),
        ];
        string Describe(IReadOnlyList<MapPolygon> polygons) => string.Join(
            " | ", polygons.Select(p => $"{p.ZLow}-{p.ZHigh}: {string.Join(" ", p.Outer)} / {string.Join(" / ", p.Holes.Select(h => string.Join(" ", h)))}"));

        string expected = Describe(MapPolygonUnion.Union(faces));
        Random random = new(7);
        for (int i = 0; i < 20; i++)
        {
            List<MapFacePolygon> shuffled = [.. faces.OrderBy(_ => random.Next())];

            // Each face's points rotated too: where a face's ring starts is not an input either.
            shuffled = [.. shuffled.Select(f => f with { Points = [.. f.Points.Skip(i % f.Points.Count), .. f.Points.Take(i % f.Points.Count)] })];
            Assert.Equal(expected, Describe(MapPolygonUnion.Union(shuffled)));
        }
    }

    /// <summary>A point outside the coordinates the arithmetic is sized for is refused, naming it.</summary>
    [Fact]
    public void APointOutsideTheBoundIsRefused()
    {
        Assert.Equal(
            "the point (70000, 0) is outside the ±65536 the map's union handles. (Parameter 'face')",
            Assert.Throws<ArgumentOutOfRangeException>(() => MapPolygonUnion.Union([Poly(0, (0, 0), (70000, 0), (0, 10))])).Message);
        Assert.Single(MapPolygonUnion.Union([Poly(0, (-65536, -65536), (65536, -65536), (65536, 65536))]));
    }
}
