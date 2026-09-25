using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.Tests.MapTools.Rad.Light;

/// <summary>
/// Builds the managed <see cref="RadWorld"/> for a stock-compiled map, the way
/// stock vrad would see it: stock vbsp + vvis output in, the map's own
/// <c>.rad</c> as the only texlight file (the test game dir has no
/// <c>lights.rad</c>, and stock's log says so), <see cref="ComplianceOptions.Stock"/>.
/// </summary>
internal static class StockRadWorld
{
    internal static async Task<BspData> LoadBspAsync(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        return await BspFile.LoadAsync(stream);
    }

    internal static async Task<TextureLightTable> TexLightsAsync(string name, bool hdr)
    {
        string? rad = StockVradReference.RadFileFor(name);
        if (rad is null)
        {
            return new TextureLightTable(new RadLightFile(), name);
        }

        RadLightFile file = await RadLightFile.ParseAsync(
            await File.ReadAllTextAsync(rad), new RadLightOptions(Hdr: hdr, SourceFile: rad));
        return new TextureLightTable(file, name);
    }

    internal static IRayTracer Tracer(BspData bsp, bool hdr)
    {
        ShadowCasterBuilder builder = new() { Compliance = ComplianceOptions.Stock };
        List<BspEntity> entities = EntityLump.Parse(bsp[BspLump.Entities]);
        BrushShadowCasters.AddBrushEntities(bsp, entities, builder, ComplianceOptions.Stock);
        bool useHdrFaces = hdr && !bsp[BspLump.FacesHdr].IsEmpty;
        BrushShadowCasters.AddWorld(bsp, useHdrFaces, builder, ComplianceOptions.Stock);
        DisplacementShadowCasters.Add(bsp, builder);
        return builder.Build().BuildTracer();
    }

    /// <summary>
    /// Stock compliance, optionally with ONE quirk flipped back to correct by
    /// naming it in <c>P4C_FLIP_QUIRK</c> -- the measurement of what a quirk's
    /// correct behaviour changes against stock (plan ruling Q17). Unset in
    /// every gate run.
    /// </summary>
    internal static ComplianceOptions Compliance()
    {
        string? flip = Environment.GetEnvironmentVariable("P4C_FLIP_QUIRK");
        return flip is { Length: > 0 }
            ? ComplianceOptions.Stock.Flipping(Enum.Parse<StockQuirk>(flip))
            : ComplianceOptions.Stock;
    }

    internal static DirectLightingSettings Settings(bool hdr, VradOptions? options = null) =>
        DirectLightingSettings.FromVrad(
            (options ?? VradOptions.Default) with { Compliance = Compliance() }, hdr);

    internal static async Task<RadWorld> StartAsync(
        string name,
        bool hdr,
        VradOptions? options = null,
        CompileParallelism? parallelism = null)
    {
        string input = StockVradReference.InputFor(name)
            ?? throw new InvalidOperationException($"no stock input for {name}");
        BspData bsp = await LoadBspAsync(input);
        IRayTracer tracer = Tracer(bsp, hdr);
        return await RadWorld.StartAsync(
            bsp, Settings(hdr, options), await TexLightsAsync(name, hdr), tracer,
            parallelism ?? CompileParallelism.Default, CancellationToken.None);
    }

    internal static async Task<RadWorld> LightAsync(
        string name,
        bool hdr,
        VradOptions? options = null,
        CompileParallelism? parallelism = null)
    {
        string input = StockVradReference.InputFor(name)
            ?? throw new InvalidOperationException($"no stock input for {name}");
        BspData bsp = await LoadBspAsync(input);
        IRayTracer tracer = Tracer(bsp, hdr);
        CompileParallelism p = parallelism ?? CompileParallelism.Default;
        RadWorld world = await RadWorld.StartAsync(
            bsp, Settings(hdr, options), await TexLightsAsync(name, hdr), tracer, p, CancellationToken.None);
        await world.LightFacesAsync(tracer, p, CancellationToken.None);
        return world;
    }

    internal static byte[] Lump(BspData bsp, BspLump lump) => bsp[lump].Data.ToArray();
}
