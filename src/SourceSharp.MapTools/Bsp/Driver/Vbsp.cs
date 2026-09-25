using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp.MaterialPatch;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Phys;

namespace SourceSharp.MapTools.Bsp.Driver;

/// <summary>What one vbsp compile produced.</summary>
/// <remarks>
/// Everything is in memory. Writing <c>.bsp</c>, <c>.prt</c> or <c>.lin</c>
/// is the caller's business (<see cref="BspFile.SaveAsync(BspData, Stream, BspWriteMode, CancellationToken)"/>,
/// <see cref="PortalFile.WriteAsync"/>, <see cref="Portals.LeakTrace.Write"/>), so a
/// compile can run with no disk writes at all.
/// </remarks>
public sealed class VbspResult
{
    internal VbspResult(BspData? bsp, PortalFile? portals, LeakReport? leak, IReadOnlyList<CompileDiagnostic> diagnostics)
    {
        Bsp = bsp;
        Portals = portals;
        Leak = leak;
        Diagnostics = diagnostics;
    }

    /// <summary>
    /// The compiled map, or null when <c>-leaktest</c> stopped the compile at
    /// a leak (stock exits without writing, <c>vbsp.cpp:302-306</c>).
    /// </summary>
    public BspData? Bsp { get; }

    /// <summary>
    /// The portal file vvis reads, or null when the map leaked: stock writes
    /// one only for a sealed world (<c>vbsp.cpp:368</c>).
    /// </summary>
    public PortalFile? Portals { get; }

    /// <summary>The leak, when the world is not sealed; <c>.lin</c> is one rendering of it.</summary>
    public LeakReport? Leak { get; }

    /// <summary>Everything the compile had to say about the map, in the order it said it.</summary>
    public IReadOnlyList<CompileDiagnostic> Diagnostics { get; }
}

/// <summary>
/// vbsp: a loaded map in, a BSP out (<c>src/utils/vbsp/vbsp.cpp</c>).
/// </summary>
/// <remarks>
/// <para>
/// The map is loaded separately (<see cref="MapFileReader"/>), so a host can
/// compile a <see cref="MapFile"/> it built in code with no VMF text in
/// between. Options are the context's (<see cref="VbspContext.Options"/>).
/// </para>
/// <para>
/// <b>Serial, as stock is.</b> Stock parses <c>-threads</c> and then forces
/// <c>numthreads = 1</c> (<c>vbsp.cpp:1302</c>); every index this stage
/// assigns — planes, nodes, leaves, faces, edges, vertices — is assigned in
/// one walk. The CPU work runs on the compile's own dedicated worker, not on
/// the caller's thread or the host's thread pool. Parallelising the stages
/// that allow it is plan phase 3p.
/// </para>
/// <para>
/// <b>Bounds first.</b> A host that builds a <see cref="MapFile"/> in code must
/// call <see cref="MapFileReader.TakeBounds"/> before <see cref="CompileAsync(MapFile, VbspContext, CancellationToken)"/>:
/// the world extents feed the vertex hash, so a map whose bounds were never
/// taken dies in <c>HashVec</c> with stock's
/// <c>"HashVec: point outside valid range"</c>
/// (<see cref="Faces.VertexWeld.HashVec"/>, the repo's port of
/// <c>faces.cpp:91</c>) on the first welded point rather than compiling a
/// wrong map.
/// </para>
/// </remarks>
public static class Vbsp
{
    /// <summary>Stage name reported through progress while models are compiled.</summary>
    public const string ModelsStage = "vbsp.ProcessModels";

    /// <summary>Stage name reported through progress while the file is finished.</summary>
    public const string EndStage = "vbsp.EndBSPFile";

    /// <summary>
    /// Compiles a loaded map: <c>ProcessModels</c> then <c>EndBSPFile</c>,
    /// with the post-load fixups stock runs before them.
    /// </summary>
    /// <param name="map">The loaded map. The compile mutates it (entity keys, visible flags).</param>
    /// <param name="context">The compile context the map was loaded with.</param>
    /// <param name="cancellationToken">Cancels the compile.</param>
    /// <returns>The BSP, portal file and leak report.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// The options ask for <c>-onlyents</c> or <c>-onlyprops</c>, which update
    /// an existing BSP: use <see cref="UpdateAsync"/>.
    /// </exception>
    /// <exception cref="MapCompileException">The map cannot be compiled.</exception>
    public static Task<VbspResult> CompileAsync(
        MapFile map,
        VbspContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(context);

        if (context.Options.OnlyEnts || context.Options.OnlyProps)
        {
            throw new ArgumentException(
                "-onlyents / -onlyprops update an existing BSP; call UpdateAsync", nameof(context));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new VbspCompilation(map, context, DefaultExtensions(context)).RunAsync(cancellationToken);
    }

    /// <summary>
    /// The lanes' stages every compile runs: cubemaps, overlays, static and
    /// detail props and the pak (3g); the collision lumps when the context has
    /// a cooker (3h/3i).
    /// </summary>
    /// <param name="context">The compile's context.</param>
    /// <param name="document">The VMF to read water overlays from; null reads the map's own.</param>
    /// <param name="propCooker">The prop hull cooker; null uses the context's.</param>
    /// <returns>The extensions.</returns>
    /// <remarks>
    /// The two never run at the same point
    /// (<see cref="VbspExtensionPoint"/>), so their list order does not
    /// reorder any stock work. The prop hull questions go to the same cooker as
    /// the collision lumps, or to the managed approximation without one.
    /// </remarks>
    internal static IReadOnlyList<IVbspExtension> DefaultExtensions(
        VbspContext context,
        VmfDocument? document = null,
        ICollisionCooker? propCooker = null)
    {
        SurfaceContentExtension surface = new(document, propCooker ?? context.CollisionCooker);
        return context.CollisionCooker is { } cooker ? [surface, new PhysCollisionStage(cooker, context.CollisionModelCache)] : [surface];
    }

    /// <summary>
    /// <c>-onlyents</c> / <c>-onlyprops</c>: re-reads the entities (and the
    /// static props) from the map into an already compiled BSP
    /// (<c>vbsp.cpp:1349-1402</c>).
    /// </summary>
    /// <param name="existing">The previously compiled BSP. It is modified and returned.</param>
    /// <param name="map">The freshly loaded map.</param>
    /// <param name="context">Its context; exactly one of the two options must be set.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns>The updated BSP.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">Neither option is set.</exception>
    public static Task<BspData> UpdateAsync(
        BspData existing,
        MapFile map,
        VbspContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Options.OnlyEnts && !context.Options.OnlyProps)
        {
            throw new ArgumentException("UpdateAsync needs -onlyents or -onlyprops", nameof(context));
        }

        cancellationToken.ThrowIfCancellationRequested();
        // -onlyprops rewrites the prop lumps; -onlyents keeps the rest of the
        // file, pak included (SurfaceContentExtension's update branch).
        return OnlyEntsUpdate.RunAsync(
            existing, map, context, [new SurfaceContentExtension(null, context.CollisionCooker)], cancellationToken);
    }

    /// <summary>
    /// The compile with the surface content stage given a document to read the
    /// water overlays from, and its own prop-hull cooker.
    /// </summary>
    internal static Task<VbspResult> CompileAsync(
        MapFile map,
        VbspContext context,
        VmfDocument? document,
        ICollisionCooker? propCooker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return new VbspCompilation(map, context, DefaultExtensions(context, document, propCooker)).RunAsync(cancellationToken);
    }

    /// <summary>The compile with other lanes' extensions attached, for the integrator.</summary>
    internal static Task<VbspResult> CompileAsync(
        MapFile map,
        VbspContext context,
        IReadOnlyList<IVbspExtension> extensions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(extensions);
        cancellationToken.ThrowIfCancellationRequested();
        return new VbspCompilation(map, context, [.. DefaultExtensions(context), .. extensions]).RunAsync(cancellationToken);
    }
}
