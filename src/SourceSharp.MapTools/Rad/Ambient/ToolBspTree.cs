//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rad.Ambient;

/// <summary>
/// <c>CToolBSPTree</c>'s box query, shared by the
/// empty-leaf neighbour search and the displacement leaf lists.
/// </summary>
public static class ToolBspTree
{
    /// <summary><c>TEST_EPSILON</c>.</summary>
    private const float TestEpsilon = 0.03125f;

    /// <summary>
    /// <c>CToolBSPTree::EnumerateLeavesInBox</c>.
    /// </summary>
    /// <param name="nodes">The map's nodes.</param>
    /// <param name="planes">The map's planes.</param>
    /// <param name="mins">The box's low corner.</param>
    /// <param name="maxs">Its high corner.</param>
    /// <param name="into">Receives the leaves, in stock's order.</param>
    /// <remarks>
    /// The descent picks the near corner and the far corner of the box against
    /// each split plane, so a box entirely on one side descends one child. When
    /// it straddles, the FRONT child is walked first and completely, then the
    /// back -- and that order is the enumeration order the callers' tie-breaks
    /// depend on.
    /// </remarks>
    public static void EnumerateLeavesInBox(
        ReadOnlySpan<DNode> nodes, ReadOnlySpan<DPlane> planes, Vec3 mins, Vec3 maxs, List<int> into)
    {
        Span<int> pending = stackalloc int[256];
        int sp = 0;
        pending[sp++] = 0;

        Span<float> cornerMin = stackalloc float[3];
        Span<float> cornerMax = stackalloc float[3];

        while (sp > 0)
        {
            int node = pending[--sp];

            while (node >= 0)
            {
                ref readonly DNode n = ref nodes[node];
                ref readonly DPlane plane = ref planes[n.PlaneNum];

                for (int i = 0; i < 3; i++)
                {
                    if (plane.Normal[i] >= 0)
                    {
                        cornerMin[i] = mins[i];
                        cornerMax[i] = maxs[i];
                    }
                    else
                    {
                        cornerMin[i] = maxs[i];
                        cornerMax[i] = mins[i];
                    }
                }

                float dotMax = (plane.Normal.X * cornerMax[0])
                    + (plane.Normal.Y * cornerMax[1])
                    + (plane.Normal.Z * cornerMax[2]);
                float dotMin = (plane.Normal.X * cornerMin[0])
                    + (plane.Normal.Y * cornerMin[1])
                    + (plane.Normal.Z * cornerMin[2]);

                if (dotMax - plane.Dist <= -TestEpsilon)
                {
                    node = n.Children[1];
                }
                else if (dotMin - plane.Dist >= TestEpsilon)
                {
                    node = n.Children[0];
                }
                else
                {
                    // Front child first and completely, then the back.
                    if (sp >= pending.Length)
                    {
                        throw new InvalidOperationException(
                            "EnumerateLeavesInBox went deeper than 256 pending splits; the tree is malformed");
                    }

                    pending[sp++] = n.Children[1];
                    node = n.Children[0];
                }
            }

            into.Add(-node - 1);
        }
    }
}
