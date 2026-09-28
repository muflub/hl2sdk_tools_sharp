//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapGen.Catalog;
using SourceSharp.MapGen.Content;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Bsp.Tree;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;

using SourceSharp.Tests.MapTools.Bsp.Csg;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Tree;

/// <summary>
/// The tree build with subtrees forked onto other threads
/// (<see cref="BspTreeParallelism"/>) builds the serial build's tree: node
/// for node, plane for plane, brush for brush, winding for winding, with the
/// same counters and the same diagnostics in the same order.
/// </summary>
/// <remarks>
/// <para>
/// The comparison is a full text dump of one world pass
/// (<see cref="BlockGrid.BuildWorldPass"/>): every node's id, plane, contents,
/// parent and chosen side; every volume and leaf brush with its id, bounds,
/// saved sides and every side's winding, point by point as float bits, read
/// through the COMPILE'S arena, so a winding a join forgot to bring home reads
/// as garbage or throws; the whole plane table; the context's counters; the
/// diagnostics; and the per-block statistics. The whole-compile half of the
/// proof, the written bytes, is <c>VbspParallelTreeTests</c>.
/// </para>
/// <para>
/// The facts force forks with a threshold of one brush and a fork depth of at
/// least three, at every degree, so the small maps fork too; each asserts that
/// something did fork, because an equality over a build that never forked
/// proves nothing. Degree 1 here is a pool of one thread with forks still on,
/// which runs every helper on the one pool thread beside the test's.
/// </para>
/// </remarks>
public sealed class BrushBspTreeParallelTests
{
    /// <summary>The degrees every equality is checked at.</summary>
    public static TheoryData<int> Degrees => [1, 2, 3, 8, 32];

    /// <summary>Catalogue maps with enough structure to fork many levels deep.</summary>
    public static TheoryData<string> CatalogueMaps =>
    [
        "l3_arena_144_pillars",
        "l2_detail_and_hint_in_a_corridor",
        "l2_areaportal_between_pools",
        "l1_water_volume",
        "l1_hint_skip",
    ];

    // ---- the fork depth and the size test -----------------------------------

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 3)]
    [InlineData(3, 4)]
    [InlineData(4, 4)]
    [InlineData(8, 5)]
    [InlineData(9, 6)]
    [InlineData(16, 6)]
    [InlineData(32, 7)]
    [InlineData(int.MaxValue, 18)]
    public void TheForkDepthIsTheLevelsThatGiveEveryThreadASubtreePlusTwo(int degree, int depth)
    {
        Assert.Equal(depth, BspTreeParallelism.ForkDepthFor(degree));
    }

    [Fact]
    public async Task HasAtLeastCountsOnlyAsFarAsItNeedsTo()
    {
        (BspBuildContext build, _) = await SixBoxes();
        BspBrush? six = CsgFixture.AllBrushes(build);

        Assert.True(BrushBspTree.HasAtLeast(null, 0));
        Assert.True(BrushBspTree.HasAtLeast(null, -1));
        Assert.False(BrushBspTree.HasAtLeast(null, 1));
        Assert.True(BrushBspTree.HasAtLeast(six, 6));
        Assert.False(BrushBspTree.HasAtLeast(six, 7));
    }

    // ---- when it forks ----------------------------------------------------

    [Fact]
    public async Task WithNoSchedulerNothingForks()
    {
        int forks = await ForksOverSixBoxesAsync(scheduler: false, maxDepth: 5, minBrushes: 1);

        Assert.Equal(0, forks);
    }

    [Fact]
    public async Task AtForkDepthZeroNothingForks()
    {
        int forks = await ForksOverSixBoxesAsync(scheduler: true, maxDepth: 0, minBrushes: 1);

        Assert.Equal(0, forks);
    }

    [Fact]
    public async Task ASplitWithASideBelowTheThresholdIsNotForked()
    {
        int forks = await ForksOverSixBoxesAsync(scheduler: true, maxDepth: 5, minBrushes: 4);

        Assert.Equal(0, forks);
    }

    [Fact]
    public async Task AtForkDepthOneOnlyTheRootForks()
    {
        int forks = await ForksOverSixBoxesAsync(scheduler: true, maxDepth: 1, minBrushes: 1);

        Assert.Equal(1, forks);
    }

    [Fact]
    public async Task DeeperForkingForksBelowTheRootToo()
    {
        int forks = await ForksOverSixBoxesAsync(scheduler: true, maxDepth: 8, minBrushes: 1);

        Assert.True(forks > 1, $"only {forks} forks");
    }

    // ---- the plane table --------------------------------------------------

    /// <summary>
    /// Splitting never adds a plane, so the plane table is read-only for the
    /// whole recursion and needs no ordering: the only planes a tree build
    /// adds are its head volume's, which <see cref="BrushGeometry.BrushFromBounds"/>
    /// finds before the recursion starts. Made first here, they leave the
    /// build nothing to add.
    /// </summary>
    [RepoSourceFact("maps/ss_sandbox.vmf")]
    public async Task BuildingTheSandboxWorldAddsNoPlaneBeyondItsHeadVolumes()
    {
        (BspBuildContext build, MapFile map) = await SandboxAsync();
        Vec3 mins = new(-4096, -4096, -4096);
        Vec3 maxs = new(4096, 4096, 4096);
        BspBrush? list = BrushCsg.MakeBspBrushList(build, build.BrushStart, build.BrushEnd, mins, maxs, DetailScreen.NoDetail);
        list = BrushCsg.ChopBrushes(build, list);
        BrushGeometry.BrushFromBounds(build, mins, maxs);
        int planes = map.Planes.Count;

        BspTree tree = BrushBspTree.BrushBsp(build, list, mins, maxs);

        Assert.True(tree.Statistics.VisibleNodes > 100, $"only {tree.Statistics.VisibleNodes} nodes");
        Assert.Equal(planes, map.Planes.Count);
    }

    // ---- equality ---------------------------------------------------------

    [RepoSourceFact("maps/ss_sandbox.vmf")]
    public async Task TheSandboxWorldIsTheSerialTreeAtEveryDegree()
    {
        string serial = await DumpAsync(SandboxAsync, degree: null);

        foreach (int degree in Degrees)
        {
            for (int run = 0; run < 3; run++)
            {
                AssertSame(serial, await DumpAsync(SandboxAsync, degree), $"sandbox, degree {degree}, run {run}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(CatalogueMaps))]
    public async Task ACatalogueWorldIsTheSerialTreeAtEveryDegree(string name)
    {
        Task<(BspBuildContext, MapFile)> Load() => CatalogueAsync(name);
        string serial = await DumpAsync(Load, degree: null);

        foreach (int degree in Degrees)
        {
            for (int run = 0; run < 3; run++)
            {
                AssertSame(serial, await DumpAsync(Load, degree), $"{name}, degree {degree}, run {run}");
            }
        }
    }

    // ---- cancellation -------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACancelledBuildStopsAtTheNextNode(bool parallel)
    {
        (BspBuildContext build, _) = await SixBoxes();
        using CompilePool pool = new(2);
        using CancellationTokenSource cancel = new();
        await cancel.CancelAsync();
        build.TreeParallelism = new BspTreeParallelism
        {
            Scheduler = parallel ? pool.Scheduler : null,
            MaxForkDepth = 3,
            MinBrushes = 1,
            CancellationToken = cancel.Token,
        };

        Assert.ThrowsAny<OperationCanceledException>(() => BrushBspTree.BrushBsp(
            build, CsgFixture.AllBrushes(build), new Vec3(-512, -512, -512), new Vec3(512, 512, 512)));
    }

    // ---- helpers ------------------------------------------------------------

    private static Task<(BspBuildContext Build, MapFile Map)> SixBoxes() =>
        CsgFixture.LoadAsync(CsgFixture.World(
            (UnitMap.Plain, (0, 0, 0), (64, 64, 64)),
            (UnitMap.Plain, (128, 0, 0), (192, 64, 64)),
            (UnitMap.Plain, (256, 0, 0), (320, 64, 64)),
            (UnitMap.Plain, (0, 128, 0), (64, 192, 64)),
            (UnitMap.Plain, (128, 128, 0), (192, 192, 64)),
            (UnitMap.Plain, (256, 128, 0), (320, 192, 64))));

    private static async Task<int> ForksOverSixBoxesAsync(bool scheduler, int maxDepth, int minBrushes)
    {
        (BspBuildContext build, _) = await SixBoxes();
        using CompilePool pool = new(2);
        build.TreeParallelism = new BspTreeParallelism
        {
            Scheduler = scheduler ? pool.Scheduler : null,
            MaxForkDepth = maxDepth,
            MinBrushes = minBrushes,
        };

        BrushBspTree.BrushBsp(
            build, CsgFixture.AllBrushes(build), new Vec3(-512, -512, -512), new Vec3(512, 512, 512));

        return build.ForkedSubtrees;
    }

    private static async Task<(BspBuildContext Build, MapFile Map)> SandboxAsync()
    {
        byte[] vmf = await File.ReadAllBytesAsync(RepoSourceFactAttribute.Find("maps/ss_sandbox.vmf")!);
        return await LoadAsync("ss_sandbox", vmf);
    }

    private static Task<(BspBuildContext Build, MapFile Map)> CatalogueAsync(string name) =>
        LoadAsync(name, TestMapCatalog.Named(name).WriteVmfBytes());

    private static async Task<(BspBuildContext Build, MapFile Map)> LoadAsync(string name, byte[] vmf)
    {
        InMemoryFileSystem disk = new();
        foreach ((string path, byte[] bytes) in SyntheticContent.Build())
        {
            disk.AddFile(path, bytes);
        }

        disk.AddFile($"maps/{name}.vmf", vmf);
        ContentFileSystem content = new([await DirectoryContentMount.MountAsync(disk, VPath.Empty)]);
        VbspContext context = new(VbspOptions.Default, content) { MapBase = name };
        MapFile map = await new MapFileReader(context, disk).LoadAsync(VPath.Create($"maps/{name}.vmf"));

        BspBuildContext build = new(context, map)
        {
            BrushStart = map.Entities[0].FirstBrush,
            BrushEnd = map.Entities[0].FirstBrush + map.Entities[0].BrushCount,
        };

        return (build, map);
    }

    /// <summary>
    /// One world pass, serial (<paramref name="degree"/> null) or forked at
    /// every pair on a pool of <paramref name="degree"/> threads, dumped.
    /// </summary>
    private static async Task<string> DumpAsync(Func<Task<(BspBuildContext, MapFile)>> load, int? degree)
    {
        (BspBuildContext build, MapFile map) = await load();
        using CompilePool? pool = degree is int d ? new CompilePool(d) : null;
        if (pool is not null)
        {
            build.TreeParallelism = new BspTreeParallelism
            {
                Scheduler = pool.Scheduler,
                MaxForkDepth = Math.Max(3, BspTreeParallelism.ForkDepthFor(pool.Degree)),
                MinBrushes = 1,
                MaxDegree = Math.Max(2, pool.Degree),
            };
        }

        BspBlockGrid grid = BlockGrid.Clamp(build.Options.Blocks, map.Mins, map.Maxs);
        bool blocksInParallel = pool is not null
            && ParallelWorldPass.Applies(build, (grid.MaxX - grid.MinX + 1) * (grid.MaxY - grid.MinY + 1));
        BspTree tree = BlockGrid.BuildWorldPass(
            build, grid, map.Mins, map.Maxs, out IReadOnlyList<BlockBuildStatistics> blocks);

        if (pool is not null)
        {
            Assert.True(build.ForkedSubtrees > 0, "nothing forked, so the comparison proves nothing");
        }

        // The blocks were built in parallel wherever the pass allows it, and
        // then every block of the grid was.
        Assert.Equal(blocksInParallel ? blocks.Count : 0, build.ForkedBlocks);

        return Dump(build, map, tree, blocks);
    }

    internal static string Dump(BspBuildContext build, MapFile map, BspTree tree, IReadOnlyList<BlockBuildStatistics> blocks)
    {
        StringBuilder text = new();
        void Line(FormattableString line) => text.Append(line.ToString(CultureInfo.InvariantCulture)).Append('\n');

        Line($"planes {map.Planes.Count}");
        for (int i = 0; i < map.Planes.Count; i++)
        {
            Plane p = map.Planes[i];
            Line($"plane {i} {Bits(p.Normal)} {Bits(p.Dist)} {map.Planes.TypeOf(i)}");
        }

        Line($"counters nodes={build.AllocatedNodes} brushes={build.AllocatedBrushes} active={build.ActiveBrushes} live={build.Nodes} nonvis={build.NonVisibleNodes} pruned={build.PrunedNodes}");
        foreach (var d in build.Diagnostics)
        {
            Line($"diagnostic {d.Code} {d.Severity} {d.Message} {d.Location}");
        }

        foreach (BlockBuildStatistics block in blocks)
        {
            Line($"block {block}");
        }

        Line($"tree {Bits(tree.Mins)} {Bits(tree.Maxs)}");

        Stack<BspNode> pending = new();
        pending.Push(tree.HeadNode!);
        while (pending.Count > 0)
        {
            BspNode node = pending.Pop();
            BspBrushSide s = node.Side;
            Line($"node {node.Id} plane={node.PlaneNumber} contents={node.Contents} parent={node.Parent?.Id} hasside={node.HasSide} side={s.PlaneNumber}/{s.TexInfo}/{s.Contents}/{s.Surface}/{s.Visible}/{s.Tested}/{s.Bevel}");
            if (node.Volume is not null)
            {
                Brush("volume", node.Volume);
            }

            if (node.IsLeaf)
            {
                for (BspBrush? b = node.BrushList; b is not null; b = b.Next)
                {
                    Brush("leaf", b);
                }

                continue;
            }

            pending.Push(node.Children[1]!);
            pending.Push(node.Children[0]!);
        }

        return text.ToString();

        void Brush(string kind, BspBrush b)
        {
            Line($"  {kind} brush {b.Id} scope={b.IdScope is null} original={b.Original?.Id} {Bits(b.Mins)} {Bits(b.Maxs)} side={b.Side} test={b.TestSide} sides={b.SideCount}");
            foreach (BspBrushSide side in b.Sides)
            {
                StringBuilder points = new();
                if (!side.Winding.IsNull)
                {
                    foreach (Vec3 p in build.Windings.Points(side.Winding))
                    {
                        points.Append(' ').Append(Bits(p));
                    }
                }

                Line($"    side {side.PlaneNumber} {side.TexInfo} {side.Contents} {side.Surface} {side.Visible} {side.Tested} {side.Bevel} {side.Displacement is not null} w={side.Winding.Count}/{side.Winding.Capacity}{points}");
            }
        }
    }

    private static string Bits(float f) => BitConverter.SingleToInt32Bits(f).ToString("x8", CultureInfo.InvariantCulture);

    private static string Bits(Vec3 v) => $"{Bits(v.X)},{Bits(v.Y)},{Bits(v.Z)}";

    internal static void AssertSame(string expected, string actual, string what)
    {
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return;
        }

        string[] e = expected.Split('\n');
        string[] a = actual.Split('\n');
        int i = 0;
        while (i < Math.Min(e.Length, a.Length) && e[i] == a[i])
        {
            i++;
        }

        Assert.Fail(
            $"{what}: the trees differ at line {i + 1} of {e.Length}\n"
            + $"  serial:   {(i < e.Length ? e[i] : "(end)")}\n"
            + $"  parallel: {(i < a.Length ? a[i] : "(end)")}");
    }
}
