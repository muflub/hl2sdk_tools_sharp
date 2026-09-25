using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapGen.Catalog;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Portals;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Vis;

using SourceSharp.Tests.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Portals;

/// <summary>
/// The portal stages run over stock vbsp's own tree, and their output compared
/// with stock's own.
/// </summary>
/// <remarks>
/// <para>
/// The plan's gate for this stage is "<c>.prt</c> from managed vbsp gives the
/// same cluster count as stock". These go past that in both directions: the
/// whole file is compared byte for byte, and then managed vvis is run over
/// stock's <c>.prt</c> and over the managed one and the two PVS are required
/// to be identical. The second is the stronger statement, because two portal
/// files can differ in ways vis cannot see (a flipped leaf order, a portal
/// written from the other leaf) and a byte comparison would call that a
/// failure while visibility would not — and they can also agree on every
/// number while disagreeing on which leaves a portal joins, which visibility
/// would catch and a count would not.
/// </para>
/// <para>
/// See <see cref="StockBspTree"/> for why running the stage over the tree
/// inside the finished <c>.bsp</c> is the same input stock's
/// <c>WritePortalFile</c> had, and not an approximation of it.
/// </para>
/// </remarks>
public class PortalCatalogueTests
{
    /// <summary>Every entry stock compiled into a <c>.bsp</c> with a <c>.prt</c>.</summary>
    public static TheoryData<string> EntriesWithAPortalFile
    {
        get
        {
            TheoryData<string> data = [];

            foreach (CatalogEntry entry in TestMapCatalog.All)
            {
                if (StockCatalogue.BspFor(entry.Name) is not null)
                {
                    data.Add(entry.Name);
                }
            }

            return data;
        }
    }

    /// <summary>Every entry that declares a <c>LUMP_AREAS</c> count.</summary>
    public static TheoryData<string> EntriesDeclaringAreas
    {
        get
        {
            TheoryData<string> data = [];

            foreach (CatalogEntry entry in TestMapCatalog.All)
            {
                if (entry.Observables.Areas is not null && StockCatalogue.BspFor(entry.Name) is not null)
                {
                    data.Add(entry.Name);
                }
            }

            return data;
        }
    }

    [StockCatalogueFact]
    public void TheStockCatalogueDirectoryActuallyHasPortalFiles()
    {
        string directory = StockCatalogue.Directory!;
        string[] found = Directory.GetFiles(directory, "*.prt");

        Assert.True(
            found.Length > 0,
            $"{StockCatalogue.DirectoryVariable} is {directory} and there is not one .prt in it. "
            + "Every gate in this file would have passed over nothing.");
    }

    [StockCatalogueFact]
    public void TheTheoryDataIsNotEmpty()
    {
        // The theories below iterate a set built from the filesystem. An empty
        // set is a green run that measured nothing, which is the one outcome
        // these gates must not be able to produce quietly.
        Assert.NotEmpty(EntriesWithAPortalFile);
        Assert.NotEmpty(EntriesDeclaringAreas);
    }

    [StockCatalogueTheory]
    [MemberData(nameof(EntriesWithAPortalFile))]
    public async Task ThePortalFileMatchesStockByteForByte(string name)
    {
        PortalRun run = await PortalRun.ForAsync(name);

        byte[] managed = run.Managed.ToBytes(PortalLineEnding.CrLf);
        byte[] stock = await File.ReadAllBytesAsync(run.StockPortalPath);

        if (!managed.AsSpan().SequenceEqual(stock))
        {
            Assert.Fail(FirstDifference(name, stock, managed));
        }
    }

    [StockCatalogueTheory]
    [MemberData(nameof(EntriesWithAPortalFile))]
    public async Task ThePortalFileIsTheSameUnderCorrectOnEveryCatalogueMap(string name)
    {
        // The gate above pins ComplianceOptions.Stock, which is its contract.
        // Measured here: the pin does not currently change anything on this
        // catalogue. Correct also gives stock's .prt byte for byte on all 19
        // maps. None of them reaches a portal-stage quirk:
        // BaseWindingNormalise's slivers and WindingIsTinyEdgePromotion's
        // exactly-0.2 edge both need geometry the catalogue lacks. The companions that DO show
        // each switch reaching its site are unit facts over hand-built
        // fixtures (TreePortalsTests, the per-quirk probes in
        // ComplianceCatalogueTests). If this fact ever goes red, a catalogue
        // map started to exercise one of them. That is when the pin above
        // becomes load-bearing, and the map belongs in a
        // Correct-differs companion of its own.
        PortalRun run = await PortalRun.ForAsync(name, ComplianceOptions.Correct);

        byte[] managed = run.Managed.ToBytes(PortalLineEnding.CrLf);
        byte[] stock = await File.ReadAllBytesAsync(run.StockPortalPath);

        if (!managed.AsSpan().SequenceEqual(stock))
        {
            Assert.Fail(FirstDifference(name, stock, managed));
        }
    }

    [StockCatalogueTheory]
    [MemberData(nameof(EntriesWithAPortalFile))]
    public async Task TheClusterAndPortalCountsMatchStock(string name)
    {
        PortalRun run = await PortalRun.ForAsync(name);
        PortalFile stock = await ReadPortalFileAsync(run.StockPortalPath);

        Assert.Equal(stock.ClusterCount, run.Managed.ClusterCount);
        Assert.Equal(stock.Portals.Count, run.Managed.Portals.Count);
    }

    [StockCatalogueTheory]
    [MemberData(nameof(EntriesWithAPortalFile))]
    public async Task TheClusterAndPortalCountsMatchTheCatalogue(string name)
    {
        CatalogEntry entry = TestMapCatalog.Named(name);
        PortalRun run = await PortalRun.ForAsync(name);

        if (entry.Observables.Clusters is CountRange clusters)
        {
            Assert.True(
                clusters.Contains(run.Managed.ClusterCount),
                $"{name}: catalogue declares {clusters} clusters, managed .prt says {run.Managed.ClusterCount}");
        }

        if (entry.Observables.Portals is CountRange portals)
        {
            Assert.True(
                portals.Contains(run.Managed.Portals.Count),
                $"{name}: catalogue declares {portals} portals, managed .prt says {run.Managed.Portals.Count}");
        }
    }

    [StockCatalogueTheory]
    [MemberData(nameof(EntriesWithAPortalFile))]
    public async Task EveryLeafGetsTheClusterTheBspRecordedForIt(string name)
    {
        PortalRun run = await PortalRun.ForAsync(name);

        // SaveClusters_r walks the leaves in tree order and writes each one
        // into dleafs starting at index 1, so leaf i+1 of the file must carry
        // the cluster the managed numbering gave leaf i of the walk.
        ReadOnlyMemory<byte> lump = run.Bsp[BspLump.Leafs].Data;
        int stockLeafCount = BspStructView.Count<DLeaf>(run.Bsp[BspLump.Leafs]);

        IReadOnlyList<int> managed = run.Result.LeafClusters;

        // The world model's leaves come first, because ProcessModels does
        // entity 0 first; brush models add more leaves after them and
        // SaveClusters_r never reaches those. So this covers a prefix, and the
        // prefix has to be non-trivial or the gate would pass over nothing.
        Assert.InRange(managed.Count, 1, stockLeafCount - 1);

        for (int i = 0; i < managed.Count; i++)
        {
            short stockCluster = BspStructView.As<DLeaf>(lump.Span)[i + 1].Cluster;

            Assert.True(
                stockCluster == managed[i],
                $"{name}: leaf {i + 1} is cluster {stockCluster} in the .bsp and {managed[i]} here");
        }
    }

    [StockCatalogueTheory]
    [MemberData(nameof(EntriesWithAPortalFile))]
    public async Task ManagedVvisSeesTheSameThingThroughBothPortalFiles(string name)
    {
        PortalRun run = await PortalRun.ForAsync(name);

        PortalFile stock = await ReadPortalFileAsync(run.StockPortalPath);

        VisResult fromStock = await ViseAsync(run.StockBspPath, stock);
        VisResult fromManaged = await ViseAsync(run.StockBspPath, run.Managed);

        Assert.Equal(fromStock.ClusterCount, fromManaged.ClusterCount);
        Assert.Equal(fromStock.PortalCount, fromManaged.PortalCount);

        for (int cluster = 0; cluster < fromStock.ClusterCount; cluster++)
        {
            Assert.True(
                fromStock.Pvs(cluster).SequenceEqual(fromManaged.Pvs(cluster)),
                $"{name}: PVS row {cluster} differs between stock's .prt and the managed one");

            Assert.True(
                fromStock.Pas(cluster).SequenceEqual(fromManaged.Pas(cluster)),
                $"{name}: PAS row {cluster} differs between stock's .prt and the managed one");
        }
    }

    [StockCatalogueTheory]
    [MemberData(nameof(EntriesDeclaringAreas))]
    public async Task TheAreaCountMatchesTheCatalogue(string name)
    {
        CatalogEntry entry = TestMapCatalog.Named(name);
        PortalRun run = await PortalRun.ForAsync(name);

        // LUMP_AREAS is c_areas + 1: EmitAreaPortals leaves index 0 as a
        // placeholder, which is why a plain sealed map is two and not one.
        int lumpAreas = run.AreaCount + 1;

        Assert.True(
            entry.Observables.Areas!.Value.Contains(lumpAreas),
            $"{name}: catalogue declares {entry.Observables.Areas} areas, "
            + $"the flood found {run.AreaCount} + the placeholder = {lumpAreas}");
    }

    [StockCatalogueTheory]
    [MemberData(nameof(EntriesDeclaringAreas))]
    public async Task TheAreaCountMatchesTheStockLump(string name)
    {
        PortalRun run = await PortalRun.ForAsync(name);
        int stockAreas = BspStructView.Count<DArea>(run.Bsp[BspLump.Areas]);

        Assert.Equal(stockAreas, run.AreaCount + 1);
    }

    [StockCatalogueTheory]
    [MemberData(nameof(EntriesWithAPortalFile))]
    public async Task ASealedMapFloodsWithoutReachingTheOutsideLeaf(string name)
    {
        PortalRun run = await PortalRun.ForAsync(name);

        // Every entry with a .prt is one stock did not report as leaked --
        // WritePortalFile is skipped on a leak -- so the flood over the same
        // tree must agree.
        Assert.True(run.Flood.Inside, $"{name}: no entity was placed inside");
        Assert.False(run.Flood.ReachedOutside, $"{name}: the flood reached the outside leaf");
        Assert.True(run.Flood.Sealed);
    }

    [StockCatalogueFact]
    public async Task TheLeakedFixtureProducesTheLineFileStockWrote()
    {
        string directory = StockCatalogue.Directory!;
        string bspPath = Path.Combine(directory, "l1_leak.bsp");
        string linPath = Path.Combine(directory, "l1_leak.lin");

        Assert.True(File.Exists(bspPath), $"no l1_leak.bsp in {directory}");
        Assert.True(File.Exists(linPath), $"no l1_leak.lin in {directory}");

        BspData bsp = await LoadAsync(bspPath);
        StockBspTree rebuilt = StockBspTree.FromBsp(bsp);

        // Byte-exact against stock output, so it must run stock's compliance.
        WindingArena arena = new() { Compliance = ComplianceOptions.Stock };
        TreePortals portals = new(arena, rebuilt.Planes);
        portals.MakeTreePortals(rebuilt.Tree);

        FloodResult flood = EntityFlood.FloodEntities(rebuilt.Tree, rebuilt.Planes, rebuilt.Entities);

        Assert.False(flood.Sealed, "l1_leak did not leak");

        LeakReport? traced = LeakTrace.Trace(rebuilt.Tree, arena, rebuilt.Entities);
        Assert.NotNull(traced);

        byte[] managed = LeakTrace.Write(traced!, PortalLineEnding.CrLf);
        byte[] stock = await File.ReadAllBytesAsync(linPath);

        if (!managed.AsSpan().SequenceEqual(stock))
        {
            Assert.Fail(FirstDifference("l1_leak.lin", stock, managed));
        }
    }

    /// <summary>
    /// The pin on <see cref="TheLeakedFixtureProducesTheLineFileStockWrote"/>
    /// is load-bearing. Flipping only
    /// <see cref="StockQuirk.LeakFileUnnudgedOrigin"/> moves the
    /// <c>.lin</c>'s LAST line, and nothing else.
    /// </summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <c>leakfile.cpp:82</c> re-reads the <c>origin</c> key. The flood
    /// started one unit higher (<c>portals.cpp:765</c>), so the corrected last
    /// point differs from stock's in z alone.
    /// </remarks>
    [StockCatalogueFact]
    public async Task TheLeakedFixturesLastPointMovesWhenTheOriginQuirkIsCorrected()
    {
        string directory = StockCatalogue.Directory!;
        BspData bsp = await LoadAsync(Path.Combine(directory, "l1_leak.bsp"));
        string[] stock = (await File.ReadAllTextAsync(Path.Combine(directory, "l1_leak.lin")))
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        StockBspTree rebuilt = StockBspTree.FromBsp(bsp);
        WindingArena arena = new()
        {
            Compliance = ComplianceOptions.Stock.Flipping(StockQuirk.LeakFileUnnudgedOrigin),
        };
        TreePortals portals = new(arena, rebuilt.Planes);
        portals.MakeTreePortals(rebuilt.Tree);
        EntityFlood.FloodEntities(rebuilt.Tree, rebuilt.Planes, rebuilt.Entities);

        LeakReport traced = LeakTrace.Trace(rebuilt.Tree, arena, rebuilt.Entities)!;
        string[] corrected = Encoding.ASCII
            .GetString(LeakTrace.Write(traced, PortalLineEnding.CrLf))
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(stock.Length, corrected.Length);
        Assert.Equal(stock[..^1], corrected[..^1]);

        float[] s = [.. stock[^1].Split(' ').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture))];
        float[] c = [.. corrected[^1].Split(' ').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture))];

        Assert.Equal((s[0], s[1], s[2] + 1f), (c[0], c[1], c[2]));
    }

    [StockCatalogueFact]
    public async Task TheLeakedFixtureNamesTheEntityThatGotOut()
    {
        BspData bsp = await LoadAsync(Path.Combine(StockCatalogue.Directory!, "l1_leak.bsp"));
        StockBspTree rebuilt = StockBspTree.FromBsp(bsp);

        // Byte-exact against stock output, so it must run stock's compliance.
        WindingArena arena = new() { Compliance = ComplianceOptions.Stock };
        TreePortals portals = new(arena, rebuilt.Planes);
        portals.MakeTreePortals(rebuilt.Tree);
        EntityFlood.FloodEntities(rebuilt.Tree, rebuilt.Planes, rebuilt.Entities);

        LeakReport report = LeakTrace.Trace(rebuilt.Tree, arena, rebuilt.Entities)!;

        // The catalogue puts a light above the ceiling, in the void.
        Assert.Equal("light", report.ClassName);
        Assert.True(report.EntityId > 0, "the occupant was not found in the entity list");
    }

    [StockCatalogueFact]
    public async Task TheLeakedFixtureHasNoPortalFileToCompareAgainst()
    {
        // Not decoration: this is the fact that says the .prt theories above
        // are not silently skipping the one entry that behaves differently.
        await Task.CompletedTask;

        Assert.Null(StockCatalogue.BspFor("l1_leak"));
        Assert.False(File.Exists(Path.Combine(StockCatalogue.Directory!, "l1_leak.prt")));
    }

    private static async Task<BspData> LoadAsync(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        return await BspFile.LoadAsync(stream, CancellationToken.None);
    }

    private static async Task<PortalFile> ReadPortalFileAsync(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        return await PortalFile.ReadAsync(stream, CancellationToken.None);
    }

    private static async Task<VisResult> ViseAsync(string bspPath, PortalFile portals)
    {
        // Loaded fresh for each run: Vvis takes the map as an input and there
        // is no promise it leaves it untouched, and "the same PVS" has to mean
        // the same PVS from the same starting state.
        BspData map = await LoadAsync(bspPath);

        VisContext context = new()
        {
            Options = VvisOptions.Default,
            Parallelism = new CompileParallelism { MaxDegree = 4 },
        };

        return await Vvis.ComputeAsync(map, PortalSet.FromPortalFile(portals), context, CancellationToken.None);
    }

    private static string FirstDifference(string name, byte[] stock, byte[] managed)
    {
        string stockText = Encoding.Latin1.GetString(stock);
        string managedText = Encoding.Latin1.GetString(managed);

        string[] stockLines = stockText.Split('\n');
        string[] managedLines = managedText.Split('\n');

        int differing = 0;
        int rotations = 0;
        int firstDiff = -1;

        for (int i = 0; i < Math.Min(stockLines.Length, managedLines.Length); i++)
        {
            if (stockLines[i] == managedLines[i])
            {
                continue;
            }

            differing++;

            if (firstDiff < 0)
            {
                firstDiff = i;
            }

            if (IsRotationOf(stockLines[i], managedLines[i]))
            {
                rotations++;
            }
        }

        if (firstDiff >= 0)
        {
            return $"{name}: {differing} of {stockLines.Length} lines differ "
                + $"({rotations} of them the same points in a rotated order). "
                + $"First at line {firstDiff + 1}.\n"
                + $"  stock:   {stockLines[firstDiff].Replace("\r", "\\r", StringComparison.Ordinal)}\n"
                + $"  managed: {managedLines[firstDiff].Replace("\r", "\\r", StringComparison.Ordinal)}";
        }

        return $"{name}: {stockLines.Length} stock lines vs {managedLines.Length} managed lines "
            + $"({stock.Length} vs {managed.Length} bytes)";
    }

    /// <summary>
    /// Whether two portal lines carry the same points in the same cyclic order,
    /// starting at a different vertex.
    /// </summary>
    /// <remarks>
    /// A rotated winding is the same polygon wound the same way, so vis reads
    /// it identically. Saying so in the failure message is the difference
    /// between "the clip order starts somewhere else" and "the geometry is
    /// wrong", which are very different bugs.
    /// </remarks>
    private static bool IsRotationOf(string stockLine, string managedLine)
    {
        string[] a = SplitPoints(stockLine);
        string[] b = SplitPoints(managedLine);

        if (a.Length != b.Length || a.Length == 0)
        {
            return false;
        }

        for (int shift = 0; shift < a.Length; shift++)
        {
            bool all = true;

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[(i + shift) % a.Length])
                {
                    all = false;
                    break;
                }
            }

            if (all)
            {
                return true;
            }
        }

        return false;
    }

    private static string[] SplitPoints(string line)
    {
        int first = line.IndexOf('(', StringComparison.Ordinal);

        if (first < 0)
        {
            return [];
        }

        return line[first..]
            .Split(')', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Trim('(', ' '))
            .Where(part => part.Length > 0)
            .ToArray();
    }

    /// <summary>
    /// One catalogue entry taken all the way through the portal stages on the
    /// tree rebuilt from its stock <c>.bsp</c>.
    /// </summary>
    private sealed class PortalRun
    {
        private PortalRun(
            BspData bsp,
            string stockBspPath,
            string stockPortalPath,
            PortalFile managed,
            PortalFileResult result,
            FloodResult flood,
            int areaCount)
        {
            Bsp = bsp;
            StockBspPath = stockBspPath;
            StockPortalPath = stockPortalPath;
            Managed = managed;
            Result = result;
            Flood = flood;
            AreaCount = areaCount;
        }

        internal BspData Bsp { get; }

        internal string StockBspPath { get; }

        internal string StockPortalPath { get; }

        internal PortalFile Managed { get; }

        internal PortalFileResult Result { get; }

        internal FloodResult Flood { get; }

        internal int AreaCount { get; }

        internal static async Task<PortalRun> ForAsync(string name) =>
            await ForAsync(name, ComplianceOptions.Stock);

        /// <summary>The same run under a chosen compliance.</summary>
        /// <param name="name">The catalogue entry.</param>
        /// <param name="compliance">
        /// The compliance the portal stages run under. The byte-exact gates use
        /// <see cref="ComplianceOptions.Stock"/>; the companions that prove
        /// the pin reaches its site use <see cref="ComplianceOptions.Correct"/>.
        /// </param>
        /// <returns>The run.</returns>
        internal static async Task<PortalRun> ForAsync(string name, ComplianceOptions compliance)
        {
            string bspPath = StockCatalogue.BspFor(name)
                ?? throw new InvalidOperationException($"{name} has no stock .bsp + .prt");

            string prtPath = Path.ChangeExtension(bspPath, ".prt");

            BspData bsp = await LoadAsync(bspPath);
            StockBspTree rebuilt = StockBspTree.FromBsp(bsp);

            WindingArena arena = new() { Compliance = compliance };
            TreePortals portals = new(arena, rebuilt.Planes);

            // The order ProcessWorldModel runs them in.
            portals.MakeTreePortals(rebuilt.Tree);

            FloodResult flood = EntityFlood.FloodEntities(rebuilt.Tree, rebuilt.Planes, rebuilt.Entities);

            if (flood.Sealed)
            {
                EntityFlood.FillOutside(rebuilt.Tree.HeadNode);
            }

            AreaFlood areas = new(rebuilt.Entities);
            int areaCount = areas.FloodAreas(rebuilt.Tree, arena);

            PortalFileBuilder builder = new(portals, arena);
            PortalFile managed = builder.Build(rebuilt.Tree);

            return new PortalRun(bsp, bspPath, prtPath, managed, builder.LastResult, flood, areaCount);
        }
    }
}
