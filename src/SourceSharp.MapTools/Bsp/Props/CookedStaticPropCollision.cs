//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Phys;

namespace SourceSharp.MapTools.Bsp.Props;

/// <summary>
/// The static prop seam answered by the real physics library through lane
/// 3h's cooker: <see cref="StaticPropHullCache.GetOrCookAsync"/> per model and
/// <see cref="StaticPropCollision.ComputeStaticPropLeavesAsync"/> per prop.
/// </summary>
/// <remarks>
/// This is what makes LUMP_GAME_LUMP's leaf list stock's: the leaves are
/// whatever <c>vphysics</c>'s <c>TraceCollide</c> says, and the managed
/// <see cref="ManagedStaticPropCollision"/> approximates that
/// (measured: 26 of 2,265 props on <c>sdk_ctf_2fort</c> differ).
/// </remarks>
public sealed class CookedStaticPropCollision : IStaticPropCollision
{
    private readonly ICollisionCooker _cooker;
    private readonly StaticPropHullCache _cache;

    /// <summary>A seam over one compile's cooker.</summary>
    /// <param name="cooker">The cooker; not disposed here.</param>
    public CookedStaticPropCollision(ICollisionCooker cooker)
    {
        ArgumentNullException.ThrowIfNull(cooker);
        _cooker = cooker;
        _cache = new StaticPropHullCache(cooker);
    }

    /// <inheritdoc/>
    public async ValueTask<IStaticPropHull?> BuildHullAsync(
        IReadOnlyList<Vec3[]> meshes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        StaticPropHull hull = await StaticPropCollision.CookHullAsync(_cooker, string.Empty, meshes, cancellationToken)
            .ConfigureAwait(false);
        return hull.Blob is null ? null : new CookedHull(_cooker, hull);
    }

    /// <inheritdoc/>
    public async ValueTask<IStaticPropHull?> BuildHullAsync(
        string modelName, IReadOnlyList<Vec3[]> meshes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelName);
        ArgumentNullException.ThrowIfNull(meshes);
        StaticPropHull hull = await _cache
            .GetOrCookAsync(modelName, _ => Task.FromResult<IReadOnlyList<Vec3[]>?>(meshes), cancellationToken)
            .ConfigureAwait(false);
        return hull.Blob is null ? null : new CookedHull(_cooker, hull);
    }

    private sealed class CookedHull(ICollisionCooker cooker, StaticPropHull hull) : IStaticPropLeafHull
    {
        public Task<IReadOnlyList<ushort>> ComputeLeavesAsync(
            BspTreeView tree, Vec3 origin, Vec3 angles, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(tree);
            if (tree.Leafs is null)
            {
                throw new ArgumentException("the cooked leaf walk needs the tree's LUMP_LEAFS", nameof(tree));
            }

            return StaticPropCollision.ComputeStaticPropLeavesAsync(
                cooker, hull, origin, angles, tree.Nodes, tree.Planes, tree.Leafs, cancellationToken);
        }

        public async ValueTask<(Vec3 Mins, Vec3 Maxs)> GetAabbAsync(
            Vec3 origin, Vec3 angles, CancellationToken cancellationToken = default) =>
            await cooker.RunAsync(
                s =>
                {
                    CollideHandle collide = s.UnserializeCollide(hull.Blob, 0);
                    try
                    {
                        return s.CollideGetAABB(collide, origin, angles);
                    }
                    finally
                    {
                        s.DestroyCollide(collide);
                    }
                },
                cancellationToken).ConfigureAwait(false);

        // TestLeafAgainstCollide.
        public async ValueTask<bool> IntersectsAsync(
            ReadOnlyMemory<(Vec3 Normal, float Dist)> planes,
            Vec3 origin,
            Vec3 angles,
            CancellationToken cancellationToken = default)
        {
            CollisionPlane[] outward = new CollisionPlane[planes.Length];
            for (int i = 0; i < outward.Length; i++)
            {
                (Vec3 normal, float dist) = planes.Span[i];
                outward[i] = new CollisionPlane(normal, dist);
            }

            return await cooker.RunAsync(
                s =>
                {
                    ConvexHandle convex = s.ConvexFromPlanes(outward, 0.0f);
                    if (convex.IsNull)
                    {
                        return false;
                    }

                    CollideHandle collide = s.UnserializeCollide(hull.Blob, 0);
                    CollideHandle leaf = s.ConvertConvexToCollide([convex]);
                    try
                    {
                        return s.TraceCollide(Vec3.Zero, Vec3.Zero, leaf, Vec3.Zero, collide, origin, angles).StartSolid;
                    }
                    finally
                    {
                        s.DestroyCollide(leaf);
                        s.DestroyCollide(collide);
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
    }
}
