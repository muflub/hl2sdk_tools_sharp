using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SourceSharp.MapFormats.Bsp.Structs;

/// <summary>
/// The <c>dprp</c> game lump: the detail prop model dictionary, the sprite
/// dictionary and the props.
/// </summary>
/// <remarks>
/// Laid out as <c>int modelCount</c>, that many
/// <see cref="DetailObjectDictLump"/>, <c>int spriteCount</c>, that many
/// <see cref="DetailSpriteDictLump"/>, <c>int propCount</c>, that many
/// <see cref="DetailObjectLump"/>.
/// Only version 4 exists; the reference reader bails out of
/// anything lower rather than guessing.
/// </remarks>
public sealed class DetailPropLump
{
    /// <summary>The detail model paths.</summary>
    public List<string> ModelNames { get; } = [];

    /// <summary>The detail sprite definitions.</summary>
    public List<DetailSpriteDictLump> Sprites { get; } = [];

    /// <summary>The detail props.</summary>
    public List<DetailObjectLump> Props { get; } = [];

    /// <summary>Decodes a <c>dprp</c> game lump entry.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The decoded lump.</returns>
    /// <exception cref="InvalidBspException">
    /// The entry is not <c>dprp</c>, is not version 4, or runs short.
    /// </exception>
    public static DetailPropLump Read(GameLumpEntry entry)
    {
        if (entry.Id != GameLumpId.MakeId(GameLumpId.DetailProps))
        {
            throw new InvalidBspException(
                $"\"{GameLumpId.CodeOf(entry.Id)}\" is not the detail prop game lump");
        }

        if (entry.Version != GameLumpVersions.DetailProps)
        {
            throw new InvalidBspException(
                $"detail prop lump version {entry.Version} is not 4; the format defines "
                + "only version 4 and a game refuses anything else");
        }

        DetailPropLump lump = new();
        ReadOnlySpan<byte> bytes = entry.Data.Span;
        int offset = 0;

        int modelCount = TakeInt(bytes, ref offset);
        foreach (DetailObjectDictLump dict in MemoryMarshal.Cast<byte, DetailObjectDictLump>(
            Take(bytes, ref offset, modelCount * Unsafe.SizeOf<DetailObjectDictLump>())))
        {
            lump.ModelNames.Add(StaticPropLump.FixedString(dict.Name));
        }

        int spriteCount = TakeInt(bytes, ref offset);
        lump.Sprites.AddRange(MemoryMarshal.Cast<byte, DetailSpriteDictLump>(
            Take(bytes, ref offset, spriteCount * Unsafe.SizeOf<DetailSpriteDictLump>())));

        int propCount = TakeInt(bytes, ref offset);
        lump.Props.AddRange(MemoryMarshal.Cast<byte, DetailObjectLump>(
            Take(bytes, ref offset, propCount * Unsafe.SizeOf<DetailObjectLump>())));

        return lump;
    }

    /// <summary>Encodes this lump back to a <c>dprp</c> game lump entry.</summary>
    /// <returns>The entry, at version 4.</returns>
    public GameLumpEntry Write()
    {
        int length = sizeof(int)
            + (ModelNames.Count * Unsafe.SizeOf<DetailObjectDictLump>())
            + sizeof(int)
            + (Sprites.Count * Unsafe.SizeOf<DetailSpriteDictLump>())
            + sizeof(int)
            + (Props.Count * Unsafe.SizeOf<DetailObjectLump>());

        byte[] bytes = new byte[length];
        Span<byte> span = bytes;
        int offset = 0;

        PutInt(span, ref offset, ModelNames.Count);
        foreach (string name in ModelNames)
        {
            DetailObjectDictLump dict = default;
            StaticPropLump.WriteFixedString(name, dict.Name);
            MemoryMarshal.Write(span[offset..], in dict);
            offset += Unsafe.SizeOf<DetailObjectDictLump>();
        }

        PutInt(span, ref offset, Sprites.Count);
        offset += Copy(span[offset..], CollectionsMarshal.AsSpan(Sprites));

        PutInt(span, ref offset, Props.Count);
        offset += Copy(span[offset..], CollectionsMarshal.AsSpan(Props));

        return new GameLumpEntry(
            GameLumpId.MakeId(GameLumpId.DetailProps),
            0,
            GameLumpVersions.DetailProps,
            bytes);
    }

    private static int Copy<T>(Span<byte> destination, ReadOnlySpan<T> items)
        where T : unmanaged
    {
        ReadOnlySpan<byte> source = MemoryMarshal.AsBytes(items);
        source.CopyTo(destination);
        return source.Length;
    }

    private static int TakeInt(ReadOnlySpan<byte> bytes, ref int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(Take(bytes, ref offset, sizeof(int)));

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> bytes, ref int offset, int count)
    {
        if (count < 0 || offset + count > bytes.Length)
        {
            throw new InvalidBspException(
                $"the detail prop game lump runs short: {count} bytes wanted at offset {offset} "
                + $"of {bytes.Length}");
        }

        ReadOnlySpan<byte> slice = bytes.Slice(offset, count);
        offset += count;
        return slice;
    }

    private static void PutInt(Span<byte> span, ref int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, sizeof(int)), value);
        offset += sizeof(int);
    }
}

/// <summary>
/// The <c>dplt</c> and <c>dplh</c> game lumps: a bare array of
/// <see cref="DetailPropLightstylesLump"/> with no count prefix.
/// </summary>
/// <remarks>
/// Unlike <c>sprp</c> and <c>dprp</c>, these two really are plain arrays --
/// the count is the lump length divided by five. Both are version 0
/// in the reference layout and differ only in which lighting
/// range they hold.
/// </remarks>
public static class DetailPropLightingLump
{
    /// <summary>Decodes a <c>dplt</c> or <c>dplh</c> entry.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The samples.</returns>
    /// <exception cref="InvalidBspException">
    /// The entry is neither <c>dplt</c> nor <c>dplh</c>, or its length is not a
    /// whole number of samples.
    /// </exception>
    public static ReadOnlySpan<DetailPropLightstylesLump> Read(GameLumpEntry entry)
    {
        if (entry.Id != GameLumpId.MakeId(GameLumpId.DetailPropLighting) &&
            entry.Id != GameLumpId.MakeId(GameLumpId.DetailPropLightingHdr))
        {
            throw new InvalidBspException(
                $"\"{GameLumpId.CodeOf(entry.Id)}\" is not a detail prop lighting game lump");
        }

        return BspStructView.As<DetailPropLightstylesLump>(entry.Data.Span);
    }

    /// <summary>Encodes samples back to a game lump entry.</summary>
    /// <param name="samples">The samples.</param>
    /// <param name="hdr">True for <c>dplh</c>, false for <c>dplt</c>.</param>
    /// <returns>The entry, at version 0.</returns>
    public static GameLumpEntry Write(ReadOnlySpan<DetailPropLightstylesLump> samples, bool hdr)
    {
        byte[] bytes = MemoryMarshal.AsBytes(samples).ToArray();
        return new GameLumpEntry(
            GameLumpId.MakeId(hdr ? GameLumpId.DetailPropLightingHdr : GameLumpId.DetailPropLighting),
            0,
            (ushort)(hdr ? GameLumpVersions.DetailPropLightingHdr : GameLumpVersions.DetailPropLighting),
            bytes);
    }
}
