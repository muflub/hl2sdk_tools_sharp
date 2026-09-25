using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Phys;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Compile;

/// <summary>
/// Everything one <see cref="MapCompiler.CompileAsync"/> needs: the map, the
/// game content, the three stages' options, and the seams.
/// </summary>
/// <remarks>
/// <para>
/// Typed options, never <c>argv</c>. A host holding a
/// Hammer command line turns it into these with
/// <see cref="StockArgs.ParseVbsp"/>, <see cref="StockArgs.ParseVvis"/> and
/// <see cref="StockArgs.ParseVrad"/>; that is what <c>ssmap all</c> does.
/// </para>
/// <para>
/// One <see cref="Parallel"/> for the whole chain. Stock's <c>-threads</c> is
/// per tool, and vbsp's is ignored(forces one thread);
/// a chain in one process has one machine to share, so it has one degree.
/// </para>
/// </remarks>
public sealed record CompileRequest
{
    /// <summary>The map.</summary>
    public required MapSource Source { get; init; }

    /// <summary>
    /// The game content every stage reads through: materials and models for
    /// vbsp, <c>lights.rad</c>, <c>&lt;map&gt;.rad</c> and the <c>-lights</c>
    /// file for vrad.
    /// </summary>
    /// <remarks>
    /// Stock's vrad finds <c>&lt;map&gt;.rad</c> BESIDE the map
    /// Outside any search path. A host that wants
    /// that layers the file over its content; <c>ssmap all</c> does.
    /// </remarks>
    public required IContentFileSystem Content { get; init; }

    /// <summary>vbsp's options. <c>-onlyents</c> and <c>-onlyprops</c> are not a chain and are refused.</summary>
    public VbspOptions Vbsp { get; init; } = VbspOptions.Default;

    /// <summary>vvis's options.</summary>
    public VvisOptions Vvis { get; init; } = VvisOptions.Default;

    /// <summary>vrad's options.</summary>
    public VradOptions Vrad { get; init; } = VradOptions.Default;

    /// <summary>How much of the machine vvis and vrad may use (vbsp is serial, as stock's is).</summary>
    public CompileParallelism Parallel { get; init; } = CompileParallelism.Default;

    /// <summary>The ray tracer vrad uses, or null for the managed CPU tracer.</summary>
    public IRayTracer? Tracer { get; init; }

    /// <summary>
    /// The host's GPU-tracer factory (plan_maptools.md 10c, the <c>-gpu</c>
    /// seam), or null for the CPU KD tracer. Mutually informative with
    /// <see cref="Tracer"/>: a host that supplies a tracer outright needs no
    /// factory; a host with <c>-gpu</c> supplies the factory and no tracer,
    /// because the GPU scene needs vrad's own shadow casters. The core
    /// references no GPU code — the factory is where the host (which may)
    /// meets the compile, the same posture as <see cref="Cache"/>.
    /// </summary>
    public Rad.IGpuTracerFactory? TracerFactory { get; init; }

    /// <summary>
    /// The cooker vbsp's collision lumps are made with, or null for a map with
    /// no PHYSCOLLIDE or PHYSDISP: stock's road when <c>vphysics.dll</c> does
    /// Not load.
    /// </summary>
    public ICollisionCooker? CollisionCooker { get; init; }

    /// <summary>What is written, if anything. Defaults to nothing.</summary>
    public CompileOutput Output { get; init; } = CompileOutput.InMemory;

    /// <summary>Where the running commentary goes, or null to keep it only in the result and the <c>.log</c>.</summary>
    public ICompileLog? Log { get; init; }

    /// <summary>The clock stage timings are read from.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    /// The incremental cache's store, or null for no
    /// cache. Owned by the host, like the cooker. Beside-the-map placement is
    /// the host's choice (the plan's ruling Q7 is one <c>&lt;map&gt;.sscache.db</c>
    /// beside the map; <c>ssmap</c> implements that default).
    /// </summary>
    public Cache.ICacheStore? Cache { get; init; }

    /// <summary>Cache posture; <see cref="Cache.CachePolicy.Default"/> when a store is supplied and none given.</summary>
    public Cache.CachePolicy? CachePolicy { get; init; }

    /// <summary>
    /// The host's opaque context tags, folded verbatim into every cache key
    /// (preset name, cooker selection, anything whose change must invalidate).
    /// CONTRACT (the T2 seam): distinct selections MUST produce distinct tag
    /// strings; the chain never interprets them.
    /// </summary>
    public IReadOnlyList<string> ContextTags { get; init; } = [];
}
