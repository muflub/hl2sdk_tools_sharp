using System.Runtime.CompilerServices;

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapFormats.Bsp.Structs;

// C's `T name[N]` inside a struct is N elements laid out end to end, with no
// length and no indirection. C# has exactly one way to say that in a blittable
// struct without `unsafe`: [InlineArray]. Each shape the BSP structs need gets
// a named type here, once, so the lump structs read like the header does and
// every one of them stays castable from raw lump bytes.
//
// They are deliberately NOT generic: [InlineArray] fixes the length in the type
// and a generic length parameter does not exist in C#.

/// <summary>Four bytes laid out end to end, as C's <c>byte x[4]</c>.</summary>
[InlineArray(4)]
public struct ByteArray4
{
    private byte _element0;
}

/// <summary>128 bytes laid out end to end, as C's <c>char x[128]</c>.</summary>
[InlineArray(128)]
public struct ByteArray128
{
    private byte _element0;
}

/// <summary>Two signed 16-bit values, as C's <c>short x[2]</c>.</summary>
[InlineArray(2)]
public struct ShortArray2
{
    private short _element0;
}

/// <summary>Three signed 16-bit values, as C's <c>short x[3]</c>.</summary>
[InlineArray(3)]
public struct ShortArray3
{
    private short _element0;
}

/// <summary>Two unsigned 16-bit values, as C's <c>unsigned short x[2]</c>.</summary>
[InlineArray(2)]
public struct UShortArray2
{
    private ushort _element0;
}

/// <summary>Four unsigned 16-bit values, as C's <c>unsigned short x[4]</c>.</summary>
[InlineArray(4)]
public struct UShortArray4
{
    private ushort _element0;
}

/// <summary>Two signed 32-bit values, as C's <c>int x[2]</c>.</summary>
[InlineArray(2)]
public struct IntArray2
{
    private int _element0;
}

/// <summary>Three signed 32-bit values, as C's <c>int x[3]</c>.</summary>
[InlineArray(3)]
public struct IntArray3
{
    private int _element0;
}

/// <summary>64 signed 32-bit values, as C's <c>int x[64]</c>.</summary>
[InlineArray(64)]
public struct IntArray64
{
    private int _element0;
}

/// <summary>256 signed 32-bit values, as C's <c>int x[256]</c>.</summary>
[InlineArray(256)]
public struct IntArray256
{
    private int _element0;
}

/// <summary>
/// Ten unsigned 32-bit values: <c>ddispinfo_t::m_AllowedVerts</c>'s bit vector.
/// </summary>
/// <remarks>
/// The length is not arbitrary. <c>bspfile.h:665</c> derives it as
/// <c>PAD_NUMBER( MAX_DISPVERTS, 32 ) / 32</c>, and
/// <c>MAX_DISPVERTS = NUM_DISP_POWER_VERTS(4) = 17 * 17 = 289</c>, which rounds
/// up to 320 bits and so to ten 32-bit words.
/// </remarks>
[InlineArray(10)]
public struct UIntArray10
{
    private uint _element0;
}

/// <summary>Two 32-bit floats, as C's <c>float x[2]</c>.</summary>
[InlineArray(2)]
public struct FloatArray2
{
    private float _element0;
}

/// <summary>
/// Eight 32-bit floats: one <c>texinfo_t</c> texture-vector matrix, which C
/// declares as <c>float x[2][4]</c>.
/// </summary>
/// <remarks>
/// C row-major <c>[2][4]</c> is eight floats end to end, so index it as
/// <c>[(row * 4) + column]</c>. Row 0 is the s axis and row 1 the t axis;
/// columns 0..2 are the xyz of the axis and column 3 is its offset
/// (<c>bspfile.h:502</c>).
/// </remarks>
[InlineArray(8)]
public struct FloatArray8
{
    private float _element0;
}

/// <summary>Four <see cref="Vec3"/>, as C's <c>Vector x[4]</c>.</summary>
[InlineArray(4)]
public struct Vec3Array4
{
    private Vec3 _element0;
}

/// <summary>
/// Six <see cref="ColorRgbExp32"/>: one <c>CompressedLightCube</c>'s faces.
/// </summary>
[InlineArray(6)]
public struct ColorRgbExp32Array6
{
    private ColorRgbExp32 _element0;
}

/// <summary>
/// Two <see cref="DispSubNeighbor"/>, as <c>CDispNeighbor::m_SubNeighbors</c>.
/// </summary>
[InlineArray(2)]
public struct DispSubNeighborArray2
{
    private DispSubNeighbor _element0;
}

/// <summary>
/// Four <see cref="DispNeighbor"/>, as <c>ddispinfo_t::m_EdgeNeighbors</c>.
/// </summary>
[InlineArray(4)]
public struct DispNeighborArray4
{
    private DispNeighbor _element0;
}

/// <summary>
/// Four <see cref="DispCornerNeighbors"/>, as
/// <c>ddispinfo_t::m_CornerNeighbors</c>.
/// </summary>
[InlineArray(4)]
public struct DispCornerNeighborsArray4
{
    private DispCornerNeighbors _element0;
}
