//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Rad.Bounce;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Bounce;

/// <summary>
/// <see cref="TransferSetCodec"/>: a transfer set survives index, chunks and
/// packing entry for entry, and anything that does not fit is refused.
/// </summary>
public sealed class TransferSetCodecTests
{
    // Four patches: 3, 0, 5 and 2 transfers, patch numbers going up and down.
    // Two segments, patch 3's list before patch 0's in the first, as a build's
    // chunks can leave them: the codec reads through For, never the layout.
    internal static TransferSet Sample()
    {
        int[] counts = [3, 0, 5, 2];
        int[] segmentOf = [0, 0, 1, 0];
        int[] offsets = [2, 0, 0, 0];
        Transfer[][] segments =
        [
            [new(0, 0.75f), new(2, 0.125f), new(1, 0.5f), new(2, 0.25f), new(3, 1e-7f)],
            [new(0, 0.1f), new(3, 0.2f), new(1, 0.3f), new(2, float.Epsilon), new(0, 0.9f)],
        ];
        return new TransferSet(segments, segmentOf, offsets, counts, 5);
    }

    private static List<(int Patch, float Weight)[]> Lists(TransferSet set) =>
        [.. Enumerable.Range(0, set.PatchCount).Select(p => set.For(p).ToArray().Select(t => (t.Patch, t.Weight)).ToArray())];

    [Fact]
    public void ASetRoundTripsThroughIndexAndChunks()
    {
        TransferSet set = Sample();
        TransferSet? back = TransferSetCodec.Read(TransferSetCodec.Index(set), [.. TransferSetCodec.Chunks(set, 1 << 20)], 4);

        Assert.NotNull(back);
        Assert.Equal(Lists(set), Lists(back));
        Assert.Equal(set.Max, back.Max);
        Assert.Equal(set.Total, back.Total);
    }

    [Fact]
    public void ChunksSplitAcrossPatchLists()
    {
        // Three transfers per chunk: patch 2's list spans two chunks.
        byte[][] chunks = [.. TransferSetCodec.Chunks(Sample(), 24)];

        Assert.Equal([24, 24, 24, 8], chunks.Select(c => c.Length));
        Assert.Equal(Lists(Sample()), Lists(TransferSetCodec.Read(TransferSetCodec.Index(Sample()), chunks, 4)!));
    }

    [Fact]
    public void PackedChunksUnpackToTheSameBytes()
    {
        foreach (byte[] chunk in TransferSetCodec.Chunks(Sample(), 24))
        {
            Assert.Equal(chunk, TransferSetCodec.Unpack(TransferSetCodec.Pack(chunk)));
        }
    }

    [Fact]
    public void AnEmptySetRoundTrips()
    {
        TransferSet empty = new([], [0, 0], [0, 0], [0, 0], 0);
        TransferSet? back = TransferSetCodec.Read(TransferSetCodec.Index(empty), [.. TransferSetCodec.Chunks(empty, 64)], 2);

        Assert.NotNull(back);
        Assert.Equal(0, back.Total);
    }

    [Fact]
    public void AnotherPatchCountIsRefused()
    {
        TransferSet set = Sample();
        Assert.Null(TransferSetCodec.Read(TransferSetCodec.Index(set), [.. TransferSetCodec.Chunks(set, 1 << 20)], 5));
    }

    [Fact]
    public void AMissingChunkIsRefused()
    {
        TransferSet set = Sample();
        byte[][] chunks = [.. TransferSetCodec.Chunks(set, 24)];
        Assert.Null(TransferSetCodec.Read(TransferSetCodec.Index(set), chunks[..^1], 4));
    }

    [Fact]
    public void APatchOutOfRangeIsRefused()
    {
        TransferSet set = Sample();
        byte[] chunk = [.. TransferSetCodec.Chunks(set, 1 << 20).Single()];
        chunk[0] = 9;
        Assert.Null(TransferSetCodec.Read(TransferSetCodec.Index(set), [chunk], 4));
    }

    // ---- ReadPacked: the cache hit's reader, straight from packed chunks ----

    private static readonly ParallelOptions Two = new() { MaxDegreeOfParallelism = 2 };

    private static byte[]?[] Packed(TransferSet set, long chunkBytes) =>
        [.. TransferSetCodec.Chunks(set, chunkBytes).Select(TransferSetCodec.Pack)];

    [Theory]
    [InlineData(24)]
    [InlineData(8)]
    [InlineData(1 << 20)]
    public void APackedSetReadsAsTheUnpackedReaderReadsIt(long chunkBytes)
    {
        TransferSet set = Sample();
        TransferSet? viaRaw = TransferSetCodec.Read(TransferSetCodec.Index(set), [.. TransferSetCodec.Chunks(set, chunkBytes)], 4);
        TransferSet? viaPacked = TransferSetCodec.ReadPacked(TransferSetCodec.Index(set), Packed(set, chunkBytes), 4, Two);

        Assert.NotNull(viaPacked);
        Assert.Equal(Lists(viaRaw!), Lists(viaPacked));
        Assert.Equal(viaRaw!.Max, viaPacked.Max);
        Assert.Equal(viaRaw.Total, viaPacked.Total);
        Assert.Equal(viaRaw.Arena.ToArray(), viaPacked.Arena.ToArray());
    }

    [Fact]
    public void ReadingPackedChunksLetsGoOfEveryOne()
    {
        // The caller's array is emptied as the arena fills, so the packed
        // bytes are not held beside the whole set once it is built.
        TransferSet set = Sample();
        byte[]?[] packed = Packed(set, 24);

        _ = TransferSetCodec.ReadPacked(TransferSetCodec.Index(set), packed, 4, Two);

        Assert.All(packed, Assert.Null);
    }

    [Fact]
    public void ARefusedPackedSetStillLetsGoOfItsChunks()
    {
        TransferSet set = Sample();
        byte[]?[] packed = Packed(set, 24);

        Assert.Null(TransferSetCodec.ReadPacked(TransferSetCodec.Index(set), packed, 5, Two));
        Assert.All(packed, Assert.Null);
    }

    [Fact]
    public void AnEmptyPackedSetReads()
    {
        TransferSet empty = new([], [0, 0], [0, 0], [0, 0], 0);
        TransferSet? back = TransferSetCodec.ReadPacked(TransferSetCodec.Index(empty), Packed(empty, 64), 2, Two);

        Assert.NotNull(back);
        Assert.Equal(0, back.Total);
    }

    [Fact]
    public void AMissingPackedChunkIsRefused()
    {
        TransferSet set = Sample();
        Assert.Null(TransferSetCodec.ReadPacked(TransferSetCodec.Index(set), Packed(set, 24)[..^1], 4, Two));
    }

    [Fact]
    public void APackedChunkThatStatesTheWrongCountIsRefused()
    {
        TransferSet set = Sample();
        byte[]?[] packed = Packed(set, 24);
        byte[] first = packed[0]!;
        byte[] second = packed[1]!;

        // Counts that still add up to the total, but the deflated bytes of
        // each hold another number of transfers.
        first[0]++;
        second[0]--;

        Assert.Null(TransferSetCodec.ReadPacked(TransferSetCodec.Index(set), packed, 4, Two));
    }

    [Fact]
    public void APackedPatchOutOfRangeIsRefused()
    {
        TransferSet set = Sample();
        byte[] chunk = [.. TransferSetCodec.Chunks(set, 1 << 20).Single()];
        chunk[0] = 9;

        Assert.Null(TransferSetCodec.ReadPacked(TransferSetCodec.Index(set), [TransferSetCodec.Pack(chunk)], 4, Two));
    }

    [Theory]
    [InlineData(new byte[] { 1, 2 })]
    [InlineData(new byte[] { 0xff, 0xff, 0xff, 0xff })]
    [InlineData(new byte[] { 10, 0, 0, 0, 0xde, 0xad, 0xbe, 0xef })]
    public void JunkPackedChunksAreRefused(byte[] junk)
    {
        TransferSet set = Sample();
        Assert.Null(TransferSetCodec.ReadPacked(TransferSetCodec.Index(set), [junk], 4, Two));
    }

    [Fact]
    public void JunkDoesNotUnpack()
    {
        Assert.Null(TransferSetCodec.Unpack([1, 0, 0, 0, 0xde, 0xad, 0xbe, 0xef]));
        Assert.Null(TransferSetCodec.Unpack([1, 2]));
    }
}
