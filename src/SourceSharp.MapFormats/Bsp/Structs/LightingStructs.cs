using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapFormats.Bsp.Structs;

/// <summary>
/// One lightmap sample: three 8-bit mantissas and a shared signed exponent
/// (<c>mathlib.h:990</c>, <c>struct ColorRGBExp32</c>).
/// </summary>
/// <remarks>
/// This is the storage format of LUMP_LIGHTING and LUMP_LIGHTING_HDR, and of
/// the per-leaf ambient cubes. It is a floating-point format: the value of a
/// channel is <c>mantissa * 2^exponent</c>, so the same struct carries both a
/// dim LDR sample and a blown-out HDR one.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ColorRgbExp32
{
    /// <summary>The red mantissa.</summary>
    public byte R;

    /// <summary>The green mantissa.</summary>
    public byte G;

    /// <summary>The blue mantissa.</summary>
    public byte B;

    /// <summary>
    /// The shared exponent. SIGNED: <c>mathlib.h:993</c> declares it
    /// <c>signed char</c>, and an unsigned read turns every sample dimmer than
    /// 1.0 into an astronomically bright one.
    /// </summary>
    public sbyte Exponent;

    /// <summary>
    /// This sample as three linear floats, as <c>ColorRGBExp32ToVector</c>
    /// computes them.
    /// </summary>
    /// <returns>The decoded colour.</returns>
    /// <remarks>
    /// <c>mathlib_base.cpp</c>'s implementation is
    /// <c>TexLightToLinear( c, exponent )</c> per channel, which is
    /// <c>mantissa * 2^exponent</c>. Written here as
    /// <see cref="MathF.Pow(float, float)"/> of two, rather than as a table
    /// lookup, so there is no static state and no initialisation order.
    /// </remarks>
    public readonly Vec3 ToLinear()
    {
        float scale = MathF.Pow(2.0f, Exponent);
        return new Vec3(R * scale, G * scale, B * scale);
    }
}

/// <summary>
/// Six <see cref="ColorRgbExp32"/>, one per axis direction
/// (<c>compressed_light_cube.h:17</c>, <c>struct CompressedLightCube</c>).
/// </summary>
/// <remarks>
/// The axis order is the order <c>g_pBoxDirections</c> uses: +x, -x, +y, -y,
/// +z, -z. Entities light themselves by interpolating this cube, which is why
/// it moved out of <c>dleaf_t</c> and into its own lump at LEAFS version 1.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CompressedLightCube
{
    /// <summary>The six face colours.</summary>
    public ColorRgbExp32Array6 Color;
}
