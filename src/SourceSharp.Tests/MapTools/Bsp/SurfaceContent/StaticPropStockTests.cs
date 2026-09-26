//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Props;
using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.SurfaceContent;

/// <summary>
/// <c>sprp</c> against stock, per catalogue entry, with the managed
/// collision seam (I3: stock's tree, 3a's entities).
/// </summary>
public class StaticPropStockTests
{
    [P3gStockTheory]
    [MemberData(nameof(P3gStock.Entries), MemberType = typeof(P3gStock))]
    public async Task TheModelDictionaryIsStocks(string name)
    {
        (StaticPropLump ours, StaticPropLump stock) = await RunAsync(name);

        Assert.Equal(stock.ModelNames, ours.ModelNames);
    }

    [P3gStockTheory]
    [MemberData(nameof(P3gStock.Entries), MemberType = typeof(P3gStock))]
    public async Task EveryPropRecordIsStocksApartFromItsLeafRun(string name)
    {
        (StaticPropLump ours, StaticPropLump stock) = await RunAsync(name);

        Assert.Equal(stock.Props.Count, ours.Props.Count);
        for (int i = 0; i < stock.Props.Count; i++)
        {
            StaticProp a = ours.Props[i];
            StaticProp b = stock.Props[i];
            a.FirstLeaf = b.FirstLeaf;
            a.LeafCount = b.LeafCount;
            if ((b.Flags & StaticPropFlags.UseLightingOrigin) == 0)
            {
                // Uninitialised in stock; see TheGameLumpIsStocksBytesApartFromUnsetLightingOrigins.
                a.LightingOrigin = b.LightingOrigin;
            }

            Assert.Equal(Bytes(b), Bytes(a));
        }
    }

    [P3gStockTheory]
    [MemberData(nameof(P3gStock.Entries), MemberType = typeof(P3gStock))]
    public async Task EveryPropsLeafListIsStocksWithTheManagedHull(string name)
    {
        // The leaf lists depend on vphysics' hull; the managed one is exact
        // convex geometry. Measured: equal on every entry of the 3g catalogue
        // (whose props sit well inside their leaves or straddle a block-grid
        // plane by a wide margin), so this is held exact rather than to a
        // threshold -- a divergence is a finding about the reference hull cooker.
        (StaticPropLump ours, StaticPropLump stock) = await RunAsync(name);

        Assert.Equal(stock.LeafEntries, ours.LeafEntries);
    }

    [P3gStockTheory]
    [MemberData(nameof(P3gStock.Entries), MemberType = typeof(P3gStock))]
    public async Task TheGameLumpIsStocksBytesApartFromUnsetLightingOrigins(string name)
    {
        // A prop whose lightingorigin names no info_lighting leaves
        // StaticPropLump_t::m_LightingOrigin as uninitialised stack
        // (ComputeLightingOrigin, returns without
        // writing it): measured as nonzero garbage on sdk_ctf_2fort. Those 12
        // bytes are zeroed on both sides only where the flag is unset.
        (StaticPropLump ours, _) = await RunAsync(name);
        BspData stock = await P3gStock.StockBspAsync(name);

        GameLumpEntry theirs = stock.GameLumps.Single(g => g.Id == GameLumpId.MakeId(GameLumpId.StaticProps));
        Assert.Equal(WithoutUnsetLightingOrigins(StaticPropLump.Read(theirs)), WithoutUnsetLightingOrigins(ours));
    }

    internal static byte[] WithoutUnsetLightingOrigins(StaticPropLump lump)
    {
        foreach (StaticProp prop in lump.Props)
        {
            if ((prop.Flags & StaticPropFlags.UseLightingOrigin) == 0)
            {
                prop.LightingOrigin = default;
            }
        }

        return lump.Write().Data.ToArray();
    }

    private static async Task<(StaticPropLump Ours, StaticPropLump Stock)> RunAsync(string name)
    {
        (VbspContext context, MapFile map, _) = await P3gStock.LoadAsync(name);
        BspData bsp = await P3gStock.StockBspAsync(name);

        StaticPropLump ours = await new StaticPropEmitter(context, new ManagedStaticPropCollision())
            .EmitAsync(map.Entities, BspTreeView.FromBsp(bsp));

        StaticPropLump stock = StaticPropLump.Read(
            bsp.GameLumps.Single(g => g.Id == GameLumpId.MakeId(GameLumpId.StaticProps)));
        return (ours, stock);
    }

    private static byte[] Bytes(StaticProp prop)
    {
        StaticPropLump one = new();
        one.Props.Add(prop);
        return one.Write().Data.ToArray();
    }
}
