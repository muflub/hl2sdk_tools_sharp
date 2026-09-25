using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Bsp.Portals;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Tracing;

using SourceSharp.Tests.MapTools.Bsp;
using SourceSharp.Tests.MapTools.Bsp.Csg;
using SourceSharp.Tests.MapTools.Bsp.Portals;

using Xunit;

namespace SourceSharp.Tests.MapTools.Options;

/// <summary>
/// Each switch this lane wired reaches its site: flipping ONE quirk and
/// nothing else changes the site's output.
/// </summary>
/// <remarks>
/// <para>
/// The IL scan in <see cref="ComplianceCatalogueTests"/> proves every site
/// CONSULTS its switch. These facts prove the consultation matters: the two
/// answers differ. Each compares <see cref="ComplianceOptions.Stock"/> with
/// <c>Stock.Flipping(quirk)</c>, so exactly one quirk moves.
/// </para>
/// <para>
/// Quirks with an effect fact elsewhere are not repeated here:
/// WindingIsTinyEdgePromotion's TreePortals site (TreePortalsTests),
/// LeakFileUnnudgedOrigin (LeakTraceTests, and
/// PortalCatalogueTests.TheLeakedFixturesLastPointMovesWhenTheOriginQuirkIsCorrected),
/// and the plane-lump factorial over both normalise quirks on the octahedron
/// (StockLoadCatalogueTests, corpus tier).
/// </para>
/// </remarks>
public class ComplianceQuirkEffectTests
{
    [Fact]
    public void AddQuadGivesTheSecondTriangleTheNextIdUnderStock()
    {
        Assert.Equal((7, 8), QuadIds(ComplianceOptions.Stock));
    }

    [Fact]
    public void AddQuadGivesBothTrianglesTheSameIdWhenTheQuirkIsCorrected()
    {
        Assert.Equal(
            (7, 7),
            QuadIds(ComplianceOptions.Stock.Flipping(StockQuirk.AddQuadSecondTriangleId)));
    }

    [Fact]
    public void BaseWindingForPlaneMovesWhenOnlyItsNormaliseIsCorrected()
    {
        // The reference normalise is the rsqrt estimate. Over a spread of
        // non-axial normals, the estimate
        // and the exact divide must disagree on at least one winding point;
        // an axial normal would not do, because the up vector is then already
        // a unit vector and both normalisations return it unchanged.
        ComplianceOptions stock = ComplianceOptions.Stock;
        ComplianceOptions corrected = stock.Flipping(StockQuirk.BaseWindingNormalise);

        int differing = 0;
        foreach (Vec3 normal in SlantedNormals())
        {
            if (!BaseWindingBits(stock, normal).SequenceEqual(BaseWindingBits(corrected, normal)))
            {
                differing++;
            }
        }

        Assert.True(differing > 0, "no slanted normal told the two normalisations apart");
    }

    [Fact]
    public void BaseWindingForPlaneIgnoresEveryOtherQuirk()
    {
        // The flip above is the ONLY input that moves this site: the policy
        // with every OTHER quirk corrected gives stock's bits exactly.
        ComplianceOptions onlyThis = ComplianceOptions.Correct.Flipping(StockQuirk.BaseWindingNormalise);

        foreach (Vec3 normal in SlantedNormals())
        {
            Assert.Equal(
                BaseWindingBits(ComplianceOptions.Stock, normal),
                BaseWindingBits(onlyThis, normal));
        }
    }

    [Fact]
    public async Task TheOctahedronsBevelPlanesMoveWhenOnlyTheEdgeBevelNormaliseIsCorrected()
    {
        // At unit tier: the octahedron is loaded from
        // an in-memory VMF with in-memory materials. It is the shape whose
        // bevel normal has |x| == |z|, which is what the estimate breaks.
        IReadOnlyList<(uint, uint, uint, uint, PlaneType)> stock =
            await OctahedronPlanesAsync(ComplianceOptions.Stock);
        IReadOnlyList<(uint, uint, uint, uint, PlaneType)> corrected =
            await OctahedronPlanesAsync(ComplianceOptions.Stock.Flipping(StockQuirk.EdgeBevelNormalise));

        Assert.NotEqual(stock, corrected);
    }

    [Fact]
    public async Task BrushGeometrysWindingIsTinyCountsAnEdgeOfExactlyTheThresholdAsLongUnderStock()
    {
        // The CSG copy of the tiny-winding test, under stock's float compare.
        // Four edges of
        // exactly 0.2f: as a double compare each is LONG, so three long edges
        // make it not tiny.
        (BspBuildContext context, Winding square) = await ThresholdSquareAsync(ComplianceOptions.Stock);

        Assert.False(BrushGeometry.WindingIsTiny(context, square));
    }

    [Fact]
    public async Task BrushGeometrysWindingIsTinyCountsItAsShortWhenTheQuirkIsCorrected()
    {
        (BspBuildContext context, Winding square) = await ThresholdSquareAsync(
            ComplianceOptions.Stock.Flipping(StockQuirk.WindingIsTinyEdgePromotion));

        Assert.True(BrushGeometry.WindingIsTiny(context, square));
    }

    /// <summary>
    /// Why FindPortalSide's half-masked plane compare was
    /// retired as a quirk rather than switched: stock never puts a node on an
    /// odd plane, so masking only the side cannot misfire on a tree stock
    /// built, and VisibleSides masks both unconditionally.
    /// </summary>
    /// <param name="name">The catalogue entry.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// The reference node builder stores the even half of the best side's
    /// plane pair ("always use front facing"). The block tree's axial nodes
    /// come from the float-plane search with a
    /// positive axial normal, and that one is always the even half of its pair.
    /// Read off every node of every stock-compiled catalogue map.
    /// </remarks>
    [StockLoadTheory]
    [MemberData(nameof(StockMaps))]
    public async Task StockNeverPutsANodeOnAnOddPlane(string name)
    {
        BspData bsp;
        await using (FileStream stream = File.OpenRead(StockLoad.BspPath(name)))
        {
            bsp = await BspFile.LoadAsync(stream, CancellationToken.None);
        }

        ReadOnlySpan<DNode> nodes = BspStructView.As<DNode>(bsp[BspLump.Nodes]);
        Assert.True(nodes.Length > 0, $"{name} has no nodes");

        int odd = 0;
        foreach (DNode node in nodes)
        {
            if ((node.PlaneNum & 1) != 0)
            {
                odd++;
            }
        }

        Assert.Equal(0, odd);
    }

    /// <summary>
    /// Under stock's walk, the leaking areaportal's report runs out through
    /// the +x WALL, a place no leak path can go.
    /// </summary>
    /// <remarks>
    /// The leak walk's <c>continue</c> past the start portal skips the step
    /// that re-derives which side of the next node it is on, so the walk
    /// follows the wrong side's link and leaves the
    /// slab's own portal list. The "best" portal found that way is on another
    /// node, so the line starts at x = 32 and 52: the solid +x wall and the
    /// void beyond it. The fixture's room spans x from -32 to 32.
    /// </remarks>
    [Fact]
    public void TheAreaportalLeakLineLeavesTheRoomUnderStock()
    {
        Assert.Equal(
            "32,0,0;52,0,0;52,0,0;-16,0,8",
            PathOf(AreaportalLeak(ComplianceOptions.Stock)));
    }

    /// <summary>
    /// With the walk corrected, the line goes round the slab through the open
    /// +x half, from below it to above it. That is the leak.
    /// </summary>
    [Fact]
    public void TheAreaportalLeakLineGoesRoundTheSlabWhenTheWalkIsCorrected()
    {
        Assert.Equal(
            "-16,0,-8;-16,0,-20;0,0,-20;0,0,20;-16,0,20;-16,0,8",
            PathOf(AreaportalLeak(ComplianceOptions.Stock.Flipping(StockQuirk.AreaportalLeakWalk))));
    }

    private static string PathOf(LeakReport? report) =>
        report is null
            ? "no report"
            : string.Join(";", report.Path.Select(p => string.Create(
                System.Globalization.CultureInfo.InvariantCulture, $"{p.X},{p.Y},{p.Z}")));

    private static LeakReport? AreaportalLeak(ComplianceOptions compliance)
    {
        PortalFixture f = PortalFixture.LeakingAreaportal(compliance);
        f.Portalise();
        EntityFlood.FloodEntities(f.Tree, f.Planes, f.Entities);
        EntityFlood.FillOutside(f.Tree.HeadNode);
        AreaFlood areas = new(f.Entities);
        areas.FloodAreas(f.Tree, f.Arena);
        return areas.AreaportalLeak;
    }

    /// <summary>The stock-compiled catalogue maps.</summary>
    public static TheoryData<string> StockMaps => StockLoad.Entries;

    private static (int First, int Second) QuadIds(ComplianceOptions compliance)
    {
        ShadowCasterBuilder builder = new() { Compliance = compliance };
        builder.BeginSource(ShadowCasterSource.StaticProp);
        builder.AddQuad(
            7,
            new Vec3(0f, 0f, 0f),
            new Vec3(1f, 0f, 0f),
            new Vec3(1f, 1f, 0f),
            new Vec3(0f, 1f, 0f),
            1f);

        ReadOnlySpan<TracedTriangle> triangles = builder.Build().Triangles;
        return (triangles[0].Id, triangles[1].Id);
    }

    private static IEnumerable<Vec3> SlantedNormals()
    {
        for (int i = 1; i <= 16; i++)
        {
            (Vec3 n, _) = new Vec3(i, 3f, 17f - i).Normalise();
            yield return n;
        }
    }

    private static uint[] BaseWindingBits(ComplianceOptions compliance, Vec3 normal)
    {
        WindingArena arena = new() { Compliance = compliance };
        Winding w = arena.BaseWindingForPlane(normal, 64f);

        List<uint> bits = [];
        foreach (Vec3 p in arena.Points(w))
        {
            bits.Add(BitConverter.SingleToUInt32Bits(p.X));
            bits.Add(BitConverter.SingleToUInt32Bits(p.Y));
            bits.Add(BitConverter.SingleToUInt32Bits(p.Z));
        }

        return [.. bits];
    }

    private static async Task<IReadOnlyList<(uint, uint, uint, uint, PlaneType)>> OctahedronPlanesAsync(
        ComplianceOptions compliance)
    {
        VmfDocument document = EdgeBevelShapes.Document();
        foreach (VmfChunk chunk in Descendants(document.Chunks))
        {
            foreach (VmfKey key in chunk.Keys.Where(k => k.Name == "material"))
            {
                key.Value = UnitMap.Plain;
            }
        }

        VbspContext context = await UnitMap.ContextAsync(VbspOptions.Default with { Compliance = compliance });
        MapFile map = await MapFileLoader.LoadAsync(context, document);

        List<(uint, uint, uint, uint, PlaneType)> planes = [];
        for (int i = 0; i < map.Planes.Count; i++)
        {
            Plane p = map.Planes[i];
            planes.Add((
                BitConverter.SingleToUInt32Bits(p.Normal.X),
                BitConverter.SingleToUInt32Bits(p.Normal.Y),
                BitConverter.SingleToUInt32Bits(p.Normal.Z),
                BitConverter.SingleToUInt32Bits(p.Dist),
                map.Planes.TypeOf(i)));
        }

        return planes;
    }

    private static IEnumerable<VmfChunk> Descendants(IEnumerable<VmfChunk> chunks)
    {
        foreach (VmfChunk chunk in chunks)
        {
            yield return chunk;
            foreach (VmfChunk child in Descendants(chunk.Chunks))
            {
                yield return child;
            }
        }
    }

    private static async Task<(BspBuildContext Context, Winding Square)> ThresholdSquareAsync(
        ComplianceOptions compliance)
    {
        VbspContext compile = await UnitMap.ContextAsync(VbspOptions.Default with { Compliance = compliance });
        MapFile map = await MapFileLoader.LoadAsync(
            compile, UnitMap.BoxMap(UnitMap.Plain, (0, 0, 0), (64, 64, 64)));

        BspBuildContext context = new(compile, map);
        Winding square = compile.Windings.Create(
        [
            new Vec3(0f, 0f, 0f),
            new Vec3(0.2f, 0f, 0f),
            new Vec3(0.2f, 0.2f, 0f),
            new Vec3(0f, 0.2f, 0f),
        ]);

        return (context, square);
    }
}
