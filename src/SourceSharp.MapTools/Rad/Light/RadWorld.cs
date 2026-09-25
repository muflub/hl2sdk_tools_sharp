using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>Counts and timings from a <see cref="RadWorld"/> run.</summary>
public sealed class RadWorldStatistics
{
    /// <summary><c>numfaces</c>, the "N faces" line.</summary>
    public int Faces { get; internal set; }

    /// <summary>The patch report: before and after subdivision.</summary>
    public SubdivisionReport Subdivision { get; internal set; }

    /// <summary><c>numdlights</c>, the "N direct lights" line: every light ALLOCATED, orphans included.</summary>
    public int DirectLights { get; internal set; }

    /// <summary>Faces left unlit because they are displacements and no displacement manager was loaded.</summary>
    public int DeferredDisplacementFaces { get; internal set; }

    /// <summary><c>m_DispTrees.Size</c>: the "N Displacements" line.</summary>
    public int Displacements { get; internal set; }

    /// <summary>The summed displacement patch area: the "[N Square Inches]" of the displacement line.</summary>
    public float DisplacementArea { get; internal set; }

    /// <summary>Faces that got a facelight.</summary>
    public int LitFaces { get; internal set; }

    /// <summary>Light samples over all lit faces.</summary>
    public long Samples { get; internal set; }

    /// <summary>Visibility rays traced by the face lighting, supersampling included.</summary>
    public long VisibilityRays { get; internal set; }

    /// <summary>Sky (closest-hit) rays traced by the face lighting.</summary>
    public long SkyRays { get; internal set; }

    /// <summary>How many trace batches the face lighting issued.</summary>
    public int Batches { get; internal set; }
}

/// <summary>
/// The patch and direct-lighting model of one vrad pass: stock's
/// <c>RadWorld_Start</c> and the direct half of
/// <c>RadWorld_Go</c>, as one explicit context.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the hand-off object for lanes 4d, 4e, 4f and 4g.</b> Stock keeps
/// all of it in globals (<c>g_Patches</c>, <c>facelight[]</c>,
/// <c>activelights</c>, <c>faceneighbor[]</c>, <c>face_offset[]</c>...); here it
/// is one object per pass:
/// </para>
/// <list type="bullet">
/// <item><see cref="Patches"/> -- every patch with its tree links
/// (<c>Parent</c>/<c>Child1</c>/<c>Child2</c>), per-face and per-cluster lists,
/// and after <see cref="LightFacesAsync"/> its <c>DirectLight</c>,
/// <c>TotalLight</c>, <c>SampleLight</c> and <c>SampleArea</c>. Bounce (4d)
/// starts here.</item>
/// <item><see cref="FaceLights"/> -- per face, the samples, luxels and per-style,
/// per-bump light that <c>FinalLightFace</c> (4f) filters and encodes.</item>
/// <item><see cref="Layout"/> -- the faces' styles and <c>lightofs</c> and the
/// LIGHTING lump's size (<c>PrecompLightmapOffsets</c>).</item>
/// <item><see cref="Lights"/> and <see cref="WorldLights"/> -- the direct lights
/// and the <c>LUMP_WORLDLIGHTS[_HDR]</c> records.</item>
/// <item><see cref="Gatherer"/> -- <c>GatherSampleLight</c> for any points, which
/// displacement (4e) and prop/detail lighting (4g) call too.</item>
/// </list>
/// <para>
/// Displacement faces are lit here too (lane 4e): <see cref="Displacements"/>
/// gives them their patches, samples and luxels, and
/// <see cref="BuildDisplacementHashAsync"/> the hashes their radial filter reads.
/// </para>
/// </remarks>
public sealed partial class RadWorld
{
    /// <summary>
    /// The tracer is called in slabs of this many rays, one work item each, so
    /// the CPU tracer runs on every worker. A multiple of 64 so each slab owns
    /// whole words of hit bits, and of 4 so packets never straddle slabs.
    /// </summary>
    public const int RaysPerTraceSlab = 16 * 1024;


    private RadWorld(
        BspData bsp,
        DirectLightingSettings settings,
        LightGeometry geometry,
        List<BspEntity> entities)
    {
        Bsp = bsp;
        Settings = settings;
        Geometry = geometry;
        Entities = entities;
        Tree = new CompiledBspTree(geometry);
        Visibility = LightVisibility.Load(bsp, geometry.Leaves);
    }

    /// <summary>The map.</summary>
    public BspData Bsp { get; }

    /// <summary>The switches, with <see cref="DirectLightingSettings.Bounces"/> forced to 0 on a map with no vis.</summary>
    public DirectLightingSettings Settings { get; private set; }

    /// <summary>The map's faces, planes and tree.</summary>
    public LightGeometry Geometry { get; }

    /// <summary>The entities, in file order.</summary>
    public IReadOnlyList<BspEntity> Entities { get; }

    /// <summary>The BSP, for point lookups.</summary>
    public CompiledBspTree Tree { get; }

    /// <summary>The PVS.</summary>
    public LightVisibility Visibility { get; }

    /// <summary><c>faceneighbor[]</c>, from <c>PairEdges</c>.</summary>
    public FaceNeighbours Neighbours { get; private set; } = null!;

    /// <summary>The patches.</summary>
    public PatchSet Patches { get; private set; } = null!;

    /// <summary>The direct lights.</summary>
    public DirectLightSet Lights { get; private set; } = null!;

    /// <summary>The sky-leaf flags, when a light_environment built them.</summary>
    public SkyLeafVisibility SkyLeaves { get; private set; } = null!;

    /// <summary>
    /// The displacements (lane 4e): <c>StaticDispMgr()</c>, loaded before the
    /// Patches.
    /// </summary>
    public Displacement.VradDisplacements Displacements { get; private set; } = null!;

    /// <summary>
    /// The displacement sample and patch hashes, after
    /// <see cref="BuildDisplacementHashAsync"/>; the displacement radial
    /// (<see cref="Displacement.DispRadial"/>) reads them in <c>FinalLightFace</c>.
    /// </summary>
    public Displacement.DispSampleHash? DisplacementHash { get; private set; }

    /// <summary>The 3D skyboxes.</summary>
    public SkyCameras SkyCameras { get; private set; } = SkyCameras.None;

    /// <summary>The gatherer the face lighting used; valid after <see cref="StartAsync"/>.</summary>
    public DirectLightGatherer Gatherer { get; private set; } = null!;

    /// <summary>The <c>LUMP_WORLDLIGHTS</c> (or <c>_HDR</c>) records.</summary>
    public DWorldLight[] WorldLights { get; private set; } = [];

    /// <summary>Per face, its facelight; null for a face stock never lights.</summary>
    public FaceLight?[] FaceLights { get; private set; } = [];

    /// <summary>The lightmap layout, after <see cref="LightFacesAsync"/>.</summary>
    public LightmapLayout? Layout { get; private set; }

    /// <summary>Warnings from every stage, in stage order.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Counts.</summary>
    public RadWorldStatistics Statistics { get; } = new();

    /// <summary>The worldlights as the bytes of the lump.</summary>
    /// <returns>88 bytes per light.</returns>
    public byte[] WorldLightBytes() => MemoryMarshal.AsBytes(WorldLights.AsSpan()).ToArray();

    /// <summary>
    /// <c>RadWorld_Start</c>: patches, subdivision,
    /// direct lights, sky cameras -- everything before the first ray.
    /// </summary>
    /// <param name="bsp">The map, as vvis left it.</param>
    /// <param name="settings">The switches.</param>
    /// <param name="texLights">The texlights from the <c>.rad</c> files.</param>
    /// <param name="tracer">
    /// The tracer, used here only for <c>CanLeafTraceToSky</c> on radial-vis
    /// leaves; the same one should light the faces.
    /// </param>
    /// <param name="parallelism">How many workers.</param>
    /// <param name="cancellationToken">Cancels the stage.</param>
    /// <returns>The model, ready for <see cref="LightFacesAsync"/>.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static async Task<RadWorld> StartAsync(
        BspData bsp,
        DirectLightingSettings settings,
        TextureLightTable texLights,
        IRayTracer tracer,
        CompileParallelism parallelism,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(texLights);
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(parallelism);
        cancellationToken.ThrowIfCancellationRequested();

        using WorkQueue queue = new(parallelism);
        RadWorld world = null!;

        // The set-up runs on a worker, never on the caller's thread (plan §1a).
        await queue.RunAsync(
            1,
            (_, _) => world = Build(bsp, settings, texLights),
            new WorkQueueOptions { Stage = "RadWorld_Start" },
            cancellationToken).ConfigureAwait(false);

        await world.ProbeRadialSkyLeavesAsync(queue, tracer, cancellationToken).ConfigureAwait(false);
        return world;
    }

    /// <summary>
    /// The synchronous body of <see cref="StartAsync"/>, for callers already
    /// on a worker.
    /// </summary>
    /// <param name="bsp">The map.</param>
    /// <param name="settings">The switches.</param>
    /// <param name="texLights">The texlights.</param>
    /// <returns>The model, radial sky leaves not yet probed.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static RadWorld Build(BspData bsp, DirectLightingSettings settings, TextureLightTable texLights)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(texLights);

        VradLightingRange range = settings.Hdr ? VradLightingRange.Hdr : VradLightingRange.Ldr;
        LightGeometry geometry = LightGeometry.Load(bsp, range, settings.Compliance);
        List<BspEntity> entities = EntityLump.Parse(bsp[BspLump.Entities]);
        RadWorld world = new(bsp, settings, geometry, entities);

        // StaticDispMgr->Init: before the patches, which
        // include the displacements' own.
        world.Displacements = Displacement.VradDisplacements.Load(bsp, geometry, settings);

        // No vis data means direct light only.
        // No vis data means direct light only, AND a flat
        // ambient of 0.1 in every channel, whatever -ambient said.
        if (!world.Visibility.HasVisibility)
        {
            world.Warnings.Add("No vis information, direct lighting only.");
            world.Settings = settings with { Bounces = 0, Ambient = new Vec3(0.1f, 0.1f, 0.1f) };
        }

        world.StartCore(texLights);
        return world;
    }

    private void StartCore(TextureLightTable texLights)
    {
        DirectLightingSettings s = Settings;
        Statistics.Faces = Geometry.Faces.Length;

        // In stock's order. MakeParents is
        // Rad.Ambient.BspParents, lane 4g's, the only reader of its output.
        Patches = PatchBuilder.Build(Geometry, Entities, texLights, s.MaxChop, s.TexScale);

        // MakePatches ends with StaticDispMgr->MakePatches.
        Statistics.Displacements = Displacements.Count;
        Statistics.DisplacementArea = Displacement.DispPatchBuilder.MakePatches(
            Displacements.Surfaces, Geometry, Patches, texLights, s);
        foreach (string w in Displacements.Warnings)
        {
            Warnings.Add(w);
        }

        Neighbours = FaceNeighbours.Build(Geometry, s.SmoothingThreshold);
        Statistics.Subdivision = PatchSubdivider.Subdivide(
            Geometry, Neighbours, Patches, Tree, s.MinChop, s.Bounces, s.Fast, s.SmoothingThreshold,
            SubdivideDisplacementPatch);

        SkyLeaves = new SkyLeafVisibility();
        Lights = DirectLightBuilder.Build(
            Geometry, Patches, Entities, Visibility, Tree,
            new DirectLightOptions
            {
                Hdr = s.Hdr,
                LightScale = s.LightScale,
                DLightThreshold = s.DLightThreshold,
                SunAngularExtent = s.SunAngularExtent,
                Compliance = s.Compliance,
            },
            SkyLeaves);
        Statistics.DirectLights = Lights.Count;
        foreach (string w in Lights.Warnings)
        {
            Warnings.Add(w);
        }

        SkyCameras = SkyCameras.Build(Entities, Tree, Geometry.Leaves, Geometry.AreaCount);
        Gatherer = new DirectLightGatherer(
            Lights.Active, Lights.SunAngularExtent, s, Tree, Geometry.Leaves, SkyCameras);
        WorldLights = WorldLightExporter.Export(Lights);
    }

    private void SubdivideDisplacementPatch(PatchSet patches, int index)
    {
        Displacement.VradDispSurface? d = Displacements.ForFace(Geometry, patches.At(index).FaceNumber);
        if (d is not null)
        {
            Displacement.DispPatchBuilder.SubdividePatch(d, patches, index, Settings);
        }
    }

    /// <summary>
    /// The displacement sample and patch hashes,
    /// built in parallel; call after <see cref="LightFacesAsync"/> and after
    /// bounce, before <c>FinalLightFace</c>.
    /// </summary>
    /// <param name="parallelism">How many workers.</param>
    /// <param name="cancellationToken">Cancels the stage.</param>
    /// <returns>The hashes, also kept in <see cref="DisplacementHash"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parallelism"/> is null.</exception>
    public async Task<Displacement.DispSampleHash> BuildDisplacementHashAsync(
        CompileParallelism parallelism, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parallelism);
        using WorkQueue queue = new(parallelism);
        DisplacementHash = await Displacement.DispSampleHash.BuildAsync(
            Geometry, FaceLights, Patches, Settings.Bounces, queue, cancellationToken).ConfigureAwait(false);
        return DisplacementHash;
    }

    /// <summary>
    /// What <see cref="Displacement.DispRadial"/> reads, once
    /// <see cref="BuildDisplacementHashAsync"/> has run.
    /// </summary>
    /// <returns>The radial context.</returns>
    /// <exception cref="InvalidOperationException">The hash has not been built.</exception>
    public Displacement.DispRadialContext DisplacementRadialContext() =>
        new(
            Geometry,
            Neighbours,
            Patches,
            FaceLights,
            Displacements,
            DisplacementHash ?? throw new InvalidOperationException("build the displacement hash first"),
            Settings.StockNormalise);

    /// <summary>
    /// <c>CanLeafTraceToSky</c> for every radial
    /// leaf <see cref="SkyLeafVisibility"/> could not decide, as one batch.
    /// </summary>
    private async Task ProbeRadialSkyLeavesAsync(
        WorkQueue queue, IRayTracer tracer, CancellationToken cancellationToken)
    {
        IReadOnlyList<int> leaves = SkyLeaves.RadialCandidates;
        if (leaves.Count == 0)
        {
            return;
        }

        LightRayLog rays = new() { StockRays = Geometry.StockEstimates };
        List<int> hits = [];
        WorkQueueOptions stage = new() { Stage = "RadWorld_Start" };

        // Collect, trace, replay: the same shape as the face lighting.
        await queue.RunAsync(
            1,
            (_, _) =>
            {
                foreach (int leaf in leaves)
                {
                    _ = CanLeafTraceToSky(leaf, rays);
                }
            },
            stage,
            cancellationToken).ConfigureAwait(false);

        await rays.TraceOwnAsync(tracer, cancellationToken).ConfigureAwait(false);
        rays.BeginResolve();

        await queue.RunAsync(
            1,
            (_, _) =>
            {
                foreach (int leaf in leaves)
                {
                    if (CanLeafTraceToSky(leaf, rays))
                    {
                        hits.Add(leaf);
                    }
                }
            },
            stage,
            cancellationToken).ConfigureAwait(false);

        foreach (int leaf in hits)
        {
            SkyLeaves.MarkSky(leaf);
        }
    }

    /// <summary>
    /// <c>CanLeafTraceToSky</c>: does any of the 162
    /// <c>g_anorms</c> directions from the leaf's box centre reach sky?
    /// </summary>
    /// <param name="leaf">The leaf.</param>
    /// <param name="rays">Where the rays are recorded or replayed.</param>
    /// <returns>True when a ray saw sky; false while collecting.</returns>
    /// <remarks>
    /// <para>
    /// <b>The tail double-count.</b> The directions go four
    /// at a time with each index clamped to 161, so the last group is
    /// <c>anorms[160], [161], [161], [161]</c>: direction 161 is cast three
    /// times. It cannot change an "any hit" answer; it is reproduced because the
    /// ray set is an observable of the tracer's work.
    /// </para>
    /// <para>
    /// <b>A stock bug that is not reproduced.</b> The box
    /// centre is computed into <c>center</c> and then never used: the rays are
    /// cast from <c>center4</c>, a <c>FourVectors</c> that is declared and never
    /// initialised, so stock traces from whatever the stack held. That has no
    /// defined meaning to reproduce; this casts from the centre, which is what
    /// the code plainly meant.
    /// </para>
    /// </remarks>
    public bool CanLeafTraceToSky(int leaf, LightRayLog rays)
    {
        ArgumentNullException.ThrowIfNull(rays);

        LeafInfo info = Geometry.Leaves[leaf];
        Vec3 center = (info.Mins + info.Maxs) * 0.5f;

        ReadOnlySpan<Vec3> anorms = VertexNormals.All;
        Span<Vec3> start = stackalloc Vec3[SampleGroup.Lanes];
        Span<Vec3> stop = stackalloc Vec3[SampleGroup.Lanes];
        Span<float> fraction = stackalloc float[SampleGroup.Lanes];
        start.Fill(center);

        // StockQuirk.SkyProbeTailDoubleCount: stock traces the clamped
        // duplicates; correct traces only the real directions of the last group.
        bool tail = Settings.Compliance.Emulates(StockQuirk.SkyProbeTailDoubleCount);
        bool any = false;
        for (int j = 0; j < LightConstants.VertexNormalCount; j += 4)
        {
            for (int lane = 0; lane < SampleGroup.Lanes; lane++)
            {
                Vec3 delta = anorms[Math.Min(j + lane, LightConstants.VertexNormalCount - 1)];
                stop[lane] = (delta * -LightConstants.MaxTraceLength) + center;
            }

            int count = tail ? SampleGroup.Lanes : Math.Min(SampleGroup.Lanes, LightConstants.VertexNormalCount - j);
            Gatherer.TestLineDoesHitSky(start, stop, count, rays, fraction);
            for (int lane = 0; lane < SampleGroup.Lanes; lane++)
            {
                any |= fraction[lane] > 0.0f;
            }
        }

        return any && !rays.Collecting;
    }
}
