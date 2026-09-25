using SourceSharp.MapFormats.Bsp.Structs;

namespace SourceSharp.MapTools.Compare;

/// <summary>
/// The one decode every lighting comparison in this instrument goes through.
/// </summary>
/// <remarks>
/// <para>
/// <c>mathlib.h:975</c>: <c>TexLightToLinear(c, exponent)</c> is
/// <c>(float)c * power2_n[exponent + 128]</c>, and <c>color_conversion.cpp:38</c>
/// declares that table as <c>2**(index - 128) / 255</c>. So a channel's linear
/// value is <c>c * 2^e / 255</c>, which is the scale Phase 0 measured the
/// vrad noise floor in and the only scale its numbers are comparable against.
/// </para>
/// <para>
/// Deliberately NOT <c>ColorRgbExp32.ToLinear()</c>, which reproduces
/// <c>ColorRGBExp32ToVector</c> -- that helper multiplies the same expression
/// back by 255 (<c>color_conversion.cpp:454</c>, under a comment asking why the
/// factor is there at all) and so answers in a different scale. Both are right
/// for their own caller; a statistic quoted against Phase 0's numbers has to be
/// in Phase 0's scale.
/// </para>
/// <para>
/// Computed in <see cref="double"/> through <see cref="Math.ScaleB(double, int)"/>,
/// which is an exact power of two, so the decode contributes no rounding of its
/// own to an error that is being measured at 1e-4.
/// </para>
/// </remarks>
public static class LinearLight
{
    /// <summary>One channel's linear value.</summary>
    /// <param name="mantissa">The channel byte.</param>
    /// <param name="exponent">The sample's shared signed exponent.</param>
    /// <returns><c>mantissa * 2^exponent / 255</c>.</returns>
    public static double Channel(byte mantissa, sbyte exponent) =>
        mantissa * Math.ScaleB(1.0, exponent) / 255.0;

    /// <summary>
    /// How far apart two samples are: the largest absolute linear difference
    /// over the three channels.
    /// </summary>
    /// <param name="a">The sample from map A.</param>
    /// <param name="b">The sample from map B.</param>
    /// <returns>The error, in the same scale Phase 0 quoted.</returns>
    /// <remarks>
    /// Per CHANNEL rather than per sample as a vector length, because that is
    /// what the Phase 0 measurement did: its headline maximum of 3.396078 is
    /// <c>(131*8 - 182) / 255</c>, the green channel of sample #538369 of
    /// 2fort, and a vector norm would not reproduce it.
    /// </remarks>
    public static double Error(ColorRgbExp32 a, ColorRgbExp32 b)
    {
        double dr = Math.Abs(Channel(a.R, a.Exponent) - Channel(b.R, b.Exponent));
        double dg = Math.Abs(Channel(a.G, a.Exponent) - Channel(b.G, b.Exponent));
        double db = Math.Abs(Channel(a.B, a.Exponent) - Channel(b.B, b.Exponent));
        return Math.Max(dr, Math.Max(dg, db));
    }
}
