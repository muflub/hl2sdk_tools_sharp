using System.Buffers.Binary;

using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;

namespace SourceSharp.MapTools.Vis;

/// <summary>
/// A read-write view over LUMP_LEAFS that works for both lump versions.
/// </summary>
/// <remarks>
/// <para>
/// vvis touches four leaf fields -- <c>contents</c>, <c>flags</c>,
/// <c>cluster</c> and <c>leafWaterDataID</c> -- plus the bounds and the leaf
/// face run. Every one of them sits at the same offset in
/// <see cref="DLeaf"/> (version 1, 32 bytes) and
/// <see cref="DLeafVersion0"/> (version 0, 56 bytes), because version 1 is
/// version 0 with the trailing ambient cube removed
/// </para>
/// <para>
/// So rather than two code paths or a conversion pass, this reads and writes
/// the shared prefix at a stride the lump's version decides. That also means a
/// version 0 map keeps its ambient cubes: the bytes this does not touch are
/// never rewritten.
/// </para>
/// </remarks>
internal sealed class VisLeaves
{
    private const int ContentsOffset = 0;
    private const int ClusterOffset = 4;
    private const int AreaFlagsOffset = 6;
    private const int MinsOffset = 8;
    private const int MaxsOffset = 14;
    private const int FirstLeafFaceOffset = 20;
    private const int NumLeafFacesOffset = 22;
    private const int LeafWaterDataIdOffset = 28;

    private readonly Memory<byte> _bytes;
    private readonly int _stride;

    private VisLeaves(Memory<byte> bytes, int stride, int version)
    {
        _bytes = bytes;
        _stride = stride;
        Version = version;
        Count = bytes.Length / stride;
    }

    /// <summary>The lump version this view was built for.</summary>
    internal int Version { get; }

    /// <summary>How many leaves the map has -- stock's <c>numleafs</c>.</summary>
    internal int Count { get; }

    /// <summary>The lump's bytes, with any edits already applied.</summary>
    internal ReadOnlyMemory<byte> Bytes => _bytes;

    /// <summary>Takes a mutable copy of a map's leaf lump.</summary>
    /// <param name="bsp">The map.</param>
    /// <returns>The view.</returns>
    /// <exception cref="InvalidBspException">
    /// The lump's version is not 0 or 1, or its length is not a whole number of
    /// leaves.
    /// </exception>
    internal static VisLeaves From(BspData bsp)
    {
        BspLumpData lump = bsp[BspLump.Leafs];
        int stride = lump.Version switch
        {
            0 => 56,
            1 => 32,
            _ => throw new InvalidBspException(
                $"LUMP_LEAFS is version {lump.Version}; the engine accepts 0 and 1"),
        };

        if (lump.Length % stride != 0)
        {
            throw new InvalidBspException(
                $"LUMP_LEAFS is {lump.Length} bytes, not a whole number of {stride}-byte leaves");
        }

        byte[] copy = lump.Data.ToArray();
        return new VisLeaves(copy, stride, lump.Version);
    }

    /// <summary>The leaf's contents flags.</summary>
    /// <param name="leaf">A leaf index.</param>
    /// <returns>The <c>CONTENTS_*</c> bits.</returns>
    internal int Contents(int leaf) =>
        BinaryPrimitives.ReadInt32LittleEndian(Field(leaf, ContentsOffset, 4));

    /// <summary>Replaces the leaf's contents flags.</summary>
    /// <param name="leaf">A leaf index.</param>
    /// <param name="contents">The new bits.</param>
    internal void SetContents(int leaf, int contents) =>
        BinaryPrimitives.WriteInt32LittleEndian(Field(leaf, ContentsOffset, 4), contents);

    /// <summary>The leaf's vis cluster, or -1 when it is solid.</summary>
    /// <param name="leaf">A leaf index.</param>
    /// <returns>The cluster index.</returns>
    internal short Cluster(int leaf) =>
        BinaryPrimitives.ReadInt16LittleEndian(Field(leaf, ClusterOffset, 2));

    /// <summary>The leaf's per-leaf flags.</summary>
    /// <param name="leaf">A leaf index.</param>
    /// <returns>The top seven bits of the area/flags word.</returns>
    internal LeafFlags Flags(int leaf) => (LeafFlags)((AreaFlags(leaf) >> 9) & 0x7F);

    /// <summary>Adds per-leaf flags, leaving the area alone.</summary>
    /// <param name="leaf">A leaf index.</param>
    /// <param name="flags">The flags to set.</param>
    internal void AddFlags(int leaf, LeafFlags flags)
    {
        ushort word = AreaFlags(leaf);
        int updated = word | (((int)flags & 0x7F) << 9);
        BinaryPrimitives.WriteUInt16LittleEndian(Field(leaf, AreaFlagsOffset, 2), (ushort)updated);
    }

    /// <summary>The leaf's LUMP_LEAFWATERDATA index, or -1.</summary>
    /// <param name="leaf">A leaf index.</param>
    /// <returns>The index.</returns>
    internal short LeafWaterDataId(int leaf) =>
        BinaryPrimitives.ReadInt16LittleEndian(Field(leaf, LeafWaterDataIdOffset, 2));

    /// <summary>The first entry of the leaf's LUMP_LEAFFACES run.</summary>
    /// <param name="leaf">A leaf index.</param>
    /// <returns>The index.</returns>
    internal ushort FirstLeafFace(int leaf) =>
        BinaryPrimitives.ReadUInt16LittleEndian(Field(leaf, FirstLeafFaceOffset, 2));

    /// <summary>How many entries the leaf's LUMP_LEAFFACES run holds.</summary>
    /// <param name="leaf">A leaf index.</param>
    /// <returns>The count.</returns>
    internal ushort NumLeafFaces(int leaf) =>
        BinaryPrimitives.ReadUInt16LittleEndian(Field(leaf, NumLeafFacesOffset, 2));

    /// <summary>The leaf's integer bounding box.</summary>
    /// <param name="leaf">A leaf index.</param>
    /// <param name="mins">The minimum corner.</param>
    /// <param name="maxs">The maximum corner.</param>
    internal void Bounds(int leaf, out (short X, short Y, short Z) mins, out (short X, short Y, short Z) maxs)
    {
        Span<byte> min = Field(leaf, MinsOffset, 6);
        Span<byte> max = Field(leaf, MaxsOffset, 6);
        mins = (
            BinaryPrimitives.ReadInt16LittleEndian(min),
            BinaryPrimitives.ReadInt16LittleEndian(min[2..]),
            BinaryPrimitives.ReadInt16LittleEndian(min[4..]));
        maxs = (
            BinaryPrimitives.ReadInt16LittleEndian(max),
            BinaryPrimitives.ReadInt16LittleEndian(max[2..]),
            BinaryPrimitives.ReadInt16LittleEndian(max[4..]));
    }

    private ushort AreaFlags(int leaf) =>
        BinaryPrimitives.ReadUInt16LittleEndian(Field(leaf, AreaFlagsOffset, 2));

    private Span<byte> Field(int leaf, int offset, int length) =>
        _bytes.Span.Slice((leaf * _stride) + offset, length);
}
