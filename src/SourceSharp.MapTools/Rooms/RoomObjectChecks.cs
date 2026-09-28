//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;

using SourceSharp.MapTools.Vis;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// The consistency a room's vis must have with its own compile before the
/// linker shifts either into the level's cluster space.
/// </summary>
/// <remarks>
/// <para>
/// The linker numbers room <c>r</c>'s cluster <c>c</c> as
/// <c>clusterBase_r + c</c>, with <c>clusterBase_r</c> the sum of the earlier
/// rooms' <see cref="VisResult.ClusterCount"/>. That is only a numbering if
/// every leaf's cluster is below its own room's count and every row names
/// only clusters below it: a leaf cluster of 7 in a room of 5 clusters, or a
/// set padding bit at 5, lands in the NEXT room's range, where it claims
/// visibility the level does not have and, worse, takes it from the cluster
/// it aliases.
/// </para>
/// <para>
/// Both the <c>.room</c> reader and the linker run the check: the reader
/// because the file is untrusted input, and the linker because a
/// <see cref="RoomObject"/> can be built in code without either.
/// </para>
/// </remarks>
internal static class RoomObjectChecks
{
    /// <summary>Refuses a vis that does not describe the compile it came with.</summary>
    /// <param name="name">The room's name, for the message.</param>
    /// <param name="bsp">The room's compile.</param>
    /// <param name="vis">The room's vis.</param>
    /// <exception cref="LinkException">The row size, a row's length, a padding bit or a leaf cluster is out of range.</exception>
    public static void CheckVis(string name, BspData bsp, VisResult vis)
    {
        int clusters = vis.ClusterCount;
        if (clusters <= 0)
        {
            throw new LinkException($"room {name}'s vis has {clusters} clusters; a linkable room has at least one.");
        }

        int rowBytes = (clusters + 7) >> 3;
        if (vis.RowBytes != rowBytes)
        {
            throw new LinkException(
                $"room {name}'s vis rows are {vis.RowBytes} bytes; {clusters} clusters need {rowBytes}.");
        }

        CheckRows(name, "PVS", vis.PvsBytes, clusters, rowBytes);
        CheckRows(name, "PAS", vis.PasBytes, clusters, rowBytes);

        ReadOnlySpan<DLeaf> leaves = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]);
        for (int i = 0; i < leaves.Length; i++)
        {
            if (leaves[i].Cluster >= clusters)
            {
                throw new LinkException(
                    $"room {name}'s leaf {i} is in cluster {leaves[i].Cluster}, but its vis has {clusters} clusters.");
            }
        }
    }

    private static void CheckRows(string name, string what, ReadOnlySpan<byte> rows, int clusters, int rowBytes)
    {
        if (rows.Length != clusters * rowBytes)
        {
            throw new LinkException(
                $"room {name}'s {what} rows hold {rows.Length} bytes; {clusters} rows of {rowBytes} need {clusters * rowBytes}.");
        }

        // The bits past the last cluster in each row's final byte must be
        // clear: they are cluster numbers this room does not have.
        int used = clusters & 7;
        if (used == 0)
        {
            return;
        }

        byte padding = (byte)(0xFF << used);
        for (int c = 0; c < clusters; c++)
        {
            if ((rows[(c * rowBytes) + rowBytes - 1] & padding) != 0)
            {
                throw new LinkException(
                    $"room {name}'s {what} row {c} sets a padding bit past its {clusters} clusters.");
            }
        }
    }
}
