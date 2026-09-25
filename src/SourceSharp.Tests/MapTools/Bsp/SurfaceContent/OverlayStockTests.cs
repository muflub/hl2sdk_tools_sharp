using System.Runtime.InteropServices;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Overlays;
using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.SurfaceContent;

/// <summary>
/// Overlays against stock, per catalogue entry. I3: the map is 3a's load of
/// the stock VMF and the face order is STOCK's — each face's side comes from
/// LUMP_FACEIDS, which <c>EmitFace</c> writes from <c>f-&gt;originalface-&gt;id</c>
/// in exactly the order it calls
/// <c>Overlay_AddFaceToLists</c>.
/// </summary>
public class OverlayStockTests
{
    [P3gStockTheory]
    [MemberData(nameof(P3gStock.Entries), MemberType = typeof(P3gStock))]
    public async Task TheOverlaysLumpIsStocksApartFromTheTexInfoIndex(string name)
    {
        (OverlayLumps lumps, BspData stock, _) = await RunAsync(name);

        DOverlay[] expected = BspStructView.As<DOverlay>(stock[BspLump.Overlays]).ToArray();
        Assert.Equal(expected.Length, lumps.Overlays.Length);

        for (int i = 0; i < expected.Length; i++)
        {
            DOverlay ours = lumps.Overlays[i];
            ours.TexInfo = expected[i].TexInfo;
            Assert.Equal(Bytes(expected[i]), Bytes(ours));
        }
    }

    [P3gStockTheory]
    [MemberData(nameof(P3gStock.Entries), MemberType = typeof(P3gStock))]
    public async Task TheWaterOverlaysLumpIsStocksApartFromTheTexInfoIndex(string name)
    {
        (OverlayLumps lumps, BspData stock, _) = await RunAsync(name);

        DWaterOverlay[] expected = BspStructView.As<DWaterOverlay>(stock[BspLump.WaterOverlays]).ToArray();
        Assert.Equal(expected.Length, lumps.WaterOverlays.Length);

        for (int i = 0; i < expected.Length; i++)
        {
            DWaterOverlay ours = lumps.WaterOverlays[i];
            ours.TexInfo = expected[i].TexInfo;
            Assert.Equal(Bytes(expected[i]), Bytes(ours));
        }
    }

    [P3gStockTheory]
    [MemberData(nameof(P3gStock.Entries), MemberType = typeof(P3gStock))]
    public async Task TheOverlayFadesLumpIsStocks(string name)
    {
        (OverlayLumps lumps, BspData stock, _) = await RunAsync(name);

        Assert.Equal(stock[BspLump.OverlayFades].Data.ToArray(), lumps.FadeBytes());
    }

    [P3gStockTheory]
    [MemberData(nameof(P3gStock.Entries), MemberType = typeof(P3gStock))]
    public async Task EveryOverlayTexInfoIsStocksByValue(string name)
    {
        // Stock's index is after CompactTexinfos, which
        // is 3e's; the entry it points at must be the same texinfo.
        (OverlayLumps lumps, BspData stock, VbspContext context) = await RunAsync(name);

        TexInfo[] stockTexInfo = BspStructView.As<TexInfo>(stock[BspLump.TexInfo]).ToArray();
        List<string> stockNames = StockTexDataNames(stock);

        IEnumerable<(short Ours, short Stock)> pairs =
            lumps.Overlays.Zip(BspStructView.As<DOverlay>(stock[BspLump.Overlays]).ToArray(), (a, b) => (a.TexInfo, b.TexInfo))
            .Concat(lumps.WaterOverlays.Zip(BspStructView.As<DWaterOverlay>(stock[BspLump.WaterOverlays]).ToArray(), (a, b) => (a.TexInfo, b.TexInfo)));

        foreach ((short ours, short theirs) in pairs)
        {
            TexInfo a = context.TexInfos[ours];
            TexInfo b = stockTexInfo[theirs];
            Assert.Equal(stockNames[b.TexData], context.TexDatas.NameOf(a.TexData));
            a.TexData = b.TexData;
            Assert.Equal(Bytes(b), Bytes(a));
        }
    }

    internal static async Task<(OverlayLumps Lumps, BspData Stock, VbspContext Context)> RunAsync(string name)
    {
        (VbspContext context, MapFile map, _) = await P3gStock.LoadAsync(name);
        VmfDocument document = await VmfDocument.ParseAsync(
            await File.ReadAllTextAsync(Path.Combine(P3gStock.Directory!, name + ".vmf")));

        OverlaySet set = OverlaySet.Load(map, document, context.MaterialReplacements, context.Options.Compliance);

        BspData stock = await P3gStock.StockBspAsync(name);
        ReadOnlySpan<DFaceId> faceIds = BspStructView.As<DFaceId>(stock[BspLump.FaceIds]);
        for (int face = 0; face < faceIds.Length; face++)
        {
            int index = map.SideIdToIndex(faceIds[face].HammerFaceId);
            set.AddFace(face, index < 0 ? null : map.BrushSides[index]);
        }

        return (await set.EmitAsync(context), stock, context);
    }

    private static List<string> StockTexDataNames(BspData bsp)
    {
        int[] table = BspStructView.As<int>(bsp[BspLump.TexDataStringTable]).ToArray();
        byte[] strings = bsp[BspLump.TexDataStringData].Data.ToArray();

        List<string> names = [];
        foreach (DTexData entry in BspStructView.As<DTexData>(bsp[BspLump.TexData]))
        {
            int start = table[entry.NameStringTableId];
            names.Add(System.Text.Encoding.Latin1.GetString(strings, start, Array.IndexOf(strings, (byte)0, start) - start));
        }

        return names;
    }

    private static byte[] Bytes<T>(T value)
        where T : unmanaged =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)).ToArray();
}
