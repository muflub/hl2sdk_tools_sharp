//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Rooms;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// The traversal data the link bakes and the reader serves, on hand-built
/// kit rooms: floor heights and the steps between them (at exactly the step
/// height and just past it), jump links (at exactly the jump distance and one
/// column past it), ladders, water, costs, and dynamic obstacles, including
/// a door that is also a socket.
/// </summary>
public sealed class NavTraversalTests
{
    private static readonly RoomDefinition East = RoomHarness.WalkableRoom("east", RoomFacing.PositiveX);

    private static readonly RoomDefinition West = RoomHarness.WalkableRoom("west", RoomFacing.NegativeX);

    private static Nav3dReader Alone(RoomDefinition definition, NavGeometry geometry, NavSettings? settings = null) =>
        NavTestRooms.Link((definition, NavTestRooms.Nav(definition, geometry, settings), 0, 0, 0));

    /// <summary>The walkable leaf standing in a column (the lowest grounded one), for the player class.</summary>
    private static int Standing(Nav3dReader nav, int cell, int x, int y)
    {
        for (int z = 0; z < nav.CellVoxels; z++)
        {
            int leaf = nav.FindLeaf(cell, x, y, z);
            if (leaf >= 0 && nav.Leaf(leaf).IsWalkable(Nav3dClipClass.Player))
            {
                return leaf;
            }
        }

        return -1;
    }

    private static bool Linked(Nav3dReader nav, int a, int b) =>
        nav.Jumps(a).ToArray().Any(j => (nav.Jump(j).LeafA, nav.Jump(j).LeafB) == ((uint)Math.Min(a, b), (uint)Math.Max(a, b)));

    /// <summary>
    /// A step whose top is exactly the step height above the floor is walked:
    /// the reader's step-up is 18, within the limit, and no jump link is
    /// baked. Just past it (18 plus a float's ulp), the step-up exceeds the
    /// limit and a one-column jump link joins the two floors.
    /// </summary>
    [Theory]
    [InlineData(34f, false)]
    [InlineData(34.00001f, true)]
    public void AStepOfExactlyTheStepHeightIsAWalkAndPastItAJump(float top, bool jump)
    {
        NavBrush step = NavBrush.Box(new Vec3(128, 16, 16), new Vec3(240, 240, top), 1);
        Nav3dReader nav = Alone(East, NavTestRooms.Geometry(East, [step]));
        int low = Standing(nav, 0, 7, 8);
        int high = Standing(nav, 0, 8, 8);
        Assert.Equal(16f, nav.Leaf(low).FloorZ(Nav3dClipClass.Player));
        Assert.Equal(top, nav.Leaf(high).FloorZ(Nav3dClipClass.Player));
        Assert.Equal(3, nav.Leaf(high).ZLo);
        float rise = nav.StepUp(low, high, Nav3dClipClass.Player);
        Assert.Equal(top - 16f, rise);
        Assert.Equal(!jump, rise <= nav.StepHeight);
        Assert.Equal(jump, Linked(nav, low, high));
        if (jump)
        {
            Nav3dJump link = nav.Jump(nav.Jumps(low).ToArray().Single(j => nav.Jump(j).LeafB == high || nav.Jump(j).LeafA == high));
            Assert.Equal((1, 3), (link.Columns, (int)link.ClassMask));
            Assert.Equal(low < high ? rise : -rise, link.Rise);
            Assert.Equal(low < high ? Nav3dDirection.East : Nav3dDirection.West, link.Direction);
        }
    }

    [Fact]
    public void ALedgeAboveTheJumpHeightHasNoLinkAndOneBelowItDoes()
    {
        NavBrush ledge = NavBrush.Box(new Vec3(128, 16, 16), new Vec3(240, 240, 72), 1);
        Nav3dReader nav = Alone(East, NavTestRooms.Geometry(East, [ledge]));
        Assert.True(Linked(nav, Standing(nav, 0, 7, 8), Standing(nav, 0, 8, 8)));

        NavBrush cliff = NavBrush.Box(new Vec3(128, 16, 16), new Vec3(240, 240, 72.01f), 1);
        nav = Alone(East, NavTestRooms.Geometry(East, [cliff]));
        Assert.False(Linked(nav, Standing(nav, 0, 7, 8), Standing(nav, 0, 8, 8)));
    }

    /// <summary>
    /// Two platforms 84 above a floor, a pit between them: a jump link joins
    /// their edges when the columns are at most the jump distance apart
    /// (96 = six columns here), and none at seven; the pit's floor is too far
    /// down to walk or jump to.
    /// </summary>
    [Theory]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public void AGapAtTheJumpDistanceIsLinkedAndOneColumnFartherIsNot(int farStart, bool linked)
    {
        NavBrush left = NavBrush.Box(new Vec3(16, 16, 16), new Vec3(80, 240, 100), 1);
        NavBrush right = NavBrush.Box(new Vec3(farStart * 16, 16, 16), new Vec3(240, 240, 100), 1);
        Nav3dReader nav = Alone(East, NavTestRooms.Geometry(East, [left, right]), NavSettings.Default with { JumpDistance = 96 });
        int edge = Standing(nav, 0, 4, 8);
        int landing = Standing(nav, 0, farStart, 8);
        Assert.Equal((100f, 100f), (nav.Leaf(edge).FloorZ(Nav3dClipClass.Player), nav.Leaf(landing).FloorZ(Nav3dClipClass.Player)));
        Assert.Equal(linked, Linked(nav, edge, landing));
        if (linked)
        {
            Nav3dJump jump = nav.Jump(nav.Jumps(edge).ToArray().Single(j => nav.Jump(j).LeafB == landing));
            Assert.Equal((6, 0f, Nav3dDirection.East), (jump.Columns, jump.Rise, jump.Direction));
        }

        // Nothing links a platform to the pit's floor: 84 down is more than the jump height.
        int pit = Standing(nav, 0, 6, 8);
        Assert.DoesNotContain(nav.Jumps(pit).ToArray(), j => nav.Jump(j).LeafA == edge || nav.Jump(j).LeafB == edge);
    }

    [Fact]
    public void AJumpNeedsTheSpaceBetweenOpenAtTheHigherFloor()
    {
        NavBrush left = NavBrush.Box(new Vec3(16, 16, 16), new Vec3(80, 240, 100), 1);
        NavBrush right = NavBrush.Box(new Vec3(128, 16, 16), new Vec3(240, 240, 100), 1);
        NavBrush beam = NavBrush.Box(new Vec3(96, 16, 100), new Vec3(112, 240, 124), 1);
        Nav3dReader open = Alone(East, NavTestRooms.Geometry(East, [left, right]));
        Nav3dReader blocked = Alone(East, NavTestRooms.Geometry(East, [left, right, beam]));
        Assert.True(Linked(open, Standing(open, 0, 4, 8), Standing(open, 0, 8, 8)));
        Assert.False(Linked(blocked, Standing(blocked, 0, 4, 8), Standing(blocked, 0, 8, 8)));
    }

    [Fact]
    public void LaddersAndWaterAreFlagsWithTheirCostsAndStayOpenSpace()
    {
        NavBrush water = NavBrush.Box(new Vec3(16, 16, 16), new Vec3(128, 240, 60), 0x20);
        Box ladder = new(new Vec3(200, 100, 16), new Vec3(216, 116, 200));
        Nav3dReader nav = Alone(East, NavTestRooms.Geometry(East, [water], ladders: [ladder]));
        int wet = nav.FindLeaf(0, 3, 3, 1);
        Assert.Equal(Nav3dLeafFlags.Water, nav.Leaf(wet).Flags & (Nav3dLeafFlags.Water | Nav3dLeafFlags.Ladder));
        Assert.Equal(2f, nav.Leaf(wet).CostMultiplier);
        Assert.True(nav.Passable(wet, 1, 32, 72, Nav3dClipClass.Player));
        Assert.True(nav.Standable(wet, 1, 32, 72, Nav3dClipClass.Player));
        int dry = nav.FindLeaf(0, 3, 3, 5);
        Assert.Equal(Nav3dLeafFlags.None, nav.Leaf(dry).Flags & Nav3dLeafFlags.Water);
        int climb = nav.FindLeaf(0, 12, 6, 6);
        Assert.Equal(Nav3dLeafFlags.Ladder, nav.Leaf(climb).Flags & (Nav3dLeafFlags.Water | Nav3dLeafFlags.Ladder));
        Assert.Equal(1.5f, nav.Leaf(climb).CostMultiplier);
        Assert.Equal(1f, nav.Leaf(nav.FindLeaf(0, 8, 12, 6)).CostMultiplier);

        // The weights are the library's: water that costs 4 is baked as 4.
        Nav3dReader heavy = Alone(East, NavTestRooms.Geometry(East, [water]), NavSettings.Default with { WaterCost = 4 });
        Assert.Equal(4f, heavy.Leaf(heavy.FindLeaf(0, 3, 3, 1)).CostMultiplier);
    }

    /// <summary>
    /// A door is open space in the grid, tagged: the leaves it could block
    /// (inside it, and beside it for an agent wide enough to reach it) name it,
    /// and the runtime's blocking state decides.
    /// </summary>
    [Fact]
    public void ADoorIsOpenSpaceItsLeavesAreTaggedAndTheRuntimeBlocksThem()
    {
        NavObstacleSource door = new("func_door", "cxry_gate", 17, Nav3dObstacleKind.Door,
            [NavBrush.Box(new Vec3(128, 16, 16), new Vec3(144, 240, 240), 1)]);
        Nav3dReader nav = Alone(East, NavTestRooms.Geometry(East, obstacles: [door]));
        int inside = nav.FindLeaf(0, 8, 8, 1);
        int beside = nav.FindLeaf(0, 10, 8, 1);
        int far = nav.FindLeaf(0, 2, 8, 1);
        Assert.True(nav.Passable(inside, 1, 32, 72, Nav3dClipClass.Player));
        Assert.False(nav.Passable(inside, 1, 0, 0, Nav3dClipClass.Player, [true]));
        Assert.True(nav.Passable(beside, 1, 32, 72, Nav3dClipClass.Player, [true]));
        Assert.False(nav.Passable(beside, 1, 64, 72, Nav3dClipClass.Player, [true]));
        Assert.True(nav.Passable(beside, 1, 64, 72, Nav3dClipClass.Player, [false]));
        Assert.Contains(inside, nav.ObstacleLeaves(0).ToArray());
        Assert.Contains(beside, nav.ObstacleLeaves(0).ToArray());
        Assert.DoesNotContain(far, nav.ObstacleLeaves(0).ToArray());
        Assert.Equal("c0r0_gate", nav.Obstacle(0).Name);

        // With the door shut the room's two halves are apart for the runtime;
        // the stored components (every obstacle open) join them.
        Assert.Equal(1, nav.ComponentCount(0));
    }

    /// <summary>
    /// A door hung in a socket's own doorway: while the socket is joined the
    /// doorway is open space tagged with the door, and the two rooms connect
    /// through it; capped, the plug fills the doorway, dominates the door, and
    /// no doorway leaf is left to tag.
    /// </summary>
    [Fact]
    public void ADoorThatIsAlsoASocketIsTaggedWhenJoinedAndGoneWhenCapped()
    {
        Box plug = RoomLinter.SealBox(East, East.Sockets[0], 256);
        NavObstacleSource door = new("prop_door_rotating", "cxry_door", 3, Nav3dObstacleKind.Door, [NavBrush.Box(plug.Mins, plug.Maxs, 1)]);
        RoomNav east = NavTestRooms.Nav(East, NavTestRooms.Geometry(East, obstacles: [door]));
        RoomNav west = NavTestRooms.Nav(West, NavTestRooms.Geometry(West));
        Nav3dReader joined = NavTestRooms.Link((East, east, 0, 0, 0), (West, west, 1, 0, 0));
        int doorway = joined.FindLeaf(0, 15, 8, 1);
        Assert.True(doorway >= 0);
        Assert.Contains(doorway, joined.ObstacleLeaves(0).ToArray());
        Assert.True(joined.Passable(doorway, 1, 32, 72, Nav3dClipClass.Player));
        Assert.False(joined.Passable(doorway, 1, 32, 72, Nav3dClipClass.Player, [true]));
        Assert.Equal(joined.Component(0, doorway), joined.Component(0, joined.FindLeaf(1, 8, 8, 1)));

        Nav3dReader capped = NavTestRooms.Link((East, east, 0, 0, 0));
        Assert.Equal(-1, capped.FindLeaf(0, 15, 8, 1));
        Assert.Empty(capped.ObstacleLeaves(0).ToArray());
    }
}
