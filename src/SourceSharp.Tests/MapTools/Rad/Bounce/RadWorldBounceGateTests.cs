//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.Tests.MapTools.Rad.Light;

using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.Rad.Bounce;

/// <summary>
/// The 4d gates: stock vrad's <c>transfers %d, max %d</c> and
/// <c>Bounce #%i added RGB...)</c> lines against the managed transfer build
/// and bounce, on stock vbsp + vvis input, under
/// <see cref="ComplianceOptions.Stock"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tolerances, frozen from the first measurement</b>. Transfer TOTALS were exact on 11 of 20 maps
/// and within 5 on the rest -- at most 2.6e-5 of the total, on
/// <c>l1_func_detail</c>. Every map runs hundreds of polygon form factors
/// whose edge sine lies within 3 ulp of 1, where
/// <see cref="StockQuirk.FormFactorSineAboveOne"/> keeps or drops a transfer
/// on a single rounding, and MSVC's arithmetic is not reproducible to the ulp.
/// <c>max_transfer</c> and the bounce COUNT were exact on every map. Bounce
/// energies were within 0.51 % (worst: <c>l3_arena_144_pillars</c> bounce 1,
/// red) or 1 in the printed integer; that residue is the direct light 4c hands
/// over, whose own gate names the same open-sky maps as its worst.
/// </para>
/// </remarks>
public sealed class RadWorldBounceGateTests(ITestOutputHelper output)
{
    /// <summary>|managed - stock| transfers allowed, as a fraction of stock's total.</summary>
    private const double TransferTolerance = 3e-5;

    /// <summary>Relative tolerance on a bounce's printed energy.</summary>
    private const double EnergyTolerance = 0.006;

    public static TheoryData<string> Maps() => StockBounceReference.Maps();

    public static TheoryData<string> DeterminismMaps()
    {
        TheoryData<string> data = [];
        foreach (string name in (string[])["l1_two_rooms_and_a_door", "l2_detail_and_hint_in_a_corridor", "p4c_texlights"])
        {
            if (StockVradReference.InputFor(name) is not null)
            {
                data.Add(name);
            }
        }

        if (data.Count == 0)
        {
            // xUnit refuses an empty theory; the attribute skips it anyway.
            data.Add("l1_two_rooms_and_a_door");
        }

        return data;
    }

    [StockVradTheory]
    [MemberData(nameof(Maps))]
    public async Task TransferTotalMatchesStock(string name)
    {
        StockBounce stock = StockBounceReference.Load()[name];
        BounceSummary ours = await BounceCache.Get(name);

        long diff = ours.Transfers - stock.Transfers;
        output.WriteLine(Invariant($"{name}: transfers {ours.Transfers} stock {stock.Transfers} ({diff:+#;-#;0})"));
        Assert.True(
            Math.Abs(diff) <= Math.Max(1, TransferTolerance * stock.Transfers),
            Invariant($"{name}: managed {ours.Transfers} transfers, stock {stock.Transfers}"));
    }

    [StockVradTheory]
    [MemberData(nameof(Maps))]
    public async Task MaxTransfersMatchesStock(string name)
    {
        StockBounce stock = StockBounceReference.Load()[name];
        BounceSummary ours = await BounceCache.Get(name);
        Assert.Equal(stock.MaxTransfers, ours.Max);
    }

    [StockVradTheory]
    [MemberData(nameof(Maps))]
    public async Task BounceCountMatchesStock(string name)
    {
        StockBounce stock = StockBounceReference.Load()[name];
        BounceSummary ours = await BounceCache.Get(name);
        Assert.Equal(stock.Bounces.Count, ours.BounceEnergies.Count);
    }

    [StockVradTheory]
    [MemberData(nameof(Maps))]
    public async Task EveryBouncesEnergyMatchesStock(string name)
    {
        StockBounce stock = StockBounceReference.Load()[name];
        BounceSummary ours = await BounceCache.Get(name);

        List<string> bad = [];
        int n = Math.Min(stock.Bounces.Count, ours.BounceEnergies.Count);
        for (int i = 0; i < n; i++)
        {
            Vec3 e = ours.BounceEnergies[i];
            (long r, long g, long b) = stock.Bounces[i];
            output.WriteLine(Invariant($"{name} #{i + 1}: {e.X:F0} {e.Y:F0} {e.Z:F0} stock {r} {g} {b}"));
            Check(bad, i, 'R', e.X, r);
            Check(bad, i, 'G', e.Y, g);
            Check(bad, i, 'B', e.Z, b);
        }

        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    /// <summary>
    /// <see cref="StockQuirk.FormFactorSineAboveOne"/> is reachable on a real
    /// map and is what stock does: the stock side matches
    /// <c>l1_sealed_room</c>'s total exactly, and correct keeps more.
    /// </summary>
    [StockVradFact]
    public async Task CorrectKeepsTheTransfersStockDropsOnASineAboveOne()
    {
        const string Name = "l1_sealed_room";
        StockBounce stock = StockBounceReference.Load()[Name];
        BounceSummary stockSide = await BounceCache.Get(Name);
        RadWorld correct = await StockBounceReference.BounceAsync(
            Name, compliance: ComplianceOptions.Stock.Flipping(StockQuirk.FormFactorSineAboveOne));

        output.WriteLine(Invariant($"stock {stock.Transfers}, stock side {stockSide.Transfers}, correct {correct.Transfers!.Total}"));
        Assert.Equal(stock.Transfers, stockSide.Transfers);
        Assert.True(correct.Transfers.Total > stock.Transfers);
    }

    /// <summary>
    /// One worker and sixteen produce the same transfers and the same bounced
    /// light, byte for byte.
    /// </summary>
    [StockVradTheory]
    [MemberData(nameof(DeterminismMaps))]
    public async Task OneWorkerAndSixteenAreByteIdentical(string name)
    {
        RadWorld one = await StockBounceReference.BounceAsync(name, CompileParallelism.Serial);
        string first = Hash(one);
        one = null!;
        RadWorld many = await StockBounceReference.BounceAsync(name, new CompileParallelism { MaxDegree = 16 });

        Assert.Equal(first, Hash(many));
    }

    /// <summary>SHA-256 over every transfer, every patch's bounced light and every bounce's total.</summary>
    internal static string Hash(RadWorld world)
    {
        using IncrementalHash h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(MemoryMarshal.AsBytes(world.Transfers!.Arena));
        for (int p = 0; p < world.Patches.Count; p++)
        {
            BumpLights total = world.Patches.At(p).TotalLight;
            h.AppendData(MemoryMarshal.AsBytes(new ReadOnlySpan<BumpLights>(in total)));
            h.AppendData(BitConverter.GetBytes(world.Transfers.CountFor(p)));
        }

        foreach (Vec3 e in world.BounceEnergies)
        {
            Vec3 copy = e;
            h.AppendData(MemoryMarshal.AsBytes(new ReadOnlySpan<Vec3>(in copy)));
        }

        return Convert.ToHexString(h.GetHashAndReset());
    }

    private static void Check(List<string> bad, int bounce, char channel, float ours, long stock)
    {
        double diff = Math.Abs(Math.Round(ours, MidpointRounding.ToEven) - stock);
        if (diff > Math.Max(1.0, EnergyTolerance * stock))
        {
            bad.Add(Invariant($"bounce #{bounce + 1} {channel}: managed {ours:F0}, stock {stock}"));
        }
    }

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}

/// <summary>What the gate theories read from one map's bounce.</summary>
/// <param name="Transfers">The transfer total.</param>
/// <param name="Max">The longest list.</param>
/// <param name="BounceEnergies">Each bounce's total.</param>
internal sealed record BounceSummary(long Transfers, int Max, IReadOnlyList<Vec3> BounceEnergies);

/// <summary>
/// One stock-compliance bounce per map, shared by the four per-map theories
/// and kept only as its summary: holding twenty lit worlds would cost
/// gigabytes.
/// </summary>
internal static class BounceCache
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Task<BounceSummary>> Summaries = [];

    internal static Task<BounceSummary> Get(string name)
    {
        lock (Gate)
        {
            if (!Summaries.TryGetValue(name, out Task<BounceSummary>? summary))
            {
                summary = Run(name);
                Summaries[name] = summary;
            }

            return summary;
        }
    }

    private static async Task<BounceSummary> Run(string name)
    {
        RadWorld world = await StockBounceReference.BounceAsync(name);
        return new BounceSummary(world.Transfers!.Total, world.Transfers.Max, world.BounceEnergies);
    }
}
