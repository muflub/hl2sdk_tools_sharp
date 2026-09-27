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
    internal static TransferSet Sample()
    {
        int[] counts = [3, 0, 5, 2];
        long[] offsets = [0, 3, 3, 8];
        Transfer[] arena =
        [
            new(1, 0.5f), new(2, 0.25f), new(3, 1e-7f),
            new(0, 0.1f), new(3, 0.2f), new(1, 0.3f), new(2, float.Epsilon), new(0, 0.9f),
            new(0, 0.75f), new(2, 0.125f),
        ];
        return new TransferSet(arena, offsets, counts, 5);
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
        TransferSet empty = new([], [0, 0], [0, 0], 0);
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

    [Fact]
    public void JunkDoesNotUnpack()
    {
        Assert.Null(TransferSetCodec.Unpack([1, 0, 0, 0, 0xde, 0xad, 0xbe, 0xef]));
        Assert.Null(TransferSetCodec.Unpack([1, 2]));
    }
}
