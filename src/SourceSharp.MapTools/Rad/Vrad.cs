//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Final;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rad;

/// <summary>
/// vrad: lights a map that vbsp and vvis produced.
/// </summary>
/// <remarks>
/// <para>
/// The stage order is stock's <c>RunVRAD</c>:
/// <c>VRAD_LoadBSP</c> (texlight files, shadow casters, the KD-tree,
/// <c>RadWorld_Start</c>), <c>RadWorld_Go</c> (<c>BuildFacelights</c>,
/// <c>PrecompLightmapOffsets</c>, the bounce, <c>FinalLightFace</c>),
/// <c>VRAD_ComputeOtherLighting</c> (detail props, leaf ambient, static
/// props), then the write.
/// </para>
/// <para>
/// <b><c>-both</c> is one compile, not two.</b> Stock's launcher runs the
/// whole DLL twice: LDR, write, then HDR on
/// the map the LDR pass wrote. This runs the same two passes in that order on
/// the same in-memory map, so the HDR pass sees exactly what stock's second
/// run loads -- but the texlight files are read once and the shadow casters
/// and KD-tree are built once and shared (plan 4p), since neither depends on
/// the range. The patches are rebuilt per pass because their emitted light
/// does (texlights have separate <c>ldr:</c>/<c>hdr:</c> values).
/// </para>
/// </remarks>
public static class Vrad
{
    /// <summary>Stage name: loading texlight files, casters and the tracer.</summary>
    public const string LoadStage = "vrad.Load";

    /// <summary>Stage name: <c>RadWorld_Start</c>.</summary>
    public const string StartStage = "vrad.RadWorld_Start";

    /// <summary>Stage name: <c>BuildFacelights</c>.</summary>
    public const string FacelightsStage = "vrad.BuildFacelights";

    /// <summary>Stage name: the bounce.</summary>
    public const string BounceStage = "vrad.BounceLight";

    /// <summary>Stage name: <c>FinalLightFace</c>.</summary>
    public const string FinalStage = "vrad.FinalLightFace";

    /// <summary>Stage name: <c>VRAD_ComputeOtherLighting</c>.</summary>
    public const string OtherStage = "vrad.ComputeOtherLighting";

    /// <summary>Lights a map and writes the result into it.</summary>
    /// <param name="bsp">
    /// The map, as vvis left it. Changed in place: FACES / FACES_HDR (styles,
    /// <c>lightofs</c>), LIGHTING / LIGHTING_HDR, WORLDLIGHTS / WORLDLIGHTS_HDR,
    /// VERTNORMALS, VERTNORMALINDICES, the SKY bits of LEAFS, MAP_FLAGS, and
    /// under <c>-luxeldensity</c> TEXINFO and the face extents -- plus whatever
    /// the other-lighting stages write.
    /// </param>
    /// <param name="context">What to read and how.</param>
    /// <param name="cancellationToken">Cancels the compile.</param>
    /// <returns>The counts and diagnostics.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="MapCompileException">An unrecoverable map error, as stock's <c>Error()</c>.</exception>
    /// <exception cref="OperationCanceledException">The compile was cancelled.</exception>
    public static Task<RadResult> LightAsync(
        BspData bsp,
        VradContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        return RunAsync(bsp, context, cancellationToken);
    }

    /// <summary>
    /// The half of <see cref="LightAsync(BspData, VradContext, CancellationToken)"/>
    /// that needs only vbsp's map, not vvis's: the texlight files and, when the
    /// context brings no tracer, the shadow casters and the KD-tree.
    /// </summary>
    /// <param name="bsp">
    /// The map. Read only, unless <c>-luxeldensity</c> is below one: then the
    /// first pass's density is applied here, as the whole compile would.
    /// </param>
    /// <param name="context">The same context the lighting will run with.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>What <see cref="LightAsync(BspData, VradPreparation, VradContext, CancellationToken)"/> continues from.</returns>
    /// <remarks>
    /// Nothing here reads LUMP_VISIBILITY or the leaf flags vvis writes (the
    /// casters read only each leaf's brush range), so a chain can run it while
    /// vvis finishes, as long as vvis's lumps are committed after it returns.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="OperationCanceledException">The load was cancelled.</exception>
    public static Task<VradPreparation> PrepareAsync(
        BspData bsp,
        VradContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        return PrepareCoreAsync(bsp, context, cancellationToken);
    }

    /// <summary>
    /// Lights a map from a <see cref="PrepareAsync"/>: exactly what
    /// <see cref="LightAsync(BspData, VradContext, CancellationToken)"/> gives
    /// for the same map and context.
    /// </summary>
    /// <param name="bsp">The map, as vvis left it; changed in place as that overload says.</param>
    /// <param name="prepared">The preparation, made from this map and context; used once.</param>
    /// <param name="context">The context the preparation was made with.</param>
    /// <param name="cancellationToken">Cancels the compile.</param>
    /// <returns>The counts and diagnostics, the preparation's first.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="MapCompileException">An unrecoverable map error, as stock's <c>Error()</c>.</exception>
    /// <exception cref="OperationCanceledException">The compile was cancelled.</exception>
    public static Task<RadResult> LightAsync(
        BspData bsp,
        VradPreparation prepared,
        VradContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        return LightCoreAsync(bsp, prepared, context, cancellationToken);
    }

    /// <summary>The ranges a compile lights, in stock's order.</summary>
    /// <param name="range">The option.</param>
    /// <returns>False for LDR, true for HDR; LDR first under <c>-both</c>.</returns>
    public static IReadOnlyList<bool> Passes(VradLightingRange range) => range switch
    {
        VradLightingRange.Hdr => [true],
        VradLightingRange.Both => [false, true],
        _ => [false],
    };

    private static async Task<RadResult> RunAsync(BspData bsp, VradContext context, CancellationToken cancellationToken)
    {
        VradPreparation prepared = await PrepareCoreAsync(bsp, context, cancellationToken).ConfigureAwait(false);
        return await LightCoreAsync(bsp, prepared, context, cancellationToken).ConfigureAwait(false);
    }

    // VRAD_LoadBSP up to RadWorld_Start, for the first pass: the texlight
    // files, the first pass's density and texlights, and the tracer.
    private static async Task<VradPreparation> PrepareCoreAsync(
        BspData bsp,
        VradContext context,
        CancellationToken cancellationToken)
    {
        VradOptions options = context.Options;
        List<CompileDiagnostic> diagnostics = [];
        IContentFileSystem content = context.Content ?? new ContentFileSystem([]);

        void Warn(string code, string message) =>
            diagnostics.Add(new CompileDiagnostic(code, DiagnosticSeverity.Warning, message));

        // VRAD_LoadBSP's texlight half: read once, parsed per range.
        Report(context, LoadStage, 0);
        List<(string Name, byte[] Bytes)> radFiles = await LoadTexlightFilesAsync(
            content, options, context.MapName, Warn, cancellationToken).ConfigureAwait(false);

        bool hdr = Passes(options.Range)[0];
        (RadLightFile texFile, IRayTracer? tracer) = await BeginPassAsync(
            bsp, context, content, radFiles, hdr, context.Tracer, Warn, cancellationToken).ConfigureAwait(false);

        return new VradPreparation(radFiles, texFile, tracer!, diagnostics);
    }

    private static async Task<RadResult> LightCoreAsync(
        BspData bsp,
        VradPreparation prepared,
        VradContext context,
        CancellationToken cancellationToken)
    {
        if (!prepared.TryTake())
        {
            throw new InvalidOperationException("a VradPreparation lights one map once");
        }

        List<CompileDiagnostic> diagnostics = [.. prepared.Diagnostics];
        List<string> notYet = [];
        List<RadPassResult> passes = [];
        IContentFileSystem content = context.Content ?? new ContentFileSystem([]);

        void Warn(string code, string message) =>
            diagnostics.Add(new CompileDiagnostic(code, DiagnosticSeverity.Warning, message));

        void NotYet(string stage, string why)
        {
            if (!notYet.Contains(stage))
            {
                notYet.Add(stage);
                Warn(VradCodes.StageNotYetPorted, $"{stage} is not ported yet: {why}");
            }
        }

        IRayTracer tracer = prepared.Tracer;

        // -both (plan 4p): the transfers are geometry; the second range reuses
        // the first's when its patch tree is the same (RadWorld.BounceAsync checks).
        Light.SharedTransfers? transfers = null;
        IReadOnlyList<bool> ranges = Passes(context.Options.Range);
        for (int p = 0; p < ranges.Count; p++)
        {
            bool hdr = ranges[p];
            cancellationToken.ThrowIfCancellationRequested();

            RadLightFile texFile = prepared.FirstTexlights;
            if (p > 0)
            {
                (texFile, _) = await BeginPassAsync(
                    bsp, context, content, prepared.RadFiles, hdr, tracer, Warn, cancellationToken).ConfigureAwait(false);
            }

            (RadPassResult pass, transfers) = await RunPassAsync(
                bsp, context, content, tracer, texFile, hdr, transfers, Warn, NotYet, cancellationToken).ConfigureAwait(false);
            passes.Add(pass);
        }

        return new RadResult(passes, diagnostics, notYet);
    }

    // A pass's head: RadWorld_Start's -luxeldensity edit, the range's
    // texlights, and (once for the whole compile) the casters and the KD-tree.
    private static async Task<(RadLightFile TexFile, IRayTracer Tracer)> BeginPassAsync(
        BspData bsp,
        VradContext context,
        IContentFileSystem content,
        List<(string Name, byte[] Bytes)> radFiles,
        bool hdr,
        IRayTracer? tracer,
        Action<string, string> warn,
        CancellationToken cancellationToken)
    {
        VradOptions options = context.Options;

        // RadWorld_Start's head: -luxeldensity edits the map.
        float density = LuxelDensity.Effective(options.LuxelDensity);
        if (density < 1.0f)
        {
            LuxelDensity.Apply(bsp, density, hdr, options.Compliance);
        }

        (RadLightFile texFile, IReadOnlyList<RadLightOverride> overrides) =
            await ParseTexlightsAsync(radFiles, hdr, options.LightScale, cancellationToken).ConfigureAwait(false);
        foreach (RadLightOverride o in overrides)
        {
            warn(VradCodes.TexlightOverride, o.SameFile
                ? $"Duplication of '{o.Name}'"
                : o.Redundant ? $"Redundant '{o.Name}' def" : $"Overriding '{o.Name}'");
        }

        // Once for the whole compile: the casters and the KD-tree.
        tracer ??= await BuildTracerAsync(bsp, options, content, context, texFile, warn, cancellationToken)
            .ConfigureAwait(false);

        Report(context, LoadStage, 1);
        return (texFile, tracer);
    }

    private static async Task<(RadPassResult Pass, Light.SharedTransfers? Transfers)> RunPassAsync(
        BspData bsp,
        VradContext context,
        IContentFileSystem content,
        IRayTracer tracer,
        RadLightFile texFile,
        bool hdr,
        Light.SharedTransfers? reuseTransfers,
        Action<string, string> warn,
        Action<string, string> notYet,
        CancellationToken cancellationToken)
    {
        VradOptions options = context.Options;
        CompileParallelism parallelism = context.Parallelism;
        string range = hdr ? "HDR" : "LDR";

        // The density was applied to the map above; the settings see a plain map.
        DirectLightingSettings settings = DirectLightingSettings.FromVrad(options with { LuxelDensity = 1.0f }, hdr);

        Report(context, StartStage, 0);
        RadWorld world = await RadWorld.StartAsync(
            bsp, settings, new TextureLightTable(texFile, context.MapName), tracer, parallelism, cancellationToken)
            .ConfigureAwait(false);
        Report(context, StartStage, 1);
        world.ReuseTransfers = reuseTransfers;

        RadPass pass = new(
            world, tracer, options, content, parallelism, context.MapName,
            context.PropCollision ?? NullPropCollisionSource.Instance);
        FinalLightingStatistics? final = null;

        // RunVRAD: RadWorld_Go only when neither -onlydetail nor -OnlyStaticProps.
        bool go = !options.OnlyDetail && !options.OnlyStaticProps;
        if (go)
        {
            // RadWorld_Go: InitMacroTexture first.
            List<string> macroWarnings = [];
            MacroTextures macro = await MacroTextures.LoadAsync(
                bsp, world.Geometry, world.Entities, context.MapName, content, macroWarnings, cancellationToken)
                .ConfigureAwait(false);
            foreach (string w in macroWarnings)
            {
                warn(VradCodes.StageWarning, w);
            }

            Report(context, FacelightsStage, 0);
            await world.LightFacesAsync(tracer, parallelism, cancellationToken).ConfigureAwait(false);
            Report(context, FacelightsStage, 1);

            // World.Settings, not settings: a map with no vis
            // has had its bounces forced to zero.
            if (world.Settings.Bounces > 0)
            {
                if (context.Stages.Bounce is { } bounce)
                {
                    Report(context, BounceStage, 0);
                    await bounce.BounceAsync(pass, cancellationToken).ConfigureAwait(false);
                    Report(context, BounceStage, 1);
                }
                else
                {
                    notYet("bounce lighting (lane 4d)",
                        $"{range} lightmaps are direct light only; run with -bounce 0 to ask for exactly that");
                }
            }

            // The displacement sample and patch hashes,
            // after the bounce (the patch hash reads TotalLight).
            Displacement.DispRadialContext? dispRadials = null;
            if (world.Statistics.Displacements > 0)
            {
                _ = await world.BuildDisplacementHashAsync(parallelism, cancellationToken).ConfigureAwait(false);
                dispRadials = world.DisplacementRadialContext();
            }

            Report(context, FinalStage, 0);
            FinalLightContext finalContext = new(world, macro)
            {
                RedErrors = options.ShowErrorsInRed,
                Displacements = dispRadials,
            };
            FinalLightingResult result = await FinalLighting.RunAsync(finalContext, parallelism, cancellationToken)
                .ConfigureAwait(false);
            Report(context, FinalStage, 1);

            pass.LightData = result.LightData;
            final = result.Statistics;

            if (final.OffGridSamples > 0)
            {
                warn(VradCodes.SampleRadialPunting,
                    $"SampleRadial: Punting, Waiting for fix ({final.OffGridSamples} luxels)");
            }

            if (final.MissingStyleSlots > 0)
            {
                warn(VradCodes.MissingStyleSlot,
                    $"{final.MissingStyleSlots} reads of a light style slot with no light "
                    + "(stock dereferences a null pointer there; read as black)");
            }

            RadLumpWriter.Write(bsp, world, result.LightData);
        }
        else
        {
            // Without RadWorld_Go the faces, lighting and worldlights stay as
            // loaded; RadWorld_Start still saved the vertex normals and the
            // sky flags.
            (MapFormats.Geometry.Vec3[] normals, ushort[] indices) =
                VradVertexNormals.Save(world.Geometry, world.Neighbours);
            (byte[] n, byte[] x) = VradVertexNormals.ToLumps(normals, indices);
            bsp.SetLump(BspLump.VertNormals, n, bsp[BspLump.VertNormals].Version);
            bsp.SetLump(BspLump.VertNormalIndices, x, bsp[BspLump.VertNormalIndices].Version);
            RadLumpWriter.WriteLeafFlags(bsp, world);
        }

        foreach (string w in world.Warnings)
        {
            warn(VradCodes.StageWarning, w);
        }

        // VRAD_ComputeOtherLighting, in stock's order.
        Report(context, OtherStage, 0);
        if (!options.NoDetailLighting)
        {
            await RunOtherAsync(context.Stages.DetailPropLighting, "detail prop lighting (lane 4g)", pass, bsp, notYet, cancellationToken)
                .ConfigureAwait(false);
        }

        await RunOtherAsync(context.Stages.LeafAmbientLighting, "leaf ambient lighting (lane 4g)", pass, bsp, notYet, cancellationToken)
            .ConfigureAwait(false);

        if (!options.Fast && options.StaticPropLighting)
        {
            await RunOtherAsync(context.Stages.StaticPropLighting, "static prop lighting (lane 4g)", pass, bsp, notYet, cancellationToken)
                .ConfigureAwait(false);
        }

        Report(context, OtherStage, 1);

        foreach (string w in pass.Warnings)
        {
            warn(VradCodes.StageWarning, w);
        }

        // VRAD_LoadBSP sets g_LevelFlags; WriteBSPFile writes it.
        RadLumpWriter.WriteLevelFlags(bsp, hdr, options.StaticPropLighting);

        return (new RadPassResult(hdr, world.Statistics, final, pass.LightData.Length), world.ShareTransfers() ?? reuseTransfers);
    }

    private static async Task RunOtherAsync(
        IVradOtherLightingStage? stage,
        string name,
        RadPass pass,
        BspData bsp,
        Action<string, string> notYet,
        CancellationToken cancellationToken)
    {
        if (stage is null)
        {
            notYet(name, "the lumps it writes keep the values the input map had");
            return;
        }

        try
        {
            await stage.ComputeAsync(pass, bsp, cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException exception)
        {
            // A stage that cannot run on this compile (4g's samplers need the
            // CPU KD tracer) is reported like a missing one, never skipped
            // silently.
            notYet(name, exception.Message);
        }
    }

    /// <summary>
    /// <c>lights.rad</c>, the <c>-lights</c> file, then <c>&lt;map&gt;.rad</c>
    /// As raw bytes.
    /// </summary>
    private static async Task<List<(string Name, byte[] Bytes)>> LoadTexlightFilesAsync(
        IContentFileSystem content,
        VradOptions options,
        string mapName,
        Action<string, string> warn,
        CancellationToken cancellationToken)
    {
        List<(string Name, bool Required)> names = [("lights.rad", true)];
        if (!string.IsNullOrEmpty(options.LightsFile))
        {
            names.Add((options.LightsFile, true));
        }

        if (mapName.Length > 0)
        {
            // Optional and implied: only read when it exists, silently otherwise.
            names.Add((mapName + ".rad", false));
        }

        List<(string, byte[])> files = [];
        foreach ((string name, bool required) in names)
        {
            byte[]? bytes = null;
            if (VPath.TryCreate(name, out VPath path))
            {
                using IMemoryOwner<byte>? owner = await content.ReadAsync(path, cancellationToken).ConfigureAwait(false);
                bytes = owner?.Memory.ToArray();
            }

            if (bytes is null)
            {
                if (required)
                {
                    warn(VradCodes.TexlightFileMissing, $"Warning: Couldn't open texlight file {name}.");
                }

                continue;
            }

            files.Add((name, bytes));
        }

        return files;
    }

    private static async Task<(RadLightFile File, IReadOnlyList<RadLightOverride> Overrides)> ParseTexlightsAsync(
        List<(string Name, byte[] Bytes)> files,
        bool hdr,
        float lightScale,
        CancellationToken cancellationToken)
    {
        RadLightFile merged = new();
        List<RadLightOverride> overrides = [];
        foreach ((string name, byte[] bytes) in files)
        {
            RadLightFile file = await RadLightFile.ParseAsync(
                bytes, new RadLightOptions(Hdr: hdr, LightScale: lightScale, SourceFile: name), cancellationToken)
                .ConfigureAwait(false);
            overrides.AddRange(merged.Merge(file));
        }

        return (merged, overrides);
    }

    private static async Task<IRayTracer> BuildTracerAsync(
        BspData bsp,
        VradOptions options,
        IContentFileSystem content,
        VradContext context,
        RadLightFile texFile,
        Action<string, string> warn,
        CancellationToken cancellationToken)
    {
        ShadowCasterLoadReport casters = await ShadowCasterLoader.LoadAsync(
            bsp,
            options,
            content,
            context.PropCollision ?? NullPropCollisionSource.Instance,
            [.. texFile.NonShadowCastingMaterials],
            transparency: null,
            cancellationToken).ConfigureAwait(false);

        // g_RtEnv.SetupAccelerationStructure: on a worker, never
        // on the caller's thread.
        if (casters.Set.Count == 0)
        {
            return new EmptySceneTracer();
        }

        // The tree's subtrees are built on the queue's workers
        // (KdTreeBuilder.BuildAsync), node for node the serial tree.
        // The KD tree is built even under -gpu: the prop and leaf-ambient
        // samplers call KdRayTracer.TestLines, which the batch seam does not
        // have, and the hybrid hands them this tree (HybridRayTracer.CpuTracer).
        using WorkQueue queue = new(context.Parallelism);
        KdRayTracer cpu = await casters.Set.BuildTracerAsync(options.Compliance, queue, cancellationToken)
            .ConfigureAwait(false);

        // The -gpu seam (plan 10c): the host's factory, asked with the casters
        // it needs. Every decline — no device, no package, a pin matching
        // nothing, a failed capability self-test — is one warning and the CPU
        // run; §10c's rule is that the GPU path must earn its way in, and a
        // driver that cannot prove itself is not a reason to crash.
        if (context.GpuTracerFactory is { } factory)
        {
            GpuTracerOffer offer = await factory.TryCreateAsync(casters.Set, cancellationToken).ConfigureAwait(false);
            if (offer.Tracer is { } gpu)
            {
                return new HybridRayTracer(gpu, cpu);
            }

            warn(
                VradCodes.GpuTracerDeclined,
                $"gpu tracer declined: {offer.DeclineReason ?? "the factory offered nothing"} — "
                + "CPU KD tracer for this run");
        }

        return cpu;
    }

    private static void Report(VradContext context, string stage, long done) =>
        context.Progress?.Report(new CompileProgress(stage, done, 1));
}
