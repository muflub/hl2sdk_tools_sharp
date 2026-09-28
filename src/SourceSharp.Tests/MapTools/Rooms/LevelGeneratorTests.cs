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
/// The seeded sequence and the seeded level generator: the same seed and
/// library always give the same level, and every level it gives is one a
/// player can walk through end to end.
/// </summary>
public sealed class LevelGeneratorTests
{
    private static readonly SocketKit Kit = new(96, 224, 16);

    /// <summary>Five room kinds, as the 3x3 sample has: four, three, two adjacent, two opposite and one socket.</summary>
    private static IReadOnlyList<RoomDefinition> Kinds { get; } =
    [
        Kind("cross", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY),
        Kind("tee", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY),
        Kind("corner", RoomFacing.PositiveX, RoomFacing.PositiveY),
        Kind("hall", RoomFacing.PositiveX, RoomFacing.NegativeX),
        Kind("end", RoomFacing.PositiveX),
    ];

    private static RoomDefinition Kind(string name, params RoomFacing[] sockets) =>
        new(name, 256, Kit, [.. sockets.Select(f => new RoomSocket(f, RoomLibraryVmf.WallName(f)))]);

    // ---- SplitMix64 ------------------------------------------------------------------------

    /// <summary>
    /// The sequence is SplitMix64 exactly: the published first outputs for
    /// seed 0, and the value the 3x3 sample's own copy of it drew.
    /// </summary>
    [Fact]
    public void TheSequenceIsSplitMix64()
    {
        SplitMix64 zero = new(0);
        Assert.Equal(0xE220A8397B1DCDAFUL, zero.NextUInt64());
        Assert.Equal(0x6E789E6AA1B965F4UL, zero.NextUInt64());
        Assert.Equal(0x06C45D188009454FUL, zero.NextUInt64());

        SplitMix64 a = new(12345), b = new(12345);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(a.NextUInt64(), b.NextUInt64());
        }
    }

    /// <summary>
    /// A bounded draw stays below its bound and reaches every value; a bound
    /// that divides 2^64 draws without rejecting; a bound of one is always 0.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(1000)]
    [InlineData(int.MaxValue)]
    public void ABoundedDrawStaysInRange(int bound)
    {
        SplitMix64 random = new(7);
        HashSet<int> seen = [];
        for (int i = 0; i < 4000; i++)
        {
            int value = random.Next(bound);
            Assert.InRange(value, 0, bound - 1);
            seen.Add(value);
        }

        if (bound <= 8)
        {
            Assert.Equal(bound, seen.Count);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => random.Next(0));
    }

    /// <summary>
    /// The rejection that removes modulo bias really rejects: for a bound
    /// just over half of 2^64's reach the top raw values are drawn again, so
    /// the draw is not simply the raw value modulo the bound.
    /// </summary>
    [Fact]
    public void ABiasedTopIsDrawnAgain()
    {
        // The largest int bound, 2^31 - 1, leaves an excess of 2^64 mod
        // (2^31 - 1) = 4, so the limit is 2^64 - 4. Seed a sequence whose
        // first raw value is the very top, 2^64 - 1, by inverting the mix.
        ulong seed = Unmix(ulong.MaxValue) - 0x9E3779B97F4A7C15UL;
        SplitMix64 raw = new(seed), drawn = new(seed);
        Assert.Equal(ulong.MaxValue, raw.NextUInt64());
        ulong second = raw.NextUInt64();
        Assert.Equal((int)(second % int.MaxValue), drawn.Next(int.MaxValue));
    }

    [Fact]
    public void AShuffleIsAPermutationFixedByTheSeed()
    {
        List<int> a = [.. Enumerable.Range(0, 20)], b = [.. Enumerable.Range(0, 20)];
        new SplitMix64(3).Shuffle(a);
        new SplitMix64(3).Shuffle(b);
        Assert.Equal(a, b);
        Assert.Equal(Enumerable.Range(0, 20), a.Order());
        Assert.NotEqual(Enumerable.Range(0, 20), a);
        Assert.Throws<ArgumentNullException>(() => new SplitMix64(0).Shuffle<int>(null!));
    }

    // ---- the generator -------------------------------------------------------------------

    /// <summary>The same seed and library give the same level; another seed gives another.</summary>
    [Fact]
    public void TheSameSeedGivesTheSameLevel()
    {
        LevelGeneratorOptions options = new(4, 5, 99, 0.2);
        LevelGrid a = LevelGenerator.Generate(Kinds, options, "a", "rooms.vmf");
        LevelGrid b = LevelGenerator.Generate(Kinds, options, "a", "rooms.vmf");
        LevelGrid c = LevelGenerator.Generate(Kinds, options with { Seed = 100 }, "a", "rooms.vmf");

        Assert.Equal(LevelYaml.Write(a), LevelYaml.Write(b));
        Assert.NotEqual(LevelYaml.Write(a), LevelYaml.Write(c));
        Assert.Equal((4, 5, "a", "rooms.vmf"), (a.Rows, a.Columns, a.Name, a.Library));
        Assert.Equal(4, a.Cells.Count(cell => cell is null)); // floor(0.2 x 20)
    }

    /// <summary>
    /// The rule the owner set for every level, over many seeds and shapes,
    /// with and without empty cells: every generated level's sockets line up
    /// between rooms and a player can reach every room from every other
    /// (<see cref="RoomLinter.CheckReachable"/>), and its empty cells number
    /// exactly what the ratio asks for.
    /// </summary>
    [Fact]
    public void EveryGeneratedLevelIsConnectedAndLinesUp()
    {
        Dictionary<string, RoomDefinition> byName = Kinds.ToDictionary(k => k.Name);
        int levels = 0;
        foreach ((int rows, int columns) in new[] { (1, 1), (1, 2), (2, 2), (3, 3), (2, 5), (4, 4), (5, 3), (6, 6) })
        {
            foreach (double ratio in new[] { 0, 0.2, 0.4, 0.6 })
            {
                for (ulong seed = 1; seed <= 12; seed++)
                {
                    LevelGrid level = LevelGenerator.Generate(Kinds, new LevelGeneratorOptions(rows, columns, seed, ratio), "l", "x");
                    int cells = rows * columns;
                    Assert.Equal(Math.Min((int)Math.Floor(ratio * cells), cells - 1), level.Cells.Count(c => c is null));

                    LevelLayout layout = level.ToLayout(n => byName[n], 256, Kit);
                    RoomLinter.CheckReachable(layout, n => byName[n]);
                    AssertLinesUp(level, byName);
                    levels++;
                }
            }
        }

        Assert.Equal(8 * 4 * 12, levels);
    }

    /// <summary>A library whose rooms have no sockets can still fill one cell, but not two.</summary>
    [Fact]
    public void RoomsWithoutSocketsOnlyStandAlone()
    {
        RoomDefinition box = new("box", 256, Kit, []);
        LevelGrid one = LevelGenerator.Generate([box], new LevelGeneratorOptions(1, 1, 1), "l", "x");
        Assert.Equal("box", one[0, 0]!.Room);

        LinkException refused = Assert.Throws<LinkException>(
            () => LevelGenerator.Generate([box], new LevelGeneratorOptions(1, 2, 1), "l", "x"));
        Assert.Equal("none of the library's 1 room(s) has a socket, so 2 rooms cannot be joined.", refused.Message);
    }

    /// <summary>
    /// A library that cannot make the level gives up after its tries, naming
    /// the grid and the seed: one-socket rooms cannot join three in a row.
    /// </summary>
    [Fact]
    public void ALevelTheRoomsCannotMakeIsRefused()
    {
        RoomDefinition end = Kinds.Single(k => k.Name == "end");
        LinkException refused = Assert.Throws<LinkException>(
            () => LevelGenerator.Generate([end], new LevelGeneratorOptions(1, 3, 5), "l", "x"));
        Assert.StartsWith(
            "no level of 1x3 cells with every room reachable was found from the library's 1 room(s) with seed 5 after 64 tries",
            refused.Message,
            StringComparison.Ordinal);
    }

    /// <summary>Options out of range, and no rooms at all, are argument errors.</summary>
    [Fact]
    public void BadOptionsAreRefused()
    {
        Assert.Throws<ArgumentException>(() => LevelGenerator.Generate([], new LevelGeneratorOptions(1, 1, 1), "l", "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => LevelGenerator.Generate(Kinds, new LevelGeneratorOptions(0, 1, 1), "l", "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => LevelGenerator.Generate(Kinds, new LevelGeneratorOptions(1, 0, 1), "l", "x"));
        Assert.Throws<ArgumentException>(() => LevelGenerator.Generate(Kinds, new LevelGeneratorOptions(100, 100, 1), "l", "x"));
        Assert.Throws<ArgumentException>(() => LevelGenerator.Generate(Kinds, new LevelGeneratorOptions(2, 2, 1, 1.0), "l", "x"));
        Assert.Throws<ArgumentException>(() => LevelGenerator.Generate(Kinds, new LevelGeneratorOptions(2, 2, 1, -0.1), "l", "x"));
        Assert.Throws<ArgumentException>(() => LevelGenerator.Generate(Kinds, new LevelGeneratorOptions(2, 2, 1, double.NaN), "l", "x"));
        Assert.Throws<ArgumentNullException>(() => LevelGenerator.Generate(null!, new LevelGeneratorOptions(1, 1, 1), "l", "x"));
    }

    /// <summary>
    /// The options check a host runs before reading a library refuses what
    /// <see cref="LevelGenerator.Generate"/> refuses, with the same messages,
    /// and passes the largest grid and a share just under one.
    /// </summary>
    [Fact]
    public void TheOptionsAreCheckedWithoutALibrary()
    {
        LevelGenerator.CheckOptions(new LevelGeneratorOptions(64, 64, 1, 0.999));
        LevelGenerator.CheckOptions(new LevelGeneratorOptions(1, LevelYaml.MaxCells, 1));

        Assert.Throws<ArgumentNullException>(() => LevelGenerator.CheckOptions(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => LevelGenerator.CheckOptions(new LevelGeneratorOptions(0, 1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => LevelGenerator.CheckOptions(new LevelGeneratorOptions(1, 0, 1)));

        ArgumentException big = Assert.Throws<ArgumentException>(() => LevelGenerator.CheckOptions(new LevelGeneratorOptions(512, 512, 1)));
        Assert.Equal("a 512x512 grid has more than 4096 cells (Parameter 'options')", big.Message);
        ArgumentException generated = Assert.Throws<ArgumentException>(
            () => LevelGenerator.Generate(Kinds, new LevelGeneratorOptions(512, 512, 1), "l", "x"));
        Assert.Equal(big.Message, generated.Message);

        // Rows times columns past int is still a grid too big, not an overflow.
        Assert.Throws<ArgumentException>(() => LevelGenerator.CheckOptions(new LevelGeneratorOptions(int.MaxValue, int.MaxValue, 1)));

        ArgumentException ratio = Assert.Throws<ArgumentException>(() => LevelGenerator.CheckOptions(new LevelGeneratorOptions(2, 2, 1, 1.5)));
        Assert.Equal("the empty ratio 1.5 is not in [0, 1) (Parameter 'options')", ratio.Message);
    }

    /// <summary>The header a generated file starts with says how it was made.</summary>
    [Fact]
    public void TheHeaderSaysHowTheLevelWasMade()
    {
        LevelGeneratorOptions options = new(2, 3, 42, 0.34);
        LevelGrid level = LevelGenerator.Generate(Kinds, options, "l", "x");
        Assert.Equal(
            ["generated by ssmap layout: seed 42, 2 rows x 3 columns, 2 empty cell(s)", "the grid's first line is the north row; each line runs west to east"],
            LevelGenerator.Header(options, level));
    }

    /// <summary>Every wall shared by two rooms has a socket on both sides or on neither.</summary>
    private static void AssertLinesUp(LevelGrid level, Dictionary<string, RoomDefinition> byName)
    {
        HashSet<(int, int, int, int)> sockets = [];
        foreach ((int x, int y, LevelCell cell) in level.Placed)
        {
            RoomTransform transform = new(new RoomPlacement(cell.Room, x, y, cell.Rotation), 1);
            foreach (RoomSocket socket in byName[cell.Room].Sockets)
            {
                (int axis, int sign) = transform.WorldNormal(socket.Facing);
                sockets.Add((x, y, axis, sign));
            }
        }

        foreach ((int x, int y, _) in level.Placed)
        {
            if (x + 1 < level.Columns && level[x + 1, y] is not null)
            {
                Assert.Equal(sockets.Contains((x, y, 0, 1)), sockets.Contains((x + 1, y, 0, -1)));
            }

            if (y + 1 < level.Rows && level[x, y + 1] is not null)
            {
                Assert.Equal(sockets.Contains((x, y, 1, 1)), sockets.Contains((x, y + 1, 1, -1)));
            }
        }
    }

    /// <summary>The inverse of SplitMix64's output mix, so a test can pick the raw value it needs.</summary>
    private static ulong Unmix(ulong z)
    {
        z = UnXorShift(z, 31);
        z *= 0x319642B2D24D8EC3UL; // inverse of 0x94D049BB133111EB
        z = UnXorShift(z, 27);
        z *= 0x96DE1B173F119089UL; // inverse of 0xBF58476D1CE4E5B9
        z = UnXorShift(z, 30);
        return z;
    }

    private static ulong UnXorShift(ulong value, int shift)
    {
        ulong result = value;
        for (int i = 0; i < 64 / shift + 1; i++)
        {
            result = value ^ (result >> shift);
        }

        return result;
    }
}
