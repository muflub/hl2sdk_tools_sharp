//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Nav;

namespace SourceSharp.MapTools.Nav;

/// <summary>
/// A clearance record as data: its static staircase, its dynamic corners
/// and its overhanging brushes; decoded from and encoded to the canonical
/// bytes (<see cref="Nav3dClearance"/>).
/// </summary>
/// <param name="Corners">The static staircase: widths rising, tops falling.</param>
/// <param name="Dynamics">The dynamic corners, in obstacle then width order.</param>
/// <param name="Brushes">The overhanging brushes, ascending.</param>
/// <remarks>
/// The link works on records in two ways a compile does not: it merges the
/// records of two capped doors that change one voxel (<see cref="Merge"/>),
/// and it renumbers a placed room's obstacles and brushes into the level's
/// tables (<see cref="Remap"/>). Both keep the canonical form, so a record
/// merged at link is the record the voxel would have had built whole.
/// </remarks>
public sealed record NavRecord(IReadOnlyList<Nav3dCorner> Corners, IReadOnlyList<Nav3dDynamicCorner> Dynamics, IReadOnlyList<int> Brushes)
{
    /// <summary>The record that blocks everything.</summary>
    public static NavRecord Blocked => new([new Nav3dCorner(float.NegativeInfinity, float.NegativeInfinity)], [], []);

    /// <summary>Whether the record blocks even a point.</summary>
    public bool IsBlocked => Corners.Count > 0 && float.IsNegativeInfinity(Corners[0].Width) && float.IsNegativeInfinity(Corners[0].Top);

    /// <summary>Reads a record.</summary>
    /// <param name="record">Its bytes.</param>
    /// <returns>The record.</returns>
    public static NavRecord Decode(ReadOnlySpan<byte> record)
    {
        Nav3dCorner[] corners = new Nav3dCorner[Nav3dClearance.StaticCount(record)];
        for (int i = 0; i < corners.Length; i++)
        {
            corners[i] = Nav3dClearance.Corner(record, i);
        }

        Nav3dDynamicCorner[] dynamics = new Nav3dDynamicCorner[Nav3dClearance.DynamicCount(record)];
        for (int i = 0; i < dynamics.Length; i++)
        {
            dynamics[i] = Nav3dClearance.Dynamic(record, i);
        }

        int[] brushes = new int[Nav3dClearance.BrushCount(record)];
        for (int i = 0; i < brushes.Length; i++)
        {
            brushes[i] = Nav3dClearance.Brush(record, i);
        }

        return new NavRecord(corners, dynamics, brushes);
    }

    /// <summary>The record's canonical bytes.</summary>
    /// <returns>The bytes.</returns>
    public byte[] Encode() => Nav3dClearance.Encode([.. Corners], [.. Dynamics], [.. Brushes]);

    /// <summary>
    /// The record of a voxel two capped doors both change: what the voxel's
    /// record is with both doors' solids. Capping only adds solids, so the
    /// answer is the union of what each record lists, pruned again: the
    /// staircase of both staircases' corners, each obstacle's corners pruned
    /// against it, and both brush lists.
    /// </summary>
    /// <param name="a">One record of the voxel.</param>
    /// <param name="b">The other.</param>
    /// <returns>The merged record.</returns>
    /// <remarks>
    /// Pruning is exact for the corners (the staircase of a union is the
    /// staircase of the two staircases). A brush one record lists is kept
    /// even when the other's corners would now cover it: listing an
    /// overhanging brush a corner covers never changes an answer, it only
    /// costs a test.
    /// </remarks>
    public static NavRecord Merge(NavRecord a, NavRecord b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        List<Nav3dCorner> stair = Staircase(a.Corners.Concat(b.Corners));
        if (stair.Count > 0 && float.IsNegativeInfinity(stair[0].Width) && float.IsNegativeInfinity(stair[0].Top))
        {
            return Blocked;
        }

        List<Nav3dDynamicCorner> dynamics = [];
        foreach (IGrouping<int, Nav3dDynamicCorner> obstacle in a.Dynamics.Concat(b.Dynamics).GroupBy(d => d.Obstacle).OrderBy(g => g.Key))
        {
            foreach (Nav3dCorner corner in Staircase(obstacle.Select(d => d.Corner)))
            {
                if (!NavClearanceBuilder.Dominated(stair, corner))
                {
                    dynamics.Add(new Nav3dDynamicCorner(obstacle.Key, corner));
                }
            }
        }

        return new NavRecord(stair, dynamics, [.. a.Brushes.Concat(b.Brushes).Distinct().Order()]);
    }

    /// <summary>The record with its obstacle and brush indices moved into a level's tables.</summary>
    /// <param name="obstacleBase">The level index of the room's first obstacle.</param>
    /// <param name="brushBase">The level index of the room's first brush.</param>
    /// <returns>The renumbered record.</returns>
    public NavRecord Remap(int obstacleBase, int brushBase) => new(
        Corners,
        [.. Dynamics.Select(d => d with { Obstacle = d.Obstacle + obstacleBase })],
        [.. Brushes.Select(b => b + brushBase)]);

    /// <summary>The corners no other corner dominates, widths rising and tops falling.</summary>
    internal static List<Nav3dCorner> Staircase(IEnumerable<Nav3dCorner> corners)
    {
        List<Nav3dCorner> stair = [];
        foreach (Nav3dCorner corner in corners.OrderBy(c => c.Width).ThenBy(c => c.Top))
        {
            if (stair.Count > 0 && !(corner.Top < stair[^1].Top))
            {
                continue;
            }

            if (stair.Count > 0 && stair[^1].Width == corner.Width)
            {
                stair[^1] = corner;
                continue;
            }

            stair.Add(corner);
        }

        return stair;
    }
}
