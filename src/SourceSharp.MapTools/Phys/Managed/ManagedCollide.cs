using System.Buffers.Binary;

namespace SourceSharp.MapTools.Phys.Managed;

/// <summary>
/// A managed <c>CPhysCollide</c>: a compact surface (<c>CPhysCollideCompactSurface</c>) or a
/// virtual mesh's packed bounding hull (<c>CPhysCollideVirtualMesh</c>).
/// </summary>
internal sealed class ManagedCollide
{
    private ManagedCollide(byte[]? surface, byte[]? packedHull, bool isVirtualMesh)
    {
        Surface = surface;
        PackedHull = packedHull;
        IsVirtualMesh = isVirtualMesh;
    }

    /// <summary>The IVP compact surface (byte_size long), or null for a virtual mesh.</summary>
    public byte[]? Surface { get; }

    /// <summary>A virtual mesh's serialisation (<c>virtualmeshhull_t</c>), or null.</summary>
    public byte[]? PackedHull { get; }

    /// <summary>True for <c>CPhysCollideVirtualMesh</c>.</summary>
    public bool IsVirtualMesh { get; }

    /// <summary><c>m_orthoAreas</c>: (1, 1, 1) until drag areas are computed or read back.</summary>
    public (float X, float Y, float Z) OrthoAreas { get; set; } = (1f, 1f, 1f);

    /// <summary>A compact-surface collide.</summary>
    /// <param name="surface">The surface bytes.</param>
    /// <returns>The collide.</returns>
    public static ManagedCollide FromSurface(byte[] surface) => new(surface, null, false);

    /// <summary>A virtual-mesh collide.</summary>
    /// <param name="packedHull">Its packed hull, or null when it has none.</param>
    /// <returns>The collide.</returns>
    public static ManagedCollide FromVirtualMesh(byte[]? packedHull) => new(null, packedHull, true);

    /// <summary>The compact surface, or an error naming what this collide is.</summary>
    public byte[] RequireSurface() =>
        Surface ?? throw new NotSupportedException(
            "the managed cooker answers this query for compact surfaces only; a virtual mesh has no surface");

    /// <summary><c>dummy[0]</c>: the index a collide was unserialised with.</summary>
    public int Index
    {
        get => Surface is null ? 0 : BinaryPrimitives.ReadInt32LittleEndian(Surface.AsSpan(0x24));
        set
        {
            if (Surface is not null)
            {
                BinaryPrimitives.WriteInt32LittleEndian(Surface.AsSpan(0x24), value);
            }
        }
    }

    /// <summary><c>GetSerializationSize</c>.</summary>
    public int SerializedSize => Surface is not null ? VphyWriter.HeaderSize + Surface.Length : PackedHull?.Length ?? 0;

    /// <summary><c>SerializeToBuffer</c>.</summary>
    /// <returns>The bytes.</returns>
    public byte[] Serialize() =>
        Surface is not null ? VphyWriter.Serialize(Surface, OrthoAreas) : (byte[]?)PackedHull?.Clone() ?? [];

    /// <summary>
    /// <c>CPhysCollide::UnserializeFromBuffer</c> (physics_collide.cpp:310): a <c>VPHY</c> blob, or
    /// a bare compact surface; <c>dummy[0]</c> becomes <paramref name="index"/>.
    /// </summary>
    /// <param name="blob">The bytes.</param>
    /// <param name="index">The collide index.</param>
    /// <returns>The collide, or null for a format this does not load (MOPP, unknown).</returns>
    public static ManagedCollide? Unserialize(ReadOnlySpan<byte> blob, int index)
    {
        if (blob.Length >= VphyWriter.HeaderSize && BinaryPrimitives.ReadUInt32LittleEndian(blob) == 0x59485056u)
        {
            short modelType = BinaryPrimitives.ReadInt16LittleEndian(blob[6..]);
            if (modelType != 0)
            {
                return null; // COLLIDE_MOPP: "Null physics model" in this build
            }

            int size = BinaryPrimitives.ReadInt32LittleEndian(blob[8..]);
            var collide = FromSurface(blob.Slice(VphyWriter.HeaderSize, size).ToArray());
            collide.Index = index;
            collide.OrthoAreas = (
                BinaryPrimitives.ReadSingleLittleEndian(blob[12..]),
                BinaryPrimitives.ReadSingleLittleEndian(blob[16..]),
                BinaryPrimitives.ReadSingleLittleEndian(blob[20..]));
            return collide;
        }

        if (blob.Length < 0x30)
        {
            return null;
        }

        uint id = BinaryPrimitives.ReadUInt32LittleEndian(blob[0x2c..]);
        if (id is 0x53505649u or 0x49565053u or 0u)
        {
            int size = (int)(BinaryPrimitives.ReadUInt32LittleEndian(blob[0x1c..]) >> 8);
            var bare = FromSurface(blob[..Math.Min(size, blob.Length)].ToArray());
            bare.Index = index;
            return bare;
        }

        return null;
    }
}
