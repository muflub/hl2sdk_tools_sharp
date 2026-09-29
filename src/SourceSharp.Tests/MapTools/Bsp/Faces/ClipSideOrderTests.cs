//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Bsp.Faces;
using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Faces;

/// <summary>
/// The side order <see cref="DetailFaces.ClipFaceToBrush"/> clips in, written into a span, must
/// be the order the reference's list gave: each axial side inserted at the head, every other
/// side appended, bevels skipped. And the span it is written into must be long enough at, just
/// past and far below the stack buffer's size.
/// </summary>
public class ClipSideOrderTests
{
    [Fact]
    public void RandomBrushesOrderTheirSidesAsTheHeadInsertingListDid()
    {
        PlaneTable planes = new();
        int[] pool = Planes(planes);
        var random = new Random(20260929);
        Span<int> order = stackalloc int[DetailFaces.StackClipSides];
        for (int trial = 0; trial < 500; trial++)
        {
            BspBrushSide[] sides = RandomSides(random, pool, random.Next(0, 40));
            int[] expected = Reference(sides, planes);

            order.Fill(-1);
            int count = DetailFaces.OrderClipSides(sides, planes, order);

            Assert.Equal(expected, order[..count].ToArray());
        }
    }

    [Fact]
    public void NoSidesAndOnlyBevelsGiveAnEmptyOrder()
    {
        PlaneTable planes = new();
        int[] pool = Planes(planes);
        Span<int> order = stackalloc int[4];

        Assert.Equal(0, DetailFaces.OrderClipSides([], planes, order));

        BspBrushSide[] bevels =
        [
            new BspBrushSide { PlaneNumber = pool[0], Bevel = true },
            new BspBrushSide { PlaneNumber = pool[^1], Bevel = true },
        ];
        Assert.Equal(0, DetailFaces.OrderClipSides(bevels, planes, order));
    }

    [Fact]
    public void AxialSidesComeFirstLastToFirstThenTheRestInOrder()
    {
        PlaneTable planes = new();
        int ax = planes.Find(new Vec3(1, 0, 0), 16);
        int ay = planes.Find(new Vec3(0, 1, 0), 16);
        int az = planes.Find(new Vec3(0, 0, -1), 16);
        int slant = planes.Find(new Vec3(1, 1, 0).Normalise().Normalised, 8);
        int slant2 = planes.Find(new Vec3(0, 1, 3).Normalise().Normalised, 8);
        BspBrushSide[] sides =
        [
            new() { PlaneNumber = slant },
            new() { PlaneNumber = ax },
            new() { PlaneNumber = slant2 },
            new() { PlaneNumber = ay, Bevel = true },
            new() { PlaneNumber = az },
        ];

        Span<int> order = stackalloc int[sides.Length];
        int count = DetailFaces.OrderClipSides(sides, planes, order);

        Assert.Equal([4, 1, 0, 2], order[..count].ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(DetailFaces.StackClipSides - 1)]
    [InlineData(DetailFaces.StackClipSides)]
    [InlineData(DetailFaces.StackClipSides + 1)]
    [InlineData(1000)]
    public void TheOrderBufferHoldsEverySideAtAndPastTheStackBound(int sideCount)
    {
        Span<int> stack = stackalloc int[DetailFaces.StackClipSides];
        stack.Fill(-7);
        Span<int> buffer = DetailFaces.ClipOrderBuffer(sideCount, stack);

        Assert.True(buffer.Length >= sideCount);

        // at or under the bound the stack buffer is used; past it a fresh array of exactly the
        // side count, so a big brush costs one exact allocation and never overruns the stack
        bool onStack = sideCount <= DetailFaces.StackClipSides;
        Assert.Equal(onStack ? DetailFaces.StackClipSides : sideCount, buffer.Length);
        Assert.Equal(onStack, buffer.Overlaps(stack));

        // a brush of that many sides, all axial or all slanted, fills the buffer to its count
        PlaneTable planes = new();
        int[] pool = Planes(planes);
        BspBrushSide[] sides = new BspBrushSide[sideCount];
        for (int i = 0; i < sideCount; i++)
        {
            sides[i] = new BspBrushSide { PlaneNumber = pool[i % pool.Length] };
        }

        Assert.Equal(Reference(sides, planes), buffer[..DetailFaces.OrderClipSides(sides, planes, buffer)].ToArray());
    }

    /// <summary>The reference's list: axial sides inserted at the head, the rest appended.</summary>
    private static int[] Reference(BspBrushSide[] sides, PlaneTable planes)
    {
        List<int> sorted = [];
        for (int i = 0; i < sides.Length; i++)
        {
            if (sides[i].Bevel)
            {
                continue;
            }

            if (planes[sides[i].PlaneNumber].Type <= PlaneType.Z)
            {
                sorted.Insert(0, i);
            }
            else
            {
                sorted.Add(i);
            }
        }

        return [.. sorted];
    }

    /// <summary>A mix of axial and slanted planes, both sides of each.</summary>
    private static int[] Planes(PlaneTable planes)
    {
        List<int> numbers = [];
        Vec3[] normals =
        [
            new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), new(-1, 0, 0), new(0, -1, 0), new(0, 0, -1),
            new Vec3(1, 1, 0).Normalise().Normalised, new Vec3(1, 0, 2).Normalise().Normalised,
            new Vec3(-3, 1, 1).Normalise().Normalised, new Vec3(0, -1, 5).Normalise().Normalised,
        ];
        for (int n = 0; n < normals.Length; n++)
        {
            numbers.Add(planes.Find(normals[n], 32 + n));
        }

        return [.. numbers];
    }

    private static BspBrushSide[] RandomSides(Random random, int[] pool, int count)
    {
        var sides = new BspBrushSide[count];
        for (int i = 0; i < count; i++)
        {
            sides[i] = new BspBrushSide { PlaneNumber = pool[random.Next(pool.Length)], Bevel = random.Next(5) == 0 };
        }

        return sides;
    }
}
