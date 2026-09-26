//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Vis;

/// <summary>
/// <see cref="VvisOptions.Tighten"/> on hand-built maps.
/// </summary>
/// <remarks>
/// <para>
/// The two properties the plan makes non-negotiable, each its own fact: the
/// tightened answer is a SUBSET of the untightened one (a single added bit is a
/// bug, not a tolerance), and it is a pure function of the input -- the same
/// bytes at one thread and at thirty-two, sorted or not, run after run.
/// </para>
/// <para>
/// The fixture is a grid of rooms whose shared walls each have a window cut at
/// a different offset, so sight lines are partly blocked in both directions,
/// the flow is deep enough for finished portals to prune later ones, and the
/// 360 memory portals give the ranking real dependencies to wait on. A straight corridor
/// would pass every one of these facts without the tightening doing anything;
/// <see cref="TighteningPrunesFlowWorkOnTheGrid"/> is the check that it did.
/// </para>
/// </remarks>
public class VvisTightenTests
{
    private const int GridWidth = 10;
    private const int GridHeight = 10;
    private const float Cell = 64f;

    // The two arms spelled explicitly so the facts mean what they say after
    // the Phase-5 promotion (vis-repair rung2): the tightened walk is the
    // record default now, and the untightened walk is opt-out, not Default.
    // Pinning them here keeps a future default change from silently turning
    // every loose-vs-tight comparison into a tight-vs-tight tautology.
    private static readonly VvisOptions Tight = new() { Tighten = true };
    private static readonly VvisOptions Untight = new() { Tighten = false };

    /// <summary>
    /// A <see cref="GridWidth"/> x <see cref="GridHeight"/> grid of 64-unit
    /// rooms. Every shared wall has one window, 16 to 40 units wide, whose
    /// position along the wall is a fixed function of the wall's coordinates.
    /// </summary>
    internal static (BspData Map, PortalSet Portals) Grid() =>
        (VisFixture.Map(GridWidth * GridHeight), PortalSet.FromPortalFile(GridFile()));

    internal static PortalFile GridFile()
    {
        List<FilePortal> portals = [];

        for (int j = 0; j < GridHeight; j++)
        {
            for (int i = 0; i + 1 < GridWidth; i++)
            {
                (float from, float to) = Window(i, j, 0);
                portals.Add(VisFixture.WindowAtX(
                    Cluster(i, j), Cluster(i + 1, j), (i + 1) * Cell, (j * Cell) + from, (j * Cell) + to));
            }
        }

        for (int j = 0; j + 1 < GridHeight; j++)
        {
            for (int i = 0; i < GridWidth; i++)
            {
                (float from, float to) = Window(i, j, 1);
                portals.Add(VisFixture.WindowAtY(
                    Cluster(i, j), Cluster(i, j + 1), (j + 1) * Cell, (i * Cell) + from, (i * Cell) + to));
            }
        }

        return VisFixture.Portals(GridWidth * GridHeight, [.. portals]);
    }

    private static int Cluster(int i, int j) => (j * GridWidth) + i;

    private static (float From, float To) Window(int i, int j, int axis)
    {
        // Deterministic and irregular: offsets 4..40, widths 16..40, never
        // past the wall's 64 units.
        int seed = (i * 7) + (j * 13) + (axis * 5);
        float from = 4f + ((seed * 11) % 10 * 3.6f);
        float width = 16f + ((seed * 3) % 7 * 4f);
        return (from, Math.Min(from + width, Cell - 4f));
    }

    private static async Task<(BspData Map, VisResult Result)> RunAsync(
        VvisOptions options,
        int degree)
    {
        (BspData map, PortalSet portals) = Grid();

        VisContext context = new()
        {
            Options = options,
            Parallelism = new CompileParallelism { MaxDegree = degree },
        };

        VisResult result = await Vvis.ComputeAsync(map, portals, context, CancellationToken.None);
        return (map, result);
    }

    private static int CountPvs(VisResult result)
    {
        int count = 0;
        for (int a = 0; a < result.ClusterCount; a++)
        {
            for (int b = 0; b < result.ClusterCount; b++)
            {
                count += result.CanSee(a, b) ? 1 : 0;
            }
        }

        return count;
    }

    [Fact]
    public async Task TheGridIsNeitherTrivialNorFullyVisible()
    {
        // The fixture's own check, on the loosest arm: if even the untightened
        // flow sees through every wall, or only its neighbours, there is
        // nothing for a tightening to get wrong.
        (_, VisResult result) = await RunAsync(Untight, degree: 1);

        int seen = CountPvs(result);
        int clusters = result.ClusterCount;

        Assert.InRange(seen, (clusters * 5) + 1, (clusters * clusters) - 1);
    }

    [Fact]
    public async Task TightenedVisibilityIsASubsetOfTheUntightened()
    {
        (_, VisResult loose) = await RunAsync(Untight, degree: 1);
        (_, VisResult tight) = await RunAsync(Tight, degree: 1);

        for (int a = 0; a < loose.ClusterCount; a++)
        {
            for (int b = 0; b < loose.ClusterCount; b++)
            {
                Assert.False(
                    tight.CanSee(a, b) && !loose.CanSee(a, b),
                    $"-tighten ADDED {a} sees {b}");
            }
        }
    }

    [Fact]
    public async Task TightenedAudibilityIsASubsetOfTheUntightened()
    {
        (_, VisResult loose) = await RunAsync(Untight, degree: 1);
        (_, VisResult tight) = await RunAsync(Tight, degree: 1);

        for (int a = 0; a < loose.ClusterCount; a++)
        {
            for (int b = 0; b < loose.ClusterCount; b++)
            {
                Assert.False(
                    tight.CanHear(a, b) && !loose.CanHear(a, b),
                    $"-tighten ADDED {a} hears {b}");
            }
        }
    }

    [Fact]
    public async Task TighteningPrunesFlowWorkOnTheGrid()
    {
        // Without this the two subset facts above could pass on a tightening
        // that never engaged: an identical answer is a subset too.
        (_, VisResult loose) = await RunAsync(Untight, degree: 1);
        (_, VisResult tight) = await RunAsync(Tight, degree: 1);

        Assert.True(
            tight.Work.Chains < loose.Work.Chains,
            $"-tighten flowed {tight.Work.Chains} chains against {loose.Work.Chains} untightened");
    }

    [Fact]
    public async Task TightenedOutputIsTheSameAtOneThreadAndThirtyTwo()
    {
        (BspData one, _) = await RunAsync(Tight, degree: 1);
        (BspData many, _) = await RunAsync(Tight, degree: 32);

        Assert.Equal(
            one[BspLump.Visibility].Data.ToArray(),
            many[BspLump.Visibility].Data.ToArray());
    }

    [Fact]
    public async Task TightenedOutputIsTheSameRunAfterRun()
    {
        (BspData first, _) = await RunAsync(Tight, degree: 8);

        for (int run = 0; run < 4; run++)
        {
            (BspData again, _) = await RunAsync(Tight, degree: 8);
            Assert.Equal(
                first[BspLump.Visibility].Data.ToArray(),
                again[BspLump.Visibility].Data.ToArray());
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task TightenedOutputIsTheSameAtAnyDegree(int degree)
    {
        // Odd degrees too: a schedule bug that only shows when workers do not
        // divide the portal count evenly is exactly the kind a 1-vs-32 check
        // can miss.
        (BspData one, _) = await RunAsync(Tight, degree: 1);
        (BspData other, _) = await RunAsync(Tight, degree: degree);

        Assert.Equal(
            one[BspLump.Visibility].Data.ToArray(),
            other[BspLump.Visibility].Data.ToArray());
    }

    [Fact]
    public async Task TightenedWorkCountersAreTheSameRunAfterRunAtOneThread()
    {
        // At one thread every neighbour a flow reads has finished before the
        // flow starts, so the walk is stock's own read for read and the
        // counters are a property of the map. At more threads they are not:
        // a flow may read a neighbour still being flowed and be re-walked
        // (VisTightening), which changes the work and never the answer.
        // that is what the lump facts above and VisSpeculationTests pin.
        (_, VisResult first) = await RunAsync(Tight, degree: 1);
        (_, VisResult second) = await RunAsync(Tight, degree: 1);

        Assert.Equal(first.Work, second.Work);
    }

    [Fact]
    public async Task NoSortDoesNotChangeTheTightenedOutput()
    {
        // The wave ranking replaces stock's sort, so -nosort must not reach it.
        (BspData sorted, _) = await RunAsync(Tight, degree: 4);
        (BspData unsorted, _) = await RunAsync(Tight with { NoSort = true }, degree: 4);

        Assert.Equal(
            sorted[BspLump.Visibility].Data.ToArray(),
            unsorted[BspLump.Visibility].Data.ToArray());
    }

    [Fact]
    public async Task TighteningLeavesAStraightCorridorAlone()
    {
        // Nothing to prune in a line of three rooms: the answer must be the
        // untightened one exactly, ends included.
        BspData looseMap = VisFixture.Map(3);
        BspData tightMap = VisFixture.Map(3);
        PortalFile file = VisFixture.Portals(
            3,
            VisFixture.WindowAtX(0, 1, x: 0f, yMin: 0f, yMax: 16f),
            VisFixture.WindowAtX(1, 2, x: 64f, yMin: 0f, yMax: 16f));

        await Vvis.ComputeAsync(
            looseMap, PortalSet.FromPortalFile(file), VisContext.Default with { Options = Untight });
        VisResult tight = await Vvis.ComputeAsync(
            tightMap, PortalSet.FromPortalFile(file), VisContext.Default with { Options = Tight });

        Assert.True(tight.CanSee(0, 2));
        Assert.Equal(
            looseMap[BspLump.Visibility].Data.ToArray(),
            tightMap[BspLump.Visibility].Data.ToArray());
    }

    [Fact]
    public async Task TighteningHasNoEffectUnderFast()
    {
        // -fast never runs the flow, and the tightening lives inside the flow;
        // the flood approximation is written unchanged.
        // Both arms explicit: after the promotion FastDefault carries
        // Tighten = true, so the pair that used to differ by the flag would
        // now be the same options twice.
        (BspData fast, _) = await RunAsync(VvisOptions.FastDefault with { Tighten = false }, degree: 4);
        (BspData both, _) = await RunAsync(VvisOptions.FastDefault with { Tighten = true }, degree: 4);

        Assert.Equal(
            fast[BspLump.Visibility].Data.ToArray(),
            both[BspLump.Visibility].Data.ToArray());
    }

    [Fact]
    // The tightened walk is the record default since the Phase-5 promotion
    // (vis-repair rung2), so `ssmap vvis` with no flag must already emit the
    // tightened chain count, and the opt-out switch must reach the untightened
    // count. StockArgs deliberately knows neither spelling (see
    // StockArgsVvisTests); these are the only ways a command line picks an arm.
    // The flow's work counters tell the arms apart, so the command's --bench
    // line carries the evidence.
    public async Task TheDefaultSsmapRunIsTightenedAndTheLooseSwitchReachesTheLibrary()
    {
        // `ssmap vvis` (no flag) == Tight; `-loose` == the untightened walk.
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "p2c-ssmap-tighten"));
        string bspPath = Path.Combine(root, "grid.bsp");
        string prtPath = Path.Combine(root, "grid.prt");

        using MemoryStream bspBytes = new();
        await BspFile.SaveAsync(VisFixture.Map(GridWidth * GridHeight), bspBytes, CancellationToken.None);
        using MemoryStream prtBytes = new();
        await GridFile().WriteAsync(prtBytes, cancellationToken: CancellationToken.None);

        InMemoryFileSystem files = new InMemoryFileSystem()
            .AddFile(bspPath, bspBytes.ToArray())
            .AddFile(prtPath, prtBytes.ToArray());

        // What the library says, on the same bytes the command will read.
        PortalFile parsed = await PortalFile.ParseAsync(prtBytes.ToArray(), CancellationToken.None);
        BspData looseMap;
        BspData tightMap;
        await using (MemoryStream again = new(bspBytes.ToArray()))
        {
            looseMap = await BspFile.LoadAsync(again, CancellationToken.None);
        }

        await using (MemoryStream again = new(bspBytes.ToArray()))
        {
            tightMap = await BspFile.LoadAsync(again, CancellationToken.None);
        }

        // One thread on both sides: only there are the tightened counters a
        // property of the map (see TightenedWorkCountersAreTheSameRunAfterRunAtOneThread).
        VisContext one = VisContext.Default with { Parallelism = new CompileParallelism { MaxDegree = 1 } };
        VisResult loose = await Vvis.ComputeAsync(
            looseMap, PortalSet.FromPortalFile(parsed), one with { Options = Untight });
        VisResult tight = await Vvis.ComputeAsync(
            tightMap, PortalSet.FromPortalFile(parsed), one with { Options = Tight });

        using StringWriter output = new();
        int exit = await VvisCommand.RunAsync(
            files, ["-threads", "1", VvisCommand.BenchSwitch, bspPath], output);

        // The no-flag default is the tightened arm.
        Assert.Equal(Program.ExitSuccess, exit);
        Assert.Contains(
            string.Create(CultureInfo.InvariantCulture, $"bench work chains={tight.Work.Chains} "),
            output.ToString(),
            StringComparison.Ordinal);

        // The explicit opt-out is the untightened arm.
        using StringWriter loosened = new();
        int looseExit = await VvisCommand.RunAsync(
            files,
            ["-threads", "1", VvisCommand.LooseSwitch, VvisCommand.BenchSwitch, bspPath],
            loosened);
        Assert.Equal(Program.ExitSuccess, looseExit);
        Assert.Contains(
            string.Create(CultureInfo.InvariantCulture, $"bench work chains={loose.Work.Chains} "),
            loosened.ToString(),
            StringComparison.Ordinal);

        // Both spellings at once is a usage error, not a last-wins: the arms
        // differ by a factor of four in work, so silently picking one would
        // be a guess at intent.
        using StringWriter both = new();
        int bothExit = await VvisCommand.RunAsync(
            files,
            [
                "-threads", "1", VvisCommand.TightenSwitch, VvisCommand.LooseSwitch,
                VvisCommand.BenchSwitch, bspPath,
            ],
            both);
        Assert.Equal(Program.ExitUsage, bothExit);
    }

    [Fact]
    public void TheDefaultRecordTightensAndTheLooseRecordDoesNot()
    {
        // The promotion as a library fact, not just a CLI one: the record a
        // caller gets with `new` is the tightened arm, and the untightened
        // walk stays reachable by spelling Tighten = false.
        Assert.True(VvisOptions.Default.Tighten);
        Assert.False(new VvisOptions { Tighten = false }.Tighten);
    }
}
