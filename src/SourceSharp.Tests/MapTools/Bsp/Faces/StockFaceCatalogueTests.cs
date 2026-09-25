using SourceSharp.MapTools.Bsp.Faces;
using SourceSharp.MapTools.Options;

using SourceSharp.Tests.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Faces;

/// <summary>
/// The face stage run over every catalogue map, and its counters compared with
/// the ones stock printed for the same map.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is Phase 3d's central gate.</b> Each fact takes one of stock's own
/// printed numbers and requires the managed pass to produce it exactly. They
/// are separate facts rather than one comparison of a record because a single
/// equality on ten numbers tells you only that something moved; ten facts tell
/// you WHICH, and merging, subdivision, welding and t-junction splitting are
/// four different pieces of code.
/// </para>
/// <para>
/// See <see cref="WorldFacePass"/> for what is run and
/// <see cref="StockFaceLog"/> for why the log is the oracle rather than the
/// face lump.
/// </para>
/// </remarks>
public class StockFaceCatalogueTests
{
    /// <summary>Every catalogue entry with a stock verbose log beside it.</summary>
    public static TheoryData<string> Entries => StockLoad.Entries;

    [StockLoadFact]
    public void TheTheoryDataIsNotEmpty()
    {
        // A theory over an empty set is a green run that measured nothing.
        Assert.NotEmpty(Entries);
        Assert.True(
            StockLoad.EntryNames.Count >= 10,
            $"only {StockLoad.EntryNames.Count} catalogue entries have a -v log; "
            + "the gates below would be measuring almost nothing");
    }

    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task MakeFacesProducesTheSameFaceCountAsStock(string name)
    {
        WorldFacePass run = await WorldFacePass.ForAsync(name, ComplianceOptions.Stock);
        StockFaceCounts stock = StockFaceLog.Counts(name);

        Assert.Equal(stock.MakeFaces, run.Counters.NodeFaces);
    }

    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task FaceMergingMergesTheSameNumberOfFacesAsStock(string name)
    {
        WorldFacePass run = await WorldFacePass.ForAsync(name, ComplianceOptions.Stock);
        StockFaceCounts stock = StockFaceLog.Counts(name);

        Assert.Equal(stock.Merged, run.Counters.Merged);
    }

    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task SubdivisionSplitsTheSameNumberOfFacesAsStock(string name)
    {
        WorldFacePass run = await WorldFacePass.ForAsync(name, ComplianceOptions.Stock);
        StockFaceCounts stock = StockFaceLog.Counts(name);

        Assert.Equal(stock.Subdivided, run.Counters.Subdivided);
    }

    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task TheWeldSeesTheSameNumberOfPointsAsStock(string name)
    {
        WorldFacePass run = await WorldFacePass.ForAsync(name, ComplianceOptions.Stock);
        StockFaceCounts stock = StockFaceLog.Counts(name);

        // The second half of "%i unique from %i": how many points were offered
        // to the weld, which is the total vertex count of every live face.
        Assert.Equal(stock.TotalVerts, run.Counters.TotalVerts);
    }

    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task TheWeldProducesTheSameNumberOfVerticesAsStock(string name)
    {
        WorldFacePass run = await WorldFacePass.ForAsync(name, ComplianceOptions.Stock);
        StockFaceCounts stock = StockFaceLog.Counts(name);

        Assert.Equal(stock.UniqueVerts, run.Counters.UniqueVerts);

        // + the error vertex 0 BeginBSPFile reserves (writebsp.cpp:1138),
        // which c_uniqueverts does not count.
        Assert.Equal(stock.UniqueVerts + 1, run.Faces.Vertices.Count);
    }

    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task TheTJunctionPassSplitsTheSameNumberOfEdgesAsStock(string name)
    {
        WorldFacePass run = await WorldFacePass.ForAsync(name, ComplianceOptions.Stock);
        StockFaceCounts stock = StockFaceLog.Counts(name);

        Assert.Equal(stock.TJunctions, run.Counters.TJunctions);
    }

    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task TheSameEdgesAndFacesDegenerateAsInStock(string name)
    {
        WorldFacePass run = await WorldFacePass.ForAsync(name, ComplianceOptions.Stock);
        StockFaceCounts stock = StockFaceLog.Counts(name);

        Assert.Equal(stock.DegenerateEdges, run.Counters.DegenerateEdges);
        Assert.Equal(stock.CollapsedFaces, run.Counters.CollapsedFaces);
    }

    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task TheSameNumberOfFacesOverflowAsInStock(string name)
    {
        WorldFacePass run = await WorldFacePass.ForAsync(name, ComplianceOptions.Stock);
        StockFaceCounts stock = StockFaceLog.Counts(name);

        Assert.Equal(stock.FaceOverflows, run.Counters.FaceOverflows);
    }

    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task TheSameFacesHaveNoCleanStartingVertexAsInStock(string name)
    {
        if (StockFaceLog.HasBrushModels(name))
        {
            // c_badstartverts is the one counter stock never resets, so on a
            // map with brush models the log's number is the total across every
            // model and a world-only pass cannot match it. Stated rather than
            // skipped silently: the assertion below still holds one way.
            WorldFacePass multi = await WorldFacePass.ForAsync(name, ComplianceOptions.Stock);
            StockFaceCounts total = StockFaceLog.Counts(name);

            Assert.True(
                multi.Counters.BadStartVerts <= total.BadStartVerts,
                $"{name}: the world alone found {multi.Counters.BadStartVerts} bad start verts, "
                + $"more than stock's cumulative {total.BadStartVerts}");
            return;
        }

        WorldFacePass run = await WorldFacePass.ForAsync(name, ComplianceOptions.Stock);
        StockFaceCounts stock = StockFaceLog.Counts(name);

        Assert.Equal(stock.BadStartVerts, run.Counters.BadStartVerts);
    }
}
