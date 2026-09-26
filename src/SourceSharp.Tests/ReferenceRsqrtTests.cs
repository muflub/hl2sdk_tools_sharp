using System.Buffers.Binary;
using System.Runtime.Intrinsics.X86;
using System.Text;

using Xunit;

namespace SourceSharp.Tests;

/// <summary>
/// <see cref="ReferenceRsqrt"/>: the gate that keeps a bit-exact stock golden
/// from being judged on a CPU whose estimate it was not measured with.
/// </summary>
public class ReferenceRsqrtTests
{
    [Fact]
    public void TheReferenceVendorRunsTheFact()
    {
        Assert.Null(ReferenceRsqrt.SkipReason("AuthenticAMD", hasCaptures: false));
    }

    [Fact]
    public void AnotherVendorWithCapturesRunsTheFact()
    {
        Assert.Null(ReferenceRsqrt.SkipReason("GenuineIntel", hasCaptures: true));
    }

    [Fact]
    public void AnotherVendorWithoutCapturesSkipsAndSaysWhich()
    {
        string? reason = ReferenceRsqrt.SkipReason("GenuineIntel", hasCaptures: false);

        Assert.NotNull(reason);
        Assert.Contains("GenuineIntel", reason, StringComparison.Ordinal);
        Assert.Contains("rsqrtss", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NoVendorSkips()
    {
        string? reason = ReferenceRsqrt.SkipReason(null, hasCaptures: true);

        Assert.NotNull(reason);
        Assert.Contains("not x86", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVendorIsReadFromEbxThenEdxThenEcx()
    {
        // CPUID leaf 0 on an AMD part: "Auth" in EBX, "enti" in EDX, "cAMD" in ECX.
        int ebx = BinaryPrimitives.ReadInt32LittleEndian(Encoding.ASCII.GetBytes("Auth"));
        int edx = BinaryPrimitives.ReadInt32LittleEndian(Encoding.ASCII.GetBytes("enti"));
        int ecx = BinaryPrimitives.ReadInt32LittleEndian(Encoding.ASCII.GetBytes("cAMD"));

        Assert.Equal("AuthenticAMD", ReferenceRsqrt.VendorFromRegisters(ebx, edx, ecx));
    }

    [Fact]
    public void ThisMachineReportsAVendorExactlyWhenItIsX86()
    {
        string? vendor = ReferenceRsqrt.CpuVendor();

        if (X86Base.IsSupported)
        {
            Assert.NotNull(vendor);
            Assert.Equal(12, vendor.Length);
        }
        else
        {
            Assert.Null(vendor);
        }
    }

    [Fact]
    public void TheAttributesSkipExactlyWhenTheGateSays()
    {
        string? expected = ReferenceRsqrt.SkipReasonHere();

        Assert.Equal(expected, new ReferenceRsqrtFactAttribute().Skip);
        Assert.Equal(expected, new ReferenceRsqrtTheoryAttribute().Skip);
    }
}
