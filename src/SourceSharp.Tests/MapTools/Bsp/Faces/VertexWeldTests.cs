using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Faces;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Faces;

/// <summary>
/// The spatial hash that makes two faces share a vertex
/// (<c>GetVertexnum</c>, <c>src/utils/vbsp/faces.cpp:112</c>).
/// </summary>
/// <remarks>
/// Every table starts with the error vertex 0 that <c>BeginBSPFile</c>
/// reserves (<c>writebsp.cpp:1138</c>), so the first welded vertex is 1 and a
/// table holding N welded vertices has <c>Count</c> N + 1.
/// </remarks>
public class VertexWeldTests
{
    [Fact]
    public void TheHashIsTwoHundredAndFiftySixCellsAcross()
    {
        // COORD_EXTENT >> HASH_BITS, which is 32768 >> 7.
        Assert.Equal(256, VertexWeld.HashSize);
    }

    [Fact]
    public void TheHashIgnoresZEntirely()
    {
        Assert.Equal(
            VertexWeld.HashVec(new Vec3(100f, 200f, -4000f)),
            VertexWeld.HashVec(new Vec3(100f, 200f, 4000f)));
    }

    [Fact]
    public void PointsOneHundredAndTwentyEightUnitsApartInXAreInDifferentCells()
    {
        Assert.NotEqual(
            VertexWeld.HashVec(new Vec3(0f, 0f, 0f)),
            VertexWeld.HashVec(new Vec3(128f, 0f, 0f)));
    }

    [Fact]
    public void APointOutsideTheWorldIsAnError()
    {
        Assert.Throws<InvalidOperationException>(
            () => VertexWeld.HashVec(new Vec3(99999f, 0f, 0f)));
    }

    [Fact]
    public void ANewTableHoldsOnlyTheErrorVertex()
    {
        VertexWeld weld = new(new FaceCounters());

        Assert.Equal(1, weld.Count);
    }

    [Fact]
    public void TheErrorVertexIsTheOrigin()
    {
        // dvertexes is a zeroed global and slot 0 is never written.
        VertexWeld weld = new(new FaceCounters());

        Assert.Equal(Vec3.Zero, weld[0]);
    }

    [Fact]
    public void TheErrorVertexIsNotCounted()
    {
        FaceCounters counters = new();
        _ = new VertexWeld(counters);

        Assert.Equal((0, 0), (counters.TotalVerts, counters.UniqueVerts));
    }

    [Fact]
    public void ANearlyIntegralCoordinateIsSnappedToTheInteger()
    {
        VertexWeld weld = new(new FaceCounters());

        int index = weld.GetVertexNumber(new Vec3(16.005f, 0f, 0f));

        Assert.Equal(16f, weld[index].X);
    }

    [Fact]
    public void ACoordinateOffByMoreThanTheIntegralEpsilonIsKept()
    {
        VertexWeld weld = new(new FaceCounters());

        int index = weld.GetVertexNumber(new Vec3(16.5f, 0f, 0f));

        Assert.Equal(16.5f, weld[index].X);
    }

    [Fact]
    public void TwoPointsWithinThePointEpsilonBecomeOneVertex()
    {
        VertexWeld weld = new(new FaceCounters());

        // 0.05 apart on one axis, which is inside POINT_EPSILON but outside
        // INTEGRAL_EPSILON, so neither is snapped away first.
        int a = weld.GetVertexNumber(new Vec3(16.5f, 0f, 0f));
        int b = weld.GetVertexNumber(new Vec3(16.55f, 0f, 0f));

        Assert.Equal(a, b);
        Assert.Equal(2, weld.Count);
    }

    [Fact]
    public void TwoPointsFurtherApartThanThePointEpsilonStayTwoVertices()
    {
        VertexWeld weld = new(new FaceCounters());

        int a = weld.GetVertexNumber(new Vec3(16.5f, 0f, 0f));
        int b = weld.GetVertexNumber(new Vec3(16.7f, 0f, 0f));

        Assert.NotEqual(a, b);
        Assert.Equal(3, weld.Count);
    }

    [Fact]
    public void TheCountersRecordEveryPointOfferedAndEveryVertexKept()
    {
        FaceCounters counters = new();
        VertexWeld weld = new(counters);

        weld.GetVertexNumber(new Vec3(0f, 0f, 0f));
        weld.GetVertexNumber(new Vec3(0f, 0f, 0f));
        weld.GetVertexNumber(new Vec3(64f, 0f, 0f));

        Assert.Equal(3, counters.TotalVerts);
        Assert.Equal(2, counters.UniqueVerts);
    }

    [Fact]
    public void ResetEmptiesTheTableAndTheHashButKeepsTheErrorVertex()
    {
        VertexWeld weld = new(new FaceCounters());
        weld.GetVertexNumber(new Vec3(0f, 0f, 0f));

        weld.Reset();

        Assert.Equal(1, weld.Count);
        Assert.Equal(1, weld.GetVertexNumber(new Vec3(0f, 0f, 0f)));
    }

    [Fact]
    public void FindEdgeVertsCollectsEveryVertexInTheEndpointsCellSpan()
    {
        VertexWeld weld = new(new FaceCounters());

        // Three points along x, all at the same y, spread across three cells.
        weld.GetVertexNumber(new Vec3(0f, 0f, 0f));
        weld.GetVertexNumber(new Vec3(150f, 0f, 0f));
        weld.GetVertexNumber(new Vec3(300f, 0f, 0f));

        weld.FindEdgeVerts(new Vec3(0f, 0f, 0f), new Vec3(300f, 0f, 0f));

        Assert.Equal([1, 2, 3], weld.EdgeVerts.Order());
    }

    [Fact]
    public void FindEdgeVertsIsIndifferentToWhichEndIsGivenFirst()
    {
        VertexWeld weld = new(new FaceCounters());
        weld.GetVertexNumber(new Vec3(150f, 0f, 0f));

        weld.FindEdgeVerts(new Vec3(300f, 0f, 0f), new Vec3(0f, 0f, 0f));

        Assert.Single(weld.EdgeVerts);
    }

    // ---- slot 0 ---------------------------------------------------------
    //
    // BeginBSPFile reserves vertex 0 (writebsp.cpp:1138), so GetVertexnum's
    // zero chain terminator (faces.cpp:131) can never hide a real vertex. The
    // retired StockQuirk.WeldHashZeroSentinel described a table that started
    // at 0, which stock's never does.

    [Fact]
    public void NoRealVertexIsEverEmittedAtIndexZero()
    {
        VertexWeld weld = new(new FaceCounters());

        int first = weld.GetVertexNumber(new Vec3(0f, 0f, 0f));
        int unwelded = weld.EmitUnwelded(new Vec3(0f, 0f, 0f));

        Assert.Equal((1, 2), (first, unwelded));
    }

    [Fact]
    public void TheFirstWeldedVertexIsWeldedOntoAgain()
    {
        VertexWeld weld = new(new FaceCounters());

        int first = weld.GetVertexNumber(new Vec3(0f, 0f, 0f));
        int again = weld.GetVertexNumber(new Vec3(0f, 0f, 0f));

        Assert.Equal((1, 1, 2), (first, again, weld.Count));
    }

    [Fact]
    public void TheFirstWeldedVertexIsATJunctionCandidate()
    {
        VertexWeld weld = new(new FaceCounters());

        weld.GetVertexNumber(new Vec3(0f, 0f, 0f));
        weld.GetVertexNumber(new Vec3(64f, 0f, 0f));

        weld.FindEdgeVerts(new Vec3(0f, 0f, 0f), new Vec3(64f, 0f, 0f));

        Assert.Equal([1, 2], weld.EdgeVerts.Order());
    }

    [Fact]
    public void TheErrorVertexIsNeverATJunctionCandidate()
    {
        VertexWeld weld = new(new FaceCounters());
        weld.GetVertexNumber(new Vec3(64f, 0f, 0f));

        weld.FindEdgeVerts(new Vec3(-64f, 0f, 0f), new Vec3(64f, 0f, 0f));

        Assert.DoesNotContain(0, weld.EdgeVerts);
    }

    [Fact]
    public void NoWeldEmissionKeepsThePointExactlyAsGiven()
    {
        VertexWeld weld = new(new FaceCounters());

        // EmitUnwelded is -noweld's path, and the snapping lives inside
        // GetVertexnum, so a near-integral coordinate is NOT snapped.
        int index = weld.EmitUnwelded(new Vec3(16.005f, 0f, 0f));

        Assert.Equal(16.005f, weld[index].X);
    }
}
