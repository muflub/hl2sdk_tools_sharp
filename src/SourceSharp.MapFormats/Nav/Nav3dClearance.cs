//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;

namespace SourceSharp.MapFormats.Nav;

/// <summary>One obstacle a clearance record lists: a solid that blocks agents wider than <see cref="Width"/> and reaching above <see cref="Top"/>.</summary>
/// <param name="Width">
/// The half-width threshold <c>R</c>: the obstacle blocks an agent of
/// half-width <c>r</c> only when <c>r &gt; R + ε</c>.
/// <see cref="float.NegativeInfinity"/> when it blocks every width.
/// </param>
/// <param name="Top">
/// The altitude threshold <c>T</c>: the obstacle blocks an agent whose box
/// reaches <c>z1 + h</c> (the voxel's top plus the agent's height) only when
/// <c>z1 + h &gt; T + ε</c>. <see cref="float.NegativeInfinity"/> when it
/// blocks at any height.
/// </param>
public readonly record struct Nav3dCorner(float Width, float Top);

/// <summary>A corner that belongs to a dynamic obstacle: it blocks only while the runtime says the obstacle does.</summary>
/// <param name="Obstacle">The obstacle's index in the file's obstacle table.</param>
/// <param name="Corner">Its thresholds.</param>
public readonly record struct Nav3dDynamicCorner(int Obstacle, Nav3dCorner Corner);

/// <summary>
/// A clearance record: what a voxel's (or a leaf's) free space looks like to
/// every axis-aligned agent of one clip class, as a staircase of obstacles.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it answers.</b> An agent is a box of half-width <c>r</c> (width
/// <c>2r</c>, square seen from above) and height <c>h</c> standing on its
/// origin. It fits in a voxel spanning <c>[x0, x1] × [y0, y1] × [z0, z1]</c>
/// when its box, with the origin anywhere in the voxel, overlaps no solid of
/// its clip class: when the swept box <c>[x0 − r, x1 + r] × [y0 − r, y1 + r] ×
/// [z0, z1 + h]</c> is free. A record answers that for every <c>(r, h)</c>.
/// </para>
/// <para>
/// <b>Why a staircase is exact.</b> Take one brush. Unless it lies wholly
/// below the voxel (then it never blocks), the swept box overlaps it exactly
/// when the widened footprint reaches it and the box's top rises past its
/// bottom: for an axis-aligned brush, when <c>r</c> exceeds the footprint's
/// gap to the brush (the larger of the four side gaps, the Chebyshev gap)
/// and <c>z1 + h</c> exceeds the brush's lowest point. That is a corner
/// <c>(R, T)</c>: blocked iff <c>r &gt; R + ε</c> and <c>z1 + h &gt; T + ε</c>.
/// A brush that is not axis-aligned but has no sloped face turned downward
/// (a ramp, an angled wall) grows no wider going up, so once the box reaches
/// its bottom what it blocks depends on the box's width alone: the same
/// corner shape, with <c>R</c> the width at which the separating-axis test
/// first fails (<see cref="NavBrush.GrowthThreshold"/>). The voxel is blocked
/// iff some corner blocks, so only the corners no other corner dominates
/// matter (one dominates another when it is no wider and no higher), and
/// those form a staircase: widths rising, tops falling. Nothing is lost, so
/// the answer equals the direct box test for every size.
/// </para>
/// <para>
/// <b>Brushes that do not fit the shape</b> are those with a sloped face
/// turned downward (<see cref="NavBrush.IsOverhang"/>): under a sloped ceiling
/// a wider agent has less head room, a slanted edge no corner can state. The
/// record lists such a brush by its index in the file's brush table, and the
/// reader runs the direct test (<see cref="NavBrush.Overlaps"/>) on it. A
/// voxel with such a brush is always a leaf of its own.
/// </para>
/// <para>
/// <b>Dynamic obstacles</b> (doors, breakables, props) are not solid in the
/// grid; their corners are listed apart, each with its obstacle, and block
/// only while the runtime says so. A dynamic corner some static corner
/// dominates is left out: it can never be the reason an agent is blocked.
/// </para>
/// <para>
/// <b>Layout</b> (little-endian, a multiple of four bytes): <c>uint16</c>
/// static count, <c>uint16</c> dynamic count, <c>uint16</c> brush count,
/// <c>uint16</c> zero; then the static corners (<c>float32</c> width, top),
/// widths strictly rising and tops strictly falling; then the dynamic
/// corners (<c>uint32</c> obstacle, <c>float32</c> width, top), in obstacle
/// then width order; then the brushes (<c>uint32</c> index), ascending. A
/// record that blocks even a point agent is the single corner
/// <c>(−∞, −∞)</c> with nothing else.
/// </para>
/// </remarks>
public static class Nav3dClearance
{
    /// <summary>The count words in front of a record's entries.</summary>
    public const int HeaderBytes = 8;

    /// <summary>One static corner: width and top.</summary>
    public const int CornerBytes = 8;

    /// <summary>One dynamic corner: obstacle, width and top.</summary>
    public const int DynamicBytes = 12;

    /// <summary>One brush reference.</summary>
    public const int BrushBytes = 4;

    /// <summary>The record that blocks everything, even a point: one corner at <c>(−∞, −∞)</c>.</summary>
    public static ReadOnlySpan<byte> BlockedRecord => [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x80, 0xFF, 0, 0, 0x80, 0xFF];

    /// <summary>The bytes of a record.</summary>
    /// <param name="corners">The static corners, already a staircase (widths strictly rising, tops strictly falling).</param>
    /// <param name="dynamics">The dynamic corners, in obstacle then width order.</param>
    /// <param name="brushes">The overhanging brushes, ascending.</param>
    /// <returns>The record's bytes.</returns>
    /// <exception cref="ArgumentException">A list is too long for its count, or out of order.</exception>
    public static byte[] Encode(ReadOnlySpan<Nav3dCorner> corners, ReadOnlySpan<Nav3dDynamicCorner> dynamics, ReadOnlySpan<int> brushes)
    {
        if (corners.Length > ushort.MaxValue || dynamics.Length > ushort.MaxValue || brushes.Length > ushort.MaxValue)
        {
            throw new ArgumentException("a clearance record holds at most 65535 entries of a kind.");
        }

        byte[] bytes = new byte[HeaderBytes + (corners.Length * CornerBytes) + (dynamics.Length * DynamicBytes) + (brushes.Length * BrushBytes)];
        Span<byte> b = bytes;
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)corners.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(b[2..], (ushort)dynamics.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(b[4..], (ushort)brushes.Length);
        int at = HeaderBytes;
        foreach (Nav3dCorner corner in corners)
        {
            BinaryPrimitives.WriteSingleLittleEndian(b[at..], corner.Width);
            BinaryPrimitives.WriteSingleLittleEndian(b[(at + 4)..], corner.Top);
            at += CornerBytes;
        }

        foreach (Nav3dDynamicCorner dynamic in dynamics)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b[at..], (uint)dynamic.Obstacle);
            BinaryPrimitives.WriteSingleLittleEndian(b[(at + 4)..], dynamic.Corner.Width);
            BinaryPrimitives.WriteSingleLittleEndian(b[(at + 8)..], dynamic.Corner.Top);
            at += DynamicBytes;
        }

        foreach (int brush in brushes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b[at..], (uint)brush);
            at += BrushBytes;
        }

        if (Problem(bytes, int.MaxValue, int.MaxValue) is { } problem)
        {
            throw new ArgumentException($"a clearance record {problem}.");
        }

        return bytes;
    }

    /// <summary>A record's length in bytes, read from its counts.</summary>
    /// <param name="record">Bytes starting at the record.</param>
    /// <returns>The length.</returns>
    public static int Length(ReadOnlySpan<byte> record) =>
        HeaderBytes + (StaticCount(record) * CornerBytes) + (DynamicCount(record) * DynamicBytes) + (BrushCount(record) * BrushBytes);

    /// <summary>How many static corners a record has.</summary>
    /// <param name="record">The record.</param>
    /// <returns>The count.</returns>
    public static int StaticCount(ReadOnlySpan<byte> record) => BinaryPrimitives.ReadUInt16LittleEndian(record);

    /// <summary>How many dynamic corners a record has.</summary>
    /// <param name="record">The record.</param>
    /// <returns>The count.</returns>
    public static int DynamicCount(ReadOnlySpan<byte> record) => BinaryPrimitives.ReadUInt16LittleEndian(record[2..]);

    /// <summary>How many overhanging brushes a record lists.</summary>
    /// <param name="record">The record.</param>
    /// <returns>The count.</returns>
    public static int BrushCount(ReadOnlySpan<byte> record) => BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);

    /// <summary>One static corner.</summary>
    /// <param name="record">The record.</param>
    /// <param name="index">Which, 0 to <see cref="StaticCount"/> - 1.</param>
    /// <returns>The corner.</returns>
    public static Nav3dCorner Corner(ReadOnlySpan<byte> record, int index)
    {
        ReadOnlySpan<byte> c = record[(HeaderBytes + (index * CornerBytes))..];
        return new Nav3dCorner(BinaryPrimitives.ReadSingleLittleEndian(c), BinaryPrimitives.ReadSingleLittleEndian(c[4..]));
    }

    /// <summary>One dynamic corner.</summary>
    /// <param name="record">The record.</param>
    /// <param name="index">Which, 0 to <see cref="DynamicCount"/> - 1.</param>
    /// <returns>The corner and its obstacle.</returns>
    public static Nav3dDynamicCorner Dynamic(ReadOnlySpan<byte> record, int index)
    {
        ReadOnlySpan<byte> c = record[(HeaderBytes + (StaticCount(record) * CornerBytes) + (index * DynamicBytes))..];
        return new Nav3dDynamicCorner(
            (int)BinaryPrimitives.ReadUInt32LittleEndian(c),
            new Nav3dCorner(BinaryPrimitives.ReadSingleLittleEndian(c[4..]), BinaryPrimitives.ReadSingleLittleEndian(c[8..])));
    }

    /// <summary>One overhanging brush's index.</summary>
    /// <param name="record">The record.</param>
    /// <param name="index">Which, 0 to <see cref="BrushCount"/> - 1.</param>
    /// <returns>The brush's index in the file's brush table.</returns>
    public static int Brush(ReadOnlySpan<byte> record, int index) =>
        (int)BinaryPrimitives.ReadUInt32LittleEndian(
            record[(HeaderBytes + (StaticCount(record) * CornerBytes) + (DynamicCount(record) * DynamicBytes) + (index * BrushBytes))..]);

    /// <summary>Whether a record blocks even a point agent: its first corner is <c>(−∞, −∞)</c>.</summary>
    /// <param name="record">The record.</param>
    /// <returns>True for a solid voxel of this class.</returns>
    public static bool IsBlocked(ReadOnlySpan<byte> record) =>
        StaticCount(record) > 0 && float.IsNegativeInfinity(Corner(record, 0).Width) && float.IsNegativeInfinity(Corner(record, 0).Top);

    /// <summary>Whether one corner blocks an agent in a voxel.</summary>
    /// <param name="corner">The corner.</param>
    /// <param name="halfWidth">The agent's half-width <c>r</c>.</param>
    /// <param name="height">The agent's height <c>h</c>.</param>
    /// <param name="voxelTop">The voxel's top <c>z1</c>.</param>
    /// <returns>True when <c>r &gt; R + ε</c> and <c>z1 + h &gt; T + ε</c>.</returns>
    public static bool Blocks(Nav3dCorner corner, double halfWidth, double height, double voxelTop) =>
        halfWidth > corner.Width + NavBrush.Epsilon && voxelTop + height > corner.Top + NavBrush.Epsilon;

    /// <summary>Whether a record's static and dynamic corners block an agent in a voxel (its brushes are the caller's).</summary>
    /// <param name="record">The record.</param>
    /// <param name="halfWidth">The agent's half-width.</param>
    /// <param name="height">The agent's height.</param>
    /// <param name="voxelTop">The voxel's top.</param>
    /// <param name="blocking">Per obstacle, whether it blocks now; an empty span for none (the grid as stored).</param>
    /// <returns>True when a corner blocks.</returns>
    public static bool CornersBlock(ReadOnlySpan<byte> record, double halfWidth, double height, double voxelTop, ReadOnlySpan<bool> blocking)
    {
        int statics = StaticCount(record);
        for (int i = 0; i < statics; i++)
        {
            if (Blocks(Corner(record, i), halfWidth, height, voxelTop))
            {
                return true;
            }
        }

        if (blocking.IsEmpty)
        {
            return false;
        }

        int dynamics = DynamicCount(record);
        for (int i = 0; i < dynamics; i++)
        {
            Nav3dDynamicCorner d = Dynamic(record, i);
            if ((uint)d.Obstacle < (uint)blocking.Length && blocking[d.Obstacle] && Blocks(d.Corner, halfWidth, height, voxelTop))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The most height an agent of a given half-width has in a voxel by the
    /// record's corners: it fits exactly when its height is at most this.
    /// </summary>
    /// <param name="record">The record.</param>
    /// <param name="halfWidth">The agent's half-width.</param>
    /// <param name="voxelTop">The voxel's top.</param>
    /// <param name="blocking">Per obstacle, whether it blocks now; empty for none.</param>
    /// <returns>
    /// The head room, <see cref="double.PositiveInfinity"/> when nothing above
    /// limits it, <see cref="double.NegativeInfinity"/> when the width does not
    /// fit at any height.
    /// </returns>
    public static double HeadRoom(ReadOnlySpan<byte> record, double halfWidth, double voxelTop, ReadOnlySpan<bool> blocking)
    {
        double room = double.PositiveInfinity;
        int statics = StaticCount(record);
        for (int i = 0; i < statics; i++)
        {
            room = Math.Min(room, HeadRoom(Corner(record, i), halfWidth, voxelTop));
        }

        if (!blocking.IsEmpty)
        {
            int dynamics = DynamicCount(record);
            for (int i = 0; i < dynamics; i++)
            {
                Nav3dDynamicCorner d = Dynamic(record, i);
                if ((uint)d.Obstacle < (uint)blocking.Length && blocking[d.Obstacle])
                {
                    room = Math.Min(room, HeadRoom(d.Corner, halfWidth, voxelTop));
                }
            }
        }

        return room;
    }

    /// <summary>
    /// The most half-width an agent of a given height has in a voxel by the
    /// record's corners: it fits exactly when its half-width is at most this.
    /// </summary>
    /// <param name="record">The record.</param>
    /// <param name="height">The agent's height.</param>
    /// <param name="voxelTop">The voxel's top.</param>
    /// <param name="blocking">Per obstacle, whether it blocks now; empty for none.</param>
    /// <returns>The room, <see cref="double.PositiveInfinity"/> when unlimited, <see cref="double.NegativeInfinity"/> when nothing fits.</returns>
    public static double WidthRoom(ReadOnlySpan<byte> record, double height, double voxelTop, ReadOnlySpan<bool> blocking)
    {
        double room = double.PositiveInfinity;
        int statics = StaticCount(record);
        for (int i = 0; i < statics; i++)
        {
            room = Math.Min(room, WidthRoom(Corner(record, i), height, voxelTop));
        }

        if (!blocking.IsEmpty)
        {
            int dynamics = DynamicCount(record);
            for (int i = 0; i < dynamics; i++)
            {
                Nav3dDynamicCorner d = Dynamic(record, i);
                if ((uint)d.Obstacle < (uint)blocking.Length && blocking[d.Obstacle])
                {
                    room = Math.Min(room, WidthRoom(d.Corner, height, voxelTop));
                }
            }
        }

        return room;
    }

    /// <summary>What is wrong with a record, or null when it is well formed.</summary>
    /// <param name="record">Bytes starting at the record (possibly longer).</param>
    /// <param name="obstacles">How many obstacles the file has.</param>
    /// <param name="brushes">How many brushes the file has.</param>
    /// <returns>The problem, worded to follow "a clearance record", or null.</returns>
    public static string? Problem(ReadOnlySpan<byte> record, int obstacles, int brushes)
    {
        if (record.Length < HeaderBytes)
        {
            return "is cut short";
        }

        if (record.Length < Length(record) || BinaryPrimitives.ReadUInt16LittleEndian(record[6..]) != 0)
        {
            return "runs past its section or has a non-zero reserved word";
        }

        int statics = StaticCount(record);
        for (int i = 0; i < statics; i++)
        {
            Nav3dCorner c = Corner(record, i);
            if (float.IsNaN(c.Width) || float.IsNaN(c.Top) || float.IsPositiveInfinity(c.Width) || float.IsPositiveInfinity(c.Top))
            {
                return $"has a corner ({c.Width}, {c.Top}) that is not a number or is infinitely far";
            }

            if (i > 0 && !(c.Width > Corner(record, i - 1).Width && c.Top < Corner(record, i - 1).Top))
            {
                return "has corners that are not a staircase (widths rising, tops falling)";
            }
        }

        int dynamics = DynamicCount(record);
        for (int i = 0; i < dynamics; i++)
        {
            Nav3dDynamicCorner d = Dynamic(record, i);
            if (d.Obstacle < 0 || d.Obstacle >= obstacles || float.IsNaN(d.Corner.Width) || float.IsNaN(d.Corner.Top))
            {
                return $"names obstacle {d.Obstacle} of {obstacles}, or a corner that is not a number";
            }

            if (i > 0 && Dynamic(record, i - 1) is var p
                && (p.Obstacle > d.Obstacle || (p.Obstacle == d.Obstacle && !(d.Corner.Width > p.Corner.Width))))
            {
                return "has dynamic corners out of order";
            }
        }

        int count = BrushCount(record);
        for (int i = 0; i < count; i++)
        {
            int brush = Brush(record, i);
            if (brush < 0 || brush >= brushes || (i > 0 && Brush(record, i - 1) >= brush))
            {
                return $"names brush {brush} of {brushes}, or lists brushes out of order";
            }
        }

        return null;
    }

    private static double HeadRoom(Nav3dCorner corner, double halfWidth, double voxelTop) =>
        halfWidth > corner.Width + NavBrush.Epsilon
            ? (float.IsNegativeInfinity(corner.Top) ? double.NegativeInfinity : corner.Top + NavBrush.Epsilon - voxelTop)
            : double.PositiveInfinity;

    private static double WidthRoom(Nav3dCorner corner, double height, double voxelTop) =>
        voxelTop + height > corner.Top + NavBrush.Epsilon
            ? (float.IsNegativeInfinity(corner.Width) ? double.NegativeInfinity : corner.Width + NavBrush.Epsilon)
            : double.PositiveInfinity;
}
