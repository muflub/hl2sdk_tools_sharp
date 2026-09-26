//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp.Portals;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Portals;

/// <summary>The <c>.prt</c> file.</summary>
public class PortalFileBuilderTests
{
    [Fact]
    public void ASealedRoomIsOneClusterAndNoPortals()
    {
        PortalFile file = Build(Sealed(), out _);

        Assert.Equal(1, file.ClusterCount);
        Assert.Empty(file.Portals);
    }

    [Fact]
    public void SolidLeavesAreClusterMinusOne()
    {
        Build(Sealed(), out PortalFixture f);

        Assert.Equal(-1, f.Leaves[0].Cluster);
    }

    [Fact]
    public void SplittingNodesAreClusterMinusNinetyNine()
    {
        Build(Sealed(), out PortalFixture f);

        Assert.Equal(-99, f.Tree.HeadNode.Cluster);
    }

    [Fact]
    public void TheLeafClusterListIsInTreeOrderAndCoversEveryLeaf()
    {
        PortalFileBuilder builder = BuilderFor(Sealed(), out PortalFixture f);
        builder.Build(f.Tree);

        // Seven leaves: six solid walls and the room, with the room fourth.
        Assert.Equal([-1, -1, -1, 0, -1, -1, -1], builder.LastResult.LeafClusters);
    }

    [Fact]
    public void TheHeaderCarriesTheClusterCountAndThePortalCount()
    {
        PortalFile file = Build(Sealed(), out _);

        string text = System.Text.Encoding.Latin1.GetString(file.ToBytes(PortalLineEnding.Lf));

        Assert.StartsWith("PRT1\n1\n0\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAreaportalLeafIsStillAVisCluster()
    {
        // CONTENTS_AREAPORTAL is not CONTENTS_SOLID, so the slab is collected
        // like any other empty leaf and numbered.
        PortalFile file = Build(Areaportal(), out _);

        Assert.Equal(3, file.ClusterCount);
    }

    [Fact]
    public void VisFloodsStraightThroughAnAreaportal()
    {
        // 0x8000 is above LAST_VISIBLE_CONTENTS, so VisibleContents of the
        // difference is zero and Portal_VisFlood returns true on its first
        // test. The portals either side of the slab are both written.
        PortalFile file = Build(Areaportal(), out _);

        Assert.Equal(2, file.Portals.Count);
    }

    [Fact]
    public void EveryPortalIsWrittenOnceAndJoinsTwoDifferentClusters()
    {
        PortalFile file = Build(Areaportal(), out _);

        foreach (FilePortal portal in file.Portals)
        {
            Assert.NotEqual(portal.Cluster0, portal.Cluster1);
        }
    }

    [Fact]
    public void AForcedVisClusterMergesEveryLeafItCoversIntoOne()
    {
        PortalFixture f = Areaportal();
        PortalFileBuilder builder = new(f.Portals, f.Arena)
        {
            VisClusters = new EverythingInOne(),
        };

        PortalFile file = builder.Build(f.Tree);

        Assert.Equal(1, file.ClusterCount);
    }

    [Fact]
    public void AForcedVisClusterAlsoRemovesThePortalsInsideIt()
    {
        PortalFixture f = Areaportal();
        PortalFileBuilder builder = new(f.Portals, f.Arena)
        {
            VisClusters = new EverythingInOne(),
        };

        PortalFile file = builder.Build(f.Tree);

        // BuildPortalList skips a portal whose two leaves share a cluster.
        Assert.Empty(file.Portals);
    }

    [Fact]
    public void EverySkyboxLeafSharesOneCluster()
    {
        PortalFixture f = Areaportal();
        AreaFlood areas = new(f.Entities);
        areas.FloodAreas(f.Tree, f.Arena);

        PortalFileBuilder builder = new(f.Portals, f.Arena)
        {
            SkyAreas = [f.Leaves[3].Area, f.Leaves[5].Area],
        };

        builder.Build(f.Tree);

        Assert.True(builder.SkyCluster >= 0);
        Assert.Equal(builder.SkyCluster, f.Leaves[3].Cluster);
        Assert.Equal(builder.SkyCluster, f.Leaves[5].Cluster);
    }

    [Fact]
    public void SkyVisGivesEverySkyboxLeafItsOwnClusterAgain()
    {
        PortalFixture f = Areaportal();
        AreaFlood areas = new(f.Entities);
        areas.FloodAreas(f.Tree, f.Arena);

        PortalFileBuilder builder = new(f.Portals, f.Arena)
        {
            SkyAreas = [f.Leaves[3].Area, f.Leaves[5].Area],
            SkyVis = true,
        };

        PortalFile file = builder.Build(f.Tree);

        Assert.Equal(-1, builder.SkyCluster);
        Assert.Equal(3, file.ClusterCount);
    }

    [Fact]
    public void TheWrittenWindingIsThePortalsOwnPoints()
    {
        PortalFile file = Build(Areaportal(), out _);

        foreach (FilePortal portal in file.Portals)
        {
            Assert.Equal(4, portal.Points.Count);

            // Both portals lie in a z plane of the 64-unit room.
            foreach (Vec3 point in portal.Points)
            {
                Assert.InRange(point.X, -32f, 32f);
                Assert.InRange(point.Y, -32f, 32f);
            }
        }
    }

    [Fact]
    public void APortalWhoseWindingAgreesWithItsPlaneKeepsItsLeafOrder()
    {
        (PortalFileBuilder Builder, Portal Portal) c = Coplanar(new Vec3(0f, 0f, -1f));

        FilePortal written = c.Builder.ToFilePortal(c.Portal);

        Assert.Equal(7, written.Cluster0);
        Assert.Equal(9, written.Cluster1);
    }

    [Fact]
    public void APortalWhoseWindingDISAGREESWithItsPlaneHasItsLeafOrderFLIPPED()
    {
        //: "sometimes planes get turned around when they are
        // very near the changeover point between different axis. Interpret the
        // plane the same way vis will, and flip the side orders if needed."
        //
        // No catalogue map reaches this -- mutating the 0.99 threshold to 0
        // leaves all 17 still byte-identical -- so it is pinned here or
        // nowhere. The winding below computes a normal of -z, so a portal
        // claiming +z is the backwards case.
        (PortalFileBuilder Builder, Portal Portal) c = Coplanar(new Vec3(0f, 0f, 1f));

        FilePortal written = c.Builder.ToFilePortal(c.Portal);

        Assert.Equal(9, written.Cluster0);
        Assert.Equal(7, written.Cluster1);
    }

    private static (PortalFileBuilder Builder, Portal Portal) Coplanar(Vec3 planeNormal)
    {
        PortalFixture f = PortalFixture.SealedRoom();

        Portal portal = new(0)
        {
            Plane = new Plane(planeNormal, 0f),
            FrontNode = new BspNode(1) { Cluster = 7 },
            BackNode = new BspNode(2) { Cluster = 9 },
            Winding = f.Arena.Create(
                [new Vec3(0f, 0f, 0f), new Vec3(1f, 0f, 0f), new Vec3(1f, 1f, 0f), new Vec3(0f, 1f, 0f)]),
        };

        return (new PortalFileBuilder(f.Portals, f.Arena), portal);
    }

    private static PortalFixture Sealed()
    {
        PortalFixture f = PortalFixture.SealedRoom();
        f.Add("light", new Vec3(0f, 0f, 8f));
        f.Portalise();
        EntityFlood.FloodEntities(f.Tree, f.Planes, f.Entities);
        EntityFlood.FillOutside(f.Tree.HeadNode);
        return f;
    }

    private static PortalFixture Areaportal()
    {
        PortalFixture f = PortalFixture.SealedRoomWithAreaportal();
        f.Add("light", new Vec3(0f, 0f, 20f));
        f.Portalise();
        EntityFlood.FloodEntities(f.Tree, f.Planes, f.Entities);
        EntityFlood.FillOutside(f.Tree.HeadNode);
        return f;
    }

    private static PortalFileBuilder BuilderFor(PortalFixture f, out PortalFixture fixture)
    {
        fixture = f;
        return new PortalFileBuilder(f.Portals, f.Arena);
    }

    private static PortalFile Build(PortalFixture f, out PortalFixture fixture)
    {
        fixture = f;
        return new PortalFileBuilder(f.Portals, f.Arena).Build(f.Tree);
    }

    private sealed class EverythingInOne : IVisClusterResolver
    {
        public int GetVisCluster(IBspNode leaf) => 0;
    }
}
