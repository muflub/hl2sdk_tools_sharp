using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rad.Ambient;

/// <summary>
/// The planes that bound a leaf, all pointing INWARDS
/// (<c>GetLeafBoundaryPlanes</c>).
/// </summary>
/// <remarks>
/// <para>
/// A BSP leaf is the intersection of the half-spaces named by every split from
/// the root down to it, so the boundary is recovered by walking UP: at each
/// step, the parent's plane, flipped when this subtree is the parent's back
/// child. <see cref="BspParents"/> supplies the upward links.
/// </para>
/// <para>
/// This is a CONSERVATIVE bound and not the leaf's actual convex hull: it
/// includes every ancestor split, including ones that do not touch the leaf at
/// all, which is why <c>GenerateLeafSamplePosition</c> can and does draw points
/// that fail the test. That is the design -- rejection sampling wants a cheap
/// superset -- rather than a defect to fix.
/// </para>
/// </remarks>
public static class LeafBoundaryPlanes
{
    /// <summary>
    /// Gathers one leaf's inward-facing boundary planes.
    /// </summary>
    /// <param name="leaf">The leaf.</param>
    /// <param name="nodes">The map's nodes.</param>
    /// <param name="planes">The map's planes.</param>
    /// <param name="parents">The upward links.</param>
    /// <param name="into">
    /// Receives the planes, leaf-first and root-last. Cleared first.
    /// </param>
    /// <remarks>
    /// Order is leaf-to-root because stock appends as it walks, and it is kept
    /// even though the caller scans the list BACKWARDS and rejects on the first
    /// failure: the boolean answer does not depend on the order, but a reader
    /// comparing the two files should not have to prove that.
    /// </remarks>
    public static void Gather(
        int leaf,
        ReadOnlySpan<DNode> nodes,
        ReadOnlySpan<DPlane> planes,
        BspParents parents,
        List<LeafPlane> into)
    {
        ArgumentNullException.ThrowIfNull(parents);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();

        int nodeIndex = parents.LeafParent(leaf);
        int child = -(leaf + 1);
        while (nodeIndex >= 0)
        {
            ref readonly DNode node = ref nodes[nodeIndex];
            ref readonly DPlane plane = ref planes[node.PlaneNum];

            if (node.Children[0] == child)
            {
                into.Add(new LeafPlane(plane.Normal, plane.Dist));
            }
            else
            {
                into.Add(new LeafPlane(-plane.Normal, -plane.Dist));
            }

            child = nodeIndex;
            nodeIndex = parents.NodeParent(child);
        }
    }
}

/// <summary>One inward-facing boundary plane.</summary>
/// <param name="Normal">The plane normal, pointing into the leaf.</param>
/// <param name="Dist">The plane offset along <paramref name="Normal"/>.</param>
/// <remarks>
/// Stock copies a whole <c>dplane_t</c>, <c>type</c> field and all, into its
/// list. The type is never read afterwards -- the only consumer is one dot
/// product in <c>GenerateLeafSamplePosition</c> -- so it is not carried here.
/// Dropping it is not a behaviour change; negating it, which stock does NOT do
/// on the back-side branch, would have been.
/// </remarks>
public readonly record struct LeafPlane(Vec3 Normal, float Dist);
