//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.Intrinsics;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// <c>TestLine</c> segments through the <see cref="IRayTracer"/> seam: the
/// ray <see cref="Ray.Segment"/> builds, the <see cref="RayTraceOptions"/>
/// members that carry a skipped id, sky pass-through and isolated rays, and
/// <see cref="KdRayTracer"/> answering them with the bits its own
/// <c>TestLines</c> gives.
/// </summary>
public sealed class TestLineSeamTests
{
    private const int SkyId = TraceId.Sky;
    private const int PropId = TraceId.StaticProp | 3;

    // A floor, a wall, a sky ceiling and a prop, so segments are blocked by
    // each kind and some pass between them.
    private static readonly KdRayTracer Tracer = KdRayTracer.Build(
    [
        new TracedTriangle(TraceId.Opaque, new Vec3(-200, -200, 0), new Vec3(200, -200, 0), new Vec3(0, 200, 0), 0),
        new TracedTriangle(TraceId.Opaque, new Vec3(50, -100, -50), new Vec3(50, 100, -50), new Vec3(50, 0, 150), 0),
        new TracedTriangle(SkyId, new Vec3(-300, -300, 120), new Vec3(300, -300, 120), new Vec3(0, 300, 120), 0),
        new TracedTriangle(PropId, new Vec3(-60, -20, 20), new Vec3(-60, 20, 20), new Vec3(-60, 0, 80), 0),
    ]);

    private static (Vec3[] Starts, Vec3[] Ends) Segments(int count, int seed)
    {
        Random random = new(seed);
        Vec3 Point() => new(
            (float)((random.NextDouble() * 400) - 200),
            (float)((random.NextDouble() * 400) - 200),
            (float)((random.NextDouble() * 300) - 100));
        Vec3[] starts = new Vec3[count];
        Vec3[] ends = new Vec3[count];
        for (int i = 0; i < count; i++)
        {
            starts[i] = Point();
            ends[i] = Point();
        }

        return (starts, ends);
    }

    private static bool[] Seam(IRayTracer tracer, Vec3[] starts, Vec3[] ends, bool stockReciprocal, RayTraceOptions options)
    {
        Ray[] rays = new Ray[starts.Length];
        for (int i = 0; i < rays.Length; i++)
        {
            rays[i] = Ray.Segment(starts[i], ends[i], stockReciprocal);
        }

        ulong[] bits = new ulong[(rays.Length + 63) / 64];
        ValueTask task = tracer.TraceVisibilityAsync(rays, bits, options);
        Assert.True(task.IsCompletedSuccessfully);
        return [.. Enumerable.Range(0, rays.Length).Select(i => (bits[i >> 6] & (1UL << (i & 63))) != 0)];
    }

    public static TheoryData<bool, bool, int> Modes() => new()
    {
        { false, false, -1 },
        { true, false, -1 },
        { true, true, -1 },
        { false, true, PropId },
        { true, false, PropId },
    };

    [Theory]
    [MemberData(nameof(Modes))]
    public void TheSeamAnswersEachSegmentAsTestLinesDoes(bool stockReciprocal, bool skyDoesNotBlock, int skipId)
    {
        (Vec3[] starts, Vec3[] ends) = Segments(4000, 11);
        bool[] direct = new bool[starts.Length];
        Tracer.TestLines(starts, ends, direct, stockReciprocal, skyDoesNotBlock, skipId);

        bool[] seam = Seam(Tracer, starts, ends, stockReciprocal, RayTraceOptions.TestLine(skipId, skyDoesNotBlock));

        Assert.Equal(direct, seam);
        Assert.Contains(true, seam);
        Assert.Contains(false, seam);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void AnIsolatedRayAnswersAsAPacketOfFourCopiesOfItself(bool stockReciprocal, bool skyDoesNotBlock, int skipId)
    {
        // The isolated path duplicates the ray into its own packet; the packed
        // path given four copies loads the same packet through the transposes,
        // and applies the sky and skip rules lane-wise. The two must agree.
        (Vec3[] starts, Vec3[] ends) = Segments(1000, 5);
        Vec3[] starts4 = [.. starts.SelectMany(s => Enumerable.Repeat(s, 4))];
        Vec3[] ends4 = [.. ends.SelectMany(e => Enumerable.Repeat(e, 4))];
        RayTraceOptions isolated = RayTraceOptions.TestLine(skipId, skyDoesNotBlock);

        bool[] alone = Seam(Tracer, starts, ends, stockReciprocal, isolated);
        bool[] packed = Seam(Tracer, starts4, ends4, stockReciprocal, isolated with { IsolatedRays = false });

        for (int i = 0; i < alone.Length; i++)
        {
            Assert.All(packed.Skip(i * 4).Take(4), b => Assert.Equal(alone[i], b));
        }
    }

    [Fact]
    public void ThePackedPathSkipsTheIdAndLetsTheSkyThrough()
    {
        // Straight up through the prop's plane and then the sky ceiling; and
        // straight through the prop sideways.
        Ray[] rays =
        [
            Ray.Segment(new Vec3(0, 0, 60), new Vec3(0, 0, 200), false),
            Ray.Segment(new Vec3(-40, 0, 40), new Vec3(-80, 0, 40), false),
            Ray.Segment(new Vec3(0, 0, 10), new Vec3(0, 0, -10), false),
        ];
        ulong[] plain = new ulong[1];
        ulong[] skipping = new ulong[1];
        Tracer.TraceVisibility(rays, plain, RayTraceOptions.StockExact);
        Tracer.TraceVisibility(rays, skipping, RayTraceOptions.StockExact with { SkipId = PropId, SkyDoesNotBlock = true });

        Assert.Equal(0b111UL, plain[0]);
        Assert.Equal(0b100UL, skipping[0]);
    }

    [Fact]
    public void ClosestHitHonoursTheSkippedIdAndIsolation()
    {
        Ray[] rays = [Ray.Segment(new Vec3(-40, 0, 40), new Vec3(-250, 0, 40), false)];
        HitId[] plain = new HitId[1];
        HitId[] skipping = new HitId[1];
        HitId[] packed = new HitId[1];
        Tracer.TraceClosest(rays, plain, RayTraceOptions.StockExact);
        Tracer.TraceClosest(rays, skipping, RayTraceOptions.TestLine(PropId));
        Tracer.TraceClosest(rays, packed, RayTraceOptions.StockExact with { SkipId = PropId });

        Assert.Equal(PropId, plain[0].Surface);
        Assert.Equal(HitId.Miss, skipping[0].Surface);
        Assert.Equal(skipping, packed);
    }

    [Fact]
    public void ClosestHitRefusesSkyPassThrough()
    {
        Ray[] rays = [Ray.Segment(Vec3.Zero, new Vec3(0, 0, 10), false)];
        Assert.Throws<ArgumentException>(() =>
            Tracer.TraceClosest(rays, new HitId[1], RayTraceOptions.TestLine(skyDoesNotBlock: true)));
    }

    [Fact]
    public void TheExactSegmentIsTheDirectionOverItsLength()
    {
        Ray r = Ray.Segment(new Vec3(1, 2, 3), new Vec3(4, 6, 3), false);

        Assert.Equal(new Ray(1, 2, 3, 3.0f * (1.0f / 5.0f), 4.0f * (1.0f / 5.0f), 0, 5), r);
    }

    [Fact]
    public void TheStockSegmentNormalisesWithTheReciprocalEstimate()
    {
        if (!FloatEstimate.IsSupported)
        {
            // No estimate instruction: the exact reciprocal, whatever was asked.
            Assert.Equal(Ray.Segment(Vec3.Zero, new Vec3(3, 0, 0), false), Ray.Segment(Vec3.Zero, new Vec3(3, 0, 0), true));
            return;
        }

        // ReciprocalSIMD: the estimate plus one Newton step, spelled out.
        int differ = 0;
        (Vec3[] starts, Vec3[] ends) = Segments(200, 3);
        for (int i = 0; i < starts.Length; i++)
        {
            Vec3 d = ends[i] - starts[i];
            float len = MathF.Sqrt((d.X * d.X) + (d.Y * d.Y) + (d.Z * d.Z));
            float est = FloatEstimate.Reciprocal(Vector128.Create(len)).ToScalar();
            float inv = (est + est) - (len * (est * est));
            Ray stock = Ray.Segment(starts[i], ends[i], true);

            Assert.Equal(new Ray(starts[i].X, starts[i].Y, starts[i].Z, d.X * inv, d.Y * inv, d.Z * inv, len), stock);
            differ += stock == Ray.Segment(starts[i], ends[i], false) ? 0 : 1;
        }

        Assert.True(differ > 0, "the estimate should move some direction off the exact one");
    }

    [Fact]
    public void AZeroLengthSegmentHasNoDirectionAndIsNotBlocked()
    {
        Ray r = Ray.Segment(new Vec3(0, 0, 40), new Vec3(0, 0, 40), false);

        Assert.Equal(0.0f, r.MaxDistance);
        Assert.True(float.IsNaN(r.DirectionX));
        Assert.Equal([false], Seam(Tracer, [new Vec3(0, 0, 40)], [new Vec3(0, 0, 40)], false, RayTraceOptions.TestLine()));
    }

    [Fact]
    public void TestLineOptionsAreIsolatedWithNoEpsilon()
    {
        RayTraceOptions plain = RayTraceOptions.TestLine();
        RayTraceOptions both = RayTraceOptions.TestLine(PropId, skyDoesNotBlock: true);

        Assert.Equal(new RayTraceOptions(0.0f) { IsolatedRays = true }, plain);
        Assert.True(plain.IsPlain);
        Assert.Equal(PropId, both.SkipId);
        Assert.True(both.SkyDoesNotBlock);
        Assert.False(both.IsPlain);
        Assert.False(RayTraceOptions.StockExact.IsolatedRays);
        Assert.True(RayTraceOptions.StockExact.IsPlain);
    }

    [Fact]
    public void AZeroSkipIdIsARealIdNotNone()
    {
        Assert.Equal(0, RayTraceOptions.TestLine(0).SkipId);
        Assert.Null(RayTraceOptions.TestLine(-5).SkipId);
        Assert.False(RayTraceOptions.TestLine(0).IsPlain);
    }

    [Fact]
    public void TheKdTracerSupportsEveryOption()
    {
        Assert.True(Tracer.Supports(RayTraceOptions.StockExact));
        Assert.True(Tracer.Supports(RayTraceOptions.TestLine(PropId, skyDoesNotBlock: true)));
    }

    [Fact]
    public void ATracerThatDoesNotSayOtherwiseAnswersOnlyThePlainQuery()
    {
        IRayTracer tracer = new PlainOnly();

        Assert.True(tracer.Supports(RayTraceOptions.StockExact));
        Assert.True(tracer.Supports(RayTraceOptions.TestLine()));
        Assert.False(tracer.Supports(RayTraceOptions.TestLine(PropId)));
        Assert.False(tracer.Supports(RayTraceOptions.TestLine(skyDoesNotBlock: true)));
    }

    [Fact]
    public void TheEmptySceneSupportsEveryOption()
    {
        IRayTracer tracer = new EmptySceneTracer();

        Assert.True(tracer.Supports(RayTraceOptions.TestLine(PropId, skyDoesNotBlock: true)));
    }

    // Written against the interface as it was before Supports existed.
    private sealed class PlainOnly : IRayTracer
    {
        public string TracerIdentity => "plain";

        public ValueTask TraceVisibilityAsync(
            ReadOnlyMemory<Ray> rays, Memory<ulong> hitBits, RayTraceOptions options, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask TraceClosestAsync(
            ReadOnlyMemory<Ray> rays, Memory<HitId> hits, RayTraceOptions options, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
