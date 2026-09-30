//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Immutable;

namespace SourceSharp.MapFormats.Map2d;

/// <summary>A point of a ring, in whole world units: +x east, +y north.</summary>
/// <param name="X">East.</param>
/// <param name="Y">North.</param>
public readonly record struct Map2dPoint(int X, int Y);

/// <summary>One placement of the level: a room in a cell.</summary>
/// <param name="CellX">Its column, 0 at the west edge.</param>
/// <param name="CellY">Its row, 0 at the south edge.</param>
/// <param name="Rotation">Its quarter turns counter-clockwise, 0 to 3.</param>
/// <param name="Height">The room's height in units.</param>
/// <param name="Name">The room's name, as the level places it.</param>
/// <param name="Label">The room's <c>map_label</c>, or empty.</param>
public sealed record Map2dRoom(int CellX, int CellY, int Rotation, float Height, string Name, string Label);

/// <summary>
/// One ring of a polygon: an outer boundary, counter-clockwise, or a hole,
/// clockwise, of the outer ring before it.
/// </summary>
/// <param name="Placement">The placement it belongs to, or <see cref="Map2dFormat.NoPlacement"/>.</param>
/// <param name="ZLow">The lowest z of the floor it draws, in whole units.</param>
/// <param name="ZHigh">The highest z, in whole units.</param>
/// <param name="IsHole">Whether it is a hole.</param>
/// <param name="Points">Its points, at least three, the first not repeated at the end.</param>
public sealed record Map2dRing(int Placement, int ZLow, int ZHigh, bool IsHole, ImmutableArray<Map2dPoint> Points)
{
    /// <summary>Whether two rings are the same: every field, and the same points in the same order.</summary>
    /// <param name="other">The other ring.</param>
    /// <returns>True when they are equal.</returns>
    /// <remarks>
    /// Written out because <see cref="ImmutableArray{T}"/> compares by the
    /// array it wraps: two rings read from two files would never be equal.
    /// </remarks>
    public bool Equals(Map2dRing? other) =>
        other is not null && Placement == other.Placement && ZLow == other.ZLow && ZHigh == other.ZHigh && IsHole == other.IsHole
        && Points.AsSpan().SequenceEqual(other.Points.AsSpan());

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Placement, ZLow, ZHigh, IsHole, Points.Length);
}

/// <summary>A door: a segment across a socket's opening, on the cell face.</summary>
/// <param name="Placement">The placement whose socket it is.</param>
/// <param name="Socket">The socket's name.</param>
/// <param name="Open">Whether the socket is joined to a neighbour's (else a cap seals it).</param>
/// <param name="Neighbour">The placement it opens into, or <see cref="Map2dFormat.NoPlacement"/> when closed.</param>
/// <param name="X0">One end, east.</param>
/// <param name="Y0">One end, north.</param>
/// <param name="X1">The other end, east.</param>
/// <param name="Y1">The other end, north.</param>
/// <param name="ZLow">The opening's bottom.</param>
/// <param name="ZHigh">The opening's top.</param>
public sealed record Map2dDoor(
    int Placement, string Socket, bool Open, int Neighbour, float X0, float Y0, float X1, float Y1, float ZLow, float ZHigh);

/// <summary>A marker: a point of interest on the map.</summary>
/// <param name="Kind">Its kind, a short identifier the game maps to an icon.</param>
/// <param name="Label">Its <c>map_label</c>, or empty.</param>
/// <param name="Placement">The placement it stands in, or <see cref="Map2dFormat.NoPlacement"/>.</param>
/// <param name="X">Where it stands, east.</param>
/// <param name="Y">North.</param>
/// <param name="Z">Up.</param>
/// <param name="Yaw">Its facing in degrees counter-clockwise from +x, 0 to 360.</param>
public sealed record Map2dMarker(string Kind, string Label, int Placement, float X, float Y, float Z, float Yaw);

/// <summary>The box the map's contents fill, in whole units.</summary>
/// <param name="MinX">West edge.</param>
/// <param name="MinY">South edge.</param>
/// <param name="MaxX">East edge.</param>
/// <param name="MaxY">North edge.</param>
/// <param name="MinZ">Lowest z.</param>
/// <param name="MaxZ">Highest z.</param>
public readonly record struct Map2dExtent(int MinX, int MinY, int MaxX, int MaxY, int MinZ, int MaxZ);

/// <summary>
/// A level's map as the <c>.map2d</c> file holds it (<see cref="Map2dFormat"/>):
/// what <see cref="Map2dWriter"/> writes and <see cref="Map2dReader"/> reads.
/// </summary>
/// <remarks>
/// <para>
/// Everything is in world units, +x east and +y north as in the map, so a
/// game places the player's arrow with no conversion. Polygon points are
/// whole units (the rooms design, 18.2: the union runs on integers); doors
/// and markers keep the map's floats.
/// </para>
/// <para>
/// <see cref="Extent"/> is not stored in the object: it is a function of the
/// contents, computed here, written into the header, and checked by the
/// reader against what the file holds, so a header and its sections cannot
/// disagree.
/// </para>
/// </remarks>
public sealed record Map2dLevel
{
    /// <summary>The checksum of the map the file was made for (<c>BspMapChecksum</c>).</summary>
    public uint MapChecksum { get; init; }

    /// <summary>The level's cell size, or 0 for a map made without a level file.</summary>
    public float CellSize { get; init; }

    /// <summary>The level grid's columns, or 0.</summary>
    public int Columns { get; init; }

    /// <summary>The level grid's rows, or 0.</summary>
    public int Rows { get; init; }

    /// <summary>The placements, in link order.</summary>
    public ImmutableArray<Map2dRoom> Rooms { get; init; } = [];

    /// <summary>The rings, each outer ring followed by its holes.</summary>
    public ImmutableArray<Map2dRing> Rings { get; init; } = [];

    /// <summary>The doors, by placement then socket.</summary>
    public ImmutableArray<Map2dDoor> Doors { get; init; } = [];

    /// <summary>The markers.</summary>
    public ImmutableArray<Map2dMarker> Markers { get; init; } = [];

    /// <summary>Whether every ring point fits an <c>int16</c>: the file then stores them in half the bytes.</summary>
    /// <remarks>
    /// Loops rather than LINQ here and elsewhere in this assembly: a lambda
    /// the compiler caches is a static field, which the no-mutable-statics
    /// rule rightly cannot tell from shared state.
    /// </remarks>
    public bool ShortPoints
    {
        get
        {
            foreach (Map2dRing ring in Rings)
            {
                foreach (Map2dPoint p in ring.Points)
                {
                    if (p.X is < short.MinValue or > short.MaxValue || p.Y is < short.MinValue or > short.MaxValue)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }

    /// <summary>How many points the rings hold together.</summary>
    public int PointCount
    {
        get
        {
            int count = 0;
            foreach (Map2dRing ring in Rings)
            {
                count += ring.Points.Length;
            }

            return count;
        }
    }

    /// <summary>
    /// The box the contents fill: every ring point and band, every door end
    /// and band, every marker, floats widened outward to whole units; all
    /// zero for an empty map.
    /// </summary>
    public Map2dExtent Extent
    {
        get
        {
            bool any = false;
            long minX = 0, minY = 0, maxX = 0, maxY = 0, minZ = 0, maxZ = 0;
            void Add(double x0, double y0, double x1, double y1, double z0, double z1)
            {
                long lx = (long)Math.Floor(x0), ly = (long)Math.Floor(y0), hx = (long)Math.Ceiling(x1), hy = (long)Math.Ceiling(y1);
                long lz = (long)Math.Floor(z0), hz = (long)Math.Ceiling(z1);
                if (!any)
                {
                    (minX, minY, maxX, maxY, minZ, maxZ, any) = (lx, ly, hx, hy, lz, hz, true);
                    return;
                }

                (minX, minY, maxX, maxY) = (Math.Min(minX, lx), Math.Min(minY, ly), Math.Max(maxX, hx), Math.Max(maxY, hy));
                (minZ, maxZ) = (Math.Min(minZ, lz), Math.Max(maxZ, hz));
            }

            foreach (Map2dRing ring in Rings)
            {
                foreach (Map2dPoint p in ring.Points)
                {
                    Add(p.X, p.Y, p.X, p.Y, ring.ZLow, ring.ZHigh);
                }
            }

            foreach (Map2dDoor door in Doors)
            {
                Add(Math.Min(door.X0, door.X1), Math.Min(door.Y0, door.Y1), Math.Max(door.X0, door.X1), Math.Max(door.Y0, door.Y1), door.ZLow, door.ZHigh);
            }

            foreach (Map2dMarker marker in Markers)
            {
                Add(marker.X, marker.Y, marker.X, marker.Y, marker.Z, marker.Z);
            }

            return new Map2dExtent(Clamp(minX), Clamp(minY), Clamp(maxX), Clamp(maxY), Clamp(minZ), Clamp(maxZ));

            static int Clamp(long v) => (int)Math.Clamp(v, int.MinValue, int.MaxValue);
        }
    }
}
