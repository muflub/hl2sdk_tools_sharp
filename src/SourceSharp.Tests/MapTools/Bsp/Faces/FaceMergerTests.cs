using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Faces;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Faces;

/// <summary>
/// Joining two coplanar faces across a shared edge
/// (<c>TryMergeWinding</c>, <c>src/utils/vbsp/faces.cpp:944</c>).
/// </summary>
public class FaceMergerTests
{
    private static readonly Vec3 Up = new(0f, 0f, 1f);

    /// <summary>
    /// The normal of a plane whose windings run counter-clockwise about +Z.
    /// </summary>
    /// <remarks>
    /// Source windings are CLOCKWISE seen from the side their normal points
    /// to: <c>BaseWindingForPlane</c> (<c>polylib.cpp:302-312</c>) emits
    /// <c>org - vright + vup</c>, <c>org + vright + vup</c>, ... which for a
    /// +Z plane is (1,1), (1,-1), (-1,-1), (-1,1). The fixtures here wind
    /// counter-clockwise about +Z, so their plane faces DOWN. Only a fact whose
    /// answer depends on the sign of <c>TryMergeWinding</c>'s convexity test
    /// (<c>faces.cpp:991</c> and <c>:1003</c>, where <c>CrossProduct(planenormal, delta)</c>
    /// must point OUT of the polygon) can tell the two apart.
    /// </remarks>
    private static readonly Vec3 Down = new(0f, 0f, -1f);

    [Fact]
    public void TwoAdjacentSquaresMergeIntoOneRectangle()
    {
        FaceBuildContext context = Plane();
        FaceMerger merger = new(context);

        Face left = Square(context, 0f, 64f);
        Face right = Square(context, 64f, 128f);

        Face? merged = merger.TryMerge(left, right, Up);

        Assert.NotNull(merged);
        Assert.Equal(4, merged!.Winding.Count);
    }

    [Fact]
    public void AMergeDropsTheTwoCollinearCornersOfTheSharedEdge()
    {
        FaceBuildContext context = Plane();
        FaceMerger merger = new(context);

        Face merged = merger.TryMerge(Square(context, 0f, 64f), Square(context, 64f, 128f), Up)!;

        Vec3[] points = FaceStageFixture.Points(context, merged);

        // The rectangle spans the union, and nothing sits on the seam.
        Assert.All(points, p => Assert.True(p.X is 0f or 128f, $"({p.X},{p.Y}) is on the seam"));
    }

    [Fact]
    public void AMergeMarksBothOriginalsDead()
    {
        FaceBuildContext context = Plane();
        FaceMerger merger = new(context);

        Face left = Square(context, 0f, 64f);
        Face right = Square(context, 64f, 128f);

        Face merged = merger.TryMerge(left, right, Up)!;

        Assert.Same(merged, left.Merged);
        Assert.Same(merged, right.Merged);
        Assert.True(left.IsDead);
        Assert.True(right.IsDead);
    }

    [Fact]
    public void AMergeIncrementsTheMergeCounter()
    {
        FaceBuildContext context = Plane();
        FaceMerger merger = new(context);

        merger.TryMerge(Square(context, 0f, 64f), Square(context, 64f, 128f), Up);

        Assert.Equal(1, context.Counters.Merged);
    }

    [Fact]
    public void TwoSquaresThatDoNotTouchDoNotMerge()
    {
        FaceBuildContext context = Plane();
        FaceMerger merger = new(context);

        Assert.Null(merger.TryMerge(Square(context, 0f, 64f), Square(context, 128f, 192f), Up));
    }

    [Fact]
    public void FacesWithDifferentTexInfoDoNotMerge()
    {
        FaceBuildContext context = Plane();
        FaceMerger merger = new(context);

        Face left = Square(context, 0f, 64f);
        Face right = Square(context, 64f, 128f);
        right.TexInfo = 1;

        Assert.Null(merger.TryMerge(left, right, Up));
    }

    [Fact]
    public void FacesWithDifferentContentsDoNotMerge()
    {
        FaceBuildContext context = Plane();
        FaceMerger merger = new(context);

        Face left = Square(context, 0f, 64f);
        Face right = Square(context, 64f, 128f);
        right.Contents = 2;

        Assert.Null(merger.TryMerge(left, right, Up));
    }

    [Fact]
    public void FacesWithDifferentSmoothingGroupsDoNotMerge()
    {
        FaceBuildContext context = Plane();
        FaceMerger merger = new(context);

        Face left = Square(context, 0f, 64f);
        Face right = Square(context, 64f, 128f);
        right.OriginalFace!.SmoothingGroups = 4;

        Assert.Null(merger.TryMerge(left, right, Up));
    }

    [Fact]
    public void FacesWithDifferentOverlaysDoNotMerge()
    {
        FaceBuildContext context = Plane();
        FaceMerger merger = new(context);

        Face left = Square(context, 0f, 64f);
        Face right = Square(context, 64f, 128f);
        right.OriginalFace!.OverlayIds.Add(7);

        Assert.False(FaceMerger.OverlaysAreEqual(left, right));
        Assert.Null(merger.TryMerge(left, right, Up));
    }

    [Fact]
    public void FacesWithTheSameOverlaysInADifferentOrderDoMerge()
    {
        FaceBuildContext context = Plane();
        FaceMerger merger = new(context);

        Face left = Square(context, 0f, 64f);
        Face right = Square(context, 64f, 128f);
        left.OriginalFace!.OverlayIds.AddRange([3, 9]);
        right.OriginalFace!.OverlayIds.AddRange([9, 3]);

        Assert.True(FaceMerger.OverlaysAreEqual(left, right));
        Assert.NotNull(merger.TryMerge(left, right, Up));
    }

    [Fact]
    public void NoMergeWaterRefusesToMergeAWaterFace()
    {
        FaceBuildContext context = Plane(VbspOptions.Default with { NoMergeWater = true });
        FaceMerger merger = new(context);

        Face left = Square(context, 0f, 64f);
        Face right = Square(context, 64f, 128f);
        left.OriginalFace!.Contents = MapFileLoader.MaskWater;

        Assert.True(FaceMerger.FaceOnWaterBrush(left));
        Assert.Null(merger.TryMerge(left, right, Up));
    }

    [Fact]
    public void WithoutNoMergeWaterAWaterFaceMergesLikeAnyOther()
    {
        FaceBuildContext context = Plane();
        FaceMerger merger = new(context);

        Face left = Square(context, 0f, 64f);
        Face right = Square(context, 64f, 128f);
        left.OriginalFace!.Contents = MapFileLoader.MaskWater;

        Assert.NotNull(merger.TryMerge(left, right, Up));
    }

    [Fact]
    public void AMergeThatWouldMakeAConcavePolygonIsRefused()
    {
        FaceBuildContext context = Plane();
        FaceMerger merger = new(context);

        // A square, and a trapezoid sharing its right edge but flaring OUT at
        // the top so the union has a reflex vertex at the seam's top end.
        Face left = Square(context, 0f, 64f);
        Face right = FaceStageFixture.Face(context,
        [
            new Vec3(64f, 0f, 0f),
            new Vec3(128f, -32f, 0f),
            new Vec3(128f, 96f, 0f),
            new Vec3(64f, 64f, 0f),
        ]);

        // Down, not Up: see Down's remarks. Against Up this same pair has
        // cross(Up, delta) pointing INTO the polygon and the reflex corners
        // read as convex, which is why the fact was red.
        Assert.Null(merger.TryMerge(left, right, Down));
    }

    [Fact]
    public void MergeFaceListJoinsAWholeRowIntoOne()
    {
        FaceBuildContext context = Plane();
        FaceMerger merger = new(context);

        // Three squares in a row. The first merge produces a rectangle that is
        // appended to the tail, and the walk reaches it and merges the third.
        Face a = Square(context, 0f, 64f);
        Face b = Square(context, 64f, 128f);
        Face c = Square(context, 128f, 192f);

        a.Next = b;
        b.Next = c;

        Face head = merger.MergeFaceList(a)!;

        Face? live = null;
        int liveCount = 0;

        for (Face? f = head; f is not null; f = f.Next)
        {
            if (!f.IsDead)
            {
                live = f;
                liveCount++;
            }
        }

        Assert.Equal(1, liveCount);
        Assert.Equal(4, live!.Winding.Count);
        Assert.Equal(2, context.Counters.Merged);
    }

    [Fact]
    public void MergeFaceListLeavesTheHeadWhereItWas()
    {
        FaceBuildContext context = Plane();
        FaceMerger merger = new(context);

        Face a = Square(context, 0f, 64f);
        a.Next = Square(context, 64f, 128f);

        Assert.Same(a, merger.MergeFaceList(a));
    }

    private static FaceBuildContext Plane(VbspOptions? options = null)
    {
        FaceBuildContext context = FaceStageFixture.Create(options);
        context.Planes.Create(Up, 0f);
        return context;
    }

    private static Face Square(FaceBuildContext context, float x0, float x1) =>
        FaceStageFixture.Face(context,
        [
            new Vec3(x0, 0f, 0f),
            new Vec3(x1, 0f, 0f),
            new Vec3(x1, 64f, 0f),
            new Vec3(x0, 64f, 0f),
        ]);
}
