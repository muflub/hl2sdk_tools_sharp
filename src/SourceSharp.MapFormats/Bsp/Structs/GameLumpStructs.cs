using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapFormats.Bsp.Structs;

// The nested game lumps of the BSP format: the layout the reference build
// declares. Four codes exist in this branch and the format lists them in the
// game lump directory.
//
// Angles are Vec3 here. QAngle is three floats in the same layout; it is a
// distinct type in the reference so that vector maths cannot be applied to it
// by accident, which is a compile-time concern the file format does not share.

/// <summary>
/// The four-character codes of the game lumps this branch writes.
/// </summary>
public static class GameLumpId
{
    /// <summary><c>sprp</c>: static props.</summary>
    public const string StaticProps = "sprp";

    /// <summary><c>dprp</c>: detail props.</summary>
    public const string DetailProps = "dprp";

    /// <summary><c>dplt</c>: detail prop lighting, low dynamic range.</summary>
    public const string DetailPropLighting = "dplt";

    /// <summary><c>dplh</c>: detail prop lighting, high dynamic range.</summary>
    public const string DetailPropLightingHdr = "dplh";

    /// <summary>
    /// The <see cref="int"/> a game lump directory entry stores for
    /// <paramref name="code"/>.
    /// </summary>
    /// <param name="code">Exactly four ASCII characters, for example <c>sprp</c>.</param>
    /// <returns>The packed identifier, first character in the HIGH byte.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="code"/> is not exactly four characters.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The format spells the id as the multi-character constant
    /// <c>'sprp'</c>. Both GCC and MSVC evaluate that as
    /// <c>('s'&lt;&lt;24) | ('p'&lt;&lt;16) | ('r'&lt;&lt;8) | 'p'</c> -- the
    /// FIRST character in the HIGH byte -- and the directory stores that int
    /// little-endian, so the bytes on disk read <c>p r p s</c>.
    /// </para>
    /// <para>
    /// That is the opposite of <c>IDBSPHEADER</c>, which is written out by
    /// hand as <c>('P'&lt;&lt;24)+('S'&lt;&lt;16)+('B'&lt;&lt;8)+'V'</c>
    /// precisely so that "VBSP" lands in file order. The two conventions are
    /// genuinely different and a helper written for one is wrong for the other;
    /// <c>dm_lockdown.bsp</c>'s directory is the arbiter, and a fact reads it.
    /// </para>
    /// </remarks>
    public static int MakeId(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (code.Length != 4)
        {
            throw new ArgumentException(
                $"a game lump id is exactly four characters, got \"{code}\"",
                nameof(code));
        }

        return (code[0] << 24) | (code[1] << 16) | (code[2] << 8) | code[3];
    }

    /// <summary>The four-character code an id spells.</summary>
    /// <param name="id">A packed identifier as the directory stores it.</param>
    /// <returns>The four characters, high byte first.</returns>
    public static string CodeOf(int id) => new(
    [
        (char)((id >> 24) & 0xFF),
        (char)((id >> 16) & 0xFF),
        (char)((id >> 8) & 0xFF),
        (char)(id & 0xFF),
    ]);
}

/// <summary>
/// The versions the reference layout declares for each game lump.
/// </summary>
public static class GameLumpVersions
{
    /// <summary>The <c>sprp</c> version this branch writes.</summary>
    public const int StaticProps = 10;

    /// <summary>The <c>dprp</c> version this branch writes.</summary>
    public const int DetailProps = 4;

    /// <summary>The <c>dplt</c> version this branch writes.</summary>
    public const int DetailPropLighting = 0;

    /// <summary>The <c>dplh</c> version this branch writes.</summary>
    public const int DetailPropLightingHdr = 0;
}

/// <summary>
/// The flags of a static prop.
/// </summary>
[Flags]
public enum StaticPropFlags
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>Computed: the prop fades with distance.</summary>
    Fades = 0x1,

    /// <summary>Computed: the prop lights itself at its lighting origin.</summary>
    UseLightingOrigin = 0x2,

    /// <summary>Computed at run time from the DirectX level: do not draw.</summary>
    NoDraw = 0x4,

    /// <summary>Set in Hammer: ignore surface normals when lighting.</summary>
    IgnoreNormals = 0x8,

    /// <summary>Set in Hammer: cast no shadow.</summary>
    NoShadow = 0x10,

    /// <summary>Set in Hammer: fade in screen space rather than world space.</summary>
    ScreenSpaceFade = 0x20,

    /// <summary>vrad computes one colour at the lighting origin, not per vertex.</summary>
    NoPerVertexLighting = 0x40,

    /// <summary>vrad does not self-shadow this prop.</summary>
    NoSelfShadowing = 0x80,

    /// <summary>vrad does not compute per-texel lightmaps for this prop.</summary>
    NoPerTexelLighting = 0x100,

    /// <summary>The subset Hammer can set.</summary>
    WcMask = 0x1D8,
}

/// <summary>
/// One entry of the static prop model dictionary
/// (the reference layout's <c>struct StaticPropDictLump_t</c>).
/// </summary>
/// <remarks>
/// A fixed 128-byte field (<c>STATIC_PROP_NAME_LENGTH</c>), NOT a length-
/// prefixed string: a name shorter than 128 bytes leaves whatever the compiler
/// left in the rest, so a byte-exact rewrite has to preserve the tail.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StaticPropDictLump
{
    /// <summary>The model path, NUL-padded to 128 bytes.</summary>
    public ByteArray128 Name;
}

/// <summary>
/// One entry of the static prop leaf list
/// (the reference layout's <c>struct StaticPropLeafLump_t</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StaticPropLeafLump
{
    /// <summary>A leaf the prop touches.</summary>
    public ushort Leaf;
}

/// <summary>
/// A static prop as <c>sprp</c> version 4 stores it: 56 bytes
/// (the reference layout's <c>struct StaticPropLumpV4_t</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StaticPropLumpV4
{
    /// <summary>The prop's world position.</summary>
    public Vec3 Origin;

    /// <summary>The prop's orientation, as a <c>QAngle</c>: pitch, yaw, roll.</summary>
    public Vec3 Angles;

    /// <summary>The index into the model dictionary.</summary>
    public ushort PropType;

    /// <summary>The first entry of this prop's run in the leaf list.</summary>
    public ushort FirstLeaf;

    /// <summary>How many leaves the run holds.</summary>
    public ushort LeafCount;

    /// <summary>The prop's solidity, a <c>SOLID_</c> value.</summary>
    public byte Solid;

    /// <summary>
    /// <see cref="StaticPropFlags"/>, as a single BYTE at this version. Version
    /// 10 widens it to a <c>uint</c> in a different position, which is why the
    /// versions are separate structs rather than a prefix relationship.
    /// </summary>
    public byte Flags;

    /// <summary>The prop's skin index.</summary>
    public int Skin;

    /// <summary>The distance at which the prop starts to fade.</summary>
    public float FadeMinDist;

    /// <summary>The distance at which it is gone.</summary>
    public float FadeMaxDist;

    /// <summary>The point vrad lights the prop from.</summary>
    public Vec3 LightingOrigin;
}

/// <summary>
/// A static prop as <c>sprp</c> version 5 stores it: 60 bytes
/// (the reference layout's <c>struct StaticPropLumpV5_t</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StaticPropLumpV5
{
    /// <summary>The prop's world position.</summary>
    public Vec3 Origin;

    /// <summary>The prop's orientation.</summary>
    public Vec3 Angles;

    /// <summary>The index into the model dictionary.</summary>
    public ushort PropType;

    /// <summary>The first entry of this prop's run in the leaf list.</summary>
    public ushort FirstLeaf;

    /// <summary>How many leaves the run holds.</summary>
    public ushort LeafCount;

    /// <summary>The prop's solidity.</summary>
    public byte Solid;

    /// <summary><see cref="StaticPropFlags"/>, one byte.</summary>
    public byte Flags;

    /// <summary>The prop's skin index.</summary>
    public int Skin;

    /// <summary>The distance at which the prop starts to fade.</summary>
    public float FadeMinDist;

    /// <summary>The distance at which it is gone.</summary>
    public float FadeMaxDist;

    /// <summary>The point vrad lights the prop from.</summary>
    public Vec3 LightingOrigin;

    /// <summary>A per-prop multiplier on the engine's fade distance. Added at version 5.</summary>
    public float ForcedFadeScale;
}

/// <summary>
/// A static prop as <c>sprp</c> version 6 stores it: 64 bytes
/// (the reference layout's <c>struct StaticPropLumpV6_t</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StaticPropLumpV6
{
    /// <summary>The prop's world position.</summary>
    public Vec3 Origin;

    /// <summary>The prop's orientation.</summary>
    public Vec3 Angles;

    /// <summary>The index into the model dictionary.</summary>
    public ushort PropType;

    /// <summary>The first entry of this prop's run in the leaf list.</summary>
    public ushort FirstLeaf;

    /// <summary>How many leaves the run holds.</summary>
    public ushort LeafCount;

    /// <summary>The prop's solidity.</summary>
    public byte Solid;

    /// <summary><see cref="StaticPropFlags"/>, one byte.</summary>
    public byte Flags;

    /// <summary>The prop's skin index.</summary>
    public int Skin;

    /// <summary>The distance at which the prop starts to fade.</summary>
    public float FadeMinDist;

    /// <summary>The distance at which it is gone.</summary>
    public float FadeMaxDist;

    /// <summary>The point vrad lights the prop from.</summary>
    public Vec3 LightingOrigin;

    /// <summary>A per-prop multiplier on the engine's fade distance.</summary>
    public float ForcedFadeScale;

    /// <summary>The lowest DirectX level that draws this prop. Added at version 6.</summary>
    public ushort MinDxLevel;

    /// <summary>The highest DirectX level that draws it.</summary>
    public ushort MaxDxLevel;
}

/// <summary>
/// A static prop as <c>sprp</c> version 10 stores it: 72 bytes
/// (the reference layout's <c>struct StaticPropLump_t</c>).
/// </summary>
/// <remarks>
/// NOT a superset of <see cref="StaticPropLumpV6"/>. The <c>unsigned char
/// m_Flags</c> that sat right after <c>m_Solid</c> in versions 4 to 6 is GONE;
/// the flags are a <c>unsigned int</c> near the end instead. So the byte after
/// <see cref="Solid"/> is padding at this version and carries no meaning, and
/// reading a version 10 lump with the version 6 struct silently shifts every
/// field after the skin.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StaticPropLumpV10
{
    /// <summary>The prop's world position.</summary>
    public Vec3 Origin;

    /// <summary>The prop's orientation.</summary>
    public Vec3 Angles;

    /// <summary>The index into the model dictionary.</summary>
    public ushort PropType;

    /// <summary>The first entry of this prop's run in the leaf list.</summary>
    public ushort FirstLeaf;

    /// <summary>How many leaves the run holds.</summary>
    public ushort LeafCount;

    /// <summary>The prop's solidity.</summary>
    public byte Solid;

    /// <summary>
    /// Padding. <see cref="Solid"/> ends at offset 31 and <see cref="Skin"/>
    /// is an <c>int</c>, so the C compiler inserts a byte here -- the byte
    /// that held <c>m_Flags</c> at versions 4 to 6.
    /// </summary>
    public byte Padding;

    /// <summary>The prop's skin index.</summary>
    public int Skin;

    /// <summary>The distance at which the prop starts to fade.</summary>
    public float FadeMinDist;

    /// <summary>The distance at which it is gone.</summary>
    public float FadeMaxDist;

    /// <summary>The point vrad lights the prop from.</summary>
    public Vec3 LightingOrigin;

    /// <summary>A per-prop multiplier on the engine's fade distance.</summary>
    public float ForcedFadeScale;

    /// <summary>The lowest DirectX level that draws this prop.</summary>
    public ushort MinDxLevel;

    /// <summary>The highest DirectX level that draws it.</summary>
    public ushort MaxDxLevel;

    /// <summary><see cref="StaticPropFlags"/>, widened to 32 bits at this version.</summary>
    public uint Flags;

    /// <summary>The per-texel lightmap width vrad allocates for this prop.</summary>
    public ushort LightmapResolutionX;

    /// <summary>The per-texel lightmap height.</summary>
    public ushort LightmapResolutionY;
}

/// <summary>
/// One static prop lightstyle sample
/// (the reference layout's <c>struct StaticPropLightstylesLump_t</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StaticPropLightstylesLump
{
    /// <summary>The sampled colour.</summary>
    public ColorRgbExp32 Lighting;
}

/// <summary>
/// How a detail prop faces the viewer.
/// </summary>
public enum DetailPropOrientation
{
    /// <summary>Oriented by its own angles.</summary>
    Normal = 0,

    /// <summary>Always faces the screen.</summary>
    ScreenAligned = 1,

    /// <summary>Faces the screen but stays upright.</summary>
    ScreenAlignedVertical = 2,
}

/// <summary>
/// What a detail prop is drawn as.
/// </summary>
public enum DetailPropType
{
    /// <summary>A studio model.</summary>
    Model = 0,

    /// <summary>A single sprite.</summary>
    Sprite = 1,

    /// <summary>Two crossed sprites.</summary>
    ShapeCross = 2,

    /// <summary>Three sprites in a triangle.</summary>
    ShapeTri = 3,
}

/// <summary>
/// One entry of the detail prop model dictionary
/// (the reference layout's <c>struct DetailObjectDictLump_t</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DetailObjectDictLump
{
    /// <summary>The model path, NUL-padded to <c>DETAIL_NAME_LENGTH</c> (128).</summary>
    public ByteArray128 Name;
}

/// <summary>
/// One entry of the detail prop sprite dictionary
/// (the reference layout's <c>struct DetailSpriteDictLump_t</c>).
/// </summary>
/// <remarks>
/// Every detail sprite must live in the <c>detail/detailsprites</c> material,
/// which is why only texture coordinates are stored and no material name.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DetailSpriteDictLump
{
    /// <summary>The upper-left corner in model space, as a <c>Vector2D</c>.</summary>
    public FloatArray2 UpperLeft;

    /// <summary>The lower-right corner in model space.</summary>
    public FloatArray2 LowerRight;

    /// <summary>The upper-left texture coordinate.</summary>
    public FloatArray2 TexUpperLeft;

    /// <summary>The lower-right texture coordinate.</summary>
    public FloatArray2 TexLowerRight;
}

/// <summary>
/// One detail prop (the reference layout's <c>struct DetailObjectLump_t</c>).
/// 52 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DetailObjectLump
{
    /// <summary>The prop's world position.</summary>
    public Vec3 Origin;

    /// <summary>The prop's orientation, as a <c>QAngle</c>.</summary>
    public Vec3 Angles;

    /// <summary>
    /// An index into either the model dictionary or the sprite dictionary,
    /// depending on <see cref="Type"/>.
    /// </summary>
    public ushort DetailModel;

    /// <summary>The leaf the prop sits in.</summary>
    public ushort Leaf;

    /// <summary>The prop's baked lighting.</summary>
    public ColorRgbExp32 Lighting;

    /// <summary>The first lightstyle sample's index in the <c>dplt</c> lump.</summary>
    public uint LightStyles;

    /// <summary>How many lightstyle samples this prop has.</summary>
    public byte LightStyleCount;

    /// <summary>How much the prop sways in the wind.</summary>
    public byte SwayAmount;

    /// <summary>The shape angle, for shaped sprites.</summary>
    public byte ShapeAngle;

    /// <summary>The shape size, for shaped sprites.</summary>
    public byte ShapeSize;

    /// <summary><see cref="DetailPropOrientation"/>.</summary>
    public byte Orientation;

    /// <summary>
    /// Three unused bytes that the reference layout itself marks for removal
    /// the next time the detail lump is revised. They are real bytes in every
    /// shipped map, so they stay.
    /// </summary>
    public ByteArray3 Padding2;

    /// <summary><see cref="DetailPropType"/>.</summary>
    public byte Type;

    /// <summary>Three more unused bytes.</summary>
    public ByteArray3 Padding3;

    /// <summary>The sprite's scale. Only sprites use it.</summary>
    public float Scale;
}

/// <summary>
/// One detail prop lightstyle sample
/// (the reference layout's <c>struct DetailPropLightstylesLump_t</c>). Five
/// bytes, with NO padding: every member is a byte, so the struct's alignment
/// is one.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DetailPropLightstylesLump
{
    /// <summary>The sampled colour.</summary>
    public ColorRgbExp32 Lighting;

    /// <summary>The lightstyle this sample belongs to.</summary>
    public byte Style;
}
