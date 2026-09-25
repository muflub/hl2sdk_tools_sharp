using Xunit.Abstractions;
using SourceSharp.MapFormats.Text;
using System.Buffers.Binary;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.ToolsPlusPlus;

/// <summary>
/// The managed vvis against Tools++ <c>vvisplusplus.exe</c>: same vbsp++ map,
/// same <c>.prt</c>, and the VISIBILITY lump the ++ run wrote must be what our
/// vvis produces for that input (plan tools++ §T6).
/// </summary>
/// <remarks>
/// <para>
/// The corpus's <c>*.ppvis.bsp</c> files are ++'s final vvis outputs (they
/// carry no overlapping-lump defect at this size; the self-inconsistent stage
/// intermediates T0 recorded are the <c>*.pprad*</c> files, and nothing here
/// parses a lump table deeper than <see cref="BspFile"/> does).
/// </para>
/// <para>
/// No masking applies to this lump: it is pure packed cluster rows, no pak
/// file, no dprp padding, no displacement payload. The comparison is raw
/// byte-equality of the whole lump, plus the counters our vvis reports — a
/// run that merely lands the same length with shuffled rows fails.
/// </para>
/// </remarks>
public sealed class VvisCrossToolParityTests(ITestOutputHelper output)
{
    /// <summary>
    /// Maps with a <c>.ppvis.bsp</c> and a <c>.prt</c> in the standing corpus.
    /// Kept as a literal list so an incomplete corpus fails loudly instead of
    /// silently shrinking the tier to one map.
    /// </summary>
    public static TheoryData<string, string> VisMaps() => new()
    {
        { "default", "p3f_p3_bump" },
        { "csgo", "p3f_p3_bump" },
        { "flag-csgo", "p3f_p3_bump" },
        { "csgoclip", "probe_csgoclip" },
    };

    [PpCrossToolTheory]
    [MemberData(nameof(VisMaps))]
    public async Task VisibilityLumpMatchesVvisPlusPlus(string preset, string map)
    {
        string root = PpCrossToolHarness.Corpus!;
        string baseBsp = Path.Combine(root, preset, $"{map}.bsp");
        string prt = Path.Combine(root, preset, $"{map}.prt");
        string ppvis = Path.Combine(root, preset, $"{map}.ppvis.bsp");
        Assert.Multiple(
            () => Assert.True(File.Exists(baseBsp), $"no {baseBsp}"),
            () => Assert.True(File.Exists(prt), $"no {prt}"),
            () => Assert.True(File.Exists(ppvis), $"no {ppvis}"));

        BspData bsp = await LoadAsync(baseBsp);
        PortalFile portals = await LoadPortalsAsync(prt);
        VisResult result = await Vvis.ComputeAsync(
            bsp,
            PortalSet.FromPortalFile(portals),
            new VisContext { Parallelism = new CompileParallelism { MaxDegree = 4 } },
            CancellationToken.None);

        BspData pp = await LoadAsync(ppvis);
        byte[] ours = bsp[BspLump.Visibility].Data.ToArray();
        byte[] theirs = pp[BspLump.Visibility].Data.ToArray();

        output.WriteLine($"{preset}/{map}: clusters={result.ClusterCount} rowBytes={result.RowBytes} "
            + $"visdatasize={result.VisDataSize} totalVisible={result.TotalVisibleClusters}");

        Assert.Equal(theirs.Length, ours.Length);
        Assert.True(ours.AsSpan().SequenceEqual(theirs), "VISIBILITY lump bytes differ from ++vvis");

        // The header both writers write is the cluster count: our counter must
        // be the number the lump itself advertises, and the table our result
        // implies must be the table in ++'s lump, entry for entry.
        Assert.Equal(result.ClusterCount, BinaryPrimitives.ReadInt32LittleEndian(theirs.AsSpan(0, 4)));
        int tableBytes = result.ClusterCount * 8;
        Assert.True(
            ours.AsSpan(0, tableBytes).SequenceEqual(theirs.AsSpan(0, tableBytes)),
            "clusterinfo tables differ even though the whole lump matched (impossible)");
    }

    private static async Task<BspData> LoadAsync(string path)
    {
        await using FileStream file = File.OpenRead(path);
        return await BspFile.LoadAsync(file);
    }

    private static async Task<PortalFile> LoadPortalsAsync(string path)
    {
        await using FileStream file = File.OpenRead(path);
        return await PortalFile.ReadAsync(file);
    }
}
