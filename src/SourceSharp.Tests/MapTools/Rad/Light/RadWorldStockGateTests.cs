using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapTools.Rad.Light;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Light;

/// <summary>
/// The 4c gates: what <c>RadWorld_Start</c> produces, against stock vrad, on
/// every catalogue map and p4c's texlight fixture.
/// </summary>
/// <remarks>
/// <para>
/// Every number here is one stock PRINTS (<c>vrad.cpp:709, 730, 933, 1045</c>,
/// <c>lightmap.cpp:1621</c>) or WRITES (the worldlight lumps). Each was checked
/// to move with the thing it is meant to gate: the patch count with
/// <c>SubdividePatch</c>'s chop test, the light count with the entity and
/// texlight parse, the worldlight bytes with every field of the export.
/// </para>
/// <para>
/// All run under <c>ComplianceOptions.Stock</c>, because two quantities --
/// a light's target direction and the phong normals subdivision uses -- pass
/// through a normalise, and stock's is an estimate.
/// </para>
/// </remarks>
public sealed class RadWorldStockGateTests
{
    public static TheoryData<string> Maps()
    {
        TheoryData<string> data = [];
        foreach (string name in StockVradReference.Load().Keys.Order(StringComparer.Ordinal))
        {
            data.Add(name);
        }

        return data;
    }

    [StockVradTheory]
    [MemberData(nameof(Maps))]
    public async Task FaceCountMatchesStock(string name)
    {
        StockVradMap stock = StockVradReference.Load()[name];
        RadWorld world = await StockRadWorld.StartAsync(name, hdr: false);
        Assert.Equal(stock.Faces, world.Statistics.Faces);
    }

    [StockVradTheory]
    [MemberData(nameof(Maps))]
    public async Task PatchesBeforeSubdivisionMatchStock(string name)
    {
        StockVradMap stock = StockVradReference.Load()[name];
        RadWorld world = await StockRadWorld.StartAsync(name, hdr: false);

        // A map with no vis prints no patch lines (vrad.cpp:930 returns
        // first); the count is still the patch count before any split.
        int expected = stock.PatchesBefore >= 0 ? stock.PatchesBefore : world.Patches.Count;
        Assert.Equal(expected, world.Statistics.Subdivision.PatchesBefore);
    }

    [StockVradTheory]
    [MemberData(nameof(Maps))]
    public async Task PatchesAfterSubdivisionMatchStock(string name)
    {
        StockVradMap stock = StockVradReference.Load()[name];
        RadWorld world = await StockRadWorld.StartAsync(name, hdr: false);
        int expected = stock.PatchesAfter >= 0 ? stock.PatchesAfter : stock.Faces - stock.DegenerateFaces;
        Assert.Equal(expected, world.Statistics.Subdivision.PatchesAfter);
    }

    [StockVradTheory]
    [MemberData(nameof(Maps))]
    public async Task DegenerateFaceCountMatchesStock(string name)
    {
        StockVradMap stock = StockVradReference.Load()[name];
        RadWorld world = await StockRadWorld.StartAsync(name, hdr: false);
        Assert.Equal(stock.DegenerateFaces, world.Patches.DegenerateFaces);
    }

    [StockVradTheory]
    [MemberData(nameof(Maps))]
    public async Task TotalAreaPrintsAsStockPrintsIt(string name)
    {
        StockVradMap stock = StockVradReference.Load()[name];
        RadWorld world = await StockRadWorld.StartAsync(name, hdr: false);

        // vrad.cpp:730 `%.2f` of the float total.
        Assert.Equal(
            stock.AreaSquareInches,
            ((double)world.Patches.TotalArea).ToString("F2", CultureInfo.InvariantCulture));
    }

    [StockVradTheory]
    [MemberData(nameof(Maps))]
    public async Task DirectLightCountMatchesStock(string name)
    {
        StockVradMap stock = StockVradReference.Load()[name];
        RadWorld world = await StockRadWorld.StartAsync(name, hdr: false);
        Assert.Equal(stock.DirectLights, world.Statistics.DirectLights);
    }

    [StockVradTheory]
    [MemberData(nameof(Maps))]
    public async Task WorldLightsLumpIsByteIdenticalToStock(string name)
    {
        string stockBsp = StockVradReference.StockOutputFor(name)
            ?? throw new InvalidOperationException($"no stock output for {name}");
        byte[] expected = StockRadWorld.Lump(await StockRadWorld.LoadBspAsync(stockBsp), BspLump.WorldLights);

        RadWorld world = await StockRadWorld.StartAsync(name, hdr: false);
        Assert.Equal(expected, world.WorldLightBytes());
    }

    [StockVradPrivateTheory]
    [MemberData(nameof(Maps))]
    public async Task HdrWorldLightsLumpIsByteIdenticalToStock(string name)
    {
        string both = StockVradReference.BothOutputFor(name)
            ?? throw new InvalidOperationException($"no stock -both output for {name}");
        byte[] expected = StockRadWorld.Lump(await StockRadWorld.LoadBspAsync(both), BspLump.WorldLightsHdr);

        RadWorld world = await StockRadWorld.StartAsync(name, hdr: true);
        Assert.Equal(expected, world.WorldLightBytes());
    }

    [StockVradTheory]
    [MemberData(nameof(Maps))]
    public async Task WorldLightsMatchTheCommittedRecord(string name)
    {
        // The same lump against the COMMITTED fixture, so a drift of the
        // corpus itself (it has been rebuilt twice) shows up as a difference
        // between these two facts rather than silently moving both sides.
        StockVradMap stock = StockVradReference.Load()[name];
        RadWorld world = await StockRadWorld.StartAsync(name, hdr: false);

        Assert.Equal(stock.WorldLights.Count, world.WorldLights.Length);
        List<string> differences = [];
        for (int i = 0; i < stock.WorldLights.Count; i++)
        {
            StockWorldLight s = stock.WorldLights[i];
            var m = world.WorldLights[i];
            void Check<T>(string field, T expected, T actual)
            {
                if (!EqualityComparer<T>.Default.Equals(expected, actual))
                {
                    differences.Add($"light {i} {field}: stock {expected} managed {actual}");
                }
            }

            Check("type", s.Type, m.Type);
            Check("style", s.Style, m.Style);
            Check("cluster", s.Cluster, m.Cluster);
            Check("origin", s.Origin, m.Origin);
            Check("intensity", s.Intensity, m.Intensity);
            Check("normal", s.Normal, m.Normal);
            Check("stopdot", s.StopDot, m.StopDot);
            Check("stopdot2", s.StopDot2, m.StopDot2);
            Check("exponent", s.Exponent, m.Exponent);
            Check("radius", s.Radius, m.Radius);
            Check("constant", s.ConstantAttn, m.ConstantAttn);
            Check("linear", s.LinearAttn, m.LinearAttn);
            Check("quadratic", s.QuadraticAttn, m.QuadraticAttn);
        }

        Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences));
    }
}
