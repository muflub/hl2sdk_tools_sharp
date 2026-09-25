using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Vis;

/// <summary>
/// <c>LoadPortals</c>'s in-memory result.
/// </summary>
public class PortalSetTests
{
    private static PortalFile TwoWindows() =>
        VisFixture.Portals(
            3,
            VisFixture.WindowAtX(0, 1, x: 0f, yMin: 0f, yMax: 16f),
            VisFixture.WindowAtX(1, 2, x: 64f, yMin: 0f, yMax: 16f));

    [Fact]
    public void EachFilePortalBecomesTwoMemoryPortals()
    {
        PortalSet set = PortalSet.FromPortalFile(TwoWindows());

        Assert.Equal(2, set.FilePortalCount);
        Assert.Equal(4, set.Count);
    }

    [Fact]
    public void TheForwardPortalLeadsToTheSecondCluster()
    {
        PortalSet set = PortalSet.FromPortalFile(TwoWindows());

        Assert.Equal(1, set.Leaf(0));
    }

    [Fact]
    public void TheBackwardPortalLeadsToTheFirstCluster()
    {
        PortalSet set = PortalSet.FromPortalFile(TwoWindows());

        Assert.Equal(0, set.Leaf(1));
    }

    [Fact]
    public void TheTwoHalvesOfOnePortalFaceOppositeWays()
    {
        PortalSet set = PortalSet.FromPortalFile(TwoWindows());

        Assert.Equal(set.Normal(0), VisClip.Negate(set.Normal(1)));
    }

    [Fact]
    public void TheBackwardPortalsWindingIsReversed()
    {
        PortalSet set = PortalSet.FromPortalFile(TwoWindows());

        ReadOnlySpan<Vec3> forward = set.Winding(0);
        ReadOnlySpan<Vec3> backward = set.Winding(1);

        Assert.Equal(forward.Length, backward.Length);
        for (int i = 0; i < forward.Length; i++)
        {
            Assert.Equal(forward[i], backward[forward.Length - 1 - i]);
        }
    }

    [Fact]
    public void AClustersPortalListIsInMemoryPortalOrder()
    {
        // Stock appends the forward portal then the backward one while walking
        // file portals in order, so cluster 1 -- which owns the backward half of
        // the first window and the forward half of the second -- holds 1 then 2.
        PortalSet set = PortalSet.FromPortalFile(TwoWindows());

        Assert.Equal([1, 2], set.ClusterPortals(1).ToArray());
    }

    [Fact]
    public void AClusterWithOnePortalListsOnlyIt()
    {
        PortalSet set = PortalSet.FromPortalFile(TwoWindows());

        Assert.Equal([0], set.ClusterPortals(0).ToArray());
        Assert.Equal([3], set.ClusterPortals(2).ToArray());
    }

    [Fact]
    public void TheSphereCentreIsTheMeanOfThePoints()
    {
        (Vec3 origin, _) = PortalSet.PortalSphere(
        [
            new Vec3(0f, 0f, 0f),
            new Vec3(4f, 0f, 0f),
            new Vec3(4f, 8f, 0f),
            new Vec3(0f, 8f, 0f),
        ]);

        Assert.Equal(new Vec3(2f, 4f, 0f), origin);
    }

    [Fact]
    public void TheSphereRadiusIsTheFarthestPoint()
    {
        (_, float radius) = PortalSet.PortalSphere(
        [
            new Vec3(-3f, -4f, 0f),
            new Vec3(3f, -4f, 0f),
            new Vec3(3f, 4f, 0f),
            new Vec3(-3f, 4f, 0f),
        ]);

        Assert.Equal(5f, radius);
    }

    [Fact]
    public void AnEmptyWindingHasNoSphere()
    {
        Assert.Throws<ArgumentException>(() => PortalSet.PortalSphere([]));
    }

    [Fact]
    public void APortalNamingAClusterPastTheEndIsRefused()
    {
        // The portal READER reproduces stock's off-by-one bound check, which
        // lets leafnum == clusterCount through to index one past the end of the
        // leaf array. Stock corrupts memory there; this refuses.
        PortalFile file = VisFixture.Portals(2, VisFixture.WindowAtX(0, 2, 0f, 0f, 16f));

        Assert.Throws<InvalidPortalFileException>(() => PortalSet.FromPortalFile(file));
    }
}
