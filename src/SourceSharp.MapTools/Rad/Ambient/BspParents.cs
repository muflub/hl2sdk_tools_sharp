using SourceSharp.MapFormats.Bsp.Structs;

namespace SourceSharp.MapTools.Rad.Ambient;

/// <summary>
/// Which node each node and each leaf hangs off (<c>vrad.cpp:146</c>,
/// <c>MakeParents</c>).
/// </summary>
/// <remarks>
/// <para>
/// The BSP file stores children and not parents, and
/// <c>GetLeafBoundaryPlanes</c> needs to walk the other way: from a leaf up to
/// the root, collecting the split plane at every step and flipping it when the
/// walk came up the back side. Stock builds these two arrays once, at load, and
/// so does this.
/// </para>
/// <para>
/// ITERATIVE, where stock recurses. A BSP from vbsp is shallow enough that
/// stock's recursion is safe in practice, but "in practice" is doing real work
/// in that sentence and an explicit stack costs nothing.
/// </para>
/// </remarks>
public sealed class BspParents
{
    /// <summary>The root's parent: the walk stops here.</summary>
    public const int NoParent = -1;

    /// <summary>Per node, its parent node.</summary>
    private readonly int[] _nodeParents;

    /// <summary>Per leaf, the node it hangs off.</summary>
    private readonly int[] _leafParents;

    /// <summary>Walks the tree.</summary>
    /// <param name="nodes">The map's nodes.</param>
    /// <param name="leafCount">How many leaves the map has.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A child index names a node or leaf the map does not have.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The root gets <see cref="NoParent"/>, which is what ends the upward walk
    /// in <c>GetLeafBoundaryPlanes</c>. Leaving it zero (as an earlier draft of
    /// this class did) makes the root its own parent and that walk never
    /// terminates -- it grew its plane list until a test process reached 37 GB.
    /// </para>
    /// <para>
    /// A node or leaf the world tree never reaches -- a brush entity's own
    /// subtree -- keeps parent ZERO, exactly as stock's zero-filled file-scope
    /// arrays do, so such a leaf's boundary planes are the root's back side and
    /// nothing else. Reproduced because those leaves get ambient samples too and
    /// their positions are drawn against that plane list.
    /// </para>
    /// </remarks>
    public BspParents(ReadOnlySpan<DNode> nodes, int leafCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(leafCount);

        _nodeParents = new int[nodes.Length];
        _leafParents = new int[leafCount];
        if (nodes.Length == 0)
        {
            return;
        }

        // vrad.cpp:1806, MakeParents(0, -1).
        _nodeParents[0] = NoParent;

        Stack<int> pending = new();
        pending.Push(0);
        while (pending.Count > 0)
        {
            int nodeNum = pending.Pop();
            ref readonly DNode node = ref nodes[nodeNum];

            for (int i = 0; i < 2; i++)
            {
                int child = node.Children[i];
                if (child < 0)
                {
                    int leaf = -child - 1;
                    ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
                        (uint)leaf, (uint)leafCount, nameof(nodes));
                    _leafParents[leaf] = nodeNum;
                }
                else
                {
                    ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
                        (uint)child, (uint)nodes.Length, nameof(nodes));
                    _nodeParents[child] = nodeNum;
                    pending.Push(child);
                }
            }
        }
    }

    /// <summary>The node a node hangs off, or <see cref="NoParent"/>.</summary>
    /// <param name="node">The node.</param>
    /// <returns>Its parent.</returns>
    public int NodeParent(int node) => _nodeParents[node];

    /// <summary>The node a leaf hangs off, or <see cref="NoParent"/>.</summary>
    /// <param name="leaf">The leaf.</param>
    /// <returns>Its parent node.</returns>
    public int LeafParent(int leaf) => _leafParents[leaf];
}
