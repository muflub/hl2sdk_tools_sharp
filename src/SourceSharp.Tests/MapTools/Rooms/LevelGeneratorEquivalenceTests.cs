//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The rewritten generator (int candidates, an iterative fill, a cheaper
/// empty-cell pass) makes exactly what the old one made: every level and
/// every refusal, compared with a frozen copy of the old generator
/// (<see cref="LegacyLevelGenerator"/>) over many seeds, shapes and
/// libraries; and the pieces the rewrite replaced, each on its own.
/// </summary>
/// <remarks>
/// A seed names a level in files people keep (the sample's seeded levels,
/// and whatever a user wrote with <c>ssmap layout</c>), so the rewrite was
/// allowed to change what the generator costs and nothing else.
/// </remarks>
public sealed class LevelGeneratorEquivalenceTests
{
    private static readonly SocketKit Kit = new(96, 224, 16);

    private static RoomDefinition Kind(string name, params RoomFacing[] sockets) =>
        new(name, 256, Kit, [.. sockets.Select(f => new RoomSocket(f, RoomLibraryVmf.WallName(f)))]);

    /// <summary>The 3x3 sample's five kinds: four, three, two adjacent, two opposite and one socket.</summary>
    private static RoomDefinition[] Kinds() =>
    [
        Kind("cross", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY),
        Kind("tee", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY),
        Kind("corner", RoomFacing.PositiveX, RoomFacing.PositiveY),
        Kind("hall", RoomFacing.PositiveX, RoomFacing.NegativeX),
        Kind("end", RoomFacing.PositiveX),
    ];

    /// <summary>
    /// A library of <paramref name="count"/> rooms with seeded random socket
    /// sets, some of them empty, so the candidate list skips rooms and the
    /// room indices behind the candidates are not contiguous.
    /// </summary>
    private static RoomDefinition[] RandomLibrary(int count, ulong seed)
    {
        SplitMix64 random = new(seed);
        RoomFacing[] facings = [RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY];
        return
        [
            .. Enumerable.Range(0, count).Select(i =>
            {
                int bits = random.Next(16);
                return Kind(
                    string.Create(CultureInfo.InvariantCulture, $"r{i}"),
                    [.. facings.Where((_, b) => (bits & (1 << b)) != 0)]);
            }),
        ];
    }

    /// <summary>What a generator made: the level file, or the refusal.</summary>
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

    /// <summary>
    /// Every level and every refusal is the old generator's, over the sample
    /// kinds, random libraries with socketless rooms, and libraries that make
    /// the search backtrack hard or fail; on rows, columns, squares and
    /// oblongs from one cell to the 4,096-cell cap; with no, some, most and
    /// nearly all cells empty. Both outcomes are reached, so the comparison
    /// covers the refusals as well as the levels.
    /// </summary>
    [Fact]
    public void TheGeneratorMakesWhatTheOldOneMade()
    {
        RoomDefinition[] kinds = Kinds();
        (string Name, RoomDefinition[] Rooms)[] libraries =
        [
            ("kinds", kinds),
            ("random24", RandomLibrary(24, 7)),
            ("random6", RandomLibrary(6, 11)),
            ("hall+end", [kinds[3], kinds[4]]),
            ("corner", [kinds[2]]),
            ("tee+end", [kinds[1], kinds[4]]),
            ("end", [kinds[4]]),
        ];
        (int Rows, int Columns)[] shapes = [(1, 1), (1, 2), (2, 1), (1, 7), (6, 1), (2, 2), (3, 3), (4, 7), (7, 4), (8, 8), (12, 10)];
        double[] ratios = [0, 0.3, 0.6, 0.9];

        int levels = 0, refusals = 0;
        void Compare(RoomDefinition[] rooms, LevelGeneratorOptions options, string what)
        {
            string expected = Outcome(() => LegacyLevelGenerator.Generate(rooms, options, "l", "x"));
            string actual = Outcome(() => LevelGenerator.Generate(rooms, options, "l", "x"));
            Assert.True(expected == actual, $"{what} {options}: the rewritten generator differs from the old one");
            if (expected.StartsWith("refused: ", StringComparison.Ordinal))
            {
                refusals++;
            }
            else
            {
                levels++;
            }
        }

        foreach ((string libraryName, RoomDefinition[] rooms) in libraries)
        {
            foreach ((int rows, int columns) in shapes)
            {
                foreach (double ratio in ratios)
                {
                    for (ulong seed = 1; seed <= 5; seed++)
                    {
                        Compare(rooms, new LevelGeneratorOptions(rows, columns, seed, ratio), libraryName);
                    }
                }
            }
        }

        // The largest grids, where the old recursion was deepest, the
        // candidate orders largest and the empty-cell pass slowest.
        // The old empty-cell pass takes a minute and more to half empty a
        // 4,096-cell row (only its ends can go), so the row and the column
        // are compared full here, and their empty-cell pass on shorter lines
        // in TheEmptyCellsAreTheOldOnes.
        foreach ((int rows, int columns, double ratio) in new[]
        {
            (64, 64, 0), (64, 64, 0.5), (64, 64, 0.9), (32, 128, 0.5), (32, 128, 0.9),
            (1, 4096, 0), (4096, 1, 0), (1, 600, 0.5), (600, 1, 0.9),
        })
        {
            Compare(kinds, new LevelGeneratorOptions(rows, columns, 3, ratio), "kinds");
        }

        Compare(RandomLibrary(256, 5), new LevelGeneratorOptions(64, 64, 9, 0.2), "random256");

        Assert.True(levels > 1000, $"only {levels} levels were compared");
        Assert.True(refusals > 50, $"only {refusals} refusals were compared");
    }

    /// <summary>
    /// The empty-cell pass empties the same cells as the old one, which flood
    /// filled the whole grid per cell it tried, and leaves the sequence at the
    /// same draw, so everything drawn after it is the same too.
    /// </summary>
    [Fact]
    public void TheEmptyCellsAreTheOldOnes()
    {
        foreach ((int rows, int columns) in new[] { (1, 1), (1, 9), (9, 1), (3, 3), (5, 8), (16, 16), (1, 200), (200, 1), (64, 64) })
        {
            int cells = rows * columns;
            foreach (double ratio in new[] { 0, 0.1, 0.4, 0.7, 0.95, 0.999 })
            {
                for (ulong seed = 1; seed <= (cells > 1000 ? 2UL : 12UL); seed++)
                {
                    int empty = (int)Math.Floor(ratio * cells);
                    SplitMix64 oldRandom = new(seed), newRandom = new(seed);
                    bool[] expected = LegacyLevelGenerator.EmptyCells(oldRandom, rows, columns, empty);
                    bool[] actual = LevelGenerator.EmptyCells(newRandom, rows, columns, empty);
                    Assert.Equal(expected, actual);
                    Assert.Equal(oldRandom.NextUInt64(), newRandom.NextUInt64());
                }
            }
        }
    }

    /// <summary>
    /// Whether a cell can be emptied is the whole-grid flood fill's answer,
    /// for every cell of many connected sets: sets with loops (where a cell
    /// whose corners do not join its sides can still go) and thin trees
    /// (where it cannot). Asking about every cell of a set without emptying
    /// any finds the cut cells at most once.
    /// </summary>
    [Fact]
    public void CanEmptyIsTheFloodFillsAnswer()
    {
        int asked = 0, splits = 0, walks = 0;
        foreach ((int rows, int columns) in new[] { (1, 1), (1, 6), (5, 1), (4, 4), (7, 9), (12, 12) })
        {
            int cells = rows * columns;
            foreach (double ratio in new[] { 0, 0.2, 0.45, 0.7 })
            {
                for (ulong seed = 1; seed <= 8; seed++)
                {
                    bool[] pattern = LegacyLevelGenerator.EmptyCells(new SplitMix64(seed), rows, columns, (int)Math.Floor(ratio * cells));
                    LevelGenerator.OccupiedCells set = Build(pattern, rows, columns);
                    for (int cell = 0; cell < cells; cell++)
                    {
                        if (!pattern[cell])
                        {
                            continue;
                        }

                        pattern[cell] = false;
                        bool expected = FloodFillJoined(pattern, rows, columns);
                        pattern[cell] = true;
                        Assert.Equal(expected, set.CanEmpty(cell));
                        asked++;
                        splits += expected ? 0 : 1;
                    }

                    Assert.InRange(set.CutWalks, 0, 1);
                    walks += set.CutWalks;
                }
            }
        }

        Assert.True(asked > 1000 && splits > 100 && walks > 50, $"{asked} cells asked, {splits} splits, {walks} walks");
    }

    /// <summary>
    /// Emptying cells one after another, each only if it can go, agrees with
    /// the flood fill at every step: the cut cells are found again after each
    /// cell emptied (a leaf's neighbour can stop being a cut cell), and are
    /// kept while cells are only refused.
    /// </summary>
    [Fact]
    public void CanEmptyStaysRightAsCellsAreEmptied()
    {
        foreach ((int rows, int columns) in new[] { (1, 12), (6, 6), (9, 7) })
        {
            for (ulong seed = 1; seed <= 6; seed++)
            {
                int cells = rows * columns;
                bool[] pattern = new bool[cells];
                Array.Fill(pattern, true);
                LevelGenerator.OccupiedCells set = new(rows, columns);
                List<int> order = [.. Enumerable.Range(0, cells)];
                new SplitMix64(seed).Shuffle(order);
                for (int pass = 0; pass < 3; pass++)
                {
                    foreach (int cell in order)
                    {
                        if (!pattern[cell])
                        {
                            continue;
                        }

                        pattern[cell] = false;
                        bool expected = FloodFillJoined(pattern, rows, columns);
                        pattern[cell] = !expected;
                        int walks = set.CutWalks;
                        bool actual = set.CanEmpty(cell);
                        Assert.Equal(expected, actual);
                        if (actual)
                        {
                            set.Empty(cell);
                        }

                        Assert.Equal(pattern, set.Occupied);
                        Assert.InRange(set.CutWalks - walks, 0, 1);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The cases spelled out: the only cell (nothing would remain: not
    /// joined), a leaf (joined), two neighbours joined at a corner (joined,
    /// without finding the cut cells), a ring, whose cell's opposite
    /// neighbours are joined only the long way round (the cut cells say
    /// yes), and a bar, where they are not joined at all (the cut cells say
    /// no); in the ring a second question reuses the cut cells.
    /// </summary>
    [Fact]
    public void CanEmptyCoversEveryCase()
    {
        Assert.False(Check(["#"], 0, 0, out _));
        Assert.True(Check(["##"], 0, 0, out int walks));
        Assert.Equal(0, walks);

        // North and east of (0, 0) meet at (1, 1). Rows are listed south first.
        Assert.True(Check(["##", "##"], 0, 0, out walks));
        Assert.Equal(0, walks);

        // A bar: (1, 0) splits it.
        Assert.False(Check(["###"], 1, 0, out walks));
        Assert.Equal(1, walks);

        // A ring: neither (1, 0) nor (1, 2) splits it, and one walk answers both.
        LevelGenerator.OccupiedCells ring = Build(Pattern(["###", "#.#", "###"]), 3, 3);
        Assert.True(ring.CanEmpty(1));
        Assert.True(ring.CanEmpty(7));
        Assert.Equal(1, ring.CutWalks);

        // Emptying (1, 0) makes its old neighbours' sides a chain: (0, 1)
        // now joins (0, 0) to the rest, and the cut cells are found again.
        ring.Empty(1);
        Assert.False(ring.CanEmpty(3));
        Assert.Equal(2, ring.CutWalks);

        static bool Check(string[] southFirst, int x, int y, out int walks)
        {
            int rows = southFirst.Length, columns = southFirst[0].Length;
            bool[] pattern = Pattern(southFirst);
            LevelGenerator.OccupiedCells set = Build(pattern, rows, columns);
            int cell = (y * columns) + x;
            bool joined = set.CanEmpty(cell);
            pattern[cell] = false;
            Assert.Equal(FloodFillJoined(pattern, rows, columns), joined);
            walks = set.CutWalks;
            return joined;
        }
    }

    /// <summary>A pattern of rows, south first, <c>#</c> occupied.</summary>
    private static bool[] Pattern(string[] southFirst) =>
        [.. southFirst.SelectMany(row => row.Select(c => c == '#'))];

    /// <summary>The occupied-cell set of a pattern: every cell, then the pattern's empty ones emptied.</summary>
    private static LevelGenerator.OccupiedCells Build(bool[] pattern, int rows, int columns)
    {
        LevelGenerator.OccupiedCells set = new(rows, columns);
        for (int cell = 0; cell < pattern.Length; cell++)
        {
            if (!pattern[cell])
            {
                set.Empty(cell);
            }
        }

        return set;
    }

    /// <summary>
    /// The fill counts one step per candidate tried and fails at the first
    /// step past <see cref="LevelGenerator.StepBudget"/>, as the recursion
    /// did. A row of <paramref name="prefix"/> cells that fit at once, then a
    /// cell whose last candidate alone lets the final cell fit: each wrong
    /// candidate costs two steps (itself, and the final cell's one try), the
    /// right one two more. The search succeeds at exactly the budget and fails
    /// one step past it.
    /// </summary>
    [Theory]
    [InlineData(0, 100_000, true)] // 2 x 99,999 + 2 = 200,000 steps
    [InlineData(1, 99_999, true)] // 1 + 2 x 99,998 + 2 = 199,999 steps
    [InlineData(1, 100_000, false)] // 200,001 steps
    [InlineData(0, 100_001, false)] // 200,002 steps
    public void TheFillStopsAtTheFirstStepPastTheBudget(int prefix, int searched, bool fits)
    {
        const int East = 1;

        // Candidate 0 has an east socket, candidate 1 has none, and neither
        // has a west one: a cell fits beside a west neighbour only when that
        // neighbour has no east socket.
        int[] maskOf = [East, 0];
        int[] tries = new int[searched];
        tries[^1] = 1;
        int cells = prefix + 2;
        int[]?[] order = [.. Enumerable.Repeat<int[]?>([1], prefix), tries, [1]];
        bool[] occupied = new bool[cells];
        Array.Fill(occupied, true);
        int[] chosen = new int[cells];
        bool filled = LevelGenerator.Fill(occupied, order, new int[cells], maskOf, cells, chosen, new int[cells], new int[cells]);

        Assert.Equal(fits, filled);
        if (fits)
        {
            Assert.Equal(searched - 1, chosen[prefix]);
        }
    }

    /// <summary>
    /// The fill's other ends: a grid with no occupied cell is filled at once,
    /// a search that runs out of candidates on the first cell fails, and
    /// backtracking resumes a cell after the candidate it had, skipping empty
    /// cells both ways.
    /// </summary>
    [Fact]
    public void TheFillBacktracksInTheRecursionsOrder()
    {
        Assert.True(LevelGenerator.Fill([false, false], [null, null], new int[2], [0], 2, new int[2], new int[2], new int[2]));

        // One cell whose tree wall needs an east socket nobody has.
        Assert.False(LevelGenerator.Fill([true], [[0, 0]], [1], [0], 1, new int[1], new int[1], new int[1]));

        // A column of three (one column, so every neighbour is south or
        // north): cell 0 south, cell 2 north. Candidate masks: 0 = north,
        // 1 = none, 2 = south, 3 = north|south.
        const int North = 4, South = 8;
        int[] maskOf = [North, 0, South, North | South];

        // Cell 0 tries "none" then "north"; cell 1 tries "north|south" then
        // "south"; cell 2 tries "north" then "none". Cell 0 = none fits;
        // cell 1 must then have no south: neither of its candidates fits, so
        // back to cell 0, which takes "north"; cell 1 takes "north|south";
        // cell 2 must have a south: none of its candidates fits, so back to
        // cell 1, which takes "south" (no north); cell 2 must have no south:
        // "north" fits.
        int[]?[] order = [[1, 0], [3, 2], [0, 1]];
        int[] chosen = new int[3];
        Assert.True(LevelGenerator.Fill([true, true, true], order, new int[3], maskOf, 1, chosen, new int[3], new int[3]));
        Assert.Equal([1, 1, 0], chosen);

        // With the bottom cell empty the search starts at cell 1, which has
        // no placed neighbour: "none" fits, cell 2 needs a south socket
        // matched by none, so cell 1 moves on to "north" and cell 2 takes
        // "north|south".
        order = [null, [1, 0], [3, 2]];
        chosen = new int[3];
        Assert.True(LevelGenerator.Fill([false, true, true], order, new int[3], maskOf, 1, chosen, new int[3], new int[3]));
        Assert.Equal([0, 1, 0], chosen);

        // And when cell 1 runs out, backing up passes the empty cell below
        // it and finds nothing left to try.
        Assert.False(LevelGenerator.Fill([false, true, true], [null, [1], [3]], new int[3], maskOf, 1, new int[3], new int[3], new int[3]));
    }

    /// <summary>
    /// The step budget covers the largest grid there is: a level of
    /// <see cref="LevelYaml.MaxCells"/> cells whose first try always fits
    /// (every room is a cross) takes one step per cell and is made, on the
    /// square, the row and the column.
    /// </summary>
    [Fact]
    public void TheStepBudgetCoversTheLargestGrid()
    {
        Assert.Equal((uint)(LevelGenerator.StepBudget - LevelYaml.MaxCells), LevelGenerator.StepBudgetCoversTheLargestGrid);
        RoomDefinition cross = Kinds()[0];
        foreach ((int rows, int columns) in new[] { (64, 64), (1, LevelYaml.MaxCells), (LevelYaml.MaxCells, 1) })
        {
            LevelGrid level = LevelGenerator.Generate([cross], new LevelGeneratorOptions(rows, columns, 1), "l", "x");
            Assert.Equal(LevelYaml.MaxCells, level.Cells.Count(c => c is not null));
        }
    }

    /// <summary>The whole-grid flood fill the empty-cell pass used to run, as the reference.</summary>
    private static bool FloodFillJoined(bool[] occupied, int rows, int columns)
    {
        int start = Array.IndexOf(occupied, true);
        if (start < 0)
        {
            return false;
        }

        bool[] seen = new bool[occupied.Length];
        Stack<int> stack = new([start]);
        seen[start] = true;
        int reached = 1;
        while (stack.Count > 0)
        {
            int at = stack.Pop();
            int x = at % columns, y = at / columns;
            foreach ((int dx, int dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int nx = x + dx, ny = y + dy;
                int next = (ny * columns) + nx;
                if (nx >= 0 && ny >= 0 && nx < columns && ny < rows && occupied[next] && !seen[next])
                {
                    seen[next] = true;
                    reached++;
                    stack.Push(next);
                }
            }
        }

        return reached == occupied.Count(o => o);
    }
}
