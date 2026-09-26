//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace SourceSharp.MapFormats.Geometry;

/// <summary>
/// The hardware reciprocal and reciprocal-square-root ESTIMATES that stock's
/// arithmetic starts from, taken from whichever instruction set this CPU has.
/// </summary>
/// <remarks>
/// <para>
/// Stock never computes <c>1/x</c> or <c>1/sqrt(x)</c> exactly on its hot
/// paths. It takes the SSE estimate (<c>rcpss</c>/<c>rcpps</c>,
/// <c>rsqrtss</c>/<c>rsqrtps</c>) and usually refines it with one
/// Newton-Raphson step written out in ordinary arithmetic. The callers here
/// keep that Newton step in their own code, in stock's operand order; this
/// class supplies only the starting estimate.
/// </para>
/// <para>
/// <b>x86.</b> The SSE instructions themselves. Their result is
/// implementation-defined within a relative error of 1.5 * 2^-12, so AMD and
/// Intel parts return different low bits for the same input. That is why the
/// stock goldens in the test suite are per CPU vendor.
/// </para>
/// <para>
/// <b>arm64.</b> ARM's own estimates, <c>frecpe</c> and <c>frsqrte</c>, each
/// followed by one ARM step instruction (<c>frecps</c>, <c>frsqrts</c>). The raw
/// ARM estimates carry about 8 bits, against x86's 12; the callers' single
/// Newton step was written for a 12-bit start, so a raw ARM estimate would
/// leave their results markedly less accurate than on x86. One ARM step brings
/// the start to about 16 bits, at least x86's precision, and the callers'
/// Newton step then gives what it gives on x86: an answer good to a few ulps.
/// Unlike x86, ARM defines these instructions' results exactly in its
/// architecture pseudocode (with FPCR.AH clear, its default), so every arm64
/// CPU should return the same bits. Only Apple Silicon has been measured.
/// </para>
/// <para>
/// Neither ISA gives stock's x86 bits on the other vendor's hardware, and ARM
/// cannot give them at all. On arm64 the stock paths therefore reproduce stock's
/// ALGORITHM with ARM's estimate, not stock's output; the test suite pins
/// arm64's results as a separate, self-captured set.
/// </para>
/// <para>
/// On a CPU with neither instruction set this class throws rather than
/// computing an exact value, because a silent exact fallback would let a
/// comparison against stock report success while no longer computing the
/// same thing.
/// </para>
/// </remarks>
public static class FloatEstimate
{
    /// <summary>Whether this CPU has estimate instructions this class can use.</summary>
    public static bool IsSupported => Sse.IsSupported || AdvSimd.IsSupported;

    /// <summary>
    /// Which estimate instructions this CPU uses: <c>"sse"</c>, <c>"arm64"</c>,
    /// or <c>"none"</c>.
    /// </summary>
    public static string Family =>
        Sse.IsSupported ? "sse" : AdvSimd.IsSupported ? "arm64" : "none";

    /// <summary>An estimate of <c>1 / a</c>, before any Newton step of the caller's.</summary>
    /// <param name="a">The value.</param>
    /// <returns><c>rcpss</c> on x86; <c>frecpe</c> and one <c>frecps</c> on arm64.</returns>
    /// <exception cref="PlatformNotSupportedException">The CPU has neither SSE nor AdvSimd.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Reciprocal(float a) =>
        Reciprocal(Vector128.CreateScalar(a)).ToScalar();

    /// <summary>An estimate of <c>1 / sqrt(a)</c>, before any Newton step of the caller's.</summary>
    /// <param name="a">The value.</param>
    /// <returns><c>rsqrtss</c> on x86; <c>frsqrte</c> and one <c>frsqrts</c> on arm64.</returns>
    /// <exception cref="PlatformNotSupportedException">The CPU has neither SSE nor AdvSimd.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ReciprocalSqrt(float a) =>
        ReciprocalSqrt(Vector128.CreateScalar(a)).ToScalar();

    /// <summary>The four-lane form of <see cref="Reciprocal(float)"/>.</summary>
    /// <param name="a">The values.</param>
    /// <returns>The estimates, lane for lane.</returns>
    /// <exception cref="PlatformNotSupportedException">The CPU has neither SSE nor AdvSimd.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Reciprocal(Vector128<float> a)
    {
        if (Sse.IsSupported)
        {
            return Sse.Reciprocal(a);
        }

        if (AdvSimd.IsSupported)
        {
            Vector128<float> e = AdvSimd.ReciprocalEstimate(a);
            return Refined(e, AdvSimd.Multiply(e, AdvSimd.ReciprocalStep(a, e)));
        }

        throw Unsupported();
    }

    /// <summary>The four-lane form of <see cref="ReciprocalSqrt(float)"/>.</summary>
    /// <param name="a">The values.</param>
    /// <returns>The estimates, lane for lane.</returns>
    /// <exception cref="PlatformNotSupportedException">The CPU has neither SSE nor AdvSimd.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ReciprocalSqrt(Vector128<float> a)
    {
        if (Sse.IsSupported)
        {
            return Sse.ReciprocalSqrt(a);
        }

        if (AdvSimd.IsSupported)
        {
            Vector128<float> e = AdvSimd.ReciprocalSquareRootEstimate(a);
            return Refined(e, AdvSimd.Multiply(e, AdvSimd.ReciprocalSquareRootStep(AdvSimd.Multiply(a, e), e)));
        }

        throw Unsupported();
    }

    /// <summary>
    /// The refined estimate where the raw one is finite and non-zero, else the
    /// raw one.
    /// </summary>
    /// <remarks>
    /// An infinite or zero estimate (a zero, infinite or tiny input) is
    /// already the answer x86 gives, and refining it multiplies a zero by an
    /// infinity somewhere: <c>rsqrt(0)</c> would come out NaN rather than
    /// +infinity, and the reciprocal of a tiny denormal would flip sign.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> Refined(Vector128<float> raw, Vector128<float> refined)
    {
        Vector128<float> finiteNonZero = Vector128.AndNot(
            Vector128.LessThan(Vector128.Abs(raw), Vector128.Create(float.PositiveInfinity)),
            Vector128.Equals(raw, Vector128<float>.Zero));
        return Vector128.ConditionalSelect(finiteNonZero, refined, raw);
    }

    /// <summary>
    /// The exception every caller throws on a CPU with no estimate
    /// instructions, so the message is the same wherever it surfaces.
    /// </summary>
    /// <returns>The exception.</returns>
    public static PlatformNotSupportedException Unsupported() =>
        new("stock's arithmetic starts from a hardware reciprocal estimate, and this CPU has "
            + "neither SSE nor AdvSimd. It refuses rather than computing an exact value, because "
            + "a comparison against stock that silently stopped computing the same thing would "
            + "still report success.");
}
