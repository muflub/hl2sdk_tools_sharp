//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;

namespace SourceSharp.MapTools.Phys.Managed;

/// <summary>
/// An <c>IVP_Compact_Ledge</c> in its serialised layout: a 16-byte header, 16 bytes per triangle,
/// then 16 bytes per point.
/// </summary>
/// <remarks>
/// <para>
/// Layout (little-endian; field semantics from the reference implementation's format,
/// bit positions from the MIT-licensed VPhysics-Jolt <c>ivp_compat</c> structs, and every field
/// Confirmed byte for byte against stock output):
/// </para>
/// <code>
/// +0   int  c_point_offset        (from this ledge to its first point)
/// +4   int  client_data / ledgetree_node_offset
/// +8   uint has_children:2, is_compact:2, dummy:4, size_div_16:24
/// +12  short n_triangles, +14 short for_future_use
/// triangle (16): uint tri_index:12, pierce_index:12, material_index:7, is_virtual:1;
///                then 3 edges: uint start_point_index:16, opposite_index:15 (signed, in edges), is_virtual:1
/// point (16):    float x, y, z, hesse_val
/// </code>
/// </remarks>
internal sealed class IvpCompactLedge
{
    /// <summary>The ledge bytes.</summary>
    public byte[] Bytes { get; }

    /// <summary>Wraps ledge bytes.</summary>
    /// <param name="bytes">A whole ledge.</param>
    public IvpCompactLedge(byte[] bytes) => Bytes = bytes;

    /// <summary>Allocates a zeroed ledge.</summary>
    /// <param name="triangles">Triangle count.</param>
    /// <param name="points">Point count.</param>
    /// <returns>The ledge, header filled.</returns>
    public static IvpCompactLedge Create(int triangles, int points)
    {
        int size = (triangles + 1 + points) * 16;
        var ledge = new IvpCompactLedge(new byte[size]);
        ledge.PointOffset = 16 + (16 * triangles);
        ledge.TriangleCount = (short)triangles;
        // Size_div_16 << 8 | (flags & 0xf3 | 4) -- is_compact = 1, has_children = 0.
        ledge.FlagsWord = ((uint)(size >> 4) << 8) | 4u;
        return ledge;
    }

    /// <summary>c_point_offset.</summary>
    public int PointOffset
    {
        get => BinaryPrimitives.ReadInt32LittleEndian(Bytes.AsSpan(0));
        set => BinaryPrimitives.WriteInt32LittleEndian(Bytes.AsSpan(0), value);
    }

    /// <summary>client_data (the game data set by <c>SetConvexGameData</c>).</summary>
    public int ClientData
    {
        get => BinaryPrimitives.ReadInt32LittleEndian(Bytes.AsSpan(4));
        set => BinaryPrimitives.WriteInt32LittleEndian(Bytes.AsSpan(4), value);
    }

    /// <summary>The flags / size word.</summary>
    public uint FlagsWord
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(8));
        set => BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(8), value);
    }

    /// <summary>n_triangles.</summary>
    public short TriangleCount
    {
        get => BinaryPrimitives.ReadInt16LittleEndian(Bytes.AsSpan(12));
        set => BinaryPrimitives.WriteInt16LittleEndian(Bytes.AsSpan(12), value);
    }

    /// <summary>Byte size (size_div_16 * 16).</summary>
    public int Size => (int)(FlagsWord >> 8) * 16;

    /// <summary>Number of points: whatever follows the triangles.</summary>
    public int PointCount => (Size - PointOffset) / 16;

    /// <summary>Reads a point's coordinates.</summary>
    /// <param name="index">Point index.</param>
    /// <returns>x, y, z.</returns>
    public (float X, float Y, float Z) Point(int index)
    {
        int o = PointOffset + (16 * index);
        ReadOnlySpan<byte> b = Bytes;
        return (
            BinaryPrimitives.ReadSingleLittleEndian(b[o..]),
            BinaryPrimitives.ReadSingleLittleEndian(b[(o + 4)..]),
            BinaryPrimitives.ReadSingleLittleEndian(b[(o + 8)..]));
    }

    /// <summary>Writes a point (hesse_val = 0).</summary>
    /// <param name="index">Point index.</param>
    /// <param name="x">X.</param>
    /// <param name="y">Y.</param>
    /// <param name="z">Z.</param>
    public void SetPoint(int index, float x, float y, float z)
    {
        int o = PointOffset + (16 * index);
        Span<byte> b = Bytes;
        BinaryPrimitives.WriteSingleLittleEndian(b[o..], x);
        BinaryPrimitives.WriteSingleLittleEndian(b[(o + 4)..], y);
        BinaryPrimitives.WriteSingleLittleEndian(b[(o + 8)..], z);
        BinaryPrimitives.WriteSingleLittleEndian(b[(o + 12)..], 0f);
    }

    /// <summary>A triangle's first word.</summary>
    /// <param name="tri">Triangle index.</param>
    /// <returns>tri_index | pierce_index &lt;&lt; 12 | material &lt;&lt; 24 | virtual &lt;&lt; 31.</returns>
    public uint TriangleWord(int tri) => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(16 + (16 * tri)));

    /// <summary>Sets a triangle's first word.</summary>
    /// <param name="tri">Triangle index.</param>
    /// <param name="value">The word.</param>
    public void SetTriangleWord(int tri, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(16 + (16 * tri)), value);

    /// <summary>An edge word.</summary>
    /// <param name="tri">Triangle index.</param>
    /// <param name="edge">Edge 0..2.</param>
    /// <returns>start_point_index | opposite_index &lt;&lt; 16 | virtual &lt;&lt; 31.</returns>
    public uint EdgeWord(int tri, int edge) =>
        BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(16 + (16 * tri) + 4 + (4 * edge)));

    /// <summary>Sets an edge word.</summary>
    /// <param name="tri">Triangle index.</param>
    /// <param name="edge">Edge 0..2.</param>
    /// <param name="value">The word.</param>
    public void SetEdgeWord(int tri, int edge, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(16 + (16 * tri) + 4 + (4 * edge)), value);

    /// <summary>An edge's start point index.</summary>
    /// <param name="tri">Triangle index.</param>
    /// <param name="edge">Edge 0..2.</param>
    /// <returns>The point index.</returns>
    public int EdgeStart(int tri, int edge) => (int)(EdgeWord(tri, edge) & 0xffff);

    /// <summary>
    /// The edge a given edge is joined to, as (triangle, edge), following opposite_index (a signed
    /// offset in 4-byte edge units from this edge).
    /// </summary>
    /// <param name="tri">Triangle index.</param>
    /// <param name="edge">Edge 0..2.</param>
    /// <returns>The opposite edge.</returns>
    public (int Tri, int Edge) Opposite(int tri, int edge)
    {
        int opp = (int)((EdgeWord(tri, edge) >> 16) & 0x7fff);
        if ((opp & 0x4000) != 0)
        {
            opp -= 0x8000;
        }

        int unit = (4 * tri) + 1 + edge + opp;
        return (unit >> 2, (unit & 3) - 1);
    }

    /// <summary>The start point of <c>edge.get_next()</c> (edge+1 mod 3 in the same triangle).</summary>
    /// <param name="tri">Triangle.</param>
    /// <param name="edge">Edge.</param>
    /// <returns>Point index.</returns>
    public int NextStart(int tri, int edge) => EdgeStart(tri, edge == 2 ? 0 : edge + 1);

    /// <summary>The start point of <c>edge.get_prev()</c>.</summary>
    /// <param name="tri">Triangle.</param>
    /// <param name="edge">Edge.</param>
    /// <returns>Point index.</returns>
    public int PrevStart(int tri, int edge) => EdgeStart(tri, edge == 0 ? 2 : edge - 1);
}
