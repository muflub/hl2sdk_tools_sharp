//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// <c>KdRayTracer.TestLines</c> with one start for every segment must answer
/// exactly as the per-segment overload does with that start repeated.
/// </summary>
public sealed class TestLinesOneStartTests
{
    private const int SkyId = 0x01000000;
    private const int PropId = 0x04000000 | 3;

    // A floor, a wall, a sky ceiling and a prop, so segments are blocked by
    // each kind and some pass between them.
    private static readonly KdRayTracer Tracer = KdRayTracer.Build(
    [
        new TracedTriangle(1, new Vec3(-200, -200, 0), new Vec3(200, -200, 0), new Vec3(0, 200, 0), 0),
        new TracedTriangle(2, new Vec3(50, -100, -50), new Vec3(50, 100, -50), new Vec3(50, 0, 150), 0),
        new TracedTriangle(SkyId, new Vec3(-300, -300, 120), new Vec3(300, -300, 120), new Vec3(0, 300, 120), 0),
        new TracedTriangle(PropId, new Vec3(-60, -20, 20), new Vec3(-60, 20, 20), new Vec3(-60, 0, 80), 0),
    ]);

    private static Vec3[] Ends(int count, int seed)
    {
        Random random = new(seed);
        Vec3[] ends = new Vec3[count];
        for (int i = 0; i < count; i++)
        {
            ends[i] = new Vec3(
                (float)((random.NextDouble() * 400) - 200),
                (float)((random.NextDouble() * 400) - 200),
                (float)((random.NextDouble() * 300) - 100));
        }

        return ends;
    }

    public static TheoryData<bool, bool, int> Modes() => new()
    {
        { false, false, -1 },
        { true, false, -1 },
        { true, true, -1 },
        { false, true, PropId },
    };

    [Theory]
    [MemberData(nameof(Modes))]
    public void OneStartAnswersExactlyAsTheStartRepeated(bool stockReciprocal, bool skyDoesNotBlock, int skipId)
    {
        Vec3 start = new(0, 0, 40);
        Vec3[] ends = Ends(3000, 7);
        Vec3[] starts = new Vec3[ends.Length];
        Array.Fill(starts, start);

        bool[] expected = new bool[ends.Length];
        bool[] actual = new bool[ends.Length];
        Tracer.TestLines(starts, ends, expected, stockReciprocal, skyDoesNotBlock, skipId);
        Tracer.TestLines(start, ends, actual, stockReciprocal, skyDoesNotBlock, skipId);

        Assert.Equal(expected, actual);
        Assert.Contains(true, actual);
        Assert.Contains(false, actual);
    }

    [Fact]
    public void NoEndsIsNoWork()
    {
        Tracer.TestLines(Vec3.Zero, [], [], stockReciprocal: true);
    }

    [Fact]
    public void AShortResultSpanIsRefused()
    {
        Assert.Throws<ArgumentException>(() => Tracer.TestLines(Vec3.Zero, [new Vec3(1, 1, 1), new Vec3(2, 2, 2)], new bool[1], true));
    }

    [Fact]
    public void ALongerResultSpanKeepsItsTail()
    {
        bool[] blocked = [false, false, true];
        Tracer.TestLines(new Vec3(0, 0, 40), [new Vec3(0, 0, -40), new Vec3(0, 0, 60)], blocked, true);
        Assert.Equal([true, false, true], blocked);
    }
}
