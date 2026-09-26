//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Detail;
using SourceSharp.MapTools.Bsp.MaterialPatch;
using SourceSharp.MapTools.Bsp.Props;
using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.SurfaceContent;

/// <summary>
/// <c>dprp</c> against stock, per catalogue entry (I3: stock's written faces,
/// face ids and tree; 3a's entities).
/// </summary>
/// <remarks>
/// Stock leaves some bytes of each record UNINITIALISED — the six padding
/// bytes always, and for a MODEL record the sway, shape and scale fields,
/// which only the sprite path sets. They
/// hold heap garbage in stock's output (measured: ASCII fragments in the
/// padding of <c>l1_detail_props</c>). This writes zeros and the comparison
/// masks exactly those bytes.
/// </remarks>
public class DetailPropStockTests
{
    [P3gStockTheory]
    [MemberData(nameof(P3gStock.Entries), MemberType = typeof(P3gStock))]
    public async Task TheModelAndSpriteDictionariesAreStocks(string name)
    {
        (DetailPropLump ours, DetailPropLump stock) = await RunAsync(name);

        Assert.Equal(stock.ModelNames, ours.ModelNames);
        Assert.Equal(Bytes(stock.Sprites), Bytes(ours.Sprites));
    }

    /// <summary>
    /// The frozen tolerance on a conforming detail prop's angles, in degrees:
    /// 2^-15, one ulp of a value between 256 and 512.
    /// </summary>
    /// <remarks>
    /// MEASURED on the three detail entries (976 + 152 + 896 props):
    /// 591 + 36 + 548 records' angles differ from stock, by at most exactly
    /// 3.0517578125e-5 degrees, under either compliance. Everything else in
    /// every record -- origin, model or sprite index, leaf, type, orientation,
    /// scale, sway, shape -- matches bit for bit, and so does the ORDER. The
    /// angles come out of <c>SetupMatrixAxisRot</c>'s <c>sin</c>/<c>cos</c>
    /// and <c>MatrixToAngles</c>' <c>atan2f</c>; the MSVC CRT's float
    /// versions are not the correctly rounded ones.NET calls, and no reading
    /// of the source's float/double conversions closes the gap (see
    /// <c>DetailPropEmitter.Atan2F</c>). Frozen here; tightening it needs the
    /// CRT's algorithms, not this port's arithmetic.
    /// </remarks>
    internal const double AngleToleranceDegrees = 3.0517578125e-5;

    [P3gStockTheory]
    [MemberData(nameof(P3gStock.Entries), MemberType = typeof(P3gStock))]
    public async Task EveryDetailPropIsStocksInStocksOrderApartFromTheAngles(string name)
    {
        (DetailPropLump ours, DetailPropLump stock) = await RunAsync(name);

        Assert.Equal(stock.Props.Count, ours.Props.Count);
        List<int> differing = [];
        for (int i = 0; i < stock.Props.Count; i++)
        {
            if (!WithoutAngles(stock.Props[i]).SequenceEqual(WithoutAngles(ours.Props[i])))
            {
                differing.Add(i);
            }
        }

        Assert.True(differing.Count == 0, $"{name}: {differing.Count} of {stock.Props.Count} records differ, first at {string.Join(", ", differing.Take(8))}");
    }

    [P3gStockTheory]
    [MemberData(nameof(P3gStock.Entries), MemberType = typeof(P3gStock))]
    public async Task EveryDetailPropsAnglesAreWithinTheFrozenTolerance(string name)
    {
        (DetailPropLump ours, DetailPropLump stock) = await RunAsync(name);

        double worst = 0;
        for (int i = 0; i < stock.Props.Count; i++)
        {
            worst = Math.Max(worst, Math.Abs(stock.Props[i].Angles.X - ours.Props[i].Angles.X));
            worst = Math.Max(worst, Math.Abs(stock.Props[i].Angles.Y - ours.Props[i].Angles.Y));
            worst = Math.Max(worst, Math.Abs(stock.Props[i].Angles.Z - ours.Props[i].Angles.Z));
        }

        Assert.InRange(worst, 0, AngleToleranceDegrees);
    }

    internal static byte[] WithoutAngles(DetailObjectLump record)
    {
        byte[] bytes = Masked(record);
        Array.Clear(bytes, 12, 12);
        return bytes;
    }

    internal static async Task<(DetailPropLump Ours, DetailPropLump Stock)> RunAsync(string name)
    {
        (VbspContext context, MapFile map, MaterialPatcher patcher) = await P3gStock.LoadAsync(name);
        BspData bsp = await P3gStock.StockBspAsync(name);

        DetailDictionary dictionary = await DetailPropEmitter.LoadDictionaryAsync(map.Entities, context.Content);
        DetailPropLump ours = await new DetailPropEmitter(context, patcher, dictionary)
            .EmitAsync(map.Entities, bsp, BspTreeView.FromBsp(bsp), new MapDisplacementSurfaces(context, map));

        DetailPropLump stock = DetailPropLump.Read(bsp.GameLumps.Single(g => g.Id == GameLumpId.MakeId(GameLumpId.DetailProps)));
        return (ours, stock);
    }

    internal static byte[] Masked(DetailObjectLump record)
    {
        byte[] bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<DetailObjectLump>(in record)).ToArray();
        Array.Clear(bytes, 41, 3);
        Array.Clear(bytes, 45, 3);
        if (record.Type == 0)
        {
            Array.Clear(bytes, 37, 3);
            Array.Clear(bytes, 48, 4);
        }

        return bytes;
    }

    private static byte[] Bytes(List<DetailSpriteDictLump> sprites) =>
        MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(sprites)).ToArray();
}
