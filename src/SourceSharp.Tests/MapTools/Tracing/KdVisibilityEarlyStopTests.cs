//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// A visibility trace stops once every lane it answers for is blocked; its
/// bits must be the full walk's, on every path that stops early and on every
/// path that must not.
/// </summary>
/// <remarks>
/// The reference is the same tree with the early stop switched off
/// (<c>KdRayTracer.WithFullVisibilityWalk</c>), which is the walk stock's
/// <c>Trace4Rays</c> makes. The scene is the stock parity scene: 2,000
/// triangles in general position and 8,000 segments through them, about
/// half of them blocked.
/// </remarks>
public sealed class KdVisibilityEarlyStopTests : IClassFixture<KdParityFixture>
{
    private const int SkyId = 0x01000000;

    private readonly KdParityFixture _fixture;

    public KdVisibilityEarlyStopTests(KdParityFixture fixture) => _fixture = fixture;

    public static TheoryData<bool, bool> Compliance() => new()
    {
        { false, false },
        { true, false },
        { false, true },
        { true, true },
    };

    private KdRayTracer Tracer(bool stock) =>
        KdRayTracer.Build(_fixture.Scene.Triangles, stock ? ComplianceOptions.Stock : ComplianceOptions.Correct);

    /// <summary>
    /// The scene's rays in a shuffled order, so packets mix directions and
    /// take the split passes as well as the single one.
    /// </summary>
    private Ray[] Shuffled(int seed)
    {
        Ray[] rays = [.. _fixture.Scene.Rays];
        new Random(seed).Shuffle(rays);
        return rays;
    }

    private static ulong[] Visibility(KdRayTracer tracer, Ray[] rays, RayTraceOptions options)
    {
        ulong[] bits = new ulong[(rays.Length + 63) / 64];
        tracer.TraceVisibility(rays, bits, options);
        return bits;
    }

    private static int Blocked(ulong[] bits) => bits.Sum(b => System.Numerics.BitOperations.PopCount(b));

    /// <summary>
    /// Packets of four, in the scene's own order (coherent packets) and
    /// shuffled (mixed-sign packets that split), with and without a
    /// <c>skip_id</c>: the same bits as the full walk.
    /// </summary>
    [Theory]
    [MemberData(nameof(Compliance))]
    public void PacketBitsAreTheFullWalks(bool stock, bool shuffled)
    {
        KdRayTracer tracer = Tracer(stock);
        KdRayTracer full = tracer.WithFullVisibilityWalk();
        Ray[] rays = shuffled ? Shuffled(11) : _fixture.Scene.Rays;
        int skipId = _fixture.Scene.Triangles[17].Id;

        foreach (RayTraceOptions options in new[]
        {
            RayTraceOptions.StockExact,
            RayTraceOptions.SelfIntersectionSafe,
            RayTraceOptions.StockExact with { SkipId = skipId },
        })
        {
            ulong[] expected = Visibility(full, rays, options);
            ulong[] actual = Visibility(tracer, rays, options);
            Assert.Equal(expected, actual);

            // Both answers occur, so both the stop and the walk to the end ran.
            Assert.InRange(Blocked(actual), 1, rays.Length - 1);
        }
    }

    /// <summary>
    /// A batch whose length is not a multiple of four: the last packet is
    /// padded with copies of the final ray, which must not change its bit.
    /// </summary>
    [Fact]
    public void AShortLastPacketKeepsItsBits()
    {
        KdRayTracer tracer = Tracer(stock: false);
        KdRayTracer full = tracer.WithFullVisibilityWalk();
        Ray[] rays = Shuffled(5)[..4001];
        Assert.Equal(
            Visibility(full, rays, RayTraceOptions.StockExact),
            Visibility(tracer, rays, RayTraceOptions.StockExact));
    }

    /// <summary>Isolated rays (<c>TestLine</c>), one packet each: the same bits.</summary>
    [Theory]
    [MemberData(nameof(Compliance))]
    public void IsolatedBitsAreTheFullWalks(bool stock, bool shuffled)
    {
        KdRayTracer tracer = Tracer(stock);
        KdRayTracer full = tracer.WithFullVisibilityWalk();
        Ray[] rays = shuffled ? Shuffled(23) : _fixture.Scene.Rays;
        RayTraceOptions options = RayTraceOptions.TestLine();

        ulong[] actual = Visibility(tracer, rays, options);
        Assert.Equal(Visibility(full, rays, options), actual);
        Assert.InRange(Blocked(actual), 1, rays.Length - 1);
    }

    /// <summary>
    /// <c>TestLines</c>, both overloads, which reach the same isolated walk
    /// without going through <see cref="RayTraceOptions"/>.
    /// </summary>
    [Fact]
    public void TestLinesAreTheFullWalks()
    {
        KdRayTracer tracer = Tracer(stock: false);
        KdRayTracer full = tracer.WithFullVisibilityWalk();
        Random random = new(3);
        Vec3 min = tracer.MinBound;
        Vec3 max = tracer.MaxBound;
        Vec3 Point() => new(
            min.X + ((max.X - min.X) * random.NextSingle()),
            min.Y + ((max.Y - min.Y) * random.NextSingle()),
            min.Z + ((max.Z - min.Z) * random.NextSingle()));

        Vec3[] starts = new Vec3[3000];
        Vec3[] ends = new Vec3[starts.Length];
        for (int i = 0; i < starts.Length; i++)
        {
            starts[i] = Point();
            ends[i] = Point();
        }

        bool[] expected = new bool[starts.Length];
        bool[] actual = new bool[starts.Length];
        full.TestLines(starts, ends, expected, stockReciprocal: false);
        tracer.TestLines(starts, ends, actual, stockReciprocal: false);
        Assert.Equal(expected, actual);
        Assert.Contains(true, actual);
        Assert.Contains(false, actual);

        full.TestLines(starts[0], ends, expected, stockReciprocal: false);
        tracer.TestLines(starts[0], ends, actual, stockReciprocal: false);
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The stop really does cut walks short: some isolated walks end on a hit
    /// that is not the nearest one, and every one of those is still blocked
    /// short of its reach -- which is all the bit says.
    /// </summary>
    [Fact]
    public void SomeWalksStopBeforeTheNearestHitAndAreStillBlocked()
    {
        KdRayTracer tracer = Tracer(stock: false);
        KdRayTracer full = tracer.WithFullVisibilityWalk();
        RayTraceOptions options = RayTraceOptions.TestLine();

        int stoppedShort = 0;
        foreach (Ray ray in _fixture.Scene.Rays)
        {
            (float nearest, int nearestTriangle) = full.IsolatedVisibilityWalk(in ray, options);
            (float stopped, int stoppedTriangle) = tracer.IsolatedVisibilityWalk(in ray, options);

            bool blocked = nearestTriangle != -1 && nearest < ray.MaxDistance;
            Assert.Equal(blocked, stoppedTriangle != -1 && stopped < ray.MaxDistance);
            if (!blocked)
            {
                // A walk that never blocks is never shortened.
                Assert.Equal((nearest, nearestTriangle), (stopped, stoppedTriangle));
            }
            else if (stoppedTriangle != nearestTriangle)
            {
                Assert.True(stopped > nearest, $"stopped at {stopped}, nearest {nearest}");
                stoppedShort++;
            }
        }

        Assert.True(stoppedShort > 0, "no walk stopped before its nearest hit");
    }

    /// <summary>
    /// With sky pass-through the stop is off: a sky triangle nearer than the
    /// blocker, further along the walk, would unblock the ray. Every walk is
    /// then the full one, distance and triangle alike.
    /// </summary>
    [Fact]
    public void SkyPassThroughWalksToTheNearestHit()
    {
        TracedTriangle[] triangles = [.. _fixture.Scene.Triangles];
        for (int i = 0; i < triangles.Length; i += 3)
        {
            triangles[i] = triangles[i] with { Id = SkyId | i };
        }

        KdRayTracer tracer = KdRayTracer.Build(triangles, ComplianceOptions.Correct);
        KdRayTracer full = tracer.WithFullVisibilityWalk();
        RayTraceOptions options = RayTraceOptions.TestLine(skyDoesNotBlock: true);

        foreach (Ray ray in _fixture.Scene.Rays)
        {
            Assert.Equal(full.IsolatedVisibilityWalk(in ray, options), tracer.IsolatedVisibilityWalk(in ray, options));
        }

        Ray[] shuffled = Shuffled(29);
        Assert.Equal(
            Visibility(full, shuffled, RayTraceOptions.StockExact with { SkyDoesNotBlock = true }),
            Visibility(tracer, shuffled, RayTraceOptions.StockExact with { SkyDoesNotBlock = true }));
        Assert.Equal(Visibility(full, shuffled, options), Visibility(tracer, shuffled, options));
    }

    /// <summary>
    /// The closest-hit trace never stops early: its callers read the id and
    /// the fraction, so it must be the nearest hit whatever the stop does.
    /// </summary>
    [Fact]
    public void ClosestHitIsUnchanged()
    {
        KdRayTracer tracer = Tracer(stock: false);
        Ray[] rays = Shuffled(31);
        HitId[] expected = new HitId[rays.Length];
        HitId[] actual = new HitId[rays.Length];
        tracer.WithFullVisibilityWalk().TraceClosest(rays, expected, RayTraceOptions.StockExact);
        tracer.TraceClosest(rays, actual, RayTraceOptions.StockExact);
        Assert.Equal(expected, actual);
    }
}
