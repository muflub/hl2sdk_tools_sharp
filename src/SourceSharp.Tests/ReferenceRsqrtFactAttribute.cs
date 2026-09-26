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
/// low bits (<c>rsqrtss(1.0f)</c> is <c>0x3F7FF000</c> there), so every
/// quantity downstream of a stock normalise moves: plane distances, hit
/// distances, cooked collision blobs, lighting lumps. The implementation is
/// not wrong on that machine; the golden is simply not that machine's answer.
/// </para>
///
/// <para>
/// A failure there would read as a regression, and a pass forced by loosening
/// the comparison would make the oracle weaker everywhere. So the facts that
/// compare a stock-normalised result bit-for-bit against a committed golden
/// SKIP, visibly and with this reason, on any other vendor.
/// </para>
/// </summary>
internal static class ReferenceRsqrt
{
    /// <summary>The CPUID vendor string of the machine the goldens came from.</summary>
    public const string ReferenceVendor = "AuthenticAMD";

    /// <summary>This CPU's CPUID vendor string, or null off x86.</summary>
    public static string? CpuVendor()
    {
        if (!X86Base.IsSupported)
        {
            return null;
        }

        (_, int ebx, int ecx, int edx) = X86Base.CpuId(0, 0);
        return VendorFromRegisters(ebx, edx, ecx);
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
    /// Null when a stock-normalised golden is this vendor's answer, else why
    /// the fact is skipped.
    /// </summary>
    public static string? SkipReason(string? vendor)
    {
        if (vendor == ReferenceVendor)
        {
            return null;
        }

        return $"the stock goldens this fact compares against were measured on {ReferenceVendor}; "
            + $"this CPU is {vendor ?? "not x86"}, whose rsqrtss estimate differs in the low bits, "
            + "so a bit-exact comparison against them says nothing here";
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
        Skip = ReferenceRsqrt.SkipReason(ReferenceRsqrt.CpuVendor());
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
        Skip = ReferenceRsqrt.SkipReason(ReferenceRsqrt.CpuVendor());
    }
}
