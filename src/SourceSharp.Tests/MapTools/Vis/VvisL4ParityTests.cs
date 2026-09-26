//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;

using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Vis;

/// <summary>Attribute for a fact needing the L4 fixture directory.</summary>
public sealed class L4FactAttribute : FactAttribute
{
    public L4FactAttribute()
    {
        if (VvisL4ParityTests.Directory is null)
        {
            Skip = VvisL4ParityTests.SkipMessage;
        }
    }
}

/// <summary>
/// The vis-repair rung4 gates on the one L4 map both arms have ledgers for:
/// <c>sdk_ctf_2fort</c> from the p2c L4 fixture directory, compared against
/// the stock <c>-threads 1</c> sorted visibility the p5-vis lane sealed
/// (<c>p5-vis/l4/sdk_ctf_2fort.stock.dump</c>, SSVIS1, sha256
/// <c>c9c10a1cc84ba3950ed503acf43f5f6d89029133aec565dc9fdb63d38a50d31d</c>).
/// </summary>
/// <remarks>
/// <para>
/// Why a separate file from <see cref="VvisCatalogueTests"/>: that suite is
/// gated on <c>VVIS_STOCK_DIR</c> (the 31-map Wine-built catalogue). This one
/// is gated on <c>VVIS_L4_DIR</c>, a directory holding the stock-vbsp'd
/// <c>sdk_ctf_2fort.bsp</c>/<c>.prt</c> and the sealed <c>.stock.dump</c>,
/// because these facts are the Phase-5 promotion's L4 evidence — the default
/// arm shipping bit-identical to stock t1 on the real map the report card
/// quotes, and the untightened arm staying the conservative superset with a
/// pinned work budget (rung4: untightened chains &lt;= stock-equivalent x 4;
/// the actual ratio was measured at 3.92x, 569,567,630 against stock's
/// 145,435,257).
/// </para>
/// <para>
/// The recipe, which is what a reader of a skipped test needs:
/// </para>
/// <code>
/// VVIS_L4_DIR=&lt;the reference l4 directory&gt; \
///   dotnet test --filter VvisL4Parity
/// # the directory must also hold sdk_ctf_2fort.stock.dump, copied from the
/// # reference recipe's l4 output (verify its sha256 above first).
/// </code>
/// </remarks>
public class VvisL4ParityTests
{
    private const string MapName = "sdk_ctf_2fort";
    private const string DirectoryVariable = "VVIS_L4_DIR";

    /// <summary>Stock's own chain count at one thread, summed from its <c>-verbose</c> (p5a §1, from p2b).</summary>
    private const long StockOneThreadChains = 145_435_257;

    /// <summary>
    /// The rung4 budget: the untightened walk may trace at most this many
    /// times the chains stock-at-one-thread traces on the same fixture. The
    /// measured ratio is 3.92 (569,567,630 / 145,435,257); four is the pinned
    /// ceiling — a regression that pushes past it is the per-ray work the
    /// repair lane exists to prevent, not a tolerance to widen.
    /// </summary>
    private const long UntightenedChainBudgetFactor = 4;

    internal static string? Directory =>
        Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } set ? set : null;

    internal static string SkipMessage =>
        $"no {DirectoryVariable}: no L4 pair to gate on. See VvisL4ParityTests for the recipe.";

    [L4Fact]
    public async Task TheDefaultArmWritesStocksOneThreadSortedVisOnTheL4Map()
    {
        // The promoted default — not `Tighten = true` spelled, but the record
        // a caller actually gets — at sixteen threads must reproduce stock's
        // one-thread sorted visibility row for row, and its own one-thread
        // run must agree (spike 0c's disease, cured: thread count cannot move
        // the shipped default's answer).
        string dir = Directory!;
        VisResult many = await RunAsync(dir, VvisOptions.Default, degree: 16);
        VisResult single = await RunAsync(dir, VvisOptions.Default, degree: 1);

        Assert.Equal(single.VisDataSize, many.VisDataSize);
        for (int cluster = 0; cluster < single.ClusterCount; cluster++)
        {
            Assert.True(
                single.Pvs(cluster).ToArray().AsSpan()
                    .SequenceEqual(many.Pvs(cluster).ToArray()),
                $"PVS row {cluster} moved between one and sixteen threads");
        }

        AssertAgainstStockDump(dir, many, containsOnly: false);
    }

    [L4Fact]
    public async Task TheUntightenedArmContainsStockAndStaysWithinTheChainBudget()
    {
        // One thread, so the counters are a property of the map, not of a
        // schedule (see VvisTightenTests for why the tightened arm's counters
        // do not qualify — speculative re-walks move them run to run).
        string dir = Directory!;
        VisResult loose = await RunAsync(dir, VvisOptions.Untightened, degree: 1);

        AssertAgainstStockDump(dir, loose, containsOnly: true);

        long chains = loose.Work.Chains;
        Assert.True(
            chains > StockOneThreadChains,
            $"the untightened walk traced {chains:N0} chains — at or below stock's "
            + $"{StockOneThreadChains:N0}; p5a measured 569,567,630, so the fixture changed "
            + "or the flood started pruning like the tightened arm");

        Assert.True(
            chains <= UntightenedChainBudgetFactor * StockOneThreadChains,
            $"the untightened walk traced {chains:N0} chains against stock's "
            + $"{StockOneThreadChains:N0}: ratio {chains / (double)StockOneThreadChains:F2}, "
            + $"/{UntightenedChainBudgetFactor:N0} pinned (measured 3.92, p5a §1). "
            + "This is rung4's budget: per-ray work regressed, fix the walk.");
    }

    /// <summary>
    /// Every PVS/PAS row of <paramref name="result"/> equals the sealed dump's
    /// (or, with <paramref name="containsOnly"/>, contains it — the direction
    /// the untightened arm is allowed, the direction the default arm is not).
    /// </summary>
    /// <remarks>
    /// The dump is the SSVIS1 shape <c>ssmap vvis --dump-vis</c> writes:
    /// magic, clusters, rowBytes, then uncompressed rows — PVS block, PAS
    /// block — which is why this compares bytes directly instead of going
    /// through <see cref="SourceSharp.MapFormats.Bsp.VisibilityLump"/>.
    /// </remarks>
    private static void AssertAgainstStockDump(
        string dir, VisResult result, bool containsOnly)
    {
        string dumpPath = Path.Combine(dir, MapName + ".stock.dump");
        Assert.True(File.Exists(dumpPath), $"no {dumpPath} — see the recipe on this class");

        byte[] dump = File.ReadAllBytes(dumpPath);
        byte[] magicBytes = System.Text.Encoding.ASCII.GetBytes(VvisCommand.DumpMagic);
        Assert.True(
            dump[..magicBytes.Length].SequenceEqual(magicBytes),
            $"{dumpPath} does not start with {VvisCommand.DumpMagic}");

        ReadOnlySpan<byte> header = dump.AsSpan(magicBytes.Length, 8);
        int clusters = MemoryMarshal.Read<int>(header[..4]);
        int rowBytes = MemoryMarshal.Read<int>(header[4..8]);

        Assert.Equal(clusters, result.ClusterCount);
        Assert.Equal(rowBytes, result.RowBytes);
        Assert.Equal(
            magicBytes.Length + 8 + (long)clusters * rowBytes * 2, dump.LongLength);

        ReadOnlySpan<byte> payload = dump.AsSpan(magicBytes.Length + 8);
        for (int cluster = 0; cluster < clusters; cluster++)
        {
            AssertRow(
                "PVS", cluster, rowBytes,
                payload.Slice(cluster * rowBytes, rowBytes),
                result.Pvs(cluster),
                containsOnly);
            AssertRow(
                "PAS", cluster, rowBytes,
                payload.Slice((clusters + cluster) * rowBytes, rowBytes),
                result.Pas(cluster),
                containsOnly);
        }
    }

    private static void AssertRow(
        string name, int cluster, int rowBytes,
        ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, bool containsOnly)
    {
        Assert.Equal(rowBytes, actual.Length);
        if (containsOnly)
        {
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.True(
                    (actual[i] & expected[i]) == expected[i],
                    $"{name} row {cluster} byte {i}: the untightened arm lost bit "
                    + $"{Convert.ToString(expected[i] & ~actual[i], 2).PadLeft(8, '0')} that stock has");
            }
        }
        else
        {
            Assert.True(
                expected.SequenceEqual(actual),
                $"{name} row {cluster} differs from the sealed stock dump");
        }
    }

    private static async Task<VisResult> RunAsync(string dir, VvisOptions options, int degree)
    {
        BspData map;
        await using (FileStream stream = File.OpenRead(Path.Combine(dir, MapName + ".bsp")))
        {
            map = await BspFile.LoadAsync(stream, CancellationToken.None);
        }

        byte[] prt = await File.ReadAllBytesAsync(Path.Combine(dir, MapName + ".prt"));
        PortalFile parsed = await PortalFile.ParseAsync(prt, CancellationToken.None);

        VisContext context = new()
        {
            Options = options,
            Parallelism = new CompileParallelism { MaxDegree = degree },
        };
        return await Vvis.ComputeAsync(
            map, PortalSet.FromPortalFile(parsed), context, CancellationToken.None);
    }
}
