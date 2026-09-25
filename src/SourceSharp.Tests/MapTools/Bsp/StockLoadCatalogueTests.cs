using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp;

/// <summary>
/// The load stage held against stock vbsp on the whole catalogue.
/// </summary>
/// <remarks>
/// <para>
/// Every fact here compares against a number STOCK produced from the same VMF,
/// not against a number reasoned about. That distinction has already mattered
/// on this plan twice: two catalogue expectations were wrong before stock was
/// run.
/// </para>
/// <para>
/// One behaviour per fact, so a failure names which of the eight counts moved
/// rather than saying "the load is different".
/// </para>
/// </remarks>
public class StockLoadCatalogueTests
{
    /// <summary>The catalogue entries with a stock verbose log.</summary>
    public static TheoryData<string> Entries => StockLoad.Entries;

    /// <summary>
    /// The two exact, in-order lump comparisons skip a map whose tables stock
    /// compacted. This is the guard that stops them from skipping everything.
    /// </summary>
    /// <remarks>
    /// Without it, a change that made every map compact — or a bug in reading
    /// the log — would turn both facts into unconditional passes, which is the
    /// exact failure mode this repo has been bitten by before.
    /// </remarks>
    [StockLoadFact]
    public void SomeMapsExerciseTheExactInOrderComparison()
    {
        int uncompacted = 0;

        foreach (string name in StockLoad.EntryNames)
        {
            (_, _, int before, int after) = StockLoad.Compaction(name);

            if (before == after)
            {
                uncompacted++;
            }
        }

        Assert.True(
            uncompacted >= 4,
            $"only {uncompacted} maps had their texdata table left uncompacted by stock, "
            + "so the exact in-order comparison is barely exercised");
    }

    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task ThePlaneCountMatchesStock(string name)
    {
        StockLoadCounts stock = StockLoad.Counts(name);
        (VbspContext _, MapFile _, MapLoadStatistics ours) = await StockLoad.LoadAsync(name, ComplianceOptions.Stock);

        Assert.Equal(stock.Planes, ours.Planes);
    }

    /// <summary>
    /// The PLANES lump, element for element, in insertion order, on the exact
    /// bytes.
    /// </summary>
    /// <param name="name">The entry name.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// <b>This is the gate this lane exists for.</b> <c>EmitPlanes</c>
    /// copies <c>g_MainMap-&gt;mapplanes</c> into
    /// LUMP_PLANES index for index and discards nothing — its own comment says
    /// "There is no oportunity to discard planes, because all of the original
    /// brushes will be saved in the map." So stock's lump IS its plane table,
    /// and nothing is matched by value or remapped: plane <c>i</c> here must be
    /// plane <c>i</c> there.
    /// </para>
    /// <para>
    /// A PREFIX, and measurably so. The later stages append to the same table.
    /// CSG, the BSP build and face merging all call <c>FindFloatPlane</c> — so
    /// stock's lump is longer than the load-time table by however many they
    /// added (24 of 40 on <c>l0_unit_cube</c>). The table is append-only, so
    /// the load-time entries are its first N, and those N are what this lane
    /// owns and what this compares.
    /// </para>
    /// <para>
    /// The comparison is on BIT PATTERNS and not on float equality, which is
    /// what makes the sign of zero part of it: an axial plane's opposite has
    /// two zero components, and <c>CreateNewFloatPlane</c> builds them with
    /// <c>0 - x</c> rather than <c>-x</c>. Unary negation
    /// would put <c>-0.0f</c> in the lump, which is the same number and
    /// different bytes — and this fact is where that is caught.
    /// </para>
    /// </remarks>
    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task ThePlanesLumpMatchesStockElementForElement(string name)
    {
        IReadOnlyList<DPlane> stock =
            await StockLoad.StockPlanesAsync(name);
        (VbspContext _, MapFile map, MapLoadStatistics _) = await StockLoad.LoadAsync(name, ComplianceOptions.Stock);

        Assert.NotEmpty(stock);
        Assert.True(
            stock.Count >= map.Planes.Count,
            $"{name}: stock's lump has {stock.Count} planes and the load alone built "
                + $"{map.Planes.Count} -- the load-time table must be a PREFIX of it");

        for (int i = 0; i < map.Planes.Count; i++)
        {
            Plane ours = map.Planes[i];

            Assert.True(
                SameBits(stock[i].Normal.X, ours.Normal.X) &&
                SameBits(stock[i].Normal.Y, ours.Normal.Y) &&
                SameBits(stock[i].Normal.Z, ours.Normal.Z) &&
                SameBits(stock[i].Dist, ours.Dist) &&
                stock[i].Type == (int)map.Planes.TypeOf(i),
                $"{name}: plane {i} -- stock {stock[i].Normal} @ {stock[i].Dist} "
                    + $"type {stock[i].Type}, ours {ours.Normal} @ {ours.Dist} "
                    + $"type {(int)map.Planes.TypeOf(i)}");
        }
    }

    /// <summary>
    /// <b>The pin above is load-bearing, and this fact proves it.</b> Under
    /// <see cref="ComplianceOptions.Correct"/> the PLANES lump does NOT match
    /// stock on the octahedron, and the first disagreement is the edge bevel
    /// the shape was built for.
    /// </summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// Twenty of the map's 52 load-time planes differ, starting at 28 — which
    /// is the one Phase 3b reported: stock
    /// <c>(0.70710677, 0, -0.70710677) @ 452.5504</c> type 3, Correct
    /// <c>(0.70710677, 0, -0.7071068) @ 452.5497</c> type 5. The STORED TYPE
    /// moves because the normal's |x| and |z| are mathematically equal and
    /// <c>PlaneTypeForNormal</c>'s <c>ax &gt;= az</c> is decided by whichever
    /// normalise ran.
    /// </para>
    /// <para>
    /// Stated as a fact rather than a comment because an unexercised pin is
    /// indistinguishable from a decorative one: if a later change made Correct
    /// and Stock agree here, the gate above would still pass and would have
    /// stopped measuring anything.
    /// </para>
    /// </remarks>
    [StockLoadFact]
    public async Task TheOctahedronsPlanesDisagreeWithStockUnderCorrect()
    {
        IReadOnlyList<int> differing =
            await PlanesDifferingFromStockAsync(Octahedron, ComplianceOptions.Correct);

        Assert.Equal(20, differing.Count);
        Assert.Equal(28, differing[0]);

        IReadOnlyList<DPlane> stock = await StockLoad.StockPlanesAsync(Octahedron);
        (VbspContext _, MapFile map, MapLoadStatistics _) =
            await StockLoad.LoadAsync(Octahedron, ComplianceOptions.Correct);

        Assert.Equal((int)PlaneType.AnyX, stock[28].Type);
        Assert.Equal(PlaneType.AnyZ, map.Planes.TypeOf(28));
    }

    /// <summary>
    /// Neither normalise quirk alone is enough, so the two are genuinely
    /// separate sites.
    /// </summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// Measured on <c>x0_octahedron</c>, counting load-time planes that differ
    /// from stock's lump in any bit or in the stored type:
    /// </para>
    /// <list type="table">
    /// <item><description>both Correct — 20 of 52</description></item>
    /// <item><description>
    /// Stock but <see cref="StockQuirk.EdgeBevelNormalise"/> corrected — 12
    /// </description></item>
    /// <item><description>
    /// Stock but <see cref="StockQuirk.BaseWindingNormalise"/> corrected — 18
    /// </description></item>
    /// <item><description>both Stock — 0</description></item>
    /// </list>
    /// <para>
    /// Folding them into one member would lose the ability to attribute a moved
    /// plane to one or the other, which is what the middle two rows are for.
    /// </para>
    /// </remarks>
    [StockLoadFact]
    public async Task NeitherNormaliseQuirkAloneMakesTheOctahedronsPlanesMatch()
    {
        IReadOnlyList<int> withoutEdge = await PlanesDifferingFromStockAsync(
            Octahedron, ComplianceOptions.Stock.Flipping(StockQuirk.EdgeBevelNormalise));
        IReadOnlyList<int> withoutBase = await PlanesDifferingFromStockAsync(
            Octahedron, ComplianceOptions.Stock.Flipping(StockQuirk.BaseWindingNormalise));
        IReadOnlyList<int> both =
            await PlanesDifferingFromStockAsync(Octahedron, ComplianceOptions.Stock);

        Assert.Equal(12, withoutEdge.Count);
        Assert.Equal(18, withoutBase.Count);
        Assert.Empty(both);
    }

    /// <summary>
    /// Correcting the edge-bevel normalise alone still lands the stored TYPE,
    /// and correcting the base-winding one alone does not.
    /// </summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// The mechanism, separated: an exact divide of a symmetric cross product
    /// gives components that are equal in magnitude, so
    /// <c>PlaneTypeForNormal</c> answers <c>PLANE_ANYX</c> — but the DISTANCE
    /// is taken from a <c>BaseWindingForPlane</c> point and is still stock's
    /// only when that one is stock's too. Leaving the base-winding one correct
    /// moves the point, and then both the normal and the type miss.
    /// </remarks>
    [StockLoadFact]
    public async Task TheStoredTypeFollowsTheEdgeBevelNormaliseAndTheDistanceTheBaseWindingOne()
    {
        IReadOnlyList<DPlane> stock = await StockLoad.StockPlanesAsync(Octahedron);

        (VbspContext _, MapFile withoutEdge, MapLoadStatistics _) = await StockLoad.LoadAsync(
            Octahedron, ComplianceOptions.Stock.Flipping(StockQuirk.EdgeBevelNormalise));
        (VbspContext _, MapFile withoutBase, MapLoadStatistics _) = await StockLoad.LoadAsync(
            Octahedron, ComplianceOptions.Stock.Flipping(StockQuirk.BaseWindingNormalise));

        Assert.Equal((int)PlaneType.AnyX, stock[28].Type);
        Assert.Equal(PlaneType.AnyX, withoutEdge.Planes.TypeOf(28));
        Assert.Equal(PlaneType.AnyZ, withoutBase.Planes.TypeOf(28));

        // And neither lands the bits: the type is not the whole plane.
        Assert.False(SameBits(stock[28].Dist, withoutEdge.Planes[28].Dist));
        Assert.False(SameBits(stock[28].Dist, withoutBase.Planes[28].Dist));
    }

    /// <summary>The shape whose edge bevel exercises both normalise quirks.</summary>
    private const string Octahedron = "x0_octahedron";

    /// <summary>
    /// The load-time planes that differ from stock's lump in any bit of the
    /// normal or distance, or in the stored type.
    /// </summary>
    /// <param name="name">The catalogue entry.</param>
    /// <param name="compliance">The compliance to load under.</param>
    /// <returns>Their indices, in order.</returns>
    private static async Task<IReadOnlyList<int>> PlanesDifferingFromStockAsync(
        string name,
        ComplianceOptions compliance)
    {
        IReadOnlyList<DPlane> stock = await StockLoad.StockPlanesAsync(name);
        (VbspContext _, MapFile map, MapLoadStatistics _) =
            await StockLoad.LoadAsync(name, compliance);

        List<int> differing = [];
        for (int i = 0; i < map.Planes.Count; i++)
        {
            Plane ours = map.Planes[i];

            bool same =
                SameBits(stock[i].Normal.X, ours.Normal.X) &&
                SameBits(stock[i].Normal.Y, ours.Normal.Y) &&
                SameBits(stock[i].Normal.Z, ours.Normal.Z) &&
                SameBits(stock[i].Dist, ours.Dist) &&
                stock[i].Type == (int)map.Planes.TypeOf(i);

            if (!same)
            {
                differing.Add(i);
            }
        }

        return differing;
    }

    /// <summary>
    /// Every zero component of an AXIAL plane is <c>+0.0f</c> in stock's own
    /// lump, which is what makes the <c>0 - x</c> spelling observable rather
    /// than a matter of taste.
    /// </summary>
    /// <param name="name">The entry name.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// Axial only, and the restriction was measured rather than assumed:
    /// <c>x0_pyramid</c> has a NON-axial plane carrying a <c>-0.0f</c>
    /// component, because <c>CrossProduct</c> can produce one when the two
    /// products it subtracts are <c>-0.0f</c> and <c>+0.0f</c>. Both stock and
    /// this port produce it — the element-for-element fact above proves they
    /// agree — so "no negative zero anywhere" is simply not true of either.
    /// </para>
    /// <para>
    /// An axial plane's normal, by contrast, comes from <c>SnapVector</c>,
    /// which clears the vector and sets one component,
    /// so its zeros are <c>+0.0f</c>; and its
    /// opposite's zeros stay <c>+0.0f</c> ONLY because the pair is built with
    /// <c>0 - x</c>. That is the claim, and it is worth its own fact because
    /// the element-for-element comparison would also pass if both sides were
    /// wrong in the same direction on a map with no axial planes at all.
    /// </para>
    /// </remarks>
    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task StockAxialPlanesCarryNoNegativeZero(string name)
    {
        IReadOnlyList<DPlane> stock = await StockLoad.StockPlanesAsync(name);
        (VbspContext _, MapFile map, MapLoadStatistics _) = await StockLoad.LoadAsync(name, ComplianceOptions.Stock);

        int zeroComponents = 0;

        // The load-time PREFIX only: the planes later stages append come from
        // clipping and merging, and nothing here has established what signs of
        // zero those carry.
        for (int p = 0; p < map.Planes.Count; p++)
        {
            if (stock[p].Type >= (int)PlaneType.AnyX)
            {
                continue;
            }

            DPlane plane = stock[p];

            foreach (float component in new[] { plane.Normal.X, plane.Normal.Y, plane.Normal.Z })
            {
                if (component != 0f)
                {
                    continue;
                }

                zeroComponents++;
                Assert.Equal(0, BitConverter.SingleToInt32Bits(component));
            }
        }

        // A map with no axial plane at all would make the fact vacuous.
        Assert.True(zeroComponents > 0, $"{name}: stock's PLANES lump has no axial plane");
    }

    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task TheBrushCountMatchesStock(string name)
    {
        StockLoadCounts stock = StockLoad.Counts(name);
        (VbspContext _, MapFile _, MapLoadStatistics ours) = await StockLoad.LoadAsync(name, ComplianceOptions.Stock);

        Assert.Equal(stock.Brushes, ours.Brushes);
    }

    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task TheSideCountMatchesStock(string name)
    {
        StockLoadCounts stock = StockLoad.Counts(name);
        (VbspContext _, MapFile _, MapLoadStatistics ours) = await StockLoad.LoadAsync(name, ComplianceOptions.Stock);

        Assert.Equal(stock.TotalSides, ours.TotalSides);
    }

    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task TheEntityCountMatchesStock(string name)
    {
        StockLoadCounts stock = StockLoad.Counts(name);
        (VbspContext _, MapFile _, MapLoadStatistics ours) = await StockLoad.LoadAsync(name, ComplianceOptions.Stock);

        Assert.Equal(stock.Entities, ours.Entities);
    }

    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task TheBevelCountsMatchStock(string name)
    {
        StockLoadCounts stock = StockLoad.Counts(name);
        (VbspContext _, MapFile _, MapLoadStatistics ours) = await StockLoad.LoadAsync(name, ComplianceOptions.Stock);

        Assert.Equal((stock.BoxBevels, stock.EdgeBevels), (ours.BoxBevels, ours.EdgeBevels));
    }

    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task TheClipBrushAndAreaPortalCountsMatchStock(string name)
    {
        StockLoadCounts stock = StockLoad.Counts(name);
        (VbspContext _, MapFile _, MapLoadStatistics ours) = await StockLoad.LoadAsync(name, ComplianceOptions.Stock);

        Assert.Equal(
            (stock.ClipBrushes, stock.AreaPortals),
            (ours.ClipBrushes, ours.AreaPortals));
    }

    /// <summary>
    /// The TEXDATA lump, compared as an ordered list where stock's own log says
    /// the compaction removed nothing.
    /// </summary>
    /// <param name="name">The entry name.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// vbsp compacts the texture tables at the END of a compile
    /// ("Reduced N texdatas to M"), so the final lump is only equal to the
    /// load-time table when nothing was dropped. Where it was, the weaker
    /// containment fact below applies instead — and saying which of the two a
    /// map gets is decided by stock's own numbers, not by whether the
    /// comparison happens to pass.
    /// </remarks>
    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task TheTexDataLumpMatchesStockExactlyWhereNothingWasCompactedAway(string name)
    {
        (_, _, int before, int after) = StockLoad.Compaction(name);

        if (before != after)
        {
            // Compaction dropped entries; the containment fact covers this map.
            // The test is on STOCK'S OWN LOG and not on the two counts being
            // equal, because a table this port did not compact can be the same
            // length as one stock did -- ss_sandbox is 20 down to 16 and this
            // port builds 16, which an equal-counts guard read as agreement.
            return;
        }

        IReadOnlyList<(DTexData Entry, string Name)> stock =
            await StockLoad.StockTexDataAsync(name);
        (VbspContext context, MapFile map, MapLoadStatistics _) = await StockLoad.LoadAsync(name, ComplianceOptions.Stock);

        int loaded = LoadBuiltPrefix(name, map, [.. stock.Select(s => s.Name)], context.TexDatas.Count);

        for (int i = 0; i < loaded; i++)
        {
            Assert.Equal(stock[i].Name, context.TexDatas.NameOf(i));
            Assert.Equal(stock[i].Entry.Width, context.TexDatas[i].Width);
            Assert.Equal(stock[i].Entry.Height, context.TexDatas[i].Height);
            Assert.Equal(stock[i].Entry.ViewWidth, context.TexDatas[i].ViewWidth);
            Assert.Equal(stock[i].Entry.ViewHeight, context.TexDatas[i].ViewHeight);
            Assert.Equal(stock[i].Entry.Reflectivity, context.TexDatas[i].Reflectivity);
        }
    }

    /// <summary>
    /// Every TEXDATA entry stock kept exists in this port's table, by name.
    /// </summary>
    /// <param name="name">The entry name.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// The containment holds whether or not compaction removed anything,
    /// because compaction only ever drops and renumbers — it cannot invent an
    /// entry this port did not build.
    /// </para>
    /// <para>
    /// Scoped to the materials the VMF itself names, for the reason
    /// <see cref="EveryTexInfoStockKeptWasBuiltHereBitForBit"/> gives: stock
    /// adds texdatas after loading for surfaces the map did not author, and
    /// those are later lanes'. The fact also asserts the other direction — that
    /// every material the VMF DID name is in this port's table — so the
    /// scoping cannot hide a missing one.
    /// </para>
    /// </remarks>
    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task EveryTexDataStockKeptExistsHere(string name)
    {
        IReadOnlyList<(DTexData Entry, string Name)> stock =
            await StockLoad.StockTexDataAsync(name);
        (VbspContext context, MapFile map, MapLoadStatistics _) = await StockLoad.LoadAsync(name, ComplianceOptions.Stock);

        HashSet<string> authored = AuthoredMaterials(map);

        foreach ((DTexData _, string material) in stock)
        {
            if (!authored.Contains(material))
            {
                continue;
            }

            Assert.True(
                context.TexDatas.Find(material) >= 0,
                $"{name}: stock has texdata \"{material}\" and this port does not");
        }

        foreach (string material in authored)
        {
            Assert.True(
                context.TexDatas.Find(material) >= 0,
                $"{name}: the VMF names material \"{material}\" and this port built no texdata for it");
        }
    }

    private static bool SameBits(float a, float b) =>
        BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    private static HashSet<string> AuthoredMaterials(MapFile map)
    {
        HashSet<string> authored = new(StringComparer.OrdinalIgnoreCase);

        foreach (BrushTexture texture in map.SideBrushTextures)
        {
            if (!string.IsNullOrEmpty(texture.Name))
            {
                authored.Add(texture.Name);
            }
        }

        return authored;
    }

    /// <summary>
    /// Every TEXDATA entry's dimensions and reflectivity match stock, for every
    /// material whose VMT actually names a <c>$basetexture</c>.
    /// </summary>
    /// <param name="name">The entry name.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// The values come from <c>MapTools/Materials</c>, not from this lane.
    /// what this lane decides is which names land in the table and in what
    /// order. The check is still here because a texdata whose dimensions are
    /// wrong is a wrong lump whoever caused it.
    /// </para>
    /// <para>
    /// The exception is CHARACTERISED rather than listed by name: a material
    /// with no <c>$basetexture</c> gives this port's reader no VTF header to
    /// read, so it falls back to 128x128 and a zero reflectivity where stock's
    /// material system asks the shader for a representative texture and gets a
    /// real one. <c>nature/water_canals_cheap001</c> is the case in this
    /// catalogue. Asserting the SHAPE of the exception rather than the name
    /// means a new disagreement with a different cause still fails.
    /// </para>
    /// </remarks>
    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task TexDataDimensionsMatchStockWhereTheMaterialHasABaseTexture(string name)
    {
        IReadOnlyList<(DTexData Entry, string Name)> stock =
            await StockLoad.StockTexDataAsync(name);
        (VbspContext context, MapFile _, MapLoadStatistics _) = await StockLoad.LoadAsync(name, ComplianceOptions.Stock);

        List<string> unexplained = [];

        foreach ((DTexData entry, string material) in stock)
        {
            int index = context.TexDatas.Find(material);
            if (index < 0)
            {
                continue;
            }

            DTexData ours = context.TexDatas[index];

            if (entry.Width == ours.Width &&
                entry.Height == ours.Height &&
                entry.Reflectivity == ours.Reflectivity)
            {
                continue;
            }

            MaterialFacts facts = await context.Materials.GetAsync(material);

            if (facts.BaseTexture is null)
            {
                // The characterised exception: no $basetexture, so no VTF
                // header to take dimensions or reflectivity from.
                continue;
            }

            unexplained.Add(
                $"{material}: stock {entry.Width}x{entry.Height} {entry.Reflectivity}, "
                + $"ours {ours.Width}x{ours.Height} {ours.Reflectivity}");
        }

        Assert.Empty(unexplained);
    }

    /// <summary>
    /// The TEXDATA_STRING_DATA and _TABLE lumps, byte for byte, where nothing
    /// was compacted away.
    /// </summary>
    /// <param name="name">The entry name.</param>
    /// <returns>A task.</returns>
    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task TheTexDataStringLumpsMatchStockExactlyWhereNothingWasCompactedAway(string name)
    {
        (_, _, int before, int after) = StockLoad.Compaction(name);

        if (before != after)
        {
            // The string lumps are keyed to the texdata table, so they are only
            // comparable in order where the texdata table was not compacted.
            return;
        }

        (int[] offsets, byte[] data) =
            await StockLoad.StockStringsAsync(name);
        IReadOnlyList<(DTexData Entry, string Name)> stock =
            await StockLoad.StockTexDataAsync(name);
        (VbspContext context, MapFile map, MapLoadStatistics _) = await StockLoad.LoadAsync(name, ComplianceOptions.Stock);

        int loaded = LoadBuiltPrefix(name, map, [.. stock.Select(s => s.Name)], context.TexDatas.Count);
        byte[] ours = context.Strings.ToData();

        // Strings are appended as texdatas are created, so the load's strings
        // are the prefix of stock's up to the first string a later stage added.
        Assert.Equal(offsets[..loaded], context.Strings.Offsets.Take(loaded));
        Assert.Equal(loaded == offsets.Length ? data : data[..offsets[loaded]], ours);
    }

    /// <summary>
    /// How many of stock's final TEXDATA entries the LOAD built: all of them,
    /// or all but those a later stage appended.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Nothing was compacted away" makes stock's final table the load-time
    /// table only when no stage after the load created a texdata. Several do,
    /// and they APPEND: <c>Overlay_EmitOverlayFace</c> makes the overlay
    /// material's texdata in <c>EndBSPFile</c>, and
    /// <c>AssignBottomWaterMaterialToFace</c> the water's underside in
    /// <c>MakeFaces</c>. l1_overlay is the map that
    /// shows it once the reference is compiled against the right content:
    /// "Reduced 4 texdatas to 4" with three made by the load.
    /// </para>
    /// <para>
    /// So the exact comparison covers the entries the load made, and the rest
    /// are held to what makes them legitimate: each is a material no brush side
    /// of the VMF names. A side's material the load failed to create would be
    /// one of them, so the scoping cannot hide a missing entry; the later
    /// entries themselves are gated where they are made
    /// (<c>VbspStockCatalogueTests</c>).
    /// </para>
    /// </remarks>
    private static int LoadBuiltPrefix(string name, MapFile map, IReadOnlyList<string> stock, int loaded)
    {
        Assert.True(
            stock.Count >= loaded,
            $"{name}: this port's load made {loaded} texdatas and stock's whole compile only {stock.Count}");

        HashSet<string> authored = AuthoredMaterials(map);
        for (int i = loaded; i < stock.Count; i++)
        {
            Assert.False(
                authored.Contains(stock[i]),
                $"{name}: stock's texdata {i} \"{stock[i]}\" is a material a brush side names, so the load should have made it");
        }

        return loaded;
    }

    /// <summary>
    /// Every TEXINFO entry stock kept is, bit for bit, one this port built.
    /// </summary>
    /// <param name="name">The entry name.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// The comparison is BY VALUE and not by index, because
    /// <c>CompactTexinfoArray</c> renumbers — stock's log says "Reduced 12
    /// texinfos to 7" on most of these maps. It is still exact: a texinfo is 16
    /// floats and two ints, compared with <see cref="TexInfoTable.AreIdentical"/>,
    /// which is stock's own 72-byte <c>memcmp</c>.
    /// </para>
    /// <para>
    /// Texdata INDICES differ between the two tables after compaction, so the
    /// entry is matched on the texdata's NAME and the vectors and flags
    /// compared around it.
    /// </para>
    /// <para>
    /// Scoped to the materials the VMF ITSELF names. Stock creates texinfos
    /// after loading for surfaces the map did not author — the underside of a
    /// water volume takes the water material's <c>$bottommaterial</c>, which is
    /// how <c>dev/dev_waterbeneath2</c> appears in three of these maps and in
    /// none of their VMFs — and those belong to later lanes. The set that is
    /// skipped is asserted to contain nothing the VMF named, so this cannot
    /// quietly widen.
    /// </para>
    /// </remarks>
    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task EveryTexInfoStockKeptWasBuiltHereBitForBit(string name)
    {
        IReadOnlyList<TexInfo> stockTexInfo =
            await StockLoad.StockTexInfoAsync(name);
        IReadOnlyList<(DTexData Entry, string Name)> stockTexData =
            await StockLoad.StockTexDataAsync(name);
        (VbspContext context, MapFile map, MapLoadStatistics _) = await StockLoad.LoadAsync(name, ComplianceOptions.Stock);

        HashSet<string> authored = AuthoredMaterials(map);
        List<string> missing = [];
        List<string> skipped = [];

        foreach (TexInfo stock in stockTexInfo)
        {
            string material = stockTexData[stock.TexData].Name;

            if (!authored.Contains(material))
            {
                skipped.Add(material);
                continue;
            }

            int ourTexData = context.TexDatas.Find(material);

            if (ourTexData < 0)
            {
                missing.Add(material);
                continue;
            }

            TexInfo wanted = stock;
            wanted.TexData = ourTexData;

            if (context.TexInfos.Find(wanted) < 0)
            {
                missing.Add($"{material} flags 0x{stock.Flags:x}");
            }
        }

        // The guard that stops the scoping from emptying the fact: a map whose
        // VMF named no materials would make `missing` empty for the wrong
        // reason. `skipped` is reported so a reader can see what the scope
        // excluded rather than having to infer it.
        Assert.NotEmpty(authored);
        Assert.Empty(missing);
        Assert.All(skipped, material => Assert.DoesNotContain(material, authored));
    }
}
