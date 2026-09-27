//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Collections;

using Xunit;

namespace SourceSharp.Tests.MapTools.Collections;

public sealed class PooledArrayBuilderTests
{
    [Fact]
    public void AnEmptyBuilderGivesTheSharedEmptyArray()
    {
        using PooledArrayBuilder<int> builder = new();
        Assert.Equal(0, builder.Count);
        Assert.True(builder.AsSpan().IsEmpty);
        Assert.Same(Array.Empty<int>(), builder.ToArray());
    }

    [Fact]
    public void ANegativeCapacityIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PooledArrayBuilder<int>(-1));
    }

    [Fact]
    public void ItemsWithinTheInitialCapacityComeBackInOrderAndExactlySized()
    {
        using PooledArrayBuilder<int> builder = new(8);
        for (int i = 0; i < 5; i++)
        {
            builder.Add(i * 10);
        }

        int[] result = builder.ToArray();
        Assert.Equal([0, 10, 20, 30, 40], result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    public void GrowingPastTheCapacityKeepsEveryItemInOrder(int capacity)
    {
        using PooledArrayBuilder<int> builder = new(capacity);
        for (int i = 0; i < 1000; i++)
        {
            builder.Add(i);
        }

        Assert.Equal(1000, builder.Count);
        Assert.Equal(Enumerable.Range(0, 1000), builder.ToArray());
    }

    [Fact]
    public void TheSpanEditsTheItemsInPlace()
    {
        using PooledArrayBuilder<int> builder = new(4);
        builder.Add(1);
        builder.Add(2);
        foreach (ref int item in builder.AsSpan())
        {
            item *= 7;
        }

        Assert.Equal([7, 14], builder.ToArray());
    }

    [Fact]
    public void TheResultIsACopyThatOutlivesTheBuilder()
    {
        PooledArrayBuilder<string> builder = new(2);
        builder.Add("a");
        builder.Add("b");
        string[] result = builder.ToArray();
        builder.Dispose();

        Assert.Equal(["a", "b"], result);
    }

    [Fact]
    public void DisposingEmptiesTheBuilderAndIsIdempotent()
    {
        PooledArrayBuilder<int> builder = new(4);
        builder.Add(1);
        builder.Dispose();
        builder.Dispose();

        Assert.Equal(0, builder.Count);
        Assert.True(builder.AsSpan().IsEmpty);
        Assert.Empty(builder.ToArray());
    }

    [Fact]
    public void AddingAfterDisposeIsRefusedRatherThanRentingAgain()
    {
        PooledArrayBuilder<int> builder = new(4);
        builder.Dispose();
        Assert.Throws<ObjectDisposedException>(() => builder.Add(1));
    }

    [Fact]
    public void ABuilderThatNeverRentedDisposesCleanly()
    {
        PooledArrayBuilder<int> builder = new();
        builder.Dispose();
        Assert.Throws<ObjectDisposedException>(() => builder.Add(1));
    }
}
