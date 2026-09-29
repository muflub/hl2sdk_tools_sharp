//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace SourceSharp.MapTools.Parallel;

/// <summary>
/// What a compile needs to know about the CPU it runs on to pick between
/// implementations that give the same bytes at different speeds.
/// </summary>
/// <param name="Vendor">
/// The CPUID vendor string (<see cref="Amd"/>, <see cref="Intel"/>, ...), or
/// empty where there is no CPUID (arm64 and anything else that is not x86).
/// </param>
/// <param name="Family">
/// The CPUID display family: the base family, plus the extended family when
/// the base is <c>0xF</c> (see <see cref="DisplayFamily"/>). Zero where there
/// is no CPUID.
/// </param>
/// <param name="Vector512Accelerated">
/// Whether <see cref="Vector512.IsHardwareAccelerated"/> holds: the runtime
/// both found AVX-512 and chose to use it.
/// </param>
/// <remarks>
/// <para>
/// <b>Why a record rather than asking the hardware where it is needed.</b> A
/// choice that depends on the CPU is a choice a fact cannot reach on any
/// machine but the one it names. With the description as a value, the rule
/// that turns it into a choice is a pure function a fact can hand "a Zen 5",
/// "a Zen 4" and "an Ice Lake" in turn, on whatever CI runner it lands on;
/// only <see cref="Detect"/> reads the real CPU, and a compile calls it once.
/// </para>
/// <para>
/// <b>Why the vendor and family matter and not only the feature bit.</b>
/// <see cref="Vector512.IsHardwareAccelerated"/> says the instructions exist
/// and are not known to throttle the clock, not that they are fast. The one
/// place the choice has been measured (the vvis separator derivation, see
/// <see cref="Vis.VisSeparatorPath"/>) leans on 512-bit double-precision
/// square root and divide, which Zen 5 (family <c>0x1A</c>) executes at full
/// width, Zen 4 (family <c>0x19</c>) executes as two 256-bit halves, and the
/// Ice Lake-class Xeons measured run at reduced throughput. The feature bit is
/// the same on all three.
/// </para>
/// <para>
/// Reading CPUID touches nothing in the process: it is an unprivileged
/// instruction with no side effect, so a library may do it (unlike reading
/// an environment variable, which is the host's business).
/// </para>
/// </remarks>
public sealed record CpuCapabilities(string Vendor, int Family, bool Vector512Accelerated)
{
    /// <summary>AMD's CPUID vendor string.</summary>
    public const string Amd = "AuthenticAMD";

    /// <summary>Intel's CPUID vendor string.</summary>
    public const string Intel = "GenuineIntel";

    /// <summary>Whether <see cref="Vendor"/> is AMD's.</summary>
    public bool IsAmd => string.Equals(Vendor, Amd, StringComparison.Ordinal);

    /// <summary>Describes the CPU this process runs on.</summary>
    /// <returns>The vendor, display family and 512-bit acceleration.</returns>
    /// <remarks>
    /// Called once per compile by the stage that needs it, never cached in a
    /// static: the answer cannot change under a running process, but a static
    /// is shared mutable state all the same, and the libraries have none.
    /// </remarks>
    public static CpuCapabilities Detect()
    {
        bool wide = Vector512.IsHardwareAccelerated;
        if (!X86Base.IsSupported)
        {
            return new CpuCapabilities(string.Empty, 0, wide);
        }

        (_, int ebx, int ecx, int edx) = X86Base.CpuId(0, 0);
        (int signature, _, _, _) = X86Base.CpuId(1, 0);
        return new CpuCapabilities(VendorFromRegisters(ebx, edx, ecx), DisplayFamily(signature), wide);
    }

    /// <summary>
    /// The twelve-byte vendor string CPUID leaf 0 returns, which it spreads
    /// over EBX, EDX and ECX, in that order.
    /// </summary>
    /// <param name="ebx">EBX of leaf 0.</param>
    /// <param name="edx">EDX of leaf 0.</param>
    /// <param name="ecx">ECX of leaf 0.</param>
    /// <returns>The vendor string, e.g. <see cref="Amd"/>.</returns>
    public static string VendorFromRegisters(int ebx, int edx, int ecx)
    {
        Span<byte> bytes = stackalloc byte[12];
        BitConverter.TryWriteBytes(bytes[..4], ebx);
        BitConverter.TryWriteBytes(bytes[4..8], edx);
        BitConverter.TryWriteBytes(bytes[8..], ecx);
        return Encoding.ASCII.GetString(bytes);
    }

    /// <summary>
    /// The display family from CPUID leaf 1's EAX: bits 8-11, plus bits 20-27
    /// when bits 8-11 are <c>0xF</c>.
    /// </summary>
    /// <param name="signature">EAX of leaf 1.</param>
    /// <returns>
    /// The family as vendors number it: <c>0x19</c> for Zen 3 and Zen 4,
    /// <c>0x1A</c> for Zen 5, 6 for every Intel core since the Pentium Pro.
    /// </returns>
    /// <remarks>
    /// The extended family only counts when the base family is saturated, by
    /// the definition both vendors publish; AMD's Zen parts all report base
    /// <c>0xF</c> and put the rest in the extension.
    /// </remarks>
    public static int DisplayFamily(int signature)
    {
        int family = (signature >> 8) & 0xF;
        return family == 0xF ? family + ((signature >> 20) & 0xFF) : family;
    }
}
