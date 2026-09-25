using System.Collections.Concurrent;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapFormats.Zip;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Bsp.MaterialPatch;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Validation;
using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.SurfaceContent;

/// <summary>
/// Phase 3g's lumps as the real driver writes them
/// (<see cref="SurfaceContentVbsp"/>), against stock vbsp's output on the
/// same VMF: I2 byte equality under Stock compliance, and I1 (the validator)
/// on the file.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the stage-isolated gates (<see cref="StaticPropStockTests"/> and
/// the rest), nothing here is stock's input: the tree, faces and texinfo
/// numbering are 3e's, so an overlay's texinfo index, a prop's leaf list and
/// a detail prop's leaf are only stock's if the whole compile is.
/// </para>
/// <para>
/// Masked, each for a measured reason: a static prop's lighting origin when
/// its flag is unset (stock leaves the stack slot uninitialised.
///); the detail records' padding and the
/// unused fields of model records (<see cref="DetailPropStockTests.Masked"/>),
/// the detail angles to <see cref="DetailPropStockTests.AngleToleranceDegrees"/>;
/// the HDR default cubemap's sphere-map face (uninitialised heap). The pak's
/// entry ORDER is not compared: stock's comes from a hash table.
/// </para>
/// </remarks>
public sealed class SurfaceContentDriverStockTests(SurfaceContentDriverStockTests.Compiles compiles)
    : IClassFixture<SurfaceContentDriverStockTests.Compiles>
{
    private static BspLump[] OverlayLumps => [BspLump.Overlays, BspLump.WaterOverlays, BspLump.OverlayFades];

    [P3gStockTheory]
    [MemberData(nameof(P3gStock.Entries), MemberType = typeof(P3gStock))]
    public async Task TheStaticPropGameLumpIsStocks(string name)
    {
        (BspData ours, BspData stock) = await compiles.GetAsync(name);

        Assert.Equal(StaticPropStockTests.WithoutUnsetLightingOrigins(StaticProps(stock)), StaticPropStockTests.WithoutUnsetLightingOrigins(StaticProps(ours)));
    }

    [P3gStockTheory]
    [MemberData(nameof(P3gStock.Entries), MemberType = typeof(P3gStock))]
    public async Task EveryDetailPropIsStocksApartFromTheAngles(string name)
    {
        (BspData ours, BspData stock) = await compiles.GetAsync(name);
        DetailPropLump mine = DetailProps(ours), theirs = DetailProps(stock);

        Assert.Equal(theirs.Props.Count, mine.Props.Count);
        Assert.Equal(theirs.ModelNames, mine.ModelNames);
        for (int i = 0; i < theirs.Props.Count; i++)
        {
            Assert.Equal(DetailPropStockTests.WithoutAngles(theirs.Props[i]), DetailPropStockTests.WithoutAngles(mine.Props[i]));
        }
    }

    [P3gStockTheory]
    [MemberData(nameof(P3gStock.Entries), MemberType = typeof(P3gStock))]
    public async Task EveryDetailPropsAnglesAreWithinTheFrozenTolerance(string name)
    {
        (BspData ours, BspData stock) = await compiles.GetAsync(name);
        DetailPropLump mine = DetailProps(ours), theirs = DetailProps(stock);

        Assert.Equal(theirs.Props.Count, mine.Props.Count);
        double worst = 0;
        for (int i = 0; i < theirs.Props.Count; i++)
        {
            worst = Math.Max(worst, Math.Abs(theirs.Props[i].Angles.X - mine.Props[i].Angles.X));
            worst = Math.Max(worst, Math.Abs(theirs.Props[i].Angles.Y - mine.Props[i].Angles.Y));
            worst = Math.Max(worst, Math.Abs(theirs.Props[i].Angles.Z - mine.Props[i].Angles.Z));
        }

        Assert.InRange(worst, 0, DetailPropStockTests.AngleToleranceDegrees);
    }

    [P3gStockTheory]
    [MemberData(nameof(P3gStock.Entries), MemberType = typeof(P3gStock))]
    public async Task TheOverlayLumpsAreStocksByteForByte(string name)
    {
        (BspData ours, BspData stock) = await compiles.GetAsync(name);

        foreach (BspLump lump in OverlayLumps)
        {
            Assert.Equal(stock[lump].Data.ToArray(), ours[lump].Data.ToArray());
        }
    }

    [P3gStockTheory]
    [MemberData(nameof(P3gStock.Entries), MemberType = typeof(P3gStock))]
    public async Task TheCubemapLumpIsStocksByteForByte(string name)
    {
        (BspData ours, BspData stock) = await compiles.GetAsync(name);

        Assert.Equal(stock[BspLump.Cubemaps].Data.ToArray(), ours[BspLump.Cubemaps].Data.ToArray());
    }

    [P3gStockTheory]
    [MemberData(nameof(P3gStock.Entries), MemberType = typeof(P3gStock))]
    public async Task TheTexInfoAndTexDataNamesAreStocksWithTheSurfaceContent(string name)
    {
        // The cubemap patches and overlay materials add texinfos and
        // texdata; CompactTexinfos must keep exactly stock's.
        // l2_cubemap_on_water_and_patch was PINNED here as an integration gap
        // until the p3j round: stock's AssignBottomWaterMaterialToFace reads
        // $bottommaterial with GetValueFromPatchedMaterial from the
        // CUBEMAP-PATCHED water ("This happens *after*
        // cubemap fixup"); the driver read the facts before AfterLoad and from
        // disk only, dropped the bottom face and never made its texinfo (the
        // last, dev/dev_waterbeneath2). It now holds like every other map.
        (BspData ours, BspData stock) = await compiles.GetAsync(name);

        Assert.Equal(stock[BspLump.TexInfo].Data.ToArray(), ours[BspLump.TexInfo].Data.ToArray());
        Assert.Equal(stock[BspLump.TexDataStringData].Data.ToArray(), ours[BspLump.TexDataStringData].Data.ToArray());
    }

    [P3gStockTheory]
    [MemberData(nameof(P3gStock.Entries), MemberType = typeof(P3gStock))]
    public async Task ThePakHoldsStocksFiles(string name)
    {
        (BspData ours, BspData stock) = await compiles.GetAsync(name);
        ZipArchiveReader mine = await P3gStock.StockPakAsync(ours), theirs = await P3gStock.StockPakAsync(stock);

        Assert.Equal(
            theirs.Entries.Select(e => e.Name).Order(StringComparer.Ordinal),
            mine.Entries.Select(e => e.Name).Order(StringComparer.Ordinal));
    }

    [P3gStockTheory]
    [MemberData(nameof(P3gStock.Entries), MemberType = typeof(P3gStock))]
    public async Task EveryPakFileIsStocksBytes(string name)
    {
        (BspData ours, BspData stock) = await compiles.GetAsync(name);
        ZipArchiveReader mine = await P3gStock.StockPakAsync(ours), theirs = await P3gStock.StockPakAsync(stock);

        Assert.NotEmpty(mine.Entries);
        foreach (ZipEntry entry in mine.Entries)
        {
            byte[] expected = theirs.Find(entry.Name)?.Data ?? throw new Xunit.Sdk.XunitException($"stock has no {entry.Name}");
            if (entry.Name.EndsWith(".hdr.vtf", StringComparison.Ordinal))
            {
                Assert.Equal(CubemapStockTests.WithoutSphereMap(expected), CubemapStockTests.WithoutSphereMap(entry.Data));
            }
            else
            {
                Assert.Equal(expected, entry.Data);
            }
        }
    }

    [P3gStockTheory]
    [MemberData(nameof(P3gStock.Entries), MemberType = typeof(P3gStock))]
    public async Task TheFileValidates(string name)
    {
        (BspData ours, _) = await compiles.GetAsync(name);
        ValidationReport report = await BspValidator.CheckAsync(ours, CancellationToken.None);

        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics.Take(10).Select(d => $"{d.Code} {d.Message}")));
    }

    [P3gStockTheory]
    [MemberData(nameof(P3gStock.Entries), MemberType = typeof(P3gStock))]
    public async Task TheCorrectCompileValidatesWithTheSameSurfaceContentFiles(string name)
    {
        // Q17: Correct is the default and must make a loadable file. What it
        // changes against Stock is measured, not asserted away: the two 3g
        // quirks that reach the fixture maps (CubemapIgnoresPatchMaterials
        // adds a patch; DetailOrientationNormalise moves detail angles).
        (BspData stock, _) = await compiles.GetAsync(name);
        BspData correct = await Compiles.CompileAsync(name, ComplianceOptions.Correct);

        ValidationReport report = await BspValidator.CheckAsync(correct, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics.Take(10).Select(d => $"{d.Code} {d.Message}")));

        ZipArchiveReader stockPak = await P3gStock.StockPakAsync(stock), correctPak = await P3gStock.StockPakAsync(correct);
        HashSet<string> added = [.. correctPak.Entries.Select(e => e.Name).Except(stockPak.Entries.Select(e => e.Name))];
        Assert.Empty(stockPak.Entries.Select(e => e.Name).Except(correctPak.Entries.Select(e => e.Name)));
        Assert.All(added, n => Assert.EndsWith(".vmt", n, StringComparison.Ordinal));
    }

    private static StaticPropLump StaticProps(BspData bsp) =>
        StaticPropLump.Read(bsp.GameLumps.Single(g => g.Id == GameLumpId.MakeId(GameLumpId.StaticProps)));

    private static DetailPropLump DetailProps(BspData bsp) =>
        DetailPropLump.Read(bsp.GameLumps.Single(g => g.Id == GameLumpId.MakeId(GameLumpId.DetailProps)));

    /// <summary>One driver compile per entry, shared by the facts.</summary>
    public sealed class Compiles
    {
        private readonly ConcurrentDictionary<string, Lazy<Task<(BspData Ours, BspData Stock)>>> _compiles = new(StringComparer.Ordinal);

        public Task<(BspData Ours, BspData Stock)> GetAsync(string name) =>
            _compiles.GetOrAdd(name, n => new Lazy<Task<(BspData, BspData)>>(() => PairAsync(n))).Value;

        private static async Task<(BspData Ours, BspData Stock)> PairAsync(string name) =>
            (await CompileAsync(name, ComplianceOptions.Stock), await P3gStock.StockBspAsync(name));

        public static async Task<BspData> CompileAsync(string name, ComplianceOptions compliance)
        {
            (VbspContext context, MapFile map, _) = await P3gStock.LoadAsync(name, compliance);

            VmfDocument document;
            await using (FileStream stream = File.OpenRead(Path.Combine(P3gStock.Directory!, name + ".vmf")))
            {
                document = await VmfDocument.ReadAsync(stream);
            }

            VbspResult result = await SurfaceContentVbsp.CompileAsync(map, context, document);
            Assert.NotNull(result.Bsp);

            // Through the file format both ways, as vvis would read it.
            using MemoryStream written = new();
            await BspFile.SaveAsync(result.Bsp!, written);
            written.Position = 0;
            return await BspFile.LoadAsync(written);
        }
    }
}
