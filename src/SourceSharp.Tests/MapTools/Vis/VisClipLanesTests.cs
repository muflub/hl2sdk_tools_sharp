//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Vis;

/// <summary>
/// <see cref="VisClipLanes"/> against <see cref="VisClip"/>: the lane-parallel
/// chop, separator derivation and lazy separator clip must give the scalar
/// code's answer to the bit, on every CPU CI runs on.
/// </summary>
/// <remarks>
/// <para>
/// Every comparison here is on raw bits (<see cref="BitConverter.SingleToInt32Bits"/>),
/// not on float equality: <c>-0 == +0</c> and <c>NaN != NaN</c> would each
/// hide a real difference. The inputs are seeded, so a failure reproduces;
/// they mix realistic portal-shaped windings (planar convex polygons on a
/// grid, where points lie exactly on planes) with arbitrary points, the exact
/// epsilon boundaries, NaN, and every winding length up to the cap -- so
/// every remainder of the four-lane loops is exercised.
/// </para>
/// <para>
/// A fused multiply-add, a reassociated dot product or a single-precision
/// reciprocal square root anywhere in the lanes changes the last bit of a
/// large share of these results, so these facts are what would catch the JIT
/// or a later edit doing any of them.
/// </para>
/// </remarks>
public class VisClipLanesTests
{
    private const float Eps = 0.01f;

    [Fact]
    public void TheSinglePrecisionEpsilonComparesExactlyAsThePromotedDoubleDoes()
    {
        // The lanes compare in float against 0.01f where the scalar code
        // promotes and compares against the double 0.01. Every float within
        // a hundred thousand ulps of each boundary, and the specials.
        List<float> values = [float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0f, -0f];
        foreach (float centre in new[] { Eps, -Eps })
        {
            float lo = centre;
            float hi = centre;
            for (int i = 0; i < 100_000; i++)
            {
                values.Add(lo);
                values.Add(hi);
                lo = MathF.BitDecrement(lo);
                hi = MathF.BitIncrement(hi);
            }
        }

        foreach (float x in values)
        {
            Assert.Equal(x > VisClip.OnVisEpsilon, x > VisClipLanes.EpsilonSingle);
            Assert.Equal(x < -VisClip.OnVisEpsilon, x < -VisClipLanes.EpsilonSingle);
            Assert.Equal(x < VisClip.OnVisEpsilon, x <= VisClipLanes.EpsilonSingle);
        }
    }

    [Fact]
    public void TheDegeneracyBoundaryIsTheOneWhereTheTwoConstantsDiffer()
    {
        // 0.01f itself: below the double epsilon, so a squared length of
        // exactly 0.01f is degenerate -- which is why the lanes use <=.
        float length = VisClipLanes.EpsilonSingle;
        Assert.True(length < VisClip.OnVisEpsilon);
        Assert.False(length < VisClipLanes.EpsilonSingle);
        Assert.True(length <= VisClipLanes.EpsilonSingle);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TheLaneChopMatchesTheScalarChopOnEveryWindingLength(int seed)
    {
        Random random = new(seed);
        int clipped = 0;
        int empty = 0;
        int unchanged = 0;

        for (int length = 0; length <= VisClip.MaxPointsOnWinding; length++)
        {
            for (int trial = 0; trial < 60; trial++)
            {
                Vec3[] winding = trial % 2 == 0 ? Polygon(random, length, quantize: trial % 4 == 0) : Scatter(random, length);
                (Vec3 normal, float distance) = Plane(random, winding, trial);
                VisChopResult result = AssertSameChop(winding, normal, distance);
                clipped += result == VisChopResult.Clipped ? 1 : 0;
                empty += result == VisChopResult.Empty ? 1 : 0;
                unchanged += result == VisChopResult.Unchanged ? 1 : 0;
            }
        }

        // The mix is part of the fact: a generator that only ever produced
        // one outcome would make the comparison vacuous.
        Assert.True(clipped > 500, $"clipped {clipped}");
        Assert.True(empty > 200, $"empty {empty}");
        Assert.True(unchanged > 500, $"unchanged {unchanged}");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(-1f)]
    public void TheLaneChopMatchesAtTheExactEpsilonBoundaries(float offset)
    {
        // An axial plane makes a point's distance exactly its X minus the
        // plane distance, so each of these lands exactly where it says: on
        // the plane, on each epsilon, and one ulp either side of each.
        float[] xs =
        [
            0f, -0f, Eps, -Eps, MathF.BitIncrement(Eps), MathF.BitDecrement(Eps),
            MathF.BitIncrement(-Eps), MathF.BitDecrement(-Eps), 1f, -1f,
        ];

        foreach (Vec3 normal in new[] { new Vec3(1f, 0f, 0f), new Vec3(-1f, 0f, 0f) })
        {
            for (int a = 0; a < xs.Length; a++)
            {
                for (int b = 0; b < xs.Length; b++)
                {
                    for (int c = 0; c < xs.Length; c++)
                    {
                        float sign = normal.X;
                        Vec3[] winding =
                        [
                            new((sign * xs[a]) + offset, 0f, 0f),
                            new((sign * xs[b]) + offset, 8f, 0f),
                            new((sign * xs[c]) + offset, 8f, 8f),
                            new((sign * xs[a]) + offset, 0f, 8f),
                            new((sign * xs[c]) + offset, -4f, 4f),
                        ];

                        AssertSameChop(winding, normal, sign * offset);
                    }
                }
            }
        }
    }

    [Fact]
    public void TheLaneChopMatchesWhenACoordinateIsNaN()
    {
        Random random = new(7);
        for (int trial = 0; trial < 400; trial++)
        {
            int length = 1 + (trial % 13);
            Vec3[] winding = Polygon(random, length, quantize: false);
            int victim = random.Next(length);
            Vec3 p = winding[victim];
            winding[victim] = (trial % 3) switch
            {
                0 => new Vec3(float.NaN, p.Y, p.Z),
                1 => new Vec3(p.X, float.NaN, p.Z),
                _ => new Vec3(p.X, p.Y, float.NaN),
            };

            (Vec3 normal, float distance) = Plane(random, winding, trial);
            AssertSameChop(winding, normal, distance);
            AssertSameChop(Polygon(random, length, quantize: true), new Vec3(float.NaN, 0f, 1f), 0f);
        }
    }

    [Fact]
    public void TheLaneChopRefusesWhatTheScalarChopRefuses()
    {
        Assert.Throws<ArgumentException>(() => VisClipLanes.ChopWinding(
            new Vec3[VisClip.MaxPointsOnWinding + 1], new Vec3(1f, 0f, 0f), 0f, new Vec3[16], out _));
        Assert.Throws<ArgumentException>(() => VisClipLanes.ChopWinding(
            new Vec3[4], new Vec3(1f, 0f, 0f), 0f, new Vec3[VisClip.MaxPointsOnFixedWinding - 1], out _));
    }

    [Fact]
    public void TheClassifierRefusesAPaddedLengthPastItsColumns()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VisClipLanes.Classify(
            new float[4], new float[4], new float[4], 8, new Vec3(1f, 0f, 0f), 0f, new float[8]));
    }

    [Fact]
    public void TransposingPadsWithTheFirstPointAndLeavesAnEmptyWindingEmpty()
    {
        float[] xs = new float[8];
        float[] ys = new float[8];
        float[] zs = new float[8];

        Assert.Equal(0, VisClipLanes.Transpose([], xs, ys, zs));
        Assert.Equal(4, VisClipLanes.Transpose([new(1f, 2f, 3f), new(4f, 5f, 6f)], xs, ys, zs));
        Assert.Equal([1f, 4f, 1f, 1f], xs[..4]);
        Assert.Equal([2f, 5f, 2f, 2f], ys[..4]);
        Assert.Equal([3f, 6f, 3f, 3f], zs[..4]);
        Assert.Equal(8, VisClipLanes.Transpose(new Vec3[8], xs, ys, zs));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothShapesOfTheReciprocalSquareRootMatchTheScalarOne(bool wide)
    {
        // The derivation takes the 256-bit shape where the CPU has it, so
        // on any one machine the flow exercises only one; this holds both to
        // the scalar expression on every CPU.
        Random random = new(31);
        List<float> values = [0f, -0f, 1f, 0.01f, float.Epsilon, float.MaxValue, float.PositiveInfinity, float.NaN, -1f];
        for (int i = 0; i < 20_000; i++)
        {
            values.Add(BitConverter.Int32BitsToSingle(random.Next(0, 0x7F800000)));
            values.Add(random.NextSingle() * 1e6f);
        }

        for (int i = 0; i + 4 <= values.Count; i += 4)
        {
            System.Runtime.Intrinsics.Vector128<float> lanes = VisClipLanes.ReciprocalSqrt(
                System.Runtime.Intrinsics.Vector128.Create(values[i], values[i + 1], values[i + 2], values[i + 3]), wide);
            for (int lane = 0; lane < 4; lane++)
            {
                float expected = (float)(1.0 / Math.Sqrt(values[i + lane]));
                Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(lanes[lane]));
            }
        }
    }

    [Fact]
    public void TransposingIntoColumnsTooShortForThePaddingIsRefused()
    {
        // Five points pad to eight, and the columns hold six.
        Assert.Throws<ArgumentException>(() => VisClipLanes.Transpose(
            new Vec3[5], new float[6], new float[8], new float[8]));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void TheLaneDerivationMatchesTheScalarDerivation(int seed)
    {
        // Every pass length from one to sixteen, so the four-lane loop over
        // pass vertices ends on every remainder, and source lengths from one
        // (no edge has another point, so nothing separates) upwards.
        Random random = new(seed);
        int planes = 0;
        for (int sourceLength = 1; sourceLength <= 9; sourceLength++)
        {
            for (int passLength = 1; passLength <= 16; passLength++)
            {
                for (int trial = 0; trial < 12; trial++)
                {
                    (Vec3[] source, Vec3[] pass) = trial switch
                    {
                        < 6 => FacingPair(random, sourceLength, passLength, quantize: trial % 2 == 0),
                        < 10 => (Scatter(random, sourceLength), Scatter(random, passLength)),
                        _ => Coplanar(random, sourceLength, passLength),
                    };

                    planes += AssertSameDerivation(source, pass);
                    planes += AssertSameDerivation(pass, source);
                }
            }
        }

        Assert.True(planes > 5_000, $"only {planes} planes derived");
    }

    [Fact]
    public void TheLaneDerivationMatchesOnDegenerateAndNaNWindings()
    {
        Vec3 a = new(0f, 0f, 0f);
        Vec3 b = new(16f, 0f, 0f);
        Vec3 c = new(16f, 16f, 0f);
        Vec3 d = new(0f, 16f, 0f);
        Vec3[] square = [a, b, c, d];
        Vec3[] far = [new(0f, 0f, 64f), new(0f, 16f, 64f), new(16f, 16f, 64f), new(16f, 0f, 64f)];

        // Coincident portals, repeated vertices (a zero-length edge gives a
        // zero cross product), a sub-epsilon sliver, and NaN in each role.
        AssertSameDerivation(square, square);
        AssertSameDerivation([a, a, b, c], far);
        AssertSameDerivation(square, [far[0], far[0], far[1], far[2], far[3]]);
        AssertSameDerivation([a, new(0.05f, 0f, 0f), new(0.05f, 0.05f, 0f)], far);
        AssertSameDerivation([a, b, new(float.NaN, 16f, 0f), d], far);
        AssertSameDerivation(square, [far[0], new(0f, float.NaN, 64f), far[2], far[3]]);
        AssertSameDerivation([a], far);
        AssertSameDerivation([a, b], far);
        AssertSameDerivation(square, [far[0]]);
    }

    [Fact]
    public void ACrossProductWhoseSquaredLengthIsExactlyTheEpsilonIsDegenerateInBoth()
    {
        // The one comparison where the single and double constants differ:
        // a squared length of exactly 0.01f is below the double epsilon, so
        // the scalar code rejects it, and the lanes' `<=` must too. Found by
        // search rather than written down, so the fact states what it needs.
        (float s, float q) = SquaresSummingToTheEpsilon();

        // Edge (0,0,0)-(1,0,0) against pass vertex (0,q,s): the cross
        // product is (0,-s,q), whose squared length is s*s + q*q = 0.01f. The
        // source's third point is well off that plane and the pass's other
        // points are well in front, so degeneracy is the only reason for the
        // scalar code to reject it.
        Vec3[] source = [new(0f, 0f, 0f), new(1f, 0f, 0f), new(0.5f, 10f, 0f)];
        Vec3[] pass = [new(0f, q, s), new(0f, -10f, 10f), new(5f, -10f, 10f)];

        Vec3[] normals = new Vec3[9];
        float[] distances = new float[9];
        int scalar = VisClip.BuildSeparators(source, pass, normals, distances);
        Assert.DoesNotContain(normals[..scalar], n => n.X == 0f && n.Y < 0f && n.Z > 0f);

        AssertSameDerivation(source, pass);
    }

    [Fact]
    public void AFlippedPlaneThroughTheOriginKeepsTheNegativeZeroDistance()
    {
        // The flip negates the distance with a unary minus, which turns +0
        // into -0, where the normal's 0 - x keeps +0. The lanes must flip
        // the sign bit rather than subtract from zero, or this distance comes
        // out +0. Edge (16,0,0)-(0,0,0) against pass vertex (0,0,64): the
        // plane is y = 0 exactly, the source's other points are at y = +16,
        // so it flips, and the pass's other points are at y = -16, in front.
        Vec3[] source = [new(16f, 0f, 0f), new(0f, 0f, 0f), new(0f, 16f, 0f), new(16f, 16f, 0f)];
        Vec3[] pass = [new(0f, 0f, 64f), new(16f, -16f, 64f), new(0f, -16f, 64f)];

        Vec3[] normals = new Vec3[12];
        float[] distances = new float[12];
        int scalar = VisClip.BuildSeparators(source, pass, normals, distances);
        Assert.Contains(
            distances[..scalar], d => BitConverter.SingleToInt32Bits(d) == BitConverter.SingleToInt32Bits(-0f));

        AssertSameDerivation(source, pass);
    }

    internal static (float S, float Q) SquaresSummingToTheEpsilon()
    {
        float s = 0.06f;
        for (int i = 0; i < 4000; i++)
        {
            float q = 0.08f;
            for (int k = 0; k < 4000; k++)
            {
                if ((s * s) + (q * q) == VisClipLanes.EpsilonSingle)
                {
                    return (s, q);
                }

                q = MathF.BitIncrement(q);
            }

            s = MathF.BitDecrement(s);
        }

        throw new InvalidOperationException("no pair of squares sums to exactly 0.01f in the searched range");
    }

    [Theory]
    [InlineData(21, false)]
    [InlineData(22, false)]
    [InlineData(21, true)]
    [InlineData(22, true)]
    public void TheLazyClipMatchesTheEagerAndTheFusedClips(int seed, bool pairEdges)
    {
        // One memo per ordering, shared by a run of targets -- the way a
        // frame's candidates share it -- so later targets see a list an
        // earlier one left partly derived.
        Random random = new(seed);
        int survived = 0;
        int blocked = 0;

        for (int pairing = 0; pairing < 300; pairing++)
        {
            int sourceLength = 3 + random.Next(6);
            int passLength = 3 + random.Next(6);
            (Vec3[] source, Vec3[] pass) = FacingPair(random, sourceLength, passLength, quantize: pairing % 2 == 0);

            VisSeparatorMemo forward = NewMemo(source, pass);
            VisSeparatorMemo reverse = NewMemo(pass, source);

            for (int t = 0; t < 8; t++)
            {
                Vec3[] target = Beyond(random, source, pass, 3 + random.Next(10));

                bool lazyFirst = AssertSameClip(ref forward, source, pass, target, flipClip: false, pairEdges, out Vec3[] first);
                if (!lazyFirst)
                {
                    blocked++;
                    continue;
                }

                if (AssertSameClip(ref reverse, pass, source, first, flipClip: true, pairEdges, out _))
                {
                    survived++;
                }
                else
                {
                    blocked++;
                }
            }
        }

        Assert.True(survived > 100, $"survived {survived}");
        Assert.True(blocked > 100, $"blocked {blocked}");
    }

    [Fact]
    public void AClipThatEndsAtTheFirstPlaneDerivesNoFurther()
    {
        // The point of the memo: a target the very first separating plane
        // removes must not pay for deriving the rest.
        // A square and a larger square facing it: every edge of the first
        // separates against some corner of the second.
        Vec3[] source = [new(0f, 0f, 0f), new(16f, 0f, 0f), new(16f, 16f, 0f), new(0f, 16f, 0f)];
        Vec3[] pass = [new(-8f, -8f, 64f), new(-8f, 24f, 64f), new(24f, 24f, 64f), new(24f, -8f, 64f)];
        Vec3[] normals = new Vec3[16];
        float[] distances = new float[16];
        int all = VisClip.BuildSeparators(source, pass, normals, distances);
        Assert.True(all >= 4, $"{all} planes");

        // A target far behind the first plane: every point on its back side.
        Vec3 n = normals[0];
        float back = distances[0] - 1000f;
        Vec3 origin = n * back;
        Vec3[] target = [origin, origin + new Vec3(0.5f, 0f, 0f), origin + new Vec3(0f, 0.5f, 0f)];

        VisSeparatorMemo memo = NewMemo(source, pass);
        Assert.False(VisClipLanes.ClipToSeparators(
            ref memo, source, pass, target, flipClip: false, new Vec3[VisClip.MaxPointsOnWinding], out _));
        Assert.True(memo.NextEdge < source.Length, $"derived {memo.NextEdge} of {source.Length} edges");
        Assert.True(memo.Count < all);
    }

    [Fact]
    public void ALazyClipWithoutRoomForAWholeWindingIsRefused()
    {
        VisSeparatorMemo memo = new([], []);
        Assert.Throws<ArgumentException>(() => VisClipLanes.ClipToSeparators(
            ref memo, [], [], [], false, new Vec3[VisClip.MaxPointsOnFixedWinding], out _));
    }

    [Fact]
    public void ALazyClipWithNoSeparatorsHandsTheTargetBack()
    {
        Vec3[] square = [new(0f, 0f, 0f), new(16f, 0f, 0f), new(16f, 16f, 0f), new(0f, 16f, 0f)];
        VisSeparatorMemo memo = NewMemo(square, square);
        Vec3[] result = new Vec3[VisClip.MaxPointsOnWinding];

        Assert.True(VisClipLanes.ClipToSeparators(ref memo, square, square, square, false, result, out int count));
        Assert.Equal(square, result[..count]);
        Assert.Equal(0, memo.Count);
        Assert.Equal(square.Length, memo.NextEdge);
    }

    [Theory]
    [InlineData(41)]
    [InlineData(42)]
    [InlineData(43)]
    public void AnEdgePairWritesWhatTwoSingleEdgesWrite(int seed)
    {
        // Every source length from two (the pair is both edges of a
        // degenerate two-point winding, and wraps to vertex 0) upwards, every
        // pair position including the one that wraps, and every pass length
        // to sixteen -- so the four-lane chunks end on every remainder and
        // the second edge's planes are held back across up to four chunks.
        // The lists start part-full, as they do in a memo.
        Random random = new(seed);
        int planes = 0;
        for (int sourceLength = 2; sourceLength <= 9; sourceLength++)
        {
            for (int passLength = 1; passLength <= 16; passLength++)
            {
                for (int trial = 0; trial < 8; trial++)
                {
                    (Vec3[] source, Vec3[] pass) = trial switch
                    {
                        < 4 => FacingPair(random, sourceLength, passLength, quantize: trial % 2 == 0),
                        < 7 => (Scatter(random, sourceLength), Scatter(random, passLength)),
                        _ => Coplanar(random, sourceLength, passLength),
                    };

                    for (int i = 0; i + 1 < sourceLength; i++)
                    {
                        planes += AssertPairMatchesSingles(source, pass, i, alreadyFound: trial % 3);
                        if (i + 1 < passLength)
                        {
                            planes += AssertPairMatchesSingles(pass, source, i, alreadyFound: 0);
                        }
                    }
                }
            }
        }

        Assert.True(planes > 5_000, $"only {planes} planes derived");
    }

    [Theory]
    [InlineData(51)]
    [InlineData(52)]
    public void ADerivationByPairsMatchesTheScalarDerivationOnOddAndEvenEdgeCounts(int seed)
    {
        // A whole list built the way the paired memo builds it -- pairs from
        // edge 0, and a single edge for the tail when the count is odd --
        // against the scalar code's list.
        Random random = new(seed);
        int odd = 0;
        int even = 0;
        for (int sourceLength = 1; sourceLength <= 9; sourceLength++)
        {
            for (int passLength = 1; passLength <= 12; passLength++)
            {
                for (int trial = 0; trial < 6; trial++)
                {
                    (Vec3[] source, Vec3[] pass) = trial < 4
                        ? FacingPair(random, sourceLength, passLength, quantize: trial % 2 == 0)
                        : (Scatter(random, sourceLength), Scatter(random, passLength));

                    int room = Math.Max(1, sourceLength * passLength);
                    Vec3[] scalarNormals = new Vec3[room];
                    float[] scalarDistances = new float[room];
                    int scalar = VisClip.BuildSeparators(source, pass, scalarNormals, scalarDistances);

                    Vec3[] pairNormals = new Vec3[room];
                    float[] pairDistances = new float[room];
                    int paired = 0;
                    for (int i = 0; i < sourceLength; i += 2)
                    {
                        paired = i + 1 < sourceLength
                            ? VisClipLanes.DeriveEdgePair(source, pass, i, pairNormals, pairDistances, paired)
                            : VisClipLanes.DeriveEdge(source, pass, i, pairNormals, pairDistances, paired);
                    }

                    Assert.Equal(scalar, paired);
                    AssertSameBits(scalarNormals.AsSpan(0, scalar), pairNormals.AsSpan(0, paired));
                    AssertSameDistanceBits(scalarDistances.AsSpan(0, scalar), pairDistances.AsSpan(0, paired));
                    if (sourceLength % 2 == 1)
                    {
                        odd += scalar;
                    }
                    else
                    {
                        even += scalar;
                    }
                }
            }
        }

        Assert.True(odd > 500, $"only {odd} planes from odd edge counts");
        Assert.True(even > 500, $"only {even} planes from even edge counts");
    }

    [Fact]
    public void AnEdgePairMatchesOnDegenerateAndNaNWindings()
    {
        Vec3 a = new(0f, 0f, 0f);
        Vec3 b = new(16f, 0f, 0f);
        Vec3 c = new(16f, 16f, 0f);
        Vec3 d = new(0f, 16f, 0f);
        Vec3[] square = [a, b, c, d];
        Vec3[] far = [new(0f, 0f, 64f), new(0f, 16f, 64f), new(16f, 16f, 64f), new(16f, 0f, 64f)];

        // The same windings as the single-edge fact, at every pair position:
        // a zero-length edge in either half, NaN in either half's scan, a
        // sub-epsilon sliver, a pass of one point, and a source of two.
        Vec3[][] sources =
        [
            square,
            [a, a, b, c],
            [a, b, b, c],
            [a, new(0.05f, 0f, 0f), new(0.05f, 0.05f, 0f)],
            [a, b, new(float.NaN, 16f, 0f), d],
            [new(float.NaN, 0f, 0f), b, c, d],
            [a, b],
        ];
        Vec3[][] passes = [far, square, [far[0], far[0], far[1], far[2], far[3]], [far[0], new(0f, float.NaN, 64f), far[2], far[3]], [far[0]]];
        foreach (Vec3[] source in sources)
        {
            foreach (Vec3[] pass in passes)
            {
                for (int i = 0; i + 1 < source.Length; i++)
                {
                    AssertPairMatchesSingles(source, pass, i, alreadyFound: 1);
                }
            }
        }
    }

    [Theory]
    [InlineData(91)]
    [InlineData(92)]
    public void AnEdgePairSkipsEachEdgesOwnEndpointsFarFromTheOrigin(int seed)
    {
        // Each half's source scan must pass over ITS edge's endpoints and no
        // others: edge A's first vertex is a point edge B tests, and edge B's
        // far vertex is one edge A tests. Near the origin an edge's own
        // endpoints land within the epsilon of its plane, so scanning them
        // would change nothing; a million units out, rounding puts
        // them past it and the first one scanned decides the side. Windings
        // there, skewed off every axis, are what tell the per-half masks
        // from a shared skip list.
        Random random = new(seed);
        int planes = 0;
        for (int trial = 0; trial < 400; trial++)
        {
            int sourceLength = 3 + random.Next(5);
            int passLength = 1 + random.Next(8);
            Vec3 far = new(Next(random, 1_000_000f), Next(random, 1_000_000f), Next(random, 1_000_000f));
            Vec3[] source = Scatter(random, sourceLength);
            Vec3[] pass = Scatter(random, passLength);
            for (int k = 0; k < sourceLength; k++)
            {
                source[k] = far + (source[k] * 0.05f);
            }

            for (int k = 0; k < passLength; k++)
            {
                pass[k] = far + (pass[k] * 0.05f) + new Vec3(0f, 0f, 64f);
            }

            for (int i = 0; i + 1 < sourceLength; i++)
            {
                planes += AssertPairMatchesSingles(source, pass, i, alreadyFound: 0);
            }
        }

        Assert.True(planes > 500, $"only {planes} planes derived");
    }

    [Fact]
    public void AnEdgePairNeedsASecondEdge()
    {
        Vec3[] square = [new(0f, 0f, 0f), new(16f, 0f, 0f), new(16f, 16f, 0f), new(0f, 16f, 0f)];
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VisClipLanes.DeriveEdgePair(square, square, 3, new Vec3[16], new float[16], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VisClipLanes.DeriveEdgePair([new Vec3(0f, 0f, 0f)], square, 0, new Vec3[4], new float[4], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VisClipLanes.DeriveEdgePair(square, square, -1, new Vec3[16], new float[16], 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AClipThatEndsInsideAPairDerivesOnlyThatPair(bool firstPlaneOfTheSecondEdge)
    {
        // The memo stops between the two edges of a pair: a target killed by
        // a plane of edge 0 (or of edge 1) leaves edges 0 and 1 derived and
        // nothing after them, and a later target that survives every plane
        // still sees the full list in the full order.
        Vec3[] source = [new(0f, 0f, 0f), new(16f, 0f, 0f), new(16f, 16f, 0f), new(0f, 16f, 0f), new(-8f, 8f, 0f)];
        Vec3[] pass = [new(-8f, -8f, 64f), new(-8f, 24f, 64f), new(24f, 24f, 64f), new(24f, -8f, 64f)];
        int room = source.Length * pass.Length;
        Vec3[] normals = new Vec3[room];
        float[] distances = new float[room];
        int all = VisClip.BuildSeparators(source, pass, normals, distances);
        int edge0 = VisClipLanes.DeriveEdge(source, pass, 0, new Vec3[room], new float[room], 0);
        int pair01 = VisClipLanes.DeriveEdgePair(source, pass, 0, new Vec3[room], new float[room], 0);
        Assert.True(edge0 > 0 && pair01 > edge0 && all > pair01, $"edge 0: {edge0}, pair: {pair01}, all: {all}");

        // A small target behind the killing plane and well in front of every
        // plane before it, found by a seeded search so the clip provably
        // ends at that plane and not earlier.
        int killer = firstPlaneOfTheSecondEdge ? edge0 : 0;
        Vec3[] dead = TargetKilledAt(normals, distances, killer, new Random(61));

        VisSeparatorMemo paired = NewMemo(source, pass);
        VisSeparatorMemo single = NewMemo(source, pass);
        Assert.False(AssertSameClip(ref paired, source, pass, dead, flipClip: false, pairEdges: true, out _));
        Assert.False(AssertSameClip(ref single, source, pass, dead, flipClip: false, pairEdges: false, out _));
        Assert.Equal(2, paired.NextEdge);
        Assert.Equal(pair01, paired.Count);
        Assert.Equal(firstPlaneOfTheSecondEdge ? 2 : 1, single.NextEdge);

        // Then a target that nothing separates, through the same memos: the
        // walk resumes from the pair's end, takes edges 2-3 as a pair and
        // edge 4 alone.
        Vec3 middle = new(8f, 8f, 256f);
        Vec3[] visible = [middle, middle + new Vec3(0.25f, 0f, 0f), middle + new Vec3(0f, 0.25f, 0f)];
        bool pairedSurvived = AssertSameClip(ref paired, source, pass, visible, flipClip: false, pairEdges: true, out Vec3[] pairedSurvivor);
        bool singleSurvived = AssertSameClip(ref single, source, pass, visible, flipClip: false, pairEdges: false, out Vec3[] singleSurvivor);
        Assert.True(pairedSurvived);
        Assert.True(singleSurvived);
        AssertSameBits(singleSurvivor, pairedSurvivor);
        Assert.Equal(source.Length, paired.NextEdge);
        Assert.Equal(all, paired.Count);
        AssertSameBits(normals.AsSpan(0, all), paired.Normals.AsSpan(0, all));
        AssertSameDistanceBits(distances.AsSpan(0, all), paired.Distances.AsSpan(0, all));
    }

    internal static Vec3[] TargetKilledAt(Vec3[] normals, float[] distances, int killer, Random random)
    {
        for (int attempt = 0; attempt < 100_000; attempt++)
        {
            Vec3 p = new(Next(random, 200f), Next(random, 200f), 64f + (random.NextSingle() * 400f));
            Vec3[] target = [p, p + new Vec3(0.5f, 0f, 0f), p + new Vec3(0f, 0.5f, 0f)];
            bool fits = true;
            for (int k = 0; k <= killer && fits; k++)
            {
                foreach (Vec3 point in target)
                {
                    float d = Vec3.Dot(point, normals[k]) - distances[k];
                    fits &= k < killer ? d > 1f : d < -1f;
                }
            }

            if (fits)
            {
                return target;
            }
        }

        throw new InvalidOperationException($"no target is killed exactly at plane {killer}");
    }

    private static int AssertPairMatchesSingles(Vec3[] source, Vec3[] pass, int i, int alreadyFound)
    {
        int room = alreadyFound + (2 * Math.Max(1, pass.Length));
        Vec3[] singleNormals = new Vec3[room];
        float[] singleDistances = new float[room];
        Vec3[] pairNormals = new Vec3[room];
        float[] pairDistances = new float[room];
        for (int k = 0; k < alreadyFound; k++)
        {
            // A prefix the derivation must leave alone.
            singleNormals[k] = pairNormals[k] = new Vec3(k, -k, 0.5f);
            singleDistances[k] = pairDistances[k] = k;
        }

        int single = VisClipLanes.DeriveEdge(source, pass, i, singleNormals, singleDistances, alreadyFound);
        single = VisClipLanes.DeriveEdge(source, pass, i + 1, singleNormals, singleDistances, single);
        int paired = VisClipLanes.DeriveEdgePair(source, pass, i, pairNormals, pairDistances, alreadyFound);

        Assert.Equal(single, paired);
        AssertSameBits(singleNormals.AsSpan(0, single), pairNormals.AsSpan(0, paired));
        AssertSameDistanceBits(singleDistances.AsSpan(0, single), pairDistances.AsSpan(0, paired));
        return single - alreadyFound;
    }

    internal static void AssertSameDistanceBits(ReadOnlySpan<float> expected, ReadOnlySpan<float> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(BitConverter.SingleToInt32Bits(expected[i]), BitConverter.SingleToInt32Bits(actual[i]));
        }
    }

    private static VisSeparatorMemo NewMemo(Vec3[] source, Vec3[] pass) =>
        new(new Vec3[source.Length * pass.Length], new float[source.Length * pass.Length]);

    private static VisChopResult AssertSameChop(Vec3[] winding, Vec3 normal, float distance)
    {
        Vec3[] scalarOut = new Vec3[VisClip.MaxPointsOnFixedWinding];
        Vec3[] laneOut = new Vec3[VisClip.MaxPointsOnFixedWinding];

        VisChopResult scalar = VisClip.ChopWinding(winding, normal, distance, scalarOut, out int scalarCount);
        VisChopResult lanes = VisClipLanes.ChopWinding(winding, normal, distance, laneOut, out int laneCount);

        Assert.Equal(scalar, lanes);
        Assert.Equal(scalarCount, laneCount);
        if (scalar == VisChopResult.Clipped)
        {
            AssertSameBits(scalarOut.AsSpan(0, scalarCount), laneOut.AsSpan(0, laneCount));
        }

        return scalar;
    }

    private static int AssertSameDerivation(Vec3[] source, Vec3[] pass)
    {
        int room = Math.Max(1, source.Length * pass.Length);
        Vec3[] scalarNormals = new Vec3[room];
        float[] scalarDistances = new float[room];
        Vec3[] laneNormals = new Vec3[room];
        float[] laneDistances = new float[room];

        int scalar = VisClip.BuildSeparators(source, pass, scalarNormals, scalarDistances);
        int lanes = VisClipLanes.BuildSeparators(source, pass, laneNormals, laneDistances);

        Assert.Equal(scalar, lanes);
        AssertSameBits(scalarNormals.AsSpan(0, scalar), laneNormals.AsSpan(0, lanes));
        for (int i = 0; i < scalar; i++)
        {
            Assert.Equal(
                BitConverter.SingleToInt32Bits(scalarDistances[i]),
                BitConverter.SingleToInt32Bits(laneDistances[i]));
        }

        return scalar;
    }

    private static bool AssertSameClip(
        ref VisSeparatorMemo memo, Vec3[] source, Vec3[] pass, Vec3[] target, bool flipClip, bool pairEdges, out Vec3[] survivor)
    {
        int room = source.Length * pass.Length;
        Vec3[] normals = new Vec3[room];
        float[] distances = new float[room];
        int planes = VisClip.BuildSeparators(source, pass, normals, distances);

        Vec3[] eager = new Vec3[VisClip.MaxPointsOnWinding];
        bool eagerSurvived = VisClip.ClipToSeparatorPlanes(
            normals.AsSpan(0, planes), distances.AsSpan(0, planes), target, flipClip, eager, out int eagerCount);

        Vec3[] fused = new Vec3[VisClip.MaxPointsOnWinding];
        bool fusedSurvived = VisClip.ClipToSeparators(source, pass, target, flipClip, fused, out int fusedCount);

        Vec3[] lazy = new Vec3[VisClip.MaxPointsOnWinding];
        bool lazySurvived = VisClipLanes.ClipToSeparators(
            ref memo, source, pass, target, flipClip, pairEdges, lazy, out int lazyCount);

        Assert.Equal(eagerSurvived, lazySurvived);
        Assert.Equal(fusedSurvived, lazySurvived);

        // Whatever the memo holds so far is a prefix of the full list.
        Assert.True(memo.Count <= planes);
        AssertSameBits(normals.AsSpan(0, memo.Count), memo.Normals.AsSpan(0, memo.Count));

        survivor = [];
        if (lazySurvived)
        {
            Assert.Equal(eagerCount, lazyCount);
            Assert.Equal(fusedCount, lazyCount);
            AssertSameBits(eager.AsSpan(0, eagerCount), lazy.AsSpan(0, lazyCount));
            AssertSameBits(fused.AsSpan(0, fusedCount), lazy.AsSpan(0, lazyCount));
            survivor = lazy[..lazyCount];
        }

        return lazySurvived;
    }

    internal static void AssertSameBits(ReadOnlySpan<Vec3> expected, ReadOnlySpan<Vec3> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(BitConverter.SingleToInt32Bits(expected[i].X), BitConverter.SingleToInt32Bits(actual[i].X));
            Assert.Equal(BitConverter.SingleToInt32Bits(expected[i].Y), BitConverter.SingleToInt32Bits(actual[i].Y));
            Assert.Equal(BitConverter.SingleToInt32Bits(expected[i].Z), BitConverter.SingleToInt32Bits(actual[i].Z));
        }
    }

    /// <summary>A planar convex polygon, the shape a portal is.</summary>
    internal static Vec3[] Polygon(Random random, int length, bool quantize)
    {
        Vec3 centre = new(Next(random, 512f), Next(random, 512f), Next(random, 512f));
        return PolygonAround(random, centre, RandomUnit(random, axial: quantize), length, quantize);
    }

    internal static Vec3[] PolygonAround(Random random, Vec3 centre, Vec3 normal, int length, bool quantize)
    {
        Vec3 helper = MathF.Abs(normal.X) < 0.9f ? new Vec3(1f, 0f, 0f) : new Vec3(0f, 1f, 0f);
        Vec3 u = Normalize(Vec3.Cross(normal, helper));
        Vec3 v = Vec3.Cross(normal, u);
        float radius = 4f + (random.NextSingle() * 96f);
        float phase = random.NextSingle();

        Vec3[] points = new Vec3[length];
        for (int i = 0; i < length; i++)
        {
            // A polygon's angles: sin and cos of evenly spaced turns, from a
            // table so the fact itself does no platform math.
            (float s, float c) = Turn(phase + ((float)i / Math.Max(1, length)));
            Vec3 p = centre + (u * (c * radius)) + (v * (s * radius));
            points[i] = quantize ? new Vec3(MathF.Round(p.X * 8f) / 8f, MathF.Round(p.Y * 8f) / 8f, MathF.Round(p.Z * 8f) / 8f) : p;
        }

        return points;
    }

    /// <summary>Two portals facing each other across a gap, like a source and a pass.</summary>
    internal static (Vec3[] Source, Vec3[] Pass) FacingPair(Random random, int sourceLength, int passLength, bool quantize)
    {
        Vec3 axis = RandomUnit(random, axial: quantize);
        Vec3 centre = new(Next(random, 256f), Next(random, 256f), Next(random, 256f));
        float gap = 8f + (random.NextSingle() * 256f);
        Vec3 tilt = RandomUnit(random, axial: false) * (random.NextSingle() * 0.3f);
        Vec3[] source = PolygonAround(random, centre, axis, sourceLength, quantize);
        Vec3[] pass = PolygonAround(random, centre + (axis * gap), Normalize(axis + tilt), passLength, quantize);
        return (source, pass);
    }

    /// <summary>A target beyond the pass, sometimes in line and sometimes off to one side.</summary>
    internal static Vec3[] Beyond(Random random, Vec3[] source, Vec3[] pass, int length)
    {
        Vec3 from = Centroid(source);
        Vec3 to = Centroid(pass);
        Vec3 axis = Normalize(to - from);
        Vec3 sideways = RandomUnit(random, axial: false) * (random.NextSingle() * 160f);
        Vec3 centre = to + (axis * (16f + (random.NextSingle() * 200f))) + sideways;
        return PolygonAround(random, centre, axis, length, quantize: random.Next(2) == 0);
    }

    internal static (Vec3[] Source, Vec3[] Pass) Coplanar(Random random, int sourceLength, int passLength)
    {
        Vec3 normal = RandomUnit(random, axial: true);
        Vec3 centre = new(Next(random, 256f), Next(random, 256f), Next(random, 256f));
        return (
            PolygonAround(random, centre, normal, sourceLength, quantize: true),
            PolygonAround(random, centre + new Vec3(normal.Y, normal.Z, normal.X) * 32f, normal, passLength, quantize: true));
    }

    internal static Vec3[] Scatter(Random random, int length)
    {
        Vec3[] points = new Vec3[length];
        for (int i = 0; i < length; i++)
        {
            points[i] = new Vec3(Next(random, 300f), Next(random, 300f), Next(random, 300f));
        }

        return points;
    }

    /// <summary>
    /// A clipping plane for a winding: through one of its points, on an
    /// epsilon from one, or anywhere, with axial normals among them.
    /// </summary>
    internal static (Vec3 Normal, float Distance) Plane(Random random, Vec3[] winding, int trial)
    {
        Vec3 normal = RandomUnit(random, axial: trial % 3 == 0);
        if (winding.Length == 0)
        {
            return (normal, Next(random, 100f));
        }

        float through = Vec3.Dot(winding[random.Next(winding.Length)], normal);
        float distance = (trial % 5) switch
        {
            0 => through,
            1 => through - Eps,
            2 => through + Eps,
            3 => MathF.BitIncrement(through),
            _ => through + Next(random, 64f),
        };
        return (normal, distance);
    }

    internal static Vec3 RandomUnit(Random random, bool axial)
    {
        if (axial)
        {
            float sign = random.Next(2) == 0 ? 1f : -1f;
            return random.Next(3) switch
            {
                0 => new Vec3(sign, 0f, 0f),
                1 => new Vec3(0f, sign, 0f),
                _ => new Vec3(0f, 0f, sign),
            };
        }

        Vec3 v;
        do
        {
            v = new Vec3(Next(random, 1f), Next(random, 1f), Next(random, 1f));
        }
        while (Vec3.Dot(v, v) < 0.01f);

        return Normalize(v);
    }

    internal static Vec3 Normalize(Vec3 v) => v * (1f / MathF.Sqrt(Vec3.Dot(v, v)));

    internal static Vec3 Centroid(Vec3[] points)
    {
        Vec3 sum = Vec3.Zero;
        foreach (Vec3 p in points)
        {
            sum += p;
        }

        return sum * (1f / points.Length);
    }

    internal static float Next(Random random, float extent) => ((random.NextSingle() * 2f) - 1f) * extent;

    /// <summary>
    /// The sine and cosine of a fraction of a turn, interpolated from a
    /// sixteen-entry table: good enough for a convex polygon, and no platform
    /// math.
    /// </summary>
    internal static (float Sin, float Cos) Turn(float fraction)
    {
        ReadOnlySpan<float> sines =
        [
            0f, 0.38268343f, 0.70710677f, 0.9238795f, 1f, 0.9238795f, 0.70710677f, 0.38268343f,
            0f, -0.38268343f, -0.70710677f, -0.9238795f, -1f, -0.9238795f, -0.70710677f, -0.38268343f,
        ];
        float f = (fraction - MathF.Floor(fraction)) * 16f;
        int i = (int)f % 16;
        int j = (i + 1) % 16;
        int k = (i + 4) % 16;
        int m = (j + 4) % 16;
        float t = f - MathF.Floor(f);
        return (sines[i] + ((sines[j] - sines[i]) * t), sines[k] + ((sines[m] - sines[k]) * t));
    }
}
