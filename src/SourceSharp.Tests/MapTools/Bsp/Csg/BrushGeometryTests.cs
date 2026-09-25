using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Bsp.Tree;
using SourceSharp.MapTools.Geometry;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Csg;

/// <summary>
/// The geometric primitives of <c>brushbsp.cpp</c>: bounds, volume, windings
/// and the splitter.
/// </summary>
public class BrushGeometryTests
{
    [Fact]
    public async Task BrushFromBoundsMakesSixAxialSidesInMaxThenMinOrder()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        BspBrush box = CsgFixture.Box(build, new Vec3(-64, -64, -64), new Vec3(64, 64, 64));

        Assert.Equal(6, box.SideCount);
        Assert.Equal(new Vec3(1, 0, 0), build.Planes[box.Sides[0].PlaneNumber].Normal);
        Assert.Equal(new Vec3(0, 1, 0), build.Planes[box.Sides[1].PlaneNumber].Normal);
        Assert.Equal(new Vec3(0, 0, 1), build.Planes[box.Sides[2].PlaneNumber].Normal);
        Assert.Equal(new Vec3(-1, 0, 0), build.Planes[box.Sides[3].PlaneNumber].Normal);
        Assert.Equal(new Vec3(0, -1, 0), build.Planes[box.Sides[4].PlaneNumber].Normal);
        Assert.Equal(new Vec3(0, 0, -1), build.Planes[box.Sides[5].PlaneNumber].Normal);
    }

    [Fact]
    public async Task BrushFromBoundsHasNoOriginalMapBrush()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        BspBrush box = CsgFixture.Box(build, new Vec3(-64, -64, -64), new Vec3(64, 64, 64));

        Assert.Null(box.Original);
    }

    [Fact]
    public async Task CreateBrushWindingsBoundsTheBrushItBuilt()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        BspBrush box = CsgFixture.Box(build, new Vec3(-64, -32, -16), new Vec3(64, 32, 16));

        Assert.Equal(new Vec3(-64, -32, -16), box.Mins);
        Assert.Equal(new Vec3(64, 32, 16), box.Maxs);
    }

    [Fact]
    public async Task BrushVolumeOfABoxIsItsProduct()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        BspBrush box = CsgFixture.Box(build, new Vec3(-64, -32, -16), new Vec3(64, 32, 16));

        Assert.Equal(128f * 64f * 32f, BrushGeometry.BrushVolume(build, box), 0.5f);
    }

    /// <summary>
    /// Stock returns zero for a null brush rather than crashing, because
    /// <c>SplitBrush</c> hands it halves that may not exist
    /// (<c>brushbsp.cpp:241</c>).
    /// </summary>
    [Fact]
    public async Task BrushVolumeOfNothingIsZero()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        Assert.Equal(0f, BrushGeometry.BrushVolume(build, null));
    }

    [Fact]
    public async Task BoundBrushOfAWindinglessBrushComesBackInsideOut()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        BspBrush empty = build.AllocBrush(1);
        empty.AddSide(new BspBrushSide { PlaneNumber = 0 });

        BrushGeometry.BoundBrush(build, empty);

        Assert.Equal(new Vec3(99999f, 99999f, 99999f), empty.Mins);
        Assert.Equal(new Vec3(-99999f, -99999f, -99999f), empty.Maxs);
    }

    [Fact]
    public async Task PointInsideBrushFindsAPointInsideAnOffCentreBox()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        BspBrush box = CsgFixture.Box(build, new Vec3(512, 512, 512), new Vec3(640, 640, 640));

        Vec3 inside = BrushGeometry.PointInsideBrush(build, box);

        for (int i = 0; i < 3; i++)
        {
            Assert.InRange(inside[i], 512f, 640f);
        }
    }

    /// <summary>
    /// Four relaxation passes are not a solver: a box far enough from the
    /// origin comes back with a point that is still outside it. The port keeps
    /// that, because <c>CreateBrushWindings</c> only wants a translation.
    /// </summary>
    [Fact]
    public async Task PointInsideBrushCanReturnAPointOutsideTheBrush()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        BspBrush wedge = build.AllocBrush(4);
        foreach (int plane in new[]
        {
            build.Planes.Find(new Vec3(1, 0, 0), 16000f),
            build.Planes.Find(new Vec3(-1, 0, 0), -15990f),
            build.Planes.Find(new Vec3(0, 1, 0), 16000f),
            build.Planes.Find(new Vec3(0, -1, 0), -15990f),
        })
        {
            wedge.AddSide(new BspBrushSide { PlaneNumber = plane });
        }

        Vec3 inside = BrushGeometry.PointInsideBrush(build, wedge);

        // Z is unconstrained, so a correct answer is impossible; the point that
        // comes back is simply where four passes got to.
        Assert.Equal(0f, inside.Z);
    }

    [Fact]
    public async Task WindingIsTinyWantsThreeEdgesLongerThanAFifthOfAUnit()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        Winding big = build.Windings.Create(
        [
            new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(1, 1, 0), new Vec3(0, 1, 0),
        ]);

        Winding sliver = build.Windings.Create(
        [
            new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(1, 0.1f, 0), new Vec3(0, 0.1f, 0),
        ]);

        Assert.False(BrushGeometry.WindingIsTiny(build, big));
        Assert.True(BrushGeometry.WindingIsTiny(build, sliver));
    }

    /// <summary>
    /// The edge test is strict, so an edge of exactly 0.2 does not count and
    /// a square of that size is "tiny".
    /// </summary>
    [Fact]
    public async Task WindingIsTinyCountsAnEdgeOfExactlyTheThresholdAsTooShort()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        Winding exact = build.Windings.Create(
        [
            new Vec3(0, 0, 0), new Vec3(0.2f, 0, 0),
            new Vec3(0.2f, 0.2f, 0), new Vec3(0, 0.2f, 0),
        ]);

        Assert.True(BrushGeometry.WindingIsTiny(build, exact));
    }

    [Fact]
    public async Task WindingIsHugeFiresOnABaseWindingAndNotOnALegalFace()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        Winding basewinding = build.Windings.BaseWindingForPlane(new Vec3(0, 0, 1), 0f);
        Winding legal = build.Windings.Create(
        [
            new Vec3(-16384, -16384, 0), new Vec3(16384, -16384, 0), new Vec3(16384, 16384, 0),
        ]);

        Assert.True(BrushGeometry.WindingIsHuge(build, basewinding));
        Assert.False(BrushGeometry.WindingIsHuge(build, legal));
    }

    /// <summary>
    /// Both comparisons are strict against a maximum that starts at zero, so a
    /// brush exactly on the plane is FRONT.
    /// </summary>
    [Fact]
    public async Task BrushMostlyOnSideCallsAFlatBrushFront()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        BspBrush box = CsgFixture.Box(build, new Vec3(-64, -64, 0), new Vec3(64, 64, 0));
        Plane zero = new(new Vec3(0, 0, 1), 0f);

        Assert.Equal(PlaneSideFlags.Front, BrushGeometry.BrushMostlyOnSide(build, box, zero));
    }

    [Fact]
    public async Task BrushMostlyOnSideFollowsTheFurthestVertex()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        BspBrush box = CsgFixture.Box(build, new Vec3(-64, -64, -100), new Vec3(64, 64, 10));
        Plane zero = new(new Vec3(0, 0, 1), 0f);

        Assert.Equal(PlaneSideFlags.Back, BrushGeometry.BrushMostlyOnSide(build, box, zero));
    }

    [Fact]
    public async Task CopyBrushDuplicatesTheWindingsRatherThanSharingThem()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        BspBrush box = CsgFixture.Box(build, new Vec3(-64, -64, -64), new Vec3(64, 64, 64));
        BspBrush copy = BrushGeometry.CopyBrush(build, box);

        Assert.NotEqual(box.Sides[0].Winding, copy.Sides[0].Winding);
        Assert.Equal(
            build.Windings.Points(box.Sides[0].Winding).ToArray(),
            build.Windings.Points(copy.Sides[0].Winding).ToArray());
    }

    /// <summary>
    /// Stock's <c>memcpy</c> of the header copies the id <c>AllocBrush</c> had
    /// just assigned, so a copy is not a new brush by id.
    /// </summary>
    [Fact]
    public async Task CopyBrushInheritsTheSourcesIdAndNextPointer()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        BspBrush box = CsgFixture.Box(build, new Vec3(-64, -64, -64), new Vec3(64, 64, 64));
        BspBrush other = CsgFixture.Box(build, new Vec3(0, 0, 0), new Vec3(1, 1, 1));
        box.Next = other;

        BspBrush copy = BrushGeometry.CopyBrush(build, box);

        Assert.Equal(box.Id, copy.Id);
        Assert.Same(other, copy.Next);
    }

    [Fact]
    public async Task SplitBrushDividesABoxThroughItsMiddle()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        BspBrush box = CsgFixture.Box(build, new Vec3(-64, -64, -64), new Vec3(64, 64, 64));
        int plane = build.Planes.Find(new Vec3(0, 0, 1), 0f);

        BrushGeometry.SplitBrush(build, box, plane, out BspBrush? front, out BspBrush? back);

        Assert.NotNull(front);
        Assert.NotNull(back);
        Assert.Equal(0f, front!.Mins.Z, 0.001f);
        Assert.Equal(0f, back!.Maxs.Z, 0.001f);
    }

    /// <summary>
    /// The midwinding side is a fresh slot of a zeroed allocation in stock, so
    /// it carries no contents, no surface flags and no bevel — only the plane,
    /// <c>TEXINFO_NODE</c> and the winding.
    /// </summary>
    [Fact]
    public async Task SplitBrushGivesTheMidwindingSideNoContentsOrSurfaceFlags()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Water, (-64, -64, -64), (64, 64, 64))));

        BspBrush? list = CsgFixture.AllBrushes(build);
        int plane = build.Planes.Find(new Vec3(0, 0, 1), 0f);

        BrushGeometry.SplitBrush(build, list!, plane, out BspBrush? front, out _);

        BspBrushSide mid = front!.Sides[front.SideCount - 1];
        Assert.Equal(BspBrushSide.TexInfoNode, mid.TexInfo);
        Assert.Equal(0, mid.Contents);
        Assert.Equal(0, mid.Surface);
        Assert.False(mid.Bevel);
        Assert.False(mid.Visible);
        Assert.Null(mid.Displacement);
    }

    /// <summary>
    /// The midwinding's plane is <c>planenum ^ i ^ 1</c>, so the FRONT half
    /// gets the mirrored plane and the back half the plane itself — each half's
    /// new face points into the other.
    /// </summary>
    [Fact]
    public async Task SplitBrushGivesTheTwoHalvesMirroredMidwindingPlanes()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        BspBrush box = CsgFixture.Box(build, new Vec3(-64, -64, -64), new Vec3(64, 64, 64));
        int plane = build.Planes.Find(new Vec3(0, 0, 1), 0f);

        BrushGeometry.SplitBrush(build, box, plane, out BspBrush? front, out BspBrush? back);

        int frontMid = front!.Sides[front.SideCount - 1].PlaneNumber;
        int backMid = back!.Sides[back.SideCount - 1].PlaneNumber;

        Assert.Equal(plane ^ 1, frontMid);
        Assert.Equal(plane, backMid);
    }

    /// <summary>
    /// A plane a tenth of a unit outside the brush does not split it: stock's
    /// threshold is 0.1 and not <see cref="BrushGeometry.PlaneSideEpsilon"/>.
    /// </summary>
    [Fact]
    public async Task SplitBrushCopiesTheWholeBrushWhenItBarelyPokesThrough()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        BspBrush box = CsgFixture.Box(build, new Vec3(-64, -64, -64), new Vec3(64, 64, 64));
        int plane = build.Planes.Find(new Vec3(0, 0, 1), 63.95f);

        BrushGeometry.SplitBrush(build, box, plane, out BspBrush? front, out BspBrush? back);

        Assert.Null(front);
        Assert.NotNull(back);
    }

    [Fact]
    public async Task SplitBrushCopiesTheWholeBrushWhenThePlaneMissesIt()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        BspBrush box = CsgFixture.Box(build, new Vec3(-64, -64, -64), new Vec3(64, 64, 64));
        int plane = build.Planes.Find(new Vec3(0, 0, 1), 512f);

        BrushGeometry.SplitBrush(build, box, plane, out BspBrush? front, out BspBrush? back);

        Assert.Null(front);
        Assert.NotNull(back);
        Assert.Equal(6, back!.SideCount);
    }

    /// <summary>
    /// The last rejection in <c>SplitBrush</c> is on volume, at one cubic unit,
    /// and it runs after both halves are otherwise complete — so a split a
    /// hair inside the brush produces ONE half and not two.
    /// </summary>
    [Fact]
    public async Task SplitBrushDropsAHalfWhoseVolumeIsUnderOneCubicUnit()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        // A 2x2 column, split 0.2 below its top: the sliver is 2*2*0.2 = 0.8.
        BspBrush column = CsgFixture.Box(build, new Vec3(-1, -1, -64), new Vec3(1, 1, 64));
        int plane = build.Planes.Find(new Vec3(0, 0, 1), 63.8f);

        BrushGeometry.SplitBrush(build, column, plane, out BspBrush? front, out BspBrush? back);

        Assert.Null(front);
        Assert.NotNull(back);
    }

    [Fact]
    public async Task SplitBrushLeavesTheOriginalBrushAlone()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        BspBrush box = CsgFixture.Box(build, new Vec3(-64, -64, -64), new Vec3(64, 64, 64));
        int plane = build.Planes.Find(new Vec3(0, 0, 1), 0f);

        BrushGeometry.SplitBrush(build, box, plane, out _, out _);

        Assert.Equal(6, box.SideCount);
        Assert.Equal(new Vec3(-64, -64, -64), box.Mins);
        Assert.Equal(new Vec3(64, 64, 64), box.Maxs);
    }
}
