//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapGen.Catalog;

/// <summary>An axis-aligned box in map space, mins to maxs.</summary>
/// <param name="Mins">The low corner.</param>
/// <param name="Maxs">The high corner.</param>
public readonly record struct Bounds(Point Mins, Point Maxs)
{
    /// <summary>A box from its centre and its half-extents.</summary>
    /// <param name="centre">The middle.</param>
    /// <param name="half">Half the size on each axis.</param>
    public static Bounds Around(Point centre, Point half)
        => new(centre - half, centre + half);

    /// <summary>A box from its low corner and its size.</summary>
    /// <param name="mins">The low corner.</param>
    /// <param name="size">The extent on each axis.</param>
    public static Bounds Sized(Point mins, Point size) => new(mins, mins + size);

    /// <summary>The middle of the box.</summary>
    public Point Centre => new(
        (Mins.X + Maxs.X) * 0.5f,
        (Mins.Y + Maxs.Y) * 0.5f,
        (Mins.Z + Maxs.Z) * 0.5f);

    /// <summary>The extent on each axis.</summary>
    public Point Size => Maxs - Mins;

    /// <summary>A point on the floor of the box, at a given height above it.</summary>
    /// <param name="above">How far above <see cref="Mins"/>.Z to sit.</param>
    public Point OnFloor(float above)
        => new(Centre.X, Centre.Y, Mins.Z + above);

    /// <summary>The same box grown by the same amount on every axis.</summary>
    /// <param name="by">How far to grow, in units.</param>
    public Bounds Inflate(float by)
        => new(Mins - new Point(by, by, by), Maxs + new Point(by, by, by));

    /// <summary>The same box shifted.</summary>
    /// <param name="by">How far to move it.</param>
    public Bounds Offset(Point by) => new(Mins + by, Maxs + by);
}
