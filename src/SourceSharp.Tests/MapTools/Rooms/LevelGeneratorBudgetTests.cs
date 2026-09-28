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
/// The layout's entity budget (<see cref="LayoutEntityBudget"/>): a budget
/// that never binds leaves every level byte for byte as it was, one that
/// binds gives a level within it from the same draws, and one no level can
/// meet is refused with its own message.
/// </summary>
public sealed class LevelGeneratorBudgetTests
{
    private static IReadOnlyList<RoomDefinition> Kinds { get; } =
    [
        Kind("cross", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY),
        Kind("tee", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY),
        Kind("corner", RoomFacing.PositiveX, RoomFacing.PositiveY),
        Kind("hall", RoomFacing.PositiveX, RoomFacing.NegativeX),
        Kind("end", RoomFacing.PositiveX),
    ];

    /// <summary>
    /// A budget no level reaches (the default of <c>cap − reserve</c> for
    /// rooms of a few entities) changes no level: the same cells for every
    /// grid, empty share and seed as the generator without a budget.
    /// </summary>
    [Fact]
    public void ABudgetThatNeverBindsChangesNoLevel()
    {
        LayoutEntityBudget budget = new(1536, [3, 2, 2, 1, 1]);
        foreach ((int rows, int columns, double empty) in new[] { (3, 3, 0.0), (4, 5, 0.25), (1, 6, 0.0), (6, 6, 0.4) })
        {
            for (ulong seed = 0; seed < 25; seed++)
            {
                LevelGeneratorOptions options = new(rows, columns, seed, empty);
                LevelGrid plain = LevelGenerator.Generate(Kinds, options, "l", "x");
                LevelGrid budgeted = LevelGenerator.Generate(Kinds, options, "l", "x", budget);
                Assert.Equal(LevelYaml.Write(plain, []), LevelYaml.Write(budgeted, []));
            }
        }
    }

    /// <summary>
    /// A budget that binds gives, for every seed, a level within it, the same
    /// one on every run; and for some seed a level other than the unbudgeted
    /// one, which passed the budget.
    /// </summary>
    [Fact]
    public void ABudgetThatBindsGivesALevelWithinIt()
    {
        int[] costs = [40, 10, 1, 1, 1];
        LayoutEntityBudget budget = new(60, costs);
        bool changed = false;
        for (ulong seed = 0; seed < 25; seed++)
        {
            LevelGeneratorOptions options = new(3, 3, seed);
            LevelGrid level = LevelGenerator.Generate(Kinds, options, "l", "x", budget);
            Assert.True(Edicts(level, costs) <= 60, $"seed {seed}: {Edicts(level, costs)} edicts");
            Assert.Equal(LevelYaml.Write(level, []), LevelYaml.Write(LevelGenerator.Generate(Kinds, options, "l", "x", budget), []));

            LevelGrid plain = LevelGenerator.Generate(Kinds, options, "l", "x");
            if (Edicts(plain, costs) > 60)
            {
                changed = true;
                Assert.NotEqual(LevelYaml.Write(plain, []), LevelYaml.Write(level, []));
            }
        }

        Assert.True(changed, "no seed's unbudgeted level passed the budget; the fact tests nothing");
    }

    /// <summary>A budget below what the cheapest rooms bring is refused before any search, with its own message.</summary>
    [Fact]
    public void ABudgetBelowTheCheapestLevelIsRefused()
    {
        LinkException refused = Assert.Throws<LinkException>(
            () => LevelGenerator.Generate(Kinds, new LevelGeneratorOptions(3, 3, 1), "l", "x", new LayoutEntityBudget(9, [2, 2, 1, 1, 1])));
        Assert.Equal(
            "no level of 3x3 cells keeps within the entity budget of 9 edicts: its 9 room(s) bring at least 10, the worldspawn included.",
            refused.Message);
    }

    /// <summary>
    /// A budget the cheapest rooms meet but no valid level does: a row of
    /// three can only be two ends and a cross, and the cross is too dear.
    /// The search fails with the message that names the budget; without the
    /// budget the level is made.
    /// </summary>
    [Fact]
    public void ABudgetNoValidLevelMeetsIsRefused()
    {
        RoomDefinition[] rooms = [Kinds[0], Kinds[4]];
        LevelGeneratorOptions options = new(1, 3, 7);
        Assert.Equal("cross", LevelGenerator.Generate(rooms, options, "l", "x").Cells[1]!.Room);

        LinkException refused = Assert.Throws<LinkException>(
            () => LevelGenerator.Generate(rooms, options, "l", "x", new LayoutEntityBudget(50, [100, 0])));
        Assert.Equal(
            $"no level of 1x3 cells with every room reachable and at most 50 edicts was found from the library's 2 room(s)"
            + $" with seed 7 after {LevelGenerator.Attempts} tries; the rooms' sockets or the entity budget may not allow one.",
            refused.Message);
        Assert.Equal("cross", LevelGenerator.Generate(rooms, options, "l", "x", new LayoutEntityBudget(101, [100, 0])).Cells[1]!.Room);
    }

    /// <summary>A budget below 0, or costs that are not one per room of 0 or more, are refused.</summary>
    [Fact]
    public void ABadBudgetIsRefused()
    {
        LevelGeneratorOptions options = new(2, 2, 1);
        Assert.Throws<ArgumentException>(() => LevelGenerator.Generate(Kinds, options, "l", "x", new LayoutEntityBudget(-1, [1, 1, 1, 1, 1])));
        Assert.Throws<ArgumentException>(() => LevelGenerator.Generate(Kinds, options, "l", "x", new LayoutEntityBudget(99, [1, 1])));
        Assert.Throws<ArgumentException>(() => LevelGenerator.Generate(Kinds, options, "l", "x", new LayoutEntityBudget(99, [1, 1, -1, 1, 1])));
        Assert.Throws<ArgumentException>(() => LevelGenerator.Generate(Kinds, options, "l", "x", new LayoutEntityBudget(99, null!)));
    }

    private static long Edicts(LevelGrid level, int[] costs) =>
        1 + level.Cells.Where(c => c is not null).Sum(c => costs[Kinds.ToList().FindIndex(k => k.Name == c!.Room)]);

    private static RoomDefinition Kind(string name, params RoomFacing[] sockets) =>
        new(name, 256, RoomHarness.WalkableKit, [.. sockets.Select(f => new RoomSocket(f, RoomLibraryVmf.WallName(f)))]);
}
