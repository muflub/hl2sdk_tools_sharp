using System.Collections.Immutable;
using System.Runtime.InteropServices;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rad.Ambient;

/// <summary>
/// The three <c>mathlib</c> colour conversions leaf ambient's output depends on,
/// at stock's exact precision.
/// </summary>
/// <remarks>
/// <para>
/// All three are byte-visible. <see cref="Encode"/> writes the lump's bytes
/// directly; <see cref="TexLightToLinear(byte, sbyte)"/> reads the lightmap that every
/// sample is built from; <see cref="LinearToScreenGamma"/> decides which samples
/// <c>CompressAmbientSampleList</c> throws away, and therefore how many records
/// the lump even has.
/// </para>
/// <para>
/// None of these is a quirk. A gamma table is a choice of colour space and an
/// exponent-mantissa split is a storage format: "wrong" has no meaning for
/// either, so they are reproduced unconditionally and there is no
/// <c>Correct</c> side to branch to.
/// </para>
/// <para>
/// Deliberately NOT <c>Compare.LinearLight</c>, which answers in a different
/// scale on purpose (its remarks explain why), nor
/// <c>ColorRgbExp32.ToLinear()</c>, which reproduces
/// <c>ColorRGBExp32ToVector</c> and multiplies by 255 again. Leaf ambient wants
/// <c>TexLightToLinear</c>'s scale, which is the one without the 255.
/// </para>
/// </remarks>
public static class StockLightColor
{
    /// <summary>
    /// <c>lineartoscreen</c>, built as
    /// <c>MathLib_Init(2.2f, 2.2f, 0.0f, 2.0f, ...)</c> leaves it.
    /// </summary>
    /// <remarks>
    /// Is the only call, so vrad's table is fixed for the
    /// life of the process and can be a static here. Built once, lazily, by the
    /// static constructor.
    /// </remarks>
    private static readonly ImmutableArray<int> LinearToScreen =
        ImmutableCollectionsMarshal.AsImmutableArray(BuildLinearToScreen());

    /// <summary>
    /// <c>power2_n</c>: <c>2^(i-128) / 255</c>, as floats
    /// </summary>
    private static readonly ImmutableArray<float> Power2N =
        ImmutableCollectionsMarshal.AsImmutableArray(BuildPower2N());

    /// <summary>
    /// One lightmap channel's linear value (
    /// <c>TexLightToLinear</c>).
    /// </summary>
    /// <param name="mantissa">The channel byte.</param>
    /// <param name="exponent">The sample's shared signed exponent.</param>
    /// <returns><c>mantissa * 2^exponent / 255</c>, in float.</returns>
    public static float TexLightToLinear(byte mantissa, sbyte exponent) =>
        mantissa * Power2N[exponent + 128];

    /// <summary>A whole lightmap sample, decoded.</summary>
    /// <param name="c">The sample.</param>
    /// <returns>Its three channels.</returns>
    public static Vec3 TexLightToLinear(ColorRgbExp32 c) => new(
        TexLightToLinear(c.R, c.Exponent),
        TexLightToLinear(c.G, c.Exponent),
        TexLightToLinear(c.B, c.Exponent));

    /// <summary>
    /// A linear 0..1 value in gamma-corrected 0..255
    /// </summary>
    /// <param name="f">The linear value.</param>
    /// <returns>The screen value, 0..255.</returns>
    /// <remarks>
    /// <c>i = f * 1023</c> is a float-to-int TRUNCATION, not a round, and the
    /// clamp is on <c>i</c> rather than on <c>f</c> -- so a NaN, which compares
    /// false against both bounds, indexes the table with whatever the conversion
    /// produced. .NET's float-to-int conversion is saturating rather than
    /// undefined, so a NaN lands on 0 and is then clamped in range; stock on x86
    /// lands on <c>0x80000000</c> and is clamped to 0 by the <c>i &lt; 0</c>
    /// test. Same answer, by different routes.
    /// </remarks>
    public static int LinearToScreenGamma(float f)
    {
        int i = (int)(f * 1023);
        if (i < 0)
        {
            i = 0;
        }

        if (i > 1023)
        {
            i = 1023;
        }

        return LinearToScreen[i];
    }

    /// <summary>
    /// A linear colour in <c>ColorRGBExp32</c>
    /// (the shipping IEEE-754 branch).
    /// </summary>
    /// <param name="v">The colour. Negative components are not expected.</param>
    /// <returns>The encoded sample.</returns>
    /// <remarks>
    /// <para>
    /// The exponent comes from the LARGEST component, picked by a branch tree
    /// whose ties go to the later component (<c>vin.x &gt; vin.y</c> is strict),
    /// and then straight out of that float's own exponent bits: the function
    /// wants the largest channel to land on 128..255, so the exponent is the
    /// float's biased exponent minus <c>7 + 127</c>.
    /// </para>
    /// <para>
    /// The rescale is by a float SYNTHESISED from bits --
    /// <c>(127 - exponent) &lt;&lt; 23</c> reinterpreted -- rather than by
    /// <c>pow</c>. That is exact for every exponent in range and is why this
    /// branch and the <c>#if 0</c> one above it in the reference build do not always agree.
    /// </para>
    /// <para>
    /// The three channel conversions are float-to-int truncations. .NET
    /// saturates rather than wrapping, which differs from x86's
    /// <c>cvttss2si</c> only for inputs stock's own asserts already exclude.
    /// </para>
    /// </remarks>
    public static ColorRgbExp32 Encode(Vec3 v)
    {
        float max;
        if (v.X > v.Y)
        {
            max = v.X > v.Z ? v.X : v.Z;
        }
        else
        {
            max = v.Y > v.Z ? v.Y : v.Z;
        }

        int exponent = CalcExponent(max);

        uint fbits = (uint)(127 - exponent) << 23;
        float scalar = BitConverter.UInt32BitsToSingle(fbits);

        return new ColorRgbExp32
        {
            R = (byte)(int)(v.X * scalar),
            G = (byte)(int)(v.Y * scalar),
            B = (byte)(int)(v.Z * scalar),
            Exponent = (sbyte)exponent,
        };
    }

    /// <summary>
    /// <c>VectorToColorRGBExp32_CalcExponent</c>: the exponent that maps
    /// <paramref name="f"/> onto 128..255.
    /// </summary>
    /// <param name="f">The largest channel.</param>
    /// <returns>The exponent.</returns>
    /// <remarks>
    /// Exactly zero returns 0 -- the early-out is on the VALUE, so negative zero
    /// takes it too. Anything else reads bits 23..30 and subtracts
    /// <c>7 + 127</c>. A denormal therefore yields <c>-134</c>, outside a signed
    /// byte; stock asserts against that rather than clamping, and this does not
    /// clamp either, because a clamp would be a behaviour stock does not have.
    /// </remarks>
    private static int CalcExponent(float f)
    {
        if (f == 0.0f)
        {
            return 0;
        }

        uint fbits = BitConverter.SingleToUInt32Bits(f);
        int expComponent = (int)((fbits & 0x7F800000u) >> 23);
        return expComponent - (7 + 127);
    }

    /// <summary>
    /// <c>BuildGammaTable(2.2, 2.2, 0.0, 2)</c>'s <c>lineartoscreen</c> half.
    /// </summary>
    /// <returns>The 1024-entry table.</returns>
    /// <remarks>
    /// <para>
    /// The float/double mixture is stock's and is kept: <c>g</c> is a FLOAT
    /// holding <c>1.0 / 2.2f</c>, <c>f</c> is a FLOAT holding <c>i / 1023.0</c>
    /// computed in double, the shift-up arithmetic promotes back to double
    /// because <c>1.0 - g3</c> is a double literal, and <c>pow</c> takes both
    /// operands widened. <c>255 * pow(...)</c> is then truncated to int.
    /// </para>
    /// <para>
    /// With <c>brightness = 0</c>, <c>g3</c> is 0.125 and the <c>f &lt;= g3</c>
    /// branch is <c>(f / 0.125f) * 0.125</c> -- an exact round trip through a
    /// power of two, so the first 128 entries are the plain gamma curve.
    /// </para>
    /// </remarks>
    private static int[] BuildLinearToScreen()
    {
        const float Gamma = 2.2f;

        float g = Gamma;
        if (g > 3.0)
        {
            g = 3.0f;
        }

        g = (float)(1.0 / g);

        // brightness <= 0.0
        const float G3 = 0.125f;

        int[] table = new int[1024];
        for (int i = 0; i < 1024; i++)
        {
            float f = (float)(i / 1023.0);

            // brightness is not > 1.0, so no scale-up.
            if (f <= G3)
            {
                f = (float)((f / G3) * 0.125);
            }
            else
            {
                f = (float)(0.125 + (((f - G3) / (1.0 - G3)) * 0.875));
            }

            int inf = (int)(255 * Math.Pow(f, g));
            if (inf < 0)
            {
                inf = 0;
            }

            if (inf > 255)
            {
                inf = 255;
            }

            table[i] = inf;
        }

        return table;
    }

    /// <summary>The <c>power2_n</c> table.</summary>
    /// <returns>256 floats, <c>2^(i-128) / 255</c>.</returns>
    /// <remarks>
    /// The reference build writes these out as decimal literals to 19 digits, which is a
    /// round-tripping double; computing <c>2^(i-128)</c> exactly in double and
    /// dividing by 255 lands on the same double, and the narrowing to float is
    /// then the same narrowing. Entries whose true value underflows float
    /// become zero here and are written as tiny denormals there; neither is
    /// reachable from a lightmap byte, whose exponent is a signed byte.
    /// </remarks>
    private static float[] BuildPower2N()
    {
        float[] table = new float[256];
        for (int i = 0; i < 256; i++)
        {
            table[i] = (float)(Math.ScaleB(1.0, i - 128) / 255.0);
        }

        return table;
    }

    /// <summary>
    /// <c>lineartovertex</c>: linear 0..4 (times 1024) to vertex-light 0..1, as
    /// <c>BuildGammaTable(2.2f, 2.2f, 0.0f, 2)</c> leaves it
    /// </summary>
    private static readonly ImmutableArray<float> LinearToVertex =
        ImmutableCollectionsMarshal.AsImmutableArray(BuildLinearToVertex());

    /// <summary>
    /// <c>LinearToVertexLight</c>: a round-to-nearest
    /// table index, clamped to 0..4095.
    /// </summary>
    /// <param name="f">The linear value.</param>
    /// <returns>The vertex-light value, 0..1.</returns>
    public static float LinearToVertexLight(float f)
    {
        int i = RoundFloatToInt(f * 1024.0f);
        if ((uint)i > 4095)
        {
            i = i < 0 ? 0 : 4095;
        }

        return LinearToVertex[i];
    }

    /// <summary>
    /// <c>ConvertRGBExp32ToRGBA8888</c>: decode, map
    /// through <see cref="LinearToVertexLight"/>, <c>ColorClamp</c>, and round
    /// each channel to a byte. Alpha is 255.
    /// </summary>
    /// <param name="c">The encoded colour.</param>
    /// <returns>R, G, B, A.</returns>
    public static (byte R, byte G, byte B, byte A) ToRgba8888(ColorRgbExp32 c)
    {
        float r = LinearToVertexLight(TexLightToLinear(c.R, c.Exponent));
        float g = LinearToVertexLight(TexLightToLinear(c.G, c.Exponent));
        float b = LinearToVertexLight(TexLightToLinear(c.B, c.Exponent));

        // ColorClamp: max(x, max(y, z)), then scale by its reciprocal.
        float yz = g > b ? g : b;
        float maxc = r > yz ? r : yz;
        if (maxc > 1.0f)
        {
            float ooMax = 1.0f / maxc;
            r *= ooMax;
            g *= ooMax;
            b *= ooMax;
        }

        r = r < 0.0f ? 0.0f : r;
        g = g < 0.0f ? 0.0f : g;
        b = b < 0.0f ? 0.0f : b;

        return ((byte)RoundFloatToInt(r * 255.0f), (byte)RoundFloatToInt(g * 255.0f), (byte)RoundFloatToInt(b * 255.0f), 255);
    }

    /// <summary>
    /// <c>RoundFloatToInt</c>: <c>cvtss2si</c>, round half
    /// to even, and <c>0x80000000</c> for NaN or out of range.
    /// </summary>
    /// <param name="f">The value.</param>
    /// <returns>The rounded integer.</returns>
    public static int RoundFloatToInt(float f)
    {
        float r = MathF.Round(f, MidpointRounding.ToEven);
        return float.IsNaN(r) || r >= 2147483648.0f || r < -2147483648.0f ? int.MinValue : (int)r;
    }

    /// <summary>
    /// <c>f = pow( i/1024.0, 1.0 / gamma )</c> with <c>gamma</c> the float 2.2f,
    /// times the overbright factor 0.5 and capped at 1.
    /// </summary>
    private static float[] BuildLinearToVertex()
    {
        const float Gamma = 2.2f;
        const float OverbrightFactor = 0.5f;
        float[] table = new float[4096];
        for (int i = 0; i < table.Length; i++)
        {
            float f = (float)Math.Pow(i / 1024.0, 1.0 / Gamma);
            float v = f * OverbrightFactor;
            table[i] = v > 1 ? 1 : v;
        }

        return table;
    }
}
