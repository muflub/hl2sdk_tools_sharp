using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Geometry;

using Xunit;

namespace SourceSharp.Tests.MapTools.Geometry;

/// <summary>
/// The port of the reference implementation, one behaviour per fact, with
/// expected values worked out by hand from the reference implementation rather
/// than recorded from a run of this code.
/// </summary>
public class PolylibTests
{
    private static Vec3[] UnitSquare() =>
    [
        new(0f, 0f, 0f),
        new(1f, 0f, 0f),
        new(1f, 1f, 0f),
        new(0f, 1f, 0f),
    ];

    private static Vec3[] CentredSquare() =>
    [
        new(-1f, -1f, 0f),
        new(1f, -1f, 0f),
        new(1f, 1f, 0f),
        new(-1f, 1f, 0f),
    ];

    // ---- BaseWindingForPlane ------------------------------------------------

    [Fact]
    public void BaseWindingForPlaneMakesFourPoints()
    {
        var arena = new WindingArena();
        Winding w = arena.BaseWindingForPlane(new Vec3(0f, 0f, 1f), 0f);
        Assert.Equal(4, w.Count);
    }

    [Fact]
    public void BaseWindingForPlaneUsesXAsUpForAZMajorPlane()
    {
        // -- a Z-major plane takes vup = (1,0,0). With
        // normal = +Z: vright = vup x normal = (0,-1,0), both scaled to 65536,
        // so p0 = org - vright + vup = (65536, 65536, 0).
        var arena = new WindingArena();
        Winding w = arena.BaseWindingForPlane(new Vec3(0f, 0f, 1f), 0f);
        Assert.Equal(new Vec3(65536f, 65536f, 0f), arena.Points(w)[0]);
    }

    [Fact]
    public void BaseWindingForPlaneUsesZAsUpForAnXMajorPlane()
    {
        // -- an X-major or Y-major plane takes vup =
        // (0,0,1). With normal = +X: vright = (0,0,1) x (1,0,0) = (0,1,0), so
        // p0 = org - vright + vup = (0, -65536, 65536). This is what makes the
        // starting quad's vertex ORDER depend on the plane's orientation.
        var arena = new WindingArena();
        Winding w = arena.BaseWindingForPlane(new Vec3(1f, 0f, 0f), 0f);
        Assert.Equal(new Vec3(0f, -65536f, 65536f), arena.Points(w)[0]);
    }

    [Fact]
    public void BaseWindingForPlaneReachesFourTimesTheCoordinateLimit()
    {
        // scales by MAX_COORD_INTEGER*4. A legal map only
        // reaches 16384, so the starting quad is four times larger than
        // anything it can be clipped against.
        Assert.Equal(4f * GeometryEpsilons.MaxCoordInteger, GeometryEpsilons.BaseWindingExtent);

        var arena = new WindingArena();
        Winding w = arena.BaseWindingForPlane(new Vec3(0f, 0f, 1f), 0f);
        Assert.Equal(65536f, arena.Points(w)[0].X);
    }

    [Fact]
    public void BaseWindingForPlaneSitsAtTheDistanceAlongTheNormal()
    {
        var arena = new WindingArena();
        Winding w = arena.BaseWindingForPlane(new Vec3(0f, 0f, 1f), 128f);
        foreach (Vec3 p in arena.Points(w))
        {
            Assert.Equal(128f, p.Z);
        }
    }

    [Fact]
    public void BaseWindingForPlaneWindsTheQuadInStocksOrder()
    {
        // p0 = -right +up, p1 = +right +up, p2 = +right -up, p3 = -right -up.
        var arena = new WindingArena();
        Winding w = arena.BaseWindingForPlane(new Vec3(0f, 0f, 1f), 0f);
        Span<Vec3> p = arena.Points(w);

        Assert.Equal(new Vec3(65536f, 65536f, 0f), p[0]);
        Assert.Equal(new Vec3(65536f, -65536f, 0f), p[1]);
        Assert.Equal(new Vec3(-65536f, -65536f, 0f), p[2]);
        Assert.Equal(new Vec3(-65536f, 65536f, 0f), p[3]);
    }

    // ---- Copy and Reverse ---------------------------------------------------

    [Fact]
    public void CopyTakesItsCapacityFromThePointCountNotTheSourceCapacity()
    {
        // -- AllocWinding(w->numpoints), not maxpoints. So
        // copying a 68-point reservation holding 3 points gives a 3-point
        // winding, and copying is also how stock compacts.
        var arena = new WindingArena();
        Winding big = arena.Alloc(68);
        arena.Points(arena.SetCount(big, 0));
        big = arena.SetCount(big, 3);
        arena.Storage(big)[0] = new Vec3(1f, 2f, 3f);

        Winding copy = arena.Copy(big);
        Assert.Equal(3, copy.Capacity);
    }

    [Fact]
    public void CopyReproducesThePoints()
    {
        var arena = new WindingArena();
        Winding w = arena.Create(UnitSquare());
        Winding copy = arena.Copy(w);
        Assert.Equal(UnitSquare(), arena.Points(copy).ToArray());
    }

    [Fact]
    public void ReverseMovesTheFirstPointToTheEnd()
    {
        // -- c->p[i] = w->p[numpoints-1-i]. A reversal that
        // pinned point 0 and reversed the rest would describe the same polygon
        // and pass every geometric check, while putting every index-paired
        // consumer off by one.
        var arena = new WindingArena();
        Winding w = arena.Create(UnitSquare());
        Winding r = arena.Reverse(w);
        Span<Vec3> p = arena.Points(r);

        Assert.Equal(new Vec3(0f, 1f, 0f), p[0]);
        Assert.Equal(new Vec3(0f, 0f, 0f), p[3]);
    }

    // ---- Area, centre, bounds ---------------------------------------------.

    [Fact]
    public void AreaOfAUnitSquareIsOne()
    {
        var arena = new WindingArena();
        Assert.Equal(1f, arena.Area(arena.Create(UnitSquare())));
    }

    [Fact]
    public void AreaOfATriangleIsHalfTheCrossProductLength()
    {
        // Legs of 2, so the cross is (0,0,4) and the area is 2.
        var arena = new WindingArena();
        Winding w = arena.Create([
            new Vec3(0f, 0f, 0f), new Vec3(2f, 0f, 0f), new Vec3(0f, 2f, 0f)
        ]);
        Assert.Equal(2f, arena.Area(w));
    }

    [Fact]
    public void CentreIsTheVertexAverageAndNotTheAreaCentroid()
    {
        // averages the VERTICES. A 4x4 square with one extra
        // vertex halfway along its bottom edge still has its area centroid at
        // (2,2), but the vertex average is pulled to (2, 1.6).
        var arena = new WindingArena();
        Winding w = arena.Create([
            new Vec3(0f, 0f, 0f),
            new Vec3(2f, 0f, 0f),
            new Vec3(4f, 0f, 0f),
            new Vec3(4f, 4f, 0f),
            new Vec3(0f, 4f, 0f),
        ]);

        Assert.Equal(new Vec3(2f, 1.6f, 0f), arena.Center(w));
    }

    [Fact]
    public void BalancePointIsTheAreaCentroidForTheSameWinding()
    {
        // The same five-vertex square as the fact above, whose vertex average
        // is (2, 1.6, 0). WindingAreaAndBalancePoint weights by triangle area
        // instead and lands on the true centroid.
        var arena = new WindingArena();
        Winding w = arena.Create([
            new Vec3(0f, 0f, 0f),
            new Vec3(2f, 0f, 0f),
            new Vec3(4f, 0f, 0f),
            new Vec3(4f, 4f, 0f),
            new Vec3(0f, 4f, 0f),
        ]);

        float area = arena.AreaAndBalancePoint(w, out Vec3 centre);
        Assert.Equal(16f, area);
        Assert.Equal(2f, centre.X, 4);
        Assert.Equal(2f, centre.Y, 4);
    }

    [Fact]
    public void BalancePointOfAZeroAreaWindingStaysAtTheOrigin()
    {
        // the reference implementation's `if (total)` guard.
        var arena = new WindingArena();
        Winding w = arena.Create([
            new Vec3(1f, 1f, 0f), new Vec3(2f, 1f, 0f), new Vec3(3f, 1f, 0f)
        ]);

        Assert.Equal(0f, arena.AreaAndBalancePoint(w, out Vec3 centre));
        Assert.Equal(Vec3.Zero, centre);
    }

    [Fact]
    public void BoundsOfASquareAreItsCorners()
    {
        var arena = new WindingArena();
        arena.Bounds(arena.Create(CentredSquare()), out Vec3 mins, out Vec3 maxs);
        Assert.Equal(new Vec3(-1f, -1f, 0f), mins);
        Assert.Equal(new Vec3(1f, 1f, 0f), maxs);
    }

    [Fact]
    public void BoundsOfAnEmptyWindingComeBackInsideOutAtNinetyNineThousand()
    {
        // seeds with +/-99999 and not with infinities or with
        // MAX_COORD_INTEGER. The inside-out result is what a union loop uses as
        // its identity element, so it is load-bearing rather than sloppy.
        var arena = new WindingArena();
        arena.Bounds(arena.Alloc(4), out Vec3 mins, out Vec3 maxs);
        Assert.Equal(new Vec3(99999f, 99999f, 99999f), mins);
        Assert.Equal(new Vec3(-99999f, -99999f, -99999f), maxs);
    }

    // ---- WindingPlane -------------------------------------------------------

    [Fact]
    public void WindingPlaneCrossesTheSecondEdgeWithTheFirstAndNotTheOtherWayRound()
    {
        // is CrossProduct(v2, v1, normal). For this triangle
        // v1 x v2 would give +Z; the stock order gives -Z, and since portals
        // are matched to their opposites by plane sign, the other order would
        // invert visibility.
        var arena = new WindingArena();
        Winding w = arena.Create([
            new Vec3(0f, 0f, 0f), new Vec3(1f, 0f, 0f), new Vec3(1f, 1f, 0f)
        ]);

        Assert.Equal(new Vec3(0f, 0f, -1f), arena.WindingPlane(w).Normal);
    }

    [Fact]
    public void WindingPlaneIgnoresPointTwoWhenThereAreMoreThanThreePoints()
    {
        // the reference implementation's "HACKHACK: Avoid potentially collinear verts" takes
        // the second edge to point 3. So moving point 2 anywhere at all does
        // not change the plane of a four-point winding.
        var arena = new WindingArena();
        Winding flat = arena.Create([
            new Vec3(0f, 0f, 0f), new Vec3(1f, 0f, 0f),
            new Vec3(1f, 1f, 0f), new Vec3(0f, 1f, 0f),
        ]);
        Winding bent = arena.Create([
            new Vec3(0f, 0f, 0f), new Vec3(1f, 0f, 0f),
            new Vec3(1f, 1f, -7f), new Vec3(0f, 1f, 0f),
        ]);

        Assert.Equal(arena.WindingPlane(flat), arena.WindingPlane(bent));
    }

    [Fact]
    public void WindingPlaneUsesPointTwoWhenThereAreExactlyThreePoints()
    {
        var arena = new WindingArena();
        Winding w = arena.Create([
            new Vec3(0f, 0f, 0f), new Vec3(1f, 0f, 0f), new Vec3(1f, 1f, 4f)
        ]);

        Assert.NotEqual(0f, arena.WindingPlane(w).Normal.Y);
    }

    [Fact]
    public void WindingPlaneRejectsAWindingWithFewerThanThreePoints()
    {
        var arena = new WindingArena();
        Winding w = arena.Create([new Vec3(0f, 0f, 0f), new Vec3(1f, 0f, 0f)]);
        Assert.Throws<InvalidWindingException>(() => arena.WindingPlane(w));
    }

    // ---- RemoveColinearPoints ----------------------------------------------

    [Fact]
    public void RemoveColinearPointsDropsAVertexInTheMiddleOfAStraightEdge()
    {
        var arena = new WindingArena();
        Winding w = arena.Create([
            new Vec3(0f, 0f, 0f),
            new Vec3(2f, 0f, 0f),
            new Vec3(4f, 0f, 0f),
            new Vec3(4f, 4f, 0f),
            new Vec3(0f, 4f, 0f),
        ]);

        w = arena.RemoveColinearPoints(w);
        Assert.Equal(4, w.Count);
        Assert.Equal(new Vec3(4f, 0f, 0f), arena.Points(w)[1]);
    }

    [Fact]
    public void RemoveColinearPointsKeepsASquareIntact()
    {
        var arena = new WindingArena();
        Winding w = arena.Create(UnitSquare());
        Assert.Equal(4, arena.RemoveColinearPoints(w).Count);
    }

    [Fact]
    public void RemoveColinearPointsDropsATwoDegreeTurn()
    {
        // cos 2 degrees is 0.99939, above the 0.999 threshold, so the vertex
        // goes. That pins the threshold at about 2.56 degrees rather than at
        // "exactly straight".
        var arena = new WindingArena();
        Assert.Equal(3, arena.RemoveColinearPoints(TurnAt(arena, 2f)).Count);
    }

    [Fact]
    public void RemoveColinearPointsKeepsAThreeDegreeTurn()
    {
        // cos 3 degrees is 0.99863, below the threshold, so the vertex stays.
        var arena = new WindingArena();
        Assert.Equal(4, arena.RemoveColinearPoints(TurnAt(arena, 3f)).Count);
    }

    private static Winding TurnAt(WindingArena arena, float degrees)
    {
        float t = degrees * MathF.PI / 180f;
        return arena.Create([
            new Vec3(0f, 0f, 0f),
            new Vec3(100f, 0f, 0f),
            new Vec3(100f + (100f * MathF.Cos(t)), 100f * MathF.Sin(t), 0f),
            new Vec3(0f, 100f, 0f),
        ]);
    }

    // ---- ClipWindingEpsilon -------------------------------------------------

    [Fact]
    public void ClipSendsACoplanarWindingToTheBackAndNotTheFront()
    {
        // -- `if (!counts[0])`, and counts[0] is the FRONT
        // count. A winding entirely inside the epsilon slab has no front points
        // and no back points, takes that branch, and comes out as BACK with the
        // front left null. Any caller reading only the front silently drops
        // coplanar geometry.
        var arena = new WindingArena();
        Winding w = arena.Create(CentredSquare());

        arena.ClipEpsilon(w, new Vec3(0f, 0f, 1f), 0f, GeometryEpsilons.OnEpsilonFloat,
            out Winding front, out Winding back);

        Assert.True(front.IsNull);
        Assert.False(back.IsNull);
        Assert.Equal(4, back.Count);
    }

    [Fact]
    public void ClassifySendsACoplanarWindingToOnRatherThanToBack()
    {
        // The extra early-out is the ONLY difference between
        // the two functions, and it means they genuinely disagree about a
        // coplanar winding.
        var arena = new WindingArena();
        Winding w = arena.Create(CentredSquare());

        arena.ClassifyEpsilon(w, new Vec3(0f, 0f, 1f), 0f, GeometryEpsilons.OnEpsilonFloat,
            out Winding front, out Winding back, out Winding on);

        Assert.True(front.IsNull);
        Assert.True(back.IsNull);
        Assert.Equal(4, on.Count);
    }

    [Fact]
    public void ClipSplitsASquareIntoTwoHalvesOfFourPointsEach()
    {
        var arena = new WindingArena();
        Winding w = arena.Create(CentredSquare());

        arena.ClipEpsilon(w, new Vec3(1f, 0f, 0f), 0f, GeometryEpsilons.OnEpsilonFloat,
            out Winding front, out Winding back);

        Assert.Equal(4, front.Count);
        Assert.Equal(4, back.Count);
    }

    [Fact]
    public void ClipPutsTheSplitPointsInTheOrderTheEdgesAreWalked()
    {
        // Walking p0(back) p1(front) p2(front) p3(back), the front half is
        // split, p1, p2, split and the back half is p0, split, split, p3.
        var arena = new WindingArena();
        Winding w = arena.Create(CentredSquare());

        arena.ClipEpsilon(w, new Vec3(1f, 0f, 0f), 0f, GeometryEpsilons.OnEpsilonFloat,
            out Winding front, out Winding back);

        Assert.Equal(
            new[]
            {
                new Vec3(0f, -1f, 0f), new Vec3(1f, -1f, 0f),
                new Vec3(1f, 1f, 0f), new Vec3(0f, 1f, 0f),
            },
            arena.Points(front).ToArray());

        Assert.Equal(
            new[]
            {
                new Vec3(-1f, -1f, 0f), new Vec3(0f, -1f, 0f),
                new Vec3(0f, 1f, 0f), new Vec3(-1f, 1f, 0f),
            },
            arena.Points(back).ToArray());
    }

    [Fact]
    public void ClipHalvesTheAreaOfASymmetricSplit()
    {
        var arena = new WindingArena();
        Winding w = arena.Create(CentredSquare());

        arena.ClipEpsilon(w, new Vec3(1f, 0f, 0f), 0f, GeometryEpsilons.OnEpsilonFloat,
            out Winding front, out Winding back);

        Assert.Equal(2f, arena.Area(front));
        Assert.Equal(2f, arena.Area(back));
    }

    [Fact]
    public void ClipSnapsTheSplitPointOntoAnAxialPlaneInsteadOfInterpolating()
    {
        // -- when normal[j] is exactly 1 the new vertex takes
        // `dist` outright. Here the interpolation would give
        // -1 + 0.55f*2 == 0.10000002384, which is NOT 0.1f. This is why a
        // grid-aligned map comes out with exactly-integral vertices.
        var arena = new WindingArena();
        Winding w = arena.Create(CentredSquare());

        arena.ClipEpsilon(w, new Vec3(1f, 0f, 0f), 0.1f, GeometryEpsilons.OnEpsilonFloat,
            out Winding front, out _);

        Assert.Equal(0.1f, arena.Points(front)[0].X);
        Assert.NotEqual(0.1f, -1f + (0.55f * 2f));
    }

    [Fact]
    public void ClipInterpolatesOnANonAxialPlane()
    {
        // The other arm of the same branch: a 45-degree plane has no component
        // equal to 1, so every component of the split point is interpolated.
        var arena = new WindingArena();
        Winding w = arena.Create(CentredSquare());
        float k = MathF.Sqrt(0.5f);

        arena.ClipEpsilon(w, new Vec3(k, k, 0f), 0f, GeometryEpsilons.OnEpsilonFloat,
            out Winding front, out _);

        Assert.Equal(3, front.Count);
    }

    [Fact]
    public void ClipDoesNotFreeItsInput()
    {
        // the reference implementation's ClipWindingEpsilon leaves `in` alone; only
        // ChopWinding frees it. Three windings are live afterwards.
        var arena = new WindingArena();
        Winding w = arena.Create(CentredSquare());

        arena.ClipEpsilon(w, new Vec3(1f, 0f, 0f), 0f, GeometryEpsilons.OnEpsilonFloat,
            out _, out _);

        Assert.Equal(3, arena.ActiveWindings);
    }

    [Fact]
    public void ClipReservesFourMoreThanTheInputPointCount()
    {
        // -- "cant use counts[0]+2 because of fp grouping
        // errors". Stock does not trust its own side classification to agree
        // with the interpolation that follows it.
        var arena = new WindingArena();
        Winding w = arena.Create(CentredSquare());

        arena.ClipEpsilon(w, new Vec3(1f, 0f, 0f), 0f, GeometryEpsilons.OnEpsilonFloat,
            out Winding front, out _);

        Assert.Equal(8, front.Capacity);
    }

    [Fact]
    public void ClipRejectsAWindingTooLongForTheSideBuffer()
    {
        // Stock writes dists[numpoints] into an array of MAX_POINTS_ON_WINDING
        // + 4 entries(and:395), so a 68-point winding runs
        // one past the end. The port refuses the input instead.
        var arena = new WindingArena();
        Winding w = arena.SetCount(arena.Alloc(100), 100);

        Assert.Throws<InvalidWindingException>(() =>
            arena.ClipEpsilon(w, new Vec3(1f, 0f, 0f), 0f, 0.1f, out _, out _));
    }

    // ---- ChopWinding --------------------------------------------------------

    [Fact]
    public void ChopKeepsOnlyTheFront()
    {
        var arena = new WindingArena();
        Winding w = arena.Create(CentredSquare());
        Winding front = arena.Chop(w, new Vec3(1f, 0f, 0f), 0f);

        Assert.Equal(4, front.Count);
        Assert.Equal(2f, arena.Area(front));
    }

    [Fact]
    public void ChopFreesBothTheInputAndTheDiscardedBack()
    {
        //. One winding live afterwards, not three.
        var arena = new WindingArena();
        Winding w = arena.Create(CentredSquare());
        arena.Chop(w, new Vec3(1f, 0f, 0f), 0f);

        Assert.Equal(1, arena.ActiveWindings);
    }

    [Fact]
    public void ChopInPlaceReturnsTheVERYSAMEHandleWhenNothingIsBehindThePlane()
    {
        // -- "inout stays the same". No copy is made and the
        // original is not freed, which is the one behaviour ClipWindingEpsilon
        // does NOT share: it would have copied.
        var arena = new WindingArena();
        Winding w = arena.Create(CentredSquare());
        Winding result = arena.ChopInPlace(w, new Vec3(0f, 0f, 1f), -10f, 0.1f);

        Assert.Equal(w, result);
        Assert.Equal(1, arena.ActiveWindings);
    }

    [Fact]
    public void ChopInPlaceReturnsNullAndFreesTheInputWhenNothingIsInFront()
    {
        var arena = new WindingArena();
        Winding w = arena.Create(CentredSquare());
        Winding result = arena.ChopInPlace(w, new Vec3(0f, 0f, 1f), 10f, 0.1f);

        Assert.True(result.IsNull);
        Assert.Equal(0, arena.ActiveWindings);
    }

    [Fact]
    public void ChopInPlaceFreesTheInputWhenItActuallyClips()
    {
        var arena = new WindingArena();
        Winding w = arena.Create(CentredSquare());
        Winding result = arena.ChopInPlace(w, new Vec3(1f, 0f, 0f), 0f, 0.1f);

        Assert.Equal(4, result.Count);
        Assert.Equal(1, arena.ActiveWindings);
    }

    // ---- CheckWinding -------------------------------------------------------

    [Fact]
    public void CheckAcceptsAPlausibleSquare()
    {
        var arena = new WindingArena();
        arena.Check(arena.Create([
            new Vec3(0f, 0f, 0f), new Vec3(10f, 0f, 0f),
            new Vec3(10f, 10f, 0f), new Vec3(0f, 10f, 0f),
        ]));
    }

    [Fact]
    public void CheckRejectsFewerThanThreePoints()
    {
        var arena = new WindingArena();
        Winding w = arena.Create([new Vec3(0f, 0f, 0f), new Vec3(1f, 0f, 0f)]);
        Assert.Throws<InvalidWindingException>(() => arena.Check(w));
    }

    [Fact]
    public void CheckRejectsAnAreaBelowOneSquareUnit()
    {
        // the reference implementation's threshold is ABSOLUTE, so a legitimately tiny face
        // is rejected exactly as readily as a degenerate one.
        var arena = new WindingArena();
        Winding w = arena.Create([
            new Vec3(0f, 0f, 0f), new Vec3(1f, 0f, 0f), new Vec3(0f, 1f, 0f)
        ]);

        Assert.Equal(0.5f, arena.Area(w));
        Assert.Throws<InvalidWindingException>(() => arena.Check(w));
    }

    [Fact]
    public void CheckRejectsACoordinateBeyondTheWorldLimit()
    {
        var arena = new WindingArena();
        Winding w = arena.Create([
            new Vec3(0f, 0f, 0f), new Vec3(20000f, 0f, 0f), new Vec3(0f, 20000f, 0f)
        ]);

        Assert.Throws<InvalidWindingException>(() => arena.Check(w));
    }

    [Fact]
    public void CheckRejectsANonConvexWinding()
    {
        // A square with one corner pulled in to (1,1). Still planar, still 10
        // square units, and every point is on the plane -- only convexity
        // fails, which is the condition WindingArea cannot see.
        var arena = new WindingArena();
        Winding w = arena.Create([
            new Vec3(0f, 0f, 0f), new Vec3(10f, 0f, 0f),
            new Vec3(1f, 1f, 0f), new Vec3(0f, 10f, 0f),
        ]);

        Assert.Equal(10f, arena.Area(w));
        Assert.Throws<InvalidWindingException>(() => arena.Check(w));
    }

    [Fact]
    public void CheckRejectsAPointOffThePlane()
    {
        var arena = new WindingArena();
        Winding w = arena.Create([
            new Vec3(0f, 0f, 0f), new Vec3(10f, 0f, 0f),
            new Vec3(10f, 10f, 0f), new Vec3(0f, 10f, 0f), new Vec3(-1f, 5f, 3f),
        ]);

        Assert.Throws<InvalidWindingException>(() => arena.Check(w));
    }

    // ---- WindingOnPlaneSide -------------------------------------------------

    [Fact]
    public void OnPlaneSideReportsFrontWhenEveryPointIsInFront()
    {
        var arena = new WindingArena();
        Winding w = arena.Create(CentredSquare());
        Assert.Equal(PlaneSide.Front, arena.OnPlaneSide(w, new Vec3(0f, 0f, 1f), -5f));
    }

    [Fact]
    public void OnPlaneSideReportsBackWhenEveryPointIsBehind()
    {
        var arena = new WindingArena();
        Winding w = arena.Create(CentredSquare());
        Assert.Equal(PlaneSide.Back, arena.OnPlaneSide(w, new Vec3(0f, 0f, 1f), 5f));
    }

    [Fact]
    public void OnPlaneSideReportsOnForACoplanarWinding()
    {
        var arena = new WindingArena();
        Winding w = arena.Create(CentredSquare());
        Assert.Equal(PlaneSide.On, arena.OnPlaneSide(w, new Vec3(0f, 0f, 1f), 0f));
    }

    [Fact]
    public void OnPlaneSideReportsCrossForASpanningWinding()
    {
        var arena = new WindingArena();
        Winding w = arena.Create(CentredSquare());
        Assert.Equal(PlaneSide.Cross, arena.OnPlaneSide(w, new Vec3(1f, 0f, 0f), 0f));
    }

    [Fact]
    public void OnPlaneSideTreatsAPointInsideTheEpsilonAsNeitherSide()
    {
        // 0.05 is inside ON_EPSILON, so a square straddling the plane by less
        // than that is ON rather than CROSS.
        var arena = new WindingArena();
        Winding w = arena.Create([
            new Vec3(-1f, -1f, -0.05f), new Vec3(1f, -1f, 0.05f),
            new Vec3(1f, 1f, 0.05f), new Vec3(-1f, 1f, -0.05f),
        ]);

        Assert.Equal(PlaneSide.On, arena.OnPlaneSide(w, new Vec3(0f, 0f, 1f), 0f));
    }

    // ---- PointInWinding -----------------------------------------------------

    [Fact]
    public void PointInWindingAcceptsTheCentre()
    {
        var arena = new WindingArena();
        Assert.True(arena.PointInWinding(arena.Create(CentredSquare()), Vec3.Zero));
    }

    [Fact]
    public void PointInWindingRejectsAPointOutside()
    {
        var arena = new WindingArena();
        Assert.False(arena.PointInWinding(
            arena.Create(CentredSquare()), new Vec3(2f, 0f, 0f)));
    }

    [Fact]
    public void PointInWindingAcceptsAPointExactlyOnAnEdge()
    {
        // the reference implementation's test is `< 0.0f` with no epsilon at all, so an
        // on-edge point is inside and a point one float outside it is not.
        var arena = new WindingArena();
        Assert.True(arena.PointInWinding(
            arena.Create(CentredSquare()), new Vec3(1f, 0f, 0f)));
    }

    [Fact]
    public void PointInWindingRejectsANullWinding()
    {
        var arena = new WindingArena();
        Assert.False(arena.PointInWinding(Winding.Null, Vec3.Zero));
    }

    // ---- Translate and the offset clippers ---------------------------------

    [Fact]
    public void TranslateMovesEveryPoint()
    {
        var arena = new WindingArena();
        Winding w = arena.Create(UnitSquare());
        arena.Translate(w, new Vec3(10f, 20f, 30f));
        Assert.Equal(new Vec3(10f, 20f, 30f), arena.Points(w)[0]);
    }

    [Fact]
    public void ClipWithAnOffsetLeavesTheInputWhereItStarted()
    {
        // translates in, clips, and translates back.
        var arena = new WindingArena();
        Winding w = arena.Create(CentredSquare());

        arena.ClipEpsilonOffset(w, new Vec3(1f, 0f, 0f), 0f, 0.1f,
            new Vec3(1000f, 0f, 0f), out _, out _);

        Assert.Equal(CentredSquare(), arena.Points(w).ToArray());
    }

    [Fact]
    public void ClipWithAnOffsetProducesTheSameShapeAsClippingAtTheOrigin()
    {
        var arena = new WindingArena();
        Winding direct = arena.Create(CentredSquare());
        Winding offset = arena.Create(CentredSquare());

        arena.ClipEpsilon(direct, new Vec3(1f, 0f, 0f), 0f, 0.1f, out Winding a, out _);
        arena.ClipEpsilonOffset(offset, new Vec3(1f, 0f, 0f), 0f, 0.1f,
            new Vec3(64f, 0f, 0f), out Winding b, out _);

        Assert.Equal(arena.Points(a).ToArray(), arena.Points(b).ToArray());
    }

    [Fact]
    public void ClassifyWithAnOffsetTranslatesTheOnResultBackToo()
    {
        // -- the `on` output gets the same correction as the
        // other two, which the two-way offset clipper has no equivalent for.
        var arena = new WindingArena();
        Winding w = arena.Create(CentredSquare());

        arena.ClassifyEpsilonOffset(w, new Vec3(0f, 0f, 1f), 0f, 0.1f,
            new Vec3(0f, 0f, 500f), out _, out _, out Winding on);

        Assert.Equal(CentredSquare(), arena.Points(on).ToArray());
    }
}
