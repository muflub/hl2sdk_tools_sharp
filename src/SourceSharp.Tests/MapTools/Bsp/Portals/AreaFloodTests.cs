using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Portals;
using SourceSharp.MapTools.Diagnostics;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Portals;

/// <summary>Dividing the map into areas (<c>portals.cpp:819-1381</c>).</summary>
public class AreaFloodTests
{
    [Fact]
    public void APlainSealedRoomIsONEAreaAndTWOLumpEntries()
    {
        // EmitAreaPortals writes numareas = c_areas + 1, leaving index 0 as a
        // placeholder. Reasoning "one room, one area" gets the lump wrong.
        PortalFixture f = Flooded(PortalFixture.SealedRoom(), new Vec3(0f, 0f, 8f));
        AreaFlood areas = new(f.Entities);

        int found = areas.FloodAreas(f.Tree, f.Arena);

        // 1 here is 2 in LUMP_AREAS, which is the number the catalogue declares.
        Assert.Equal(1, found);
    }

    [Fact]
    public void TheRoomGetsAreaOne()
    {
        PortalFixture f = Flooded(PortalFixture.SealedRoom(), new Vec3(0f, 0f, 8f));
        AreaFlood areas = new(f.Entities);

        areas.FloodAreas(f.Tree, f.Arena);

        Assert.Equal(1, f.Room.Area);
    }

    [Fact]
    public void AnUnreachedLeafGetsNoArea()
    {
        PortalFixture f = PortalFixture.SealedRoom();
        f.Portalise();
        AreaFlood areas = new(f.Entities);

        // Nothing flooded, so nothing is reachable by an entity.
        Assert.Equal(0, areas.FloodAreas(f.Tree, f.Arena));
        Assert.Equal(0, f.Room.Area);
    }

    [Fact]
    public void AnAreaportalSplitsTheRoomIntoTwoAreasAndThreeLumpEntries()
    {
        PortalFixture f = Flooded(PortalFixture.SealedRoomWithAreaportal(), new Vec3(0f, 0f, 20f));
        AreaFlood areas = new(f.Entities);

        int found = areas.FloodAreas(f.Tree, f.Arena);

        // 2 here is 3 in LUMP_AREAS, which is what the catalogue's
        // l1_areaportal declares and what stock measured.
        Assert.Equal(2, found);
    }

    [Fact]
    public void TheEntityFloodItselfCrossesTheAreaportal()
    {
        // Only the AREA flood stops at an areaportal; the entity flood goes
        // straight through, which is why one light seals both halves.
        PortalFixture f = Flooded(PortalFixture.SealedRoomWithAreaportal(), new Vec3(0f, 0f, 20f));

        Assert.NotEqual(0, f.Leaves[5].Occupied);
    }

    [Fact]
    public void TheAreaportalEntityEndsUpBoundingBothAreas()
    {
        PortalFixture f = Flooded(PortalFixture.SealedRoomWithAreaportal(), new Vec3(0f, 0f, 20f));
        AreaFlood areas = new(f.Entities);

        areas.FloodAreas(f.Tree, f.Arena);
        MapEntity areaportal = f.Entities[1];

        Assert.Equal(1, areaportal.PortalAreas[0]);
        Assert.Equal(2, areaportal.PortalAreas[1]);
    }

    [Fact]
    public void TheAreaportalLeafTakesTheFirstAreaItTouched()
    {
        PortalFixture f = Flooded(PortalFixture.SealedRoomWithAreaportal(), new Vec3(0f, 0f, 20f));
        AreaFlood areas = new(f.Entities);

        areas.FloodAreas(f.Tree, f.Arena);

        Assert.Equal(1, f.Slab.Area);
    }

    [Fact]
    public void TheAreaportalRecordsThePortalEachAreaReachedItThrough()
    {
        PortalFixture f = Flooded(PortalFixture.SealedRoomWithAreaportal(), new Vec3(0f, 0f, 20f));
        AreaFlood areas = new(f.Entities);

        areas.FloodAreas(f.Tree, f.Arena);
        AreaPortalLink link = areas.Links[f.Entities[1]];

        Assert.NotNull(link.Into0);
        Assert.NotNull(link.Into1);
        Assert.NotSame(link.Into0, link.Into1);
    }

    [Fact]
    public void AnAreaportalThatOnlyEverTouchesOneAreaIsReported()
    {
        // Wall the lower half off so the slab only ever gets flooded into from
        // above.
        PortalFixture f = PortalFixture.SealedRoomWithAreaportal();
        f.Leaves[5].Contents = PortalContents.Solid;
        f.Add("light", new Vec3(0f, 0f, 20f));
        f.Portalise();
        EntityFlood.FloodEntities(f.Tree, f.Planes, f.Entities);

        AreaFlood areas = new(f.Entities);
        areas.FloodAreas(f.Tree, f.Arena);

        Assert.Contains(
            areas.Diagnostics,
            d => d.Code == AreaFlood.AreaportalTouchesOneArea);
    }

    [Fact]
    public void AreaportalBrushForNodeThrowsWhenTheLeafHasNoAreaportalBrush()
    {
        BspNode leaf = new(0) { Contents = PortalContents.AreaPortal };

        Assert.Throws<MapCompileException>(() => AreaFlood.AreaportalBrushForNode(leaf));
    }

    [Fact]
    public void ANodeWhoseChildrenAgreeOnAnAreaTakesIt()
    {
        BspNode a = new(0) { Area = 3 };
        BspNode b = new(1) { Area = 3 };
        BspNode node = new(2);
        node.SplitOn(0, a, b);

        AreaFlood.SetNodeAreaIndices(node);

        Assert.Equal(3, node.Area);
    }

    [Fact]
    public void ANodeWhoseChildrenDisagreeGetsMinusOne()
    {
        BspNode a = new(0) { Area = 3 };
        BspNode b = new(1) { Area = 4 };
        BspNode node = new(2);
        node.SplitOn(0, a, b);

        AreaFlood.SetNodeAreaIndices(node);

        Assert.Equal(-1, node.Area);
    }

    private static PortalFixture Flooded(PortalFixture f, Vec3 entityOrigin)
    {
        f.Add("light", entityOrigin);
        f.Portalise();
        EntityFlood.FloodEntities(f.Tree, f.Planes, f.Entities);
        return f;
    }
}
