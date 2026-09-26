//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Vis;

/// <summary>
/// <see cref="Vvis.ComputeAsync"/> end to end, on maps small enough to reason
/// about without a compiler.
/// </summary>
public class VvisComputeTests
{
    /// <summary>
    /// A straight corridor: cluster 0, a window, cluster 1, a second window in
    /// line with the first, cluster 2.
    /// </summary>
    private static (BspData Map, PortalSet Portals) Corridor()
    {
        BspData map = VisFixture.Map(3);
        PortalFile file = VisFixture.Portals(
            3,
            VisFixture.WindowAtX(0, 1, x: 0f, yMin: 0f, yMax: 16f),
            VisFixture.WindowAtX(1, 2, x: 64f, yMin: 0f, yMax: 16f));

        return (map, PortalSet.FromPortalFile(file));
    }

    private static async Task<VisResult> RunAsync(
        BspData map,
        PortalSet portals,
        VvisOptions? options = null,
        int degree = 1,
        BitVectorPath path = BitVectorPath.Auto)
    {
        VisContext context = new()
        {
            Options = options ?? VvisOptions.Default,
            Parallelism = new CompileParallelism { MaxDegree = degree },
            Path = path,
        };

        return await Vvis.ComputeAsync(map, portals, context, CancellationToken.None);
    }

    [Fact]
    public async Task ACorridorClusterSeesItself()
    {
        (BspData map, PortalSet portals) = Corridor();

        VisResult result = await RunAsync(map, portals);

        Assert.True(result.CanSee(1, 1));
    }

    [Fact]
    public async Task TheTwoEndsOfAStraightCorridorSeeEachOther()
    {
        (BspData map, PortalSet portals) = Corridor();

        VisResult result = await RunAsync(map, portals);

        Assert.True(result.CanSee(0, 2));
        Assert.True(result.CanSee(2, 0));
    }

    [Fact]
    public async Task TheVisibilityLumpIsWrittenIntoTheMap()
    {
        (BspData map, PortalSet portals) = Corridor();

        VisResult result = await RunAsync(map, portals);

        VisibilityLump? lump = VisibilityLump.Read(map[BspLump.Visibility]);
        Assert.NotNull(lump);
        Assert.Equal(3, lump.NumClusters);
        Assert.Equal(result.VisDataSize, map[BspLump.Visibility].Length);
    }

    [Fact]
    public async Task TheLumpDecompressesBackToTheRowsThatWereComputed()
    {
        (BspData map, PortalSet portals) = Corridor();

        VisResult result = await RunAsync(map, portals);

        VisibilityLump lump = VisibilityLump.Read(map[BspLump.Visibility])!;
        ReadOnlySpan<byte> bytes = map[BspLump.Visibility].Data.Span;

        for (int cluster = 0; cluster < result.ClusterCount; cluster++)
        {
            byte[] row = new byte[lump.RowBytes()];
            lump.DecompressRow(bytes[lump.BitOffset(cluster, VisibilityLump.Pvs)..], row);
            Assert.Equal(result.Pvs(cluster).ToArray(), row);
        }
    }

    [Fact]
    public async Task TheLeafMinDistanceToWaterLumpIsOneEntryPerLeaf()
    {
        (BspData map, PortalSet portals) = Corridor();

        await RunAsync(map, portals);

        Assert.Equal(3 * sizeof(ushort), map[BspLump.LeafMinDistToWater].Length);
    }

    [Fact]
    public async Task ALeafThatSeesNoWaterGetsTheNoWaterSentinel()
    {
        (BspData map, PortalSet portals) = Corridor();

        await RunAsync(map, portals);

        ReadOnlySpan<byte> bytes = map[BspLump.LeafMinDistToWater].Data.Span;
        for (int leaf = 0; leaf < 3; leaf++)
        {
            Assert.Equal(
                (ushort)65535,
                System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes[(leaf * 2)..]));
        }
    }

    [Fact]
    public async Task PvsIsSymmetricAfterTheCrosscheck()
    {
        (BspData map, PortalSet portals) = Corridor();

        VisResult result = await RunAsync(map, portals);

        for (int a = 0; a < result.ClusterCount; a++)
        {
            for (int b = 0; b < result.ClusterCount; b++)
            {
                Assert.Equal(result.CanSee(a, b), result.CanSee(b, a));
            }
        }
    }

    [Fact]
    public async Task ThePasContainsThePvs()
    {
        (BspData map, PortalSet portals) = Corridor();

        VisResult result = await RunAsync(map, portals);

        for (int a = 0; a < result.ClusterCount; a++)
        {
            for (int b = 0; b < result.ClusterCount; b++)
            {
                if (result.CanSee(a, b))
                {
                    Assert.True(result.CanHear(a, b), $"{a} sees {b} but cannot hear it");
                }
            }
        }
    }

    [Fact]
    public async Task OneThreadAndThirtyTwoGiveTheSameAnswer()
    {
        // Instrument I4, on a fixture rather than a corpus map: the strongest
        // gate this port has, because it needs no reference at all. Stock fails
        // its own version of this (spike 0c).
        (BspData one, PortalSet portalsOne) = Corridor();
        (BspData many, PortalSet portalsMany) = Corridor();

        await RunAsync(one, portalsOne, degree: 1);
        await RunAsync(many, portalsMany, degree: 32);

        Assert.Equal(
            one[BspLump.Visibility].Data.ToArray(),
            many[BspLump.Visibility].Data.ToArray());
    }

    [Fact]
    public async Task SortedAndUnsortedGiveTheSameAnswer()
    {
        (BspData sorted, PortalSet portalsSorted) = Corridor();
        (BspData unsorted, PortalSet portalsUnsorted) = Corridor();

        await RunAsync(sorted, portalsSorted, degree: 8);
        await RunAsync(unsorted, portalsUnsorted, VvisOptions.Default with { NoSort = true }, degree: 8);

        Assert.Equal(
            sorted[BspLump.Visibility].Data.ToArray(),
            unsorted[BspLump.Visibility].Data.ToArray());
    }

    [Fact]
    public async Task TheScalarAndTheWidestBitVectorPathAgree()
    {
        // The seeded equivalence gate in MapTools.Geometry proves the paths
        // agree on generated input. This proves it on a whole compile, whose
        // vector shapes nobody chose.
        (BspData scalar, PortalSet portalsScalar) = Corridor();
        (BspData auto, PortalSet portalsAuto) = Corridor();

        await RunAsync(scalar, portalsScalar, path: BitVectorPath.Scalar);
        await RunAsync(auto, portalsAuto, path: BitVectorPath.Auto);

        Assert.Equal(
            scalar[BspLump.Visibility].Data.ToArray(),
            auto[BspLump.Visibility].Data.ToArray());
    }

    [Fact]
    public async Task FastIsASupersetOfTheFullAnswer()
    {
        // -fast keeps the flood approximation, which is the ceiling on what the
        // full flow can find. More overdraw, never missing geometry.
        (BspData full, PortalSet portalsFull) = Corridor();
        (BspData fast, PortalSet portalsFast) = Corridor();

        VisResult slow = await RunAsync(full, portalsFull);
        VisResult quick = await RunAsync(fast, portalsFast, VvisOptions.FastDefault);

        for (int a = 0; a < slow.ClusterCount; a++)
        {
            for (int b = 0; b < slow.ClusterCount; b++)
            {
                if (slow.CanSee(a, b))
                {
                    Assert.True(quick.CanSee(a, b), $"-fast lost {a} sees {b}");
                }
            }
        }
    }

    [Fact]
    public async Task AMapWithOneClusterAndNoPortalsSeesOnlyItself()
    {
        BspData map = VisFixture.Map(1);
        PortalSet portals = PortalSet.FromPortalFile(VisFixture.Portals(1));

        VisResult result = await RunAsync(map, portals);

        Assert.Equal(1, result.TotalVisibleClusters);
        Assert.True(result.CanSee(0, 0));
    }

    [Fact]
    public async Task AMapWithNoNodesIsRefused()
    {
        BspData map = VisFixture.Map(1);
        map.SetLump(BspLump.Nodes, ReadOnlyMemory<byte>.Empty);
        PortalSet portals = PortalSet.FromPortalFile(VisFixture.Portals(1));

        await Assert.ThrowsAsync<InvalidBspException>(
            async () => await RunAsync(map, portals));
    }

    [Fact]
    public async Task AnEnvFogControllerFarzTurnsOnRadialVis()
    {
        (BspData map, PortalSet portals) = Corridor();
        map[BspLump.Entities] = EntityLump.Write(
        [
            Entity("worldspawn"),
            Entity("env_fog_controller", ("farz", "2048")),
        ]);

        VisResult result = await RunAsync(map, portals);

        Assert.True(result.UsedRadius);
        Assert.Equal(2048.0 * 2048.0, result.VisRadiusSquared);
    }

    [Fact]
    public async Task AFarzOfZeroMeansNoRadiusAtAll()
    {
        // -- a farz of exactly zero is turned into -1 and then
        // rejected by `flRadius > 0`, so it is "no radius" rather than "a
        // radius of nothing".
        (BspData map, PortalSet portals) = Corridor();
        map[BspLump.Entities] = EntityLump.Write(
        [
            Entity("env_fog_controller", ("farz", "0")),
        ]);

        VisResult result = await RunAsync(map, portals);

        Assert.False(result.UsedRadius);
    }

    [Fact]
    public async Task RadialVisMarksEveryLeaf()
    {
        (BspData map, PortalSet portals) = Corridor();

        await RunAsync(map, portals, VvisOptions.Default with { RadiusOverride = 1024f });

        ReadOnlySpan<DLeaf> leaves = BspStructView.As<DLeaf>(map[BspLump.Leafs]);
        for (int leaf = 0; leaf < leaves.Length; leaf++)
        {
            Assert.True(leaves[leaf].GetFlags().HasFlag(LeafFlags.Radial));
        }
    }

    [Fact]
    public async Task TheOverrideRadiusIsSquaredInDouble()
    {
        // reads it with atof into a double and squares in
        // double; the map's own farz arrives as a float and is squared in
        // float. The two paths are not interchangeable and this pins the one
        // the option record takes.
        (BspData map, PortalSet portals) = Corridor();

        VisResult result = await RunAsync(
            map, portals, VvisOptions.Default with { RadiusOverride = 4096.5f });

        Assert.Equal(4096.5 * 4096.5, result.VisRadiusSquared);
    }

    [Fact]
    public async Task ARadiusSmallEnoughToCullEverythingLosesTheFarCluster()
    {
        // The radius test is on portal-to-portal distance, so a radius smaller
        // than the corridor removes the far end from the near end's PVS. If it
        // did not, -radius_override would be doing nothing.
        (BspData map, PortalSet portals) = Corridor();

        VisResult result = await RunAsync(
            map, portals, VvisOptions.Default with { RadiusOverride = 16f });

        Assert.False(result.CanSee(0, 2));
    }

    [Fact]
    public async Task APreCancelledTokenDoesNoWork()
    {
        (BspData map, PortalSet portals) = Corridor();
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Vvis.ComputeAsync(map, portals, VisContext.Default, cancelled.Token));

        Assert.True(map[BspLump.Visibility].IsEmpty);
    }

    [Fact]
    public async Task ATraceRunWritesNoVisibilityLump()
    {
        // -- a trace skips the whole write path.
        (BspData map, PortalSet portals) = Corridor();

        VisResult result = await RunAsync(
            map, portals, VvisOptions.Default with { Trace = (0, 2) });

        Assert.True(map[BspLump.Visibility].IsEmpty);
        Assert.NotNull(result.Trace);
    }

    [Fact]
    public async Task ATraceBetweenClustersThatCannotSeeEachOtherFindsNothing()
    {
        BspData map = VisFixture.Map(2);
        PortalSet portals = PortalSet.FromPortalFile(VisFixture.Portals(2));

        VisResult result = await RunAsync(
            map, portals, VvisOptions.Default with { Trace = (0, 1) });

        Assert.Null(result.Trace);
    }

    [Fact]
    public async Task ATraceOutsideTheClusterRangeIsRefused()
    {
        (BspData map, PortalSet portals) = Corridor();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await RunAsync(map, portals, VvisOptions.Default with { Trace = (0, 99) }));
    }

    [Fact]
    public async Task TwoConsecutiveRunsProduceTheSameLump()
    {
        (BspData first, PortalSet portalsFirst) = Corridor();
        (BspData second, PortalSet portalsSecond) = Corridor();

        await RunAsync(first, portalsFirst, degree: 4);
        await RunAsync(second, portalsSecond, degree: 4);

        Assert.Equal(
            first[BspLump.Visibility].Data.ToArray(),
            second[BspLump.Visibility].Data.ToArray());
    }

    [Fact]
    public async Task TheFlowReportsHowManyChainsItWalked()
    {
        (BspData map, PortalSet portals) = Corridor();

        VisResult result = await RunAsync(map, portals);

        Assert.True(result.Work.Chains > 0, "a corridor that sees end to end walked no chains");
    }

    [Fact]
    public async Task TheFlowReportsHowManyCandidatesPassedTheMightSeeTest()
    {
        (BspData map, PortalSet portals) = Corridor();

        VisResult result = await RunAsync(map, portals);

        Assert.True(result.Work.Candidates > 0);
    }

    [Fact]
    public async Task OneThreadAndThirtyTwoDoTheSameAmountOfWork()
    {
        // The counters are a SECOND reading of I4: the bytes agreeing says the
        // answers match, and this says the two runs got there by the same
        // route. An optimisation that changed how much the flow explores would
        // move these even where it happened not to move a bit.
        (BspData one, PortalSet portalsOne) = Corridor();
        (BspData many, PortalSet portalsMany) = Corridor();

        // The untightened arm, the one whose counters are schedule-invariant
        // by construction; the tightened walk's counters move with the
        // schedule (speculative re-walks) while its bytes do not, which
        // VvisTightenTests pins separately.
        VisResult single = await RunAsync(one, portalsOne, VvisOptions.Untightened, degree: 1);
        VisResult parallel = await RunAsync(many, portalsMany, VvisOptions.Untightened, degree: 32);

        Assert.Equal(single.Work, parallel.Work);
    }

    [Fact]
    public async Task AFastRunReportsNoFlowWorkBecauseItRanNone()
    {
        (BspData map, PortalSet portals) = Corridor();

        VisResult result = await RunAsync(map, portals, VvisOptions.FastDefault);

        // Flow skipped, base pass NOT: -fast copies portalflood into portalvis
        // and PortalRunVis never runs, but BasePortalVis is
        // dispatched unconditionally. The flow counters are
        // zero; the base cast counter is not.
        Assert.Equal(0L, result.Work.Chains);
        Assert.Equal(0L, result.Work.Candidates);
        Assert.Equal(0L, result.Work.SeparatorClips);
        Assert.True(result.Work.BaseRays > 0, "-fast must not skip the base pass");
    }

    /// <summary>
    /// The anomaly-3 gate. P12 measured "managed -fast still casts
    /// BasePortalVis rays that stock skips" and recommended gating the base
    /// pass under <c>Fast</c>. Stock does not skip it — its own -fast log
    /// prints the BasePortalVis pacifier, and the reference binary dispatches
    /// it before the fastvis test, outside both branches of
    /// <c>CalcPortalVis</c>. So the enforced invariant is that -fast casts
    /// EXACTLY the default's rays, and the bug this fact kills is the naive
    /// "skip the base pass under -fast" that the anomaly report would have
    /// shipped: it zeroes this counter and diverges from stock's bytes.
    /// </summary>
    [Fact]
    public async Task FastCastsExactlyTheDefaultBaseRays()
    {
        (BspData map, PortalSet portals) = Corridor();
        long expected = portals.Count * (long)(portals.Count - 1);

        VisResult fast = await RunAsync(map, portals, VvisOptions.FastDefault);
        VisResult full = await RunAsync(map, portals);

        Assert.True(full.Work.BaseRays > 0, "the default run must cast");
        Assert.Equal(expected, full.Work.BaseRays);
        Assert.Equal(full.Work.BaseRays, fast.Work.BaseRays);
    }

    /// <summary>
    /// Every remaining option either leaves the base-pass ray count at the
    /// full cross product or does not reach it at all — there is no managed
    /// flag combo under which any base test is skipped, which is the shape of
    /// the one-flag-one-branch binary (fastvis is read at one place,
    /// and <c>-trace</c> never reads it).
    /// </summary>
    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, false, false)]
    public async Task NoFlagComboEverSkipsBaseWork(bool fast, bool noSort, bool tighten, bool trace)
    {
        (BspData map, PortalSet portals) = Corridor();
        VvisOptions options = new()
        {
            Fast = fast,
            NoSort = noSort,
            Tighten = tighten,
            Trace = trace ? (0, 2) : null,
        };

        VisResult result = await RunAsync(map, portals, options);

        Assert.Equal(portals.Count * (long)(portals.Count - 1), result.Work.BaseRays);
    }

    /// <summary>
    /// -fast does skip what stock skips: the flow's own work counters go to
    /// zero while the base pass stays whole, and -tighten never runs under
    /// -fast (stock has no such combo; the managed Fast branch wins exactly as
    /// the flag table records).
    /// </summary>
    [Fact]
    public async Task FastSkipsFlowEvenTightenAndSortAreMoot()
    {
        (BspData map, PortalSet portals) = Corridor();
        VisResult fast = await RunAsync(map, portals, VvisOptions.FastDefault);
        VisResult tightenedFast = await RunAsync(
            map, portals, new VvisOptions { Fast = true, Tighten = true, NoSort = true });
        VisResult tightened = await RunAsync(map, portals, new VvisOptions { Tighten = true });

        Assert.True(tightened.Work.Chains > 0, "the tightened flow must run");
        Assert.Equal(0L, fast.Work.Chains);
        Assert.Equal(0L, tightenedFast.Work.Chains);
        Assert.Equal(VisWorkCounters.Zero + new VisWorkCounters(0, 0, 0, fast.Work.BaseRays),
            tightenedFast.Work);
    }

    /// <summary>
    /// Wall-time sanity, bounded loosely on purpose: the ray counters are the
    /// gate; this only catches a -fast run that somehow takes longer than the
    /// full compile it is supposed to shortcut.
    /// </summary>
    [Fact]
    public async Task AFastRunIsNotSlowerThanTheFullOne()
    {
        (BspData map, PortalSet portals) = Corridor();

        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        await RunAsync(map, portals, VvisOptions.FastDefault);
        long fastMs = watch.ElapsedMilliseconds;

        Assert.True(fastMs < 30_000, $"-fast took {fastMs} ms on a three-cluster corridor");
    }

    [Fact]
    public void CountersFromTwoWorkersAddFieldByField()
    {
        VisWorkCounters left = new(Chains: 3, Candidates: 5, SeparatorClips: 7, BaseRays: 9);
        VisWorkCounters right = new(Chains: 11, Candidates: 13, SeparatorClips: 17, BaseRays: 21);

        Assert.Equal(new VisWorkCounters(14, 18, 24, 30), left + right);
    }

    [Fact]
    public void TheNamedAddAgreesWithTheOperator()
    {
        VisWorkCounters left = new(Chains: 3, Candidates: 5, SeparatorClips: 7, BaseRays: 9);
        VisWorkCounters right = new(Chains: 11, Candidates: 13, SeparatorClips: 17, BaseRays: 21);

        Assert.Equal(left + right, VisWorkCounters.Add(left, right));
    }

    private static BspEntity Entity(string className, params (string Key, string Value)[] pairs)
    {
        BspEntity entity = new();
        entity.Pairs.Add(new BspKeyValue("classname", className));
        foreach ((string key, string value) in pairs)
        {
            entity.Pairs.Add(new BspKeyValue(key, value));
        }

        return entity;
    }
}
