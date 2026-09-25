using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Bsp.Tree;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Csg;

/// <summary>
/// <b>I2 on BRUSHES and BRUSHSIDES</b>, scoped to what each phase actually
/// decides in them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Naming who owns each column is most of this gate.</b> <c>EmitBrushes</c>
/// (<c>writebsp.cpp:1048</c>) copies <c>g_MainMap-&gt;mapbrushes</c> into
/// BRUSHES and their <c>original_sides</c> into BRUSHSIDES — the MAP brushes,
/// not the carved ones. So:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Brush count, side runs, plane numbers and bevel flags</b> are Phase 3a's
/// loader plus Phase 3e's axial bevel pass. Compared here EXACTLY, element for
/// element, on all 28 maps — which is what gives the edge-bevel work below
/// somewhere to land.
/// </description></item>
/// <item><description>
/// <b>The texinfo column</b> cannot match until Phase 3e runs
/// <c>CompactTexinfoArray</c>, which renumbers the whole table at the end of
/// the compile. What is checkable now is that this stage does not move it.
/// </description></item>
/// <item><description>
/// <b>The contents column</b> is Phase 3a's and now agrees exactly. It used to
/// disagree on one class of brush; see
/// <see cref="TheContentsColumnMatchesStockExactly"/>.
/// </description></item>
/// <item><description>
/// <b>What PHASE 3B writes into these lumps</b> is one function:
/// <c>FixupAreaportalWaterBrushes</c> ORs a water brush's contents into an
/// overlapping areaportal MAP brush and copies its texinfos onto that brush's
/// MAP sides (<c>csg.cpp:368-374</c>). No corpus map has an areaportal inside
/// water, so on this corpus the stage writes nothing — which is stated as a
/// fact rather than left as an absence.
/// </description></item>
/// </list>
/// </remarks>
public class StockBrushLumpTests
{
    /// <summary>
    /// The brush count and every brush's side run, exactly.
    /// </summary>
    /// <param name="name">The catalogue entry.</param>
    /// <returns>The running fact.</returns>
    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task TheBrushCountAndEverySideRunMatchStockExactly(string name)
    {
        (_, MapFile map) = await CompiledAsync(name);

        IReadOnlyList<EmittedBrush> stock = await StockBrushLumps.StockBrushesAsync(name);
        (List<EmittedBrush> managed, _) = StockBrushLumps.Emit(map);

        Assert.Equal(stock.Count, managed.Count);
        for (int i = 0; i < stock.Count; i++)
        {
            Assert.Equal(
                (stock[i].FirstSide, stock[i].SideCount),
                (managed[i].FirstSide, managed[i].SideCount));
        }
    }

    /// <summary>
    /// Every brush side's plane index and bevel flag, exactly — which is the
    /// whole of BRUSHSIDES except the texinfo column.
    /// </summary>
    /// <param name="name">The catalogue entry.</param>
    /// <returns>The running fact.</returns>
    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task EveryBrushSidePlaneAndBevelFlagMatchesStockExactly(string name)
    {
        (_, MapFile map) = await CompiledAsync(name);

        IReadOnlyList<EmittedBrushSide> stock = await StockBrushLumps.StockBrushSidesAsync(name);
        (_, List<EmittedBrushSide> managed) = StockBrushLumps.Emit(map);

        Assert.Equal(stock.Count, managed.Count);
        for (int i = 0; i < stock.Count; i++)
        {
            Assert.Equal(
                (stock[i].PlaneNumber, stock[i].Bevel),
                (managed[i].PlaneNumber, managed[i].Bevel));
        }
    }

    /// <summary>
    /// The pin on <see cref="EveryBrushSidePlaneAndBevelFlagMatchesStockExactly"/>
    /// is its contract, and on this catalogue it is inert.
    /// </summary>
    /// <param name="name">The catalogue entry.</param>
    /// <returns>The running fact.</returns>
    /// <remarks>
    /// Measured. Under <see cref="ComplianceOptions.Correct"/> the
    /// octahedron's PLANES lump moves: 20 of 52 planes differ in value
    /// (<c>StockLoadCatalogueTests.TheOctahedronsPlanesDisagreeWithStockUnderCorrect</c>).
    /// But every side still names the same plane INDEX, and every bevel flag is
    /// the same. The two normalise quirks move a plane's bits, never which
    /// plane a side refers to. This goes red the day a quirk starts to decide
    /// which index a side gets, and that is when the pin becomes load-bearing
    /// for this column.
    /// </remarks>
    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task TheSidePlaneIndicesAndBevelFlagsAreTheSameUnderCorrect(string name)
    {
        (_, MapFile map) = await CompiledAsync(name, ComplianceOptions.Correct);

        IReadOnlyList<EmittedBrushSide> stock = await StockBrushLumps.StockBrushSidesAsync(name);
        (_, List<EmittedBrushSide> managed) = StockBrushLumps.Emit(map);

        Assert.Equal(stock.Count, managed.Count);
        for (int i = 0; i < stock.Count; i++)
        {
            Assert.Equal(
                (stock[i].PlaneNumber, stock[i].Bevel),
                (managed[i].PlaneNumber, managed[i].Bevel));
        }
    }

    /// <summary>
    /// The contents column, exactly, on every map.
    /// </summary>
    /// <param name="name">The catalogue entry.</param>
    /// <returns>The running fact.</returns>
    /// <remarks>
    /// <para>
    /// <b>This used to be a waiver and is now an equality.</b> Stock's
    /// <c>ALL_VISIBLE_CONTENTS</c> is
    /// <c>LAST_VISIBLE_CONTENTS | (LAST_VISIBLE_CONTENTS-1)</c> =
    /// <c>0x80 | 0x7F</c> = <c>0xFF</c> (<c>bspflags.h:34-36</c>) — a
    /// contiguous mask of the low eight bits, whatever those bits are called.
    /// Phase 3a spelled the same mask as an OR of the NAMED flags, came to
    /// <c>0xBF</c> because <c>CONTENTS_BLOCKLOS</c> (<c>0x40</c>) has no entry
    /// in that list, and so fired the solid default at <c>map.cpp:2745</c> for
    /// every <c>%compileBlockLOS</c> brush.
    /// <see cref="MapFileLoader.AllVisibleContents"/> is now derived from
    /// <see cref="MapFileLoader.LastVisibleContents"/> the way stock derives
    /// it, and the whole column agrees.
    /// </para>
    /// <para>
    /// Compliance-independent: <c>0xFF</c> is both what stock computes and what
    /// is right, so this fact does not select a policy.
    /// </para>
    /// </remarks>
    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task TheContentsColumnMatchesStockExactly(string name)
    {
        (_, MapFile map) = await CompiledAsync(name);

        IReadOnlyList<EmittedBrush> stock = await StockBrushLumps.StockBrushesAsync(name);
        (List<EmittedBrush> managed, _) = StockBrushLumps.Emit(map);

        Assert.Equal(stock.Count, managed.Count);
        for (int i = 0; i < stock.Count; i++)
        {
            Assert.Equal(stock[i].Contents, managed[i].Contents);
        }
    }

    /// <summary>
    /// The one brush in the corpus that showed the defect, still there and now
    /// agreeing — so that a corpus which loses its <c>toolsblock_los</c> brush
    /// turns this fact red instead of quietly making the fact above vacuous.
    /// </summary>
    [StockLoadFact]
    public async Task TheCorpusStillContainsTheBlockLosBrushThatShowedTheDefect()
    {
        const string Name = "l1_tool_textures";

        (_, MapFile map) = await CompiledAsync(Name);

        IReadOnlyList<EmittedBrush> stock = await StockBrushLumps.StockBrushesAsync(Name);
        (List<EmittedBrush> managed, _) = StockBrushLumps.Emit(map);

        // The brush is still a BLOCKLOS brush, which is what made it the
        // witness; before the fix this port put 0x8000041 here.
        Assert.NotEqual(0, stock[9].Contents & (int)BrushContents.BlockLos);
        Assert.Equal(0x8000040, stock[9].Contents);
        Assert.Equal(0x8000040, managed[9].Contents);
    }

    /// <summary>
    /// <b>What this lane writes into these lumps, measured rather than
    /// assumed.</b> The CSG stage's only path into BRUSHES and BRUSHSIDES is
    /// <c>FixupAreaportalWaterBrushes</c>, so running the whole first world
    /// pass must leave both lumps exactly as the loader left them on every map
    /// that has no areaportal inside water — which is all 24 catalogue maps,
    /// the corpus map and both bevel shapes. The one map where it does
    /// something is the one built for it.
    /// </summary>
    [StockLoadFact]
    public async Task TheOnlyMapWhereThisStageTouchesEitherLumpIsTheAreaportalInWaterOne()
    {
        List<string> changed = [];

        foreach (string name in StockLoad.EntryNames)
        {
            (VbspContext context, MapFile map, _) = await StockLoad.LoadAsync(name, ComplianceOptions.Stock);
            (List<EmittedBrush> b0, List<EmittedBrushSide> s0) = StockBrushLumps.Emit(map);

            RunWorldPass(context, map);
            (List<EmittedBrush> b1, List<EmittedBrushSide> s1) = StockBrushLumps.Emit(map);

            if (!b0.SequenceEqual(b1) || !s0.SequenceEqual(s1))
            {
                changed.Add(name);
            }
        }

        Assert.Equal([AreaportalWaterShape.Name], changed);
    }

    /// <summary>
    /// <b>The one thing this lane decides in these lumps, against stock.</b>
    /// On <see cref="AreaportalWaterShape"/> the fixup ORs the water's contents
    /// into the areaportal MAP brush and copies the water's texinfos onto its
    /// MAP sides. Both reach BRUSHES and BRUSHSIDES, and both land on stock's
    /// answer: the contents comparison over this map is exact, and every one of
    /// the areaportal's sides ends up carrying a texinfo the water brush also
    /// carries.
    /// </summary>
    [StockLoadFact]
    public async Task TheFixupGivesTheAreaportalTheWatersContentsAndTexinfos()
    {
        (VbspContext context, MapFile map, _) = await StockLoad.LoadAsync(
            AreaportalWaterShape.Name, ComplianceOptions.Stock);

        MapBrush areaportal = Single(map, b => b.Contents == (int)BrushContents.AreaPortal);
        MapBrush water = Single(map, b => (b.Contents & (int)BrushContents.Water) != 0);

        HashSet<int> waterTexInfos = [];
        for (int i = 0; i < water.SideCount; i++)
        {
            waterTexInfos.Add(map.BrushSides[water.FirstSide + i].TexInfo);
        }

        RunWorldPass(context, map);

        Assert.NotEqual(0, areaportal.Contents & (int)BrushContents.Water);
        Assert.NotEqual(0, areaportal.Contents & (int)BrushContents.AreaPortal);

        for (int i = 0; i < areaportal.SideCount; i++)
        {
            Assert.Contains(map.BrushSides[areaportal.FirstSide + i].TexInfo, waterTexInfos);
        }

        // And the contents column of the whole lump is stock's, exactly.
        IReadOnlyList<EmittedBrush> stock =
            await StockBrushLumps.StockBrushesAsync(AreaportalWaterShape.Name);
        (List<EmittedBrush> managed, _) = StockBrushLumps.Emit(map);

        for (int i = 0; i < stock.Count; i++)
        {
            Assert.Equal(stock[i].Contents, managed[i].Contents);
        }
    }

    private static MapBrush Single(MapFile map, Func<MapBrush, bool> predicate)
    {
        MapBrush? found = null;
        foreach (MapBrush brush in map.Brushes)
        {
            if (!predicate(brush))
            {
                continue;
            }

            Assert.Null(found);
            found = brush;
        }

        Assert.NotNull(found);
        return found!;
    }

    /// <summary>The catalogue entries with a stock BSP beside them.</summary>
    public static TheoryData<string> Entries => StockLoad.Entries;

    private static Task<(VbspContext Context, MapFile Map)> CompiledAsync(string name) =>
        CompiledAsync(name, ComplianceOptions.Stock);

    private static async Task<(VbspContext Context, MapFile Map)> CompiledAsync(
        string name,
        ComplianceOptions compliance)
    {
        (VbspContext context, MapFile map, _) = await StockLoad.LoadAsync(name, compliance);
        RunWorldPass(context, map);
        return (context, map);
    }

    private static void RunWorldPass(VbspContext context, MapFile map)
    {
        MapEntity world = map.Entities[0];

        BspBuildContext build = new(context, map)
        {
            BrushStart = world.FirstBrush,
            BrushEnd = world.FirstBrush + world.BrushCount,
        };

        BspBlockGrid grid = BlockGrid.Clamp(BspBlockGrid.Full, map.Mins, map.Maxs);
        BlockGrid.BuildWorldPass(build, grid, map.Mins, map.Maxs, out _);
    }
}
