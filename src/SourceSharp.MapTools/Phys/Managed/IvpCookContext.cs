//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Phys.Managed;

/// <summary>
/// Per-thread scratch for one cook: the qhull runner and the qhull storage it reuses. Holds no
/// state that outlives a call's result, so any number of contexts may cook concurrently.
/// </summary>
/// <remarks>
/// The context is where the cooker's reused storage lives, and so what bounds its lifetime: the
/// cooker keeps one context per thread and drops them all when it is disposed, and nothing
/// else holds one. The storage is never shared between contexts, so two compiles cooking at
/// once never touch the same pool.
/// </remarks>
internal sealed class IvpCookContext
{
    /// <summary>Creates a context.</summary>
    /// <param name="qhull">
    /// The hull builder this context owns. When it is the qhull port's own runner, the
    /// geometric queries (<see cref="Hulls"/>) share its storage, so a thread keeps one pool
    /// rather than two; any other runner (a fact's stand-in) gets a storage of its own beside it.
    /// </param>
    public IvpCookContext(IQhullRunner qhull)
    {
        Qhull = qhull;
        Hulls = qhull is Qhull.QhullRunner runner ? runner.Session : new Qhull.QhullSession();
    }

    /// <summary>The qhull runner (one per context; never shared).</summary>
    public IQhullRunner Qhull { get; }

    /// <summary>
    /// Plain qhull builds for the collide queries (the leaf hulls <see cref="ManagedTrace"/>
    /// tests against), on this context's storage. Builds never overlap on one thread and each
    /// copies its facets out before returning, so they can share storage with <see cref="Qhull"/>.
    /// </summary>
    public Qhull.QhullSession Hulls { get; }

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
