using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Phys;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.Tests.MapTools.Phys;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Collision;

/// <summary>
/// Plan §7's gate: managed <c>EmitPhysCollision</c> over stock's own lumps
/// against stock's LUMP_PHYSCOLLIDE, per map -- equal models, equal solid
/// counts, equal per-solid byte sizes, equal keydata text.
/// </summary>
/// <remarks>
/// <para>
/// Stock <c>vbsp.exe</c> under wine loads the WINDOWS <c>vphysics.dll</c>
/// (<c>sdk2013-win-tools/bin/x64</c>, md5 <c>972f9018...</c>); this port loads
/// the pinned Linux <c>vphysics.so</c>. Spike 0b measured two builds of the
/// same library disagreeing in 15 of a cube's 440 bytes, so the blob BYTES
/// are measured and reported (<see cref="BlobBytesAreMeasured"/>) rather than
/// asserted equal; the sizes, counts and text are asserted.
/// </para>
/// </remarks>
[Collection(VPhysicsCollection.Name)]
[Trait("Tier", PinnedVPhysics.Tier)]
[VPhysicsJournal]
public class PhysStockGateTests
{
    private static readonly ConcurrentDictionary<string, Task<(IReadOnlyList<PhysCollideModel> Ours, IReadOnlyList<PhysCollideModel> Stock)>> Cache = new();

    private readonly VPhysicsCookerFixture _fixture;

    public PhysStockGateTests(VPhysicsCookerFixture fixture) => _fixture = fixture;

    /// <summary>The maps.</summary>
    public static TheoryData<string> Maps()
    {
        TheoryData<string> data = [];
        foreach (string name in PhysStockCatalogue.Names)
        {
            data.Add(name);
        }

        return data;
    }

    private Task<(IReadOnlyList<PhysCollideModel> Ours, IReadOnlyList<PhysCollideModel> Stock)> BothAsync(string name) =>
        Cache.GetOrAdd(name, async n =>
        {
            BspData bsp = await PhysStockCatalogue.LoadAsync(n);
            PhysCollisionInput input = await PhysStockCatalogue.InputAsync(n, bsp);
            PhysCollisionResult ours = await PhysCollisionEmitter.EmitAsync(input, _fixture.Cooker);
            return (PhysCollideLump.Read(ours.PhysCollide), PhysCollideLump.Read(bsp[BspLump.PhysCollide].Data.Span));
        });

    [PhysStockTheory]
    [MemberData(nameof(Maps))]
    public async Task TheCorrectLumpLoadsThroughTheEnginesLoader(string map)
    {
        // Compliance.Correct changes bytes stock never wrote, so they are
        // checked the way the engine will read them: every record through
        // VCollideLoad (cmodel_bsp.cpp:1017's call), every solid a real
        // collide with volume.
        BspData bsp = await PhysStockCatalogue.LoadAsync(map);
        PhysCollisionInput input = await PhysStockCatalogue.InputAsync(map, bsp);
        PhysCollisionResult correct = await PhysCollisionEmitter.EmitAsync(input with { Compliance = SourceSharp.MapTools.Options.ComplianceOptions.Correct }, _fixture.Cooker);

        IReadOnlyList<string> loaded = await _fixture.Cooker.RunAsync<IReadOnlyList<string>>(s =>
        {
            List<string> problems = [];
            foreach (PhysCollideModel model in correct.Models)
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
                    problems.Add($"model {model.ModelIndex}: {v.Solids.Count} of {model.Solids.Count} solids loaded");
                }

                s.VCollideUnload(v);
            }

            return problems;
        });

        if (Environment.GetEnvironmentVariable("PHYS_BYTE_REPORT") is { Length: > 0 } report)
        {
            var (stockMode, _) = await BothAsync(map);
            bool same = stockMode.Select(m => m.KeyText).SequenceEqual(correct.Models.Select(m => m.KeyText))
                && stockMode.SelectMany(m => m.Solids).Zip(correct.Models.SelectMany(m => m.Solids)).All(p => p.First.AsSpan().SequenceEqual(p.Second));
            await File.AppendAllTextAsync(report + ".modes", $"{map}\tcorrect-vs-stock-mode\t{(same ? "same" : "DIFFERS")}\n");
        }

        Assert.Empty(loaded);
    }

    [PhysStockTheory]
    [MemberData(nameof(Maps))]
    public async Task TheSameModelsHaveCollision(string map)
    {
        var (ours, stock) = await BothAsync(map);

        Assert.Equal(stock.Select(m => m.ModelIndex), ours.Select(m => m.ModelIndex));
    }

    [PhysStockTheory]
    [MemberData(nameof(Maps))]
    public async Task EachModelHasStocksSolidCount(string map)
    {
        var (ours, stock) = await BothAsync(map);

        Assert.Equal(stock.Select(m => m.Solids.Count), ours.Select(m => m.Solids.Count));
    }

    [PhysStockTheory]
    [MemberData(nameof(Maps))]
    public async Task EachSingleConvexSolidHasStocksByteSize(string map)
    {
        var (ours, stock) = await BothAsync(map);
        IReadOnlyList<string> shapes = await ShapesAsync(_fixture.Cooker, stock);

        Assert.Equal(
            Sizes(stock).Where((_, i) => shapes[i].StartsWith("1 ", StringComparison.Ordinal)),
            Sizes(ours).Where((_, i) => shapes[i].StartsWith("1 ", StringComparison.Ordinal)));
    }

    [PhysStockTheory]
    [MemberData(nameof(Maps))]
    public async Task EachSolidHasStocksShape(string map)
    {
        // Both blobs read back through the pinned library's query model:
        // convex count, per-convex triangle count and game data (the brush
        // number), every triangle's material index, volume and bounds.
        var (ours, stock) = await BothAsync(map);

        Assert.Equal(await ShapesAsync(_fixture.Cooker, stock), await ShapesAsync(_fixture.Cooker, ours));
    }

    [PhysStockTheory]
    [MemberData(nameof(Maps))]
    public async Task AMultiConvexSolidIsLargerOnlyByWholeVertices(string map)
    {
        // Measured on l1_sealed_room: stock's Windows vphysics.dll pools the
        // vertices that brushes share (34 points for 6 boxes) where the Linux
        // library stores each ledge's 8 inline (48): 14 x 16 bytes = 224, the
        // whole difference, with an identical ledge tree. So the difference
        // must be a non-negative multiple of one 16-byte IVP point.
        var (ours, stock) = await BothAsync(map);
        List<int> a = Sizes(ours), b = Sizes(stock);

        Assert.Equal(b.Count, a.Count);
        Assert.All(Enumerable.Range(0, a.Count), i => Assert.True(
            a[i] >= b[i] && (a[i] - b[i]) % 16 == 0, $"solid {i}: ours {a[i]}, stock {b[i]}"));
    }

    private static List<int> Sizes(IReadOnlyList<PhysCollideModel> models) =>
        [.. models.SelectMany(m => m.Solids.Select(s => s.Length))];

    internal static Task<IReadOnlyList<string>> ShapesAsync(ICollisionCooker cooker, IReadOnlyList<PhysCollideModel> models) =>
        cooker.RunAsync<IReadOnlyList<string>>(s =>
        {
            List<string> shapes = [];
            foreach (byte[] blob in models.SelectMany(m => m.Solids))
            {
                CollideHandle c = s.UnserializeCollide(blob, 0);
                StringBuilder shape = new();
                s.WithQueryModel(c, q =>
                {
                    shape.Append(CultureInfo.InvariantCulture, $"{q.ConvexCount} ");
                    for (int i = 0; i < q.ConvexCount; i++)
                    {
                        shape.Append(CultureInfo.InvariantCulture, $"[{q.GetGameData(i)}:");
                        for (int t = 0; t < q.TriangleCount(i); t++)
                        {
                            shape.Append(CultureInfo.InvariantCulture, $"{q.GetTriangleMaterialIndex(i, t)},");
                        }

                        shape.Append(']');
                    }
                });

                (Vec3 mins, Vec3 maxs) = s.CollideGetAABB(c, Vec3.Zero, Vec3.Zero);
                shape.Append(CultureInfo.InvariantCulture, $" vol={s.CollideVolume(c)} box={mins}..{maxs}");
                s.DestroyCollide(c);
                shapes.Add(shape.ToString());
            }

            return shapes;
        });

    [PhysStockTheory]
    [MemberData(nameof(Maps))]
    public async Task EachModelHasStocksKeydataText(string map)
    {
        // The text is the port's; three of its numbers are the cooker's
        // (a solid's volume, the mass computed from it, and a surfaceless
        // fluid's CollideGetExtent top). Those may differ by float ULPs
        // between the Windows and Linux builds -- measured on
        // x0_areaportal_in_water: 63.999992 there, 64.000000 here -- so a
        // number is equal within 1e-6 relative; every other byte is exact.
        var (ours, stock) = await BothAsync(map);
        string expected = string.Join("\n--\n", stock.Select(m => m.KeyText));
        string actual = string.Join("\n--\n", ours.Select(m => m.KeyText));

        if (expected != actual && Environment.GetEnvironmentVariable("PHYS_BYTE_REPORT") is { Length: > 0 } report)
        {
            await File.AppendAllTextAsync(report + ".keys", $"== {map} STOCK\n{expected}\n== {map} OURS\n{actual}\n");
        }

        Assert.Equal(Tokens(expected).Count, Tokens(actual).Count);
        Assert.All(Tokens(expected).Zip(Tokens(actual)), pair =>
        {
            if (double.TryParse(pair.First, NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
                && double.TryParse(pair.Second, NumberStyles.Float, CultureInfo.InvariantCulture, out double b)
                && pair.First.Contains('.', StringComparison.Ordinal))
            {
                Assert.True(Math.Abs(a - b) <= 1e-6 * Math.Max(1.0, Math.Abs(a)), $"{pair.First} vs {pair.Second}");
            }
            else
            {
                Assert.Equal(pair.First, pair.Second);
            }
        });
    }

    private static List<string> Tokens(string text) =>
        [.. System.Text.RegularExpressions.Regex.Split(text, "([\"\\s]+)").Where(t => t.Length > 0)];

    [PhysStockTheory]
    [MemberData(nameof(Maps))]
    public async Task BlobBytesAreMeasured(string map)
    {
        var (ours, stock) = await BothAsync(map);

        // Not asserted equal: stock's bytes come from a different build of
        // the library. Appended to PHYS_BYTE_REPORT when set, so the
        // difference is measured and reported rather than assumed.
        StringBuilder line = new();
        for (int m = 0; m < Math.Min(ours.Count, stock.Count); m++)
        {
            for (int s = 0; s < Math.Min(ours[m].Solids.Count, stock[m].Solids.Count); s++)
            {
                byte[] a = ours[m].Solids[s];
                byte[] b = stock[m].Solids[s];
                int differing = 0;
                for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
                {
                    if (a[i] != b[i])
                    {
                        differing++;
                    }
                }

                line.Append(CultureInfo.InvariantCulture, $"{map}\t{ours[m].ModelIndex}\t{s}\t{b.Length}\t{a.Length}\t{differing}\n");
            }
        }

        if (Environment.GetEnvironmentVariable("PHYS_BYTE_REPORT") is { Length: > 0 } report)
        {
            await File.AppendAllTextAsync(report, line.ToString());
        }

        Assert.NotEmpty(ours);
    }
}

/// <summary>A native-tier theory over the stock catalogue: skips when <c>PHYS_STOCK_DIR</c> is unset.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PhysStockTheoryAttribute : VPhysicsNativeTheoryAttribute
{
    /// <summary>Skips outside the child, or when there is no stock catalogue.</summary>
    public PhysStockTheoryAttribute()
        : base(PhysStockCatalogue.Directory is null
            ? $"no {PhysStockCatalogue.DirectoryVariable}: no stock-compiled catalogue to gate the physics lump against "
              + "(point it at ~/.cache/maptools/ref/catmaps)."
            : null)
    {
    }
}
