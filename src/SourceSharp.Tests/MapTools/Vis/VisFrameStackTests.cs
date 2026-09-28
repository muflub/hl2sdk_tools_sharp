//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Vis;

/// <summary>
/// The might-see extent: the flow intersects only the words that can hold a
/// set bit, and the frame slab keeps every other word zero so the vector is
/// still the whole intersection.
/// </summary>
public class VisFrameStackTests
{
    private const int Block = BitVector.WordsPerBlock;

    [Fact]
    public void AnEmptyVectorHasAnEmptyExtent()
    {
        Assert.Equal((0, 0), VisFrameStack.Extent(new ulong[4 * Block]));
        Assert.Equal((0, 0), VisFrameStack.Extent([]));
    }

    [Fact]
    public void TheExtentIsTheBlocksHoldingTheFirstAndLastSetWords()
    {
        ulong[] bits = new ulong[6 * Block];
        bits[Block + 3] = 1;
        bits[(3 * Block) + 1] = 1UL << 63;

        Assert.Equal((Block, 4 * Block), VisFrameStack.Extent(bits));
    }

    [Fact]
    public void ASetBitInTheFirstOrLastWordReachesTheEnd()
    {
        ulong[] bits = new ulong[3 * Block];
        bits[0] = 1;
        bits[^1] = 1;

        Assert.Equal((0, 3 * Block), VisFrameStack.Extent(bits));
    }

    [Fact]
    public void ANarrowerSearchOnlyLooksInside()
    {
        // The flow passes its own extent so the search never walks words it
        // already knows are zero; the answer can only shrink.
        ulong[] bits = new ulong[6 * Block];
        bits[(2 * Block) + 5] = 1;

        Assert.Equal((2 * Block, 3 * Block), VisFrameStack.Extent(bits, (Block, 5 * Block)));
        Assert.Equal((0, 0), VisFrameStack.Extent(bits, (3 * Block, 5 * Block)));
    }

    [Fact]
    public void AFrameBufferIsZeroOutsideTheExtentItIsHanded()
    {
        VisFrameStack stack = new(6 * Block * 64);

        // An earlier frame at this depth wrote the whole vector.
        ulong[] first = stack.MightSee(3, 0, 6 * Block);
        Array.Fill(first, ulong.MaxValue);

        // A later frame with a narrower extent must find zeros outside it,
        // and whatever it has not written inside it untouched.
        ulong[] second = stack.MightSee(3, 2 * Block, 4 * Block);
        Assert.Same(first, second);
        Assert.All(second[..(2 * Block)], w => Assert.Equal(0UL, w));
        Assert.All(second[(4 * Block)..], w => Assert.Equal(0UL, w));
        Assert.All(second[(2 * Block)..(4 * Block)], w => Assert.Equal(ulong.MaxValue, w));
    }

    [Fact]
    public void AnExtentThatMovesClearsOnlyWhatTheLastOneLeft()
    {
        VisFrameStack stack = new(8 * Block * 64);
        ulong[] buffer = stack.MightSee(1, Block, 3 * Block);
        buffer.AsSpan(Block, 2 * Block).Fill(7);

        // Disjoint and above: the old range is cleared entirely.
        stack.MightSee(1, 5 * Block, 6 * Block);
        Assert.All(buffer, w => Assert.Equal(0UL, w));

        // Overlapping: only the part of the old range outside the new one is
        // cleared, and what the two share is left for the frame to overwrite.
        stack.MightSee(1, 2 * Block, 5 * Block);
        buffer.AsSpan(2 * Block, 3 * Block).Fill(9);
        stack.MightSee(1, Block, 3 * Block);
        Assert.All(buffer.AsSpan(2 * Block, Block).ToArray(), w => Assert.Equal(9UL, w));
        Assert.All(buffer.AsSpan(3 * Block).ToArray(), w => Assert.Equal(0UL, w));
        Assert.All(buffer.AsSpan(0, 2 * Block).ToArray(), w => Assert.Equal(0UL, w));
    }

    [Fact]
    public void AnEmptyExtentClearsEverythingTheLastFrameWrote()
    {
        VisFrameStack stack = new(4 * Block * 64);
        ulong[] buffer = stack.MightSee(2, 0, 4 * Block);
        Array.Fill(buffer, 5UL);

        stack.MightSee(2, 0, 0);
        Assert.All(buffer, w => Assert.Equal(0UL, w));

        // And nothing is left marked: a later narrow frame clears nothing it
        // does not need to.
        buffer[0] = 3;
        stack.MightSee(2, 0, Block);
        Assert.Equal(3UL, buffer[0]);
    }

    [Fact]
    public void ADepthReachedForTheFirstTimeIsAllZeros()
    {
        VisFrameStack stack = new(2 * Block * 64);
        Assert.All(stack.MightSee(5, Block, 2 * Block), w => Assert.Equal(0UL, w));
    }
}
