//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapFormats.Bsp.Structs;

/// <summary>
/// One static prop, in the shape version 10 stores, whatever version it was
/// read from.
/// </summary>
/// <remarks>
/// A class rather than one of the four on-disk structs because a caller that
/// wants to know where a prop is should not have to know which compiler wrote
/// the map. <see cref="SourceVersion"/> records what it was read from so that
/// information is not lost.
/// </remarks>
public sealed class StaticProp
{
    /// <summary>The prop's world position.</summary>
    public Vec3 Origin { get; set; }

    /// <summary>The prop's orientation: pitch, yaw, roll.</summary>
    public Vec3 Angles { get; set; }

    /// <summary>The index into the model dictionary.</summary>
    public ushort PropType { get; set; }

    /// <summary>The first entry of this prop's run in the leaf list.</summary>
    public ushort FirstLeaf { get; set; }

    /// <summary>How many leaves the run holds.</summary>
    public ushort LeafCount { get; set; }

    /// <summary>The prop's solidity, a <c>SOLID_</c> value.</summary>
    public byte Solid { get; set; }

    /// <summary>The prop's skin index.</summary>
    public int Skin { get; set; }

    /// <summary>The distance at which the prop starts to fade.</summary>
    public float FadeMinDist { get; set; }

    /// <summary>The distance at which it is gone.</summary>
    public float FadeMaxDist { get; set; }

    /// <summary>The point vrad lights the prop from.</summary>
    public Vec3 LightingOrigin { get; set; }

    /// <summary>A per-prop multiplier on the engine's fade distance.</summary>
    public float ForcedFadeScale { get; set; }

    /// <summary>The lowest DirectX level that draws this prop.</summary>
    public ushort MinDxLevel { get; set; }

    /// <summary>The highest DirectX level that draws it.</summary>
    public ushort MaxDxLevel { get; set; }

    /// <summary>The prop's flags.</summary>
    public StaticPropFlags Flags { get; set; }

    /// <summary>The per-texel lightmap width vrad allocates.</summary>
    public ushort LightmapResolutionX { get; set; }

    /// <summary>The per-texel lightmap height.</summary>
    public ushort LightmapResolutionY { get; set; }

    /// <summary>The <c>sprp</c> version this prop was read from.</summary>
    public int SourceVersion { get; set; } = GameLumpVersions.StaticProps;
}

/// <summary>
/// The <c>sprp</c> game lump: the model dictionary, the leaf list and the
/// props.
/// </summary>
/// <remarks>
/// <para>
/// The lump is <c>int dictCount</c>, that many
/// <see cref="StaticPropDictLump"/>, <c>int leafCount</c>, that many
/// <see cref="StaticPropLeafLump"/>, <c>int propCount</c>, that many props,
/// which is the order the reference writer emits them in. The leaf list comes
/// BEFORE the props, which is easy to get backwards from the order the three
/// dictionaries are declared in.
/// </para>
/// <para>
/// Only the prop struct changed between versions; the three counts and the
/// dictionary never did.
/// </para>
/// </remarks>
public sealed class StaticPropLump
{
    /// <summary>The model paths, indexed by <see cref="StaticProp.PropType"/>.</summary>
    public List<string> ModelNames { get; } = [];

    /// <summary>The leaf indices the props' runs point into.</summary>
    public List<ushort> LeafEntries { get; } = [];

    /// <summary>The props.</summary>
    public List<StaticProp> Props { get; } = [];

    /// <summary>The version the lump was read from.</summary>
    public int Version { get; private set; } = GameLumpVersions.StaticProps;

    /// <summary>The <c>sprp</c> versions this reader accepts.</summary>
    /// <remarks>
    /// <para>
    /// 4, 5 and 6 are what the reference reader allows, and 10 is what
    /// the reference layout declares for this branch. There is no 7, 8 or 9
    /// struct anywhere in this tree, and inventing one would be guessing at
    /// another game's format.
    /// </para>
    /// <para>
    /// Note that stock behaviour is stale here: the reference swapper reads
    /// version 6 with <c>StaticPropLump_t</c>, which is the 72-byte version 10
    /// struct. That is a bug in it, not a format rule, and it is not
    /// reproduced.
    /// </para>
    /// </remarks>
    public static ReadOnlySpan<int> SupportedVersions => [4, 5, 6, 10];

    /// <summary>Decodes a game lump entry.</summary>
    /// <param name="entry">The entry, whose <see cref="GameLumpEntry.Id"/> must be <c>sprp</c>.</param>
    /// <returns>The decoded lump.</returns>
    /// <exception cref="InvalidBspException">
    /// The entry is not <c>sprp</c>, its version is unsupported, or it runs short.
    /// </exception>
    public static StaticPropLump Read(GameLumpEntry entry)
    {
        if (entry.Id != GameLumpId.MakeId(GameLumpId.StaticProps))
        {
            throw new InvalidBspException(
                $"\"{GameLumpId.CodeOf(entry.Id)}\" is not the static prop game lump");
        }

        StaticPropLump lump = new() { Version = entry.Version };
        ReadOnlySpan<byte> bytes = entry.Data.Span;
        int offset = 0;

        int dictCount = TakeInt(bytes, ref offset);
        foreach (StaticPropDictLump dict in MemoryMarshal.Cast<byte, StaticPropDictLump>(
            Take(bytes, ref offset, dictCount * Unsafe.SizeOf<StaticPropDictLump>())))
        {
            lump.ModelNames.Add(FixedString(dict.Name));
        }

        int leafCount = TakeInt(bytes, ref offset);
        foreach (StaticPropLeafLump leaf in MemoryMarshal.Cast<byte, StaticPropLeafLump>(
            Take(bytes, ref offset, leafCount * Unsafe.SizeOf<StaticPropLeafLump>())))
        {
            lump.LeafEntries.Add(leaf.Leaf);
        }

        int propCount = TakeInt(bytes, ref offset);
        switch (entry.Version)
        {
            case 4:
                foreach (StaticPropLumpV4 prop in MemoryMarshal.Cast<byte, StaticPropLumpV4>(
                    Take(bytes, ref offset, propCount * Unsafe.SizeOf<StaticPropLumpV4>())))
                {
                    lump.Props.Add(FromV4(prop));
                }

                break;

            case 5:
                foreach (StaticPropLumpV5 prop in MemoryMarshal.Cast<byte, StaticPropLumpV5>(
                    Take(bytes, ref offset, propCount * Unsafe.SizeOf<StaticPropLumpV5>())))
                {
                    lump.Props.Add(FromV5(prop));
                }

                break;

            case 6:
                foreach (StaticPropLumpV6 prop in MemoryMarshal.Cast<byte, StaticPropLumpV6>(
                    Take(bytes, ref offset, propCount * Unsafe.SizeOf<StaticPropLumpV6>())))
                {
                    lump.Props.Add(FromV6(prop));
                }

                break;

            case 10:
                foreach (StaticPropLumpV10 prop in MemoryMarshal.Cast<byte, StaticPropLumpV10>(
                    Take(bytes, ref offset, propCount * Unsafe.SizeOf<StaticPropLumpV10>())))
                {
                    lump.Props.Add(FromV10(prop));
                }

                break;

            default:
                throw new InvalidBspException(
                    $"static prop lump version {entry.Version} has no struct in this tree; "
                    + "the format defines 4, 5, 6 and 10");
        }

        return lump;
    }

    /// <summary>Encodes this lump back to a game lump entry at version 10.</summary>
    /// <returns>The entry, ready to put in <see cref="BspData.GameLumps"/>.</returns>
    /// <remarks>
    /// Always version 10, because that is the version the reference layout
    /// declares for this branch and the one the reference writer emits.
    /// A map read at version 4 and written back therefore UPGRADES, which is
    /// what vbsp does to it too.
    /// </remarks>
    public GameLumpEntry Write()
    {
        int propSize = Unsafe.SizeOf<StaticPropLumpV10>();
        int length = sizeof(int)
            + (ModelNames.Count * Unsafe.SizeOf<StaticPropDictLump>())
            + sizeof(int)
            + (LeafEntries.Count * Unsafe.SizeOf<StaticPropLeafLump>())
            + sizeof(int)
            + (Props.Count * propSize);

        byte[] bytes = new byte[length];
        Span<byte> span = bytes;
        int offset = 0;

        PutInt(span, ref offset, ModelNames.Count);
        foreach (string name in ModelNames)
        {
            StaticPropDictLump dict = default;
            WriteFixedString(name, dict.Name);
            MemoryMarshal.Write(span[offset..], in dict);
            offset += Unsafe.SizeOf<StaticPropDictLump>();
        }

        PutInt(span, ref offset, LeafEntries.Count);
        foreach (ushort leaf in LeafEntries)
        {
            StaticPropLeafLump entry = new() { Leaf = leaf };
            MemoryMarshal.Write(span[offset..], in entry);
            offset += Unsafe.SizeOf<StaticPropLeafLump>();
        }

        PutInt(span, ref offset, Props.Count);
        foreach (StaticProp prop in Props)
        {
            StaticPropLumpV10 raw = ToV10(prop);
            MemoryMarshal.Write(span[offset..], in raw);
            offset += propSize;
        }

        return new GameLumpEntry(
            GameLumpId.MakeId(GameLumpId.StaticProps),
            0,
            GameLumpVersions.StaticProps,
            bytes);
    }

    private static StaticProp FromV4(in StaticPropLumpV4 prop) => new()
    {
        Origin = prop.Origin,
        Angles = prop.Angles,
        PropType = prop.PropType,
        FirstLeaf = prop.FirstLeaf,
        LeafCount = prop.LeafCount,
        Solid = prop.Solid,
        Skin = prop.Skin,
        FadeMinDist = prop.FadeMinDist,
        FadeMaxDist = prop.FadeMaxDist,
        LightingOrigin = prop.LightingOrigin,

        // The version 4 upgrade path sets these five
        // explicitly rather than leaving them zero-initialised, and the fade
        // scale of 1.0f is the one that matters: a zero there makes the prop
        // never fade at all.
        ForcedFadeScale = 1.0f,
        MinDxLevel = 0,
        MaxDxLevel = 0,
        LightmapResolutionX = 0,
        LightmapResolutionY = 0,

        // "Older versions don't want this." vrad would
        // otherwise try to build a per-texel lightmap for a prop compiled
        // before per-texel lighting existed.
        Flags = (StaticPropFlags)prop.Flags | StaticPropFlags.NoPerTexelLighting,
        SourceVersion = 4,
    };

    private static StaticProp FromV5(in StaticPropLumpV5 prop)
    {
        // The reference upgrade path reinterprets the V5 as a V4 and runs the
        // V4 path, then overwrites the fade scale. The two structs share a
        // prefix, so the cast is valid; reproduced field by field here.
        StaticProp result = FromV4(new StaticPropLumpV4
        {
            Origin = prop.Origin,
            Angles = prop.Angles,
            PropType = prop.PropType,
            FirstLeaf = prop.FirstLeaf,
            LeafCount = prop.LeafCount,
            Solid = prop.Solid,
            Flags = prop.Flags,
            Skin = prop.Skin,
            FadeMinDist = prop.FadeMinDist,
            FadeMaxDist = prop.FadeMaxDist,
            LightingOrigin = prop.LightingOrigin,
        });

        result.ForcedFadeScale = prop.ForcedFadeScale;
        result.SourceVersion = 5;
        return result;
    }

    private static StaticProp FromV6(in StaticPropLumpV6 prop)
    {
        StaticProp result = FromV5(new StaticPropLumpV5
        {
            Origin = prop.Origin,
            Angles = prop.Angles,
            PropType = prop.PropType,
            FirstLeaf = prop.FirstLeaf,
            LeafCount = prop.LeafCount,
            Solid = prop.Solid,
            Flags = prop.Flags,
            Skin = prop.Skin,
            FadeMinDist = prop.FadeMinDist,
            FadeMaxDist = prop.FadeMaxDist,
            LightingOrigin = prop.LightingOrigin,
            ForcedFadeScale = prop.ForcedFadeScale,
        });

        result.MinDxLevel = prop.MinDxLevel;
        result.MaxDxLevel = prop.MaxDxLevel;
        result.SourceVersion = 6;
        return result;
    }

    private static StaticProp FromV10(in StaticPropLumpV10 prop) => new()
    {
        Origin = prop.Origin,
        Angles = prop.Angles,
        PropType = prop.PropType,
        FirstLeaf = prop.FirstLeaf,
        LeafCount = prop.LeafCount,
        Solid = prop.Solid,
        Skin = prop.Skin,
        FadeMinDist = prop.FadeMinDist,
        FadeMaxDist = prop.FadeMaxDist,
        LightingOrigin = prop.LightingOrigin,
        ForcedFadeScale = prop.ForcedFadeScale,
        MinDxLevel = prop.MinDxLevel,
        MaxDxLevel = prop.MaxDxLevel,
        Flags = (StaticPropFlags)prop.Flags,
        LightmapResolutionX = prop.LightmapResolutionX,
        LightmapResolutionY = prop.LightmapResolutionY,
        SourceVersion = 10,
    };

    private static StaticPropLumpV10 ToV10(StaticProp prop) => new()
    {
        Origin = prop.Origin,
        Angles = prop.Angles,
        PropType = prop.PropType,
        FirstLeaf = prop.FirstLeaf,
        LeafCount = prop.LeafCount,
        Solid = prop.Solid,
        Padding = 0,
        Skin = prop.Skin,
        FadeMinDist = prop.FadeMinDist,
        FadeMaxDist = prop.FadeMaxDist,
        LightingOrigin = prop.LightingOrigin,
        ForcedFadeScale = prop.ForcedFadeScale,
        MinDxLevel = prop.MinDxLevel,
        MaxDxLevel = prop.MaxDxLevel,
        Flags = (uint)prop.Flags,
        LightmapResolutionX = prop.LightmapResolutionX,
        LightmapResolutionY = prop.LightmapResolutionY,
    };

    /// <summary>A NUL-padded fixed-width name field as a string.</summary>
    /// <param name="name">The 128-byte field.</param>
    /// <returns>The text up to the first NUL.</returns>
    internal static string FixedString(ByteArray128 name)
    {
        ReadOnlySpan<byte> span = name;
        int end = span.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? span : span[..end]);
    }

    /// <summary>Fills a fixed-width name field, NUL-padding the rest.</summary>
    /// <param name="value">The name.</param>
    /// <param name="destination">The field to fill.</param>
    /// <exception cref="ArgumentException">The name does not fit in 127 bytes plus a NUL.</exception>
    internal static void WriteFixedString(string value, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] encoded = Encoding.Latin1.GetBytes(value);
        if (encoded.Length >= destination.Length)
        {
            throw new ArgumentException(
                $"\"{value}\" is {encoded.Length} bytes and does not fit a "
                + $"{destination.Length}-byte NUL-terminated field",
                nameof(value));
        }

        destination.Clear();
        encoded.CopyTo(destination);
    }

    private static int TakeInt(ReadOnlySpan<byte> bytes, ref int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(Take(bytes, ref offset, sizeof(int)));

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> bytes, ref int offset, int count)
    {
        if (count < 0 || offset + count > bytes.Length)
        {
            throw new InvalidBspException(
                $"the static prop game lump runs short: {count} bytes wanted at offset {offset} "
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
