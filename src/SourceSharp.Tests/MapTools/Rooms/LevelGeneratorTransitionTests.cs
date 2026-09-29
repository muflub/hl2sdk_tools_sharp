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
/// The generator's transition rooms (the rooms design, 11.2 and 11.7):
/// exactly one up and one down room per level, at least the minimum doors
/// apart, over many seeds and grids; the roles switched off; the run of
/// levels with chained map names; the refusals; and a library without roles
/// getting the levels it always got.
/// </summary>
public sealed class LevelGeneratorTransitionTests
{
    private static readonly SocketKit Kit = new(96, 224, 16);

    private static RoomDefinition Kind(string name, params RoomFacing[] sockets) =>
        new(name, 256, Kit, [.. sockets.Select(f => new RoomSocket(f, RoomLibraryVmf.WallName(f)))]);

    private static readonly RoomFacing[] All = [RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY];

    /// <summary>The 3x3 sample's five kinds, then an up room and a down room of three and four sockets.</summary>
    private static RoomDefinition[] Library() =>
    [
        Kind("cross", All),
        Kind("tee", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY),
        Kind("corner", RoomFacing.PositiveX, RoomFacing.PositiveY),
        Kind("hall", RoomFacing.PositiveX, RoomFacing.NegativeX),
        Kind("end", RoomFacing.PositiveX),
        Kind("lift_up", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY),
        Kind("lift_down", All),
    ];

    private static RoomRole[] Roles => [RoomRole.None, RoomRole.None, RoomRole.None, RoomRole.None, RoomRole.None, RoomRole.Up, RoomRole.Down];

    /// <summary>Grids and empty shares the placement facts run over.</summary>
    public static TheoryData<int, int, double> Grids => new()
    {
        { 2, 2, 0 },
        { 3, 3, 0 },
        { 3, 3, 0.25 },
        { 4, 5, 0.2 },
        { 1, 6, 0 },
        { 6, 6, 0.3 },
    };

    private static List<(RoomRole Role, int X, int Y)> RoleCells(LevelGrid level) =>
        [.. level.Placed
            .Select(p => (Role: Roles[Array.FindIndex(Library(), d => d.Name == p.Cell.Room)], p.X, p.Y))
            .Where(p => p.Role != RoomRole.None)];

    private static int Distance(LevelGrid level)
    {
        RoomDefinition[] library = Library();
        LevelLayout layout = level.ToLayout(n => library.First(d => d.Name == n), 256, Kit);
        int up = layout.Rooms.ToList().FindIndex(r => r.Placement.Room == "lift_up");
        int down = layout.Rooms.ToList().FindIndex(r => r.Placement.Room == "lift_down");
        return LevelTransitionPlan.DoorDistances(layout, n => library.First(d => d.Name == n), up)[down];
    }

    /// <summary>
    /// Over forty seeds per grid: one up room and one down room, in different
    /// cells, at least two doors apart through the level's own joints, and
    /// every level valid (reachable, sockets matched) as the link reads it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Grids))]
    public void EachLevelPlacesOneUpAndOneDownRoomApart(int rows, int columns, double empty)
    {
        RoomDefinition[] library = Library();
        int distance = rows * columns >= 4 ? 2 : 1;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            LevelGrid level;
            try
            {
                level = LevelGenerator.Generate(
                    library, new LevelGeneratorOptions(rows, columns, seed, empty), "l", "rooms.vmf", null,
                    new LayoutTransitions(Roles) { MinDistance = distance });
            }
            catch (LinkException exception) when (exception.Message.StartsWith("layout: no level of", StringComparison.Ordinal))
            {
                continue;
            }

            List<(RoomRole Role, int X, int Y)> roles = RoleCells(level);
            Assert.Equal([RoomRole.Up, RoomRole.Down], roles.Select(r => r.Role).Order());
            Assert.True(Distance(level) >= distance, $"seed {seed}: {Distance(level)} doors");
            LevelLayout layout = level.ToLayout(n => library.First(d => d.Name == n), 256, Kit);
            RoomLinter.CheckReachable(layout, n => library.First(d => d.Name == n));
        }
    }

    /// <summary>Most seeds make a level: the second sequence and the role filter do not starve the fill.</summary>
    [Fact]
    public void MostSeedsMakeALevel()
    {
        int made = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            try
            {
                LevelGenerator.Generate(
                    Library(), new LevelGeneratorOptions(3, 3, seed), "l", "rooms.vmf", null, new LayoutTransitions(Roles) { MinDistance = 2 });
                made++;
            }
            catch (LinkException)
            {
            }
        }

        Assert.True(made >= 36, $"{made} of 40");
    }

    /// <summary>A role switched off is never placed, and neither is a role room anywhere else.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ARoleSwitchedOffIsNotPlaced(bool noUp, bool noDown)
    {
        for (ulong seed = 1; seed <= 20; seed++)
        {
            LevelGrid level = LevelGenerator.Generate(
                Library(), new LevelGeneratorOptions(3, 3, seed), "l", "rooms.vmf", null,
                new LayoutTransitions(Roles) { NoUp = noUp, NoDown = noDown, MinDistance = 3 });
            List<RoomRole> expected = [];
            if (!noUp)
            {
                expected.Add(RoomRole.Up);
            }

            if (!noDown)
            {
                expected.Add(RoomRole.Down);
            }

            Assert.Equal(expected, RoleCells(level).Select(r => r.Role).Order());
        }
    }

    /// <summary>
    /// A library without role rooms gets exactly the level it always got, over
    /// many seeds, grids and empty shares, whatever transition settings it is
    /// given: the second sequence is never made and the candidates are the
    /// fill's own.
    /// </summary>
    [Fact]
    public void ALibraryWithoutRolesGetsTheLevelsItAlwaysGot()
    {
        RoomDefinition[] library = Library()[..5];
        RoomRole[] none = [.. Enumerable.Repeat(RoomRole.None, 5)];
        foreach ((int rows, int columns, double empty) in new[] { (3, 3, 0.0), (3, 3, 0.25), (4, 6, 0.3), (1, 5, 0.0), (8, 8, 0.4) })
        {
            for (ulong seed = 0; seed < 25; seed++)
            {
                LevelGeneratorOptions options = new(rows, columns, seed, empty);
                string today = Outcome(() => LevelGenerator.Generate(library, options, "l", "rooms.vmf"));
                Assert.Equal(today, Outcome(() => LevelGenerator.Generate(library, options, "l", "rooms.vmf", null, new LayoutTransitions(none))));
                Assert.Equal(today, Outcome(() => LevelGenerator.Generate(
                    library, options, "l", "rooms.vmf", null, new LayoutTransitions(none) { NoUp = true, MinDistance = 4 })));
            }
        }
    }

    /// <summary>The same seed gives the same level with roles, run after run.</summary>
    [Fact]
    public void ALevelWithRolesIsAFunctionOfItsSeed()
    {
        for (ulong seed = 1; seed <= 10; seed++)
        {
            LevelGeneratorOptions options = new(4, 4, seed, 0.2);
            LayoutTransitions transitions = new(Roles) { MinDistance = 2 };
            Assert.Equal(
                Outcome(() => LevelGenerator.Generate(Library(), options, "l", "rooms.vmf", null, transitions)),
                Outcome(() => LevelGenerator.Generate(Library(), options, "l", "rooms.vmf", null, transitions)));
        }
    }

    /// <summary>The distance refusal, with the 15.4 text: a two-cell level cannot put its roles two doors apart.</summary>
    [Fact]
    public void ADistanceNoLevelCanKeepIsRefused()
    {
        LinkException refusal = Assert.Throws<LinkException>(() => LevelGenerator.Generate(
            Library(), new LevelGeneratorOptions(1, 2, 7), "l", "rooms.vmf", null, new LayoutTransitions(Roles) { MinDistance = 2 }));
        Assert.Equal("layout: no level of 1x2 with seed 7 places the up and down rooms at least 2 doors apart.", refusal.Message);
    }

    /// <summary>A role the level keeps but the library lacks, and a one-room level asked for both, are refused.</summary>
    [Fact]
    public void ARoleTheLibraryLacksIsRefused()
    {
        RoomRole[] upOnly = [.. Roles[..6], RoomRole.None];
        Assert.Equal(
            "the library has no down room with a socket, so a level of 9 rooms cannot hold one; switch the role off (down: none) or add one.",
            Assert.Throws<LinkException>(() => LevelGenerator.Generate(
                Library(), new LevelGeneratorOptions(3, 3, 1), "l", "rooms.vmf", null, new LayoutTransitions(upOnly))).Message);
        Assert.Equal(
            "a level of 1 room(s) cannot hold both an up and a down room; they stand in different cells.",
            Assert.Throws<LinkException>(() => LevelGenerator.Generate(
                Library(), new LevelGeneratorOptions(1, 1, 1), "l", "rooms.vmf", null, new LayoutTransitions(Roles))).Message);
        Assert.Throws<ArgumentException>(() => LevelGenerator.Generate(
            Library(), new LevelGeneratorOptions(3, 3, 1), "l", "rooms.vmf", null, new LayoutTransitions(Roles[..3])));
    }

    /// <summary>
    /// A run of three levels from consecutive seeds: named with the base and a
    /// two-digit number, each level the one its own seed makes with its roles,
    /// chained down and up by map name, the first <c>up: none</c> and the last
    /// <c>down: none</c>.
    /// </summary>
    [Fact]
    public void ASequenceChainsItsLevels()
    {
        LevelGeneratorOptions options = new(3, 3, 10);
        IReadOnlyList<LevelGrid> run = LevelGenerator.GenerateSequence(Library(), options, 3, "deep", "rooms.vmf", null, Roles, 2);
        Assert.Equal(["deep_01", "deep_02", "deep_03"], run.Select(l => l.Name));
        Assert.Equal(new LevelTransitions { NoUp = true, DownMap = "deep_02" }, run[0].Transitions);
        Assert.Equal(new LevelTransitions { UpMap = "deep_01", DownMap = "deep_03" }, run[1].Transitions);
        Assert.Equal(new LevelTransitions { NoDown = true, UpMap = "deep_02" }, run[2].Transitions);
        Assert.Equal([RoomRole.Down], RoleCells(run[0]).Select(r => r.Role));
        Assert.Equal([RoomRole.Up], RoleCells(run[2]).Select(r => r.Role));
        LevelGrid second = LevelGenerator.Generate(
            Library(), options with { Seed = 11 }, "deep_02", "rooms.vmf", null, new LayoutTransitions(Roles) { MinDistance = 2 });
        Assert.Equal(LevelYaml.Write(second), LevelYaml.Write(run[1].WithTransitions(null)));

        IReadOnlyList<LevelGrid> single = LevelGenerator.GenerateSequence(Library(), options, 1, "one", "rooms.vmf", null, Roles);
        Assert.Equal(new LevelTransitions { NoUp = true, NoDown = true }, Assert.Single(single).Transitions);
        Assert.Empty(RoleCells(single[0]));
    }

    /// <summary>A sequence level's number is padded to two digits, or to the widest.</summary>
    [Theory]
    [InlineData(1, 3, "run_01")]
    [InlineData(12, 12, "run_12")]
    [InlineData(7, 120, "run_007")]
    public void SequenceNamesArePadded(int index, int count, string name) =>
        Assert.Equal(name, LevelGenerator.SequenceName("run", index, count));

    private static string Outcome(Func<LevelGrid> generate)
    {
        try
        {
            return LevelYaml.Write(generate());
        }
        catch (LinkException exception)
        {
            return "refused: " + exception.Message;
        }
    }
}
