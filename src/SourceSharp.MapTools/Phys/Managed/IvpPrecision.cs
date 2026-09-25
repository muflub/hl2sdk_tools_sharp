using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SourceSharp.MapTools.Phys.Managed;

/// <summary>
/// The one knob between the two managed cookers: what IVP's <c>IVP_DOUBLE</c> is, and the few
/// routines whose code differs between the stock and TF2 builds beyond width.
/// </summary>
/// <remarks>
/// <para>
/// Measured, not assumed: the stock and TF2 linux64 <c>vphysics.so</c> are the same IVP source built with the same GCC 10.3,
/// <c>-ffast-math</c>, SSE2 and no FMA. The only cook-path difference is the precision typedef:
/// <c>IVP_DOUBLE</c> is <c>float</c> under the stock policy and <c>double</c> under the
/// corrected one. Everything else in this
/// namespace is written once, generic over <typeparamref name="T"/> = <c>IVP_DOUBLE</c>, in the
/// compiled evaluation order of the reference builds (and checked against both).
/// <c>IVP_FLOAT</c> fields stay <see cref="float"/> in both.
/// </para>
/// <para>
/// The implementations are structs so the JIT specialises every generic method per precision and
/// folds <see cref="IsDouble"/>; there is no virtual dispatch and no boxing on the cook path.
/// </para>
/// </remarks>
/// <typeparam name="T">The type IVP calls <c>IVP_DOUBLE</c>.</typeparam>
internal interface IIvpPrecision<T>
    where T : unmanaged, IBinaryFloatingPointIeee754<T>
{
    /// <summary>True for TF2's double build, false for the stock float build.</summary>
    static abstract bool IsDouble { get; }

    /// <summary>A short name for identities and diagnostics.</summary>
    static abstract string Name { get; }

    /// <summary>
    /// <c>IVP_U_Float_Point::normize</c>: normalises float storage in place, false when the squared
    /// length is below the threshold (the vector is then left unchanged).
    /// </summary>
    /// <param name="x">X.</param>
    /// <param name="y">Y.</param>
    /// <param name="z">Z.</param>
    /// <returns>Whether the vector was long enough to normalise.</returns>
    static abstract bool NormizeFloatPoint(ref float x, ref float y, ref float z);

    /// <summary>
    /// The four-lane hesse normalise (<c>IVP_U_Hesse::normize</c>): scales normal and distance by one
    /// reciprocal length, with no zero guard.
    /// </summary>
    /// <param name="x">Normal X.</param>
    /// <param name="y">Normal Y.</param>
    /// <param name="z">Normal Z.</param>
    /// <param name="w">Hesse distance.</param>
    static abstract void NormizeHesse(ref T x, ref T y, ref T z, ref T w);
}

/// <summary>
/// The stock policy: <c>IVP_DOUBLE = float</c>, and <c>-ffast-math</c> lowering of <c>1/sqrtf</c> to
/// <c>rsqrtss</c>/<c>rsqrtps</c> plus one Newton step.
/// </summary>
/// <remarks>
/// The estimate instruction's result is implementation-defined, so this build's output depends on
/// the CPU (the goldens were cut on an AMD Ryzen 9 9950X). It is the reason
/// <see cref="Options.StockQuirk.CollisionCookerSinglePrecision"/> exists: stock reproduces it,
/// correct does not.
/// </remarks>
internal readonly struct StockPrecision : IIvpPrecision<float>
{
    /// <inheritdoc/>
    public static bool IsDouble => false;

    /// <inheritdoc/>
    public static string Name => "stock-float";

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool NormizeFloatPoint(ref float x, ref float y, ref float z)
    {
        // Stock: s = (x*x + y*y) + z*z; if (s < 1e-10f) return false;
        // r = rsqrtss(s); f = ((s*r)*r + -3) * (r * -0.5); x*=f, y*=f, z = f*z.
        float s = ((x * x) + (y * y)) + (z * z);
        if (!(1.0e-10f <= s))
        {
            return false;
        }

        float f = RsqrtNewton(s);
        x *= f;
        y *= f;
        z *= f;
        return true;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void NormizeHesse(ref float x, ref float y, ref float z, ref float w)
    {
        // Stock (rsqrtps, all four lanes share one length): s = x*x + (y*y + z*z).
        float s = (x * x) + ((y * y) + (z * z));
        float f = RsqrtNewton(s);
        x = f * x;
        y = f * y;
        z = f * z;
        w = f * w;
    }

    /// <summary>
    /// <c>rsqrtss</c> then the Newton step exactly as GCC emitted it:
    /// <c>((s*r)*r + -3) * (r * -0.5)</c>.
    /// </summary>
    /// <param name="s">The squared length.</param>
    /// <returns>The refined reciprocal square root.</returns>
    /// <exception cref="PlatformNotSupportedException">No SSE: stock has no meaning without it.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float RsqrtNewton(float s)
    {
        if (!Sse.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "The stock-precision cooker reproduces the reference build's rsqrtss estimate and has no meaning without SSE.");
        }

        float r = Sse.ReciprocalSqrtScalar(Vector128.CreateScalarUnsafe(s)).ToScalar();
        return (((s * r) * r) + -3.0f) * (r * -0.5f);
    }
}

/// <summary>
/// The TF2 policy: <c>IVP_DOUBLE = double</c>, no approximate instructions on the cook path. Plain IEEE,
/// So identical on every CPU.
/// </summary>
internal readonly struct CorrectPrecision : IIvpPrecision<double>
{
    /// <inheritdoc/>
    public static bool IsDouble => true;

    /// <inheritdoc/>
    public static string Name => "tf2-double";

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool NormizeFloatPoint(ref float x, ref float y, ref float z)
    {
        // TF2: the squared length is summed in float, widened, and IVP's exponent
        // bit-hack inverse square root refines it with four double Newton steps.
        double s = ((x * x) + (y * y)) + (z * z);
        if (!(1e-19 <= s))
        {
            return false;
        }

        double r = IsqrtDouble(s);
        x = (float)(x * r);
        y = (float)(y * r);
        z = (float)(z * r);
        return true;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void NormizeHesse(ref double x, ref double y, ref double z, ref double w)
    {
        // TF2: f = 1.0 / sqrt((x*x + y*y) + z*z).
        double f = 1.0 / Math.Sqrt(((x * x) + (y * y)) + (z * z));
        x *= f;
        y *= f;
        z *= f;
        w = f * w;
    }

    /// <summary>
    /// TF2's <c>isqrt</c>: a first guess from the exponent bits, then four
    /// <c>r = r * (1.5 - (r*r) * (s*0.5))</c> steps.
    /// </summary>
    /// <param name="s">The positive argument.</param>
    /// <returns>An approximation of <c>1/sqrt(s)</c> that is identical on every CPU.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double IsqrtDouble(double s)
    {
        double half = s * 0.5;
        int hi = (int)(BitConverter.DoubleToInt64Bits(s) >> 32);
        int guess = ((0x7ff00000 - hi) >> 1) + 0x1ff00000;
        double r = BitConverter.Int64BitsToDouble((long)(uint)guess << 32);
        r *= 1.5 - ((r * r) * half);
        r *= 1.5 - ((r * r) * half);
        r *= 1.5 - ((r * r) * half);
        r *= 1.5 - (half * (r * r));
        return r;
    }
}
