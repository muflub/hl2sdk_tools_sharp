using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Zip;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Tracing;

using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.ToolsPlusPlus;

/// <summary>
/// The managed vrad against Tools++ <c>vradplusplus.exe</c> on direct lighting
/// (plan tools++ §T6): same ++vvis output on both sides, our LIGHTING lump
/// within the frozen tolerance against ++'s, WORLDLIGHTS behaviour matched.
/// </summary>
/// <remarks>
/// <para>
/// Gate input is <c>csgoclip/probe_csgoclip.ppvis.bsp</c>, the only corpus
/// stage output whose ++vrad run wrote real LIGHTING samples (906,692 bytes;
/// the default-preset pprad outputs carry an EMPTY LIGHTING lump — ++vrad
/// itself lit zero faces there, and our vrad reproduces that empty-lump
/// behaviour in its own fact below).
/// </para>
/// <para>
/// <b>Masking.</b> Unlit faces (face <c>lightofs == -1</c>) keep whatever the
/// sample buffer held before writing; both ++ and stock leave exponent bytes
/// of 253–255 (uninitialised float bits) in those samples — 128,622 of the
/// map's 226,671 records. Samples where either side shows an exponent ≥ 200
/// are excluded from the numeric comparison: comparing them compares stack
/// garbage. The remaining 98,049 comparable samples carry the real signal.
/// This is the same uninitialised-memory class the stock gates already model
/// (<see cref="MapTools.Rad.Final.FrozenTolerance"/> remarks).
/// </para>
/// <para>
/// <b>Tolerance.</b> plan_maptools.md §0d: the lightmap threshold must sit
/// above the AddSampleToPatch race floor between two stock runs; §8's gate
/// wording is "thresholds set above the 0d noise floor". The measured
/// cross-binary distribution here (frozen below) is p99 0.0235 (= 6/255,
/// ~3 LSB), max 0.278 on the two worst lit faces — set from the first
/// measurement and frozen, stock-gate style: regressions raise the count and
/// fail, improvements pass.
/// </para>
/// <para>
/// ++ runs at its default 32 threads here are non-deterministic at byte level
/// (M2: two runs differ), so the reference is the corpus output as produced;
/// the ++-side stability fact this tier relies on is <c>-threads 1</c>
/// byte-equality across runs (T6 findings), not this file's ref.
/// </para>
/// </remarks>
public sealed class VradCrossToolParityTests(ITestOutputHelper output)
{
    /// <summary>Preset dir and map with real ++vrad LIGHTING output.</summary>
    public const string Preset = "csgoclip";

    public const string Map = "probe_csgoclip";

    /// <summary>
    /// LIGHTING records (non-masked, i.e. both sides' exponent &lt; 200) that
    /// may differ from ++'s by more than a byte. Measured 56,097 differing
    /// records of 98,049 comparable; frozen one percent above the measurement
    /// — the cross-binary floor between ficool2's ZHLT-derived lighting and
    /// ours, dominated by float-order noise, not by missing light.
    /// </summary>
    private const int FrozenDifferingRecords = 56660;

    /// <summary>Max p99 per-channel linear error, plan §0d-style ceiling.</summary>
    private const double FrozenP99 = 0.025;

    /// <summary>Max worst per-channel linear error (unlit-edge rays grazing brush seams).</summary>
    private const double FrozenMax = 0.30;

    [PpCrossToolFact]
    public async Task LightingLumpIsWithinFrozenToleranceAgainstVradPlusPlus()
    {
        string root = PpCrossToolHarness.Corpus!;
        string ppvis = Path.Combine(root, Preset, $"{Map}.ppvis.bsp");
        string pprad = Path.Combine(root, Preset, $"{Map}.pprad.bsp");
        Assert.Multiple(
            () => Assert.True(File.Exists(ppvis), $"no {ppvis}"),
            () => Assert.True(File.Exists(pprad), $"no {pprad}"));

        BspData ours = await LoadAsync(ppvis);
        RadResult result = await LightAsync(ours, ppvis);
        BspData theirs = await LoadAsync(pprad);

        byte[] us = ours[BspLump.Lighting].Data.ToArray();
        byte[] them = theirs[BspLump.Lighting].Data.ToArray();
        Assert.Equal(them.Length, us.Length); // same face/lightmap layout ⇒ same sample count
        Assert.Equal(theirs[BspLump.Lighting].Version, ours[BspLump.Lighting].Version);

        int samples = (us.Length - 8) / 4; // header is lumpsum_t {size, sizeHDR}
        int comparable = 0;
        int differing = 0;
        int exact = 0;
        List<double> errors = [];
        for (int s = 0; s < samples; s++)
        {
            int b = 8 + s * 4;
            if (us[b + 3] >= 200 || them[b + 3] >= 200)
            {
                continue; // both/one side uninitialised: unlit face, masked
            }

            comparable++;
            if (us[b] == them[b] && us[b + 1] == them[b + 1] && us[b + 2] == them[b + 2] && us[b + 3] == them[b + 3])
            {
                exact++;
            }
            else
            {
                differing++;
            }

            double worst = 0;
            for (int ch = 0; ch < 3; ch++)
            {
                worst = Math.Max(worst, Math.Abs(Decode(us[b + ch], us[b + 3]) - Decode(them[b + ch], them[b + 3])));
            }

            errors.Add(worst);
        }

        errors.Sort();
        double P99 = errors.Count == 0 ? 0 : errors[Math.Min(errors.Count - 1, (int)Math.Ceiling(0.99 * errors.Count) - 1)];
        double max = errors.Count == 0 ? 0 : errors[^1];
        output.WriteLine($"{Preset}/{Map}: samples={samples} comparable={comparable} masked={samples - comparable} "
            + $"exact={exact} differing={differing} p99={P99:F6} max={max:F6} passes={result.Passes.Count}");

        Assert.Equal(98_049, comparable);
        Assert.True(differing <= FrozenDifferingRecords, $"{differing} differing records, frozen at {FrozenDifferingRecords}");
        Assert.True(P99 <= FrozenP99, $"p99 {P99:F6} over frozen ceiling {FrozenP99}");
        Assert.True(max <= FrozenMax, $"max {max:F6} over frozen ceiling {FrozenMax}");
    }

    [PpCrossToolTheory]
    [InlineData("default", "p3f_p3_bump")]
    [InlineData("default", "probe_csgoclip")]
    public async Task EmptyLightingBehaviourMatchesOnUnlitMaps(string preset, string map)
    {
        // ++vrad writes an EMPTY LIGHTING lump on these maps (no face reached a
        // lightmap sample). Our vrad must do the same, not invent samples.
        string root = PpCrossToolHarness.Corpus!;
        string ppvis = Path.Combine(root, preset, $"{map}.ppvis.bsp");
        string pprad = Path.Combine(root, preset, $"{map}.pprad.bsp");
        Assert.Multiple(
            () => Assert.True(File.Exists(ppvis), $"no {ppvis}"),
            () => Assert.True(File.Exists(pprad), $"no {pprad}"));

        BspData ours = await LoadAsync(ppvis);
        await LightAsync(ours, ppvis);
        BspData theirs = await LoadAsync(pprad);

        Assert.Equal(0, theirs[BspLump.Lighting].Length);
        Assert.Equal(0, ours[BspLump.Lighting].Length);

        // WORLDLIGHTS: both sides write the same-size direct-light lump here
        // (88 bytes at version 0 on the default preset).
        Assert.Equal(theirs[BspLump.WorldLights].Length, ours[BspLump.WorldLights].Length);
    }

    private static async Task<RadResult> LightAsync(BspData bsp, string path)
    {
        string mapName = Path.GetFileNameWithoutExtension(path);
        if (mapName.EndsWith(".ppvis", StringComparison.OrdinalIgnoreCase))
        {
            mapName = mapName[..^".ppvis".Length];
        }

        // A supplied tracer skips the shadow-caster loader. The corpus maps
        // carry zero static props (both ++ logs say "0 props added to
        // raytrace"), so an empty scene matches ++ behaviour — and it is the
        // only way our vrad reaches the lighting stages on the csgoclip-preset
        // map, whose 'prps' game lump is version 11 (T6 finding #2).
        VradContext context = new()
        {
            MapName = mapName,
            Parallelism = new CompileParallelism { MaxDegree = 1 },
            Tracer = new BspSurfaceTracer(BspTraceGeometry.Build(bsp)),
        };
        return await Vrad.LightAsync(bsp, context, CancellationToken.None);
    }

    private static double Decode(byte mantissa, byte exponent) =>
        (mantissa / 255.0) * (exponent == 0 ? 1.0 : Math.Pow(2, exponent));

    private static async Task<BspData> LoadAsync(string path)
    {
        await using FileStream file = File.OpenRead(path);
        return await BspFile.LoadAsync(file);
    }
}
