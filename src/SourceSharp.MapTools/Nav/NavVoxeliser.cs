//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;

using SourceSharp.MapFormats.Nav;

namespace SourceSharp.MapTools.Nav;

/// <summary>A box of voxels to classify: its low corner, its size in voxels and the voxel's edge.</summary>
/// <param name="OriginX">The low corner's x.</param>
/// <param name="OriginY">The low corner's y.</param>
/// <param name="OriginZ">The low corner's z.</param>
/// <param name="SizeX">Voxels along x.</param>
/// <param name="SizeY">Voxels along y.</param>
/// <param name="SizeZ">Voxels along z.</param>
/// <param name="VoxelSize">A voxel's edge.</param>
public readonly record struct NavRegion(
    double OriginX, double OriginY, double OriginZ, int SizeX, int SizeY, int SizeZ, double VoxelSize);

/// <summary>
/// A classified box of voxels: for each, blocked or free, and a free
/// voxel's contact flags; plus, one voxel beyond the box on every side,
/// whether the agent is blocked there.
/// </summary>
/// <remarks>
/// The ring beyond the box is what lets a room say which of its boundary
/// voxels continue into a neighbour (a door portal) and which end at a
/// wall, without the neighbour in hand.
/// </remarks>
public sealed class NavVoxelGrid
{
    /// <summary>A voxel's code when the agent is blocked there.</summary>
    public const ushort Blocked = 0;

    /// <summary>The bit a free voxel's code carries; its low byte is its <see cref="Nav3dLeafFlags"/>.</summary>
    public const ushort FreeBit = 0x100;

    private readonly ushort[] _cells;
    private readonly bool[] _ring;

    internal NavVoxelGrid(NavRegion region, ushort[] cells, bool[] ring)
    {
        Region = region;
        _cells = cells;
        _ring = ring;
    }

    /// <summary>The box.</summary>
    public NavRegion Region { get; }

    /// <summary>Each voxel's code, x fastest, then y, then z.</summary>
    public ReadOnlySpan<ushort> Cells => _cells;

    /// <summary>One voxel's code.</summary>
    /// <param name="x">Along x, 0 to <see cref="NavRegion.SizeX"/> - 1.</param>
    /// <param name="y">Along y.</param>
    /// <param name="z">Along z.</param>
    /// <returns><see cref="Blocked"/>, or <see cref="FreeBit"/> with the voxel's flags.</returns>
    public ushort this[int x, int y, int z] => _cells[(((z * Region.SizeY) + y) * Region.SizeX) + x];

    /// <summary>Whether the agent is blocked at a voxel of the box or of the ring one voxel beyond it.</summary>
    /// <param name="x">Along x, -1 to <see cref="NavRegion.SizeX"/>.</param>
    /// <param name="y">Along y, -1 to <see cref="NavRegion.SizeY"/>.</param>
    /// <param name="z">Along z, -1 to <see cref="NavRegion.SizeZ"/>.</param>
    /// <returns>Whether it is blocked.</returns>
    public bool IsBlockedWithRing(int x, int y, int z) =>
        _ring[((((z + 1) * (Region.SizeY + 2)) + y + 1) * (Region.SizeX + 2)) + x + 1];
}

/// <summary>
/// Classifies voxels for one agent against a set of brushes: which voxels
/// the agent's origin may occupy anywhere without overlapping a brush it
/// collides with, and what each free voxel touches.
/// </summary>
/// <remarks>
/// <para>
/// <b>Free means free everywhere in the voxel.</b> A voxel is free when the
/// agent's box, with its origin anywhere in the voxel, overlaps no brush:
/// the box swept over the voxel (the voxel grown by the agent's box, which
/// is again a box) overlaps none. This is the Minkowski sum of the solids
/// with the agent's box, tested per voxel exactly
/// (<see cref="NavBrush.Overlaps"/>), and it makes a free voxel a guarantee
/// rather than a likelihood: an agent whose origin is anywhere in it fits.
/// </para>
/// <para>
/// <b>Contact flags.</b> A free voxel looks at its six face neighbours. A
/// blocked horizontal neighbour sets that side's bit. For each brush that
/// blocks a neighbour, the brush's planes that face back against the step
/// and have the voxel's whole swept box on their outside are the surfaces
/// the agent would meet, and each one's normal says what it is: a floor when
/// its z is at least the floor threshold, a ceiling when at most minus it,
/// a wall otherwise. A brush met only across an edge, with no such plane,
/// counts by the step's direction (down a floor, up a ceiling, sideways a
/// wall). So a voxel over a gentle slope is a floor voxel, one over a
/// steep slope a wall voxel, and a voxel on the floor in a corner is a
/// floor and a wall voxel with two side bits.
/// </para>
/// <para>
/// <b>Deterministic.</b> The brushes are visited in the order given and the
/// voxels in index order, on one thread; the answer for a voxel depends
/// only on the brushes, so it is the same at any thread count of the
/// compile around it.
/// </para>
/// </remarks>
public static class NavVoxeliser
{
    private static ReadOnlySpan<int> StepX => [1, 0, -1, 0, 0, 0];

    private static ReadOnlySpan<int> StepY => [0, 1, 0, -1, 0, 0];

    private static ReadOnlySpan<int> StepZ => [0, 0, 0, 0, 1, -1];

    /// <summary>Classifies a box of voxels.</summary>
    /// <param name="brushes">The brushes; those whose contents the agent does not collide with are ignored.</param>
    /// <param name="region">The voxels.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="floorNormalZ">The least normal z of a floor.</param>
    /// <param name="cancellationToken">Cancels the classification between slices.</param>
    /// <returns>The grid.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static NavVoxelGrid Classify(
        IReadOnlyList<NavBrush> brushes,
        NavRegion region,
        NavAgentSpec agent,
        float floorNormalZ,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(brushes);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentOutOfRangeException.ThrowIfLessThan(region.SizeX, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(region.SizeY, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(region.SizeZ, 1);
        if (!(region.VoxelSize > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(region), "the voxel has a positive edge.");
        }

        int rx = region.SizeX + 2;
        int ry = region.SizeY + 2;
        int rz = region.SizeZ + 2;
        bool[] ring = new bool[rx * ry * rz];
        // Which brushes block each voxel, as linked lists threaded through
        // two flat arrays: one allocation for the lot rather than a list per
        // blocked voxel, which dominated the collector's work.
        int[] firstBlocker = new int[ring.Length];
        Array.Fill(firstBlocker, -1);
        List<int> blockerBrush = [];
        List<int> nextBlocker = [];
        double s = region.VoxelSize;
        double minX = agent.Mins.X;
        double minY = agent.Mins.Y;
        double minZ = agent.Mins.Z;
        double maxX = agent.Maxs.X;
        double maxY = agent.Maxs.Y;
        double maxZ = agent.Maxs.Z;

        for (int b = 0; b < brushes.Count; b++)
        {
            NavBrush brush = brushes[b];
            if ((brush.Contents & agent.ContentsMask) == 0)
            {
                continue;
            }

            (int x0, int x1) = Range(brush.MinX, brush.MaxX, region.OriginX, minX, maxX, s, region.SizeX);
            (int y0, int y1) = Range(brush.MinY, brush.MaxY, region.OriginY, minY, maxY, s, region.SizeY);
            (int z0, int z1) = Range(brush.MinZ, brush.MaxZ, region.OriginZ, minZ, maxZ, s, region.SizeZ);
            for (int z = z0; z <= z1; z++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        NavBox swept = Swept(region, x, y, z, minX, minY, minZ, maxX, maxY, maxZ);
                        if (brush.Overlaps(swept))
                        {
                            int at = ((((z + 1) * ry) + y + 1) * rx) + x + 1;
                            ring[at] = true;
                            blockerBrush.Add(b);
                            nextBlocker.Add(firstBlocker[at]);
                            firstBlocker[at] = blockerBrush.Count - 1;
                        }
                    }
                }
            }
        }

        ushort[] cells = new ushort[region.SizeX * region.SizeY * region.SizeZ];
        for (int z = 0; z < region.SizeZ; z++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int y = 0; y < region.SizeY; y++)
            {
                for (int x = 0; x < region.SizeX; x++)
                {
                    int at = ((((z + 1) * ry) + y + 1) * rx) + x + 1;
                    if (ring[at])
                    {
                        continue;
                    }

                    NavBox swept = Swept(region, x, y, z, minX, minY, minZ, maxX, maxY, maxZ);
                    Nav3dLeafFlags flags = Nav3dLeafFlags.None;
                    for (int d = 0; d < 6; d++)
                    {
                        int next = ((((z + 1 + StepZ[d]) * ry) + y + 1 + StepY[d]) * rx) + x + 1 + StepX[d];
                        if (!ring[next])
                        {
                            continue;
                        }

                        if (d < 4)
                        {
                            flags |= (Nav3dLeafFlags)(1 << (3 + d));
                        }

                        for (int link = firstBlocker[next]; link >= 0; link = nextBlocker[link])
                        {
                            flags |= Contact(brushes[blockerBrush[link]], swept, d, floorNormalZ);
                        }
                    }

                    cells[(((z * region.SizeY) + y) * region.SizeX) + x] = (ushort)(NavVoxelGrid.FreeBit | (ushort)flags);
                }
            }
        }

        return new NavVoxelGrid(region, cells, ring);
    }

    /// <summary>What kind of surface of one brush a voxel's box meets stepping one way.</summary>
    private static Nav3dLeafFlags Contact(NavBrush brush, in NavBox swept, int direction, float floorNormalZ)
    {
        Nav3dLeafFlags found = Nav3dLeafFlags.None;
        bool any = false;
        for (int p = 0; p < brush.PlaneCount; p++)
        {
            (double nx, double ny, double nz, _) = brush.Plane(p);
            double against = (nx * StepX[direction]) + (ny * StepY[direction]) + (nz * StepZ[direction]);
            if (against >= -1e-6 || !brush.FaceSeparates(p, swept))
            {
                continue;
            }

            any = true;
            found |= nz >= floorNormalZ ? Nav3dLeafFlags.Floor
                : nz <= -floorNormalZ ? Nav3dLeafFlags.Ceiling
                : Nav3dLeafFlags.Wall;
        }

        if (any)
        {
            return found;
        }

        return direction switch
        {
            5 => Nav3dLeafFlags.Floor,
            4 => Nav3dLeafFlags.Ceiling,
            _ => Nav3dLeafFlags.Wall,
        };
    }

    private static NavBox Swept(
        NavRegion region, int x, int y, int z, double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
    {
        double s = region.VoxelSize;
        return new NavBox(
            region.OriginX + (x * s) + minX,
            region.OriginY + (y * s) + minY,
            region.OriginZ + (z * s) + minZ,
            region.OriginX + ((x + 1) * s) + maxX,
            region.OriginY + ((y + 1) * s) + maxY,
            region.OriginZ + ((z + 1) * s) + maxZ);
    }

    /// <summary>
    /// The voxels, ring included, whose swept box might reach a brush's
    /// extent on one axis: a voxel either side more than the exact bound, so
    /// rounding never drops one; the overlap test decides.
    /// </summary>
    private static (int Lo, int Hi) Range(double lo, double hi, double origin, double agentMin, double agentMax, double s, int size)
    {
        int first = (int)Math.Max(-1, Math.Floor((lo - origin - agentMax) / s) - 1);
        int last = (int)Math.Min(size, Math.Floor((hi - origin - agentMin) / s) + 1);
        return (first, last);
    }
}
