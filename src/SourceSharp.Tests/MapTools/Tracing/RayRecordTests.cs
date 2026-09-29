//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;

using SourceSharp.MapTools.Gpu;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// The GPU wire records (<see cref="RayRecord"/>) on the CPU: every float
/// comes back as the bits it was packed from, in both records, over the
/// values a float can hold, and a record is only narrow when that is true
/// of the reach it leaves out.
/// </summary>
/// <remarks>
/// <see cref="RayRecord.Decode"/> is the host's mirror of the kernel's
/// <c>load_ray</c>; the kernel side of the same claim is
/// <c>VulkanRayTracerFacts.PackedRecordsGiveTheWordsOfWideRecords</c>, on a
/// real device. Every comparison here is of bits, never of float values:
/// NaN is unequal to itself and <c>0 == -0</c>, so a value comparison would
/// pass a codec that lost either.
/// </remarks>
public sealed class RayRecordTests
{
    /// <summary>Bit patterns every field is packed with: the edges a float has.</summary>
    public static readonly uint[] EdgeBits =
    [
        0x00000000u, // +0
        0x80000000u, // -0
        0x00000001u, // smallest denormal
        0x80000001u, // smallest negative denormal
        0x007FFFFFu, // largest denormal
        0x00800000u, // smallest normal
        0x3F800000u, // 1
        0x3F800001u, // just above 1
        0x3F7FFFFFu, // just below 1
        0x475DB3D7u, // 56755.84: stock's longest trace, sqrt(3) * 32768
        0x7F7FFFFFu, // largest finite
        0xFF7FFFFFu, // most negative finite
        0x7F800000u, // +inf
        0xFF800000u, // -inf
        0x7FC00000u, // quiet NaN
        0x7FC12345u, // quiet NaN with a payload
        0x7F800001u, // signalling NaN
        0xFFC00001u, // negative NaN with a payload
    ];

    /// <summary>The reaches a uniform-reach record may carry: every finite edge.</summary>
    public static TheoryData<uint> FiniteReaches()
    {
        TheoryData<uint> data = [];
        foreach (uint bits in EdgeBits.Where(b => (b & 0x7F800000u) != 0x7F800000u))
        {
            data.Add(bits);
        }

        return data;
    }

    /// <summary>The reaches that must never be sent in the push constants.</summary>
    public static TheoryData<uint> NonFiniteReaches()
    {
        TheoryData<uint> data = [];
        foreach (uint bits in EdgeBits.Where(b => (b & 0x7F800000u) == 0x7F800000u))
        {
            data.Add(bits);
        }

        return data;
    }

    private static float F(uint bits) => BitConverter.UInt32BitsToSingle(bits);

    private static uint[] BitsOf(Ray r) =>
    [
        BitConverter.SingleToUInt32Bits(r.OriginX), BitConverter.SingleToUInt32Bits(r.OriginY),
        BitConverter.SingleToUInt32Bits(r.OriginZ), BitConverter.SingleToUInt32Bits(r.DirectionX),
        BitConverter.SingleToUInt32Bits(r.DirectionY), BitConverter.SingleToUInt32Bits(r.DirectionZ),
        BitConverter.SingleToUInt32Bits(r.MaxDistance),
    ];

    /// <summary>
    /// Every edge value in every field: field k of ray i takes edge
    /// <c>(i + 5k) mod n</c>, so each field sees every edge and next to
    /// different neighbours, with the reach given separately.
    /// </summary>
    private static Ray[] EdgeRays(Func<int, uint> reach)
    {
        int n = EdgeBits.Length;
        Ray[] rays = new Ray[n * 2];
        for (int i = 0; i < rays.Length; i++)
        {
            uint E(int k) => EdgeBits[(i + (5 * k)) % n];
            rays[i] = new Ray(F(E(0)), F(E(1)), F(E(2)), F(E(3)), F(E(4)), F(E(5)), F(reach(i)));
        }

        return rays;
    }

    private static void AssertRoundTrip(RayRecord record, Ray[] rays)
    {
        uint[] words = new uint[rays.Length * record.Words];
        record.Pack(rays, words);
        for (int i = 0; i < rays.Length; i++)
        {
            Ray back = record.Decode(words.AsSpan(i * record.Words, record.Words));
            Assert.Equal(BitsOf(rays[i]), BitsOf(back));
        }
    }

    [Fact]
    public void TheWideRecordIsTheRayStructSevenWordsInOrder()
    {
        // The wide record is a block copy of the struct, so the struct's
        // layout is the wire format: seven floats, in declaration order.
        Assert.Equal(28, Unsafe.SizeOf<Ray>());
        Assert.Equal(RayRecord.MaxBytes, Unsafe.SizeOf<Ray>());
        Ray ray = new(F(1), F(2), F(3), F(4), F(5), F(6), F(7));
        uint[] words = new uint[7];

        RayRecord.Wide.Pack([ray], words);

        Assert.Equal([1u, 2u, 3u, 4u, 5u, 6u, 7u], words);
    }

    [Fact]
    public void TheRecordsAreTwentyEightAndTwentyFourBytes()
    {
        Assert.Equal(7, RayRecord.Wide.Words);
        Assert.Equal(28, RayRecord.Wide.Bytes);
        Assert.False(RayRecord.Wide.HasUniformReach);
        Assert.Equal(0u, RayRecord.Wide.ReachBits);

        RayRecord narrow = RayRecord.UniformReach(0x3F800000u);
        Assert.Equal(6, narrow.Words);
        Assert.Equal(24, narrow.Bytes);
        Assert.True(narrow.HasUniformReach);
        Assert.Equal(0x3F800000u, narrow.ReachBits);
    }

    [Fact]
    public void TheWideRecordRoundTripsEveryEdgeInEveryFieldIncludingTheReach()
    {
        // The reach takes every edge too, NaN and infinity included: the wide
        // record carries whatever the caller sent, and the kernel's guard
        // answers the undefined ones as misses.
        AssertRoundTrip(RayRecord.Wide, EdgeRays(i => EdgeBits[(i + 30) % EdgeBits.Length]));
    }

    [Theory]
    [MemberData(nameof(FiniteReaches))]
    public void TheUniformReachRecordRoundTripsEveryEdgeWithEachFiniteReach(uint reach)
    {
        Ray[] rays = EdgeRays(_ => reach);

        Assert.Equal(reach, RayRecord.UniformReachOf(rays));
        AssertRoundTrip(RayRecord.UniformReach(reach), rays);
    }

    [Theory]
    [MemberData(nameof(NonFiniteReaches))]
    public void ANonFiniteReachNeverGoesInThePushConstants(uint reach)
    {
        Ray[] rays = EdgeRays(_ => reach);

        Assert.Null(RayRecord.UniformReachOf(rays));
        Assert.Throws<ArgumentOutOfRangeException>(() => RayRecord.UniformReach(reach));

        // Such a slab is wide, and the wide record still carries the reach exactly.
        AssertRoundTrip(RayRecord.For(RayRecord.UniformReachOf(rays)), rays);
    }

    [Theory]
    [InlineData(0x00000000u, 0x80000000u)] // +0 and -0 compare equal as floats
    [InlineData(0x3F800000u, 0x3F800001u)] // one ulp apart
    [InlineData(0x00000001u, 0x00000002u)] // two denormals
    [InlineData(0x475DB3D7u, 0x3F800000u)]
    public void RaysWhoseReachesDifferInAnyBitAreNotUniform(uint a, uint b)
    {
        Ray[] rays = EdgeRays(i => i == 7 ? b : a);

        Assert.Null(RayRecord.UniformReachOf(rays));
        Assert.Equal(RayRecord.Wide, RayRecord.For(RayRecord.UniformReachOf(rays)));
    }

    [Fact]
    public void TwoNaNsWithDifferentPayloadsAreNotOneReach()
    {
        // Equal bits would be refused anyway (not finite); this is the case
        // a value comparison could never even express.
        Ray[] rays = EdgeRays(i => i == 0 ? 0x7FC00000u : 0x7FC12345u);

        Assert.Null(RayRecord.UniformReachOf(rays));
    }

    [Fact]
    public void OneRayAndNoRaysAreDecidedLikeAnyOther()
    {
        Assert.Null(RayRecord.UniformReachOf([]));
        Assert.Equal(0x3F800000u, RayRecord.UniformReachOf([new Ray(0, 0, 0, 1, 0, 0, 1f)]));
        Assert.Null(RayRecord.UniformReachOf([new Ray(0, 0, 0, 1, 0, 0, float.NaN)]));
    }

    [Fact]
    public void ForPicksTheNarrowRecordOnlyWhenThereIsAReach()
    {
        Assert.Equal(RayRecord.Wide, RayRecord.For(null));
        Assert.Equal(RayRecord.UniformReach(0x80000000u), RayRecord.For(0x80000000u));
        Assert.Throws<ArgumentOutOfRangeException>(() => RayRecord.For(0x7F800000u));
    }

    [Fact]
    public void PackingARayWithAnotherReachIntoANarrowRecordIsRefused()
    {
        // The record would silently give that ray the slab's reach instead.
        Ray[] rays = [new(0, 0, 0, 1, 0, 0, 1f), new(0, 0, 0, 1, 0, 0, -0f)];
        uint[] words = new uint[12];

        ArgumentException e = Assert.Throws<ArgumentException>(() => RayRecord.UniformReach(0x3F800000u).Pack(rays, words));
        Assert.Contains("ray 1", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PackingIntoTooFewWordsIsRefusedAndWritesNothing(bool narrow)
    {
        RayRecord record = narrow ? RayRecord.UniformReach(0x3F800000u) : RayRecord.Wide;
        Ray[] rays = [new(1, 2, 3, 4, 5, 6, 1f), new(1, 2, 3, 4, 5, 6, 1f)];
        uint[] words = new uint[(2 * record.Words) - 1];

        Assert.Throws<ArgumentException>(() => record.Pack(rays, words));
        Assert.All(words, w => Assert.Equal(0u, w));
    }

    [Fact]
    public void DecodingTooFewWordsIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RayRecord.Wide.Decode(new uint[6]));
        Assert.Throws<ArgumentOutOfRangeException>(() => RayRecord.UniformReach(0).Decode(new uint[5]));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    [InlineData(true, 5)]
    public void FillRepeatsOneRayExactly(bool narrow, int count)
    {
        Ray ray = new(F(0x80000000u), F(0x00000001u), F(0x7F7FFFFFu), F(0x7FC12345u), F(0xFF800000u), 1f, F(0x475DB3D7u));
        RayRecord record = narrow ? RayRecord.UniformReach(0x475DB3D7u) : RayRecord.Wide;
        uint[] words = new uint[(count + 1) * record.Words];
        Array.Fill(words, 0xDEADBEEFu);

        record.Fill(ray, count, words);

        for (int i = 0; i < count; i++)
        {
            Assert.Equal(BitsOf(ray), BitsOf(record.Decode(words.AsSpan(i * record.Words))));
        }

        // Nothing past the copies is touched.
        Assert.All(words.AsSpan(count * record.Words).ToArray(), w => Assert.Equal(0xDEADBEEFu, w));
    }
}
