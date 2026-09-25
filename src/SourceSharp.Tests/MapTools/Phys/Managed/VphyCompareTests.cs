using System.Buffers.Binary;
using Xunit;

namespace SourceSharp.Tests.MapTools.Phys.Managed;

/// <summary>
/// The grading instrument's own known answers, so a comparator that cannot fail
/// is caught before it can quietly pass everything.
/// </summary>
public class VphyCompareTests
{
    private static byte[] CubeBlob() =>
        CookerFixture.Cook(CookerFixture.Jobs("shapes")[0], CookMode.Stock, CookerFixture.Context(CookMode.Stock))!;

    [Fact]
    public void IdenticalBlobsGradeIdentical()
    {
        byte[] a = CubeBlob();
        Assert.True(VphyCompare.Compare(a, (byte[])a.Clone()).Identical);
    }

    [Fact]
    public void AOneUlpMassCentreChangeIsWithinTolerance()
    {
        byte[] a = CubeBlob();
        byte[] b = (byte[])a.Clone();
        float mc = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(28 + 12));
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(28 + 12), MathF.BitIncrement(mc));
        VphyCompare.Result r = VphyCompare.Compare(a, b);
        Assert.False(r.Identical);
        Assert.True(r.StructureEqual && r.FloatsWithin, r.Detail);
    }

    [Fact]
    public void ALargeFloatChangeFailsTheTolerance()
    {
        byte[] a = CubeBlob();
        byte[] b = (byte[])a.Clone();
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(28 + 24), 2.0f * BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(28 + 24)));
        Assert.False(VphyCompare.Compare(a, b).FloatsWithin);
    }

    [Fact]
    public void AChangedEdgeWordFailsTheStructure()
    {
        byte[] a = CubeBlob();
        byte[] b = (byte[])a.Clone();
        int ledge = 28 + 48;
        b[ledge + 16 + 4] ^= 1; // first triangle, first edge's start point index
        Assert.False(VphyCompare.Compare(a, b).StructureEqual);
    }

    [Fact]
    public void ASignFlipOfATinyValueIsWithinTheAbsoluteFloor()
    {
        Assert.Equal(0, VphyCompare.UlpDistance(0f, -0f));
        Assert.Equal(int.MaxValue, VphyCompare.UlpDistance(1e-12f, -1e-12f));
    }
}
