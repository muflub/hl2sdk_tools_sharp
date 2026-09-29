//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Rooms;

using SourceSharp.RoomContracts;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.TransitHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The level rule and the spawn (the rooms design, 11.1 and 11.5), without a
/// compile: every refusal with its 15.4 text, when a level has transitions
/// at all, the landmarks' names and places, and the spawn points at every
/// quarter turn and for the top level.
/// </summary>
public sealed class LevelTransitionPlanTests
{
    private static readonly Vec3 Centre = new(128, 128, 48);

    private static RoomTransit UpData => new(
        RoomRole.Up, 103, Centre, -1, default, new TransitPoint(UpArrival, 0), [new TransitPoint(UpSpawn, 270)]);

    private static RoomTransit DownData => new(
        RoomRole.Down, 202, Centre, 201, new Vec3(128, 128, 72), new TransitPoint(DownArrival, 90), []);

    private static RoomTransit PlainData => new(RoomRole.None, -1, default, -1, default, null, [new TransitPoint(PlainSpawn, 45)]);

    private static RoomDefinition Definition(string name) => name switch
    {
        "up" => Up,
        "down" => Down,
        _ => RoomHarness.WalkableRoom(name, RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY),
    };

    private static RoomTransit? DataOf(string room) => room switch
    {
        "up" => UpData,
        "down" => DownData,
        "plain" => PlainData,
        _ => null,
    };

    private static LevelTransitionPlan? Plan(LevelGrid level, bool mod = false)
    {
        LevelLayout layout = level.ToLayout(Definition, RoomHarness.Cell, RoomHarness.WalkableKit);
        return LevelTransitionPlan.Make(layout, [.. layout.Rooms.Select(r => DataOf(r.Placement.Room))], Definition, mod);
    }

    private static string Refusal(LevelGrid level) => Assert.Throws<RoomLintException>(() => Plan(level)).Message;

    /// <summary>
    /// A level without transition keys and without a role room has no plan,
    /// so it links as it always did, even with rooms that have spawn points.
    /// </summary>
    [Fact]
    public void ALevelWithoutTransitionsHasNoPlan()
    {
        Assert.Null(Plan(Level("solo", string.Empty, "plain, other")));
        Assert.NotNull(Plan(Level("keys", "up: none\ndown: none\n", "plain, other")));
    }

    /// <summary>The count refusal, for none and for two of a role, naming the cells.</summary>
    [Fact]
    public void ALevelNeedsExactlyOneOfEachRole()
    {
        Assert.Equal(
            "level l: 0 up rooms (none); a level has exactly one unless it says \"up: none\".",
            Refusal(Level("l", "down_map: b\nup_map: a\n", "plain, down")));
        Assert.Equal(
            "level l: 2 down rooms ((1, 0), (0, 1)); a level has exactly one unless it says \"down: none\".",
            Refusal(Level("l", "down_map: b\nup_map: a\n", "down, other", "up, down")));
    }

    /// <summary>A role switched off must hold no room of that role, and name no map.</summary>
    [Fact]
    public void ARoleSwitchedOffHoldsNoRoomAndNoMap()
    {
        Assert.Equal(
            "level l: says \"up: none\" but places up room up at cell (0, 0).",
            Refusal(Level("l", "up: none\ndown_map: b\n", "up, down")));
        Assert.Equal(
            "level l: says \"down: none\" and also names down_map.",
            Refusal(Level("l", "down: none\ndown_map: b\nup_map: a\n", "up, plain")));
    }

    /// <summary>A role present needs its map, with the article its role takes.</summary>
    [Fact]
    public void ARolePresentNeedsItsMap()
    {
        Assert.Equal("level l: has an up room but no up_map.", Refusal(Level("l", "down_map: b\n", "up, down")));
        Assert.Equal("level l: has a down room but no down_map.", Refusal(Level("l", "up_map: a\n", "up, down")));

        // A role room placed in a level without any key makes it a level of a run too.
        Assert.Equal("level l: has an up room but no up_map.", Refusal(Level("l", string.Empty, "up, down")));
    }

    /// <summary>
    /// The landmarks: named upper level first, in the down room at the
    /// changelevel's centre (the folded hallway's), in the up room at its
    /// arrival; the maps from the keys; the volume centres turned; with the
    /// mod's classes no landmark and no fold.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void TheLandmarksAndMapsComeFromTheLevel(int rotation)
    {
        LevelTransitionPlan plan = Plan(Level("mid", "up_map: top\ndown_map: low\n", $"up@{rotation}, plain, down@{rotation}"))!;
        PlacementTransition up = plan.Placements[0]!;
        PlacementTransition down = plan.Placements[2]!;
        Assert.Null(plan.Placements[1]);
        Assert.Equal((TransitionDirection.Up, "top", 103, -1, "top__mid", At(0, 0, rotation, UpArrival)), (up.Direction, up.Map, up.VolumeId, up.FoldId, up.Landmark, up.LandmarkOrigin));
        Assert.Equal((TransitionDirection.Down, "low", 202, 201, "mid__low", At(2, 0, rotation, new Vec3(128, 128, 72))), (down.Direction, down.Map, down.VolumeId, down.FoldId, down.Landmark, down.LandmarkOrigin));
        Assert.Equal(At(0, 0, rotation, Centre), up.Centre);
        Assert.False(up.OmitsVolume(false));
        Assert.True(down.OmitsVolume(false));

        LevelTransitionPlan mod = Plan(Level("mid", "up_map: top\ndown_map: low\n", $"up@{rotation}, plain, down@{rotation}"), mod: true)!;
        Assert.Equal((-1, (string?)null), (mod.Placements[2]!.FoldId, mod.Placements[2]!.Landmark));
        Assert.True(mod.Placements[0]!.OmitsVolume(true));
        Assert.Empty(mod.Spawns);
    }

    /// <summary>The spawn is the up room's arrival then its spawn points, turned with the room, at every quarter turn.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void TheSpawnIsTheUpArrivalTurned(int rotation)
    {
        LevelTransitionPlan plan = Plan(Level("mid", "up_map: top\ndown_map: low\n", $"down, up@{rotation}"))!;
        Assert.Equal(
            [
                new LevelSpawnPoint(1, At(1, 0, rotation, UpArrival), $"0 {rotation} 0"),
                new LevelSpawnPoint(1, At(1, 0, rotation, UpSpawn), $"0 {(270 + rotation) % 360} 0"),
            ],
            plan.Spawns);
    }

    /// <summary>
    /// A top level spawns at the spawn points of the room farthest in doors
    /// from the down room (ties to the earlier in link order), or of its
    /// <c>spawn</c> cell's room.
    /// </summary>
    [Fact]
    public void ATopLevelSpawnsFarthestFromTheDownRoomOrAtItsCell()
    {
        LevelTransitionPlan far = Plan(Level("top", "up: none\ndown_map: b\n", "plain, down, other, plain"))!;
        Assert.Equal([new LevelSpawnPoint(3, At(3, 0, 0, PlainSpawn), "0 45 0")], far.Spawns);

        LevelTransitionPlan tie = Plan(Level("top", "up: none\ndown_map: b\n", "plain, down, plain@90"))!;
        Assert.Equal(0, Assert.Single(tie.Spawns).Placement);

        LevelTransitionPlan cell = Plan(Level("top", "up: none\ndown_map: b\nspawn: [2, 0]\n", "plain, down, plain@90"))!;
        Assert.Equal([new LevelSpawnPoint(2, At(2, 0, 90, PlainSpawn), "0 135 0")], cell.Spawns);

        // Without a down room, the first room with spawn points.
        LevelTransitionPlan alone = Plan(Level("one", "up: none\ndown: none\n", "other, plain@180"))!;
        Assert.Equal(1, Assert.Single(alone.Spawns).Placement);
    }

    /// <summary>The spawn refusals: no spawn point anywhere, a spawn cell without one, a spawn cell beside an up room, too few for spawn_count.</summary>
    [Fact]
    public void TheSpawnRefusals()
    {
        Assert.Equal(
            "level top: says \"up: none\" and no room has a spawn point; add an info_poi of type spawn or a spawn cell.",
            Refusal(Level("top", "up: none\ndown_map: b\n", "other, down")));
        Assert.Equal(
            "level top: spawn names cell (0, 0), which holds no room with a spawn point.",
            Refusal(Level("top", "up: none\ndown_map: b\nspawn: [0, 0]\n", "other, down, plain")));
        Assert.Equal(
            "level mid: names spawn cell (1, 0), but a level with an up room spawns at its arrival point.",
            Refusal(Level("mid", "up_map: a\ndown_map: b\nspawn: [1, 0]\n", "up, down")));
        Assert.Equal(
            "level mid: spawn_count 3, but up room up has 2 spawn points.",
            Refusal(Level("mid", "up_map: a\ndown_map: b\nspawn_count: 3\n", "up, down")));
        Assert.Equal(
            "level top: spawn_count 2, but spawn room plain has 1 spawn points.",
            Refusal(Level("top", "up: none\ndown_map: b\nspawn_count: 2\n", "plain, down")));
        Assert.NotNull(Plan(Level("mid", "up_map: a\ndown_map: b\nspawn_count: 2\n", "up, down")));
    }

    /// <summary>The door distances the spawn reads: breadth first over the level's joints.</summary>
    [Fact]
    public void DoorDistancesFollowTheJoints()
    {
        LevelGrid level = Level("d", string.Empty, "plain, other", "other, plain");
        LevelLayout layout = level.ToLayout(Definition, RoomHarness.Cell, RoomHarness.WalkableKit);
        Assert.Equal([0, 1, 1, 2], LevelTransitionPlan.DoorDistances(layout, Definition, 0));
    }

    /// <summary>A landmark's name is the upper level's map, two underscores, the lower's.</summary>
    [Fact]
    public void ALandmarkIsNamedForItsTwoLevels() => Assert.Equal("a_01__a_02", LevelTransitionPlan.Landmark("a_01", "a_02"));
}
