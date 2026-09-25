using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Portals;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Portals;

/// <summary>
/// The entity flood and the fill that follows it.
/// </summary>
public class EntityFloodTests
{
    [Fact]
    public void ASealedRoomWithAnEntityInItDoesNotLeak()
    {
        PortalFixture f = Sealed();

        FloodResult flood = EntityFlood.FloodEntities(f.Tree, f.Planes, f.Entities);

        Assert.True(flood.Sealed);
    }

    [Fact]
    public void TheOccupiedLeafIsNumberedOne()
    {
        PortalFixture f = Sealed();

        EntityFlood.FloodEntities(f.Tree, f.Planes, f.Entities);

        Assert.Equal(1, f.Room.Occupied);
    }

    [Fact]
    public void ASolidLeafIsNeverNumbered()
    {
        PortalFixture f = Sealed();

        EntityFlood.FloodEntities(f.Tree, f.Planes, f.Entities);

        Assert.Equal(0, f.BeyondXHigh.Occupied);
    }

    [Fact]
    public void TheLeafHoldingTheEntityRemembersIt()
    {
        PortalFixture f = Sealed();

        EntityFlood.FloodEntities(f.Tree, f.Planes, f.Entities);

        Assert.Equal("light", f.Room.Occupant!.ValueForKey("classname"));
    }

    [Fact]
    public void AMapWithNoEntitiesIsNotSealedAndIsNotLeakedEither()
    {
        PortalFixture f = PortalFixture.SealedRoom();
        f.Portalise();

        FloodResult flood = EntityFlood.FloodEntities(f.Tree, f.Planes, f.Entities);

        Assert.False(flood.Inside);
        Assert.False(flood.ReachedOutside);
        Assert.False(flood.Sealed);
    }

    [Fact]
    public void AnEntityAtExactlyTheWorldOriginIsSkipped()
    {
        PortalFixture f = PortalFixture.SealedRoom();
        f.Add("light", Vec3.Zero);
        f.Portalise();

        FloodResult flood = EntityFlood.FloodEntities(f.Tree, f.Planes, f.Entities);

        Assert.False(flood.Inside);
    }

    [Fact]
    public void AnEntityInSolidDoesNotCountAsInside()
    {
        PortalFixture f = PortalFixture.SealedRoom();
        f.Add("light", new Vec3(48f, 0f, 0f));   // beyond the +x wall
        f.Portalise();

        FloodResult flood = EntityFlood.FloodEntities(f.Tree, f.Planes, f.Entities);

        Assert.False(flood.Inside);
    }

    [Fact]
    public void AnEntityOneUnitUnderTheFloorStillCountsBecauseOriginsAreRaised()
    {
        // "so objects on floor are ok": every origin gains one unit in z before
        // the leaf is found. z = -32 is the floor plane and would land in solid
        // without it; the plane test is d >= 0, so z = -32 exactly is the front
        // side, and -32.5 is not.
        PortalFixture f = PortalFixture.SealedRoom();
        f.Add("light", new Vec3(0f, 0f, -32.5f));
        f.Portalise();

        FloodResult flood = EntityFlood.FloodEntities(f.Tree, f.Planes, f.Entities);

        Assert.True(flood.Sealed);
    }

    [Fact]
    public void AnUnsealedRoomLetsTheFloodReachTheOutsideLeaf()
    {
        PortalFixture f = Leaky();

        FloodResult flood = EntityFlood.FloodEntities(f.Tree, f.Planes, f.Entities);

        Assert.True(flood.ReachedOutside);
        Assert.False(flood.Sealed);
    }

    [Fact]
    public void TheOutsideLeafIsOneHopFurtherThanTheLeafThatReachedIt()
    {
        PortalFixture f = Leaky();

        EntityFlood.FloodEntities(f.Tree, f.Planes, f.Entities);

        Assert.Equal(1, f.BeyondXHigh.Occupied);
        Assert.Equal(2, f.Tree.OutsideNode.Occupied);
    }

    [Fact]
    public void FillOutsideTurnsEveryUnreachedNonSolidLeafSolid()
    {
        // No flood at all, so nothing is reachable and the room itself is
        // filled away -- which is exactly what stock does to a map whose only
        // entities are in the void.
        PortalFixture f = PortalFixture.SealedRoom();
        f.Portalise();

        FloodResult fill = EntityFlood.FillOutside(f.Tree.HeadNode);

        Assert.Equal(PortalContents.Solid, f.Room.Contents);
        Assert.Equal(1, fill.FilledLeaves);
        Assert.Equal(6, fill.SolidLeaves);
        Assert.Equal(0, fill.InsideLeaves);
    }

    [Fact]
    public void FillOutsideCountsLeavesThatWereAlreadySolidSeparately()
    {
        PortalFixture f = Sealed();
        EntityFlood.FloodEntities(f.Tree, f.Planes, f.Entities);

        FloodResult fill = EntityFlood.FillOutside(f.Tree.HeadNode);

        Assert.Equal(6, fill.SolidLeaves);
        Assert.Equal(0, fill.FilledLeaves);
        Assert.Equal(1, fill.InsideLeaves);
    }

    [Fact]
    public void ClearingOccupiedResetsEveryLeaf()
    {
        PortalFixture f = Sealed();
        EntityFlood.FloodEntities(f.Tree, f.Planes, f.Entities);

        EntityFlood.ClearOccupied(f.Tree.HeadNode);

        Assert.Equal(0, f.Room.Occupied);
    }

    [Fact]
    public void APointExactlyOnASplitPlaneGoesToTheFrontSide()
    {
        // d >= 0 takes children[0]. x = 32 is the +x wall, so exactly on it is
        // the solid side, not the room.
        PortalFixture f = PortalFixture.SealedRoom();
        f.Portalise();

        IBspNode leaf = EntityFlood.LeafForPoint(f.Tree.HeadNode, f.Planes, new Vec3(32f, 0f, 0f));

        Assert.Same(f.BeyondXHigh, leaf);
    }

    [Fact]
    public void APointJustInsideThatPlaneIsTheRoom()
    {
        PortalFixture f = PortalFixture.SealedRoom();
        f.Portalise();

        IBspNode leaf = EntityFlood.LeafForPoint(f.Tree.HeadNode, f.Planes, new Vec3(31.9f, 0f, 0f));

        Assert.Same(f.Room, leaf);
    }

    private static PortalFixture Sealed()
    {
        PortalFixture f = PortalFixture.SealedRoom();
        f.Add("light", new Vec3(0f, 0f, 8f));
        f.Portalise();
        return f;
    }

    private static PortalFixture Leaky()
    {
        PortalFixture f = PortalFixture.SealedRoom();

        // Knock a hole in the +x wall by making that leaf empty, and put the
        // map's one entity in it. Nothing then stops the flood reaching the
        // box, one hop away.
        f.BeyondXHigh.Contents = 0;
        f.Add("light", new Vec3(48f, 0f, 0f));
        f.Portalise();
        return f;
    }
}
