using System.Collections.Concurrent;
using System.Globalization;

using SourceSharp.MapFormats.Bsp;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys;
using SourceSharp.Tests.MapTools.Bsp.Collision;
using SourceSharp.Tests.MapTools.Phys;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Driver;

/// <summary>
/// Plan §7's gate for the WHOLE managed vbsp: the PHYSCOLLIDE and PHYSDISP a
/// managed compile writes, against pure stock vbsp's, per catalogue map.
/// </summary>
/// <remarks>
/// <para>
/// p3h gated the emitter over stock's OWN finished lumps (stage isolation).
/// Here nothing is borrowed from stock: the planes, brushes, tree, leaves,
/// water volumes (from the real portal flood), side visibility (from
/// <c>MarkVisibleSides</c>) and surface properties are the managed
/// compile's, under <see cref="ComplianceOptions.Stock"/>. The criteria are
/// p3h's: the same models, the same solid counts, the same keydata apart
/// from the three cooker-derived numbers (1e-6 relative), the same shape
/// read back through the query model, stock's byte size for single-convex
/// solids and only whole 16-byte IVP points more for multi-convex ones (the
/// Windows build pools shared vertices), the same PHYSDISP sizes, and every
/// Correct-mode record loads through <c>VCollideLoad</c>.
/// </para>
/// <para>
/// Needs <c>VVIS_STOCK_DIR</c> (a <see cref="StockLoad"/> catalogue: the
/// xcomp 30-map corpus, or the displacement corpus) and runs only in the
/// native-tier child.
/// </para>
/// </remarks>
[Collection(VPhysicsCollection.Name)]
[Trait("Tier", PinnedVPhysics.Tier)]
[VPhysicsJournal]
public class VbspPhysStockGateTests(VPhysicsCookerFixture fixture) : VbspPhysStockGateBase(fixture)
{
    /// <inheritdoc/>
    protected override Task<ICollisionCooker> CompileCookerAsync(ComplianceOptions compliance) =>
        Task.FromResult<ICollisionCooker>(Fixture.Cooker);
}

/// <summary>
/// The same gate with the compile's collision cooked by the MANAGED cooker (phase 8a): only the
/// compile changes; every blob is still read back through the pinned native library (its query
/// model, its <c>VCollideLoad</c>), which is the engine's reader.
/// </summary>
[Collection(VPhysicsCollection.Name)]
[Trait("Tier", PinnedVPhysics.Tier)]
[VPhysicsJournal]
public class VbspPhysStockGateManagedTests(VPhysicsCookerFixture fixture) : VbspPhysStockGateBase(fixture)
{
    /// <inheritdoc/>
    protected override Task<ICollisionCooker> CompileCookerAsync(ComplianceOptions compliance) =>
        Task.FromResult<ICollisionCooker>(SourceSharp.MapTools.Phys.Managed.ManagedCollisionCooker.Create(compliance));

    /// <inheritdoc/>
    protected override bool OwnsCompileCooker => true;
}

/// <summary>The gate's facts, over whichever cooker a derived class compiles with.</summary>
public abstract class VbspPhysStockGateBase
{
    private static readonly ConcurrentDictionary<(string, bool, Type), Task<(BspData Ours, BspData Stock)>> Cache = new();

    private readonly VPhysicsCookerFixture _fixture;

    protected VbspPhysStockGateBase(VPhysicsCookerFixture fixture) => _fixture = fixture;

    /// <summary>The pinned native library: the reader every blob goes back through.</summary>
    protected VPhysicsCookerFixture Fixture => _fixture;

    public static TheoryData<string> Entries => StockLoad.Entries;

    /// <summary>The cooker the compile under test uses.</summary>
    /// <param name="compliance">The compile's compliance.</param>
    /// <returns>The cooker.</returns>
    protected abstract Task<ICollisionCooker> CompileCookerAsync(ComplianceOptions compliance);

    /// <summary>Whether <see cref="CompileCookerAsync"/> hands over a cooker the gate must dispose.</summary>
    protected virtual bool OwnsCompileCooker => false;

    private Task<(BspData Ours, BspData Stock)> BothAsync(string name, bool correct = false) =>
        Cache.GetOrAdd((name, correct, GetType()), async key =>
        {
            ComplianceOptions compliance = key.Item2 ? ComplianceOptions.Correct : ComplianceOptions.Stock;
            (VbspContext context, MapFile map, _) = await StockLoad.LoadAsync(key.Item1, compliance);
            ICollisionCooker cooker = await CompileCookerAsync(compliance);
            context.CollisionCooker = cooker;
            VbspResult result = await Vbsp.CompileAsync(map, context);
            if (OwnsCompileCooker)
            {
                await cooker.DisposeAsync();
            }

            await using FileStream stream = File.OpenRead(StockLoad.BspPath(key.Item1));
            return (result.Bsp!, await BspFile.LoadAsync(stream, CancellationToken.None));
        });

    private async Task<(IReadOnlyList<PhysCollideModel> Ours, IReadOnlyList<PhysCollideModel> Stock)> ModelsAsync(string name)
    {
        (BspData ours, BspData stock) = await BothAsync(name);
        return (PhysCollideLump.Read(ours[BspLump.PhysCollide].Data.Span), PhysCollideLump.Read(stock[BspLump.PhysCollide].Data.Span));
    }

    [VbspPhysTheory]
    [MemberData(nameof(Entries), MemberType = typeof(VbspPhysStockGateBase))]
    public async Task TheSameModelsHaveCollision(string name)
    {
        var (ours, stock) = await ModelsAsync(name);

        Assert.Equal(stock.Select(m => m.ModelIndex), ours.Select(m => m.ModelIndex));
    }

    [VbspPhysTheory]
    [MemberData(nameof(Entries), MemberType = typeof(VbspPhysStockGateBase))]
    public async Task EachModelHasStocksSolidCount(string name)
    {
        var (ours, stock) = await ModelsAsync(name);

        Assert.Equal(stock.Select(m => m.Solids.Count), ours.Select(m => m.Solids.Count));
    }

    [VbspPhysTheory]
    [MemberData(nameof(Entries), MemberType = typeof(VbspPhysStockGateBase))]
    public async Task EachModelHasStocksKeydataText(string name)
    {
        // Three numbers are the cooker's (a solid's volume, the mass from it,
        // a surfaceless fluid's CollideGetExtent top): equal within 1e-6
        // relative between the Windows and Linux builds. Every other token exact.
        var (ours, stock) = await ModelsAsync(name);
        List<string> expected = Tokens(string.Join("\n--\n", stock.Select(m => m.KeyText)));
        List<string> actual = Tokens(string.Join("\n--\n", ours.Select(m => m.KeyText)));

        Assert.Equal(expected.Count, actual.Count);
        Assert.All(expected.Zip(actual), pair =>
        {
            if (pair.First.Contains('.', StringComparison.Ordinal)
                && double.TryParse(pair.First, NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
                && double.TryParse(pair.Second, NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
            {
                Assert.True(Math.Abs(a - b) <= 1e-6 * Math.Max(1.0, Math.Abs(a)), $"{pair.First} vs {pair.Second}");
            }
            else
            {
                Assert.Equal(pair.First, pair.Second);
            }
        });
    }

    [VbspPhysTheory]
    [MemberData(nameof(Entries), MemberType = typeof(VbspPhysStockGateBase))]
    public async Task EachSolidHasStocksShape(string name)
    {
        // Convex count, per-convex triangles and game data (the brush number),
        // every triangle's material index, volume and bounds, as the pinned
        // library reads both blobs back.
        var (ours, stock) = await ModelsAsync(name);

        Assert.Equal(
            await PhysStockGateTests.ShapesAsync(_fixture.Cooker, stock),
            await PhysStockGateTests.ShapesAsync(_fixture.Cooker, ours));
    }

    [VbspPhysTheory]
    [MemberData(nameof(Entries), MemberType = typeof(VbspPhysStockGateBase))]
    public async Task ASolidIsStocksSizeOrLargerOnlyByWholeVertices(string name)
    {
        // Measured by p3h: the Windows vphysics.dll pools the vertices brushes
        // share; the Linux library stores each ledge's inline. Single-convex
        // solids are the same size; the rest differ by whole 16-byte points.
        var (ours, stock) = await ModelsAsync(name);
        List<int> a = [.. ours.SelectMany(m => m.Solids.Select(s => s.Length))];
        List<int> b = [.. stock.SelectMany(m => m.Solids.Select(s => s.Length))];

        Assert.Equal(b.Count, a.Count);
        Assert.All(Enumerable.Range(0, a.Count), i => Assert.True(
            a[i] >= b[i] && (a[i] - b[i]) % 16 == 0, $"solid {i}: ours {a[i]}, stock {b[i]}"));
    }

    [VbspPhysTheory]
    [MemberData(nameof(Entries), MemberType = typeof(VbspPhysStockGateBase))]
    public async Task PhysDispIsPresentExactlyWhenStocksIs(string name)
    {
        // The -novirtualmesh road (a power-4 displacement) writes no lump.
        (BspData ours, BspData stock) = await BothAsync(name);

        Assert.Equal(stock[BspLump.PhysDisp].Data.Length == 0, ours[BspLump.PhysDisp].Data.Length == 0);
    }

    [VbspPhysTheory]
    [MemberData(nameof(Entries), MemberType = typeof(VbspPhysStockGateBase))]
    public async Task EachDisplacementsHullHasStocksSize(string name)
    {
        (BspData ours, BspData stock) = await BothAsync(name);
        if (stock[BspLump.PhysDisp].Data.Length == 0)
        {
            return;
        }

        Assert.Equal(
            PhysDispLump.ReadSizes(stock[BspLump.PhysDisp].Data.Span),
            PhysDispLump.ReadSizes(ours[BspLump.PhysDisp].Data.Span));
    }

    [VbspPhysTheory]
    [MemberData(nameof(Entries), MemberType = typeof(VbspPhysStockGateBase))]
    public async Task EveryCorrectRecordLoadsThroughTheEnginesLoader(string name)
    {
        // Correct writes bytes stock never did, so they are checked the way
        // the engine reads them: every record through VCollideLoad
        // (cmodel_bsp.cpp:1017), every solid a real collide.
        (BspData correct, _) = await BothAsync(name, correct: true);
        IReadOnlyList<PhysCollideModel> models = PhysCollideLump.Read(correct[BspLump.PhysCollide].Data.Span);

        IReadOnlyList<string> problems = await _fixture.Cooker.RunAsync<IReadOnlyList<string>>(s =>
        {
            List<string> found = [];
            foreach (PhysCollideModel model in models)
            {
                List<byte> body = [];
                foreach (byte[] solid in model.Solids)
                {
                    body.AddRange(BitConverter.GetBytes(solid.Length));
                    body.AddRange(solid);
                }

                body.AddRange(model.KeyData);
                LoadedVCollide v = s.VCollideLoad([.. body], model.Solids.Count);
                if (v.Solids.Count != model.Solids.Count || v.Solids.Any(c => c.IsNull))
                {
                    found.Add($"model {model.ModelIndex}: {v.Solids.Count} of {model.Solids.Count} solids loaded");
                }

                s.VCollideUnload(v);
            }

            return found;
        });

        Assert.NotEmpty(models);
        Assert.Empty(problems);
    }

    [VbspPhysTheory]
    [MemberData(nameof(Entries), MemberType = typeof(VbspPhysStockGateBase))]
    public async Task TheLumpsAreMeasured(string name)
    {
        // Not asserted equal (a different build of the library): appended to
        // PHYS_BYTE_REPORT when set, so the difference is a number.
        (BspData ours, BspData stock) = await BothAsync(name);
        var (a, b) = await ModelsAsync(name);
        int identical = a.SelectMany(m => m.Solids).Zip(b.SelectMany(m => m.Solids)).Count(p => p.First.AsSpan().SequenceEqual(p.Second));

        if (Environment.GetEnvironmentVariable("PHYS_BYTE_REPORT") is { Length: > 0 } report)
        {
            await File.AppendAllTextAsync(report, string.Create(CultureInfo.InvariantCulture,
                $"{name}\tPHYSCOLLIDE {stock[BspLump.PhysCollide].Data.Length}/{ours[BspLump.PhysCollide].Data.Length}\t"
                + $"solids {b.Sum(m => m.Solids.Count)} identical {identical}\t"
                + $"PHYSDISP {stock[BspLump.PhysDisp].Data.Length}/{ours[BspLump.PhysDisp].Data.Length}\n"));
        }

        Assert.NotEmpty(a);
    }

    private static List<string> Tokens(string text) =>
        [.. System.Text.RegularExpressions.Regex.Split(text, "([\"\\s]+)").Where(t => t.Length > 0)];
}

/// <summary>A native-tier theory over a <see cref="StockLoad"/> catalogue: skips without one.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class VbspPhysTheoryAttribute : VPhysicsNativeTheoryAttribute
{
    /// <summary>Skips outside the child, or without <c>VVIS_STOCK_DIR</c>.</summary>
    public VbspPhysTheoryAttribute()
        : base(StockLoad.SkipReason())
    {
    }
}
