using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Phys;

/// <summary>
/// A cooked collision model: the bytes that go into the physics lump.
/// </summary>
/// <param name="Bytes">
/// The cooked blob, beginning with the <c>VPHY</c> container header. NOTE that
/// the leading record size is NOT part of this: stock writes it in the caller
/// (<c>ivp.cpp:183-187</c>), so the lump's record framing is the port's job and
/// not the cooker's.
/// </param>
/// <param name="CookerIdentity">
/// Which cooker produced it, precisely enough to tell two builds apart.
/// </param>
public readonly record struct CookedCollide(ReadOnlyMemory<byte> Bytes, string CookerIdentity);

/// <summary>
/// Turns convex geometry into the collision bytes the engine loads.
/// </summary>
/// <remarks>
/// <para>
/// The default implementation drives Valve's own closed <c>vphysics.so</c>
/// through its vtable, which closes the worst risk in this port by
/// construction: the engine performs NO validation of the physics lump
/// (<c>cmodel_bsp.cpp:1017</c> walks the records and hands them straight to
/// <c>VCollideLoad</c>), so a wrong byte is a crash inside closed code rather
/// than an error message. Bytes produced by the same library that will load
/// them cannot be wrong in that way.
/// </para>
/// <para>
/// ONE THREAD, and that is measured rather than cautious. Cooking concurrently
/// without a lock crashes the library: heap corruption, stack smashing and
/// SIGSEGV in every trial at both 8 and 2 threads, and
/// <c>ThreadContextCreate()</c> provides no isolation whatsoever -- it is
/// literally <c>return this;</c>. With one lock, 11,520 cooks completed with no
/// mismatch. So every call here is marshalled onto one dedicated cooker thread,
/// which is stricter than a lock and also covers any thread affinity the closed
/// library may have.
/// </para>
/// <para>
/// THERE IS A WAY ROUND IT, AND IT WAS REJECTED ON VALUE RATHER THAN ON
/// FEASIBILITY. <c>vphysics.so</c> carries no <c>DT_SONAME</c>, so a plain file
/// copy under another name gets its own link map and its own writable data. One
/// copy per thread measured 6.5x at 8 workloads and 13.8x at 24, byte-identical
/// to the serial reference across ~1.32M concurrent cooks, against a control
/// that died 5/5 with the library shared. The corrupted state is vphysics's
/// own: the copies share ONE <c>libtier0</c> -- and so tier0's allocator -- and
/// work anyway.
/// </para>
/// <para>
/// It is not built because the prize is too small to justify the failure mode.
/// A whole map's collision is about 0.1 s single-threaded: <c>dm_lockdown</c>'s
/// 47 solids over 2,227 convexes cook in 78-83 ms, on a stage the incremental
/// cache stores per model anyway. Set against that, the scheme degrades
/// SILENTLY -- a symlink instead of a copy makes glibc deduplicate with no
/// error and returns you to the crashing regime, and a future Valve build that
/// ships a <c>DT_SONAME</c> would do the same. If Phase 3p's profile ever
/// disagrees, build it with file copies rather than <c>dlmopen</c> (which caps
/// at 13 cookers and costs 5.7x the memory), and assert the interface, vtable
/// and resolved code pointers really do differ before trusting the isolation.
/// </para>
/// <para>
/// A consequence for the gates: the same shape cooked by two DIFFERENT builds
/// of <c>vphysics.so</c> does not give the same bytes -- TF2's copy differs
/// from SDK Base 2013's in 15 of a cube's 440 bytes. So any comparison must pin
/// which library both sides loaded and record its BuildID beside the hash.
/// </para>
/// </remarks>
public interface ICollisionCooker : IAsyncDisposable
{
    /// <summary>
    /// Runs one unit of cooking on the cooker thread.
    /// </summary>
    /// <typeparam name="T">
    /// What the work returns. It must not carry a handle out: handles mean
    /// nothing after the callback returns.
    /// </typeparam>
    /// <param name="work">The calls to make, against a session valid only inside it.</param>
    /// <param name="cancellationToken">
    /// Cancels before the work reaches the native library. Work already
    /// running cannot be interrupted; cancellation is observed when it returns.
    /// </param>
    /// <returns>What <paramref name="work"/> returned.</returns>
    Task<T> RunAsync<T>(Func<ICollisionSession, T> work, CancellationToken cancellationToken = default);

    /// <summary>
    /// Identifies the cooker, including the library build it drives.
    /// </summary>
    /// <remarks>
    /// Carries the <c>vphysics.so</c> BuildID and MD5, because two builds of it
    /// cook the same shape differently and a comparison that does not say which
    /// was used is not reproducible.
    /// </remarks>
    string CookerIdentity { get; }
}

/// <summary>Single-solid conveniences over <see cref="ICollisionCooker.RunAsync{T}"/>.</summary>
public static class CollisionCookerExtensions
{
    /// <summary>
    /// Cooks one convex solid described by the planes that bound it:
    /// <c>ConvexFromPlanes</c> then <c>ConvertConvexToCollide</c> then
    /// <c>CollideWrite</c> (<c>ivp.cpp:531</c>, <c>:546</c>, <c>:183-187</c>).
    /// </summary>
    /// <param name="cooker">The cooker.</param>
    /// <param name="planes">Outward-facing bounding planes.</param>
    /// <param name="mergeDistance">The vertex merge distance.</param>
    /// <param name="cancellationToken">Cancels before the native call.</param>
    /// <returns>The cooked bytes, or null when the planes bound nothing.</returns>
    public static Task<CookedCollide?> CookFromPlanesAsync(
        this ICollisionCooker cooker,
        ReadOnlyMemory<CollisionPlane> planes,
        float mergeDistance = 0f,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cooker);
        string identity = cooker.CookerIdentity;
        return cooker.RunAsync(
            session => Finish(session, session.ConvexFromPlanes(planes.Span, mergeDistance), identity),
            cancellationToken);
    }

    /// <summary>
    /// Cooks the convex hull of a point cloud: <c>ConvexFromVerts</c>
    /// (<c>staticprop.cpp:208</c>) then <c>ConvertConvexToCollide</c>.
    /// </summary>
    /// <param name="cooker">The cooker.</param>
    /// <param name="points">The points.</param>
    /// <param name="cancellationToken">Cancels before the native call.</param>
    /// <returns>The cooked bytes, or null when the points span no volume.</returns>
    public static Task<CookedCollide?> CookFromVertsAsync(
        this ICollisionCooker cooker,
        ReadOnlyMemory<Vec3> points,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cooker);
        string identity = cooker.CookerIdentity;
        return cooker.RunAsync(
            session => Finish(session, session.ConvexFromVerts(points.Span), identity),
            cancellationToken);
    }

    private static CookedCollide? Finish(ICollisionSession session, ConvexHandle convex, string identity)
    {
        if (convex.IsNull)
        {
            return null;
        }

        CollideHandle collide = session.ConvertConvexToCollide([convex]);
        if (collide.IsNull)
        {
            return null;
        }

        try
        {
            return new CookedCollide(session.CollideWrite(collide), identity);
        }
        finally
        {
            session.DestroyCollide(collide);
        }
    }
}
