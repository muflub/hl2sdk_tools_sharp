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
/// The linker's cost in the size of the level: the passes that run before
/// any room is planned stay linear in the room count, so a level far past
/// the format's limits is refused in moments rather than after minutes.
/// </summary>
/// <remarks>
/// The budgets are generous (tens of seconds for work that takes well under
/// one) so a loaded machine never fails them; what they catch is a pass
/// that is quadratic in the rooms, which at these sizes takes minutes.
/// </remarks>
public sealed class LevelLinkerScaleTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Checking the joints of a 400 x 400 level (160,000 rooms, 638,400
    /// joints) finds each joint's neighbour by its cell, not by a scan of
    /// every room: a scan is ~5·10¹⁰ comparisons, minutes of work before the
    /// level can even be measured against the format's limits.
    /// </summary>
    [Fact]
    public async Task CheckingTheJointsIsLinearInTheRooms()
    {
        RoomLibrary library = await RoomHarness.LibraryAsync(false, RoomHarness.Hub());
        LevelLayout layout = HubGrid(library, 400);

        Task check = Task.Run(() => LevelLinker.ValidateJoints(layout, library));
        Assert.True(await Task.WhenAny(check, Task.Delay(Budget)) == check, "the joint check did not finish within its budget");
        await check;
    }

    // ---- the visibility closure --------------------------------------------

    /// <summary>
    /// The closure of 24,000 clusters in one directed ring (each sees itself
    /// and the next) is every cluster seeing every other. A pivot-by-pivot
    /// closure costs the cube of the clusters over the word width, ~4·10¹¹
    /// word operations here, minutes of work.
    /// </summary>
    [Fact]
    public void TheClosureOfALargeRingIsFast()
    {
        const int clusters = 24_000;
        int rowBytes = (clusters + 7) >> 3;
        byte[][] rows = new byte[clusters][];
        for (int c = 0; c < clusters; c++)
        {
            rows[c] = new byte[rowBytes];
            LevelLinker.OrBit(rows[c], c);
            LevelLinker.OrBit(rows[c], (c + 1) % clusters);
        }

        using CancellationTokenSource budget = new(Budget);
        LevelLinker.CloseRows(rows, clusters, budget.Token);

        Assert.All(rows, row => Assert.Equal(clusters, LevelLinker.PopCount(row)));
    }

    /// <summary>
    /// The closure is exactly what a pivot-by-pivot transitive closure
    /// (Warshall's, the linker's original) gives, on random graphs of every
    /// shape: sparse and dense, with and without each cluster seeing itself,
    /// and row lengths that are and are not a whole number of words.
    /// </summary>
    [Theory]
    [InlineData(1, 0.0, true, 1)]
    [InlineData(1, 1.0, false, 2)]
    [InlineData(2, 0.5, false, 3)]
    [InlineData(7, 0.2, false, 4)]
    [InlineData(31, 0.05, false, 5)]
    [InlineData(32, 0.05, true, 6)]
    [InlineData(33, 0.1, false, 7)]
    [InlineData(64, 0.02, false, 8)]
    [InlineData(100, 0.01, true, 9)]
    [InlineData(100, 0.03, false, 10)]
    [InlineData(257, 0.004, false, 11)]
    [InlineData(257, 0.01, true, 12)]
    [InlineData(300, 0.0, false, 13)]
    [InlineData(300, 0.5, false, 14)]
    public void TheClosureIsWarshallsClosure(int clusters, double density, bool self, int seed)
    {
        Random random = new(seed);
        int rowBytes = (clusters + 7) >> 3;
        byte[][] rows = new byte[clusters][];
        for (int c = 0; c < clusters; c++)
        {
            rows[c] = new byte[rowBytes];
            if (self)
            {
                LevelLinker.OrBit(rows[c], c);
            }

            for (int d = 0; d < clusters; d++)
            {
                if (random.NextDouble() < density)
                {
                    LevelLinker.OrBit(rows[c], d);
                }
            }
        }

        byte[][] expected = [.. rows.Select(r => (byte[])r.Clone())];
        Warshall(expected, clusters);
        LevelLinker.CloseRows(rows, clusters, CancellationToken.None);

        for (int c = 0; c < clusters; c++)
        {
            Assert.True(expected[c].AsSpan().SequenceEqual(rows[c]), $"row {c} differs");
        }
    }

    /// <summary>
    /// Groups of clusters that see each other in a cycle, chained one way:
    /// the first group sees all of them, the last only itself, a cluster that
    /// sees nothing (not even itself) stays empty, and one that sees into the
    /// first group but not itself sees everything but itself.
    /// </summary>
    [Fact]
    public void TheClosureFollowsEdgesOneWayBetweenGroups()
    {
        // Groups {0,1,2} -> {3,4} -> {5}; cluster 6 sees nothing; 7 sees 0 but not itself.
        const int clusters = 8;
        byte[][] rows = [.. Enumerable.Range(0, clusters).Select(_ => new byte[1])];
        void Edge(int a, int b) => LevelLinker.OrBit(rows[a], b);
        Edge(0, 1);
        Edge(1, 2);
        Edge(2, 0);
        Edge(2, 3);
        Edge(3, 4);
        Edge(4, 3);
        Edge(4, 5);
        Edge(5, 5);
        Edge(7, 0);

        byte[][] expected = [.. rows.Select(r => (byte[])r.Clone())];
        Warshall(expected, clusters);
        LevelLinker.CloseRows(rows, clusters, CancellationToken.None);

        Assert.Equal(expected, rows);
        Assert.Equal(0b0011_1111, rows[0][0]);
        Assert.Equal(0b0011_1000, rows[3][0]);
        Assert.Equal(0b0010_0000, rows[5][0]);
        Assert.Equal(0, rows[6][0]);
        Assert.Equal(0b0011_1111, rows[7][0]);
    }

    /// <summary>
    /// The reference: Warshall's closure over the rows, pivot by pivot in
    /// cluster order, as the linker first computed it.
    /// </summary>
    private static void Warshall(byte[][] rows, int clusterCount)
    {
        for (int k = 0; k < clusterCount; k++)
        {
            for (int i = 0; i < clusterCount; i++)
            {
                if ((rows[i][k >> 3] & (1 << (k & 7))) == 0)
                {
                    continue;
                }

                for (int b = 0; b < rows[i].Length; b++)
                {
                    rows[i][b] |= rows[k][b];
                }
            }
        }
    }

    // ---- helpers -------------------------------------------------------------

    /// <summary>A grid of hubs, every one turned the same way, with joints wherever two meet.</summary>
    internal static LevelLayout HubGrid(RoomLibrary library, int size)
    {
        LevelCell?[] cells = new LevelCell?[size * size];
        Array.Fill(cells, new LevelCell("hub", 0));
        LevelGrid grid = new("grid", "rooms.vmf", size, size, cells);
        return grid.ToLayout(name => library.Find(name)?.Definition, library.CellSize, library.Kit);
    }
}
