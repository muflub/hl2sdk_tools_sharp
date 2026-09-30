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

    // ---- the format's limits, up front ---------------------------------------

    /// <summary>
    /// A level past a field's limit is refused before any room is planned: a
    /// room whose compile the relocation refuses (it carries area portals)
    /// stands in the first cell, and the refusal is still the limit's. Planning
    /// every room first costs memory and time in the rooms, which for a
    /// level hundreds of times too big is gigabytes and minutes spent on a
    /// level that was never going to link.
    /// </summary>
    [Fact]
    public async Task ALevelPastALimitIsRefusedBeforeAnyRoomIsPlanned()
    {
        RoomLibrary hubs = await RoomHarness.LibraryAsync(false, RoomHarness.Hub());
        RoomObject hub = hubs.Get("hub");
        RoomObject bad = RoomHarness.WithLumps(hub with { Definition = hub.Definition with { Name = "bad" } }, bsp =>
        {
            bsp.SetLump(SourceSharp.MapFormats.Bsp.BspLump.Areas, new byte[3 * 8]);
            bsp.SetLump(SourceSharp.MapFormats.Bsp.BspLump.AreaPortals, new byte[2 * 12]);
        });
        RoomLibrary library = RoomHarness.Library(hub, bad);

        // Enough hubs to pass the vertex limit, which every room adds to
        // (the planes are shared, so a grid of one room never fills them).
        int vertices = SourceSharp.MapFormats.Bsp.Structs.BspStructView.Count<SourceSharp.MapFormats.Geometry.Vec3>(
            hub.Bsp[SourceSharp.MapFormats.Bsp.BspLump.Vertexes]);
        int size = (int)Math.Ceiling(Math.Sqrt((65536.0 / vertices) + 2));
        LevelCell?[] cells = new LevelCell?[size * size];
        Array.Fill(cells, new LevelCell("hub", 0));
        cells[0] = new LevelCell("bad", 0);
        LevelLayout layout = new LevelGrid("big", "rooms.vmf", size, size, cells)
            .ToLayout(name => library.Find(name)?.Definition, library.CellSize, library.Kit);

        LinkException refused = await Assert.ThrowsAsync<LinkException>(
            async () => await LevelLinker.LinkAsync(layout, library, await RoomHarness.ContextAsync()));
        Assert.Contains(" pushes the link to ", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("area portal", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The limit refusal names the room whose addition crosses the limit, its
    /// cell, the running total and the limit; a level one room short of it
    /// passes the check.
    /// </summary>
    [Fact]
    public async Task TheLimitRefusalNamesTheRoomThatCrossesIt()
    {
        RoomLibrary library = await RoomHarness.LibraryAsync(false, RoomHarness.Hub());
        RoomObject hub = library.Get("hub");
        LevelLinker.LinkCounts counts = LevelLinker.LinkCounts.Of(hub.Bsp, hub.ClusterCount);

        // A jointed socket's plug brushes are not written, so they are not
        // counted: a line's two end hubs each strip one plug, every hub
        // between them two (each hub socket has one plug brush).
        int[] plugs = LevelLinker.ComputeShared(hub).Sockets[0].StrippedBrushes;
        Assert.Single(plugs);
        int plugSides = SourceSharp.MapFormats.Bsp.Structs.BspStructView.As<SourceSharp.MapFormats.Bsp.Structs.DBrush>(
            hub.Bsp[SourceSharp.MapFormats.Bsp.BspLump.Brushes])[plugs[0]].NumSides;
        long Kept(long perRoom, long plug, long length) =>
            length == 1 ? perRoom : (2 * (perRoom - plug)) + ((length - 2) * (perRoom - (2 * plug)));

        // The field every hub fills fastest crosses first; the room that
        // crosses it is the last of the shortest line whose total passes the
        // limit (the totals grow room by room, and a line one room shorter
        // ends on a hub that strips one plug where this one strips two, so
        // it stays under). The shared tables are not in the race: a line of
        // one room brings its materials once, and its planes and texinfos
        // are checked as the assembly shares them (LevelLinkerSharedTablesTests).
        (string what, Func<long, long> total, long max)[] fields =
        [
            ("vertices", n => n * counts.Vertices, ushort.MaxValue + 1),
            ("faces", n => n * counts.Faces, ushort.MaxValue + 1),
            ("leaves", n => 1 + (n * counts.Leaves), ushort.MaxValue + 1),
            ("leaf faces", n => n * counts.LeafFaces, ushort.MaxValue + 1),
            ("primitive indices", n => n * counts.PrimitiveIndices, ushort.MaxValue + 1),
            ("brushes", n => Kept(counts.Brushes, 1, n), 8192),
            ("brush sides", n => Kept(counts.BrushSides, plugSides, n), 65536),
            ("nodes", n => -1 + (n * (counts.Nodes + 2)), 65536),
        ];
        (string what, long crossing) = fields
            .Select(f =>
            {
                long n = 1;
                while (f.total(n) <= f.max)
                {
                    n++;
                }

                return (f.what, crossing: n);
            })
            .MinBy(f => f.crossing);
        Assert.Equal("brushes", what); // the hub's binding total, which the stripped plugs move

        // Without the fold: with it, the brush caps are held after the
        // fold, in the assembly, where the folded totals are known.
        LevelLinkOptions noFold = new() { FoldBrushes = false };
        LevelLayout under = Line(library, (int)crossing - 1);
        LevelLinker.CheckCapacity(under, library, noFold);

        LevelLayout over = Line(library, (int)crossing);
        LinkException refused = Assert.Throws<LinkException>(() => LevelLinker.CheckCapacity(over, library, noFold));
        Assert.StartsWith($"room hub at cell ({(crossing - 1) % LineRow}, {(crossing - 1) / LineRow}) pushes the link to ", refused.Message, StringComparison.Ordinal);
        Assert.Contains($" {what}; the ", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The cluster total is checked against the leaf's short cluster field
    /// once every room is counted, and names the last room.
    /// </summary>
    [Fact]
    public void TheClusterTotalIsLimitedToTheLeafsField()
    {
        LevelLinker.LinkCounts one = new() { Clusters = short.MaxValue / 2 };
        LevelLinker.LinkTotals totals = new();
        totals.Add(one, "a", 0, 0);
        totals.Add(one, "b", 1, 0);
        totals.CheckClusters("b", 1, 0);
        totals.Add(one with { Clusters = 2 }, "c", 2, 0);
        LinkException refused = Assert.Throws<LinkException>(() => totals.CheckClusters("c", 2, 0));
        Assert.Equal(
            $"room c at cell (2, 0) pushes the link to {short.MaxValue + 1} clusters; the format carries at most {short.MaxValue}.",
            refused.Message);
    }

    /// <summary>
    /// Totals the engine's loader caps below their field's width are refused
    /// at the loader's cap, not the field's: a linked map past
    /// <c>MAX_MAP_TEXDATA</c>, <c>MAX_MAP_BRUSHES</c> or
    /// <c>MAX_MAP_BRUSHSIDES</c> is one the engine refuses to load (and
    /// <c>ssmap check</c> reports), so the link must not write it.
    /// (<c>MAX_MAP_TEXINFO</c> is held where the texinfos are shared.)
    /// </summary>
    [Theory]
    [InlineData("texdatas", 2048, "MAX_MAP_TEXDATA")]
    [InlineData("brushes", 8192, "MAX_MAP_BRUSHES")]
    [InlineData("brush sides", 65536, "MAX_MAP_BRUSHSIDES")]
    public void TotalsPastTheLoadersCapsAreRefused(string what, int cap, string constant)
    {
        LevelLinker.LinkCounts Half() => what switch
        {
            "texdatas" => new() { TexDatas = cap / 2 },
            "brushes" => new() { Brushes = cap / 2 },
            _ => new() { BrushSides = cap / 2 },
        };

        LevelLinker.LinkTotals totals = new();
        totals.Add(Half(), "a", 0, 0);
        totals.Add(Half(), "b", 1, 0); // exactly the cap: loads
        LinkException refused = Assert.Throws<LinkException>(() => totals.Add(new LevelLinker.LinkCounts() with
        {
            TexDatas = what == "texdatas" ? 1 : 0,
            Brushes = what == "brushes" ? 1 : 0,
            BrushSides = what == "brush sides" ? 1 : 0,
        }, "c", 2, 0));
        Assert.Equal(
            $"room c at cell (2, 0) pushes the link to {cap + 1} {what}; the engine loads at most {cap} ({constant}).",
            refused.Message);
    }

    /// <summary>
    /// The node total starts at -1 and each room adds its nodes and 2: the
    /// top tree's floor of 2 x rooms - 1 nodes. It may reach
    /// <c>MAX_MAP_NODES</c> and is refused one past it, naming the loader's
    /// constant.
    /// </summary>
    [Fact]
    public void TheNodeTotalCountsTheTopTreesFloorAndIsCappedAtTheLoaders()
    {
        LevelLinker.LinkTotals totals = new();
        totals.Add(new LevelLinker.LinkCounts() with { Nodes = 32_767 }, "a", 0, 0); // -1 + 32,769
        totals.Add(new LevelLinker.LinkCounts() with { Nodes = 32_766 }, "b", 1, 0); // 65,536: loads
        LinkException refused = Assert.Throws<LinkException>(() => totals.Add(new LevelLinker.LinkCounts(), "c", 2, 0));
        Assert.Equal(
            "room c at cell (2, 0) pushes the link to 65538 nodes; the engine loads at most 65536 (MAX_MAP_NODES).",
            refused.Message);
    }

    /// <summary>
    /// The visibility lump is capped on its bytes (<c>MAX_MAP_VISIBILITY</c>,
    /// 16 MB): a linked level's rows are its whole door graph's closure, so
    /// they grow with the square of the clusters and can pass the cap before
    /// any count does.
    /// </summary>
    [Fact]
    public void AVisibilityLumpPastTheLoadersCapIsRefused()
    {
        LevelLinker.LimitVisibility("a", 3, 4, 0x1000000);
        LinkException refused = Assert.Throws<LinkException>(() => LevelLinker.LimitVisibility("a", 3, 4, 0x1000001));
        Assert.Equal(
            "room a at cell (3, 4) pushes the link to 16777217 visibility bytes; the engine loads at most 16777216 (MAX_MAP_VISIBILITY).",
            refused.Message);
    }

    /// <summary>A line of hubs along +x, jointed end to end.</summary>
    /// <summary>
    /// A line of hubs, each joined to the next, folded into rows of
    /// <see cref="LineRow"/>: the joints are the line's, which is all the
    /// capacity count reads of a placement besides its room, and the rows
    /// keep every placement within the engine's coordinates, which the
    /// capacity check refuses to pass (the rooms design, 17.6).
    /// </summary>
    private static LevelLayout Line(RoomLibrary library, int length)
    {
        LevelCell?[] cells = new LevelCell?[length];
        Array.Fill(cells, new LevelCell("hub", 0));
        LevelLayout line = new LevelGrid("line", "rooms.vmf", 1, length, cells)
            .ToLayout(name => library.Find(name)?.Definition, library.CellSize, library.Kit);
        return line with
        {
            Rooms = [.. line.Rooms.Select((r, i) => r with { Placement = r.Placement with { CellX = i % LineRow, CellY = i / LineRow } })],
        };
    }

    /// <summary>How many hubs a line's row holds: 64 cells of 256 reach 16,384, the engine's limit.</summary>
    private const int LineRow = 64;

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
