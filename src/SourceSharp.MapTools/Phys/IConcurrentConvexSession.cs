//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Phys;

/// <summary>
/// An optional capability of an <see cref="ICollisionSession"/>: building many
/// convexes at once, each on a worker with a session of its own, and handing
/// them back to this session in index order.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> A cook is one work item per model, and the world
/// model holds nearly every brush of a map, so without this its brushes'
/// convexes -- most of vbsp's time -- are built one after another on one
/// thread however many the compile has. Each brush's convex depends only on
/// its own planes and the cook's options, so that part can run anywhere; what
/// must stay serial is everything that touches the session's tables and the
/// running sums, and that stays with the caller, in brush order.
/// </para>
/// <para>
/// <b>Why it is optional.</b> A session is single-threaded by contract, and
/// the native library's is so for real: its heap does not survive two threads.
/// A session that does not implement this is driven exactly as before, one
/// brush at a time; only the managed cooker, whose builders keep no shared
/// state, offers it.
/// </para>
/// <para>
/// <b>What the caller relies on.</b> The results are the ones a serial loop
/// over the same delegate on this session would produce, byte for byte: the
/// managed builders' output does not depend on the thread, on the order of
/// earlier builds or on handle values. The handles come back in index order,
/// adopted into this session only after every item has succeeded, so a failure
/// leaves this session exactly as it was.
/// </para>
/// </remarks>
public interface IConcurrentConvexSession
{
    /// <summary>
    /// Runs <paramref name="build"/> for every index below <paramref name="count"/>,
    /// up to <paramref name="maxDegree"/> at once, and adopts the convexes it returns.
    /// </summary>
    /// <param name="count">How many convexes.</param>
    /// <param name="build">
    /// One item: given a worker session and the index, returns a convex of THAT
    /// session (or a null handle). It must not touch this session, and it
    /// destroys any collide it makes along the way; anything else it leaves in
    /// the worker session is dropped with it.
    /// </param>
    /// <param name="maxDegree">The most items at once, the calling thread included.</param>
    /// <param name="cancellationToken">Checked before every item.</param>
    /// <returns>The convexes as handles of this session, index for index.</returns>
    /// <exception cref="OperationCanceledException">Cancelled before every item had started.</exception>
    /// <remarks>
    /// Synchronous: it runs inside a cook, which cannot await. The calling
    /// thread builds items itself and never waits for a helper that has not
    /// started, so it cannot deadlock a pool whose threads are all cooking.
    /// When items fail, the exception of the lowest failing index is thrown,
    /// which is the one a serial loop would have stopped at.
    /// </remarks>
    ConvexHandle[] BuildConvexes(
        int count,
        Func<ICollisionSession, int, ConvexHandle> build,
        int maxDegree,
        CancellationToken cancellationToken);
}
