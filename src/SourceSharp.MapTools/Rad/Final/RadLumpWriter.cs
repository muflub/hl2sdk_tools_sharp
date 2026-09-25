using System.Buffers.Binary;
using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapTools.Rad.Light;

namespace SourceSharp.MapTools.Rad.Final;

/// <summary>
/// The lumps one vrad pass changes, written back into the map: what stock's
/// <c>WriteBSPFile</c> (<c>vrad.cpp:2342</c>) takes from the globals the pass
/// filled.
/// </summary>
/// <remarks>
/// <para>
/// An LDR pass writes <c>LUMP_FACES</c> (styles and <c>lightofs</c>),
/// <c>LUMP_LIGHTING</c> and <c>LUMP_WORLDLIGHTS</c>; an HDR pass writes
/// <c>LUMP_FACES_HDR</c>, <c>LUMP_LIGHTING_HDR</c> and
/// <c>LUMP_WORLDLIGHTS_HDR</c> and leaves the LDR ones as they were -- which is
/// how <c>-both</c> ends up with all six. Either pass also rewrites
/// <c>LUMP_VERTNORMALS</c>/<c>LUMP_VERTNORMALINDICES</c>
/// (<see cref="VradVertexNormals"/>) and the SKY/SKY2D bits of
/// <c>LUMP_LEAFS</c> when a light_environment recomputed them.
/// </para>
/// <para>
/// The HDR face lump starts as a copy of the LDR one when the map has none
/// (<c>vrad.cpp:2221-2229</c>). Every face's styles and <c>lightofs</c> are
/// replaced from the layout, so the copy's LDR values never survive.
/// </para>
/// </remarks>
public static class RadLumpWriter
{
    /// <summary><c>LUMP_LIGHTING_VERSION</c> (<c>bspfile.h</c>).</summary>
    public const int LightingVersion = 1;

    /// <summary><c>LUMP_FACES_VERSION</c>.</summary>
    public const int FacesVersion = 1;

    /// <summary>Byte offset of the area/flags word in both leaf versions.</summary>
    private const int LeafAreaFlagsOffset = 6;

    /// <summary>Writes one pass's lumps.</summary>
    /// <param name="bsp">The map, changed in place.</param>
    /// <param name="world">The pass.</param>
    /// <param name="lightData">The pass's lighting lump.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The world has no layout.</exception>
    public static void Write(BspData bsp, RadWorld world, byte[] lightData)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(lightData);

        LightmapLayout layout = world.Layout
            ?? throw new ArgumentException("the pass has no lightmap layout", nameof(world));
        bool hdr = world.Settings.Hdr;

        WriteFaces(bsp, layout, hdr);
        bsp.SetLump(hdr ? BspLump.LightingHdr : BspLump.Lighting, lightData, LightingVersion);
        bsp.SetLump(hdr ? BspLump.WorldLightsHdr : BspLump.WorldLights, world.WorldLightBytes(),
            bsp[hdr ? BspLump.WorldLightsHdr : BspLump.WorldLights].Version);

        (MapFormats.Geometry.Vec3[] normals, ushort[] indices) = VradVertexNormals.Save(world.Geometry, world.Neighbours);
        (byte[] n, byte[] x) = VradVertexNormals.ToLumps(normals, indices);
        bsp.SetLump(BspLump.VertNormals, n, bsp[BspLump.VertNormals].Version);
        bsp.SetLump(BspLump.VertNormalIndices, x, bsp[BspLump.VertNormalIndices].Version);

        WriteLeafFlags(bsp, world);
    }

    /// <summary>
    /// The face lump the pass lit, with each face's styles and <c>lightofs</c>
    /// from the layout.
    /// </summary>
    /// <param name="bsp">The map.</param>
    /// <param name="layout">The layout.</param>
    /// <param name="hdr">True for <c>LUMP_FACES_HDR</c>.</param>
    public static void WriteFaces(BspData bsp, LightmapLayout layout, bool hdr)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(layout);

        BspLumpData source = hdr && !bsp[BspLump.FacesHdr].IsEmpty
            ? bsp[BspLump.FacesHdr]
            : bsp[BspLump.Faces];

        byte[] bytes = source.Data.ToArray();
        Span<DFace> faces = MemoryMarshal.Cast<byte, DFace>(bytes.AsSpan());
        if (faces.Length != layout.LightOffsets.Length)
        {
            throw new ArgumentException(
                $"{faces.Length} faces in the map, {layout.LightOffsets.Length} in the layout", nameof(layout));
        }

        for (int f = 0; f < faces.Length; f++)
        {
            for (int k = 0; k < LightConstants.MaxLightmaps; k++)
            {
                faces[f].Styles[k] = layout.Styles[(f * LightConstants.MaxLightmaps) + k];
            }

            faces[f].LightOfs = layout.LightOffsets[f];
        }

        bsp.SetLump(hdr ? BspLump.FacesHdr : BspLump.Faces, bytes, source.Version == 0 ? FacesVersion : source.Version);
    }

    /// <summary><c>LVLFLAGS_BAKED_STATIC_PROP_LIGHTING_NONHDR</c> (<c>bspfile.h:395</c>).</summary>
    public const uint BakedStaticPropLightingLdr = 0x1;

    /// <summary><c>LVLFLAGS_BAKED_STATIC_PROP_LIGHTING_HDR</c> (<c>bspfile.h:396</c>).</summary>
    public const uint BakedStaticPropLightingHdr = 0x2;

    /// <summary>
    /// <c>LUMP_MAP_FLAGS</c> as <c>VRAD_LoadBSP</c> leaves <c>g_LevelFlags</c>
    /// (<c>vrad.cpp:2214-2219</c>).
    /// </summary>
    /// <param name="bsp">The map.</param>
    /// <param name="hdr">Whether this is the HDR pass.</param>
    /// <param name="staticPropLighting"><c>-StaticPropLighting</c>.</param>
    /// <remarks>
    /// With the switch, this pass's bit is set and the other survives;
    /// without it, BOTH bits are cleared -- so an LDR-only rerun without the
    /// switch forgets an earlier HDR bake. The lump is always written, four
    /// bytes (<c>bsplib.cpp:2699-2700</c>).
    /// </remarks>
    public static void WriteLevelFlags(BspData bsp, bool hdr, bool staticPropLighting)
    {
        ArgumentNullException.ThrowIfNull(bsp);

        BspLumpData lump = bsp[BspLump.MapFlags];
        uint flags = lump.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(lump.Data.Span) : 0u;

        flags = staticPropLighting
            ? flags | (hdr ? BakedStaticPropLightingHdr : BakedStaticPropLightingLdr)
            : flags & ~(BakedStaticPropLightingLdr | BakedStaticPropLightingHdr);

        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, flags);
        bsp.SetLump(BspLump.MapFlags, bytes, lump.Version);
    }

    /// <summary>
    /// The SKY and SKY2D bits <c>BuildVisForLightEnvironment</c> recomputed,
    /// written into <c>LUMP_LEAFS</c>; nothing when no light_environment ran it.
    /// </summary>
    /// <param name="bsp">The map.</param>
    /// <param name="world">The pass.</param>
    public static void WriteLeafFlags(BspData bsp, RadWorld world)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(world);

        LeafFlags[] flags = world.SkyLeaves.Flags;
        BspLumpData leafs = bsp[BspLump.Leafs];
        if (flags.Length == 0 || leafs.IsEmpty || leafs.Length % flags.Length != 0)
        {
            return;
        }

        int stride = leafs.Length / flags.Length;
        byte[] bytes = leafs.Data.ToArray();
        for (int leaf = 0; leaf < flags.Length; leaf++)
        {
            Span<byte> word = bytes.AsSpan((leaf * stride) + LeafAreaFlagsOffset, 2);
            int areaFlags = BinaryPrimitives.ReadUInt16LittleEndian(word);
            areaFlags = (areaFlags & 0x1FF) | (((int)flags[leaf] & 0x7F) << 9);
            BinaryPrimitives.WriteUInt16LittleEndian(word, (ushort)areaFlags);
        }

        bsp.SetLump(BspLump.Leafs, bytes, leafs.Version);
    }
}
