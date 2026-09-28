//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Globalization;
using System.Runtime.ExceptionServices;
using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp.MaterialPatch;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;

namespace SourceSharp.MapTools.Bsp.Props;

/// <summary>
/// <c>EmitStaticProps</c>: every
/// <c>prop_static</c> into the <c>sprp</c> game lump, and the props and
/// <c>info_lighting</c> entities out of the entity lump.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stock call order.</b> In <c>EndBSPFile</c> after
/// <c>EmitPhysCollision</c> and <c>ClearDistToClosestWater</c>, before
/// <c>EmitDetailObjects</c>; also in the
/// <c>-onlyents</c> and <c>-onlyprops</c> paths.
/// It reads the WRITTEN tree (<see cref="BspTreeView"/>).
/// </para>
/// <para>
/// One collision model per distinct model, keyed on the lower-cased,
/// forward-slashed name (<c>GetCollisionModel</c>); a model
/// that failed to load is remembered as failed, so its warning is printed
/// once.
/// </para>
/// </remarks>
public sealed class StaticPropEmitter
{
    private readonly VbspContext _context;
    private readonly IStaticPropCollision _collision;
    private readonly Dictionary<string, ModelEntry> _models = new(StringComparer.Ordinal);
    private Task _cooking = Task.CompletedTask;

    /// <summary>An emitter for one compile.</summary>
    /// <param name="context">The compile: its content and diagnostics.</param>
    /// <param name="collision">The physics seam: 3h's cooker, or <see cref="ManagedStaticPropCollision"/>.</param>
    public StaticPropEmitter(VbspContext context, IStaticPropCollision collision)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(collision);

        _context = context;
        _collision = collision;
    }

    /// <summary>Emits the lump and strips the entities.</summary>
    /// <param name="entities">The main map's entities: <c>entities[]</c>.</param>
    /// <param name="tree">The written tree.</param>
    /// <param name="cancellationToken">Cancels the model reads and the physics queries.</param>
    /// <returns>The lump: dictionary, leaf list and props, in stock's order.</returns>
    public async Task<StaticPropLump> EmitAsync(
        IReadOnlyList<MapEntity> entities,
        BspTreeView tree,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(tree);

        StaticPropLump lump = new();

        List<int> lightingInfo = [];
        for (int i = 0; i < entities.Count; i++)
        {
            if (string.Equals(entities[i].ValueForKey("classname"), "info_lighting", StringComparison.Ordinal))
            {
                lightingInfo.Add(i);
            }
        }

        // Stock does one prop at a time: GetCollisionModel (load and cook the
        // model the first time it is named), ComputeStaticPropLeaves, append
        // The props do not read each other, so this
        // runs it as passes (plan 3p): read every prop; load each new model
        // once, in the order stock first names it; cook the hulls (perhaps
        // already cooking since PrefetchAsync) and trace the leaves in
        // parallel into per-model and per-prop slots; then commit serially in
        // entity order, which is the only place the lump, the dictionary and
        // the warnings are written.
        List<PropSlot> props = [];
        List<ModelEntry> newModels = [];
        for (int i = 0; i < entities.Count; i++)
        {
            MapEntity entity = entities[i];
            if (!IsStaticProp(entity))
            {
                continue;
            }

            StaticPropBuild build = ReadBuild(entity);
            props.Add(new PropSlot(entity, build, Entry(build.ModelName, newModels)));
        }

        await LoadAllAsync(newModels, cancellationToken).ConfigureAwait(false);

        // A model PrefetchAsync could not load fails the compile at its first
        // prop, where stock's Error() in GetCollisionModel would.
        foreach (PropSlot prop in props)
        {
            prop.Model.LoadError?.Throw();
        }

        await _cooking.ConfigureAwait(false);
        await CookAsync([.. props.Select(p => p.Model).Distinct().Where(m => m.Meshes is not null && !m.Cooking)], cancellationToken)
            .ConfigureAwait(false);

        await ForEachAsync(props.Count, async (i, token) =>
        {
            PropSlot prop = props[i];
            if (prop.Model.Hull is IStaticPropHull hull)
            {
                prop.Leaves = hull is IStaticPropLeafHull listing
                    ? await listing.ComputeLeavesAsync(tree, prop.Build.Origin, prop.Build.Angles, token).ConfigureAwait(false)
                    : await StaticPropLeaves.ComputeAsync(tree, hull, prop.Build.Origin, prop.Build.Angles, token)
                        .ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);

        foreach (PropSlot prop in props)
        {
            if (!prop.Model.Committed)
            {
                _context.Diagnostics.AddRange(prop.Model.Diagnostics);
                prop.Model.Committed = true;
            }

            if (prop.Model.Hull is not null)
            {
                Add(lump, prop.Build, prop.Leaves!, entities, lightingInfo);
            }

            // Epairs = 0, whether or not it was emitted.
            prop.Entity.Clear();
        }

        for (int i = lightingInfo.Count - 1; i >= 0; i--)
        {
            entities[lightingInfo[i]].Clear();
        }

        return lump;
    }

    /// <summary>
    /// Starts the static-prop hulls early: loads every model the map's
    /// <c>prop_static</c>s name (serially, on the caller's flow, in the order
    /// stock first names them) and, when the compile may use more than one
    /// thread, starts cooking their hulls in the background so that they
    /// overlap the BSP build. <see cref="EmitAsync"/> uses what this made.
    /// </summary>
    /// <param name="entities">The main map's entities.</param>
    /// <param name="cancellationToken">Cancels the reads and the cooks.</param>
    /// <returns>When the loads are done; the cooks may still be running.</returns>
    /// <remarks>
    /// Output-neutral: a hull is a pure function of its model, a model's
    /// warnings wait in its entry for its first prop's turn in
    /// <see cref="EmitAsync"/>, and a load that fails is rethrown at that same
    /// turn, never here.
    /// </remarks>
    public async Task PrefetchAsync(IReadOnlyList<MapEntity> entities, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entities);

        List<ModelEntry> newModels = [];
        foreach (MapEntity entity in entities)
        {
            if (IsStaticProp(entity))
            {
                Entry(entity.ValueForKey("model"), newModels);
            }
        }

        await LoadAllAsync(newModels, cancellationToken).ConfigureAwait(false);

        if (_context.Parallelism.MaxDegree > 1)
        {
            _cooking = CookAsync([.. newModels.Where(m => m.Meshes is not null)], cancellationToken);
        }
    }

    /// <summary>
    /// The entity's keys as <c>EmitStaticProps</c> reads them into a
    /// <c>StaticPropBuild_t</c>.
    /// </summary>
    /// <param name="entity">A <c>prop_static</c>.</param>
    /// <returns>The build.</returns>
    public static StaticPropBuild ReadBuild(MapEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        StaticPropFlags flags = StaticPropFlags.None;
        if (entity.IntForKey("ignorenormals") == 1)
        {
            flags |= StaticPropFlags.IgnoreNormals;
        }

        if (entity.IntForKey("disableshadows") == 1)
        {
            flags |= StaticPropFlags.NoShadow;
        }

        if (entity.IntForKey("disablevertexlighting") == 1)
        {
            flags |= StaticPropFlags.NoPerVertexLighting;
        }

        if (entity.IntForKey("disableselfshadowing") == 1)
        {
            flags |= StaticPropFlags.NoSelfShadowing;
        }

        if (entity.IntForKey("screenspacefade") == 1)
        {
            flags |= StaticPropFlags.ScreenSpaceFade;
        }

        int lightmapX = 0, lightmapY = 0;
        if (entity.IntForKey("generatelightmaps") == 0)
        {
            flags |= StaticPropFlags.NoPerTexelLighting;
        }
        else
        {
            lightmapX = entity.IntForKey("lightmapresolutionx");
            lightmapY = entity.IntForKey("lightmapresolutiony");
        }

        float fadeMax = entity.FloatForKey("fademaxdist");
        bool fades = fadeMax > 0;
        float fadeMin = 0;
        if (fades)
        {
            fadeMin = entity.FloatForKey("fademindist");
            if (fadeMin < 0)
            {
                fadeMin = fadeMax;
            }
        }

        return new StaticPropBuild(
            ModelName: entity.ValueForKey("model"),
            LightingOrigin: entity.ValueForKey("lightingorigin"),
            Origin: entity.GetVectorForKey("origin"),
            Angles: entity.GetVectorForKey("angles"),
            Solid: entity.IntForKey("solid"),
            Skin: entity.IntForKey("skin"),
            Flags: flags,
            FadeMinDist: fadeMin,
            FadeMaxDist: fadeMax,
            FadesOut: fades,
            ForcedFadeScale: entity.ValueForKey("fadescale").Length > 0 ? entity.FloatForKey("fadescale") : 1,
            MinDxLevel: unchecked((ushort)entity.IntForKey("mindxlevel")),
            MaxDxLevel: unchecked((ushort)entity.IntForKey("maxdxlevel")),
            LightmapResolutionX: lightmapX,
            LightmapResolutionY: lightmapY);
    }

    // AddStaticPropToLump, after its two queries:
    // the model's hull is not null and the leaves are the prop's.
    private void Add(
        StaticPropLump lump,
        StaticPropBuild build,
        IReadOnlyList<ushort> leaves,
        IReadOnlyList<MapEntity> entities,
        List<int> lightingInfo)
    {
        if (leaves.Count == 0)
        {
            _context.Diagnostics.Add(new CompileDiagnostic(
                SurfaceContentDiagnostics.StaticPropOutsideMap,
                DiagnosticSeverity.Warning,
                string.Create(CultureInfo.InvariantCulture,
                    $"Static prop {build.ModelName} outside the map ({build.Origin.X:F2}, {build.Origin.Y:F2}, {build.Origin.Z:F2})"),
                new MapLocation(Position: (build.Origin.X, build.Origin.Y, build.Origin.Z))));
            return;
        }

        StaticProp prop = new()
        {
            PropType = (ushort)AddDictionary(lump, build.ModelName),
            Origin = build.Origin,
            Angles = build.Angles,
            FirstLeaf = (ushort)lump.LeafEntries.Count,
            LeafCount = (ushort)leaves.Count,
            Solid = unchecked((byte)build.Solid),
            Skin = build.Skin,
            Flags = build.FadesOut ? build.Flags | StaticPropFlags.Fades : build.Flags,
            FadeMinDist = build.FadeMinDist,
            FadeMaxDist = build.FadeMaxDist,
            ForcedFadeScale = build.ForcedFadeScale,
            MinDxLevel = build.MinDxLevel,
            MaxDxLevel = build.MaxDxLevel,
            LightmapResolutionX = unchecked((ushort)build.LightmapResolutionX),
            LightmapResolutionY = unchecked((ushort)build.LightmapResolutionY),
        };

        // ComputeLightingOrigin: the LAST info_lighting whose
        // targetname matches, case-sensitively.
        if (build.LightingOrigin.Length > 0)
        {
            for (int i = lightingInfo.Count - 1; i >= 0; i--)
            {
                MapEntity lighting = entities[lightingInfo[i]];
                if (string.Equals(lighting.ValueForKey("targetname"), build.LightingOrigin, StringComparison.Ordinal))
                {
                    prop.LightingOrigin = lighting.GetVectorForKey("origin");
                    prop.Flags |= StaticPropFlags.UseLightingOrigin;
                    break;
                }
            }
        }

        lump.Props.Add(prop);
        lump.LeafEntries.AddRange(leaves);
    }

    // AddStaticPropDictLump: strncpy into 128 bytes, then a memcmp
    // search from the END, so the match is by exact bytes.
    private static int AddDictionary(StaticPropLump lump, string modelName)
    {
        for (int i = lump.ModelNames.Count - 1; i >= 0; i--)
        {
            if (string.Equals(lump.ModelNames[i], modelName, StringComparison.Ordinal))
            {
                return i;
            }
        }

        lump.ModelNames.Add(modelName);
        return lump.ModelNames.Count - 1;
    }

    private static bool IsStaticProp(MapEntity entity)
    {
        string className = entity.ValueForKey("classname");
        return string.Equals(className, "static_prop", StringComparison.Ordinal)
            || string.Equals(className, "prop_static", StringComparison.Ordinal);
    }

    // S_ModelCollisionCache's key: lower case,
    // forward slashes. A new entry keeps the spelling that named it first.
    private ModelEntry Entry(string modelName, List<ModelEntry> newModels)
    {
        string key = modelName.ToLowerInvariant().Replace('\\', '/');
        if (!_models.TryGetValue(key, out ModelEntry? model))
        {
            model = new ModelEntry(modelName);
            _models.Add(key, model);
            newModels.Add(model);
        }

        return model;
    }

    // Content reads stay on the caller's flow, one model at a time; a load
    // that throws is kept for the model's first prop to rethrow.
    private async Task LoadAllAsync(List<ModelEntry> models, CancellationToken cancellationToken)
    {
        foreach (ModelEntry model in models)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await LoadAsync(model, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                model.LoadError = ExceptionDispatchInfo.Capture(e);
            }
        }
    }

    // GetCollisionModel's cook half, for many models at once,
    // biggest first so that the longest cook starts first.
    private Task CookAsync(ModelEntry[] models, CancellationToken cancellationToken)
    {
        foreach (ModelEntry model in models)
        {
            model.Cooking = true;
        }

        ModelEntry[] order =
        [
            .. models
                .Select((m, index) => (Model: m, Index: index))
                .OrderByDescending(m => m.Model.VertexCount)
                .ThenBy(m => m.Index)
                .Select(m => m.Model),
        ];

        return ForEachAsync(order.Length, async (i, token) =>
        {
            ModelEntry model = order[i];
            model.Hull = await _collision.BuildHullAsync(model.Name, model.Meshes!, token).ConfigureAwait(false);
            model.Meshes = null;
            if (model.Hull is null)
            {
                model.Diagnostics.Add(new CompileDiagnostic(
                    SurfaceContentDiagnostics.StaticPropBadGeometry,
                    DiagnosticSeverity.Warning,
                    $"Bad geometry on \"{model.Name}\"!"));
            }
        }, cancellationToken);
    }

    // GetCollisionModel's load half: the warnings go to the model's
    // own list and reach the compile's when its first prop is committed.
    private async ValueTask LoadAsync(ModelEntry model, CancellationToken cancellationToken)
    {
        StudioModelLoad load = await StudioModelCheck.LoadAsync(
            _context.Content, model.Name, "prop_static", model.Diagnostics, _context.Options.Compliance, cancellationToken)
            .ConfigureAwait(false);

        if (!load.IsValid)
        {
            model.Diagnostics.Add(new CompileDiagnostic(
                SurfaceContentDiagnostics.StudioModelLoadFailed,
                DiagnosticSeverity.Warning,
                $"Error loading studio model \"{model.Name}\"!"));
            return;
        }

        VvdFile vvd = await LoadVertexFileAsync(load.Mdl!, cancellationToken).ConfigureAwait(false);
        model.Meshes = StudioModelCheck.MeshHulls(load.Mdl!, vvd);
        model.VertexCount = model.Meshes.Sum(m => (long)m.Length);
    }

    // An index-slot loop over the compile's degree: every body writes only
    // its own slot, so the degree changes the speed and never the result.
    private Task ForEachAsync(int count, Func<int, CancellationToken, ValueTask> body, CancellationToken cancellationToken)
    {
        if (count == 0)
        {
            return Task.CompletedTask;
        }

        // On a pool, through the pool's own loop, which ends in a fault if a
        // host disposes the pool under the compile instead of running the
        // remaining models on the disposing thread (CompilePool.ForAsync).
        if (_context.Parallelism.Pool is { } pool)
        {
            return pool.ForAsync(count, _context.Parallelism.MaxDegree, body, cancellationToken);
        }

        ParallelOptions options = new()
        {
            MaxDegreeOfParallelism = Math.Max(1, _context.Parallelism.MaxDegree),
            TaskScheduler = _context.Parallelism.Scheduler ?? TaskScheduler.Default,
            CancellationToken = cancellationToken,
        };
        return System.Threading.Tasks.Parallel.ForAsync(0, count, options, body);
    }

    // One model, as GetCollisionModel caches it (s_ModelCollisionCache).
    private sealed class ModelEntry(string name)
    {
        public string Name { get; } = name;

        public List<CompileDiagnostic> Diagnostics { get; } = [];

        public List<Vec3[]>? Meshes { get; set; }

        public long VertexCount { get; set; }

        public IStaticPropHull? Hull { get; set; }

        public ExceptionDispatchInfo? LoadError { get; set; }

        public bool Cooking { get; set; }

        public bool Committed { get; set; }
    }

    // One prop_static and the leaves its hull touches.
    private sealed class PropSlot(MapEntity entity, StaticPropBuild build, ModelEntry model)
    {
        public MapEntity Entity { get; } = entity;

        public StaticPropBuild Build { get; } = build;

        public ModelEntry Model { get; } = model;

        public IReadOnlyList<ushort>? Leaves { get; set; }
    }

    // mstudiomodel_t::CacheVertexData: "models/" + the header's own
    // name, extension swapped for .vvd. Every failure there is Error().
    private async ValueTask<VvdFile> LoadVertexFileAsync(MdlFile mdl, CancellationToken cancellationToken)
    {
        string name = "models/" + mdl.Name;
        int dot = name.LastIndexOf('.');
        int slash = name.LastIndexOfAny(['/', '\\']);
        string path = (dot > slash ? name[..dot] : name) + ".vvd";

        using IMemoryOwner<byte>? owner = VPath.TryCreate(path, out VPath vpath) && !vpath.IsEmpty
            ? await _context.Content.ReadAsync(vpath, cancellationToken).ConfigureAwait(false)
            : null;

        if (owner is null || owner.Memory.Length == 0)
        {
            throw new MapCompileException(
                owner is null ? $"Unable to load vertex data \"{path}\"" : $"Bad size for vertex data \"{path}\"");
        }

        VvdFile vvd;
        try
        {
            vvd = VvdFile.Parse(owner.Memory.ToArray());
        }
        catch (InvalidStudioException e)
        {
            throw new MapCompileException($"Error Vertex File {path}: {e.Message}");
        }

        if (vvd.Checksum != mdl.Checksum)
        {
            throw new MapCompileException(
                $"Error Vertex File {path} checksum {vvd.Checksum} should be {mdl.Checksum}");
        }

        return vvd;
    }
}

/// <summary>
/// <c>StaticPropBuild_t</c>: one prop's keys,
/// already interpreted.
/// </summary>
/// <param name="ModelName">The <c>model</c> key.</param>
/// <param name="LightingOrigin">The <c>lightingorigin</c> key.</param>
/// <param name="Origin">The origin.</param>
/// <param name="Angles">The angles.</param>
/// <param name="Solid">The <c>solid</c> key; stored as a byte.</param>
/// <param name="Skin">The <c>skin</c> key.</param>
/// <param name="Flags">The flags the keys set, before <c>FADES</c> and <c>USE_LIGHTING_ORIGIN</c>.</param>
/// <param name="FadeMinDist">The fade start; the max when <c>fademindist</c> is negative.</param>
/// <param name="FadeMaxDist">The fade end.</param>
/// <param name="FadesOut">Whether <paramref name="FadeMaxDist"/> is positive.</param>
/// <param name="ForcedFadeScale"><c>fadescale</c>, or 1 when the key is empty.</param>
/// <param name="MinDxLevel"><c>mindxlevel</c>, truncated to 16 bits.</param>
/// <param name="MaxDxLevel"><c>maxdxlevel</c>, truncated to 16 bits.</param>
/// <param name="LightmapResolutionX">0 unless <c>generatelightmaps</c> is set.</param>
/// <param name="LightmapResolutionY">0 unless <c>generatelightmaps</c> is set.</param>
public sealed record StaticPropBuild(
    string ModelName,
    string LightingOrigin,
    Vec3 Origin,
    Vec3 Angles,
    int Solid,
    int Skin,
    StaticPropFlags Flags,
    float FadeMinDist,
    float FadeMaxDist,
    bool FadesOut,
    float ForcedFadeScale,
    ushort MinDxLevel,
    ushort MaxDxLevel,
    int LightmapResolutionX,
    int LightmapResolutionY);
