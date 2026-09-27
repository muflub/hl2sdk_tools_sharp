//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Text;

using Xunit;

namespace SourceSharp.Tests;

/// <summary>
/// Whether this CPU's <c>rsqrtss</c> is the one the committed stock goldens
/// were measured with.
///
/// <para>
/// <b>WHY THIS EXISTS.</b> <c>Vec3.NormaliseLikeStock</c> reproduces stock's
/// reciprocal-square-root ESTIMATE, and that estimate is implementation-defined
/// within its error bound. The goldens those parity facts compare against were
/// produced on an AMD Zen part. On an Intel part the estimate differs in the
/// low bits (<c>rsqrtss(1.0f)</c> is <c>0x3F7FF000</c> there), and on arm64
/// the estimate is ARM's own instruction altogether, so every
/// quantity downstream of a stock normalise moves: plane distances, hit
/// distances, cooked collision blobs, lighting lumps. The implementation is
/// not wrong on that machine; the golden is simply not that machine's answer.
/// </para>
///
/// <para>
/// A failure there would read as a regression, and a pass forced by loosening
/// the comparison would make the oracle weaker everywhere. So those facts take
/// their expected values from <see cref="VendorGolden"/>, which holds a
/// per-vendor delta against the goldens, and SKIP, visibly and with this
/// reason, on a vendor that has none.
/// </para>
/// </summary>
internal static class ReferenceRsqrt
{
    /// <summary>The CPUID vendor string of the machine the goldens came from.</summary>
    public const string ReferenceVendor = "AuthenticAMD";

    /// <summary>
    /// The key arm64 CPUs share. ARM defines its estimate instructions' results
    /// exactly, so one capture serves every arm64 CPU, whoever made it.
    /// </summary>
    public const string Arm64 = "Arm64";

    /// <summary>
    /// Which estimate this CPU produces: its CPUID vendor string on x86,
    /// <see cref="Arm64"/> on arm64, or null on anything else.
    /// </summary>
    public static string? CpuVendor()
    {
        if (X86Base.IsSupported)
        {
            (_, int ebx, int ecx, int edx) = X86Base.CpuId(0, 0);
            return VendorFromRegisters(ebx, edx, ecx);
        }

        return AdvSimd.Arm64.IsSupported ? Arm64 : null;
    }

    /// <summary>
    /// The twelve-byte vendor string CPUID leaf 0 returns, which it spreads
    /// over EBX, EDX, ECX in that order.
    /// </summary>
    public static string VendorFromRegisters(int ebx, int edx, int ecx)
    {
        Span<byte> bytes = stackalloc byte[12];
        BitConverter.TryWriteBytes(bytes[..4], ebx);
        BitConverter.TryWriteBytes(bytes[4..8], edx);
        BitConverter.TryWriteBytes(bytes[8..], ecx);
        return Encoding.ASCII.GetString(bytes);
    }

    /// <summary>
    /// Null when this vendor has expected values to compare against (the
    /// goldens themselves, or a captured delta), else why the fact is skipped.
    /// </summary>
    public static string? SkipReason(string? vendor, bool hasCaptures)
    {
        if (vendor == ReferenceVendor || (vendor is not null && hasCaptures))
        {
            return null;
        }

        return $"the stock goldens this fact compares against were measured on {ReferenceVendor}; "
            + $"this CPU is {vendor ?? "neither x86 nor arm64"}, whose reciprocal estimate differs in the low bits, "
            + "and it has no captured delta to compare against instead";
    }

    /// <summary><see cref="SkipReason(string?, bool)"/> for this machine.</summary>
    public static string? SkipReasonHere()
    {
        string? vendor = CpuVendor();
        return SkipReason(vendor, vendor is not null && (VendorGolden.Capturing || VendorGolden.HasCaptures(vendor)));
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> for a fact that compares a stock-normalised
/// result bit-for-bit against a committed golden. See <see cref="ReferenceRsqrt"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ReferenceRsqrtFactAttribute : FactAttribute
{
    public ReferenceRsqrtFactAttribute()
    {
        Skip = ReferenceRsqrt.SkipReasonHere();
    }
}

/// <summary>
/// The <see cref="TheoryAttribute"/> counterpart of
/// <see cref="ReferenceRsqrtFactAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ReferenceRsqrtTheoryAttribute : TheoryAttribute
{
    public ReferenceRsqrtTheoryAttribute()
    {
        Skip = ReferenceRsqrt.SkipReasonHere();
    }
}
