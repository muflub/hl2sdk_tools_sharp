//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Security.Cryptography;

using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;

using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Vis;

/// <summary>
/// <see cref="VvisOptions.FastFlow"/> (<c>-fastflow</c>) on the windowed grid
/// of <see cref="VvisTightenTests"/>.
/// </summary>
/// <remarks>
/// <para>
/// The fast flow is knowingly not the exact answer, so these facts pin what
/// it IS instead: off by default and then the exact walk's bytes; the same
/// bytes at every degree and run after run; and fixed containments against
/// the exact PVS -- the shipped (truncated) arm is a SUBSET of it, with every
/// cluster still seeing itself and its neighbours, and the two arms it was
/// chosen against are SUPERSETS, the cluster-granular one of the
/// conservative one.
/// </para>
/// <para>
/// The grid is where the stop has something to cut: 100 rooms with offset
/// windows, deep flows, and ranks that depend on each other. Its walks are
/// all shorter than the shipped step threshold, so most facts cut from the
/// first step (<see cref="VisContext.FastFlowMinChains"/> zero) and one
/// checks the threshold itself.
/// </para>
/// </remarks>
public class VvisFastFlowTests
{
    private static readonly VvisOptions Exact = new();
    private static readonly VvisOptions Fast = new() { FastFlow = true };

    private static async Task<(BspData Map, VisResult Result)> RunAsync(
        VvisOptions options,
        int degree,
        VisFastFlowFilter filter = VisFastFlowFilter.Truncated,
        int minChains = 0)
    {
        (BspData map, PortalSet portals) = VvisTightenTests.Grid();
        VisContext context = new()
        {
            Options = options,
            Parallelism = new CompileParallelism { MaxDegree = degree },
            FastFlowFilter = filter,
            FastFlowMinChains = minChains,
        };

        VisResult result = await Vvis.ComputeAsync(map, portals, context, CancellationToken.None);
        return (map, result);
    }

    private static byte[] Lump(BspData map) => map[BspLump.Visibility].Data.ToArray();

    /// <summary>Asserts every PVS and PAS bit of <paramref name="inner"/> is in <paramref name="outer"/>.</summary>
    private static void AssertContained(VisResult inner, VisResult outer, string what)
    {
        Assert.Equal(inner.ClusterCount, outer.ClusterCount);
        for (int a = 0; a < inner.ClusterCount; a++)
        {
            for (int b = 0; b < inner.ClusterCount; b++)
            {
                Assert.False(inner.CanSee(a, b) && !outer.CanSee(a, b), $"{what}: {a} sees {b} only in the inner arm");
                Assert.False(inner.CanHear(a, b) && !outer.CanHear(a, b), $"{what}: {a} hears {b} only in the inner arm");
            }
        }
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

    // ---- off by default --------------------------------------------------------

    [Fact]
    public void TheFastFlowIsOffByDefault()
    {
        Assert.False(VvisOptions.Default.FastFlow);
        Assert.False(VvisOptions.Untightened.FastFlow);
        Assert.False(VvisOptions.FastDefault.FastFlow);
        Assert.Equal(VisFastFlowFilter.Truncated, VisContext.Default.FastFlowFilter);
        Assert.Equal(VisClusterStop.DefaultMinChains, VisContext.Default.FastFlowMinChains);
    }

    [Fact]
    public async Task OffTheGridLumpIsTheExactWalksBytes()
    {
        // The exact walk's lump on this grid, as main wrote it before the
        // fast flow existed. Pinned so the few lines the fast flow touches
        // on the exact path (the cover test, the stop's cut) provably cost
        // the default nothing, whatever the filter knob says.
        (BspData map, _) = await RunAsync(Exact, degree: 4, VisFastFlowFilter.Conservative);

        Assert.Equal(ExactGridLumpSha256, Convert.ToHexStringLower(SHA256.HashData(Lump(map))));
    }

    /// <summary>SHA-256 of the default walk's visibility lump on the grid, captured on main.</summary>
    internal const string ExactGridLumpSha256 = "efc50bdfbea380ce218c869cfe3927070947d75f9ac8e624e5e6fed1cf029e41";

    // ---- deterministic ------------------------------------------------------------

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(32)]
    public async Task TheFastLumpIsTheSameAtAnyDegree(int degree)
    {
        (BspData one, _) = await RunAsync(Fast, degree: 1);
        (BspData other, _) = await RunAsync(Fast, degree);

        Assert.Equal(Lump(one), Lump(other));
    }

    [Fact]
    public async Task TheFastLumpIsTheSameRunAfterRun()
    {
        (BspData first, _) = await RunAsync(Fast, degree: 8);
        for (int run = 0; run < 4; run++)
        {
            (BspData again, _) = await RunAsync(Fast, degree: 8);
            Assert.Equal(Lump(first), Lump(again));
        }
    }

    [Fact]
    public async Task TheFastWorkIsTheSameAtEveryDegree()
    {
        // Stronger than the lump: each portal is flowed exactly once, whole,
        // with every neighbour it reads finished, so even the work counters
        // are a property of the map -- unlike the exact walk's, which
        // re-walk speculative runs at more than one thread.
        (_, VisResult one) = await RunAsync(Fast, degree: 1);
        (_, VisResult many) = await RunAsync(Fast, degree: 16);

        Assert.Equal(one.Work, many.Work);
    }

    [Theory]
    [InlineData((int)VisFastFlowFilter.Conservative)]
    [InlineData((int)VisFastFlowFilter.ClusterGranular)]
    public async Task EveryArmIsTheSameAtOneThreadAndSixteen(int arm)
    {
        // The enum is internal, so the theory takes its value.
        VisFastFlowFilter filter = (VisFastFlowFilter)arm;
        (BspData one, _) = await RunAsync(Fast, degree: 1, filter);
        (BspData many, _) = await RunAsync(Fast, degree: 16, filter);

        Assert.Equal(Lump(one), Lump(many));
    }

    // ---- what it computes -------------------------------------------------------

    [Fact]
    public async Task UnderTheShippedThresholdTheGridsShortWalksAreExact()
    {
        // Every grid walk is shorter than the threshold, so no walk may cut:
        // the answer is the exact one, lump for lump. This is the threshold
        // doing its job -- the cheap portals everything else prunes with
        // stay exact.
        (BspData exact, _) = await RunAsync(Exact, degree: 2);
        (BspData fast, VisResult result) = await RunAsync(Fast, degree: 2, minChains: VisClusterStop.DefaultMinChains);

        Assert.Equal(Lump(exact), Lump(fast));
        Assert.True(result.Work.Chains > 0);
    }

    [Fact]
    public async Task TheFastFlowCutsWork()
    {
        (_, VisResult exact) = await RunAsync(Exact, degree: 1);
        (_, VisResult fast) = await RunAsync(Fast, degree: 1);

        Assert.True(
            fast.Work.Chains < exact.Work.Chains,
            $"-fastflow walked {fast.Work.Chains} chains against the exact {exact.Work.Chains}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(VisClusterStop.DefaultMinChains)]
    public async Task TheFastPvsIsInsideTheExactOne(int minChains)
    {
        // A cut-short vector lacks portals behind its cuts, so the portals
        // ranked above prune chains the exact flow keeps: the fast PVS may
        // lose clusters, and never gains one. Whatever the threshold.
        (_, VisResult exact) = await RunAsync(Exact, degree: 4);
        (_, VisResult fast) = await RunAsync(Fast, degree: 4, minChains: minChains);

        AssertContained(fast, exact, "fast inside exact");
    }

    [Fact]
    public async Task TheArmsItWasChosenAgainstContainTheExactPvsInTurn()
    {
        // Publishing everything a cut could have reached keeps every portal
        // the exact walk could mark, so nothing prunes a chain the exact flow
        // keeps (a superset); publishing every flood portal into a seen
        // cluster prunes less again (a superset of that).
        (_, VisResult exact) = await RunAsync(Exact, degree: 4);
        (_, VisResult conservative) = await RunAsync(Fast, degree: 4, VisFastFlowFilter.Conservative);
        (_, VisResult cluster) = await RunAsync(Fast, degree: 4, VisFastFlowFilter.ClusterGranular);

        AssertContained(exact, conservative, "exact inside conservative");
        AssertContained(conservative, cluster, "conservative inside cluster-granular");
    }

    [Fact]
    public async Task EveryClusterStillSeesItselfAndItsNeighbours()
    {
        (_, VisResult fast) = await RunAsync(Fast, degree: 4);
        PortalSet portals = VvisTightenTests.Grid().Portals;

        for (int cluster = 0; cluster < fast.ClusterCount; cluster++)
        {
            Assert.True(fast.CanSee(cluster, cluster), $"{cluster} does not see itself");
            foreach (int portal in portals.ClusterPortals(cluster))
            {
                Assert.True(fast.CanSee(cluster, portals.Leaf(portal)), $"{cluster} does not see its neighbour {portals.Leaf(portal)}");
            }
        }
    }

    [Fact]
    public async Task TheFastFlowIsNotTheExactAnswerOnTheGrid()
    {
        // The flag's warning is not decoration: on this grid the fast PVS
        // differs from the exact one (it is smaller). If it ever came out
        // equal, the containment facts would pass on a stop that never
        // engaged.
        (_, VisResult exact) = await RunAsync(Exact, degree: 1);
        (_, VisResult fast) = await RunAsync(Fast, degree: 1);

        Assert.True(CountPvs(fast) < CountPvs(exact), "the fast flow lost no cluster on the grid");
    }

    [Fact]
    public async Task OnTheLooseWalkTheStopIsExact()
    {
        // The untightened walk never reads another portal's vector, so what a
        // portal publishes does not matter and the stop, which only abandons
        // subtrees that could reach no new cluster, gives the exact answer.
        VvisOptions loose = new() { Tighten = false };
        (BspData exact, VisResult exactResult) = await RunAsync(loose, degree: 4);
        (BspData fast, VisResult fastResult) = await RunAsync(loose with { FastFlow = true }, degree: 4);

        Assert.Equal(Lump(exact), Lump(fast));
        Assert.True(fastResult.Work.Chains < exactResult.Work.Chains);
    }

    [Fact]
    public async Task StockFastWinsOverTheFastFlow()
    {
        // -fast skips the flow altogether; -fastflow has nothing to shorten.
        (BspData fast, _) = await RunAsync(VvisOptions.FastDefault, degree: 2);
        (BspData both, _) = await RunAsync(VvisOptions.FastDefault with { FastFlow = true }, degree: 2);

        Assert.Equal(Lump(fast), Lump(both));
    }

    // ---- the warning -------------------------------------------------------------

    [Fact]
    public void OnlyTheFastFlowEarnsAWarning()
    {
        Assert.Empty(Vvis.OptionWarnings(VvisOptions.Default));
        Assert.Empty(Vvis.OptionWarnings(VvisOptions.Untightened));

        // -fast skips the flow the flag would shorten: nothing to warn of.
        Assert.Empty(Vvis.OptionWarnings(VvisOptions.FastDefault with { FastFlow = true }));

        CompileDiagnostic warning = Assert.Single(Vvis.OptionWarnings(Fast));
        Assert.Equal(VvisCodes.ApproximateFlow, warning.Code);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal(Vvis.FastFlowWarning, warning.Message);
        Assert.DoesNotContain('\n', warning.Message);
        Assert.Contains("approximate", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionWarningsRefusesNull() =>
        Assert.Throws<ArgumentNullException>(() => Vvis.OptionWarnings(null!));

    [Fact]
    public async Task SsmapVvisPrintsTheWarningOnceAndOnlyWithTheFlag()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ssmap-fastflow"));
        string bspPath = Path.Combine(root, "grid.bsp");
        string prtPath = Path.Combine(root, "grid.prt");

        using MemoryStream bspBytes = new();
        await BspFile.SaveAsync(VisFixture.Map(100), bspBytes, CancellationToken.None);
        using MemoryStream prtBytes = new();
        await VvisTightenTests.GridFile().WriteAsync(prtBytes, cancellationToken: CancellationToken.None);

        async Task<(int Exit, string Output, byte[] Bsp)> RunCommandAsync(params string[] flags)
        {
            InMemoryFileSystem files = new InMemoryFileSystem()
                .AddFile(bspPath, bspBytes.ToArray())
                .AddFile(prtPath, prtBytes.ToArray());
            using StringWriter output = new();
            int exit = await VvisCommand.RunAsync(files, [.. flags, "-threads", "2", bspPath], output);
            byte[] written = files.GetBytes(VPath.Create(bspPath))!;
            return (exit, output.ToString(), written);
        }

        (int exactExit, string exactOutput, byte[] exact) = await RunCommandAsync();
        (int fastExit, string fastOutput, byte[] fast) = await RunCommandAsync("-fastflow");

        Assert.Equal(Program.ExitSuccess, exactExit);
        Assert.Equal(Program.ExitSuccess, fastExit);
        Assert.DoesNotContain(VvisCodes.ApproximateFlow, exactOutput, StringComparison.Ordinal);
        string line = $"Warning {VvisCodes.ApproximateFlow}: {Vvis.FastFlowWarning}";
        Assert.Single(fastOutput.Split('\n'), l => l.TrimEnd('\r') == line);

        // And the command wrote what the library computes for the flag (on
        // the grid that is the exact lump: its walks are all shorter than the
        // shipped threshold, see UnderTheShippedThresholdTheGridsShortWalksAreExact).
        (BspData library, _) = await RunAsync(Fast, degree: 2, minChains: VisClusterStop.DefaultMinChains);
        BspData written;
        await using (MemoryStream again = new(fast))
        {
            written = await BspFile.LoadAsync(again, CancellationToken.None);
        }

        Assert.Equal(Lump(library), Lump(written));
        Assert.Equal(exact.Length, fast.Length);
    }

    // ---- the schedule -------------------------------------------------------------

    private static readonly WorkerContext Worker = new(0, 1 << 20, CancellationToken.None);

    private static VisPortalState FloodedGrid(PortalSet portals)
    {
        VisPortalState state = new(portals.Count);
        VisBaseFlow baseFlow = new(portals, state, useRadius: false, radiusSquared: 0.0);
        VisFloodScratch scratch = new(portals.Count);
        for (int p = 0; p < portals.Count; p++)
        {
            baseFlow.Run(p, scratch, Worker);
        }

        return state;
    }

    [Fact]
    public async Task ARunWholeScheduleDefersAClaimUntilItsNeighboursFinish()
    {
        // The lowest rank's claim is held until some other claim has been
        // put back for want of a finished neighbour, so the deferral path is
        // taken on every run of this fact; the per-portal vectors must still
        // be the one-thread ones.
        PortalSet portals = VvisTightenTests.Grid().Portals;
        VisClusterStop stop = new(portals, minChains: 0);

        VisPortalState reference = FloodedGrid(portals);
        VisTightening oneThread = new(reference, whole: true);
        using (WorkQueue one = new(new CompileParallelism { MaxDegree = 1 }))
        {
            await oneThread.RunAsync(
                one, new VisPortalFlow?[1], () => new VisPortalFlow(portals, reference, BitVectorPath.Auto, stop: stop),
                progress: null, CancellationToken.None);
        }

        Assert.Equal(0, oneThread.Deferrals);

        VisPortalState state = FloodedGrid(portals);
        bool deferredInTime = false;
        VisTightening tightening = null!;
        tightening = new VisTightening(state, whole: true)
        {
            Window = 4096,
            ClaimProbe = rank =>
            {
                if (rank == 0)
                {
                    deferredInTime = SpinWait.SpinUntil(
                        () => Volatile.Read(ref tightening) is { Deferrals: > 0 }, TimeSpan.FromSeconds(30));
                }
            },
        };
        using WorkQueue queue = new(new CompileParallelism { MaxDegree = 4 });
        await tightening.RunAsync(
            queue, new VisPortalFlow?[queue.Degree], () => new VisPortalFlow(portals, state, BitVectorPath.Auto, stop: stop),
            progress: null, CancellationToken.None);

        Assert.True(deferredInTime, "no claim was deferred while the lowest rank was held");
        Assert.Equal(0, tightening.SpeculativeRuns);
        Assert.Equal(0, tightening.Splits);
        for (int p = 0; p < portals.Count; p++)
        {
            Assert.Equal(reference.Vis(p).ToArray(), state.Vis(p).ToArray());
        }
    }

    [Fact]
    public void AFastWalkRefusesToSpeculateOrSplit()
    {
        // Its cover is one worker's and depends on the order it met
        // clusters in, so a run that speculates or is shared would publish a
        // schedule-dependent vector.
        PortalSet portals = VvisTightenTests.Grid().Portals;
        VisPortalState state = FloodedGrid(portals);
        VisTightening ranking = new(state);
        int[] rank = new int[portals.Count];
        for (int p = 0; p < portals.Count; p++)
        {
            rank[p] = ranking.RankOf(p);
        }

        VisPortalFlow flow = new(portals, state, BitVectorPath.Auto, stop: new VisClusterStop(portals));
        flow.UseTightening(rank, new VisSpeculativeReads(state.Words, () => new ulong[state.Words]));

        Assert.Throws<InvalidOperationException>(() => flow.Run(
            ranking.PortalAt(0), 0, new VisRepairTree(_ => { }, VisRepairTree.DefaultLevels), splitter: null, Worker));
        Assert.Throws<InvalidOperationException>(() => flow.Run(
            ranking.PortalAt(0), 0, tree: null, splitter: ranking, Worker));

        // Whole and unshared, it runs.
        flow.Run(ranking.PortalAt(0), 0, tree: null, splitter: null, Worker);
    }

    [Fact]
    public void TheIntoTableListsEachPortalUnderTheClusterItLeadsInto()
    {
        PortalSet portals = VvisTightenTests.Grid().Portals;
        VisClusterStop stop = new(portals, VisFastFlowFilter.Conservative, minChains: 7);

        Assert.Equal(VisFastFlowFilter.Conservative, stop.Filter);
        Assert.Equal(7, stop.MinChains);
        Assert.Equal(VisClusterStop.DefaultMinChains, new VisClusterStop(portals).MinChains);
        Assert.Throws<ArgumentOutOfRangeException>(() => new VisClusterStop(portals, minChains: -1));
        Assert.Throws<ArgumentNullException>(() => new VisClusterStop(null!));
        Assert.Equal(portals.ClusterCount, stop.ClusterCount);
        int listed = 0;
        for (int cluster = 0; cluster < portals.ClusterCount; cluster++)
        {
            int previous = -1;
            foreach (int portal in stop.PortalsInto(cluster))
            {
                Assert.Equal(cluster, portals.Leaf(portal));
                Assert.True(portal > previous);
                previous = portal;
                listed++;
            }
        }

        Assert.Equal(portals.Count, listed);
    }
}
