using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SourceSharp.MapFormats.Bsp.Structs;

/// <summary>
/// LUMP_OCCLUSION, decoded.
/// </summary>
/// <remarks>
/// <para>
/// The only lump in the file that is three different arrays in one. It is laid
/// out as <c>int occluderCount</c>, that many
/// <see cref="DOccluderData"/>, <c>int polyCount</c>, that many
/// <see cref="DOccluderPolyData"/>, <c>int indexCount</c>, that many
/// <c>int</c> (<c>bsplib.cpp:1341</c>, <c>AddOcclusionLump</c>).
/// </para>
/// <para>
/// So it cannot be a cast, and a length check against any one struct is
/// meaningless for it -- which is why <see cref="BspLumpLayout.ElementSize"/>
/// answers null rather than pretending.
/// </para>
/// </remarks>
public sealed class OcclusionLump
{
    /// <summary>The occluders.</summary>
    public List<DOccluderData> Occluders { get; } = [];

    /// <summary>The polygons the occluders are built from.</summary>
    public List<DOccluderPolyData> Polys { get; } = [];

    /// <summary>The vertex indices the polygons are built from.</summary>
    public List<int> VertexIndices { get; } = [];

    /// <summary>The lump version this decoder writes (<c>bspfile.h:364</c>).</summary>
    public const int CurrentVersion = 2;

    /// <summary>Decodes a lump's bytes.</summary>
    /// <param name="lump">The lump, with the version its header recorded.</param>
    /// <returns>The decoded contents.</returns>
    /// <exception cref="InvalidBspException">
    /// The lump's version is not one this decoder knows, or it runs short.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Version 0 means an EMPTY lump and is not an error: <c>bsplib.cpp:1423</c>
    /// has a <c>case 0: break;</c> that does nothing at all.
    /// </para>
    /// <para>
    /// Version 1 is read here with <see cref="DOccluderDataV1"/>, which has no
    /// <see cref="DOccluderData.Area"/> field (<c>bspfile.h:540</c>). Stock
    /// <c>bsplib</c> does NOT do this -- it calls <c>Error()</c> on any version
    /// but 0 and 2 -- but the struct is in the header for a reason and a map
    /// tool that refuses to open an old map is worse than one that reads it.
    /// </para>
    /// </remarks>
    public static OcclusionLump Read(BspLumpData lump)
    {
        OcclusionLump result = new();
        if (lump.IsEmpty)
        {
            return result;
        }

        switch (lump.Version)
        {
            case 0:
                // bsplib.cpp:1423 -- a version 0 occlusion lump is read as
                // nothing whatever its length says.
                return result;

            case 1:
                result.ReadBody<DOccluderDataV1>(lump.Data.Span, upgradeFromV1: true);
                return result;

            case 2:
                result.ReadBody<DOccluderData>(lump.Data.Span, upgradeFromV1: false);
                return result;

            default:
                throw new InvalidBspException(
                    $"occlusion lump version {lump.Version} is not one bspfile.h defines; "
                    + "bsplib.cpp:1428 errors out on it too");
        }
    }

    private void ReadBody<TOccluder>(ReadOnlySpan<byte> bytes, bool upgradeFromV1)
        where TOccluder : unmanaged
    {
        int offset = 0;
        int occluderCount = TakeInt(bytes, ref offset);
        int occluderSize = Unsafe.SizeOf<TOccluder>();
        ReadOnlySpan<TOccluder> occluders =
            MemoryMarshal.Cast<byte, TOccluder>(Take(bytes, ref offset, occluderCount * occluderSize));

        foreach (TOccluder occluder in occluders)
        {
            if (upgradeFromV1)
            {
                DOccluderDataV1 v1 = Unsafe.As<TOccluder, DOccluderDataV1>(ref Unsafe.AsRef(in occluder));
                Occluders.Add(new DOccluderData
                {
                    Flags = v1.Flags,
                    FirstPoly = v1.FirstPoly,
                    PolyCount = v1.PolyCount,
                    Mins = v1.Mins,
                    Maxs = v1.Maxs,

                    // Version 1 had no area at all. Zero is the only honest
                    // value: area 0 is the outside-of-all-areas default the
                    // engine already uses for geometry no areaportal encloses.
                    Area = 0,
                });
            }
            else
            {
                Occluders.Add(Unsafe.As<TOccluder, DOccluderData>(ref Unsafe.AsRef(in occluder)));
            }
        }

        int polyCount = TakeInt(bytes, ref offset);
        Polys.AddRange(MemoryMarshal.Cast<byte, DOccluderPolyData>(
            Take(bytes, ref offset, polyCount * Unsafe.SizeOf<DOccluderPolyData>())));

        int indexCount = TakeInt(bytes, ref offset);
        VertexIndices.AddRange(MemoryMarshal.Cast<byte, int>(
            Take(bytes, ref offset, indexCount * sizeof(int))));
    }

    private static int TakeInt(ReadOnlySpan<byte> bytes, ref int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(Take(bytes, ref offset, sizeof(int)));

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> bytes, ref int offset, int count)
    {
        if (count < 0 || offset + count > bytes.Length)
        {
            throw new InvalidBspException(
                $"the occlusion lump runs short: {count} bytes wanted at offset {offset} of "
                + $"{bytes.Length}");
        }

        ReadOnlySpan<byte> slice = bytes.Slice(offset, count);
        offset += count;
        return slice;
    }

    /// <summary>Encodes this lump back to bytes, at version 2.</summary>
    /// <returns>The lump, with <see cref="CurrentVersion"/> recorded.</returns>
    public BspLumpData Write()
    {
        int length = sizeof(int)
            + (Occluders.Count * Unsafe.SizeOf<DOccluderData>())
            + sizeof(int)
            + (Polys.Count * Unsafe.SizeOf<DOccluderPolyData>())
            + sizeof(int)
            + (VertexIndices.Count * sizeof(int));

        byte[] bytes = new byte[length];
        Span<byte> span = bytes;
        int offset = 0;

        PutInt(span, ref offset, Occluders.Count);
        PutStructs(span, ref offset, CollectionsMarshal.AsSpan(Occluders));
        PutInt(span, ref offset, Polys.Count);
        PutStructs(span, ref offset, CollectionsMarshal.AsSpan(Polys));
        PutInt(span, ref offset, VertexIndices.Count);
        PutStructs(span, ref offset, CollectionsMarshal.AsSpan(VertexIndices));

        return new BspLumpData(bytes, CurrentVersion, 0);
    }

    private static void PutInt(Span<byte> span, ref int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, sizeof(int)), value);
        offset += sizeof(int);
    }

    private static void PutStructs<T>(Span<byte> span, ref int offset, ReadOnlySpan<T> items)
        where T : unmanaged
    {
        ReadOnlySpan<byte> source = MemoryMarshal.AsBytes(items);
        source.CopyTo(span[offset..]);
        offset += source.Length;
    }
}
