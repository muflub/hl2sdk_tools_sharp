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

    /// <inheritdoc/>
    /// <remarks>
    /// Runs <paramref name="work"/> on the thread pool against a fresh
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

        return Task.Run(() => work(OpenSession()), cancellationToken);
    }

    /// <summary>A session on the calling thread (what <see cref="RunAsync{T}"/> hands its work).</summary>
    /// <returns>The session.</returns>
    public ICollisionSession OpenSession() =>
        new ManagedCollisionSession(Build(), _surfaceProps) { FixPolysoupMaterialWalk = _fixPolysoupMaterialWalk };

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
