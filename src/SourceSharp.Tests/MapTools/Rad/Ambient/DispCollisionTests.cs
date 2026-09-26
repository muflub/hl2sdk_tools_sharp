//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Rad.Ambient;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Ambient;

/// <summary>
/// The ray half of <c>CDispCollTree</c>.
/// <c>ComputeIntersectionBarycentricCoordinates</c>.
/// <c>IsBoxIntersectingRay</c> and <c>DispTested_t</c>.
/// </summary>
public sealed class DispCollisionTests
{
    /// <summary>
    /// A flat power-2 displacement on z = 0 over [0,64] x [0,64], opaque, with
    /// luxel coordinates from a 16-unit lightmap.
    /// </summary>
    private static DispCollisionTree Flat(int flags = 0, int contents = 1)
    {
        CoreDispInfo disp = new(2);
        disp.Surface.SetPoint(0, new Vec3(0, 0, 0));
        disp.Surface.SetPoint(1, new Vec3(0, 64, 0));
        disp.Surface.SetPoint(2, new Vec3(64, 64, 0));
        disp.Surface.SetPoint(3, new Vec3(64, 0, 0));
        disp.Surface.Contents = contents;
        disp.Surface.PointStart = new Vec3(0, 0, 0);
        disp.Surface.FindSurfPointStartIndex();
        disp.Surface.AdjustSurfPointData();
        disp.Surface.CalcLuxelCoords(16, false, new Vec3(1f / 16, 0, 0), new Vec3(0, 1f / 16, 0));

        int n = disp.Size;
        disp.InitDispInfo(
            unchecked((int)0x80000000) | flags,
            new float[n],
            Enumerable.Repeat(new Vec3(0, 0, 1), n).ToArray(),
            new float[n]);
        disp.Create();
        return new DispCollisionTree(disp, face: 7);
    }

    [Fact]
    public void MortonIndexInterleavesXIntoTheEvenBits()
    {
        //: x = 3 (0b11), y = 1 (0b1) -> 0b0111.
        Assert.Equal(7, DispCollisionTree.IndexFromComponents(3, 1));
    }

    [Fact]
    public void NodeCountIncludesTheLeaves()
    {
        //: power 2 is 1 + 4 + 16 = 21.
        Assert.Equal(21, DispCollisionTree.NodesCalcCount(2));
    }

    [Fact]
    public void ARayDownOntoTheSurfaceHitsAtItsHeight()
    {
        DispCollisionTree tree = Flat();

        Assert.True(tree.Ray(new Vec3(20, 30, 10), new Vec3(0, 0, -20), out DispRayHit hit));
        Assert.Equal(0.5f, hit.Distance, 5);
    }

    [Fact]
    public void AHitReportsTheDisplacementsBaseFace()
    {
        DispCollisionTree tree = Flat();

        tree.Ray(new Vec3(20, 30, 10), new Vec3(0, 0, -20), out DispRayHit hit);

        Assert.Equal(7, hit.Face);
    }

    [Fact]
    public void TheLuxelCoordinateIsInterpolatedFromTheVertices()
    {
        // 16 units per luxel over a 64-unit edge: CalcLuxelCoords makes it
        // int(64/16) + 1 = 5 luxels, corners at 0.5 and 5.5,
        // so (20, 30) sits at 0.5 + 5*20/64, 0.5 + 5*30/64.
        DispCollisionTree tree = Flat();

        tree.Ray(new Vec3(20, 30, 10), new Vec3(0, 0, -20), out DispRayHit hit);

        Assert.Equal(2.0625f, hit.LuxelS, 3);
        Assert.Equal(2.84375f, hit.LuxelT, 3);
    }

    [Fact]
    public void AHorizontalRayAboveTheSurfaceMisses()
    {
        DispCollisionTree tree = Flat();

        Assert.False(tree.Ray(new Vec3(-10, 30, 5), new Vec3(100, 0, 0), out _));
    }

    [Fact]
    public void ARayThatStopsShortMisses()
    {
        // The barycentric t must be in (0, 1 + 1e-3].
        DispCollisionTree tree = Flat();

        Assert.False(tree.Ray(new Vec3(20, 30, 10), new Vec3(0, 0, -5), out _));
    }

    [Fact]
    public void ANoRayCollisionDisplacementIsTransparent()
    {
        //, SURF_NORAY_COLL.
        DispCollisionTree tree = Flat(flags: DispCollisionTree.SurfNoRayColl);

        Assert.False(tree.Ray(new Vec3(20, 30, 10), new Vec3(0, 0, -20), out _));
    }

    [Fact]
    public void ANonOpaqueDisplacementIsTransparent()
    {
        //, contents & MASK_OPAQUE.
        DispCollisionTree tree = Flat(contents: 0x8);

        Assert.False(tree.Ray(new Vec3(20, 30, 10), new Vec3(0, 0, -20), out _));
    }

    [Fact]
    public void TheBoundsAreBloatedByAUnit()
    {
        DispCollisionTree tree = Flat();

        Assert.Equal((new Vec3(-1, -1, -1), new Vec3(65, 65, 1)), (tree.Mins, tree.Maxs));
    }

    [Fact]
    public void BarycentricRefusesARayParallelToTheTriangle()
    {
        // |denom| < 1e-6.
        bool hit = DispCollisionTree.IntersectBarycentric(
            new Vec3(0, 0, 1), new Vec3(1, 0, 0), new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0),
            out _, out _, out _);

        Assert.False(hit);
    }

    [Fact]
    public void BarycentricCoordinatesLocateTheHit()
    {
        DispCollisionTree.IntersectBarycentric(
            new Vec3(0.25f, 0.5f, 1), new Vec3(0, 0, -2), new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0),
            out float u, out float v, out float t);

        Assert.Equal((0.25f, 0.5f, 0.5f), (u, v, t));
    }

    [Fact]
    public void ABoxBehindTheRayIsMissed()
    {
        Assert.False(DispCollisionTree.IsBoxIntersectingRay(
            new Vec3(-10, -10, -10), new Vec3(-5, -5, -5), new Vec3(0, 0, 0), new Vec3(1, 1, 1), 0));
    }

    [Fact]
    public void ABoxAlongTheRayIsHit()
    {
        Assert.True(DispCollisionTree.IsBoxIntersectingRay(
            new Vec3(4, 4, 4), new Vec3(6, 6, 6), new Vec3(0, 0, 0), new Vec3(10, 10, 10), 0));
    }

    [Fact]
    public void TheToleranceWidensTheBox()
    {
        // A ray passing 0.02 beside the box: missed at 0, hit at 1/32.
        Vec3 min = new(0, 0, 0), max = new(1, 1, 1);
        Vec3 start = new(-1, 1.02f, 0.5f), delta = new(3, 0, 0);

        Assert.Equal(
            (false, true),
            (DispCollisionTree.IsBoxIntersectingRay(min, max, start, delta, 0),
             DispCollisionTree.IsBoxIntersectingRay(min, max, start, delta, DispCollisionTree.DistEpsilon)));
    }

    [Fact]
    public void AScratchTestsEachDisplacementOncePerRay()
    {
        DispTestedScratch scratch = new(4);
        scratch.StartRayTest();

        Assert.Equal((true, false), (scratch.TryMark(2), scratch.TryMark(2)));
    }

    [Fact]
    public void ANewRayForgetsWhatTheLastOneTested()
    {
        DispTestedScratch scratch = new(4);
        scratch.StartRayTest();
        _ = scratch.TryMark(2);
        scratch.StartRayTest();

        Assert.True(scratch.TryMark(2));
    }
}
