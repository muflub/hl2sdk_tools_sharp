using Xunit;

namespace SourceSharp.Tests;

/// <summary>
/// <see cref="VendorGolden"/>: a vendor's expected values are stock's with a
/// delta applied, and the delta records exactly the lines that moved.
/// </summary>
public class VendorGoldenTests
{
    [Fact]
    public void ADeltaRecordsOnlyTheLinesThatDiffer()
    {
        string delta = VendorGolden.Delta(["a", "b", "c"], ["a", "B", "c"]);

        Assert.Equal("count 3\n1 B\n", delta);
    }

    [Fact]
    public void ApplyingADeltaGivesBackTheActualLines()
    {
        string[] stock = ["a", "b", "c", "d"];
        string[] actual = ["x", "b", "c y z", "d"];

        Assert.Equal(actual, VendorGolden.Apply(stock, VendorGolden.Delta(stock, actual)));
    }

    [Fact]
    public void ADeltaCanGrowOrShrinkTheList()
    {
        string[] stock = ["a", "b"];

        Assert.Equal(["a", "b", "c"], VendorGolden.Apply(stock, VendorGolden.Delta(stock, ["a", "b", "c"])));
        Assert.Equal(["a"], VendorGolden.Apply(stock, VendorGolden.Delta(stock, ["a"])));
    }

    [Fact]
    public void AnEmptyDeltaIsStock()
    {
        string[] stock = ["a", "b"];

        Assert.Equal(stock, VendorGolden.Apply(stock, "count 2\n"));
    }

    [Fact]
    public void ADeltaWithoutACountIsRefused()
    {
        Assert.Throws<FormatException>(() => VendorGolden.Apply(["a"], "0 b\n"));
    }

    [Fact]
    public void ADeltaLineWithoutAValueIsRefused()
    {
        Assert.Throws<FormatException>(() => VendorGolden.Apply(["a"], "count 1\n0\n"));
    }

    [Fact]
    public void ALineNeitherStockNorTheDeltaCoversIsRefused()
    {
        Assert.Throws<FormatException>(() => VendorGolden.Apply(["a"], "count 3\n1 b\n"));
    }

    [Fact]
    public void BitsRoundTripExactly()
    {
        float value = BitConverter.UInt32BitsToSingle(0x41f80002);

        Assert.Equal("41f80002", VendorGolden.Bits(value));
        Assert.Equal(BitConverter.SingleToUInt32Bits(value), BitConverter.SingleToUInt32Bits(VendorGolden.FromBits("41f80002")));
    }

    [Fact]
    public void TheReferenceVendorsExpectedValueIsStocks()
    {
        if (ReferenceRsqrt.CpuVendor() != ReferenceRsqrt.ReferenceVendor)
        {
            return;
        }

        Assert.Equal(31f, VendorGolden.Expected("unused-on-the-reference-vendor", 31f, 32f));
    }
}
