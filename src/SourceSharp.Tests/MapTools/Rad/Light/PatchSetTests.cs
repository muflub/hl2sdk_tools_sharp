//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;

using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Rad.Light;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Light;

/// <summary>
/// The patch set's segmented storage: indices, growth, and what it allocates.
/// </summary>
public sealed class PatchSetTests
{
    private static Patch Marked(int i) => new() { FaceNumber = i, Area = i * 0.5f, Parent = -1 };

    private static PatchSet Filled(int faceCount, int count)
    {
        PatchSet set = new(faceCount, 1, new WindingArena());
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(i, set.Add(Marked(i)));
        }

        return set;
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(40, 1000)]
    [InlineData(10, PatchSet.SegmentLength)]
    [InlineData(10, PatchSet.SegmentLength + 1)]
    [InlineData(3000, (5 * PatchSet.SegmentLength) + 17)]
    public void EveryPatchIsReadBackAtItsIndexAcrossSegments(int faceCount, int count)
    {
        PatchSet set = Filled(faceCount, count);

        Assert.Equal(count, set.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(i, set.At(i).FaceNumber);
            Assert.Equal(i * 0.5f, set.At(i).Area);
        }

        Patch[] all = set.ToArray();
        Assert.Equal(count, all.Length);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(i, all[i].FaceNumber);
        }
    }

    [Fact]
    public void AnEmptySetCopiesToAnEmptyArray() => Assert.Empty(Filled(5, 0).ToArray());

    [Fact]
    public void AtRejectsAnIndexPastTheCount()
    {
        PatchSet set = Filled(5, PatchSet.SegmentLength + 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => set.At(PatchSet.SegmentLength + 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => set.At(-1));
    }

    [Fact]
    public void WritesThroughAtLandInTheRightSegment()
    {
        PatchSet set = Filled(5, (2 * PatchSet.SegmentLength) + 5);
        set.At(PatchSet.SegmentLength).Chop = 7f;
        set.At((2 * PatchSet.SegmentLength) + 4).Chop = 9f;

        Assert.Equal(7f, set.ToArray()[PatchSet.SegmentLength].Chop);
        Assert.Equal(9f, set.At((2 * PatchSet.SegmentLength) + 4).Chop);
        Assert.Equal(0f, set.At(PatchSet.SegmentLength - 1).Chop);
    }

    [Fact]
    public void TheFirstSegmentStartsFromTheFaceCountAndDoublesToAFullSegment()
    {
        // A small map keeps a small first segment; it only reaches a full
        // segment when it has to.
        PatchSet small = Filled(40, 10);
        Assert.Equal(80, small.Capacity);

        PatchSet grown = Filled(40, 81);
        Assert.Equal(160, grown.Capacity);

        PatchSet full = Filled(40, PatchSet.SegmentLength);
        Assert.Equal(PatchSet.SegmentLength, full.Capacity);
    }

    [Fact]
    public void LaterSegmentsAreWholeAndTheCapacityStaysWithinOneSegmentOfTheCount()
    {
        int count = (3 * PatchSet.SegmentLength) + 1;
        PatchSet set = Filled(10, count);
        Assert.Equal(4 * PatchSet.SegmentLength, set.Capacity);
        Assert.InRange(set.Capacity - count, 0, PatchSet.SegmentLength);
    }

    [Fact]
    public void AReferenceIntoAFullSegmentSurvivesLaterAdds()
    {
        // Full segments are never copied, so a ref into one stays live. (The
        // documented contract is still "do not hold a ref across Add", which
        // the first segment's doubling needs; this pins that the later
        // segments do not move at all.)
        PatchSet set = Filled(10, PatchSet.SegmentLength + 1);
        ref Patch held = ref set.At(PatchSet.SegmentLength);
        for (int i = 0; i < 3 * PatchSet.SegmentLength; i++)
        {
            set.Add(Marked(i));
        }

        held.Chop = 3f;
        Assert.Equal(3f, set.At(PatchSet.SegmentLength).Chop);
    }

    [Fact]
    public void GrowingAllocatesAboutTheKeptStorageNotADoublingChain()
    {
        // Eight segments' worth of patches. Doubling one array from the face
        // count would allocate every array it outgrew as well -- about twice
        // the final array -- and could leave the kept one up to half empty.
        int count = 8 * PatchSet.SegmentLength;
        long patchBytes = Unsafe.SizeOf<Patch>();
        PatchSet set = new(64, 1, new WindingArena());

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < count; i++)
        {
            set.Add(Marked(i));
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // The eight segments, the first one's doublings from 128 (under one
        // more segment), and a few directory arrays.
        Assert.InRange(allocated, count * patchBytes, (count + PatchSet.SegmentLength) * patchBytes + 4096);
    }
}
