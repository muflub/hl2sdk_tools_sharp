using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.Tests.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Ambient;

/// <summary>
/// The lane's central gate: recompute leaf ambient from a stock-vrad'd map and
/// require the same bytes the file already holds.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY THIS ORACLE.</b> Leaf ambient runs after <c>FinalLightFace</c>,
/// so a finished map holds both its inputs (lightmaps,
/// world lights, tree, brushes, displacements) and its outputs (lumps 51/52 and
/// 55/56): the comparison is the output itself, bit for bit.
/// <see cref="TheOracleRespondsToTheSampleCountQuirk"/> shows on the live maps
/// that it can fail.
/// </para>
/// <para>
/// <b>WHAT IT COVERS.</b> The <c>ran1</c> stream, the leaf boundary planes, the
/// brush and displacement sample rejection, the 162-direction spherical sample,
/// the BSP surface walk with its displacement clip, the lightmap decode and
/// reflectivity tint, the cone blend, the baked surface lights and their
/// <c>TestLine</c> visibility, the 16-sample eviction, the gamma-space
/// compression, the 8-bit positions, the <c>ColorRGBExp32</c> encode, and the
/// empty-leaf neighbour search.
/// </para>
/// <para>
/// A map that bakes <c>emit_surface</c> lights needs the ray-trace environment
/// for visibility, which needs the installed game content for its static
/// props; without it such a map is REPORTED, not skipped silently.
/// </para>
/// </remarks>
public sealed class LeafAmbientStockParityTests
{
    private sealed record Loaded(string Path, BspData Bsp, AmbientScene Scene, IAmbientLightVisibility? Visibility);

    [StockRadFact]
    public async Task TheLdrAmbientLightingLumpMatchesStockByteForByte()
    {
        List<string> failures = [];
        int compared = await ForEachMap(LightingMode.Ldr, BspLump.LeafAmbientLighting, async (loaded, fail) =>
        {
            LeafAmbientResult result = await Build(loaded, LeafAmbientOptions.StockParity);
            ReadOnlySpan<DLeafAmbientLighting> stock =
                BspStructView.As<DLeafAmbientLighting>(loaded.Bsp[BspLump.LeafAmbientLighting]);
            if (Describe(stock, result.Lighting) is { } difference)
            {
                fail(difference);
            }
        }, failures);

        Assert.True(compared > 0, "no map could be compared");
        Assert.Empty(failures);
    }

    [StockRadFact]
    public async Task TheLdrAmbientIndexLumpMatchesStockEntryForEntry()
    {
        List<string> failures = [];
        int compared = await ForEachMap(LightingMode.Ldr, BspLump.LeafAmbientIndex, async (loaded, fail) =>
        {
            LeafAmbientResult result = await Build(loaded, LeafAmbientOptions.StockParity);
            ReadOnlySpan<DLeafAmbientIndex> stock =
                BspStructView.As<DLeafAmbientIndex>(loaded.Bsp[BspLump.LeafAmbientIndex]);
            if (!MemoryMarshal.AsBytes(stock).SequenceEqual(MemoryMarshal.AsBytes<DLeafAmbientIndex>(result.Index)))
            {
                fail($"{result.Index.Length} index entries differ from stock's {stock.Length}");
            }
        }, failures);

        Assert.True(compared > 0, "no map could be compared");
        Assert.Empty(failures);
    }

    [StockRadHdrFact]
    public async Task TheHdrAmbientLumpsMatchStockByteForByte()
    {
        List<string> failures = [];
        int compared = await ForEachMap(LightingMode.Hdr, BspLump.LeafAmbientLightingHdr, async (loaded, fail) =>
        {
            LeafAmbientResult result = await Build(loaded, LeafAmbientOptions.StockParity);
            ReadOnlySpan<DLeafAmbientLighting> stock =
                BspStructView.As<DLeafAmbientLighting>(loaded.Bsp[BspLump.LeafAmbientLightingHdr]);
            ReadOnlySpan<DLeafAmbientIndex> index =
                BspStructView.As<DLeafAmbientIndex>(loaded.Bsp[BspLump.LeafAmbientIndexHdr]);
            if (Describe(stock, result.Lighting) is { } difference)
            {
                fail(difference);
            }
            else if (!MemoryMarshal.AsBytes(index).SequenceEqual(MemoryMarshal.AsBytes<DLeafAmbientIndex>(result.Index)))
            {
                fail("the HDR index differs");
            }
        }, failures);

        Assert.True(compared > 0, "no map has an HDR pass");
        Assert.Empty(failures);
    }

    [StockRadFact]
    public async Task OneAndManyWorkersProduceTheSameBytes()
    {
        // I4 for this stage: the per-leaf stream, the per-work-item displacement
        // scratch and the index-ordered merge make -threads 1 and -threads N
        // byte-identical.
        List<string> failures = [];
        int compared = await ForEachMap(LightingMode.Ldr, BspLump.LeafAmbientLighting, async (loaded, fail) =>
        {
            LeafAmbientResult serial = await Build(loaded, LeafAmbientOptions.StockParity);
            LeafAmbientResult wide = await Build(loaded, LeafAmbientOptions.StockParity with { Parallelism = 8 });
            if (Describe(serial.Lighting, wide.Lighting) is { } difference)
            {
                fail($"1 vs 8 workers: {difference}");
            }
        }, failures);

        Assert.True(compared > 0, "no map could be compared");
        Assert.Empty(failures);
    }

    [StockRadFact]
    public async Task TheWorldLightAmbientCubeFlagsMatchStock()
    {
        // ComputePerLeafAmbientLighting's first act is to set or clear
        // DWL_FLAGS_INAMBIENTCUBE on every world light,
        // and those flags are written back to the BSP.
        List<string> failures = [];
        int compared = await ForEachMap(LightingMode.Ldr, BspLump.WorldLights, (loaded, fail) =>
        {
            ReadOnlySpan<DWorldLight> stock = loaded.Scene.WorldLights;
            DWorldLight[] recomputed = stock.ToArray();
            _ = LeafAmbientSurfaceLights.Classify(recomputed, stockEstimate: true);
            for (int i = 0; i < stock.Length; i++)
            {
                if ((stock[i].Flags & 1) != (recomputed[i].Flags & 1))
                {
                    fail($"light {i} classified {recomputed[i].Flags & 1} against stock's {stock[i].Flags & 1}");
                    break;
                }
            }

            return Task.CompletedTask;
        }, failures);

        Assert.True(compared > 0, "no map could be compared");
        Assert.Empty(failures);
    }

    /// <summary>The gate's own mutation proof, on the live maps.</summary>
    /// <remarks>
    /// Flipping ONE compliance switch -- the sample-count axes quirk
    /// -- must break the byte
    /// comparison on every map with a leaf whose y or z extent dominates. If this
    /// goes green the parity facts above are measuring nothing.
    /// </remarks>
    [StockRadFact]
    public async Task TheOracleRespondsToTheSampleCountQuirk()
    {
        LeafAmbientOptions flipped = LeafAmbientOptions.StockParity with
        {
            Compliance = ComplianceOptions.Stock.Flipping(StockQuirk.LeafAmbientSampleCountAxes),
        };

        int sensitive = 0;
        List<string> failures = [];
        int compared = await ForEachMap(LightingMode.Ldr, BspLump.LeafAmbientLighting, async (loaded, _) =>
        {
            LeafAmbientResult result = await Build(loaded, flipped);
            ReadOnlySpan<DLeafAmbientLighting> stock =
                BspStructView.As<DLeafAmbientLighting>(loaded.Bsp[BspLump.LeafAmbientLighting]);
            if (Describe(stock, result.Lighting) is not null)
            {
                sensitive++;
            }
        }, failures);

        Assert.True(compared > 0, "no map could be compared");
        Assert.True(sensitive > 0, $"flipping the quirk changed none of {compared} maps");
    }

    private static Task<LeafAmbientResult> Build(Loaded loaded, LeafAmbientOptions options) =>
        LeafAmbientBuilder.BuildAsync(
            loaded.Scene, loaded.Scene.WorldLights.ToArray(), options, loaded.Visibility, CancellationToken.None);

    /// <summary>
    /// Runs a check on every map in the catalogue directory that has the lump,
    /// one map in memory at a time.
    /// </summary>
    private static async Task<int> ForEachMap(
        LightingMode mode, BspLump needs, Func<Loaded, Action<string>, Task> check, List<string> failures)
    {
        IReadOnlyList<string> maps = StockRadCatalogue.Maps();
        Assert.True(
            maps.Count > 0,
            $"{StockRadCatalogue.DirectoryVariable} names '{StockRadCatalogue.Directory}', which holds no .bsp.");

        int compared = 0;
        foreach (string path in maps)
        {
            string name = System.IO.Path.GetFileName(path);
            BspData bsp;
            await using (FileStream stream = File.OpenRead(path))
            {
                bsp = await BspFile.LoadAsync(stream);
            }

            BspLump lighting = mode == LightingMode.Hdr ? BspLump.LightingHdr : BspLump.Lighting;
            if (bsp[needs].IsEmpty || bsp[lighting].IsEmpty)
            {
                continue;
            }

            AmbientScene scene = AmbientScene.Create(bsp, mode);
            DWorldLight[] classified = scene.WorldLights.ToArray();
            (int flagged, _) = LeafAmbientSurfaceLights.Classify(classified, stockEstimate: true);

            IAmbientLightVisibility? visibility = null;
            ContentFileSystem? content = null;
            if (flagged > 0)
            {
                if (InstalledGameContent.SkipReason is { } why)
                {
                    failures.Add($"{name}: bakes {flagged} surface lights and needs game content for their visibility ({why})");
                    continue;
                }

                (visibility, content) = await LoadVisibility(path, bsp);
            }

            try
            {
                compared++;
                await check(new Loaded(path, bsp, scene, visibility), message => failures.Add($"{name}: {message}"));
            }
            finally
            {
                if (content is not null)
                {
                    await content.DisposeAsync();
                }
            }
        }

        return compared;
    }

    /// <summary>
    /// <c>g_RtEnv</c> for <c>TestLine</c>: 4b's shadow casters in a KD-tree.
    /// </summary>
    /// <remarks>
    /// The props are read from the content stock vrad was given, which the
    /// map's <c>&lt;n&gt;.gameinfo</c> sidecar names (<see cref="StockProvenance"/>);
    /// a map without one fails rather than borrowing some other game's models.
    /// </remarks>
    private static async Task<(IAmbientLightVisibility, ContentFileSystem)> LoadVisibility(string path, BspData bsp)
    {
        string file = System.IO.Path.GetFileName(path);
        string name = file.EndsWith(StockRadCatalogue.StockRadSuffix, StringComparison.Ordinal)
            ? file[..^StockRadCatalogue.StockRadSuffix.Length]
            : System.IO.Path.GetFileNameWithoutExtension(file);
        GameContentMounter.Result mounted = await StockProvenance.MountHostAsync(
            System.IO.Path.GetDirectoryName(path)!, name);

        ShadowCasterLoadReport casters = await ShadowCasterLoader.LoadAsync(
            bsp, VradOptions.Default, mounted.Content, NullPropCollisionSource.Instance);
        return (new TracerLineVisibility(casters.Set.BuildTracer(), ComplianceOptions.Stock), mounted.Content);
    }

    /// <summary>The first difference between two sample runs, or null.</summary>
    private static string? Describe(ReadOnlySpan<DLeafAmbientLighting> stock, ReadOnlySpan<DLeafAmbientLighting> ours)
    {
        if (stock.Length != ours.Length)
        {
            return $"{ours.Length} samples against {stock.Length}";
        }

        ReadOnlySpan<byte> a = MemoryMarshal.AsBytes(stock);
        ReadOnlySpan<byte> b = MemoryMarshal.AsBytes(ours);
        if (a.SequenceEqual(b))
        {
            return null;
        }

        int differing = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                differing++;
            }
        }

        return $"{differing} of {a.Length} bytes differ over {stock.Length} samples";
    }
}
