using System.Buffers.Binary;
using System.Text;

using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Diagnostics;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Collision;

/// <summary>
/// The keydata writer (<c>CTextBuffer</c>, <c>ivp.cpp:51-139</c>) and the two
/// lumps' framing (<c>ivp.cpp:1591-1652</c>, <c>disp_ivp.cpp:316-336</c>,
/// <c>bsplib.cpp:1556</c>).
/// </summary>
public class PhysLumpFramingTests
{
    private static string Text(Action<CollisionTextBuffer> write)
    {
        CollisionTextBuffer b = new();
        write(b);
        return Encoding.Latin1.GetString([.. b.Bytes]);
    }

    [Fact]
    public void AnIntKeyIsQuotedPairAndNewline() =>
        Assert.Equal("\"index\" \"-3\"\n", Text(b => b.WriteIntKey("index", -3)));

    [Fact]
    public void AFloatKeyIsPrintedWithSixDecimals() =>
        Assert.Equal("\"mass\" \"11655.935547\"\n", Text(b => b.WriteFloatKey("mass", 11655.935547f)));

    [Fact]
    public void AFloatArrayHasATrailingSpaceInsideTheQuotes() =>
        Assert.Equal("\"surfaceplane\" \"0.000000 1.500000 \"\n", Text(b => b.WriteFloatArrayKey("surfaceplane", [0f, 1.5f])));

    [Fact]
    public void AnOverlongNumericKeyIsDropped() =>
        Assert.Equal(string.Empty, Text(b => b.WriteIntKey(new string('k', 1001), 1)));

    [Fact]
    public void AnOverlongStringKeyIsNot() =>
        Assert.StartsWith("\"kkk", Text(b => b.WriteStringKey(new string('k', 1001), "v")), StringComparison.Ordinal);

    [Fact]
    public void TerminateAppendsOneNul()
    {
        CollisionTextBuffer b = new();
        b.WriteText("x");
        b.Terminate();

        Assert.Equal([(byte)'x', 0], b.ToArray());
    }

    private static PhysCollideModel Model(int index, int[] sizes, string key) =>
        new(index, [.. sizes.Select(s => Enumerable.Repeat((byte)0xAB, s).ToArray())], [.. Encoding.Latin1.GetBytes(key), 0]);

    [Fact]
    public void ARecordIsHeaderSizesBlobsThenKeydata()
    {
        byte[] lump = PhysCollideLump.Write([Model(0, [5, 3], "k")]);

        // {modelIndex, dataSize = 4*2 + 8, keydataSize = 2, solidCount = 2}
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(lump));
        Assert.Equal(16, BinaryPrimitives.ReadInt32LittleEndian(lump.AsSpan(4)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(lump.AsSpan(8)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(lump.AsSpan(12)));
        Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(lump.AsSpan(16)));
    }

    [Fact]
    public void TheLumpEndsWithTheTerminatorAndIsNotPadded()
    {
        // ivp.cpp:1646-1652: {-1, -1, 0, 0}; the 5-byte blob stays 5 bytes.
        byte[] lump = PhysCollideLump.Write([Model(0, [5], "k")]);

        Assert.Equal(16 + 4 + 5 + 2 + 16, lump.Length);
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(lump.AsSpan(lump.Length - 16)));
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(lump.AsSpan(lump.Length - 12)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(lump.AsSpan(lump.Length - 8)));
    }

    [Fact]
    public void AnEmptyLumpIsTheTerminatorAlone() =>
        Assert.Equal(16, PhysCollideLump.Write([]).Length);

    [Fact]
    public void ReadInvertsWrite()
    {
        PhysCollideModel[] models = [Model(0, [5, 7], "world"), Model(3, [9], "door")];

        IReadOnlyList<PhysCollideModel> back = PhysCollideLump.Read(PhysCollideLump.Write(models));

        Assert.Equal([0, 3], back.Select(m => m.ModelIndex));
        Assert.Equal(["world", "door"], back.Select(m => m.KeyText));
        Assert.Equal([5, 7, 9], back.SelectMany(m => m.Solids.Select(s => s.Length)));
    }

    [Fact]
    public void ALumpWithoutATerminatorIsAnError() =>
        Assert.Throws<MapCompileException>(() => PhysCollideLump.Read(PhysCollideLump.Write([Model(0, [4], "k")]).AsSpan(0, 20)));

    [Fact]
    public void SwapAlignmentPadsEachSolidToFourAndRewritesItsSize()
    {
        // bsplib.cpp:1611-1628.
        byte[] aligned = PhysCollideLump.AlignForSwap(PhysCollideLump.Write([Model(0, [5], "k")]));

        Assert.Equal(8, BinaryPrimitives.ReadInt32LittleEndian(aligned.AsSpan(16)));
        Assert.Equal(4 + 8, BinaryPrimitives.ReadInt32LittleEndian(aligned.AsSpan(4)));
    }

    [Fact]
    public void SwapAlignmentPadsTheKeydataToAFourByteBoundary()
    {
        // bsplib.cpp:1630-1644: dataSize 12 + keydata 2 -> 2 bytes of padding.
        byte[] aligned = PhysCollideLump.AlignForSwap(PhysCollideLump.Write([Model(0, [5], "k")]));

        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(aligned.AsSpan(8)));
        Assert.Equal(0, (16 + 12 + 4) % 4);
        Assert.Equal(16 + 12 + 4 + 16, aligned.Length);
    }

    [Fact]
    public void PhysDispIsCountSizesThenBlobs()
    {
        byte[] lump = PhysDispLump.Write([[1, 2, 3], null, [4]]);

        Assert.Equal(new byte[] { 3, 0, 3, 0, 0xFF, 0xFF, 1, 0, 1, 2, 3, 4 }, lump);
    }

    [Fact]
    public void PhysDispSizesReadNoneAsMinusOne() =>
        Assert.Equal([3, -1, 1], PhysDispLump.ReadSizes(PhysDispLump.Write([[1, 2, 3], null, [4]])));

    [Fact]
    public void APhysDispBlobTooLargeForItsShortIsRefused() =>
        Assert.Throws<MapCompileException>(() => PhysDispLump.Write([new byte[70000]]));
}
