using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;

using Xunit;

namespace SourceSharp.Tests.MapFormats;

/// <summary>
/// The tools++ oracle tier: BSPs written by the recovered vbsp++ binary,
/// loaded and round-tripped by this port.
/// </summary>
/// <remarks>
/// <para>
/// These are BLACK-BOX artifacts — the ++ exe's own output, never its code —
/// and they close the caveat the synthetic relayout fact opened with: the
/// L4D2 lump-directory decode and the 19/20/21 reader cap are now judged
/// against files an actual ++ build stamped, across every preset that
/// changes the file shape (<c>~/.cache/maptools/bin/make-catmaps++</c>).
/// </para>
/// <para>
/// <b>Why the matrix is pinned per preset and not derived.</b> T0's matrix
/// measured that the re-layout fires for <c>l4d2</c> alone — portal2, asw,
/// csgo and csgoclip are all version 21 with a STANDARD directory. A reader
/// that keyed the re-layout on version 21 alone would read every one of
/// those maps as a pile of nonsense offsets and still not fail loudly if it
/// read garbage consistently. The InlineData rows say which directory must
/// carry which shape; ++ changing its mind becomes a red fact, which is the
/// point of an oracle tier.
/// </para>
/// <para>
/// <b>Stage intermediates are excluded, on purpose.</b> vrad/vis intermediates
/// (<c>*.pprad*</c>, <c>*.ppvis*</c>) reuse freed space, so their lump
/// payloads physically OVERLAP — <c>default/sdk_ctf_2fort.pprad-both.bsp</c>
/// declares 38.4 MB of lumps inside 26.8 MB of file. No writer can reproduce
/// that layout by replaying (ofs, len) placements at fresh offsets, stock
/// least of all: <c>WriteBSPFile</c> linearises payloads exactly as this
/// port's preserve writer does. They are vis/rad-lane subjects, not container
/// ones, so the byte-exact gate covers vbsp++ outputs only.
/// </para>
/// </remarks>
internal static class PpOracle
{
    /// <summary>
    /// Environment variable naming the <c>make-catmaps++</c> output directory.
    /// The standing corpus is <c>~/.cache/maptools/ref/catmaps-pp</c>.
    /// </summary>
    public const string DirectoryVariable = "PP_CATMAPS_DIR";

    /// <summary>The reference directory, or null when the tier is not mounted.</summary>
    public static string? Root =>
        Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } set ? set : null;

    /// <summary>Why the oracle facts cannot run, or null.</summary>
    public static string? SkipReason()
    {
        string? directory = Root;
        if (directory is null)
        {
            return $"no {DirectoryVariable}: this run has no tools++ oracle. The standing "
                + "corpus is ~/.cache/maptools/ref/catmaps-pp (make-catmaps++ from lane T0); "
                + $"export {DirectoryVariable}=/path/to/catmaps-pp to mount it.";
        }

        if (!Directory.Exists(directory))
        {
            return $"{DirectoryVariable}={directory} does not exist.";
        }

        foreach ((string preset, _, _, _) in Matrix)
        {
            if (!File.Exists(Path.Combine(directory, preset, "p3f_p3_bump.bsp")))
            {
                return $"{directory} has no {preset}/p3f_p3_bump.bsp: the corpus is "
                    + "incomplete for this fact set, which pins every preset it names.";
            }
        }

        return null;
    }

    /// <summary>
    /// The shape each preset must produce: (directory, header version, L4D2
    /// re-layout, world-light lump version). Measured from the T0 corpus; the
    /// facts below re-assert it so drift is loud.
    /// </summary>
    public static readonly (string Preset, int Version, bool Relayout, int WorldLights)[] Matrix =
    [
        ("l4d2", 21, true, 1),
        ("flag-l4d2", 21, true, 1),
        ("portal2", 21, false, 1),
        ("flag-portal2", 21, false, 1),
        ("asw", 21, false, 1),
        ("csgo", 21, false, 1),
        ("csgoclip", 21, false, 1),
        ("singleplayer", 20, false, 0),
        ("default", 20, false, 0),
        ("bspformat19", 19, false, 0),
    ];

    /// <summary>One corpus map.</summary>
    public static string Map(string preset, string name = "p3f_p3_bump") =>
        Path.Combine(Root!, preset, $"{name}.bsp");

    /// <summary>
    /// Every vbsp++-written map in the corpus: plain <c>*.bsp</c>, no stage
    /// intermediates (see the class remarks for why those cannot qualify).
    /// </summary>
    public static List<string> VbspWrittenMaps() =>
    [
        .. Directory.EnumerateFiles(Root!, "*.bsp", SearchOption.AllDirectories)
            .Where(static f => !Path.GetFileName(f).Contains(".pprad", StringComparison.Ordinal)
                && !Path.GetFileName(f).Contains(".ppvis", StringComparison.Ordinal)),
    ];
}

/// <summary>Skips the whole tools++ oracle tier when the corpus is not mounted.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PpOracleFactAttribute : FactAttribute
{
    /// <summary>Names the skip reason the runner will show.</summary>
    public PpOracleFactAttribute() => Skip = PpOracle.SkipReason();
}

/// <summary>Theory variant of <see cref="PpOracleFactAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PpOracleTheoryAttribute : TheoryAttribute
{
    /// <summary>Names the skip reason the runner will show.</summary>
    public PpOracleTheoryAttribute() => Skip = PpOracle.SkipReason();
}

/// <summary>
/// Container facts over the ++ oracle corpus: version acceptance, re-layout
/// detection firing for exactly the L4D2 presets, and a byte-exact preserve
/// round trip over every vbsp++ output.
/// </summary>
public class BspToolsPlusPlusOracleTests
{
    [PpOracleTheory]
    [InlineData("l4d2", 21, true, 1)]
    [InlineData("flag-l4d2", 21, true, 1)]
    [InlineData("portal2", 21, false, 1)]
    [InlineData("flag-portal2", 21, false, 1)]
    [InlineData("asw", 21, false, 1)]
    [InlineData("csgo", 21, false, 1)]
    [InlineData("csgoclip", 21, false, 1)]
    [InlineData("singleplayer", 20, false, 0)]
    [InlineData("default", 20, false, 0)]
    [InlineData("bspformat19", 19, false, 0)]
    public async Task PresetProducesExactlyThePinnedShape(
        string preset, int version, bool relayout, int worldLights)
    {
        using FileStream file = File.OpenRead(PpOracle.Map(preset));
        BspData bsp = await BspFile.LoadAsync(file);

        Assert.Equal(version, bsp.FileVersion);

        // The negative rows are the whole point: 21 without the re-layout is
        // T0's measured shape for portal2/asw/csgo, so detection must key on
        // the parity check, never on the version alone.
        Assert.Equal(relayout, bsp.SourceLumpsUseL4d2Layout);

        Assert.Equal(worldLights, bsp[BspLump.WorldLights].Version);
        Assert.Equal(worldLights, bsp[BspLump.WorldLightsHdr].Version);
    }

    [PpOracleFact]
    public async Task EveryVbspPlusPlusMapRoundTripsByteExact()
    {
        List<string> failures = [];
        foreach (string path in PpOracle.VbspWrittenMaps())
        {
            byte[] original = await File.ReadAllBytesAsync(path);
            using MemoryStream input = new(original);
            BspData bsp = await BspFile.LoadAsync(input);
            using MemoryStream output = new();
            await BspFile.SaveAsync(bsp, output, BspWriteMode.PreserveSourceLayout);

            if (!output.GetBuffer().AsSpan(0, (int)output.Length)
                    .SequenceEqual(original.AsSpan()))
            {
                failures.Add(Path.GetRelativePath(PpOracle.Root!, path));
            }
        }

        Assert.Empty(string.Join(", ", failures));
    }

    [PpOracleFact]
    public async Task CanonicalWriteWithThePresetFormatPreservesEveryLumpOfARealMap()
    {
        // The container half of "++ maps are writable": a canonical write with
        // the shape the preset's own flags name must read back with every
        // lump's payload and version intact, including through the L4D2
        // re-layout on the l4d2 rows.
        foreach ((string preset, int version, bool relayout, int worldLights) in PpOracle.Matrix)
        {
            using FileStream file = File.OpenRead(PpOracle.Map(preset));
            BspData source = await BspFile.LoadAsync(file);

            BspWriteFormat format = new(
                version, worldLights, StaticPropsFormat: null, L4d2LumpDirLayout: relayout);
            using MemoryStream output = new();
            await BspFile.SaveAsync(source, output, BspWriteMode.Canonical, format);

            using MemoryStream reread = new(output.ToArray());
            BspData saved = await BspFile.LoadAsync(reread);

            Assert.Equal(version, saved.FileVersion);
            Assert.Equal(relayout, saved.SourceLumpsUseL4d2Layout);
            foreach (BspLump lump in Enum.GetValues<BspLump>())
            {
                Assert.True(
                    source[lump].Data.Span.SequenceEqual(saved[lump].Data.Span),
                    $"{preset}: {lump} payload changed");

                // The two world-light slots take the format's stamp (and the
                // rows pin that stamp to what ++ itself wrote, so this proves
                // the flag agrees with the binary); everything else keeps the
                // source's version.
                int expected = lump is BspLump.WorldLights or BspLump.WorldLightsHdr
                    ? worldLights
                    : source[lump].Version;
                Assert.Equal(expected, saved[lump].Version);
            }
        }
    }

    [PpOracleFact]
    public async Task NoVbspPlusPlusMapProducesAnError()
    {
        // These are real artifacts: vbsp++ wrote every byte, and the engine
        // lineage loads them. A validator error against them is a validator
        // bug — over-validation past what the loaders actually demand. The
        // rule set's only honest finding across the corpus is the cubemap
        // warning (BSP0029), faithful to modelloader's default-cubemap
        // fallback and stock's own -requirecubemaps gate; the companion fact
        // pins exactly that.
        List<string> failures = [];
        foreach (string path in PpOracle.VbspWrittenMaps())
        {
            using FileStream file = File.OpenRead(path);
            BspData bsp = await BspFile.LoadAsync(file);
            SourceSharp.MapTools.Validation.ValidationReport report =
                await SourceSharp.MapTools.Validation.BspValidator.CheckAsync(bsp, CancellationToken.None);
            if (report.ErrorCount != 0)
            {
                failures.Add($"{Path.GetFileName(path)}: "
                    + string.Join("; ", report.Diagnostics
                        .Where(d => d.Severity == SourceSharp.MapTools.Diagnostics.DiagnosticSeverity.Error)
                        .Select(d => $"{d.Code} {d.Message}")));
            }
        }

        Assert.Empty(failures);
    }

    [PpOracleFact]
    public async Task TheOnlyFindingAnyOracleMapCarriesIsTheCubemapWarning()
    {
        List<string> surprises = [];
        int clean = 0;
        int cubemapWarned = 0;
        foreach (string path in PpOracle.VbspWrittenMaps())
        {
            using FileStream file = File.OpenRead(path);
            BspData bsp = await BspFile.LoadAsync(file);
            SourceSharp.MapTools.Validation.ValidationReport report =
                await SourceSharp.MapTools.Validation.BspValidator.CheckAsync(bsp, CancellationToken.None);
            if (report.Diagnostics.IsEmpty)
            {
                clean++;
                continue;
            }

            // The p3f probe sources ship no cubemap textures, so their maps
            // legitimately carry no cubemap samples; the sdk_* maps do.
            bool cubemapOnly = report.Diagnostics.All(d =>
                d.Code == SourceSharp.MapTools.Validation.BspRuleCodes.NoCubemaps
                && d.Severity == SourceSharp.MapTools.Diagnostics.DiagnosticSeverity.Warning);
            if (cubemapOnly && !Path.GetFileName(path).StartsWith("sdk_", StringComparison.Ordinal))
            {
                cubemapWarned++;
                continue;
            }

            surprises.Add($"{Path.GetFileName(path)}: "
                + string.Join("; ", report.Diagnostics.Select(d => $"{d.Code} {d.Severity} {d.Message}")));
        }

        Assert.Empty(surprises);
        Assert.Equal(33, clean);
        Assert.Equal(22, cubemapWarned);
    }

    [PpOracleFact]
    public async Task ThePlusPlusDeadEdgeSentinelIsValidates()
    {
        // The -cullall build leaves its edge-0 dummy at (0xffff, 0xffff) — an
        // endpoint pair outside the vertex lump, unreferenced by any surfedge,
        // and invisible to the engine (Mod_LoadEdges copies the array; only
        // edges a surfedge names are dereferenced). Pre-T4 the validator
        // called this BSP0018. This fact is the pin against re-widening the
        // rule: it walks the real probe map, asserts the sentinel is there,
        // and requires zero findings for it.
        using FileStream file = File.OpenRead(PpOracle.Map("probe-tools"));
        BspData bsp = await BspFile.LoadAsync(file);

        System.ReadOnlySpan<SourceSharp.MapFormats.Bsp.Structs.DEdge> edges =
            System.Runtime.InteropServices.MemoryMarshal
                .Cast<byte, SourceSharp.MapFormats.Bsp.Structs.DEdge>(bsp[BspLump.Edges].Data.Span);
        Assert.Equal(0xffff, edges[0].V[0]);
        Assert.Equal(0xffff, edges[0].V[1]);

        SourceSharp.MapTools.Validation.ValidationReport report =
            await SourceSharp.MapTools.Validation.BspValidator.CheckAsync(bsp, CancellationToken.None);

        Assert.True(report.ForCode(SourceSharp.MapTools.Validation.BspRuleCodes.EdgeVertex).IsEmpty);
    }
}
