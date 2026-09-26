//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Rad.Displacement;
using SourceSharp.MapTools.Rad.Light;

using Xunit;

using static SourceSharp.Tests.MapTools.Rad.Displacement.DispTestSurfaces;

namespace SourceSharp.Tests.MapTools.Rad.Displacement;

/// <summary>
/// <c>CDispCollTree</c>'s AABB tree and ray test
/// and the per-leaf state of <c>ClipRayToDispInLeaf</c>.
/// </summary>
public sealed class DispCollisionTests
{
    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 21)]
    [InlineData(3, 85)]
    [InlineData(4, 341)]
    public void NodesCalcCountIsTheQuadTreeSize(int power, int count) =>
        Assert.Equal(count, DispCollisionTree.CalcCount(power));

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 1)]
    [InlineData(0, 1, 2)]
    [InlineData(1, 1, 3)]
    [InlineData(2, 0, 4)]
    [InlineData(3, 3, 15)]
    public void LeafIndicesInterleaveColumnAndRowBits(int x, int y, int index) =>
        Assert.Equal(index, DispCollisionTree.IndexFromComponents(x, y));

    [Fact]
    public void APowerTwoTreeHasFiveNodesAndSixteenLeaves()
    {
        DispCollisionTree t = Surface().Tree;
        Assert.Equal(5, t.NodeCount);
        Assert.Equal(16, t.LeafCount);
    }

    [Fact]
    public void EachLeafHoldsItsCellsTwoTriangles()
    {
        DispCollisionTree t = Surface().Tree;

        // Cell (1, 2): leaf IndexFromComponents(1, 2) = 1 | 8 = 9; triangles 2 * (2 * 4 + 1).
        Assert.Equal((18, 19), t.LeafTris(9));
    }

    [Fact]
    public void TheBoundsAreTheSurfaceGrownByOneUnit()
    {
        DispCollisionTree t = Surface((x, y) => x * 10.0f).Tree;
        Assert.Equal(new Vec3(-1, -1, -1), t.Mins);
        Assert.Equal(new Vec3(257, 257, 41), t.Maxs);
    }

    [Fact]
    public void ARootChildBoxCoversItsQuadrant()
    {
        DispCollisionTree t = Surface().Tree;

        // Child 0 of the root: cells x 0..1, y 0..1 -> world 0..128.
        (Vec3 mins, Vec3 maxs) = t.ChildBox(0, 0);
        Assert.Equal(new Vec3(0, 0, 0), mins);
        Assert.Equal(new Vec3(128, 128, 0), maxs);
    }

    [Fact]
    public void ARayStraightDownHitsAtTheExpectedFraction()
    {
        VradDispSurface s = Surface((_, _) => 20.0f);
        DispRayHit hit = new(float.MaxValue, 0, 0, -1, -1, -1);
        Assert.True(DispCollision.Ray(s, new Vec3(100, 70, 120), new Vec3(0, 0, -200), ref hit));
        Assert.Equal(0.5f, hit.Dist, 5);
    }

    [Fact]
    public void ARayThatStopsShortMisses()
    {
        VradDispSurface s = Surface((_, _) => 20.0f);
        DispRayHit hit = new(float.MaxValue, 0, 0, -1, -1, -1);
        Assert.False(DispCollision.Ray(s, new Vec3(100, 70, 120), new Vec3(0, 0, -50), ref hit));
    }

    [Fact]
    public void AHitJustBehindTheStartIsNotTaken()
    {
        // The barycentric test accepts t down to -1e-3 (ComputeBoxOffset); the
        // caller's t > 0 rejects it.
        VradDispSurface s = Surface((_, _) => 20.0f);
        DispRayHit hit = new(float.MaxValue, 0, 0, -1, -1, -1);
        Assert.False(DispCollision.Ray(s, new Vec3(100, 70, 19.99f), new Vec3(0, 0, -100), ref hit));
    }

    [Fact]
    public void AHitFartherThanTheOneToBeatIsNotTaken()
    {
        VradDispSurface s = Surface((_, _) => 20.0f);
        DispRayHit hit = new(0.4f, 0, 0, -1, -1, -1);
        Assert.False(DispCollision.Ray(s, new Vec3(100, 70, 120), new Vec3(0, 0, -200), ref hit));
    }

    [Fact]
    public void ANonOpaqueDisplacementIsNeverHit()
    {
        CoreDispInfo core = Core((_, _) => 20.0f);
        core.Surface.Contents = 0x8; // CONTENTS_GRATE
        VradDispSurface s = VradDispSurface.Create(core, Tex(), new DirectLightingSettings(), false);
        DispRayHit hit = new(float.MaxValue, 0, 0, -1, -1, -1);
        Assert.False(DispCollision.Ray(s, new Vec3(100, 70, 120), new Vec3(0, 0, -200), ref hit));
    }

    [Fact]
    public void TheHitsBarycentricsRebuildTheHitPoint()
    {
        // ndxVerts are the triangle's 0, 2, 1,
        // and u, v run along verts[1]-verts[0] and verts[2]-verts[0] in that order.
        VradDispSurface s = Surface((x, y) => (x * 7.0f) + (y * 3.0f));
        Vec3 start = new(100, 70, 200);
        Vec3 delta = new(0, 0, -300);
        DispRayHit hit = new(float.MaxValue, 0, 0, -1, -1, -1);
        Assert.True(DispCollision.Ray(s, start, delta, ref hit));

        Vec3 v0 = s.Verts[hit.Vert0];
        Vec3 p = v0 + ((s.Verts[hit.Vert1] - v0) * hit.U) + ((s.Verts[hit.Vert2] - v0) * hit.V);
        Vec3 onRay = start + (delta * hit.Dist);
        Assert.True(Near(p, onRay, 1e-3f), $"{p} vs {onRay}");
    }

    [Fact]
    public void AParallelRayHasNoBarycentricIntersection()
    {
        Assert.False(DispCollision.IntersectBarycentric(
            new Vec3(0, 0, 1), new Vec3(10, 0, 0), new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0),
            out _, out _, out _));
    }

    [Fact]
    public void BarycentricCoordinatesLocateTheHit()
    {
        Assert.True(DispCollision.IntersectBarycentric(
            new Vec3(0.25f, 0.5f, 1), new Vec3(0, 0, -2), new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0),
            out float u, out float v, out float t));
        Assert.Equal(0.25f, u, 6);
        Assert.Equal(0.5f, v, 6);
        Assert.Equal(0.5f, t, 6);
    }

    [Fact]
    public void ABoxBehindTheRayIsNotIntersected() =>
        Assert.False(DispCollision.IsBoxIntersectingRay(
            new Vec3(-10, -10, -10), new Vec3(-5, -5, -5), Vec3.Zero, new Vec3(10, 10, 10), 0.03125f));

    [Fact]
    public void ABoxAcrossTheRayIsIntersected() =>
        Assert.True(DispCollision.IsBoxIntersectingRay(
            new Vec3(4, 4, 4), new Vec3(6, 6, 6), Vec3.Zero, new Vec3(10, 10, 10), 0.03125f));

    [Fact]
    public void InvDeltaIsFltMaxOnAZeroAxis() =>
        Assert.Equal(new Vec3(0.5f, float.MaxValue, -0.25f), DispCollision.InvDelta(new Vec3(2, 0, -4)));

    [Fact]
    public void PointFromBarycentricBlendsTheThreePoints() =>
        Assert.Equal(
            new DispUv(2.5f, 4.0f),
            DispCollision.PointFromBarycentric(new(1, 2), new(3, 2), new(1, 6), 0.75f, 0.5f));

    [Fact]
    public void ADisplacementIsTestedOncePerRay()
    {
        DispRayTestState state = new(3);
        state.StartRayTest();
        Assert.True(state.MarkTested(1));
        Assert.False(state.MarkTested(1));
    }

    [Fact]
    public void ANewRayClearsTheTestedMarks()
    {
        DispRayTestState state = new(3);
        state.StartRayTest();
        state.MarkTested(1);
        state.StartRayTest();
        Assert.True(state.MarkTested(1));
    }
}
