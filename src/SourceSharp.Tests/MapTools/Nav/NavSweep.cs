//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Nav;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// The direct box sweep the clearance grid must agree with: for one agent
/// size, each voxel is free when the agent's box, with its origin anywhere
/// in the voxel, overlaps no brush of its class (the voxel grown by the box,
/// tested with <see cref="NavBrush.Overlaps"/>). This is version 1's per-agent
/// classification, kept here as the reference the exactness facts compare
/// against, with the same arithmetic for the swept box.
/// </summary>
internal static class NavSweep
{
    /// <summary>Each voxel's freedom for an agent, x fastest.</summary>
    /// <param name="brushes">The brushes; those not solid for the agent's mask are ignored.</param>
    /// <param name="region">The voxels.</param>
    /// <param name="width">The agent's width.</param>
    /// <param name="height">The agent's height.</param>
    /// <param name="mask">The contents the agent collides with.</param>
    /// <returns>True where the agent fits.</returns>
    public static bool[] Classify(IReadOnlyList<NavBrush> brushes, NavRegion region, float width, float height, int mask)
    {
        double s = region.VoxelSize;
        double minX = -width / 2f;
        double maxX = width / 2f;
        double maxZ = height;
        bool[] free = new bool[region.SizeX * region.SizeY * region.SizeZ];
        Array.Fill(free, true);
        foreach (NavBrush brush in brushes)
        {
            if ((brush.Contents & mask) == 0)
            {
                continue;
            }

            (int x0, int x1) = Range(brush.MinX, brush.MaxX, region.OriginX, minX, maxX, s, region.SizeX);
            (int y0, int y1) = Range(brush.MinY, brush.MaxY, region.OriginY, minX, maxX, s, region.SizeY);
            (int z0, int z1) = Range(brush.MinZ, brush.MaxZ, region.OriginZ, 0, maxZ, s, region.SizeZ);
            for (int z = z0; z <= z1; z++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        int at = region.Index(x, y, z);
                        if (free[at] && brush.Overlaps(Swept(region, x, y, z, minX, maxX, maxZ)))
                        {
                            free[at] = false;
                        }
                    }
                }
            }
        }

        return free;
    }

    /// <summary>The swept box of a voxel, as version 1's voxeliser formed it.</summary>
    public static NavBox Swept(NavRegion region, int x, int y, int z, double minX, double maxX, double maxZ)
    {
        double s = region.VoxelSize;
        return new NavBox(
            region.OriginX + (x * s) + minX,
            region.OriginY + (y * s) + minX,
            region.OriginZ + (z * s),
            region.OriginX + ((x + 1) * s) + maxX,
            region.OriginY + ((y + 1) * s) + maxX,
            region.OriginZ + ((z + 1) * s) + maxZ);
    }

    /// <summary>Whether a grid's record for a class lets an agent fit in a voxel: the builder's answer the facts compare.</summary>
    public static bool GridFits(NavGrid grid, IReadOnlyList<NavBrush> overhang, int x, int y, int z, float width, float height, int mask) =>
        RoomNavBuilder.Fits(grid, overhang, x, y, z, new NavAgentSpec("probe", width, height, mask));

    private static (int Lo, int Hi) Range(double lo, double hi, double origin, double agentMin, double agentMax, double s, int size)
    {
        int first = (int)Math.Max(0, Math.Floor((lo - origin - agentMax) / s) - 1);
        int last = (int)Math.Min(size - 1, Math.Floor((hi - origin - agentMin) / s) + 1);
        return (first, last);
    }
}
