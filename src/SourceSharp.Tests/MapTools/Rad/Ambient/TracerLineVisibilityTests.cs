//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Ambient;

/// <summary>
/// Leaf ambient's line visibility: a fraction of 0 or 1 per sample, for
/// batches that fit on the stack and batches that borrow pooled storage.
/// </summary>
public sealed class TracerLineVisibilityTests
{
    private static readonly KdRayTracer Tracer = KdRayTracer.Build(
    [
        new TracedTriangle(1, new Vec3(-200, -200, 0), new Vec3(200, -200, 0), new Vec3(0, 200, 0), 0),
        new TracedTriangle(2, new Vec3(50, -100, -50), new Vec3(50, 100, -50), new Vec3(50, 0, 150), 0),
    ]);

    private static readonly Vec3 Start = new(0, 0, 40);

    private static Vec3[] Ends(int count)
    {
        Random random = new(count);
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

    // What the fractions must be: each segment on its own, through the
    // per-segment overload.
    private static float[] Expected(Vec3[] ends, bool stockReciprocal)
    {
        float[] fractions = new float[ends.Length];
        for (int i = 0; i < ends.Length; i++)
        {
            Span<bool> blocked = [false];
            Tracer.TestLines([Start], [ends[i]], blocked, stockReciprocal);
            fractions[i] = blocked[0] ? 0.0f : 1.0f;
        }

        return fractions;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(1024)]   // the largest batch on the stack
    [InlineData(1025)]   // the smallest that borrows
    [InlineData(5000)]
    public void EveryBatchSizeGivesEachSegmentsOwnAnswer(int count)
    {
        foreach ((ComplianceOptions compliance, bool stock) in new[] { (ComplianceOptions.Correct, false), (ComplianceOptions.Stock, true) })
        {
            Vec3[] ends = Ends(count);
            float[] fractions = new float[count];
            new TracerLineVisibility(Tracer, compliance).FractionsVisible(Start, ends, fractions);
            Assert.Equal(Expected(ends, stock), fractions);
        }
    }

    [Fact]
    public void ABorrowedBatchIsNotSeenByTheNext()
    {
        // The pooled flags come back dirty; a later, smaller batch must still
        // get only its own answers.
        TracerLineVisibility visibility = new(Tracer, ComplianceOptions.Correct);
        Vec3[] big = Ends(4000);
        visibility.FractionsVisible(Start, big, new float[big.Length]);

        Vec3[] next = Ends(2000);
        float[] fractions = new float[next.Length];
        visibility.FractionsVisible(Start, next, fractions);
        Assert.Equal(Expected(next, stockReciprocal: false), fractions);
    }

    [Fact]
    public void ALargeBatchAllocatesNothingOnceWarm()
    {
        TracerLineVisibility visibility = new(Tracer, ComplianceOptions.Correct);
        Vec3[] ends = Ends(5000);
        float[] fractions = new float[ends.Length];
        visibility.FractionsVisible(Start, ends, fractions);

        long before = GC.GetAllocatedBytesForCurrentThread();
        visibility.FractionsVisible(Start, ends, fractions);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // A copy of the start per segment was 60 KB here, the flags 5 KB.
        Assert.True(allocated < 1024, $"{allocated} bytes allocated");
    }

    [Fact]
    public void TooFewFractionSlotsIsRefused()
    {
        TracerLineVisibility visibility = new(Tracer, ComplianceOptions.Correct);
        Assert.Throws<ArgumentException>(() => visibility.FractionsVisible(Start, Ends(3), new float[2]));
    }
}
