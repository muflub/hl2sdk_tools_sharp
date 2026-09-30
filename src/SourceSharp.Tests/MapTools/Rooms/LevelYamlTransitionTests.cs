//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Rooms;

using SourceSharp.RoomContracts;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The level file's transition keys (the rooms design, 11.1, 11.5 and open
/// points O20, O21, O23): read, refused where they are wrong, and written
/// back in their fixed place and order; a level without them reads and
/// writes as it always did.
/// </summary>
public sealed class LevelYamlTransitionTests
{
    private const string Head = "library: rooms.vmf\nrows: 2\ncolumns: 2\n";

    private const string Grid = "grid:\n  - [a, b]\n  - [c, ~]\n";

    /// <summary>Every key read into the level's transitions.</summary>
    [Fact]
    public void EveryTransitionKeyIsRead()
    {
        LevelGrid level = LevelYaml.Parse(Head + "up: none\ndown_map: deep_02\nspawn: [1, 0]\nspawn_count: 3\n" + Grid, "deep_01");
        Assert.Equal(new LevelTransitions { NoUp = true, DownMap = "deep_02", SpawnCell = (1, 0), SpawnCount = 3 }, level.Transitions);
        Assert.True(level.Transitions!.IsOff(TransitionDirection.Up));
        Assert.False(level.Transitions.IsOff(TransitionDirection.Down));
        Assert.Equal("deep_02", level.Transitions.MapOf(TransitionDirection.Down));
        Assert.Null(level.Transitions.MapOf(TransitionDirection.Up));

        LevelGrid other = LevelYaml.Parse(Head + "down: none\nup_map: top.v2\n" + Grid, "x");
        Assert.Equal(new LevelTransitions { NoDown = true, UpMap = "top.v2" }, other.Transitions);
    }

    /// <summary>A level without the keys has no transitions, as every level file written before them.</summary>
    [Fact]
    public void ALevelWithoutTheKeysHasNoTransitions() => Assert.Null(LevelYaml.Parse(Head + Grid, "l").Transitions);

    /// <summary>
    /// The keys are written between the size and the grid, in a fixed order,
    /// and read back to the same level; a level without them writes no line
    /// of them.
    /// </summary>
    [Fact]
    public void TheKeysAreWrittenInTheirPlaceAndReadBack()
    {
        LevelGrid level = LevelYaml.Parse(Head + "spawn_count: 2\nspawn: [0, 1]\ndown_map: b\nup_map: a\ndown: none\nup: none\n" + Grid, "l");
        string text = LevelYaml.Write(level);
        Assert.Equal(
            Head + "up: none\ndown: none\nup_map: a\ndown_map: b\nspawn: [0, 1]\nspawn_count: 2\ngrid:\n  - [a, b]\n  - [c, ~]\n",
            text);
        Assert.Equal(level.Transitions, LevelYaml.Parse(text, "l").Transitions);
        Assert.Equal(Head + Grid, LevelYaml.Write(level.WithTransitions(null)));
    }

    /// <summary>Every refusal of a transition key, at the node at fault.</summary>
    [Theory]
    [InlineData("up: yes\n", 4, 5, "up is \"yes\"; the only value it takes is none, which switches the role off.")]
    [InlineData("down: [none]\n", 4, 7, "down is a single value, not a sequence.")]
    [InlineData("up_map: \"a b\"\n", 4, 9, "up_map \"a b\" is not a map name: use letters, digits, _, - and . only.")]
    [InlineData("down_map: \"\"\n", 4, 11, "down_map \"\" is empty.")]
    [InlineData("down_map: ../x\n", 4, 11, "down_map \"../x\" is not a map name")]
    [InlineData("spawn: 3\n", 4, 8, "spawn is a cell, [column, row], each a whole number from 0")]
    [InlineData("spawn: [1]\n", 4, 8, "spawn is a cell, [column, row]")]
    [InlineData("spawn: [-1, 0]\n", 4, 8, "spawn is a cell, [column, row]")]
    [InlineData("spawn: [2, 0]\n", 4, 8, "spawn names cell (2, 0), which is off the 2x2 grid.")]
    [InlineData("spawn_count: 0\n", 4, 14, "spawn_count is \"0\"; it is a whole number, at least 1.")]
    public void ABadTransitionKeyIsRefusedWhereItIs(string key, int line, int column, string problem)
    {
        LevelFileException refused = Assert.Throws<LevelFileException>(() => LevelYaml.Parse(Head + key + Grid, "l"));
        Assert.Contains(problem, refused.Problem, StringComparison.Ordinal);
        Assert.Equal((line, column), (refused.Line, refused.Column));
    }

    /// <summary>An unknown key's refusal names the optional keys too.</summary>
    [Fact]
    public void AnUnknownKeyNamesEveryKey()
    {
        LevelFileException refused = Assert.Throws<LevelFileException>(() => LevelYaml.Parse("next_map: b\n", "l"));
        Assert.Equal(
            "unknown key \"next_map\"; a level has library (or libraries), rows, columns and grid, and may have aliases, up, down, up_map, down_map, spawn and spawn_count.",
            refused.Problem);
    }

    /// <summary>What a map name may be: the characters of a map file's name.</summary>
    [Theory]
    [InlineData("level_01", null)]
    [InlineData("A-b.c", null)]
    [InlineData(null, "is empty")]
    [InlineData("", "is empty")]
    [InlineData("a/b", "is not a map name: use letters, digits, _, - and . only")]
    [InlineData("a\"b", "is not a map name: use letters, digits, _, - and . only")]
    public void AMapNameIsAFileName(string? name, string? problem) => Assert.Equal(problem, LevelTransitions.MapNameProblem(name));
}
