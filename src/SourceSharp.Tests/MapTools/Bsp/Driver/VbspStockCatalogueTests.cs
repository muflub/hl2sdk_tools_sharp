//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Driver;

/// <summary>
/// The whole compile against stock vbsp's own output, lump for lump, on the
/// stock catalogue (plan §6 gates, I2's no-float-freedom lumps).
/// </summary>
/// <remarks>
/// <para>
/// Needs <c>VVIS_STOCK_DIR</c> pointing at a directory of <c>.vmf</c> plus the
/// PURE stock vbsp output (<c>.bsp</c>, <c>.prt</c>, <c>.vbspv.log</c>) and
/// the game content <see cref="StockLoad"/> mounts; skips otherwise. Every
/// comparison runs under <see cref="ComplianceOptions.Stock"/>.
/// </para>
/// <para>
/// Not compared, because other lanes own them: PHYSCOLLIDE/PHYSDISP (3h).
/// PAKFILE and the game lumps (3g). TEXDATA is compared by name only here: a
/// water material with no <c>$basetexture</c> reads 128x128 / reflectivity 0
/// where stock reads 256x256 / 0.2 (the Materials gap 3a recorded), and that
/// is not this stage's arithmetic.
/// </para>
/// </remarks>
public sealed class VbspStockCatalogueTests
{
    public static TheoryData<string> Entries => StockLoad.Entries;

    private static async Task<(VbspResult Result, BspData Stock)> CompileAsync(string name)
    {
        (VbspContext context, MapFile map, _) = await StockLoad.LoadAsync(name, ComplianceOptions.Stock);
        VbspResult result = await Vbsp.CompileAsync(map, context);

        await using FileStream stream = File.OpenRead(StockLoad.BspPath(name));
        BspData stock = await BspFile.LoadAsync(stream, CancellationToken.None);
        return (result, stock);
    }

    private static void Same(VbspResult result, BspData stock, BspLump lump) =>
        Assert.Equal(stock[lump].Data.ToArray(), result.Bsp![lump].Data.ToArray());

    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task TheEntityLumpIsStocksByteForByte(string name)
    {
        (VbspResult result, BspData stock) = await CompileAsync(name);
        Same(result, stock, BspLump.Entities);
    }

    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task TheTexInfoLumpIsStocksByteForByte(string name)
    {
        (VbspResult result, BspData stock) = await CompileAsync(name);
        Same(result, stock, BspLump.TexInfo);
    }

    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task TheTexDataNamesAreStocksByteForByte(string name)
    {
        (VbspResult result, BspData stock) = await CompileAsync(name);
        Same(result, stock, BspLump.TexDataStringData);
        Same(result, stock, BspLump.TexDataStringTable);
    }

    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task TheBrushLumpsAreStocksByteForByte(string name)
    {
        (VbspResult result, BspData stock) = await CompileAsync(name);
        Same(result, stock, BspLump.Brushes);
        Same(result, stock, BspLump.BrushSides);
    }

    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task TheModelsAreStocksByteForByte(string name)
    {
        (VbspResult result, BspData stock) = await CompileAsync(name);
        Same(result, stock, BspLump.Models);
    }

    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task TheTreeLumpsAreStocksByteForByte(string name)
    {
        (VbspResult result, BspData stock) = await CompileAsync(name);
        foreach (BspLump lump in (BspLump[])[
            BspLump.Planes, BspLump.Nodes, BspLump.Leafs, BspLump.LeafFaces, BspLump.LeafBrushes,
            BspLump.Areas, BspLump.AreaPortals, BspLump.ClipPortalVerts, BspLump.LeafWaterData])
        {
            Same(result, stock, lump);
        }
    }

    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task TheFaceLumpsAreStocksByteForByte(string name)
    {
        (VbspResult result, BspData stock) = await CompileAsync(name);
        foreach (BspLump lump in (BspLump[])[
            BspLump.Faces, BspLump.OriginalFaces, BspLump.FaceIds, BspLump.Vertexes, BspLump.Edges,
            BspLump.SurfEdges, BspLump.VertNormals, BspLump.VertNormalIndices, BspLump.Primitives,
            BspLump.PrimIndices, BspLump.PrimVerts, BspLump.Occlusion])
        {
            Same(result, stock, lump);
        }
    }

    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task ThePortalFileIsStocksByteForByte(string name)
    {
        string prt = Path.ChangeExtension(StockLoad.BspPath(name), ".prt");
        (VbspResult result, _) = await CompileAsync(name);

        if (!File.Exists(prt))
        {
            // stock writes none for a leaked map, and neither do we
            Assert.Null(result.Portals);
            return;
        }

        Assert.Equal(await File.ReadAllBytesAsync(prt), result.Portals!.ToBytes(PortalLineEnding.CrLf));
    }
}
