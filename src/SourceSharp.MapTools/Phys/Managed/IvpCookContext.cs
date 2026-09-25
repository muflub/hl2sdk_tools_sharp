namespace SourceSharp.MapTools.Phys.Managed;

/// <summary>
/// Per-thread scratch for one cook: the qhull runner. Holds no state that outlives a call's
/// result, so any number of contexts may cook concurrently.
/// </summary>
internal sealed class IvpCookContext
{
    /// <summary>Creates a context.</summary>
    /// <param name="qhull">The hull builder this context owns.</param>
    public IvpCookContext(IQhullRunner qhull) => Qhull = qhull;

    /// <summary>The qhull runner (one per context; never shared).</summary>
    public IQhullRunner Qhull { get; }

    /// <summary>
    /// When true, the inertia integral skips edges of zero length instead of dividing 0 by 0
    /// (<see cref="Options.StockQuirk.CollisionInertiaZeroLengthEdge"/> not emulated).
    /// </summary>
    public bool SkipZeroLengthInertiaEdges { get; init; }

    /// <summary>
    /// Optional diagnostic sink: the builders narrate their decisions here when it is set. Null in
    /// production.
    /// </summary>
    public Action<string>? Trace { get; set; }
}
