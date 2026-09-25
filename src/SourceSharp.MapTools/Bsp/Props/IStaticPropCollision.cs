using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Bsp.Props;

/// <summary>
/// The four physics-library calls the static prop emitter makes, as a seam
/// The hull build, its bounding box, and the
/// leaf-against-hull test.
/// </summary>
/// <remarks>
/// <para>
/// Stock asks <c>vphysics</c>: <c>ConvexFromVerts</c> per mesh and
/// <c>ConvertConvexToCollide</c>, <c>CollideGetAABB</c>
/// And <c>ConvexFromPlanes</c> + <c>TraceCollide</c> with a
/// zero-length sweep, reading <c>startsolid</c>. Which leaves
/// a prop is listed in follows from those answers, so the answers decide
/// LUMP_GAME_LUMP's leaf list.
/// </para>
/// <para>
/// Lane 3h owns the binding to the real library; it implements this. The
/// managed <see cref="ManagedStaticPropCollision"/> answers the same questions
/// from exact convex geometry and is what the unit tier and a host with no
/// <c>vphysics.so</c> use.
/// </para>
/// </remarks>
public interface IStaticPropCollision
{
    /// <summary>
    /// <c>ComputeConvexHull(studiohdr_t*)</c>: one convex per mesh, merged into
    /// one collision model.
    /// </summary>
    /// <param name="meshes">Each mesh's vertex positions, model space.</param>
    /// <param name="cancellationToken">Cancels before the build.</param>
 /// <returns>The hull, or null for "Bad geometry".</returns>
    ValueTask<IStaticPropHull?> BuildHullAsync(
        IReadOnlyList<Vec3[]> meshes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The same for a named model, which a cooker that caches per model
    /// (<c>s_ModelCollisionCache</c>) keys on.
    /// </summary>
    /// <param name="modelName">The model, as the entity spells it.</param>
    /// <param name="meshes">Each mesh's vertex positions, model space.</param>
    /// <param name="cancellationToken">Cancels before the build.</param>
    /// <returns>The hull, or null for "Bad geometry".</returns>
    ValueTask<IStaticPropHull?> BuildHullAsync(
        string modelName,
        IReadOnlyList<Vec3[]> meshes,
        CancellationToken cancellationToken = default) => BuildHullAsync(meshes, cancellationToken);
}

/// <summary>
/// A hull that lists its own leaves in one query, rather than one
/// <see cref="IStaticPropHull.IntersectsAsync"/> per leaf: the cooked hull,
/// whose whole walk runs on the cooker thread
/// (<see cref="Collision.StaticPropCollision.ComputeStaticPropLeavesAsync"/>).
/// </summary>
public interface IStaticPropLeafHull : IStaticPropHull
{
    /// <summary><c>ComputeStaticPropLeaves</c>.</summary>
    /// <param name="tree">The written tree; its <see cref="BspTreeView.Leafs"/> must be set.</param>
    /// <param name="origin">The prop origin.</param>
    /// <param name="angles">The prop angles.</param>
    /// <param name="cancellationToken">Cancels before the walk.</param>
    /// <returns>The leaf indices, in walk order.</returns>
    Task<IReadOnlyList<ushort>> ComputeLeavesAsync(
        BspTreeView tree, Vec3 origin, Vec3 angles, CancellationToken cancellationToken = default);
}

/// <summary>One prop model's cooked collision hull.</summary>
public interface IStaticPropHull
{
    /// <summary>
    /// <c>CollideGetAABB</c>: the world-space box of the hull placed at an
    /// origin and orientation.
    /// </summary>
    /// <param name="origin">The prop origin.</param>
    /// <param name="angles">The prop angles: pitch, yaw, roll in degrees.</param>
    /// <param name="cancellationToken">Cancels before the query.</param>
    /// <returns>The box.</returns>
    ValueTask<(Vec3 Mins, Vec3 Maxs)> GetAabbAsync(Vec3 origin, Vec3 angles, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>TestLeafAgainstCollide</c>: whether the convex region bounded by
    /// <paramref name="planes"/> (each keeping <c>normal·x &lt;= dist</c>)
    /// overlaps the placed hull.
    /// </summary>
    /// <param name="planes">The leaf's bounding planes, in stock's order.</param>
    /// <param name="origin">The prop origin.</param>
    /// <param name="angles">The prop angles.</param>
    /// <param name="cancellationToken">Cancels before the query.</param>
    /// <returns>True when the leaf holds part of the prop.</returns>
    ValueTask<bool> IntersectsAsync(
        ReadOnlyMemory<(Vec3 Normal, float Dist)> planes,
        Vec3 origin,
        Vec3 angles,
        CancellationToken cancellationToken = default);
}
