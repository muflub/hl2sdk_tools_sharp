//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Disp;

/// <summary>
/// Turns a displacement's allowed-vertex bit vector into the triangle list the
/// engine would actually draw: <c>TesselateDisplacement</c>.
/// </summary>
/// <remarks>
/// <para>
/// Shared between vbsp and the engine in stock, as a template over a helper
/// class. vbsp's helper(<c>CVBSPTesselateHelper</c>)
/// does two things: it appends indices to a list, and it hands back a single
/// static <c>DispNodeInfo_t</c> for every node because it does not care about
/// per-node counts. This port therefore drops the node-info half entirely
/// rather than writing to a shared dummy, and what is left is the index list.
/// </para>
/// <para>
/// WHAT THE RESULT MEANS: this is the tessellation AFTER
/// <see cref="DispNeighbourFinder.SetupAllowedVerts"/> has removed the
/// vertices a coarser neighbour cannot match. It is therefore a SUBSET of
/// <see cref="CoreDispInfo.TriIndices"/>, which is the full grid, and the
/// vertices it leaves out are exactly the ones
/// <c>SnapRemainingVertsToSurface</c> then flattens onto it.
/// </para>
/// </remarks>
public static class DispTesselator
{
    /// <summary>
    /// Tessellates one displacement under its current allowed-vertex set.
    /// </summary>
    /// <param name="disp">The displacement.</param>
    /// <returns>Three vertex indices per triangle.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="disp"/> is null.</exception>
    public static List<ushort> Tesselate(CoreDispInfo disp)
    {
        ArgumentNullException.ThrowIfNull(disp);

        List<ushort> indices = [];
        Tesselate(disp, disp.PowerInfo.RootNode, 0, indices);
        return indices;
    }

    /// <summary>
    /// <c>TesselateDisplacement_R</c>: recurse into
    /// the live children, then fan the node itself.
    /// </summary>
    private static void Tesselate(
        CoreDispInfo disp, VertIndex nodeIndex, int level, List<ushort> indices)
    {
        PowerInfo info = disp.PowerInfo;

        Span<bool> activeChildren = stackalloc bool[4];

        if (level < info.Power - 1)
        {
            int iNode = info.VertIndexToInt(nodeIndex);

            for (int child = 0; child < 4; child++)
            {
                VertIndex childNode = info.ChildVerts[iNode].Verts[child];

                // A child node whose own centre vertex has been switched off
                // cannot be recursed into: the quadrant is drawn by its parent
                // as part of the fan instead.
                activeChildren[child] =
                    disp.AllowedVertsGet(info.VertIndexToInt(childNode));

                if (activeChildren[child])
                {
                    Tesselate(disp, childNode, level + 1, indices);
                }
            }
        }

        TesselateNode(disp, nodeIndex, level, activeChildren, indices);
    }

    /// <summary>
    /// <c>TesselateDisplacementNode</c>: walk the
    /// node's nine-step winding, emitting a triangle each time two vertices
    /// have accumulated.
    /// </summary>
    /// <remarks>
    /// The winding step that lands on an ACTIVE child breaks the fan rather
    /// than contributing to it — that quadrant already has its own triangles
    /// from the recursion — which is why <c>iCurTriVert</c> is reset to zero
    /// there and not to one.
    /// </remarks>
    private static void TesselateNode(
        CoreDispInfo disp,
        VertIndex nodeIndex,
        int level,
        ReadOnlySpan<bool> activeChildren,
        List<ushort> indices)
    {
        PowerInfo info = disp.PowerInfo;

        int vertInc = 1 << (info.Power - level - 1);

        Span<ushort> temp = stackalloc ushort[2];
        int curTriVert = 0;

        ReadOnlySpan<TesselateVert> winding = PowerInfo.TesselateWinding;

        for (int i = 0; i < winding.Length; i++)
        {
            VertIndex sideVert = nodeIndex.Offset(winding[i].Index, vertInc);

            int vertNode = winding[i].Node;
            if (vertNode != -1 && activeChildren[vertNode])
            {
                if (curTriVert == 2)
                {
                    EndTriangle(info, nodeIndex, temp, indices, ref curTriVert);
                }

                curTriVert = 0;
                continue;
            }

            int bit = info.VertIndexToInt(sideVert);
            if (!disp.AllowedVertsGet(bit))
            {
                continue;
            }

            temp[curTriVert] = (ushort)bit;
            curTriVert++;

            if (curTriVert == 2)
            {
                EndTriangle(info, nodeIndex, temp, indices, ref curTriVert);
            }
        }
    }

    /// <summary>
    /// <c>InternalEndTriangle</c>.
    /// </summary>
    /// <remarks>
    /// The third vertex is always the NODE's own centre, and the second vertex
    /// becomes the first of the next triangle — so the node's triangles are a
    /// fan and not a strip.
    /// </remarks>
    private static void EndTriangle(
        PowerInfo info,
        VertIndex nodeIndex,
        Span<ushort> temp,
        List<ushort> indices,
        ref int curTriVert)
    {
        indices.Add(temp[0]);
        indices.Add(temp[1]);
        indices.Add((ushort)info.VertIndexToInt(nodeIndex));

        temp[0] = temp[1];
        curTriVert = 1;
    }
}
