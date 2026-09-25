using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Phys;

namespace SourceSharp.MapTools.Bsp;

/// <summary>
/// One vbsp compile's state, in one object.
/// </summary>
/// <remarks>
/// <para>
/// <b>This replaces 48 externs and 117 file-scope statics.</b> Stock's vbsp
/// keeps its whole state in globals — <c>g_Maps</c>, <c>g_MainMap</c>,
/// <c>g_LoadingMap</c>, <c>texinfo</c>, <c>dtexdata</c>, <c>numtexdata</c>,
/// <c>textureref</c>, <c>nummiptex</c>, <c>g_TexDataString*</c>,
/// <c>g_bHasWater</c>, <c>CMapFile::m_InstancePath</c>,
/// <c>CMapFile::m_InstanceCount</c>, <c>CMapFile::c_areaportals</c>, and the
/// switches — which is why one compile per process is the only shape stock
/// supports. Every stage in this port takes one of these instead, and that is
/// what makes a second compile in the same process, a compile on a worker
/// thread, and a re-entrant compile possible later.
/// </para>
/// <para>
/// The stages after loading — CSG, the BSP build, portals, faces, writing —
/// take this same object. Whatever they need to add belongs here rather than
/// in a static, and adding it does not change any signature.
/// </para>
/// <para>
/// Not thread-safe, on purpose. The load order is the output, so one context is
/// one compile on one thread; parallelism inside a stage gets its own
/// per-worker state rather than sharing this.
/// </para>
/// </remarks>
public sealed class VbspContext
{
    /// <summary>Creates a context for a compile.</summary>
    /// <param name="options">The compile's switches.</param>
    /// <param name="content">Where materials and instances are read from.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> or <paramref name="content"/> is null.
    /// </exception>
    public VbspContext(VbspOptions options, IContentFileSystem content)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(content);

        Options = options;
        Content = content;
        Materials = new MaterialFactsCache(content);

        // The subset of the switches FindMiptex reads: g_BumpAll (-bumpall),
        // g_bLightIfMissing (-lightifmissing), g_NodrawTriggers
        // (-nodrawtriggers) and g_DisableWaterLighting. Mapped once, here,
        // rather than at each of the classifier's call sites.
        MaterialOptions = new MaterialCompileOptions
        {
            BumpAll = options.BumpAll,
            LightIfMissing = options.LightIfMissing,
            NodrawTriggers = options.NoDrawTriggers,
        };

        Strings = new TexDataStringTable();
        TexDatas = new TexDataTable(Strings);

        // The compile's compliance reaches the polylib here and nowhere else:
        // every stage takes its arena from this context.
        Windings = new WindingArena { Compliance = options.Compliance };

        Patcher = new MaterialPatch.MaterialPatcher(content, new MaterialPatch.MapPakFile(), options.Compliance);
    }

    /// <summary>
    /// The compile's material patches and the pak they are written into:
    /// <c>materialpatch.cpp</c>'s translation table and <c>GetPakFile()</c>.
    /// </summary>
    /// <remarks>
    /// One per compile, and owned HERE rather than by the stage that happens
    /// to create the first patch, because stock's are globals every stage
    /// reads: the face stage's <c>AssignBottomWaterMaterialToFace</c> reads a
    /// water's <c>$bottommaterial</c> through <c>GetValueFromPatchedMaterial</c>,
    /// from the patch the cubemap fixup put in the pak
    /// (<c>faces.cpp:1255-1264</c>, "This happens *after* cubemap fixup").
    /// </remarks>
    public MaterialPatch.MaterialPatcher Patcher { get; }

    /// <summary>The pak file being built: <see cref="Patcher"/>'s.</summary>
    public MaterialPatch.MapPakFile Pak => Patcher.Pak;

    /// <summary>The compile's switches.</summary>
    public VbspOptions Options { get; }

    /// <summary>Where materials and instance VMFs are read from.</summary>
    public IContentFileSystem Content { get; }

    /// <summary>The material cache: one read per distinct material.</summary>
    public MaterialFactsCache Materials { get; }

    /// <summary>
    /// The switches <see cref="MaterialSurfaceClassifier"/> reads, derived from
    /// <see cref="Options"/>.
    /// </summary>
    public MaterialCompileOptions MaterialOptions { get; }

    /// <summary>The arena every side winding in the compile lives in.</summary>
    /// <remarks>
    /// One for the whole compile, not one per map, because a merged instance
    /// keeps the winding handles it was given
    /// (<c>MergeBrushSides</c> transforms the points in place,
    /// <c>map.cpp:2230-2234</c>).
    /// </remarks>
    public WindingArena Windings { get; }

    /// <summary>The TEXDATA_STRING_DATA and _TABLE lumps.</summary>
    public TexDataStringTable Strings { get; }

    /// <summary>The TEXDATA lump.</summary>
    public TexDataTable TexDatas { get; }

    /// <summary>The TEXINFO lump, before compaction.</summary>
    public TexInfoTable TexInfos { get; } = new();

    /// <summary>
    /// The per-material classification table: <c>textureref</c>.
    /// </summary>
    public TextureReferenceTable TextureReferences { get; } = new();

    /// <summary>
    /// The material substitutions <c>-replacematerials</c> loaded, or null.
    /// </summary>
    public MaterialReplacements? MaterialReplacements { get; set; }

    /// <summary>Everything the compile has to say about the map.</summary>
    public List<CompileDiagnostic> Diagnostics { get; } = [];

    /// <summary>
    /// Every map loaded so far, main map first: <c>g_Maps</c>.
    /// </summary>
    /// <remarks>
    /// Stock never removes from this and neither does this, even though an
    /// instance's <c>CMapFile</c> is <c>delete</c>d right after it is merged
    /// (<c>map.cpp:2045</c>) — leaving a dangling pointer in <c>g_Maps</c> that
    /// nothing happens to dereference. Keeping the object alive is the same
    /// behaviour minus the bug.
    /// </remarks>
    public List<MapFile> Maps { get; } = [];

    /// <summary>The map everything is merged into: <c>g_MainMap</c>.</summary>
    public MapFile? MainMap { get; set; }

    /// <summary>The map currently being read: <c>g_LoadingMap</c>.</summary>
    public MapFile? LoadingMap { get; set; }

    /// <summary>
    /// The <c>InstancePath</c> from <c>gameinfo.txt</c>, lowercased and
    /// slash-fixed: <c>CMapFile::m_InstancePath</c>.
    /// </summary>
    public string InstancePath { get; set; } = string.Empty;

    /// <summary>
    /// How many instances have been merged: <c>CMapFile::m_InstanceCount</c>.
    /// </summary>
    /// <remarks>
    /// Only ever read to name an instance that has no <c>targetname</c> and no
    /// <c>name</c>, as <c>InstanceAuto&lt;n&gt;</c> (<c>map.cpp:2361</c>).
    /// </remarks>
    public int InstanceCount { get; set; }

    /// <summary>
    /// How many areaportals have been seen: <c>CMapFile::c_areaportals</c>.
    /// </summary>
    /// <remarks>
    /// One-based when written into an entity's <c>portalnumber</c>: the counter
    /// is incremented BEFORE it is used (<c>map.cpp:1702-1707</c>), so the
    /// first areaportal in a map is portal 1.
    /// </remarks>
    public int AreaPortalCount { get; set; }

    /// <summary>
    /// The map file name, without directory or extension: <c>mapbase</c>.
    /// </summary>
    /// <remarks>
    /// Read by <c>GeneratePatchedMaterialName</c> to build
    /// <c>maps/&lt;mapbase&gt;/&lt;material&gt;_wvt_patch</c>
    /// (<c>worldvertextransitionfixup.cpp:51</c>), so it is part of a material
    /// name that reaches TEXDATA_STRING_DATA.
    /// </remarks>
    public string MapBase { get; set; } = string.Empty;

    /// <summary>
    /// The VMF's <c>mapversion</c>: <c>g_MapRevision</c>,
    /// <c>utils/common/bsplib.cpp:695</c>.
    /// </summary>
    /// <remarks>
    /// Written into the BSP header. Set by whichever entity carries the key,
    /// which in practice is worldspawn.
    /// </remarks>
    public int MapRevision { get; set; }

    /// <summary>
    /// The <c>env_cubemap</c> samples the map placed, in entity order.
    /// </summary>
    /// <remarks>
    /// <c>Cubemap_InsertSample</c>'s list. Attaching the named brush sides to
    /// each sample, and patching the materials of specular sides, is pipeline
    /// work that a later lane owns; the samples are collected here because
    /// this is where the entities are read and blanked.
    /// </remarks>
    public List<CubemapSample> CubemapSamples { get; } = [];

    /// <summary>
    /// The default luxel size when a side's <c>lightmapscale</c> is zero:
    /// <c>g_defaultLuxelSize</c>, from <c>DEFAULT_LUXEL_SIZE</c>
    /// (<c>utils/common/bsplib.h:44</c>).
    /// </summary>
    public float DefaultLuxelSize { get; set; } = 16.0f;

    /// <summary>
    /// The collision cooker <c>EmitPhysCollision</c> drives, or null for no
    /// collision lumps: stock's <c>physcollision == NULL</c>, which writes
    /// neither LUMP_PHYSCOLLIDE nor LUMP_PHYSDISP (<c>ivp.cpp:1510</c>).
    /// </summary>
    /// <remarks>
    /// Owned by the host, not the compile: the native
    /// <see cref="VPhysicsCollisionCooker"/> is one per process and every
    /// compile in it shares it. Any <see cref="ICollisionCooker"/> works
    /// here, including a managed one.
    /// </remarks>
    public ICollisionCooker? CollisionCooker { get; set; }

    /// <summary>
    /// Where the compile reports each stage as it starts, or null for nowhere.
    /// </summary>
    /// <remarks>
    /// One report per stage transition (<c>Done</c> and <c>Total</c> are
    /// zero), from whichever thread runs the stage, with a stage name that is
    /// a literal: <c>vbsp.world.tree</c>, <c>vbsp.write</c>,
    /// <c>vbsp.ext.StaticProps</c> and so on. A host turns the stream into a
    /// per-stage wall clock (the Phase 5 benches); nothing in
    /// the compile reads it back, so it cannot change the output.
    /// </remarks>
    public IProgress<CompileProgress>? Progress { get; init; }

    /// <summary>
    /// How many threads the compile's parallel stages may use (plan 3p):
    /// <c>-threads</c>, mapped by the host.
    /// </summary>
    /// <remarks>
    /// Stock vbsp is serial whatever it is told (<c>vbsp.cpp:1302</c>). Here
    /// the parallel stages compute into index-addressed slots and commit in
    /// stock's order, so every degree, 1 included, writes the same bytes; this
    /// only decides how many cores that takes.
    /// </remarks>
    public CompileParallelism Parallelism { get; set; } = CompileParallelism.Default;

    /// <summary>
    /// The per-model cooked-collision cache, or null (plan_maptools.md 10a).
    /// Owned by the host like the cooker; a hit replays one model's collision
    /// bytes instead of cooking them.
    /// </summary>
    public ICollisionModelCache? CollisionModelCache { get; set; }
}
