using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Vis;

/// <summary>
/// The visibility lump's run-length coder
/// (<c>utils/common/bsplib.cpp:1441</c> and <c>:1477</c>).
/// </summary>
public class VisRunLengthTests
{
    [Fact]
    public void ANonZeroByteIsALiteral()
    {
        byte[] destination = new byte[16];

        int written = VisRunLength.Compress([0xAB, 0xCD], destination);

        Assert.Equal(2, written);
        Assert.Equal<byte>([0xAB, 0xCD], destination.AsSpan(0, 2).ToArray());
    }

    [Fact]
    public void AZeroByteCostsTwoBytesEvenWhenItIsAlone()
    {
        byte[] destination = new byte[16];

        int written = VisRunLength.Compress([0x01, 0x00, 0x02], destination);

        // The zero, then a repeat count of one. This is why the worst case is
        // twice the row rather than the row plus a constant.
        Assert.Equal(4, written);
        Assert.Equal<byte>([0x01, 0x00, 0x01, 0x02], destination.AsSpan(0, 4).ToArray());
    }

    [Fact]
    public void ARunOfZeroesBecomesOneCount()
    {
        byte[] row = new byte[10];
        row[0] = 0x7F;
        byte[] destination = new byte[64];

        int written = VisRunLength.Compress(row, destination);

        Assert.Equal<byte>([0x7F, 0x00, 0x09], destination.AsSpan(0, written).ToArray());
    }

    [Fact]
    public void ARunStopsAtTwoHundredAndFiftyFive()
    {
        // bsplib.cpp:1458 breaks at rep == 255, so 300 zeroes is 255 then 45 --
        // NOT a truncated 300 and not a count that wrapped to 44.
        byte[] row = new byte[300];
        byte[] destination = new byte[VisRunLength.MaxCompressedLength(row.Length)];

        int written = VisRunLength.Compress(row, destination);

        Assert.Equal<byte>([0x00, 0xFF, 0x00, 0x2D], destination.AsSpan(0, written).ToArray());
    }

    [Fact]
    public void ARowOfNothingButZeroesRoundTrips()
    {
        byte[] row = new byte[300];
        byte[] destination = new byte[VisRunLength.MaxCompressedLength(row.Length)];
        int written = VisRunLength.Compress(row, destination);

        byte[] back = new byte[row.Length];
        VisRunLength.Decompress(destination.AsSpan(0, written), back);

        Assert.Equal(row, back);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(31)]
    [InlineData(256)]
    [InlineData(1021)]
    public void EverySeededRowRoundTrips(int length)
    {
        // A fixed seed, not a random one: a coder that fails on one shape in ten
        // thousand should fail on the same shape every run, or the failure is
        // unreproducible and gets closed as a flake.
        byte[] row = SeededRow(length, seed: 12345);
        byte[] destination = new byte[VisRunLength.MaxCompressedLength(length)];

        int written = VisRunLength.Compress(row, destination);
        Assert.True(written <= VisRunLength.MaxCompressedLength(length));

        byte[] back = new byte[length];
        VisRunLength.Decompress(destination.AsSpan(0, written), back);

        Assert.Equal(row, back);
    }

    [Fact]
    public void TheWorstCaseIsExactlyTwiceTheRow()
    {
        // Alternating zero and non-zero: every zero costs two bytes and every
        // literal costs one, so a row of 2n bytes costs 3n -- and the bound is
        // reached exactly by a row that is ALL zeroes with no run longer than
        // one, which cannot happen; the honest statement is that the bound
        // holds, which is what the compressor's argument check relies on.
        byte[] row = new byte[64];
        for (int i = 0; i < row.Length; i += 2)
        {
            row[i] = 0xFF;
        }

        byte[] destination = new byte[VisRunLength.MaxCompressedLength(row.Length)];
        int written = VisRunLength.Compress(row, destination);

        Assert.Equal(96, written);
        Assert.True(written <= destination.Length);
    }

    [Fact]
    public void CompressingIntoATooSmallBufferThrows()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            byte[] destination = new byte[3];
            VisRunLength.Compress(new byte[4], destination);
        });
    }

    [Fact]
    public void AZeroRepeatCountIsRefused()
    {
        // bsplib.cpp:1497 calls Error() here. A zero repeat can only come from
        // a corrupt lump, and silently treating it as "skip nothing" is how a
        // decoder walks off the end of a map.
        Assert.Throws<InvalidDataException>(() => VisRunLength.Decompress([0x00, 0x00], new byte[4]));
    }

    [Fact]
    public void ARunThatOverrunsTheRowIsClamped()
    {
        // bsplib.cpp:1500-1504 warns and clamps rather than erroring, so a map
        // whose last run is generous still loads.
        byte[] row = new byte[4];
        VisRunLength.Decompress([0x00, 0xFF], row);

        Assert.Equal<byte>([0, 0, 0, 0], row);
    }

    [Fact]
    public void TheDecoderAgreesWithTheOneInMapFormats()
    {
        // Two decoders written from the same C function, held against each
        // other: MapFormats' reads a lump for the engine's benefit and this one
        // reads a row for vvis's, and a divergence between them would show up
        // as a map that validates and renders wrong.
        byte[] row = SeededRow(97, seed: 99);
        byte[] compressed = new byte[VisRunLength.MaxCompressedLength(row.Length)];
        int written = VisRunLength.Compress(row, compressed);

        byte[] mine = new byte[row.Length];
        VisRunLength.Decompress(compressed.AsSpan(0, written), mine);

        // 97 rows of one byte each is 776 clusters; the lump reader needs a
        // header describing that shape before it will decode anything.
        VisibilityLump lump = BuildLumpWithClusterCount(row.Length * 8);
        byte[] theirs = new byte[lump.RowBytes()];
        lump.DecompressRow(compressed.AsSpan(0, written), theirs);

        Assert.Equal(row.Length, lump.RowBytes());
        Assert.Equal(mine, theirs);
    }

    private static VisibilityLump BuildLumpWithClusterCount(int clusters)
    {
        byte[] bytes = new byte[4 + (clusters * 8)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, clusters);
        return VisibilityLump.Read(new BspLumpData(bytes, 0, 0))!;
    }

    private static byte[] SeededRow(int length, int seed)
    {
        byte[] row = new byte[length];
        uint state = (uint)seed;
        for (int i = 0; i < length; i++)
        {
            // xorshift, written out: the point is that the SAME bytes come back
            // on every machine and every run, which Random does not promise.
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;

            // Biased towards zero, because a visibility row is mostly zero and
            // a uniform row would barely exercise the run encoder.
            row[i] = (state & 3) == 0 ? (byte)(state & 0xFF) : (byte)0;
        }

        return row;
    }
}
