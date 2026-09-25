using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapGen;
using SourceSharp.MapGen.Catalog;

using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Validation;
using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Vis;

/// <summary>
/// The catalogue's vvis expectations, against managed vvis running on STOCK
/// vbsp's output (plan_maptools.md I3).
/// </summary>
/// <remarks>
/// <para>
/// The counts here were MEASURED by compiling every entry with stock, not
/// reasoned about: phase 1j found two declarations that were wrong the other
/// way -- a sealed room is four clusters because vbsp cuts on a 1024-unit block
/// grid first -- and a correct vvis judged against the reasoned numbers would
/// have looked broken.
/// </para>
/// <para>
/// A probe is a POINT, not a cluster number, which is what makes these
/// expectations survive a recompile: the assertion is "the cluster containing A
/// sees the cluster containing B", and cluster numbering is an output.
/// </para>
/// </remarks>
public class VvisCatalogueTests
{
    /// <summary>Every entry that declares something vvis can be held to.</summary>
    public static TheoryData<string> EntriesWithVisExpectations
    {
        get
        {
            TheoryData<string> data = [];
            foreach (CatalogEntry entry in TestMapCatalog.All)
            {
                MapObservables declared = entry.Observables;
                if (declared.Clusters is not null ||
                    declared.Portals is not null ||
                    declared.MustSee.Count > 0 ||
                    declared.MustNotSee.Count > 0)
                {
                    data.Add(entry.Name);
                }
            }

            return data;
        }
    }

    [StockCatalogueTheory]
    [MemberData(nameof(EntriesWithVisExpectations))]
    public async Task ThePortalFileHasTheDeclaredClusterAndPortalCounts(string name)
    {
        CatalogEntry entry = TestMapCatalog.Named(name);
        string? bsp = StockCatalogue.BspFor(name);
        if (bsp is null)
        {
            // Not a skip: the catalogue directory IS there and this entry is
            // missing from it, which means the stock compile did not cover it.
            // l1_leak is the one entry that legitimately has no .prt, and it
            // declares no vvis expectations, so it never reaches here.
            Assert.Fail($"{name} has no compiled .bsp + .prt in {StockCatalogue.Directory}");
            return;
        }

        PortalFile portals = await ReadPortalsAsync(bsp);

        if (entry.Observables.Clusters is CountRange clusters)
        {
            Assert.True(
                clusters.Contains(portals.ClusterCount),
                $"{name}: declared {clusters} clusters, .prt says {portals.ClusterCount}");
        }

        if (entry.Observables.Portals is CountRange declared)
        {
            Assert.True(
                declared.Contains(portals.Portals.Count),
                $"{name}: declared {declared} portals, .prt says {portals.Portals.Count}");
        }
    }

    [StockCatalogueTheory]
    [MemberData(nameof(EntriesWithVisExpectations))]
    public async Task EveryMustSeePairCanSeeEachOther(string name)
    {
        CatalogEntry entry = TestMapCatalog.Named(name);
        if (entry.Observables.MustSee.Count == 0)
        {
            return;
        }

        (BspData map, VisResult result) = await ViseAsync(name);

        foreach (ProbePair pair in entry.Observables.MustSee)
        {
            IReadOnlyList<int> from = ClustersOf(map, entry, pair.From);
            IReadOnlyList<int> to = ClustersOf(map, entry, pair.To);

            bool anySees = from.Any(a => to.Any(b => result.CanSee(a, b)));

            Assert.True(
                anySees,
                $"{name}: {pair.From} (clusters {string.Join(',', from)}) cannot see "
                + $"{pair.To} (clusters {string.Join(',', to)})");
        }
    }

    [StockCatalogueTheory]
    [MemberData(nameof(EntriesWithVisExpectations))]
    public async Task NoMustNotSeePairCanSeeEachOther(string name)
    {
        // The half that can actually fail. A vvis that says everything sees
        // everything passes every must-see expectation there is.
        CatalogEntry entry = TestMapCatalog.Named(name);
        if (entry.Observables.MustNotSee.Count == 0)
        {
            return;
        }

        (BspData map, VisResult result) = await ViseAsync(name);

        foreach (ProbePair pair in entry.Observables.MustNotSee)
        {
            IReadOnlyList<int> from = ClustersOf(map, entry, pair.From);
            IReadOnlyList<int> to = ClustersOf(map, entry, pair.To);

            // EVERY pair, not any: a place that must not be visible must not be
            // visible from any cluster the probe stands in.
            foreach (int a in from)
            {
                foreach (int b in to)
                {
                    Assert.False(
                        result.CanSee(a, b),
                        $"{name}: {pair.From} (cluster {a}) can see {pair.To} (cluster {b}) "
                        + "and must not");
                }
            }
        }
    }

    [StockCatalogueTheory]
    [MemberData(nameof(EntriesWithVisExpectations))]
    public async Task EveryProbeLandsInARealCluster(string name)
    {
        // A probe that fell into solid would read as cluster -1, and every
        // must-see and must-not-see assertion about it would then be answering
        // a question about the wrong place. This is the check that keeps the
        // two above honest.
        CatalogEntry entry = TestMapCatalog.Named(name);
        if (entry.Observables.Probes.Count == 0)
        {
            return;
        }

        (BspData map, VisResult result) = await ViseAsync(name);

        foreach (VisProbe probe in entry.Observables.Probes)
        {
            int cluster = VisFixture.ClusterAt(map, ToVec3(probe.At));
            Assert.True(
                cluster >= 0 && cluster < result.ClusterCount,
                $"{name}: probe {probe.Name} at {probe.At} is in cluster {cluster}");

            IReadOnlyList<int> clusters = VisFixture.ClustersAt(map, ToVec3(probe.At));
            Assert.Contains(cluster, clusters);
        }
    }

    [StockCatalogueFact]
    public async Task SomeCatalogueProbeSitsOnAClusterBoundary()
    {
        // The finding this gate's set-valued probes exist for, pinned so it
        // cannot quietly stop being true: the catalogue's probes stand at
        // y = 0, which is exactly where vbsp's 1024-unit block grid cuts. If
        // this ever reports no boundary probes, the probes have moved and the
        // set-valued reading above should be reconsidered -- it would then be
        // doing nothing, which is the shape of a check that cannot fail.
        (BspData map, _) = await ViseAsync("l1_long_corridor");
        CatalogEntry entry = TestMapCatalog.Named("l1_long_corridor");

        int onABoundary = entry.Observables.Probes.Count(
            p => VisFixture.ClustersAt(map, ToVec3(p.At)).Count > 1);

        Assert.True(onABoundary > 0, "no probe in l1_long_corridor sits on a cluster boundary");
    }

    [StockCatalogueTheory]
    [MemberData(nameof(EntriesWithVisExpectations))]
    public async Task OneThreadAndThirtyTwoWriteTheSameLump(string name)
    {
        // Instrument I4 on real geometry. Stock does NOT have this property --
        // spike 0c measured 2 PVS and 25 PAS clusters moving between -threads 1
        // and -threads 16 on 2fort -- and having it is the reason this port
        // does not make stock's opportunistic pruning read.
        (BspData one, _) = await ViseAsync(name, degree: 1);
        (BspData many, _) = await ViseAsync(name, degree: 32);

        Assert.Equal(
            one[BspLump.Visibility].Data.ToArray(),
            many[BspLump.Visibility].Data.ToArray());
    }

    [StockCatalogueTheory]
    [MemberData(nameof(EntriesWithVisExpectations))]
    public async Task SortedAndUnsortedWriteTheSameLump(string name)
    {
        (BspData sorted, _) = await ViseAsync(name, degree: 8);
        (BspData unsorted, _) = await ViseAsync(
            name, degree: 8, options: VvisOptions.Default with { NoSort = true });

        Assert.Equal(
            sorted[BspLump.Visibility].Data.ToArray(),
            unsorted[BspLump.Visibility].Data.ToArray());
    }

    [StockCatalogueTheory]
    [MemberData(nameof(EntriesWithVisExpectations))]
    public async Task TheVisedMapPassesTheEngineLoaderRules(string name)
    {
        // Instrument I1 on the map managed vvis produced, not on the one stock
        // vbsp produced: a visibility lump whose offsets or cluster count are
        // wrong is exactly what the loader rejects.
        (BspData map, _) = await ViseAsync(name);

        ValidationReport report = await BspValidator.CheckAsync(map, CancellationToken.None);

        Assert.Empty(report.Diagnostics.Where(
            d => d.Severity == SourceSharp.MapTools.Diagnostics.DiagnosticSeverity.Error));
    }

    [StockCatalogueTheory]
    [MemberData(nameof(EntriesWithVisExpectations))]
    public async Task ManagedVisibilityContainsStocksAtOneThreadSorted(string name)
    {
        // Instrument I2, with the (threads, sort) pinned -- which spike 0c
        // showed is the only well-formed way to compare with stock, because
        // stock's own answer moves with both.
        //
        // CONTAINMENT, not equality, and the direction is the claim: this port
        // prunes with portalflood where stock prunes with a neighbour's
        // finished portalvis when it happens to have one, and portalvis is
        // always a subset of portalflood. So the managed answer can only ever
        // be MORE conservative. A bit stock has and this does not would mean a
        // sight line was lost, which is the failure that makes geometry vanish
        // at runtime.
        string? reference = StockCatalogue.StockVisBspFor(name);
        if (reference is null)
        {
            Assert.Fail(
                $"{name} has no {name}.stockvis.bsp in {StockCatalogue.Directory}; "
                + "see StockCatalogue.StockVisBspFor for how to make one");
            return;
        }

        // The untightened arm spelled: this is the I2 containment gate for the
        // conservative walk (the tightened walk's relation to stock is EQUALITY, which
        // TightenedVisibilityEqualsStocksAtOneThreadSorted pins).
        (_, VisResult managed) = await ViseAsync(
            name, degree: 1, options: VvisOptions.Untightened);

        BspData stock;
        await using (FileStream stream = File.OpenRead(reference))
        {
            stock = await BspFile.LoadAsync(stream, CancellationToken.None);
        }

        VisibilityLump lump = VisibilityLump.Read(stock[BspLump.Visibility])
            ?? throw new InvalidOperationException($"{reference} has no visibility lump");

        Assert.Equal(managed.ClusterCount, lump.NumClusters);
        Assert.Equal(managed.RowBytes, lump.RowBytes());

        ReadOnlySpan<byte> bytes = stock[BspLump.Visibility].Data.Span;
        byte[] row = new byte[lump.RowBytes()];

        for (int cluster = 0; cluster < lump.NumClusters; cluster++)
        {
            lump.DecompressRow(bytes[lump.BitOffset(cluster, VisibilityLump.Pvs)..], row);
            AssertContains(name, "PVS", cluster, row, managed.Pvs(cluster));

            lump.DecompressRow(bytes[lump.BitOffset(cluster, VisibilityLump.Pas)..], row);
            AssertContains(name, "PAS", cluster, row, managed.Pas(cluster));
        }
    }

    [StockCatalogueTheory]
    [MemberData(nameof(EntriesWithVisExpectations))]
    public async Task TightenedVisibilityIsASubsetOfTheUntightened(string name)
    {
        // plan_maptools.md 5, 2c: a -tighten delta that ADDS a bit anywhere is
        // a bug in the tightening, not a tolerance to widen. PVS and PAS both,
        // every row.
        (_, VisResult loose) = await ViseAsync(name, degree: 8, options: VvisOptions.Untightened);
        (_, VisResult tight) = await ViseAsync(
            name, degree: 8, options: VvisOptions.Default with { Tighten = true });

        for (int cluster = 0; cluster < loose.ClusterCount; cluster++)
        {
            AssertContains(name, "tightened PVS", cluster, tight.Pvs(cluster), loose.Pvs(cluster));
            AssertContains(name, "tightened PAS", cluster, tight.Pas(cluster), loose.Pas(cluster));
        }
    }

    [StockCatalogueTheory]
    [MemberData(nameof(EntriesWithVisExpectations))]
    public async Task TightenedOneThreadAndThirtyTwoWriteTheSameLump(string name)
    {
        VvisOptions tight = VvisOptions.Default;
        (BspData one, _) = await ViseAsync(name, degree: 1, options: tight);
        (BspData many, _) = await ViseAsync(name, degree: 32, options: tight);

        Assert.Equal(
            one[BspLump.Visibility].Data.ToArray(),
            many[BspLump.Visibility].Data.ToArray());
    }

    [StockCatalogueTheory]
    [MemberData(nameof(EntriesWithVisExpectations))]
    public async Task TightenedVisibilityContainsStocksAtOneThreadSorted(string name)
    {
        // The I2 threshold 2a froze for vis is containment of stock at
        // (-threads 1, sorted): zero bits lost. -tighten is promoted in Phase 5
        // only if it stays inside it.
        string? reference = StockCatalogue.StockVisBspFor(name);
        if (reference is null)
        {
            Assert.Fail($"{name} has no {name}.stockvis.bsp in {StockCatalogue.Directory}");
            return;
        }

        (_, VisResult tight) = await ViseAsync(
            name, degree: 8, options: VvisOptions.Default with { Tighten = true });

        BspData stock;
        await using (FileStream stream = File.OpenRead(reference))
        {
            stock = await BspFile.LoadAsync(stream, CancellationToken.None);
        }

        VisibilityLump lump = VisibilityLump.Read(stock[BspLump.Visibility])
            ?? throw new InvalidOperationException($"{reference} has no visibility lump");
        ReadOnlySpan<byte> bytes = stock[BspLump.Visibility].Data.Span;
        byte[] row = new byte[lump.RowBytes()];

        for (int cluster = 0; cluster < lump.NumClusters; cluster++)
        {
            lump.DecompressRow(bytes[lump.BitOffset(cluster, VisibilityLump.Pvs)..], row);
            AssertContains(name, "tightened PVS vs stock", cluster, row, tight.Pvs(cluster));

            lump.DecompressRow(bytes[lump.BitOffset(cluster, VisibilityLump.Pas)..], row);
            AssertContains(name, "tightened PAS vs stock", cluster, row, tight.Pas(cluster));
        }
    }

    [StockCatalogueTheory]
    [MemberData(nameof(EntriesWithVisExpectations))]
    public async Task TightenedVisibilityEqualsStocksAtOneThreadSorted(string name)
    {
        // Stronger than the frozen threshold, and measured rather than
        // guaranteed: -tighten reads a finished neighbour exactly where stock
        // at one thread does, so its PVS and PAS should be stock's bit for bit.
        // The one legitimate way this can go red is the ranking: stock sorts
        // with qsort, which leaves the order of equal might-see counts to the C
        // runtime, and this breaks ties by index.
        string? reference = StockCatalogue.StockVisBspFor(name);
        if (reference is null)
        {
            Assert.Fail($"{name} has no {name}.stockvis.bsp in {StockCatalogue.Directory}");
            return;
        }

        (_, VisResult tight) = await ViseAsync(
            name, degree: 8, options: VvisOptions.Default with { Tighten = true });

        BspData stock;
        await using (FileStream stream = File.OpenRead(reference))
        {
            stock = await BspFile.LoadAsync(stream, CancellationToken.None);
        }

        VisibilityLump lump = VisibilityLump.Read(stock[BspLump.Visibility])
            ?? throw new InvalidOperationException($"{reference} has no visibility lump");
        ReadOnlySpan<byte> bytes = stock[BspLump.Visibility].Data.Span;
        byte[] row = new byte[lump.RowBytes()];

        for (int cluster = 0; cluster < lump.NumClusters; cluster++)
        {
            lump.DecompressRow(bytes[lump.BitOffset(cluster, VisibilityLump.Pvs)..], row);
            Assert.True(
                row.AsSpan().SequenceEqual(tight.Pvs(cluster)),
                $"{name}: PVS row {cluster} differs from stock");

            lump.DecompressRow(bytes[lump.BitOffset(cluster, VisibilityLump.Pas)..], row);
            Assert.True(
                row.AsSpan().SequenceEqual(tight.Pas(cluster)),
                $"{name}: PAS row {cluster} differs from stock");
        }
    }

    [StockCatalogueFact]
    public async Task TighteningRemovesBitsOnTheArena()
    {
        // The check that keeps the subset theory honest: an identical answer is
        // a subset too, so on the one catalogue family where the untightened
        // flow is measurably looser than stock, -tighten must actually remove
        // something.
        (_, VisResult loose) = await ViseAsync(
            "l3_arena_144_pillars", degree: 8, options: VvisOptions.Untightened);
        (_, VisResult tight) = await ViseAsync(
            "l3_arena_144_pillars", degree: 8, options: VvisOptions.Default with { Tighten = true });

        Assert.True(
            tight.TotalVisibleClusters < loose.TotalVisibleClusters,
            $"-tighten left {tight.TotalVisibleClusters} visible of {loose.TotalVisibleClusters}");
    }

    [StockCatalogueFact]
    public async Task TheStockReferenceIsRealVisibilityAndNotAnEmptyLump()
    {
        // The check that cannot fail: if the stockvis maps were copied without
        // stock vvis ever running over them, every containment above would hold
        // trivially against a lump of zeroes.
        string reference = StockCatalogue.StockVisBspFor("l1_long_corridor")
            ?? throw new InvalidOperationException("no stock reference for l1_long_corridor");

        BspData stock;
        await using (FileStream stream = File.OpenRead(reference))
        {
            stock = await BspFile.LoadAsync(stream, CancellationToken.None);
        }

        VisibilityLump lump = VisibilityLump.Read(stock[BspLump.Visibility])!;
        ReadOnlySpan<byte> bytes = stock[BspLump.Visibility].Data.Span;

        int set = 0;
        byte[] row = new byte[lump.RowBytes()];
        for (int cluster = 0; cluster < lump.NumClusters; cluster++)
        {
            lump.DecompressRow(bytes[lump.BitOffset(cluster, VisibilityLump.Pvs)..], row);
            foreach (byte b in row)
            {
                set += System.Numerics.BitOperations.PopCount(b);
            }
        }

        Assert.True(set > lump.NumClusters, $"the stock reference has only {set} PVS bits set");
    }

    [StockCatalogueFact]
    public void TheStockCatalogueDirectoryActuallyHasMaps()
    {
        // The check that cannot fail is worth nothing. If the variable is set
        // to somewhere with no maps in it, every theory above returns without
        // asserting and the suite reports a clean pass over nothing.
        string directory = StockCatalogue.Directory!;

        Assert.True(
            Directory.GetFiles(directory, "*.prt").Length > 0,
            $"{StockCatalogue.DirectoryVariable} is {directory}, which holds no .prt files");
    }

    private static void AssertContains(
        string name,
        string which,
        int cluster,
        ReadOnlySpan<byte> stock,
        ReadOnlySpan<byte> managed)
    {
        for (int i = 0; i < stock.Length; i++)
        {
            int lost = stock[i] & ~managed[i] & 0xFF;
            Assert.True(
                lost == 0,
                $"{name}: {which} row {cluster} byte {i} -- bits 0x{lost:X2} are in the "
                + "reference (stock, or the tightened answer) and missing from what must contain it");
        }
    }

    private static async Task<PortalFile> ReadPortalsAsync(string bspPath)
    {
        await using FileStream stream = File.OpenRead(Path.ChangeExtension(bspPath, ".prt"));
        return await PortalFile.ReadAsync(stream, CancellationToken.None);
    }

    private static async Task<(BspData Map, VisResult Result)> ViseAsync(
        string name,
        int degree = 4,
        VvisOptions? options = null)
    {
        string bspPath = StockCatalogue.BspFor(name)
            ?? throw new InvalidOperationException($"{name} was not compiled by stock vbsp");

        BspData map;
        await using (FileStream stream = File.OpenRead(bspPath))
        {
            map = await BspFile.LoadAsync(stream, CancellationToken.None);
        }

        PortalSet portals = PortalSet.FromPortalFile(await ReadPortalsAsync(bspPath));

        VisContext context = new()
        {
            Options = options ?? VvisOptions.Default,
            Parallelism = new CompileParallelism { MaxDegree = degree },
        };

        VisResult result = await Vvis.ComputeAsync(map, portals, context, CancellationToken.None);
        return (map, result);
    }

    private static IReadOnlyList<int> ClustersOf(BspData map, CatalogEntry entry, string probeName) =>
        VisFixture.ClustersAt(map, ToVec3(entry.Observables.Probe(probeName).At));

    private static Vec3 ToVec3(Point point) => new(point.X, point.Y, point.Z);
}
