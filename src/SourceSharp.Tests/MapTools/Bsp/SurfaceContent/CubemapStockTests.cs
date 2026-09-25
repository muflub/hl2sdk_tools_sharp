using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Zip;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Cubemaps;
using SourceSharp.MapTools.Bsp.MaterialPatch;
using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.SurfaceContent;

/// <summary>
/// The cubemap half of 3g against stock vbsp's own output, per catalogue
/// entry (I3: the stock map, loaded by 3a, is the input).
/// </summary>
public class CubemapStockTests
{
    [P3gStockTheory]
    [MemberData(nameof(P3gStock.Entries), MemberType = typeof(P3gStock))]
    public async Task TheCubemapLumpIsStocks(string name)
    {
        (VbspContext context, _, _) = await P3gStock.LoadAsync(name);
        BspData stock = await P3gStock.StockBspAsync(name);

        Assert.Equal(stock[BspLump.Cubemaps].Data.ToArray(), CubemapSampleLump.ToBytes(context.CubemapSamples));
    }

    [P3gStockTheory]
    [MemberData(nameof(P3gStock.Entries), MemberType = typeof(P3gStock))]
    public async Task ThePakHoldsStocksCubemapFilesAndPatches(string name)
    {
        MapPakFile pak = await RunAsync(name);
        ZipArchiveReader stock = await P3gStock.StockPakAsync(await P3gStock.StockBspAsync(name));

        // The water depth patches are the collision emitter's (
        // lane 3h), and come from a stage this does not run. The
        // WorldVertexTransition patches themselves are 3a's output (written
        // here only so the cubemap pass sees them): on sdk_ctf_2fort 3a
        // writes one stock does not (blendgroundtograss003), which is 3a's
        // finding, not this gate's. Cubemap patches OF a wvt patch are kept.
        Assert.Equal(
            stock.Entries.Select(e => e.Name).Where(Ours).Order(StringComparer.Ordinal),
            pak.Entries.Select(e => e.Name).Where(Ours).Order(StringComparer.Ordinal));
    }

    [P3gStockTheory]
    [MemberData(nameof(P3gStock.Entries), MemberType = typeof(P3gStock))]
    public async Task ThePakIsNotVacuous(string name)
    {
        // Every catalogue and L4 map writes at least one 3g file: the default
        // cubemaps, or cubemap patches where the skybox refuses.
        MapPakFile pak = await RunAsync(name);

        Assert.Contains(pak.Entries, e => Ours(e.Name));
    }

    [P3gStockTheory]
    [MemberData(nameof(P3gStock.Entries), MemberType = typeof(P3gStock))]
    public async Task EveryPatchedVmtIsStocksBytes(string name)
    {
        MapPakFile pak = await RunAsync(name);
        ZipArchiveReader stock = await P3gStock.StockPakAsync(await P3gStock.StockBspAsync(name));

        foreach (ZipEntry entry in pak.Entries.Where(e => e.Name.EndsWith(".vmt", StringComparison.Ordinal) && Ours(e.Name)))
        {
            Assert.Equal(stock.Find(entry.Name)!.Data, entry.Data);
        }
    }

    [P3gStockTheory]
    [MemberData(nameof(P3gStock.Entries), MemberType = typeof(P3gStock))]
    public async Task EveryLdrCubemapIsStocksBytes(string name)
    {
        MapPakFile pak = await RunAsync(name);
        ZipArchiveReader stock = await P3gStock.StockPakAsync(await P3gStock.StockBspAsync(name));

        List<ZipEntry> ldr = [.. pak.Entries.Where(e => e.Name.EndsWith(".vtf", StringComparison.Ordinal) && !e.Name.EndsWith(".hdr.vtf", StringComparison.Ordinal))];

        // Not vacuous: the count must be stock's, and stock writes none only
        // when the skybox's faces disagree (the TF2 L4 maps).
        Assert.Equal(stock.Entries.Count(e => e.Name.EndsWith(".vtf", StringComparison.Ordinal) && !e.Name.EndsWith(".hdr.vtf", StringComparison.Ordinal)), ldr.Count);
        foreach (ZipEntry entry in ldr)
        {
            Assert.Equal(stock.Find(entry.Name)!.Data, entry.Data);
        }
    }

    [P3gStockTheory]
    [MemberData(nameof(P3gStock.Entries), MemberType = typeof(P3gStock))]
    public async Task EveryHdrCubemapIsStocksBytesOutsideTheSphereMap(string name)
    {
        // The sphere map face of the HDR file is uninitialised heap in stock
        //; everything else must match exactly.
        MapPakFile pak = await RunAsync(name);
        ZipArchiveReader stock = await P3gStock.StockPakAsync(await P3gStock.StockBspAsync(name));

        List<ZipEntry> hdr = [.. pak.Entries.Where(e => e.Name.EndsWith(".hdr.vtf", StringComparison.Ordinal))];

        Assert.Equal(stock.Entries.Count(e => e.Name.EndsWith(".hdr.vtf", StringComparison.Ordinal)), hdr.Count);
        foreach (ZipEntry entry in hdr)
        {
            byte[] expected = stock.Find(entry.Name)!.Data;
            Assert.Equal(expected.Length, entry.Data.Length);
            Assert.Equal(WithoutSphereMap(expected), WithoutSphereMap(entry.Data));
        }
    }

    [P3gStockTheory]
    [MemberData(nameof(P3gStock.Entries), MemberType = typeof(P3gStock))]
    public async Task EveryPatchedTexDataStockKeptIsOneThisCreated(string name)
    {
        // Stock compacts TEXDATA (CompactTexinfos), so its list is the patched
        // names some emitted face still uses; each of those must be one of
        // ours, and ours may only add names for sides that emit no face.
        (VbspContext context, MapFile map, MaterialPatcher patcher) = await P3gStock.LoadAsync(name);
        await RunFixupsAsync(context, map, patcher);

        BspData bsp = await P3gStock.StockBspAsync(name);
        HashSet<string> ours = [.. Enumerable.Range(0, context.TexDatas.Count).Select(context.TexDatas.NameOf)
                                              .Where(n => n.StartsWith($"maps/{name}/", StringComparison.Ordinal))];

        foreach (string stockName in StockTexDataNames(bsp).Where(n => n.StartsWith($"maps/{name}/", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.Contains(stockName.ToLowerInvariant(), ours);
        }
    }

    private static bool Ours(string name) =>
        !name.Contains("_depth_", StringComparison.Ordinal) &&
        !name.EndsWith("_wvt_patch.vmt", StringComparison.Ordinal);

    internal static async Task<MapPakFile> RunAsync(string name)
    {
        (VbspContext context, MapFile map, MaterialPatcher patcher) = await P3gStock.LoadAsync(name);
        CubemapFixups fixups = await RunFixupsAsync(context, map, patcher);

        string? skyName = map.Entities.FirstOrDefault(e => e.ValueForKey("classname") == "worldspawn")?.ValueForKey("skyname");
        await DefaultCubemapBuilder.CreateAsync(
            skyName, name, fixups.DefaultCubemapNames, context.Materials, context.Content, patcher.Pak, context.Diagnostics);

        return patcher.Pak;
    }

    private static async Task<CubemapFixups> RunFixupsAsync(VbspContext context, MapFile map, MaterialPatcher patcher)
    {
        //: WorldVertexTransitionFixup (3a's) runs first,
        // and its patches are then candidates for cubemap patches too.
        foreach (WorldVertexTransitionPatch patch in await WorldVertexTransitionFixup.RunAsync(context, map))
        {
            patcher.WriteMaterialKeyValuesToPak(patch.Name, patch.Material);
        }

        CubemapFixups fixups = new(context, map, patcher);
        await fixups.FixupBrushSidesMaterialsAsync();
        await fixups.AttachDefaultCubemapToSpecularSidesAsync();
        fixups.AddUnreferencedCubemaps();
        return fixups;
    }

    private static IEnumerable<string> StockTexDataNames(BspData bsp)
    {
        ReadOnlySpan<DTexData> texData = BspStructView.As<DTexData>(bsp[BspLump.TexData]);
        int[] table = BspStructView.As<int>(bsp[BspLump.TexDataStringTable]).ToArray();
        byte[] strings = bsp[BspLump.TexDataStringData].Data.ToArray();

        List<string> names = [];
        foreach (DTexData entry in texData)
        {
            int start = table[entry.NameStringTableId];
            int end = Array.IndexOf(strings, (byte)0, start);
            names.Add(System.Text.Encoding.Latin1.GetString(strings, start, end - start));
        }

        return names;
    }

    // Zero the face-6 bytes of every mip of a 32x32 7-face RGBA16161616F file,
    // written smallest mip first, frame, then face(WriteImageData).
    internal static byte[] WithoutSphereMap(byte[] vtf)
    {
        byte[] copy = (byte[])vtf.Clone();
        int frames = BitConverter.ToUInt16(copy, 24);
        int offset = BitConverter.ToInt32(copy, 84);

        for (int mip = 5; mip >= 0; mip--)
        {
            int dim = 32 >> mip;
            int faceBytes = ImageFormatInfo.SizeInBytes(ImageFormat.Rgba16161616F, dim, dim);
            for (int frame = 0; frame < frames; frame++)
            {
                offset += 6 * faceBytes;
                Array.Clear(copy, offset, faceBytes);
                offset += faceBytes;
            }
        }

        return copy;
    }
}
