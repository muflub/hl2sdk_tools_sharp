//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Disp;

using Xunit;

namespace SourceSharp.Tests.MapTools.Disp;

/// <summary>
/// vrad's normal sewing, on hand-built
/// neighbour layouts whose normals start out different.
/// </summary>
public sealed class DispNormalSmootherTests
{
    private static readonly Vec3[] A = DispFixtures.UnitFloor();

    private static readonly Vec3[] Right = DispFixtures.FloorQuad(new Vec3(256, 0, 0), 256, 256);

    private static readonly Vec3[] Diagonal = DispFixtures.FloorQuad(new Vec3(256, 256, 0), 256, 256);

    /// <summary><c>RemapVal</c> maps linearly:.</summary>
    [Fact]
    public void RemapValIsLinear()
    {
        Assert.Equal(0.25f, DispNormalSmoother.RemapVal(3, 2, 6, 0, 1));
    }

    /// <summary>With an empty input range it is a step at B:.</summary>
    [Fact]
    public void RemapValIsAStepWhenTheRangeIsEmpty()
    {
        Assert.Equal(1.0f, DispNormalSmoother.RemapVal(2, 2, 2, 0, 1));
        Assert.Equal(0.0f, DispNormalSmoother.RemapVal(1, 2, 2, 0, 1));
    }

    /// <summary>
    /// The closest corner within 0.1 wins: <c>FindNeighborCornerVert</c>.
    /// </summary>
    [Fact]
    public void TheClosestCornerWithinATenthIsFound()
    {
        CoreDispInfo core = DispFixtures.Core(DispFixtures.Heightfield(2, A[0]), A);

        Assert.Equal((int)DispCorner.UpperRight,
            DispNormalSmoother.FindNeighborCornerVert(core, new Vec3(256.05f, 256, 0)));
    }

    /// <summary>A point more than 0.1 from every corner finds none.</summary>
    [Fact]
    public void APointFarFromEveryCornerFindsNone()
    {
        CoreDispInfo core = DispFixtures.Core(DispFixtures.Heightfield(2, A[0]), A);

        Assert.Equal(-1, DispNormalSmoother.FindNeighborCornerVert(core, new Vec3(128, 128, 0)));
    }

    /// <summary>
    /// Corner neighbours are listed before edge neighbours:
    /// <c>GetAllNeighbors</c>.
    /// </summary>
    [Fact]
    public void CornerNeighboursComeBeforeEdgeNeighbours()
    {
        CoreDispInfo core = new(2);
        ref DispCornerNeighbors c = ref core.CornerNeighbors(3);
        c.NumNeighbors = 1;
        c.Neighbors[0] = 7;
        ref DispNeighbor e = ref core.EdgeNeighbor(0);
        e.SubNeighbors[0].Neighbor = 4;

        Assert.Equal([7, 4], DispNormalSmoother.GetAllNeighbors(core));
    }

    /// <summary>
    /// Nothing de-duplicates: a hand-built table
    /// naming one displacement as edge and corner neighbour lists it twice.
    /// Real tables cannot.
    /// </summary>
    [Fact]
    public void ADoubleNeighbourIsListedTwice()
    {
        CoreDispInfo core = new(2);
        ref DispCornerNeighbors c = ref core.CornerNeighbors(0);
        c.NumNeighbors = 1;
        c.Neighbors[0] = 4;
        ref DispNeighbor e = ref core.EdgeNeighbor(0);
        e.SubNeighbors[0].Neighbor = 4;

        Assert.Equal([4, 4], DispNormalSmoother.GetAllNeighbors(core));
    }

    /// <summary>
    /// After <c>BlendEdges</c> the shared interior edge vertices carry one
    /// normal on both sides:.
    /// </summary>
    [Fact]
    public void BlendEdgesGivesASharedEdgeOneNormal()
    {
        (IReadOnlyList<DisplacementResult> r, _) = DispFixtures.Build(
        [
            (DispFixtures.Heightfield(2, A[0], (x, _) => x * 16), A),
            (DispFixtures.Heightfield(2, Right[0], (x, _) => 64 - (x * 8)), Right),
        ]);
        CoreDispInfo[] cores = [r[0].Core, r[1].Core];

        DispNormalSmoother.BlendEdges(cores, stockNormalise: false);

        Assert.Equal(cores[0].Normal(DispFixtures.Index(cores[0], 4, 2)),
            cores[1].Normal(DispFixtures.Index(cores[1], 0, 2)));
    }

    /// <summary>
    /// <c>BlendEdges</c> leaves the edge's end corners to <c>BlendCorners</c>:
    /// the walk consumes the first as <c>viPrevPos</c> and skips the last by
    /// <c>IsLastVert</c>.
    /// </summary>
    [Fact]
    public void BlendEdgesLeavesTheCornersAlone()
    {
        (IReadOnlyList<DisplacementResult> r, _) = DispFixtures.Build(
        [
            (DispFixtures.Heightfield(2, A[0], (x, _) => x * 16), A),
            (DispFixtures.Heightfield(2, Right[0], (x, _) => 64 - (x * 8)), Right),
        ]);
        CoreDispInfo[] cores = [r[0].Core, r[1].Core];
        int corner = DispFixtures.Index(cores[0], 4, 4);
        Vec3 before = cores[0].Normal(corner);

        DispNormalSmoother.BlendEdges(cores, stockNormalise: false);

        Assert.Equal(before, cores[0].Normal(corner));
    }

    /// <summary>
    /// After <c>BlendCorners</c> a shared corner has one normal:
    /// </summary>
    [Fact]
    public void BlendCornersGivesASharedCornerOneNormal()
    {
        (IReadOnlyList<DisplacementResult> r, _) = DispFixtures.Build(
        [
            (DispFixtures.Heightfield(2, A[0], (x, y) => (x + y) * 8), A),
            (DispFixtures.Heightfield(2, Diagonal[0], (x, y) => 64 + (y * 4)), Diagonal),
        ]);
        CoreDispInfo[] cores = [r[0].Core, r[1].Core];
        int a = DispFixtures.Index(cores[0], 4, 4);
        int d = DispFixtures.Index(cores[1], 0, 0);
        Assert.NotEqual(cores[0].Normal(a), cores[1].Normal(d));

        DispNormalSmoother.BlendCorners(cores, stockNormalise: false);

        Assert.Equal(cores[0].Normal(a), cores[1].Normal(d));
    }

    /// <summary>
    /// Where one edge meets two half-length neighbours, its midpoint and their
    /// two corners get one normal: <c>BlendTJuncs</c>.
    /// </summary>
    [Fact]
    public void BlendTJuncsGivesAJunctionOneNormal()
    {
        Vec3[] lower = DispFixtures.FloorQuad(new Vec3(256, 0, 0), 256, 128);
        Vec3[] upper = DispFixtures.FloorQuad(new Vec3(256, 128, 0), 256, 128);
        (IReadOnlyList<DisplacementResult> r, _) = DispFixtures.Build(
        [
            (DispFixtures.Heightfield(2, A[0]), A),
            (DispFixtures.Heightfield(2, lower[0], (x, _) => x * 8), lower),
            (DispFixtures.Heightfield(2, upper[0], (x, _) => x * -8), upper),
        ]);
        CoreDispInfo[] cores = [r[0].Core, r[1].Core, r[2].Core];
        Assert.True(r[0].Info.EdgeNeighbors[(int)DispEdge.Right].SubNeighbors[1].IsValid());

        DispNormalSmoother.BlendTJuncs(cores, stockNormalise: false);

        Vec3 mid = cores[0].Normal(DispFixtures.Index(cores[0], 4, 2));
        Assert.Equal(mid, cores[1].Normal(DispFixtures.Index(cores[1], 0, 4)));
        Assert.Equal(mid, cores[2].Normal(DispFixtures.Index(cores[2], 0, 0)));
    }

    /// <summary>
    /// A displacement with no neighbours keeps every normal through all three
    /// passes, corner renormalisation aside (its corners are re-normalised
    /// alone:).
    /// </summary>
    [Fact]
    public void ALoneDisplacementsInteriorNormalsAreUntouched()
    {
        CoreDispInfo core = DispFixtures.Core(
            DispFixtures.Heightfield(2, A[0], (x, y) => x * y), A);
        Vec3 before = core.Normal(DispFixtures.Index(core, 2, 2));

        DispNormalSmoother.SmoothNeighboringDispSurfNormals([core], stockNormalise: false);

        Assert.Equal(before, core.Normal(DispFixtures.Index(core, 2, 2)));
    }

    /// <summary>
    /// <c>BlendCorners</c> renormalises even an unshared corner, which undoes
    /// stock's short mean there.
    /// </summary>
    [Fact]
    public void AnUnsharedCornerIsRenormalised()
    {
        CoreDispInfo core = DispFixtures.Core(
            DispFixtures.Heightfield(2, A[0], (x, y) => (x == 1 && y == 0) ? 64 : 0), A, stockNormalMean: true);
        int corner = DispFixtures.Index(core, 0, 0);
        Assert.True(core.Normal(corner).Length() < 0.99f);

        DispNormalSmoother.BlendCorners([core], stockNormalise: false);

        Assert.Equal(1.0f, core.Normal(corner).Length(), 1e-6f);
    }
}
