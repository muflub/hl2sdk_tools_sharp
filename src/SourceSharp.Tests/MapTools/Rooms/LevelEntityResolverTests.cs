//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Rooms;
using SourceSharp.RoomContracts;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.RoomNamingFacts;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The naming resolver on levels built in memory: names resolved at every
/// turn, the three missing-neighbour mechanisms, the <c>logic_room</c> hub in
/// both emission modes, and the checks on resolved names (the design's
/// section 5.11).
/// </summary>
public class LevelEntityResolverTests
{
    public static TheoryData<int> Turns => [0, 1, 2, 3];

    /// <summary>The design's authored directions and their placeholders, in mask order.</summary>
    private static readonly (RoomDirection Direction, string Placeholder)[] EightWays =
    [
        (RoomDirection.East, "cx+1ry_"), (RoomDirection.North, "cxry+1_"), (RoomDirection.West, "cx-1ry_"), (RoomDirection.South, "cxry-1_"),
        (RoomDirection.NorthEast, "cx+1ry+1_"), (RoomDirection.NorthWest, "cx-1ry+1_"), (RoomDirection.SouthWest, "cx-1ry-1_"), (RoomDirection.SouthEast, "cx+1ry-1_"),
    ];

    /// <summary>The design's worked example: <c>cx+1ry_door</c> in the room at column 3, row 5, placed at 90°, is <c>c3r6_door</c>.</summary>
    [Fact]
    public void TheWorkedExampleResolves()
    {
        LevelResolution level = Resolve(false,
        [
            new Placed("hub", 3, 5, 1, Room(["classname", "func_button", "OnPressed", Out("cx+1ry_door", "Open")])),
            new Placed("north", 3, 6, 0, Room(["classname", "func_door", "targetname", "cxry_door"])),
        ], columns: 8, rows: 8);
        Assert.Equal("c3r6_door", Outputs(level.Entities[0], "OnPressed").Single().Target);
        Assert.NotNull(Named(level, "c3r6_door"));
        Assert.Empty(level.Warnings);
    }

    /// <summary>
    /// One room naming its neighbours in all eight directions, at the centre
    /// of a 3x3 grid at each turn, a distinct room in every other cell: each
    /// reference reaches the entity of the room behind the same authored
    /// wall, which at a turn is another level cell.
    /// </summary>
    [Theory]
    [MemberData(nameof(Turns))]
    public void EveryDirectionReachesTheRoomBehindItsAuthoredWall(int turn)
    {
        List<string> keys = ["classname", "logic_relay", "targetname", "cxry_hub"];
        foreach ((_, string placeholder) in EightWays)
        {
            keys.AddRange(["OnTrigger", Out(placeholder + "door", "Open")]);
        }

        List<Placed> placements = [new("hub", 1, 1, turn, Room([.. keys]))];
        for (int x = 0; x < 3; x++)
        {
            for (int y = 0; y < 3; y++)
            {
                if ((x, y) != (1, 1))
                {
                    placements.Add(new Placed($"n{x}{y}", x, y, 0, Room(["classname", "func_door", "targetname", "cxry_door", "note", $"{x}{y}"])));
                }
            }
        }

        LevelResolution level = Resolve(false, placements);
        List<RoomOutput> outputs = Outputs(Named(level, "c1r1_hub")!, "OnTrigger");
        for (int i = 0; i < EightWays.Length; i++)
        {
            (int dx, int dy) = RoomDirections.Offset(EightWays[i].Direction);
            (int tx, int ty) = turn switch
            {
                0 => (dx, dy),
                1 => (-dy, dx),
                2 => (-dx, -dy),
                _ => (dy, -dx),
            };

            string target = $"c{1 + tx}r{1 + ty}_door";
            Assert.Equal(target, outputs[i].Target);
            Assert.Equal($"{1 + tx}{1 + ty}", Named(level, target)!.Get("note"));
        }

        Assert.Empty(level.Warnings);
    }

    /// <summary>
    /// (a): the same room alone in a corner cell, at each turn: every
    /// reference to a cell with no room or off the grid is warned of by the
    /// design's text, its output removed or its key cleared, and the link
    /// goes on.
    /// </summary>
    [Theory]
    [MemberData(nameof(Turns))]
    public void AReferenceToAMissingNeighbourIsWarnedOfAndDropped(int turn)
    {
        LevelResolution level = Resolve(false,
        [
            new Placed("hub", 0, 0, turn, Room(
                ["classname", "logic_relay", "targetname", "cxry_hub", "OnTrigger", Out("cx+1ry_door", "Open"), "OnTrigger", Out("cxry_door", "Open")],
                ["classname", "func_door", "targetname", "cxry_door", "parentname", "cxry+1_arm"])),
        ]);

        LevelEntity hub = Named(level, "c0r0_hub")!;
        Assert.Equal(["c0r0_door"], Outputs(hub, "OnTrigger").Select(o => o.Target));
        LevelEntity door = Named(level, "c0r0_door")!;
        Assert.Null(door.Get("parentname"));

        (int ex, int ey) = turn switch { 0 => (1, 0), 1 => (0, 1), 2 => (-1, 0), _ => (0, -1) };
        (int nx, int ny) = turn switch { 0 => (0, 1), 1 => (-1, 0), 2 => (0, -1), _ => (1, 0) };
        string Where(int x, int y) => x < 0 || y < 0 ? "is off the grid" : "holds no room";
        Assert.Equal(
            [
                $"room hub at cell (0, 0): entity c0r0_hub (logic_relay) key \"OnTrigger\" names c{ex}r{ey}_door, but cell ({ex}, {ey}) {Where(ex, ey)}; the output was removed.",
                $"room hub at cell (0, 0): entity c0r0_door (func_door) key \"parentname\" names c{nx}r{ny}_arm, but cell ({nx}, {ny}) {Where(nx, ny)}; the key was cleared.",
            ],
            level.Warnings);
    }

    /// <summary>A reference past the grid's last column or row is off the grid too; with no grid given, only a negative cell is.</summary>
    [Fact]
    public void PastTheLastColumnIsOffTheGrid()
    {
        Placed hub = new("hub", 2, 2, 0, Room(["classname", "logic_relay", "hammerid", "4", "OnTrigger", Out("cx+1ry_door", "Open")]));
        Assert.Contains("cell (3, 2) is off the grid", Assert.Single(Resolve(false, [hub]).Warnings), StringComparison.Ordinal);
        Assert.Contains("entity 4 (logic_relay)", Assert.Single(Resolve(false, [hub]).Warnings), StringComparison.Ordinal);
        Assert.Contains("cell (3, 2) holds no room", Assert.Single(Resolve(false, [hub], columns: 4, rows: 4).Warnings), StringComparison.Ordinal);
    }

    /// <summary>
    /// (c) then (a): no warning for a reference inside an entity
    /// <c>room_needs</c> dropped (it is gone), and a reference to an entity it
    /// dropped goes quietly, reported only under verbose output.
    /// </summary>
    [Fact]
    public void NeedsDropsComeBeforeMissingNeighbours()
    {
        LevelResolution level = Resolve(false,
        [
            new Placed("hub", 0, 0, 0, Room(
                ["classname", "prop_dynamic", "targetname", "cxry_lamp", "room_needs", "east", "OnUser1", Out("cx+1ry_x", "Open")],
                ["classname", "logic_relay", "targetname", "cxry_r", "OnTrigger", Out("cxry_lamp", "Skin")])),
        ]);
        Assert.Empty(level.Warnings);
        Assert.Null(Named(level, "c0r0_lamp"));
        Assert.Empty(Outputs(Named(level, "c0r0_r")!, "OnTrigger"));
        Assert.Equal(
            ["room hub at cell (0, 0): entity c0r0_r (logic_relay) key \"OnTrigger\" names c0r0_lamp, which room_needs dropped; the output was removed."],
            level.Verbose);
    }

    /// <summary>
    /// (c) at every turn: an entity stays when its conditions hold in the
    /// placement (in the room's authored frame), goes otherwise, and the key
    /// never survives. Negation and joined sides included.
    /// </summary>
    [Theory]
    [MemberData(nameof(Turns))]
    public void NeedsKeepOrDropPerPlacement(int turn)
    {
        // The room's authored east is the level's direction at this turn.
        (int ex, int ey) = turn switch { 0 => (1, 0), 1 => (0, 1), 2 => (-1, 0), _ => (0, -1) };
        Func<int, List<LevelEntity>> hub = Room(
            ["classname", "prop_dynamic", "targetname", "a", "room_needs", "east"],
            ["classname", "prop_dynamic", "targetname", "b", "room_needs", "!east"],
            ["classname", "prop_dynamic", "targetname", "c", "room_needs", "joined_west, !joined_north"],
            ["classname", "prop_dynamic", "targetname", "d", "room_needs", "east,joined_north"]);

        LevelResolution with = Resolve(false,
            [new Placed("hub", 1, 1, turn, hub, JoinedMask.West), new Placed("next", 1 + ex, 1 + ey, 0, Room(["classname", "info_target"]))]);
        Assert.Equal(["a", "c"], with.Entities.Where(e => e.Placement == 0).Select(e => e.TargetName));
        Assert.All(with.Entities, e => Assert.Null(e.Get("room_needs")));

        LevelResolution without = Resolve(false, [new Placed("hub", 1, 1, turn, hub, JoinedMask.North)]);
        Assert.Equal(["b"], without.Entities.Select(e => e.TargetName));
    }

    /// <summary>
    /// (b) at every turn: a flag exists only where it is named, its initial
    /// value is whether that authored direction's cell holds a room (or that
    /// side is joined), cardinal and diagonal, and one named from a
    /// neighbour is that neighbour's flag.
    /// </summary>
    [Theory]
    [MemberData(nameof(Turns))]
    public void FlagsAreWrittenOnlyWhereNamedWithTheirValue(int turn)
    {
        (int ex, int ey) = turn switch { 0 => (1, 0), 1 => (0, 1), 2 => (-1, 0), _ => (0, -1) };

        // A listener keeps the flags from folding, so they can be looked at.
        Func<int, List<LevelEntity>> hub = Room(
            ["classname", "logic_branch_listener", "Branch01", "cxry_has_east", "Branch02", "cxry_has_west", "Branch03", "cxry_has_northeast",
                "Branch04", "cxry_joined_south", "Branch05", "cx+1ry_has_west"]);
        // The neighbour turned as the hub is, so its authored west faces the hub.
        LevelResolution level = Resolve(false,
        [
            new Placed("hub", 1, 1, turn, hub, JoinedMask.South),
            new Placed("east", 1 + ex, 1 + ey, turn, Room(["classname", "info_target"])),
        ]);

        string east = $"c{1 + ex}r{1 + ey}_";
        Assert.Equal("1", Named(level, "c1r1_has_east")!.Get("InitialValue"));
        Assert.Equal("0", Named(level, "c1r1_has_west")!.Get("InitialValue"));
        Assert.Equal("0", Named(level, "c1r1_has_northeast")!.Get("InitialValue"));
        Assert.Equal("1", Named(level, "c1r1_joined_south")!.Get("InitialValue"));

        // The east neighbour's own west flag: the hub stands there, whatever the neighbour's turn.
        LevelEntity theirs = Named(level, east + "has_west")!;
        Assert.Equal(1, theirs.Placement);
        Assert.Equal("1", theirs.Get("InitialValue"));
        Assert.Equal(("logic_branch", "1 1 0"), (theirs.ClassName, Named(level, "c1r1_has_east")!.Get("origin")));

        // Nothing else was written: four flags for the hub, one for its neighbour.
        Assert.Equal(7, level.Entities.Count);
    }

    /// <summary>A flag the room places itself gets its value set; its outputs stay its own, and with only tests it folds.</summary>
    [Fact]
    public void AnAuthoredFlagGetsItsValueAndFolds()
    {
        Func<int, List<LevelEntity>> hub = Room(
            ["classname", "logic_branch", "targetname", "cxry_has_north", "InitialValue", "1", "OnTrue", Out("lamp", "TurnOn"), "OnFalse", Out("lamp", "TurnOff")],
            ["classname", "func_button", "targetname", "b", "OnPressed", Out("cxry_has_north", "Test", "", "0.5")]);
        LevelResolution folded = Resolve(false, [new Placed("hub", 0, 0, 0, hub)]);
        Assert.Null(Named(folded, "c0r0_has_north"));
        RoomOutput inlined = Outputs(Named(folded, "b")!, "OnPressed").Single();
        Assert.Equal(("lamp", "TurnOff", "0.5"), (inlined.Target, inlined.Input, inlined.Delay));

        LevelResolution kept = Resolve(false, [new Placed("hub", 0, 0, 0, hub)], fold: false);
        Assert.Equal("0", Named(kept, "c0r0_has_north")!.Get("InitialValue"));
        Assert.Equal("c0r0_has_north", Outputs(Named(kept, "b")!, "OnPressed").Single().Target);
    }

    /// <summary>With <c>-mod-entities</c>: the hub is written with its keys filled from the level at every turn, and a hub that is only tested folds.</summary>
    [Theory]
    [MemberData(nameof(Turns))]
    public void TheHubIsFilledFromTheLevel(int turn)
    {
        (int ex, int ey) = turn switch { 0 => (1, 0), 1 => (0, 1), 2 => (-1, 0), _ => (0, -1) };
        Func<int, List<LevelEntity>> hub = Room(
            ["classname", "logic_room", "targetname", "cxry_room", "OnTrigger1", Out("lamp", "Toggle"), "OnEastTrue", Out("sign", "Show")],
            ["classname", "func_button", "targetname", "b", "OnPressed", Out("cxry_room", "Trigger1"), "OnPressed", Out("cxry_room", "TestEast")]);
        LevelResolution level = Resolve(true,
            [new Placed("hub", 1, 1, turn, hub, JoinedMask.East | JoinedMask.South), new Placed("n", 1 + ex, 1 + ey, 0, Room(["classname", "info_target"]))]);

        LevelEntity room = Named(level, "c1r1_room")!;
        Assert.Equal(
            ("1", "9", turn.ToString(System.Globalization.CultureInfo.InvariantCulture), "1", "1", "hub"),
            (room.Get("neighbours"), room.Get("joined"), room.Get("rotation"), room.Get("column"), room.Get("row"), room.Get("room")));
        Assert.Equal([(ModEntityContract.EntitiesKey, "mod"), (ModEntityContract.VersionKey, "1")], level.WorldKeys);
    }

    /// <summary>With <c>-mod-entities</c>, a hub with no channels that is only tested folds into its answers, and one nobody tests goes.</summary>
    [Fact]
    public void AHubThatIsOnlyTestedFolds()
    {
        Func<int, List<LevelEntity>> hub = Room(
            ["classname", "logic_room", "targetname", "cxry_room", "OnNorthTrue", Out("sign", "Show"), "OnNorthFalse", Out("sign", "Hide")],
            ["classname", "func_button", "targetname", "b", "OnPressed", Out("cxry_room", "TestNorth", "", "1", "3")]);
        LevelResolution level = Resolve(true, [new Placed("hub", 1, 1, 0, hub), new Placed("n", 1, 2, 0, Room(["classname", "info_target"]))]);
        Assert.Null(Named(level, "c1r1_room"));
        RoomOutput answer = Outputs(Named(level, "b")!, "OnPressed").Single();
        Assert.Equal(("sign", "Show", "1", "3"), (answer.Target, answer.Input, answer.Delay, answer.Times));

        Assert.Null(Named(Resolve(true, [new Placed("hub", 1, 1, 0, Room(["classname", "logic_room", "targetname", "cxry_room"]))]), "c1r1_room"));
    }

    /// <summary>
    /// Without <c>-mod-entities</c> the hub is expanded into stock entities:
    /// a branch per tested direction (its outputs the hub's), a relay per
    /// channel with the channel's flags, callers rewritten; nothing of class
    /// <c>logic_room</c> reaches the map and no worldspawn key is written.
    /// </summary>
    [Fact]
    public void WithoutTheFlagTheHubFallsBackToStockEntities()
    {
        Func<int, List<LevelEntity>> hub = Room(
            ["classname", "logic_room", "targetname", "cxry_room", "relayflags2", "3",
                "OnTrigger2", Out("lamp", "Toggle", "", "2"), "OnJoinedWestFalse", Out("wall", "Show")],
            ["classname", "func_button", "targetname", "b",
                "OnPressed", Out("cxry_room", "Trigger2"), "OnPressed", Out("cxry_room", "TestJoinedWest"), "OnPressed", Out("cxry_room", "Disable2")]);
        LevelResolution level = Resolve(false, [new Placed("hub", 1, 1, 0, hub)], fold: false);

        Assert.DoesNotContain(level.Entities, e => e.ClassName == LogicRoom.ClassName);
        Assert.Empty(level.WorldKeys);
        LevelEntity branch = Named(level, "c1r1_joined_west")!;
        Assert.Equal(("logic_branch", "0"), (branch.ClassName, branch.Get("InitialValue")));
        Assert.Equal("wall", Outputs(branch, "OnFalse").Single().Target);
        LevelEntity relay = Named(level, "c1r1_room_channel2")!;
        Assert.Equal(("logic_relay", "1", "1"), (relay.ClassName, relay.Get("spawnflags"), relay.Get("StartDisabled")));
        Assert.Equal("lamp", Outputs(relay, "OnTrigger").Single().Target);
        Assert.Equal(
            [("c1r1_room_channel2", "Trigger"), ("c1r1_joined_west", "Test"), ("c1r1_room_channel2", "Disable")],
            Outputs(Named(level, "b")!, "OnPressed").Select(o => (o.Target, o.Input)));

        // With the fold on, the constant branch folds away; the relay (fire once) stays.
        LevelResolution folded = Resolve(false, [new Placed("hub", 1, 1, 0, hub)]);
        Assert.Null(Named(folded, "c1r1_joined_west"));
        Assert.NotNull(Named(folded, "c1r1_room_channel2"));
    }

    /// <summary>
    /// With <c>-mod-entities</c>, flags and relays that did not fold merge into
    /// the placement's hub (written for them): callers rewritten to the hub's
    /// inputs, outputs moved to its outputs, a relay's settings kept as its
    /// channel's flags; at most eight relays.
    /// </summary>
    [Fact]
    public void WithTheFlagUnfoldedLogicMergesIntoTheHub()
    {
        List<string[]> entities =
        [
            ["classname", "logic_branch", "targetname", "cxry_has_east", "OnTrue", Out("a", "Open", "", "0", "1")],
            ["classname", "func_button", "targetname", "b", "OnPressed", Out("cxry_has_east", "Test")],
        ];
        for (int i = 0; i < 9; i++)
        {
            entities.Add(["classname", "logic_relay", "targetname", $"cxry_r{i}", "spawnflags", "1", "OnTrigger", Out("lamp", "Toggle")]);
            entities.Add(["classname", "trigger_once", "OnTrigger", Out($"cxry_r{i}", i == 0 ? "Enable" : "Trigger")]);
        }

        LevelResolution level = Resolve(true, [new Placed("hub", 1, 1, 0, Room([.. entities]))]);
        LevelEntity room = Named(level, "c1r1_room")!;
        Assert.Equal(("0", "0"), (room.Get("neighbours"), room.Get("joined")));
        Assert.Equal(("c1r1_room", "TestEast"), Outputs(Named(level, "b")!, "OnPressed").Select(o => (o.Target, o.Input)).Single());
        Assert.Equal("a", Outputs(room, "OnEastTrue").Single().Target);
        for (int channel = 1; channel <= 8; channel++)
        {
            Assert.Equal("lamp", Outputs(room, $"OnTrigger{channel}").Single().Target);
            Assert.Equal("2", room.Get($"relayflags{channel}"));
            Assert.Null(Named(level, $"c1r1_r{channel - 1}"));
        }

        Assert.Equal("Enable1", level.Entities.Where(e => e.ClassName == "trigger_once").Select(e => Outputs(e, "OnTrigger").Single().Input).First());
        Assert.NotNull(Named(level, "c1r1_r8"));
    }

    /// <summary>A trailing wildcard stays inside the prefix; special names and a bare wildcard are untouched.</summary>
    [Fact]
    public void WildcardsAndSpecialNamesAreKept()
    {
        LevelResolution level = Resolve(false,
        [
            new Placed("hub", 2, 1, 0, Room(
                ["classname", "func_button", "targetname", "b", "OnPressed", Out("cxry_door*", "Open"), "OnPressed", Out("!activator", "Kill"),
                    "OnPressed", Out("*", "Use"), "OnPressed", Out("!self", "SetParent", "cxry_door1")],
                ["classname", "func_door", "targetname", "cxry_door1"])),
        ]);
        Assert.Equal(
            [("c2r1_door*", ""), ("!activator", ""), ("*", ""), ("!self", "c2r1_door1")],
            Outputs(Named(level, "b")!, "OnPressed").Select(o => (o.Target, o.Parameter)));
    }

    /// <summary>A resolved value past what the engine reads is refused, naming entity and key; an output is measured whole.</summary>
    [Fact]
    public void AResolvedValueTooLongIsRefused()
    {
        // "c10r20_" is 7 bytes: a rest of 1016 resolves to 1023 (read whole), 1017 to 1024.
        string fits = new('x', 1016);
        Assert.NotNull(Named(Resolve(false, [new Placed("hub", 10, 20, 0, Room(["classname", "func_door", "targetname", "cxry_" + fits]))], columns: 30, rows: 30),
            "c10r20_" + fits));

        string rest = new('x', 1017);
        LinkException refused = Assert.Throws<LinkException>(() => Resolve(false,
            [new Placed("hub", 10, 20, 0, Room(["classname", "func_door", "targetname", "cxry_" + rest]))], columns: 30, rows: 30));
        Assert.Equal(
            $"room hub at cell (10, 20): entity c10r20_{rest} (func_door) key \"targetname\" resolves to 1024 bytes; the engine reads at most 1023.",
            refused.Message);

        // An output is measured whole: "c1r1_" and the target (1015), then ESC Open ESC ESC 0 ESC -1 (11).
        string target = new('y', 1010);
        LinkException output = Assert.Throws<LinkException>(() => Resolve(false,
            [new Placed("hub", 1, 1, 0, Room(["classname", "func_button", "targetname", "b", "OnPressed", Out("cxry_" + target, "Open", "", "0", "-1")]))]));
        Assert.Contains("entity b (func_button) key \"OnPressed\" resolves to 1026 bytes", output.Message, StringComparison.Ordinal);
    }

    /// <summary>The duplicate-global warning names the room, how many placements define the name, and their cells.</summary>
    [Fact]
    public void AGlobalNameInARepeatedRoomIsWarnedOf()
    {
        Func<int, List<LevelEntity>> hall = Room(["classname", "light", "targetname", "hall_lamp"], ["classname", "func_door", "targetname", "cxry_door"]);
        LevelResolution level = Resolve(false, [new Placed("hall", 0, 0, 0, hall), new Placed("hall", 2, 1, 1, hall), new Placed("end", 1, 1, 0, Room(["classname", "light", "targetname", "hall_lamp"]))]);
        Assert.Equal(["the global name \"hall_lamp\" is defined by 2 placements of room hall, at cells (0, 0), (2, 1)."], level.Warnings);

        // 15.3 fact 7: a room placed twice gives two distinct resolved names.
        Assert.NotNull(Named(level, "c0r0_door"));
        Assert.NotNull(Named(level, "c2r1_door"));
    }

    /// <summary>
    /// A resolved name that equals a global one is refused (the pack refuses
    /// such a global name; a room compiled before the rule could carry one).
    /// </summary>
    [Fact]
    public void AResolvedNameEqualToAGlobalOneIsRefused()
    {
        // The old room's table is empty (as a room compiled before the rule
        // would have none), so its reserved-looking global name gets through.
        List<LevelEntity> olds = [Ent(0, 0, "classname", "info_target", "targetname", "C1R0_door")];
        List<LevelEntity> news = [Ent(1, 0, "classname", "func_door", "targetname", "cxry_door")];
        LinkException refused = Assert.Throws<LinkException>(() => LevelEntityResolver.Resolve(
            [
                new ResolverRoom
                {
                    Room = "old", Column = 0, Row = 0, Turns = 0, Names = new RoomNameTurn(0, 1, [], [], [], []),
                    Entities = olds, Joined = JoinedMask.None, CellCentre = "0 0 0",
                },
                new ResolverRoom
                {
                    Room = "new", Column = 1, Row = 0, Turns = 0, Names = RoomNameAnalysis.Analyse("new", news, null)[0],
                    Entities = news, Joined = JoinedMask.None, CellCentre = "1 0 0",
                },
            ],
            new LevelNamingOptions(false, true, 2, 1)));
        Assert.Equal(
            "room new at cell (1, 0): its resolved name c1r0_door equals the global name of room old at cell (0, 0); a global name may not begin like a resolved one.",
            refused.Message);
    }

    /// <summary>A stored table that does not fit its room is refused naming the room and section, rather than resolving the wrong keys.</summary>
    [Fact]
    public void ATableThatDoesNotFitIsRefused()
    {
        List<LevelEntity> entities = [Ent(0, 0, "classname", "func_door", "targetname", "cxry_door")];
        RoomNameTurn names = RoomNameAnalysis.Analyse("hub", entities, null)[2];
        ResolverRoom room = new()
        {
            Room = "hub", Column = 0, Row = 0, Turns = 2, Names = names,
            Entities = [.. entities, Ent(0, 1, "classname", "info_target")], Joined = JoinedMask.None, CellCentre = "0 0 0",
        };
        Assert.Equal(
            "room pack entry \"hub\": its \"NAM2\" section holds a table for 1 entities; the room has 2.",
            Assert.Throws<LinkException>(() => LevelEntityResolver.Resolve([room], new LevelNamingOptions(false, true, 1, 1))).Message);

        ResolverRoom shorter = new()
        {
            Room = "hub", Column = 0, Row = 0, Turns = 2, Names = names,
            Entities = [Ent(0, 0, "classname", "func_door")], Joined = JoinedMask.None, CellCentre = "0 0 0",
        };
        Assert.Contains("a key 1 of entity 0, which has 1 keys",
            Assert.Throws<LinkException>(() => LevelEntityResolver.Resolve([shorter], new LevelNamingOptions(false, true, 1, 1))).Message, StringComparison.Ordinal);
    }

    /// <summary>Every class the resolver writes is stock, or a class the contract declares (only with the flag).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryWrittenClassIsStockOrTheContracts(bool mod)
    {
        Func<int, List<LevelEntity>> hub = Room(
            ["classname", "logic_room", "targetname", "cxry_room", "OnTrigger1", Out("x", "Use"), "OnEastTrue", Out("y", "Use")],
            ["classname", "logic_branch_listener", "Branch01", "cxry_has_north", "Branch02", "cx+1ry_joined_west"],
            ["classname", "func_button", "OnPressed", Out("cxry_room", "Toggle1"), "OnPressed", Out("cxry_room", "TestSouthWest")]);
        LevelResolution level = Resolve(mod, [new Placed("hub", 1, 1, 0, hub), new Placed("e", 2, 1, 0, Room(["classname", "info_target"]))], fold: false);
        HashSet<string> allowed = ["logic_branch", "logic_relay", "logic_branch_listener", "func_button", "info_target"];
        if (mod)
        {
            allowed.UnionWith(ModEntityContract.Classes.Select(c => c.ClassName));
        }

        Assert.All(level.Entities, e => Assert.Contains(e.ClassName, allowed));
        Assert.Equal(mod, level.Entities.Any(e => e.ClassName == LogicRoom.ClassName));
    }
}
