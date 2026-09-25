using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Phys;

namespace SourceSharp.MapTools.Bsp.Collision;

/// <summary>
/// A static prop model's cooked hull: what <c>GetCollisionModel</c>
/// Caches per model.
/// </summary>
/// <param name="ModelName">The name, normalised as the cache keys it.</param>
/// <param name="Blob">The cooked collide, or null: "Bad geometry" or a model that did not load, and the prop is dropped.</param>
public sealed record StaticPropHull(string ModelName, byte[]? Blob);

/// <summary>
/// The collision half of vbsp's static props:
/// cook a model's hull, and find the leaves a placed prop touches, for the
/// sprp leaf lists. The prop lane emits the lump; this is the part that needs
/// the cooker.
/// </summary>
/// <remarks>
/// <para>
/// HOOK for the static-prop emitter: per unique model, call
/// <see cref="StaticPropHullCache.GetOrCookAsync"/> with the model's meshes'
/// vertex positions (model space, in bodypart / model / mesh order, as
/// <c>ComputeConvexHull( studiohdr_t* )</c> walks them); per prop, call
/// <see cref="ComputeStaticPropLeavesAsync"/> with the BSP's nodes, planes and
/// leaves. A prop whose hull is null, or that touches no leaf, is not
/// emitted("Static prop %s outside the map").
/// </para>
/// <para>
/// The hull is kept as BYTES, not a live collide: a native handle must not
/// outlive its cooker callback, and re-reading 1-2 KB per prop costs nothing
/// next to the leaf traces.
/// </para>
/// </remarks>
public static class StaticPropCollision
{
    /// <summary>
    /// The cache key: lower case, back slashes forward.
    /// </summary>
    /// <param name="modelName">The model name as the entity spells it.</param>
    /// <returns>The normalised name.</returns>
    public static string NormalizeModelName(string modelName)
    {
        ArgumentNullException.ThrowIfNull(modelName);
        return string.Create(modelName.Length, modelName, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                span[i] = c == '\\' ? '/' : c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            }
        });
    }

    /// <summary>
    /// <c>ComputeConvexHull(studiohdr_t*)</c>: one
    /// <c>ConvexFromVerts</c> per mesh, then one collide of them all.
    /// </summary>
    /// <param name="cooker">The cooker.</param>
    /// <param name="modelName">The model.</param>
    /// <param name="meshes">Each mesh's vertex positions, in model space.</param>
    /// <param name="cancellationToken">Cancels before the cook.</param>
    /// <returns>The hull; its blob is null when no mesh gave a convex.</returns>
    public static Task<StaticPropHull> CookHullAsync(
        ICollisionCooker cooker,
        string modelName,
        IReadOnlyList<Vec3[]> meshes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cooker);
        ArgumentNullException.ThrowIfNull(modelName);
        ArgumentNullException.ThrowIfNull(meshes);
        string name = NormalizeModelName(modelName);

        return cooker.RunAsync(
            s =>
            {
                // A null convex stays in the list, as stock's does; vphysics
                // Skips it.
                ConvexHandle[] hulls = [.. meshes.Select(m => s.ConvexFromVerts(m))];
                CollideHandle collide = s.ConvertConvexToCollide(hulls);
                if (collide.IsNull)
                {
                    return new StaticPropHull(name, null);
                }

                byte[] blob = s.CollideWrite(collide);
                s.DestroyCollide(collide);
                return new StaticPropHull(name, blob);
            },
            cancellationToken);
    }

    /// <summary>
    /// <c>ComputeStaticPropLeaves</c>: the non-solid
    /// leaves the placed hull really overlaps, in tree order.
    /// </summary>
    /// <param name="cooker">The cooker.</param>
    /// <param name="hull">The model's hull (a null blob touches nothing).</param>
    /// <param name="origin">The prop's origin.</param>
    /// <param name="angles">The prop's angles (pitch, yaw, roll).</param>
    /// <param name="nodes">LUMP_NODES; the walk starts at node 0.</param>
    /// <param name="planes">LUMP_PLANES.</param>
    /// <param name="leafs">LUMP_LEAFS.</param>
    /// <param name="cancellationToken">Cancels before the traces.</param>
    /// <returns>The leaf indices.</returns>
    public static Task<IReadOnlyList<ushort>> ComputeStaticPropLeavesAsync(
        ICollisionCooker cooker,
        StaticPropHull hull,
        Vec3 origin,
        Vec3 angles,
        IReadOnlyList<DNode> nodes,
        IReadOnlyList<DPlane> planes,
        IReadOnlyList<DLeaf> leafs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cooker);
        ArgumentNullException.ThrowIfNull(hull);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(planes);
        ArgumentNullException.ThrowIfNull(leafs);

        if (hull.Blob is null)
        {
            return Task.FromResult<IReadOnlyList<ushort>>([]);
        }

        return cooker.RunAsync<IReadOnlyList<ushort>>(
            s =>
            {
                CollideHandle collide = s.UnserializeCollide(hull.Blob, 0);
                try
                {
                    (Vec3 mins, Vec3 maxs) = s.CollideGetAABB(collide, origin, angles);
                    List<ushort> leaves = [];
                    int[] nodeList = new int[1024];
                    Walk(new Walker(s, nodes, planes, leafs, origin, angles, collide, nodeList, leaves), 0, 0, mins, maxs);
                    return leaves;
                }
                finally
                {
                    s.DestroyCollide(collide);
                }
            },
            cancellationToken);
    }

    private readonly record struct Walker(
        ICollisionSession Session,
        IReadOnlyList<DNode> Nodes,
        IReadOnlyList<DPlane> Planes,
        IReadOnlyList<DLeaf> Leafs,
        Vec3 Origin,
        Vec3 Angles,
        CollideHandle Collide,
        int[] NodeList,
        List<ushort> Leaves);

    /// <summary><c>ComputeConvexHullLeaves_R</c>.</summary>
    private static void Walk(Walker w, int node, int depth, Vec3 mins, Vec3 maxs)
    {
        while (node >= 0)
        {
            DNode n = w.Nodes[node];
            DPlane plane = w.Planes[n.PlaneNum];

            // "Arbitrary split plane here"
            float[] cmin = new float[3], cmax = new float[3];
            for (int i = 0; i < 3; i++)
            {
                if (plane.Normal[i] >= 0)
                {
                    cmin[i] = mins[i];
                    cmax[i] = maxs[i];
                }
                else
                {
                    cmin[i] = maxs[i];
                    cmax[i] = mins[i];
                }
            }

            Vec3 cornerMin = new(cmin[0], cmin[1], cmin[2]);
            Vec3 cornerMax = new(cmax[0], cmax[1], cmax[2]);

            if (Vec3.Dot(plane.Normal, cornerMax) <= plane.Dist)
            {
                w.NodeList[depth++] = node;
                node = n.Children[1];
            }
            else if (Vec3.Dot(plane.Normal, cornerMin) >= plane.Dist)
            {
                // In front: the outward normal is the reverse, marked by a negative index.
                w.NodeList[depth++] = -node - 1;
                node = n.Children[0];
            }
            else
            {
                w.NodeList[depth++] = node;
                Walk(w, n.Children[1], depth, mins, maxs);
                w.NodeList[depth - 1] = -node - 1;
                Walk(w, n.Children[0], depth, mins, maxs);
                return;
            }
        }

        // "Never add static props to solid leaves"
        int leaf = -node - 1;
        if ((w.Leafs[leaf].Contents & CollisionContents.Solid) == 0 && TestLeafAgainstCollide(w, depth))
        {
            w.Leaves.Add((ushort)leaf);
        }
    }

    /// <summary><c>TestLeafAgainstCollide</c>.</summary>
    private static bool TestLeafAgainstCollide(Walker w, int depth)
    {
        // The node list, deepest first, as outward planes.
        CollisionPlane[] planes = new CollisionPlane[depth];
        int idx = 0;
        for (int i = depth; --i >= 0; ++idx)
        {
            int entry = w.NodeList[i];
            int sign = entry < 0 ? -1 : 1;
            int node = sign < 0 ? -entry - 1 : entry;
            DPlane plane = w.Planes[w.Nodes[node].PlaneNum];
            planes[idx] = new CollisionPlane(
                new Vec3(sign * plane.Normal.X, sign * plane.Normal.Y, sign * plane.Normal.Z),
                sign * plane.Dist);
        }

        ConvexHandle convex = w.Session.ConvexFromPlanes(planes, 0.0f);

        // "This should never happen, but if it does, return no collision"
        if (convex.IsNull)
        {
            return false;
        }

        CollideHandle leafCollide = w.Session.ConvertConvexToCollide([convex]);
        CollisionTrace trace = w.Session.TraceCollide(
            Vec3.Zero, Vec3.Zero, leafCollide, Vec3.Zero, w.Collide, w.Origin, w.Angles);
        w.Session.DestroyCollide(leafCollide);
        return trace.StartSolid;
    }
}

/// <summary>
/// <c>s_ModelCollisionCache</c>: one hull per
/// model per compile, a failed load remembered as a null hull so it is not
/// retried. One instance per compile; not shared between compiles, because
/// the content that answers a model name belongs to the compile.
/// </summary>
public sealed class StaticPropHullCache
{
    private readonly Dictionary<string, Task<StaticPropHull>> _hulls = new(StringComparer.Ordinal);
    private readonly ICollisionCooker _cooker;

    /// <summary>Creates a cache over a cooker.</summary>
    /// <param name="cooker">The cooker hulls are made with.</param>
    public StaticPropHullCache(ICollisionCooker cooker)
    {
        ArgumentNullException.ThrowIfNull(cooker);
        _cooker = cooker;
    }

    /// <summary>How many models have an entry.</summary>
    public int Count => _hulls.Count;

    /// <summary>The hull for a model, cooking it the first time it is asked for.</summary>
    /// <param name="modelName">The model, any spelling.</param>
    /// <param name="loadMeshes">
    /// Reads the model's mesh vertices; null when the model does not load
    /// (stock: "Error loading studio model").
    /// </param>
    /// <param name="cancellationToken">Cancels the load and the cook.</param>
    /// <returns>The hull.</returns>
    public Task<StaticPropHull> GetOrCookAsync(
        string modelName,
        Func<CancellationToken, Task<IReadOnlyList<Vec3[]>?>> loadMeshes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelName);
        ArgumentNullException.ThrowIfNull(loadMeshes);
        string name = StaticPropCollision.NormalizeModelName(modelName);

        lock (_hulls)
        {
            if (!_hulls.TryGetValue(name, out Task<StaticPropHull>? hull))
            {
                hull = CookAsync(name, loadMeshes, cancellationToken);
                _hulls[name] = hull;
            }

            return hull;
        }
    }

    private async Task<StaticPropHull> CookAsync(
        string name,
        Func<CancellationToken, Task<IReadOnlyList<Vec3[]>?>> loadMeshes,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Vec3[]>? meshes = await loadMeshes(cancellationToken).ConfigureAwait(false);
        return meshes is null
            ? new StaticPropHull(name, null)
            : await StaticPropCollision.CookHullAsync(_cooker, name, meshes, cancellationToken).ConfigureAwait(false);
    }
}
