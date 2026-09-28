//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// A level grid, its implicit joints, and the reachability rule every level
/// must pass: a player can walk from any room to any other.
/// </summary>
public sealed class LevelGridTests
{
    private static readonly SocketKit Kit = RoomHarness.WalkableKit;

    private static RoomDefinition Hub => RoomHarness.WalkableRoom(
        "hub", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);

    private static RoomDefinition End => RoomHarness.WalkableRoom("end", RoomFacing.PositiveX);

    private static RoomDefinition? Library(string name) => name switch
    {
        "hub" => Hub,
        "end" => End,
        _ => null,
    };

    // ---- the grid ----------------------------------------------------------------------

    [Fact]
    public void AGridHoldsRowsTimesColumnsCells()
    {
        Assert.Throws<ArgumentException>(() => new LevelGrid("l", "x", 2, 2, new LevelCell?[3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LevelGrid("l", "x", 0, 2, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LevelGrid("l", "x", 1, 0, []));
        Assert.Throws<ArgumentException>(() => new LevelGrid("l", "x", 1, 1, [new LevelCell("hub", 4)]));
        Assert.Throws<ArgumentNullException>(() => new LevelGrid("l", "x", 1, 1, null!));

        LevelGrid grid = new("l", "lib.vmf", 2, 3, [new LevelCell("a", 0), null, null, null, null, new LevelCell("b", 3)]);
        Assert.Equal("a", grid[0, 0]!.Room);
        Assert.Equal("b", grid[2, 1]!.Room);
        Assert.Null(grid[1, 0]);
        Assert.Equal([(0, 0), (2, 1)], grid.Placed.Select(p => (p.X, p.Y)));
        Assert.Equal(6, grid.Cells.Count);
    }

    // ---- implicit joints ---------------------------------------------------------------

    /// <summary>
    /// Two sockets that face each other across a shared wall are joined; a
    /// socket facing the grid's edge, an empty cell, or a neighbour's plain
    /// wall is capped. The end room's one socket, turned, decides which.
    /// </summary>
    [Fact]
    public void SocketsThatFaceEachOtherAreJoinedAndTheRestCapped()
    {
        // Row y = 0: end facing east | hub | end turned to face west.
        // Row y = 1: empty | end facing south, onto the hub | empty.
        LevelGrid grid = new("l", "x", 2, 3,
        [
            new LevelCell("end", 0), new LevelCell("hub", 0), new LevelCell("end", 2),
            null, new LevelCell("end", 3), null,
        ]);

        LevelLayout layout = grid.ToLayout(Library, 256, Kit);

        Assert.Equal("l", layout.Name);
        Assert.Equal(4, layout.Rooms.Count);
        RoomInstance west = layout.Rooms[0], hub = layout.Rooms[1], east = layout.Rooms[2], north = layout.Rooms[3];
        Assert.Equal([("east", "west")], west.Joints);
        Assert.Equal([("east", "east")], east.Joints);
        Assert.Equal([("east", "north")], north.Joints);
        Assert.Equal([("east", "east"), ("west", "east"), ("north", "east")], hub.Joints);
        Assert.Equal(["south"], hub.Capped);
        Assert.Equal(new RoomPlacement("end", 1, 1, 3), north.Placement);

        // Turn the north end to face east instead: its socket now faces the
        // empty cell beside it and is capped, and so is the hub's north
        // socket, which faces the end's plain south wall.
        LevelGrid away = new("l", "x", 2, 3,
        [
            new LevelCell("end", 0), new LevelCell("hub", 0), new LevelCell("end", 2),
            null, new LevelCell("end", 0), null,
        ]);
        LevelLayout capped = away.ToLayout(Library, 256, Kit);
        Assert.Empty(capped.Rooms[3].Joints);
        Assert.Equal(["east"], capped.Rooms[3].Capped);
        Assert.Contains("north", capped.Rooms[1].Capped);
    }

    /// <summary>A socket facing a neighbour's plain wall is capped, not joined.</summary>
    [Fact]
    public void ASocketFacingAPlainWallIsCapped()
    {
        LevelGrid grid = new("l", "x", 1, 2, [new LevelCell("end", 0), new LevelCell("end", 0)]);
        LevelLayout layout = grid.ToLayout(Library, 256, Kit);
        Assert.All(layout.Rooms, r => Assert.Empty(r.Joints));
        Assert.All(layout.Rooms, r => Assert.Equal(["east"], r.Capped));
    }

    /// <summary>A room the library lacks is refused, with where the level file placed it when it was read from one.</summary>
    [Fact]
    public void ARoomTheLibraryLacksIsRefusedWhereItIs()
    {
        LevelGrid read = LevelYaml.Parse(RoomHarness.LevelText("x", "hub, attic"), "l");
        LinkException refused = Assert.Throws<LinkException>(() => read.ToLayout(Library, 256, Kit));
        Assert.Equal("line 5, column 11: the level places room \"attic\", which is not in the room library.", refused.Message);

        LevelGrid built = new("l", "x", 1, 1, [new LevelCell("attic", 0)]);
        Assert.Equal(
            "the level places room \"attic\", which is not in the room library.",
            Assert.Throws<LinkException>(() => built.ToLayout(Library, 256, Kit)).Message);

        // A neighbour the library lacks is refused too, from the room that looks at it.
        LevelGrid neighbour = new("l", "x", 1, 2, [new LevelCell("hub", 0), new LevelCell("attic", 0)]);
        Assert.Contains("\"attic\"", Assert.Throws<LinkException>(() => neighbour.ToLayout(Library, 256, Kit)).Message, StringComparison.Ordinal);
    }

    // ---- reachability ----------------------------------------------------------------------

    /// <summary>An empty level, a one-room level and a joined level are all reachable.</summary>
    [Fact]
    public void JoinedLevelsAreReachable()
    {
        RoomLinter.CheckReachable(new LevelLayout("empty", 256, Kit, []), n => Library(n)!);
        RoomLinter.CheckReachable(Layout("hub"), n => Library(n)!);
        RoomLinter.CheckReachable(Layout("hub, hub", "hub, ~"), n => Library(n)!);
    }

    /// <summary>
    /// Rooms a player cannot reach are named with their cells: the rooms
    /// outside the largest joined group, whatever the group's size.
    /// </summary>
    [Theory]
    [InlineData(new[] { "hub, ~, hub" }, "room \"hub\" at cell (2, 0) is not joined to the other 1 room(s)")]
    [InlineData(new[] { "hub, ~, hub, hub" }, "room \"hub\" at cell (0, 0) is not joined to the other 2 room(s)")]
    [InlineData(new[] { "hub, ~, hub", "~, ~, ~", "hub, ~, hub" }, "room \"hub\" at cell (2, 0), room \"hub\" at cell (0, 2), room \"hub\" at cell (2, 2) are not joined to the other 1 room(s)")]
    [InlineData(new[] { "end, end" }, "room \"end\" at cell (1, 0) is not joined")]
    public void UnreachableRoomsAreNamed(string[] rows, string expected)
    {
        RoomLintException refused = Assert.Throws<RoomLintException>(() => RoomLinter.CheckReachable(Layout(rows), n => Library(n)!));
        Assert.StartsWith("rule 6 (EveryRoomReachable): a player cannot reach every room: ", refused.Message, StringComparison.Ordinal);
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A joint only reaches the room its own wall faces: a joint naming a
    /// socket the room does not have, or one facing a cell with no room,
    /// joins nothing (the linker refuses such joints before this rule runs).
    /// </summary>
    [Fact]
    public void AJointThatReachesNoRoomJoinsNothing()
    {
        LevelLayout layout = new("l", 256, Kit,
        [
            new RoomInstance(new RoomPlacement("hub", 0, 0, 0), [("nowhere", "west"), ("north", "south")], []),
            new RoomInstance(new RoomPlacement("hub", 1, 0, 0), [], []),
        ]);

        Assert.Contains(
            "room \"hub\" at cell (1, 0) is not joined",
            Assert.Throws<RoomLintException>(() => RoomLinter.CheckReachable(layout, n => Library(n)!)).Message,
            StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => RoomLinter.CheckReachable(null!, n => Library(n)!));
        Assert.Throws<ArgumentNullException>(() => RoomLinter.CheckReachable(layout, null!));
    }

    /// <summary>The layout of a level file of the given rows, north row first.</summary>
    private static LevelLayout Layout(params string[] rows) =>
        LevelYaml.Parse(RoomHarness.LevelText("x", rows), "l").ToLayout(Library, 256, Kit);
}
