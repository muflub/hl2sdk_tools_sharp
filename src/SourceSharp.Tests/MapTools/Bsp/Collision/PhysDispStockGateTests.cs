using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Options;
using SourceSharp.Tests.MapTools.Disp;
using SourceSharp.Tests.MapTools.Phys;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Collision;

/// <summary>
/// LUMP_PHYSDISP and the displacement maps' LUMP_PHYSCOLLIDE against stock,
/// over lane p3f-t's displacement catalogue (<c>DISP_STOCK_DIR</c>).
/// </summary>
/// <remarks>
/// Stage isolation: the brush lumps are stock's own, and each displacement's
/// <c>CCoreDispInfo</c> is rebuilt by the managed displacement stage from the
/// VMF and stock's own base faces (<see cref="DispStockMap"/>, under
/// <see cref="ComplianceOptions.Stock"/>, which p3f-t measured vertex-exact).
/// </remarks>
[Collection(VPhysicsCollection.Name)]
[Trait("Tier", PinnedVPhysics.Tier)]
[VPhysicsJournal]
public class PhysDispStockGateTests
{
    private static readonly ConcurrentDictionary<string, Task<(PhysCollisionResult Ours, BspData Stock)>> Cache = new();

    private readonly VPhysicsCookerFixture _fixture;

    public PhysDispStockGateTests(VPhysicsCookerFixture fixture) => _fixture = fixture;

    /// <summary>The maps.</summary>
    public static TheoryData<string> Maps() => DispStockCatalogue.Entries;

    private Task<(PhysCollisionResult Ours, BspData Stock)> BothAsync(string name) =>
        Cache.GetOrAdd(name, async n =>
        {
            BspData bsp;
            await using (FileStream stream = File.OpenRead(DispStockCatalogue.BspPath(n)))
            {
                bsp = await BspFile.LoadAsync(stream);
            }

            (PhysCollisionInput input, IReadOnlyList<int> prop2) = await PhysStockCatalogue.InputAndProp2Async(DispStockCatalogue.Directory!, n, bsp);

            DispStockMap map = DispStockMap.Load(n);
            IReadOnlyList<DisplacementResult> results = DisplacementLumpBuilder.Build(
                map.Displacements, map.Faces, new VbspOptions { Compliance = ComplianceOptions.Stock }, new DisplacementLumps());

            DFace[] faces = [.. BspStructView.As<DFace>(bsp[BspLump.Faces])];
            List<CollisionDisplacement> disps = [];
            for (int i = 0; i < results.Count; i++)
            {
                int texInfo = faces[map.Faces[i].FaceIndex].TexInfo;
                int texData = input.TexInfos[texInfo].TexData;
                disps.Add(new CollisionDisplacement(results[i].Core, map.Displacements[i].Contents, texInfo, prop2[texData]));
            }

            PhysCollisionResult ours = await PhysCollisionEmitter.EmitAsync(input with { Displacements = disps }, _fixture.Cooker);
            return (ours, bsp);
        });

    [DispPhysTheory]
    [MemberData(nameof(Maps))]
    public async Task PhysDispHasStocksDisplacementCount(string map)
    {
        var (ours, stock) = await BothAsync(map);

        Assert.Equal(
            PhysDispLump.ReadSizes(stock[BspLump.PhysDisp].Data.Span).Count,
            PhysDispLump.ReadSizes(ours.PhysDisp).Count);
    }

    [DispPhysTheory]
    [MemberData(nameof(Maps))]
    public async Task EachDisplacementsHullHasStocksSize(string map)
    {
        var (ours, stock) = await BothAsync(map);

        Assert.Equal(PhysDispLump.ReadSizes(stock[BspLump.PhysDisp].Data.Span), PhysDispLump.ReadSizes(ours.PhysDisp));
    }

    [DispPhysTheory]
    [MemberData(nameof(Maps))]
    public async Task ThePhysCollideKeydataIsStocks(string map)
    {
        var (ours, stock) = await BothAsync(map);

        Assert.Equal(
            PhysCollideLump.Read(stock[BspLump.PhysCollide].Data.Span).Select(m => m.KeyText),
            ours.Models.Select(m => m.KeyText));
    }

    [DispPhysTheory]
    [MemberData(nameof(Maps))]
    public async Task PhysDispBytesAreMeasured(string map)
    {
        var (ours, stock) = await BothAsync(map);
        byte[] b = stock[BspLump.PhysDisp].Data.ToArray();

        // The -novirtualmesh road (forced by a power-4 displacement) writes no
        // lump at all, in stock (g_pPhysDisp stays NULL) and here.
        Assert.Equal(b.Length == 0, ours.PhysDisp is null);
        byte[] a = ours.PhysDisp ?? [];
        int differing = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (a[i] != b[i])
            {
                differing++;
            }
        }

        if (Environment.GetEnvironmentVariable("PHYS_BYTE_REPORT") is { Length: > 0 } report)
        {
            await File.AppendAllTextAsync(report, string.Create(CultureInfo.InvariantCulture, $"{map}\tPHYSDISP\t-\t{b.Length}\t{a.Length}\t{differing}\n"));
        }

        Assert.Equal(b.Length, a.Length);
    }

    [DispPhysTheory]
    [MemberData(nameof(Maps))]
    public async Task EachSolidHasStocksShape(string map)
    {
        // The world's brushes and, on the polysoup road, one static mesh per
        // grid cell. Sizes differ by the Windows build's vertex pooling (the
        // polysoup of p3f_lm8_clamp: stock 57920, here 77872), so the solids
        // are compared as the pinned library reads them back.
        var (ours, stock) = await BothAsync(map);

        Assert.Equal(
            await PhysStockGateTests.ShapesAsync(_fixture.Cooker, PhysCollideLump.Read(stock[BspLump.PhysCollide].Data.Span)),
            await PhysStockGateTests.ShapesAsync(_fixture.Cooker, ours.Models));
    }
}

/// <summary>A native-tier theory over the displacement catalogue: skips when <c>DISP_STOCK_DIR</c> is unset.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DispPhysTheoryAttribute : VPhysicsNativeTheoryAttribute
{
    /// <summary>Skips outside the child, or without the displacement catalogue.</summary>
    public DispPhysTheoryAttribute()
        : base(DispStockCatalogue.SkipReason())
    {
    }
}
