using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Validation;

using Xunit;

namespace SourceSharp.Tests.MapTools.Validation;

/// <summary>
/// One fact per index-bounds rule.
/// </summary>
/// <remarks>
/// Most of these are places the engine does NOT check: it subscripts an array
/// with a number straight out of the file. A map that breaks one of them does
/// not get an error message from the engine, it gets a read outside a lump.
/// which is exactly why they belong in an instrument that can say so.
/// </remarks>
public class IndexRuleTests
{
    [Corrupts(BspRuleCodes.FaceTexInfo)]
    public async Task AFaceWhoseTexinfoIsOutOfRangeIsReported()
    {
        //, "Mod_LoadFaces: bad texinfo
        // number". One of the few the engine states out loud.
        BspData bsp = await Corrupted.GoldenAsync();
        Span<DFace> faces = Corrupted.Edit<DFace>(bsp, BspLump.Faces);
        faces[0].TexInfo = 9999;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.FaceTexInfo);
        Severity.Is(report, BspRuleCodes.FaceTexInfo, DiagnosticSeverity.Error);
    }

    [Corrupts(BspRuleCodes.LeafFaceSurface)]
    public async Task ALeaffaceNamingAFaceThatDoesNotExistIsReported()
    {
        //, "Mod_LoadMarksurfaces: bad surface
        // number".
        BspData bsp = await Corrupted.GoldenAsync();
        Span<ushort> leafFaces = Corrupted.Edit<ushort>(bsp, BspLump.LeafFaces);
        leafFaces[0] = 60000;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.LeafFaceSurface);
        Severity.Is(report, BspRuleCodes.LeafFaceSurface, DiagnosticSeverity.Error);
    }

    [Corrupts(BspRuleCodes.SurfEdgeEdge)]
    public async Task ASurfedgeNamingAnEdgeThatDoesNotExistIsReported()
    {
        // -- out[i] = pedges[edge].v[index],
        // with edge the magnitude of the file's own number and no bound on it.
        BspData bsp = await Corrupted.GoldenAsync();
        Span<int> surfEdges = Corrupted.Edit<int>(bsp, BspLump.SurfEdges);
        surfEdges[0] = 999999;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.SurfEdgeEdge);
        Severity.Is(report, BspRuleCodes.SurfEdgeEdge, DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task ANegativeSurfedgeIsBoundedByItsMagnitude()
    {
        // The SIGN of a surfedge picks which end of the edge to start from
        //, so -999999 is the same overrun as 999999.
        BspData bsp = await Corrupted.GoldenAsync();
        Span<int> surfEdges = Corrupted.Edit<int>(bsp, BspLump.SurfEdges);
        surfEdges[0] = -999999;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.SurfEdgeEdge);
    }

    [Corrupts(BspRuleCodes.EdgeVertex)]
    public async Task AReferencedEdgeNamingAVertexThatDoesNotExistIsReported()
    {
        // Edge 1, not edge 0: the rule checks the edges a surfedge names,
        // which is every edge the loaders dereference (Mod_LoadSurfedges
        // reads pedges[edge] for each surfedge's magnitude and nothing else
        // touches the array), and edge 0 is the tool's reserved, never-emitted
        // slot. The golden's edge 1 is named by a surfedge, so this
        // corruption sits on a real load path.
        BspData bsp = await Corrupted.GoldenAsync();
        Assert.True(NamesEdge(bsp, 1));
        Span<DEdge> edges = Corrupted.Edit<DEdge>(bsp, BspLump.Edges);
        edges[1].V[0] = 60000;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.EdgeVertex);
        Severity.Is(report, BspRuleCodes.EdgeVertex, DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task AnUnreferencedEdgeWithSentinelEndpointsValidatesClean()
    {
        // The dead-edge marker: an aggressive cull leaves an edge no surfedge
        // names holding (0xffff, 0xffff), and stock itself never emits edge 0
        // (BeginBSPFile sets numedges = 1, "edge 0 is unused because 0 cannot
        // be sign-inverted"). The engine loads such an
        // edge lump without a word: Mod_LoadEdges copies the array checking
        // only its size, and only edges a surfedge names are dereferenced. An
        // oracle map (probe-tools/p3f_p3_bump) carries exactly this edge-0
        // sentinel; this fact pins the shape without needing the corpus.
        BspData bsp = await Corrupted.GoldenAsync();
        Assert.False(NamesEdge(bsp, 0));
        Span<DEdge> edges = Corrupted.Edit<DEdge>(bsp, BspLump.Edges);
        edges[0].V[0] = 0xffff;
        edges[0].V[1] = 0xffff;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Assert.True(report.IsClean, string.Join("; ",
            report.Diagnostics.Select(d => $"{d.Code} {d.Severity}: {d.Message}")));
    }

    /// <summary>
    /// Whether any surfedge entry names <paramref name="edge"/> by magnitude.
    /// </summary>
    private static bool NamesEdge(BspData bsp, int edge)
    {
        ReadOnlySpan<int> surfEdges = System.Runtime.InteropServices
            .MemoryMarshal.Cast<byte, int>(bsp[BspLump.SurfEdges].Data.Span);
        foreach (int value in surfEdges)
        {
            if (Math.Abs(value) == edge)
            {
                return true;
            }
        }

        return false;
    }

    [Corrupts(BspRuleCodes.NodeChildren)]
    public async Task ANodeChildNamingANodeThatDoesNotExistIsReported()
    {
        // tests only the sign.
        BspData bsp = await Corrupted.GoldenAsync();
        Span<DNode> nodes = Corrupted.Edit<DNode>(bsp, BspLump.Nodes);
        nodes[0].Children[0] = 99999;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.NodeChildren);
        Severity.Is(report, BspRuleCodes.NodeChildren, DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task ANegativeNodeChildIsCheckedAgainstTheLeafCount()
    {
        // A negative child is the leaf -1 - p, so -1 is
        // leaf 0 and there is no way to spell "no child".
        BspData bsp = await Corrupted.GoldenAsync();
        Span<DNode> nodes = Corrupted.Edit<DNode>(bsp, BspLump.Nodes);
        nodes[0].Children[1] = -99999;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.NodeChildren);
    }

    [Corrupts(BspRuleCodes.LeafCluster)]
    public async Task ALeafInAClusterTheVisLumpHasNoRowForIsReported()
    {
        // subscripts the vis lump's own offset
        // table by cluster. does NOT bound it.
        // it grows the map's cluster count to fit, which is what makes the
        // mismatch survive to the point of the read.
        BspData bsp = await Corrupted.GoldenAsync();
        Span<DLeafVersion0> leaves = Corrupted.Edit<DLeafVersion0>(bsp, BspLump.Leafs);
        leaves[0].Cluster = 30000;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.LeafCluster);
        Severity.Is(report, BspRuleCodes.LeafCluster, DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task AMapWithNoVisibilityLumpHasNoClusterToCheckAgainst()
    {
        // An unvised map is a normal intermediate state, not a broken one.
        BspData bsp = await Corrupted.GoldenAsync();
        Corrupted.Replace(bsp, BspLump.Visibility, [], 0);
        Span<DLeafVersion0> leaves = Corrupted.Edit<DLeafVersion0>(bsp, BspLump.Leafs);
        leaves[0].Cluster = 30000;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Assert.True(report.ForCode(BspRuleCodes.LeafCluster).IsEmpty);
    }

    [Corrupts(BspRuleCodes.BrushSideTexInfo)]
    public async Task ABrushSideWhoseTexinfoIsOutOfRangeIsReported()
    {
        //, "Bad brushside texinfo".
        BspData bsp = await Corrupted.GoldenAsync();
        Span<DBrushSide> sides = Corrupted.Edit<DBrushSide>(bsp, BspLump.BrushSides);
        sides[0].TexInfo = 9999;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.BrushSideTexInfo);
        Severity.Is(report, BspRuleCodes.BrushSideTexInfo, DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task ABrushSideTexinfoOfMinusOneIsLegal()
    {
        // maps a negative texinfo to
        // SURFACE_INDEX_INVALID, and the BUGBUG comment above it says vbsp
        // writes -1. Rejecting it would reject maps the engine loads.
        BspData bsp = await Corrupted.GoldenAsync();
        Span<DBrushSide> sides = Corrupted.Edit<DBrushSide>(bsp, BspLump.BrushSides);
        sides[0].TexInfo = -1;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Assert.True(report.Diagnostics.IsEmpty);
    }

    [Corrupts(BspRuleCodes.ModelHeadNode)]
    public async Task AModelWhoseHeadNodeDoesNotExistIsReported()
    {
        //, "Inline model %i has bad
        // firstnode".
        BspData bsp = await Corrupted.GoldenAsync();
        Span<DModel> models = Corrupted.Edit<DModel>(bsp, BspLump.Models);
        models[1].HeadNode = 99999;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.ModelHeadNode);
        Severity.Is(report, BspRuleCodes.ModelHeadNode, DiagnosticSeverity.Error);
    }

    [Corrupts(BspRuleCodes.TexDataStringIndex)]
    public async Task ATexdataNamingAStringTableEntryThatDoesNotExistIsReported()
    {
        // -- two subscripts in a row, guarded
        // only by Asserts that a release build compiles out.
        BspData bsp = await Corrupted.GoldenAsync();
        Span<DTexData> texData = Corrupted.Edit<DTexData>(bsp, BspLump.TexData);
        texData[0].NameStringTableId = 9999;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.TexDataStringIndex);
        Severity.Is(report, BspRuleCodes.TexDataStringIndex, DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task AStringTableOffsetPastTheStringDataIsTheSameRule()
    {
        // The second subscript, which a bound on the first cannot catch.
        BspData bsp = await Corrupted.GoldenAsync();
        Span<int> table = Corrupted.Edit<int>(bsp, BspLump.TexDataStringTable);
        table[0] = 999999;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.Fires(report, BspRuleCodes.TexDataStringIndex);
    }

    [Corrupts(BspRuleCodes.OverlayFaces)]
    public async Task AnOverlayClaimingMoreFacesThanItsArrayHoldsIsReported()
    {
        // reads aFaces[iFace] for every face it
        // claims, and aFaces is a fixed int[OVERLAY_BSP_FACE_COUNT]
        //, so a bigger count reads the NEXT overlay's
        // bytes as face indices.
        BspData bsp = await Corrupted.GoldenAsync();
        Span<DOverlay> overlays = Corrupted.Edit<DOverlay>(bsp, BspLump.Overlays);
        overlays[0].SetFaceCount(100);

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.OverlayFaces);
        Severity.Is(report, BspRuleCodes.OverlayFaces, DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task AnOverlayNamingAFaceThatDoesNotExistIsTheSameRule()
    {
        BspData bsp = await Corrupted.GoldenAsync();
        Span<DOverlay> overlays = Corrupted.Edit<DOverlay>(bsp, BspLump.Overlays);
        overlays[0].Faces[0] = 999999;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.OverlayFaces);
    }

    [Corrupts(BspRuleCodes.PlaneIndex)]
    public async Task ANodeNamingAPlaneThatDoesNotExistIsReported()
    {
        // -- planes + p, with no test at all.
        BspData bsp = await Corrupted.GoldenAsync();
        Span<DNode> nodes = Corrupted.Edit<DNode>(bsp, BspLump.Nodes);
        nodes[0].PlaneNum = 99999;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.PlaneIndex);
        Severity.Is(report, BspRuleCodes.PlaneIndex, DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task AFaceNamingAPlaneThatDoesNotExistIsTheSameRule()
    {
        BspData bsp = await Corrupted.GoldenAsync();
        Span<DFace> faces = Corrupted.Edit<DFace>(bsp, BspLump.Faces);
        faces[0].PlaneNum = 60000;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.PlaneIndex);
    }

    [Fact]
    public async Task ABrushSideNamingAPlaneThatDoesNotExistIsTheSameRule()
    {
        BspData bsp = await Corrupted.GoldenAsync();
        Span<DBrushSide> sides = Corrupted.Edit<DBrushSide>(bsp, BspLump.BrushSides);
        sides[0].PlaneNum = 60000;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.PlaneIndex);
    }

    [Corrupts(BspRuleCodes.BrushSideRun)]
    public async Task ABrushWhoseSideRunLeavesTheLumpIsReported()
    {
        // walks in[firstbrushside + j] for j <
        // numsides with nothing bounding either number.
        BspData bsp = await Corrupted.GoldenAsync();
        Span<DBrush> brushes = Corrupted.Edit<DBrush>(bsp, BspLump.Brushes);
        brushes[0].NumSides = 99999;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.BrushSideRun);
        Severity.Is(report, BspRuleCodes.BrushSideRun, DiagnosticSeverity.Error);
    }

    [Corrupts(BspRuleCodes.LeafRuns)]
    public async Task ALeafWhoseLeaffaceRunLeavesTheLumpIsReported()
    {
        BspData bsp = await Corrupted.GoldenAsync();
        Span<DLeafVersion0> leaves = Corrupted.Edit<DLeafVersion0>(bsp, BspLump.Leafs);
        leaves[1].NumLeafFaces = 60000;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.LeafRuns);
        Severity.Is(report, BspRuleCodes.LeafRuns, DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task ALeafWhoseLeafbrushRunLeavesTheLumpIsTheSameRule()
    {
        BspData bsp = await Corrupted.GoldenAsync();
        Span<DLeafVersion0> leaves = Corrupted.Edit<DLeafVersion0>(bsp, BspLump.Leafs);
        leaves[1].FirstLeafBrush = 60000;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.LeafRuns);
    }

    [Corrupts(BspRuleCodes.StaticPropDictIndex)]
    public async Task AStaticPropWhoseTypeIsNotInTheDictionaryIsReported()
    {
        // -- m_StaticPropDict[ lump.m_PropType ]
        // straight from the file.
        BspData bsp = await Corrupted.GoldenAsync();
        RewriteStaticProps(bsp, prop => prop.PropType = 9999);

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.StaticPropDictIndex);
        Severity.Is(report, BspRuleCodes.StaticPropDictIndex, DiagnosticSeverity.Error);
    }

    [Corrupts(BspRuleCodes.StaticPropLeafRun)]
    public async Task AStaticPropWhoseLeafRunLeavesTheLeafListIsReported()
    {
        // walks m_StaticPropLeaves from
        // FirstLeaf for LeafCount entries, unbounded.
        BspData bsp = await Corrupted.GoldenAsync();
        RewriteStaticProps(bsp, prop => prop.FirstLeaf = 60000);

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.StaticPropLeafRun);
        Severity.Is(report, BspRuleCodes.StaticPropLeafRun, DiagnosticSeverity.Error);
    }

    /// <summary>
    /// Decodes the <c>sprp</c> game lump, changes the first prop, and puts it
    /// back.
    /// </summary>
    /// <remarks>
    /// The game lump is three counted runs rather than an array, so it cannot
    /// be cast and edited in place the way every other corruption here does it.
    /// Re-encoding upgrades the lump to version 10, which is what vbsp would
    /// have done to it anyway and still well above the version gate.
    /// </remarks>
    private static void RewriteStaticProps(BspData bsp, Action<StaticProp> change)
    {
        for (int i = 0; i < bsp.GameLumps.Count; i++)
        {
            if (bsp.GameLumps[i].IdString() != GameLumpId.StaticProps)
            {
                continue;
            }

            StaticPropLump props = StaticPropLump.Read(bsp.GameLumps[i]);
            change(props.Props[0]);
            bsp.GameLumps[i] = props.Write();
            return;
        }

        throw new InvalidOperationException("the golden map has no sprp game lump");
    }
}
