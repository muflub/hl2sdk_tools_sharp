//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Phys.Managed;

/// <summary>
/// A second <see cref="ICollisionCooker"/>: IVP's collision cooker reimplemented in managed code,
/// so a compile needs no <c>vphysics.so</c> and cooks on every thread at once.
/// </summary>
/// <remarks>
/// <para>
/// One algorithm with precision as the knob. Under
/// <see cref="CompliancePolicy.Correct"/> it computes what TF2's <c>vphysics.so</c> computes: IVP
/// with <c>IVP_DOUBLE = double</c>, in the evaluation order GCC emitted, plain IEEE and therefore
/// identical on every CPU. Under <see cref="CompliancePolicy.Stock"/> it computes what the stock
/// <c>vphysics.so</c> computes: <c>IVP_DOUBLE = float</c> with the <c>rsqrtss</c>/<c>rsqrtps</c>
/// estimate, which is CPU-dependent (<see cref="StockQuirk.CollisionCookerSinglePrecision"/>).
/// </para>
/// <para>
/// Qhull is a port of qhull 2.6 (Qhull licence); the IVP surface, ledge-tree and
/// Ledge-solver behaviour is reproduced expression by expression from the reference
/// Builds, every floating-point expression grouped as each build evaluates it.
/// </para>
/// <para>
/// Thread-safe and deterministic by construction: no static state, a private scratch context per
/// thread, and nothing that depends on addresses. Any number of threads may call at once and get
/// the same bytes as one thread would.
/// </para>
/// </remarks>
public sealed class ManagedCollisionCooker : ICollisionCooker
{
    private readonly bool _double;
    private readonly bool _fixPolysoupMaterialWalk;
    private readonly ThreadLocal<IvpCookContext> _contexts;
    private readonly LockedSurfaceProps _surfaceProps = new(new SurfacePropertyTable());

    private ManagedCollisionCooker(bool useDouble, bool skipZeroLengthEdges, bool fixPolysoupMaterialWalk)
    {
        _double = useDouble;
        _fixPolysoupMaterialWalk = fixPolysoupMaterialWalk;
        _contexts = new ThreadLocal<IvpCookContext>(
            () => new IvpCookContext(new Qhull.QhullRunner()) { SkipZeroLengthInertiaEdges = skipZeroLengthEdges },
            trackAllValues: false);
        CookerIdentity = useDouble
            ? "managed-ivp " + CorrectPrecision.Name + " (double-precision reference arithmetic)"
            : "managed-ivp " + StockPrecision.Name + " (float-precision reference arithmetic)";
    }

    /// <summary>
    /// Creates a cooker whose arithmetic follows <paramref name="compliance"/>: TF2's double
    /// precision unless <see cref="StockQuirk.CollisionCookerSinglePrecision"/> is emulated.
    /// </summary>
    /// <param name="compliance">The compile's compliance.</param>
    /// <returns>The cooker.</returns>
    public static ManagedCollisionCooker Create(ComplianceOptions compliance)
    {
        ArgumentNullException.ThrowIfNull(compliance);
        return new ManagedCollisionCooker(
            !compliance.Emulates(StockQuirk.CollisionCookerSinglePrecision),
            !compliance.Emulates(StockQuirk.CollisionInertiaZeroLengthEdge),
            !compliance.Emulates(StockQuirk.CollisionPolysoupMaterialOverrun));
    }

    /// <inheritdoc/>
    public string CookerIdentity { get; }

    /// <summary>True when this cooker computes in TF2's double precision.</summary>
    public bool IsDoublePrecision => _double;

    /// <summary>
    /// Where <see cref="RunAsync"/> runs its cooks: the thread pool by default,
    /// or a compile's <see cref="SourceSharp.MapTools.Parallel.CompilePool.Scheduler"/> so that cooking
    /// counts against the same <c>-threads</c> budget as every other stage.
    /// </summary>
    public TaskScheduler Scheduler { get; set; } = TaskScheduler.Default;

    /// <inheritdoc/>
    /// <remarks>
    /// Runs <paramref name="work"/> on <see cref="Scheduler"/> against a fresh
    /// <see cref="ManagedCollisionSession"/>; calls may run concurrently, each with its own
    /// handles and its thread's scratch.
    /// </remarks>
    public Task<T> RunAsync<T>(Func<ICollisionSession, T> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        // Task.Run's own options, on the chosen scheduler.
        return Task.Factory.StartNew(
            () => work(OpenSession()),
            cancellationToken,
            TaskCreationOptions.DenyChildAttach,
            Scheduler);
    }

    /// <summary>A session on the calling thread (what <see cref="RunAsync{T}"/> hands its work).</summary>
    /// <returns>The session.</returns>
    /// <remarks>
    /// The session can also build brush convexes on several threads at once
    /// (<see cref="IConcurrentConvexSession"/>): its workers take their builders from this
    /// cooker's per-thread scratch and queue on <see cref="Scheduler"/>, so they count against the
    /// same thread budget as the cooks themselves.
    /// </remarks>
    public ICollisionSession OpenSession() =>
        new ManagedCollisionSession(Build(), _surfaceProps)
        {
            FixPolysoupMaterialWalk = _fixPolysoupMaterialWalk,
            WorkerBuilds = Build,
            WorkerScheduler = Scheduler,
        };

    private IIvpBuild Build()
    {
        IvpCookContext context = _contexts.Value!;
        return _double
            ? new IvpBuild<double, CorrectPrecision>(context)
            : new IvpBuild<float, StockPrecision>(context);
    }

    /// <summary>
    /// <c>ConvexFromPlanes</c> + <c>ConvertConvexToCollide</c> + <c>CollideWrite</c> for one convex,
    /// synchronously on the calling thread.
    /// </summary>
    /// <param name="planes">Outward HL planes.</param>
    /// <param name="mergeDistance">Point merge distance in HL units.</param>
    /// <returns>The VPHY blob, or null when IVP builds nothing.</returns>
    public byte[]? CookPlanes(ReadOnlySpan<(float X, float Y, float Z, float Distance)> planes, float mergeDistance)
    {
        IIvpBuild build = Build();
        IvpCompactLedge? ledge = build.ConvexFromPlanes(planes, mergeDistance);
        return ledge is null ? null : Serialize(build.Compile([ledge], false));
    }

    /// <summary>
    /// <c>ConvexFromVerts</c> + <c>ConvertConvexToCollide</c> + <c>CollideWrite</c> for one convex.
    /// </summary>
    /// <param name="points">HL points.</param>
    /// <returns>The VPHY blob, or null.</returns>
    public byte[]? CookVerts(ReadOnlySpan<(float X, float Y, float Z)> points)
    {
        IIvpBuild build = Build();
        IvpCompactLedge? ledge = build.ConvexFromVerts(points);
        return ledge is null ? null : Serialize(build.Compile([ledge], false));
    }

    /// <summary>
    /// <c>ConvertConvexToCollide</c> + <c>CollideWrite</c> over ledges that
    /// already exist: one static compact surface built from convexes taken out
    /// of other surfaces, synchronously on the calling thread.
    /// </summary>
    /// <param name="ledges">The convexes; the compile takes them, as stock's conversion frees its input.</param>
    /// <returns>The VPHY blob, or null when IVP builds nothing.</returns>
    /// <remarks>
    /// The room linker's collision merge: every room's world ledges, moved to
    /// their placement, rebuilt into one surface so the ledge tree, the
    /// surface's bounding radius and its mass properties describe the level
    /// rather than any one room. The ledges themselves are not re-cooked, so a
    /// linked room collides with exactly the convexes its own compile made.
    /// </remarks>
    internal byte[]? CompileLedges(List<IvpCompactLedge> ledges)
    {
        ArgumentNullException.ThrowIfNull(ledges);
        return ledges.Count == 0 ? null : Serialize(Build().Compile(ledges, false));
    }

    private static byte[]? Serialize(byte[]? surface) =>
        surface is null ? null : VphyWriter.Serialize(surface, (1f, 1f, 1f));

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _contexts.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// <see cref="ISurfacePropertySession"/> over a managed <see cref="SurfacePropertyTable"/>, locked
/// because concurrent sessions share it (as they share the native binding's one table).
/// </summary>
internal sealed class LockedSurfaceProps(SurfacePropertyTable table) : ISurfacePropertySession
{
    private readonly Lock _gate = new();

    /// <inheritdoc/>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return table.Count;
            }
        }
    }

    /// <inheritdoc/>
    public int ParseSurfaceData(string fileName, string text)
    {
        lock (_gate)
        {
            return table.ParseSurfaceData(fileName, text);
        }
    }

    /// <inheritdoc/>
    public int GetSurfaceIndex(string name)
    {
        lock (_gate)
        {
            return table.GetSurfaceIndex(name);
        }
    }

    /// <inheritdoc/>
    public SurfacePhysics GetPhysicsProperties(int index)
    {
        lock (_gate)
        {
            return table.GetPhysicsProperties(index);
        }
    }

    /// <inheritdoc/>
    public string? GetPropName(int index)
    {
        lock (_gate)
        {
            return table.GetPropName(index);
        }
    }
}
