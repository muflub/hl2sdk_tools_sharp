using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp.Portals;
using SourceSharp.MapTools.Diagnostics;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Portals;

/// <summary>
/// The convex outline of an areaportal (<c>portals.cpp:1012-1269</c>).
/// </summary>
public class AreaPortalGeometryTests
{
    [Fact]
    public void AngleOffsetOfADirectionWithItselfIsZero()
    {
        Assert.Equal(0f, AreaPortalGeometry.AngleOffset(0f, 0f));
    }

    [Fact]
    public void AngleOffsetMeasuresClockwiseSoASmallAnticlockwiseStepIsNearlyAFullTurn()
    {
        float offset = AreaPortalGeometry.AngleOffset(0f, 1f);

        Assert.Equal((2f * MathF.PI) - 1f, offset, 4);
    }

    [Fact]
    public void AngleOffsetIsAlwaysBelowAFullTurn()
    {
        for (int i = -20; i <= 20; i++)
        {
            float offset = AreaPortalGeometry.AngleOffset(0.5f, i * 0.3f);

            Assert.InRange(offset, 0f, 2f * MathF.PI);
        }
    }

    [Fact]
    public void FindUniquePointsKeepsTheFirstOfEachCluster()
    {
        (float X, float Y)[] points = [(0f, 0f), (0.05f, 0f), (10f, 0f)];
        Span<int> map = stackalloc int[8];

        int unique = AreaPortalGeometry.FindUniquePoints(points, map, 0.1f);

        Assert.Equal(2, unique);
        Assert.Equal(0, map[0]);
        Assert.Equal(2, map[1]);
    }

    [Fact]
    public void FindUniquePointsThrowsRatherThanOverflowingItsIndexList()
    {
        (float X, float Y)[] points = [(0f, 0f), (10f, 0f), (20f, 0f)];
        Span<int> map = stackalloc int[2];

        try
        {
            AreaPortalGeometry.FindUniquePoints(points, map, 0.1f);
            Assert.Fail("expected the overflow to be reported");
        }
        catch (MapCompileException)
        {
        }
    }

    [Fact]
    public void ConvexTwoDWrapsASquareInFourVertices()
    {
        (float X, float Y)[] square = [(0f, 0f), (10f, 0f), (10f, 10f), (0f, 10f)];
        Span<int> indices = stackalloc int[64];

        int count = AreaPortalGeometry.Convex2D(square, indices);

        Assert.Equal(4, count);
    }

    [Fact]
    public void ConvexTwoDDropsAPointInsideTheHull()
    {
        (float X, float Y)[] square = [(0f, 0f), (10f, 0f), (10f, 10f), (0f, 10f), (5f, 5f)];
        Span<int> indices = stackalloc int[64];

        int count = AreaPortalGeometry.Convex2D(square, indices);

        Assert.Equal(4, count);
        Assert.DoesNotContain(4, indices[..count].ToArray());
    }

    [Fact]
    public void ConvexTwoDCollapsesCoincidentPointsRatherThanLoopingForever()
    {
        // Stock's own comment: without the unique-set pass "we can loop around
        // forever and max out nMaxIndices".
        (float X, float Y)[] doubled =
            [(0f, 0f), (0f, 0f), (10f, 0f), (10f, 0f), (10f, 10f), (0f, 10f)];
        Span<int> indices = stackalloc int[64];

        int count = AreaPortalGeometry.Convex2D(doubled, indices);

        Assert.Equal(4, count);
    }

    [Fact]
    public void ConvexTwoDOfNothingIsNothing()
    {
        Span<int> indices = stackalloc int[8];

        Assert.Equal(0, AreaPortalGeometry.Convex2D([], indices));
    }

    [Fact]
    public void VectorAnglesOfStraightUpIsPitchTwoSeventy()
    {
        (float Pitch, float Yaw, float Roll) angles = AreaPortalGeometry.VectorAngles(new Vec3(0f, 0f, 1f));

        Assert.Equal(270f, angles.Pitch);
        Assert.Equal(0f, angles.Yaw);
        Assert.Equal(0f, angles.Roll);
    }

    [Fact]
    public void VectorAnglesOfStraightDownIsPitchNinety()
    {
        (float Pitch, float Yaw, float Roll) angles = AreaPortalGeometry.VectorAngles(new Vec3(0f, 0f, -1f));

        Assert.Equal(90f, angles.Pitch);
    }

    [Fact]
    public void VectorAnglesWrapsANegativeYawUpIntoThreeSixty()
    {
        (float Pitch, float Yaw, float Roll) angles = AreaPortalGeometry.VectorAngles(new Vec3(0f, -1f, 0f));

        Assert.Equal(270f, angles.Yaw, 3);
    }

    [Fact]
    public void AngleVectorsOfZeroIsTheEnginesDefaultBasis()
    {
        // Forward is +x, "right" is -y, up is +z. The right vector pointing at
        // -y is Source's convention, not a sign slip.
        (Vec3 Forward, Vec3 Right, Vec3 Up) basis = AreaPortalGeometry.AngleVectors((0f, 0f, 0f));

        Assert.Equal(new Vec3(1f, 0f, 0f), basis.Forward);
        Assert.Equal(new Vec3(0f, -1f, 0f), basis.Right);
        Assert.Equal(new Vec3(0f, 0f, 1f), basis.Up);
    }

    [Fact]
    public void TheClipHullOfAnAreaportalIsItsFourCorners()
    {
        PortalFixture f = PortalFixture.SealedRoomWithAreaportal();
        f.Add("light", new Vec3(0f, 0f, 20f));
        f.Portalise();
        EntityFlood.FloodEntities(f.Tree, f.Planes, f.Entities);
        EntityFlood.FillOutside(f.Tree.HeadNode);

        AreaFlood areas = new(f.Entities);
        areas.FloodAreas(f.Tree, f.Arena);

        Portal dividing = FindDividingPortal(f);

        IReadOnlyList<Vec3> hull = AreaPortalGeometry.ClipPortalGeometry(
            f.Tree.HeadNode, f.Planes, f.Arena, dividing, 1, 2);

        Assert.Equal(4, hull.Count);

        foreach (Vec3 v in hull)
        {
            Assert.Equal(-8f, v.Z, 3);
        }
    }

    [Fact]
    public void TheIndexFindsTheWalksPortalsInTheWalksOrder()
    {
        PortalFixture f = FloodedRoomWithAreaportal();
        Portal dividing = FindDividingPortal(f);
        AreaPortalIndex index = new(f.Tree.HeadNode);

        foreach ((int src, int dst) in new[] { (1, 2), (2, 1), (1, 1), (2, 2), (0, 1), (0, 0) })
        {
            List<Portal> walked = [];
            AreaPortalGeometry.FindPortalsLeadingToArea(f.Tree.HeadNode, f.Planes, src, dst, dividing.Plane, walked);
            List<Portal> indexed = [];
            index.FindPortalsLeadingToArea(f.Planes, src, dst, dividing.Plane, indexed);

            Assert.Equal(walked, indexed);
        }
    }

    [Fact]
    public void TheIndexAgreesWhenTheFrontAreaIsTheHigherOne()
    {
        // Renumber areas 1 and 2 the other way round, so the dividing portal's
        // FRONT leaf carries the higher area.
        PortalFixture f = FloodedRoomWithAreaportal();
        Portal dividing = FindDividingPortal(f);
        foreach (BspNode leaf in f.Leaves)
        {
            leaf.Area = leaf.Area switch { 1 => 2, 2 => 1, int a => a };
        }

        AreaPortalIndex index = new(f.Tree.HeadNode);
        foreach ((int src, int dst) in new[] { (1, 2), (2, 1) })
        {
            List<Portal> walked = [];
            AreaPortalGeometry.FindPortalsLeadingToArea(f.Tree.HeadNode, f.Planes, src, dst, dividing.Plane, walked);
            List<Portal> indexed = [];
            index.FindPortalsLeadingToArea(f.Planes, src, dst, dividing.Plane, indexed);

            Assert.NotEmpty(walked);
            Assert.Equal(walked, indexed);
        }
    }

    [Fact]
    public void TheWalkFindsTheDividingPortalOncePerOccupiedLeafItBounds()
    {
        // The walk visits a portal from each of its two leaves, and both of
        // this one's are occupied: stock adds it twice (portals.cpp:1172-1193).
        PortalFixture f = FloodedRoomWithAreaportal();
        Portal dividing = FindDividingPortal(f);
        List<Portal> indexed = [];

        new AreaPortalIndex(f.Tree.HeadNode).FindPortalsLeadingToArea(f.Planes, 1, 2, dividing.Plane, indexed);

        Assert.Equal(2, indexed.Count(p => ReferenceEquals(p, dividing)));
    }

    [Fact]
    public void TheClipHullIsTheSameWithTheIndex()
    {
        PortalFixture f = FloodedRoomWithAreaportal();
        Portal dividing = FindDividingPortal(f);

        IReadOnlyList<Vec3> walked = AreaPortalGeometry.ClipPortalGeometry(
            f.Tree.HeadNode, f.Planes, f.Arena, dividing, 1, 2);
        IReadOnlyList<Vec3> indexed = AreaPortalGeometry.ClipPortalGeometry(
            f.Tree.HeadNode, f.Planes, f.Arena, dividing, 1, 2, null, new AreaPortalIndex(f.Tree.HeadNode));

        Assert.Equal(walked, indexed);
    }

    private static PortalFixture FloodedRoomWithAreaportal()
    {
        PortalFixture f = PortalFixture.SealedRoomWithAreaportal();
        f.Add("light", new Vec3(0f, 0f, 20f));
        f.Portalise();
        EntityFlood.FloodEntities(f.Tree, f.Planes, f.Entities);
        EntityFlood.FillOutside(f.Tree.HeadNode);
        new AreaFlood(f.Entities).FloodAreas(f.Tree, f.Arena);
        return f;
    }

    private static Portal FindDividingPortal(PortalFixture f)
    {
        for (Portal? p = f.Slab.Portals; p is not null; p = p.NextAt(p.SideOf(f.Slab)))
        {
            // The slab also touches four solid walls, whose area is 0. The one
            // that matters is the one with a real area on both sides.
            if (p.OnNode is not null && p.FrontNode!.Area == 1 && p.BackNode!.Area == 2)
            {
                return p;
            }
        }

        Assert.Fail("the areaportal slab has no portal between two areas");
        return null!;
    }
}
