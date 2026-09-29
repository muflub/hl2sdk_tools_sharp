//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Vis;

using Xunit;

using static SourceSharp.Tests.MapTools.Vis.VisClipLanesTests;

namespace SourceSharp.Tests.MapTools.Vis;

/// <summary>
/// <see cref="VisClipLanes512"/>, the <see cref="VisSeparatorPath.Vector512"/>
/// separator clip, against the scalar <see cref="VisClip"/> and against
/// <see cref="VisClipLanes"/>, main's path: the same planes in the same order,
/// the same chops and the same bits, on every CPU CI runs on.
/// </summary>
/// <remarks>
/// <para>
/// These run everywhere, including on a CPU without AVX-512 and on arm64:
/// there the runtime executes <see cref="System.Runtime.Intrinsics.Vector512{T}"/>
/// as narrower operations in software, and each of those is as correctly
/// rounded as the hardware instruction, so a fact that passes on one runner
/// holds the software fallback to the scalar code on the next.
/// <see cref="TheseFactsRunOnEveryCpuWhateverItsVectorWidth"/> says which
/// kind of run this one was.
/// </para>
/// <para>
/// Comparisons are on raw bits, as in <see cref="VisClipLanesTests"/>, whose
/// seeded generators these reuse. The work-count facts at the end are this
/// path's counterpart of that class's laziness facts: that path derives only
/// as far as a clip reaches, this one derives a frame's whole list at the
/// first clip that needs any of it, four edges per derivation, and never
/// again.
/// </para>
/// </remarks>
public class VisClipLanes512Tests
{
    private const float Eps = 0.01f;

    [Fact]
    public void TheseFactsRunOnEveryCpuWhateverItsVectorWidth()
    {
        // The claim above, stated where a runner's log shows it: the wide
        // derivation runs, and agrees with the scalar one, on this runner,
        // whether or not it accelerates 512-bit vectors. Nothing here is
        // conditioned on the hardware.
        Vec3[] source = [new(0f, 0f, 0f), new(16f, 0f, 0f), new(16f, 16f, 0f), new(0f, 16f, 0f)];
        Vec3[] pass = [new(-8f, -8f, 64f), new(-8f, 24f, 64f), new(24f, 24f, 64f), new(24f, -8f, 64f)];
        Assert.True(AssertSameDerivation512(source, pass) > 0);
    }

    // ---------------------------------------------------------------- chop

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TheBatchChopMatchesTheScalarChopOnEveryWindingLength(int seed)
    {
        Random random = new(seed);
        int clipped = 0;
        int empty = 0;
        int unchanged = 0;

        for (int length = 0; length <= VisClip.MaxPointsOnWinding; length++)
        {
            for (int trial = 0; trial < 40; trial++)
            {
                Vec3[] winding = trial % 2 == 0 ? Polygon(random, length, quantize: trial % 4 == 0) : Scatter(random, length);
                (Vec3 normal, float distance) = Plane(random, winding, trial);
                VisChopResult result = AssertSameChop512(winding, normal, distance);
                clipped += result == VisChopResult.Clipped ? 1 : 0;
                empty += result == VisChopResult.Empty ? 1 : 0;
                unchanged += result == VisChopResult.Unchanged ? 1 : 0;
            }
        }

        Assert.True(clipped > 300, $"clipped {clipped}");
        Assert.True(empty > 100, $"empty {empty}");
        Assert.True(unchanged > 300, $"unchanged {unchanged}");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(-1f)]
    public void TheBatchChopMatchesAtTheExactEpsilonBoundaries(float offset)
    {
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

                        AssertSameChop512(winding, normal, sign * offset);
                    }
                }
            }
        }
    }

    [Fact]
    public void TheBatchChopMatchesWhenACoordinateIsNaN()
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
            AssertSameChop512(winding, normal, distance);
            AssertSameChop512(Polygon(random, length, quantize: true), new Vec3(float.NaN, 0f, 1f), 0f);
        }
    }

    [Fact]
    public void TheBatchChopRefusesWhatTheScalarChopRefuses()
    {
        Assert.Throws<ArgumentException>(() => VisClipLanes512.ChopWinding(
            new Vec3[VisClip.MaxPointsOnWinding + 1], new Vec3(1f, 0f, 0f), 0f, true, new Vec3[16], out _));
        Assert.Throws<ArgumentException>(() => VisClipLanes512.ChopWinding(
            new Vec3[4], new Vec3(1f, 0f, 0f), 0f, false, new Vec3[VisClip.MaxPointsOnFixedWinding - 1], out _));
    }

    // ---------------------------------------------------------------- batches

    [Fact]
    public void ABatchRefusesPlanesOrLanesOutsideItsBuffers()
    {
        Vec3[] square = [new(0f, 0f, 0f), new(16f, 0f, 0f), new(16f, 16f, 0f), new(0f, 16f, 0f)];
        float[] planes = new float[4 * 16];
        float[] lanes = new float[square.Length * VisClipLanes512.BatchLanes];

        // In range, for both widths: the last batch that fits a column.
        Assert.Equal(-1, VisClipLanes512.FirstBehind8(square, planes, 16, 8, 1, false, lanes, out _));
        Assert.Equal(-1, VisClipLanes512.FirstBehind4(square, planes, 16, 12, 1, false, lanes, out _));

        // A batch that would read past its column, one that starts before
        // the list, one with no planes, columns longer than the array, and
        // lanes too short for the winding.
        Assert.Throws<ArgumentException>(() => VisClipLanes512.FirstBehind8(square, planes, 16, 9, 1, false, lanes, out _));
        Assert.Throws<ArgumentException>(() => VisClipLanes512.FirstBehind4(square, planes, 16, 13, 1, false, lanes, out _));
        Assert.Throws<ArgumentException>(() => VisClipLanes512.FirstBehind8(square, planes, 16, -1, 1, false, lanes, out _));
        Assert.Throws<ArgumentException>(() => VisClipLanes512.FirstBehind4(square, planes, 16, 0, 0, false, lanes, out _));
        Assert.Throws<ArgumentException>(() => VisClipLanes512.FirstBehind8(square, planes, 17, 0, 1, false, lanes, out _));
        Assert.Throws<ArgumentException>(() => VisClipLanes512.FirstBehind4(
            square, planes, 16, 0, 1, false, new float[(square.Length * VisClipLanes512.BatchLanes) - 1], out _));
    }

    [Theory]
    [InlineData(71, false)]
    [InlineData(72, false)]
    [InlineData(71, true)]
    [InlineData(72, true)]
    public void ABatchFindsTheFirstPlaneTheScalarChopWouldActOnWithTheScalarDistances(int seed, bool flip)
    {
        // Up to eight planes against one winding, every lane's distance held
        // to the scalar chop's on raw bits, and the batch's answer -- the
        // first plane with a point behind, and whether a point is in front
        // of it -- held to the scalar classification. Lanes past `available`
        // hold planes that put every point behind, so a batch that looked at
        // them would answer wrongly.
        Random random = new(seed);
        int hits = 0;
        int misses = 0;
        int empties = 0;
        for (int trial = 0; trial < 3000; trial++)
        {
            int length = trial % (VisClip.MaxPointsOnWinding + 1);
            Vec3[] winding = (trial % 3) switch
            {
                0 => Polygon(random, length, quantize: trial % 6 == 0),
                1 => Scatter(random, length),
                _ => Polygon(random, length, quantize: true),
            };
            if (length > 0 && trial % 17 == 0)
            {
                int victim = random.Next(length);
                winding[victim] = new Vec3(winding[victim].X, float.NaN, winding[victim].Z);
            }

            int available = 1 + random.Next(8);
            const int stride = 16;
            float[] columns = new float[4 * stride];
            Vec3[] normals = new Vec3[8];
            float[] distances = new float[8];
            for (int j = 0; j < 8; j++)
            {
                (Vec3 normal, float distance) = j < available
                    ? Plane(random, winding, trial + j)
                    : (new Vec3(1f, 0f, 0f), flip ? -1e30f : 1e30f);
                if (j < available && trial % 23 == 0 && j == available - 1)
                {
                    normal = new Vec3(normal.X, float.NaN, normal.Z);
                }

                normals[j] = normal;
                distances[j] = distance;
                columns[j] = normal.X;
                columns[stride + j] = normal.Y;
                columns[(2 * stride) + j] = normal.Z;
                columns[(3 * stride) + j] = distance;
            }

            int expectedHit = -1;
            bool expectedFront = false;
            float[,] expectedDists = new float[Math.Max(1, length), 8];
            for (int j = 0; j < available; j++)
            {
                Vec3 normal = flip ? VisClip.Negate(normals[j]) : normals[j];
                float distance = flip ? -distances[j] : distances[j];
                bool anyBehind = false;
                bool anyFront = false;
                for (int k = 0; k < length; k++)
                {
                    float dot = Vec3.Dot(winding[k], normal);
                    dot -= distance;
                    expectedDists[k, j] = dot;
                    anyBehind |= dot < -VisClip.OnVisEpsilon;
                    anyFront |= dot > VisClip.OnVisEpsilon;
                }

                if (anyBehind && expectedHit < 0)
                {
                    expectedHit = j;
                    expectedFront = anyFront;
                }
            }

            foreach (bool wide in new[] { false, true })
            {
                if (!wide && available > 4)
                {
                    continue;
                }

                float[] lanes = new float[Math.Max(1, length) * VisClipLanes512.BatchLanes];
                bool front;
                int hit = wide
                    ? VisClipLanes512.FirstBehind8(winding, columns, stride, 0, available, flip, lanes, out front)
                    : VisClipLanes512.FirstBehind4(winding, columns, stride, 0, available, flip, lanes, out front);

                Assert.Equal(expectedHit, hit);
                if (hit >= 0)
                {
                    Assert.Equal(expectedFront, front);
                }

                for (int k = 0; k < length; k++)
                {
                    for (int j = 0; j < Math.Min(available, wide ? 8 : 4); j++)
                    {
                        Assert.Equal(
                            BitConverter.SingleToInt32Bits(expectedDists[k, j]),
                            BitConverter.SingleToInt32Bits(lanes[(k * VisClipLanes512.BatchLanes) + j]));
                    }
                }
            }

            hits += expectedHit >= 0 && expectedFront ? 1 : 0;
            empties += expectedHit >= 0 && !expectedFront ? 1 : 0;
            misses += expectedHit < 0 ? 1 : 0;
        }

        Assert.True(hits > 300, $"hits {hits}");
        Assert.True(empties > 100, $"empties {empties}");
        Assert.True(misses > 100, $"misses {misses}");
    }

    [Theory]
    [InlineData(81, false)]
    [InlineData(82, false)]
    [InlineData(81, true)]
    [InlineData(82, true)]
    public void ABatchedClipAppliesAnyPlaneListAsTheScalarClipDoes(int seed, bool wideBatch)
    {
        // The batch walk against the scalar one-plane-at-a-time clip on plane
        // lists no derivation would produce: long lists, planes that cut the
        // target one after another (so a batch restarts after every cut and
        // later lanes of a batch are judged on the NEW winding), planes
        // behind the ORIGINAL target but not the cut one, repeats, and lists
        // whose lengths end a batch on every remainder. The list is filled by
        // hand and marked fully derived, so the clip reads only these planes.
        Random random = new(seed);
        int survived = 0;
        int blocked = 0;
        int cuts = 0;
        for (int trial = 0; trial < 1500; trial++)
        {
            Vec3[] target = trial % 2 == 0
                ? Polygon(random, 3 + random.Next(10), quantize: trial % 4 == 0)
                : Polygon(random, 3 + random.Next(VisClip.MaxPointsOnWinding - 2), quantize: false);
            int count = random.Next(41);
            Vec3[] normals = new Vec3[count];
            float[] distances = new float[count];
            Vec3 centre = Centroid(target);
            for (int j = 0; j < count; j++)
            {
                Vec3 normal = RandomUnit(random, axial: j % 5 == 0);
                float through = Vec3.Dot(centre, normal);
                (normals[j], distances[j]) = (random.Next(10), j) switch
                {
                    (< 6, _) => (normal, through - 500f - random.NextSingle()),
                    (< 8, _) => (normal, through + Next(random, 20f)),
                    (8, > 0) => (normals[j - 1], distances[j - 1]),
                    (8, _) => (normal, through + 2000f),
                    _ => Plane(random, target, trial + j),
                };
            }

            foreach (bool flipClip in new[] { false, true })
            {
                Vec3[] eager = new Vec3[VisClip.MaxPointsOnWinding];
                bool eagerSurvived = VisClip.ClipToSeparatorPlanes(normals, distances, target, flipClip, eager, out int eagerCount);

                int stride = VisSeparatorColumns.StrideFor(count);
                VisSeparatorColumns memo = new(new float[4 * stride], stride);
                for (int j = 0; j < count; j++)
                {
                    memo.Planes[j] = normals[j].X;
                    memo.Planes[stride + j] = normals[j].Y;
                    memo.Planes[(2 * stride) + j] = normals[j].Z;
                    memo.Planes[(3 * stride) + j] = distances[j];
                }

                // Garbage past the list, which the batches load and must
                // ignore: planes every point is far behind.
                for (int j = count; j < stride; j++)
                {
                    memo.Planes[j] = 1f;
                    memo.Planes[(3 * stride) + j] = flipClip ? -1e30f : 1e30f;
                }

                memo.Count = count;
                memo.NextEdge = 1;

                Vec3[] batched = new Vec3[VisClip.MaxPointsOnWinding];
                bool batchedSurvived = VisClipLanes512.ClipToSeparators(
                    ref memo, [Vec3.Zero], [Vec3.Zero], target, flipClip, wideBatch, batched, out int batchedCount);

                Assert.Equal(eagerSurvived, batchedSurvived);
                Assert.Equal(count, memo.Count);
                Assert.Equal(0, memo.Derivations);
                if (eagerSurvived)
                {
                    Assert.Equal(eagerCount, batchedCount);
                    AssertSameBits(eager.AsSpan(0, eagerCount), batched.AsSpan(0, batchedCount));
                    survived++;
                    cuts += eagerCount != target.Length ? 1 : 0;
                }
                else
                {
                    blocked++;
                }
            }
        }

        Assert.True(survived > 300, $"survived {survived}");
        Assert.True(blocked > 300, $"blocked {blocked}");
        Assert.True(cuts > 200, $"cut {cuts}");
    }

    // ---------------------------------------------------------------- columns

    [Fact]
    public void ColumnsAndARunRefuseRoomThatDoesNotFit()
    {
        Vec3[] square = [new(0f, 0f, 0f), new(16f, 0f, 0f), new(16f, 16f, 0f), new(0f, 16f, 0f)];
        Vec3[] far = [new(0f, 0f, 64f), new(0f, 16f, 64f), new(16f, 16f, 64f), new(16f, 0f, 64f)];

        // The stride is the room plus a whole batch, rounded to batches.
        Assert.Equal(8, VisSeparatorColumns.StrideFor(0));
        Assert.Equal(16, VisSeparatorColumns.StrideFor(1));
        Assert.Equal(16, VisSeparatorColumns.StrideFor(8));
        Assert.Equal(24, VisSeparatorColumns.StrideFor(9));
        Assert.Equal(24, VisSeparatorColumns.StrideFor(16));
        for (int room = 0; room <= 300; room++)
        {
            int stride = VisSeparatorColumns.StrideFor(room);
            Assert.True(stride >= room + VisSeparatorColumns.BatchPadding && stride % VisSeparatorColumns.BatchPadding == 0);
        }

        Assert.Throws<ArgumentException>(() => new VisSeparatorColumns(new float[(4 * 16) - 1], 16));
        Assert.Throws<ArgumentException>(() => new VisSeparatorColumns(new float[4 * 12], 12));
        Assert.Throws<ArgumentException>(() => new VisSeparatorColumns(new float[0], 0));

        // A run adds at most one plane per edge and pass vertex; columns
        // without that room, or longer than the array, are refused before
        // anything is written.
        Assert.Throws<ArgumentException>(() => VisClipLanes512.DeriveEdgeRun(square, far, 0, 1, new float[4 * 3], 3, 0));
        Assert.Throws<ArgumentException>(() => VisClipLanes512.DeriveEdgeRun(square, far, 0, 4, new float[4 * 15], 15, 0));
        Assert.Throws<ArgumentException>(() => VisClipLanes512.DeriveEdgeRun(square, far, 0, 2, new float[4 * 8], 8, 1));
        Assert.Throws<ArgumentException>(() => VisClipLanes512.DeriveEdgeRun(square, far, 0, 2, new float[(4 * 8) - 1], 8, 0));
        Assert.Throws<ArgumentException>(() => VisClipLanes512.DeriveEdgeRun(square, far, 0, 2, new float[4 * 8], 8, -1));
        Assert.True(VisClipLanes512.DeriveEdgeRun(square, far, 0, 1, new float[4 * 4], 4, 0) >= 0);
        Assert.True(VisClipLanes512.DeriveEdgeRun(square, far, 0, 4, new float[4 * 16], 16, 0) >= 0);
    }

    [Fact]
    public void ARunIsOneToFourEdgesInsideTheSource()
    {
        Vec3[] square = [new(0f, 0f, 0f), new(16f, 0f, 0f), new(16f, 16f, 0f), new(0f, 16f, 0f)];
        float[] planes = new float[4 * 32];
        Assert.Throws<ArgumentOutOfRangeException>(() => VisClipLanes512.DeriveEdgeRun(square, square, 0, 0, planes, 32, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => VisClipLanes512.DeriveEdgeRun(square, square, 0, 5, planes, 32, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => VisClipLanes512.DeriveEdgeRun(square, square, -1, 1, planes, 32, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => VisClipLanes512.DeriveEdgeRun(square, square, 1, 4, planes, 32, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => VisClipLanes512.DeriveEdgeRun(square, square, 4, 1, planes, 32, 0));

        // The last edge wraps to vertex 0, and is a legal run of one.
        Assert.True(VisClipLanes512.DeriveEdgeRun(square, square, 3, 1, planes, 32, 0) >= 0);
    }

    // ---------------------------------------------------------------- derivation

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void TheRunDerivationMatchesTheScalarAndTheLaneDerivations(int seed)
    {
        // Source lengths one to nine, so the runs of four end on every
        // remainder and a list is one, two or three runs; pass lengths one
        // to sixteen, so the four-vertex chunks end on every remainder and
        // later edges' planes are held back across chunks.
        Random random = new(seed);
        int planes = 0;
        for (int sourceLength = 1; sourceLength <= 9; sourceLength++)
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

                    planes += AssertSameDerivation512(source, pass);
                    planes += AssertSameDerivation512(pass, source);
                }
            }
        }

        Assert.True(planes > 3_000, $"only {planes} planes derived");
    }

    [Fact]
    public void TheRunDerivationMatchesOnDegenerateAndNaNWindings()
    {
        Vec3 a = new(0f, 0f, 0f);
        Vec3 b = new(16f, 0f, 0f);
        Vec3 c = new(16f, 16f, 0f);
        Vec3 d = new(0f, 16f, 0f);
        Vec3[] square = [a, b, c, d];
        Vec3[] far = [new(0f, 0f, 64f), new(0f, 16f, 64f), new(16f, 16f, 64f), new(16f, 0f, 64f)];

        AssertSameDerivation512(square, square);
        AssertSameDerivation512([a, a, b, c], far);
        AssertSameDerivation512([a, b, b, c, c, d], far);
        AssertSameDerivation512(square, [far[0], far[0], far[1], far[2], far[3]]);
        AssertSameDerivation512([a, new(0.05f, 0f, 0f), new(0.05f, 0.05f, 0f)], far);
        AssertSameDerivation512([a, b, new(float.NaN, 16f, 0f), d], far);
        AssertSameDerivation512([new(float.NaN, 0f, 0f), b, c, d, new(-8f, 8f, 0f)], far);
        AssertSameDerivation512(square, [far[0], new(0f, float.NaN, 64f), far[2], far[3]]);
        AssertSameDerivation512([a], far);
        AssertSameDerivation512([a, b], far);
        AssertSameDerivation512(square, [far[0]]);
        AssertSameDerivation512([], far);
    }

    [Fact]
    public void ACrossProductWhoseSquaredLengthIsExactlyTheEpsilonIsDegenerateInEveryQuarter()
    {
        (float s, float q) = SquaresSummingToTheEpsilon();
        Vec3[] source = [new(0f, 0f, 0f), new(1f, 0f, 0f), new(0.5f, 10f, 0f)];
        Vec3[] pass = [new(0f, q, s), new(0f, -10f, 10f), new(5f, -10f, 10f)];

        Vec3[] normals = new Vec3[9];
        float[] distances = new float[9];
        int scalar = VisClip.BuildSeparators(source, pass, normals, distances);
        Assert.DoesNotContain(normals[..scalar], n => n.X == 0f && n.Y < 0f && n.Z > 0f);

        AssertSameDerivation512(source, pass);

        // The degenerate edge in each quarter of a run in turn: rotate the
        // source so the edge (0,0,0)-(1,0,0) is the run's first, second,
        // third edge.
        AssertSameDerivation512([source[2], source[0], source[1]], pass);
        AssertSameDerivation512([source[1], source[2], source[0]], pass);
        AssertSameDerivation512([new(0.5f, 12f, 0f), new(0.25f, 11f, 0f), source[0], source[1], source[2]], pass);
    }

    [Fact]
    public void AFlippedPlaneThroughTheOriginKeepsTheNegativeZeroDistance()
    {
        Vec3[] source = [new(16f, 0f, 0f), new(0f, 0f, 0f), new(0f, 16f, 0f), new(16f, 16f, 0f)];
        Vec3[] pass = [new(0f, 0f, 64f), new(16f, -16f, 64f), new(0f, -16f, 64f)];

        Vec3[] normals = new Vec3[12];
        float[] distances = new float[12];
        int scalar = VisClip.BuildSeparators(source, pass, normals, distances);
        Assert.Contains(
            distances[..scalar], d => BitConverter.SingleToInt32Bits(d) == BitConverter.SingleToInt32Bits(-0f));

        AssertSameDerivation512(source, pass);
    }

    [Theory]
    [InlineData(41)]
    [InlineData(42)]
    [InlineData(43)]
    public void ARunWritesWhatItsSingleEdgesWrite(int seed)
    {
        // Every source length from one to nine, every start, every run length
        // that fits, after a prefix the run must leave alone: the planes of
        // edges i .. i+edges-1, edge-major, exactly as that many
        // VisClipLanes.DeriveEdge calls write them.
        Random random = new(seed);
        int planes = 0;
        for (int sourceLength = 1; sourceLength <= 9; sourceLength++)
        {
            for (int trial = 0; trial < 12; trial++)
            {
                int passLength = 1 + random.Next(12);
                (Vec3[] source, Vec3[] pass) = trial % 3 == 2
                    ? (Scatter(random, sourceLength), Scatter(random, passLength))
                    : FacingPair(random, sourceLength, passLength, quantize: trial % 2 == 0);
                for (int i = 0; i < sourceLength; i++)
                {
                    for (int edges = 1; edges <= Math.Min(4, sourceLength - i); edges++)
                    {
                        planes += AssertRunMatchesSingles(source, pass, i, edges, alreadyFound: trial % 4);
                    }
                }
            }
        }

        Assert.True(planes > 2_000, $"only {planes} planes derived");
    }

    [Theory]
    [InlineData(91)]
    [InlineData(92)]
    public void ARunSkipsEachEdgesOwnEndpointsFarFromTheOrigin(int seed)
    {
        // Each quarter's source scan must pass over ITS edge's endpoints and
        // no others. A million units out, rounding puts an edge's own
        // endpoints past the epsilon of its plane, so a quarter that skipped
        // another quarter's endpoints (or none) would decide its side from
        // the wrong point. See VisClipLanesTests' pair fact for the argument.
        Random random = new(seed);
        int planes = 0;
        for (int trial = 0; trial < 300; trial++)
        {
            int sourceLength = 3 + random.Next(6);
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

            for (int i = 0; i < sourceLength; i++)
            {
                planes += AssertRunMatchesSingles(source, pass, i, Math.Min(4, sourceLength - i), alreadyFound: 0);
            }
        }

        Assert.True(planes > 500, $"only {planes} planes derived");
    }

    // ---------------------------------------------------------------- the clip, both paths

    [Theory]
    [InlineData(21, false)]
    [InlineData(22, false)]
    [InlineData(21, true)]
    [InlineData(22, true)]
    public void TheWholeListClipMatchesTheLazyEagerAndFusedClips(int seed, bool wideBatch)
    {
        // One list per ordering per path, shared by a run of targets the way
        // a frame's candidates share them: this path's list is whole after
        // the first target, main's is as far as the targets so far reached.
        // Every target's clip gives the same answer and the same bits
        // through the scalar list, the scalar fused clip, main's lazy clip
        // and this path's.
        Random random = new(seed);
        int survived = 0;
        int blocked = 0;

        for (int pairing = 0; pairing < 300; pairing++)
        {
            int sourceLength = 3 + random.Next(7);
            int passLength = 3 + random.Next(7);
            (Vec3[] source, Vec3[] pass) = FacingPair(random, sourceLength, passLength, quantize: pairing % 2 == 0);

            VisSeparatorColumns forward = NewColumns(source, pass);
            VisSeparatorColumns reverse = NewColumns(pass, source);
            VisSeparatorMemo lazyForward = NewMemo256(source, pass);
            VisSeparatorMemo lazyReverse = NewMemo256(pass, source);

            for (int t = 0; t < 8; t++)
            {
                Vec3[] target = Beyond(random, source, pass, 3 + random.Next(10));

                if (!AssertSameClips(ref forward, ref lazyForward, source, pass, target, flipClip: false, wideBatch, out Vec3[] first))
                {
                    blocked++;
                    continue;
                }

                if (AssertSameClips(ref reverse, ref lazyReverse, pass, source, first, flipClip: true, wideBatch, out _))
                {
                    survived++;
                }
                else
                {
                    blocked++;
                }
            }

            // Each list was derived once, at its first clip, in runs of four.
            int runs = (sourceLength + 3) / 4;
            Assert.Equal(runs, forward.Derivations);
            Assert.True(reverse.Derivations is 0 || reverse.Derivations == (passLength + 3) / 4);
        }

        Assert.True(survived > 100, $"survived {survived}");
        Assert.True(blocked > 100, $"blocked {blocked}");
    }

    [Fact]
    public void AClipWithoutRoomForAWholeWindingIsRefused()
    {
        VisSeparatorColumns memo = new(new float[4 * 16], 16);
        Assert.Throws<ArgumentException>(() => VisClipLanes512.ClipToSeparators(
            ref memo, [], [], [], false, true, new Vec3[VisClip.MaxPointsOnFixedWinding], out _));
        Assert.Throws<ArgumentException>(() => VisClipLanes512.ClipToSeparators(
            ref memo, [], [], new Vec3[VisClip.MaxPointsOnWinding + 1], false, true, new Vec3[VisClip.MaxPointsOnWinding], out _));
    }

    [Fact]
    public void AClipWithNoSeparatorsHandsTheTargetBack()
    {
        Vec3[] square = [new(0f, 0f, 0f), new(16f, 0f, 0f), new(16f, 16f, 0f), new(0f, 16f, 0f)];
        VisSeparatorColumns memo = NewColumns(square, square);
        Vec3[] result = new Vec3[VisClip.MaxPointsOnWinding];

        Assert.True(VisClipLanes512.ClipToSeparators(ref memo, square, square, square, false, true, result, out int count));
        Assert.Equal(square, result[..count]);
        Assert.Equal(0, memo.Count);
        Assert.Equal(square.Length, memo.NextEdge);
        Assert.Equal(1, memo.Derivations);
    }

    // ---------------------------------------------------------------- work counts

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AClipThatEndsAtTheFirstPlaneStillDerivesTheWholeListInOneRun(bool wideBatch)
    {
        // The counterpart of VisClipLanesTests.AClipThatEndsAtTheFirstPlane-
        // DerivesNoFurther. Main's path stops after the first edge; this one
        // derives a quad's four edges in the one run it would have needed for
        // the first, so the whole list is there after the first clip, from one
        // derivation -- and a later clip of the same frame derives nothing.
        Vec3[] source = [new(0f, 0f, 0f), new(16f, 0f, 0f), new(16f, 16f, 0f), new(0f, 16f, 0f)];
        Vec3[] pass = [new(-8f, -8f, 64f), new(-8f, 24f, 64f), new(24f, 24f, 64f), new(24f, -8f, 64f)];
        Vec3[] normals = new Vec3[16];
        float[] distances = new float[16];
        int all = VisClip.BuildSeparators(source, pass, normals, distances);
        Assert.True(all >= 4, $"{all} planes");

        Vec3 origin = normals[0] * (distances[0] - 1000f);
        Vec3[] target = [origin, origin + new Vec3(0.5f, 0f, 0f), origin + new Vec3(0f, 0.5f, 0f)];

        VisSeparatorColumns memo = NewColumns(source, pass);
        VisSeparatorMemo lazy = NewMemo256(source, pass);
        Assert.False(AssertSameClips(ref memo, ref lazy, source, pass, target, flipClip: false, wideBatch, out _));
        Assert.Equal(1, memo.Derivations);
        Assert.Equal(source.Length, memo.NextEdge);
        Assert.Equal(all, memo.Count);
        Assert.True(lazy.NextEdge < source.Length, "main's path derives only what the clip reached");

        Vec3 middle = new(8f, 8f, 256f);
        Vec3[] visible = [middle, middle + new Vec3(0.25f, 0f, 0f), middle + new Vec3(0f, 0.25f, 0f)];
        Assert.True(AssertSameClips(ref memo, ref lazy, source, pass, visible, flipClip: false, wideBatch, out _));
        Assert.Equal(1, memo.Derivations);
        Assert.Equal(all, memo.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AClipThatEndsInsideTheFirstEdgesDerivesEveryEdgeInRunsOfFour(bool firstPlaneOfTheSecondEdge, bool wideBatch)
    {
        // The counterpart of VisClipLanesTests.AClipThatEndsInsideAPair-
        // DerivesOnlyThatPair, on the same five-edge source: where main's path
        // stops after edge 0 or the pair 0-1, this one has derived all five
        // edges in two runs (four, then one) by the time the killing plane is
        // read, and a target that survives every plane afterwards derives
        // nothing more. The answers are main's either way.
        Vec3[] source = [new(0f, 0f, 0f), new(16f, 0f, 0f), new(16f, 16f, 0f), new(0f, 16f, 0f), new(-8f, 8f, 0f)];
        Vec3[] pass = [new(-8f, -8f, 64f), new(-8f, 24f, 64f), new(24f, 24f, 64f), new(24f, -8f, 64f)];
        int room = source.Length * pass.Length;
        Vec3[] normals = new Vec3[room];
        float[] distances = new float[room];
        int all = VisClip.BuildSeparators(source, pass, normals, distances);
        int edge0 = VisClipLanes.DeriveEdge(source, pass, 0, new Vec3[room], new float[room], 0);
        Assert.True(edge0 > 0 && all > edge0, $"edge 0: {edge0}, all: {all}");

        int killer = firstPlaneOfTheSecondEdge ? edge0 : 0;
        Vec3[] dead = TargetKilledAt(normals, distances, killer, new Random(61));

        VisSeparatorColumns memo = NewColumns(source, pass);
        VisSeparatorMemo lazy = NewMemo256(source, pass);
        Assert.False(AssertSameClips(ref memo, ref lazy, source, pass, dead, flipClip: false, wideBatch, out _));
        Assert.Equal(2, memo.Derivations);
        Assert.Equal(source.Length, memo.NextEdge);
        Assert.Equal(all, memo.Count);
        Assert.True(lazy.NextEdge < source.Length);

        Vec3 middle = new(8f, 8f, 256f);
        Vec3[] visible = [middle, middle + new Vec3(0.25f, 0f, 0f), middle + new Vec3(0f, 0.25f, 0f)];
        Assert.True(AssertSameClips(ref memo, ref lazy, source, pass, visible, flipClip: false, wideBatch, out _));
        Assert.Equal(2, memo.Derivations);
        Assert.Equal(all, memo.Count);
        Assert.Equal(source.Length, lazy.NextEdge);
    }

    [Fact]
    public void AListOfNEdgesIsDerivedInNOverFourRoundedUpRunsAtItsFirstClip()
    {
        // The whole work count, for every source length to twelve: the first
        // clip makes ceil(n / 4) derivations and every later one none.
        Random random = new(101);
        for (int sourceLength = 1; sourceLength <= 12; sourceLength++)
        {
            (Vec3[] source, Vec3[] pass) = FacingPair(random, sourceLength, 4, quantize: true);
            VisSeparatorColumns memo = NewColumns(source, pass);
            Assert.Equal(0, memo.Derivations);

            Vec3[] result = new Vec3[VisClip.MaxPointsOnWinding];
            for (int t = 0; t < 3; t++)
            {
                VisClipLanes512.ClipToSeparators(
                    ref memo, source, pass, Beyond(random, source, pass, 4), false, true, result, out _);
                Assert.Equal((sourceLength + 3) / 4, memo.Derivations);
                Assert.Equal(sourceLength, memo.NextEdge);
            }
        }
    }

    [Fact]
    public void AListNoClipReachesIsNeverDerived()
    {
        // A frame whose candidates all die in the forward clip never runs a
        // reverse clip, and so never derives the reverse list: the list is
        // derived by a clip, not by being set up.
        Vec3[] source = [new(0f, 0f, 0f), new(16f, 0f, 0f), new(16f, 16f, 0f), new(0f, 16f, 0f)];
        VisSeparatorColumns memo = NewColumns(source, source);
        Assert.Equal(0, memo.Derivations);
        Assert.Equal(0, memo.NextEdge);
        Assert.Equal(0, memo.Count);

        // A source of no points has no edges: a clip derives nothing.
        VisSeparatorColumns none = new(new float[4 * 16], 16);
        Vec3[] result = new Vec3[VisClip.MaxPointsOnWinding];
        Assert.True(VisClipLanes512.ClipToSeparators(ref none, [], source, source, false, false, result, out int count));
        Assert.Equal(source.Length, count);
        Assert.Equal(0, none.Derivations);
    }

    // ---------------------------------------------------------------- helpers

    private static VisSeparatorColumns NewColumns(Vec3[] source, Vec3[] pass)
    {
        int stride = VisSeparatorColumns.StrideFor(source.Length * pass.Length);
        return new VisSeparatorColumns(new float[4 * stride], stride);
    }

    private static VisSeparatorMemo NewMemo256(Vec3[] source, Vec3[] pass) =>
        new(new Vec3[source.Length * pass.Length], new float[source.Length * pass.Length]);

    private static Vec3[] Normals(float[] columns, int stride, int count)
    {
        Vec3[] normals = new Vec3[count];
        for (int i = 0; i < count; i++)
        {
            normals[i] = new Vec3(columns[i], columns[stride + i], columns[(2 * stride) + i]);
        }

        return normals;
    }

    private static float[] Distances(float[] columns, int stride, int count) =>
        columns.AsSpan(3 * stride, count).ToArray();

    private static VisChopResult AssertSameChop512(Vec3[] winding, Vec3 normal, float distance)
    {
        Vec3[] scalarOut = new Vec3[VisClip.MaxPointsOnFixedWinding];
        VisChopResult scalar = VisClip.ChopWinding(winding, normal, distance, scalarOut, out int scalarCount);

        foreach (bool wide in new[] { false, true })
        {
            Vec3[] batchOut = new Vec3[VisClip.MaxPointsOnFixedWinding];
            VisChopResult batch = VisClipLanes512.ChopWinding(winding, normal, distance, wide, batchOut, out int batchCount);
            Assert.Equal(scalar, batch);
            Assert.Equal(scalarCount, batchCount);
            if (scalar == VisChopResult.Clipped)
            {
                AssertSameBits(scalarOut.AsSpan(0, scalarCount), batchOut.AsSpan(0, batchCount));
            }
        }

        return scalar;
    }

    /// <summary>
    /// The scalar list, main's lane list and this path's run list: the same
    /// planes, in the same order, to the bit.
    /// </summary>
    private static int AssertSameDerivation512(Vec3[] source, Vec3[] pass)
    {
        int room = Math.Max(1, source.Length * pass.Length);
        Vec3[] scalarNormals = new Vec3[room];
        float[] scalarDistances = new float[room];
        Vec3[] laneNormals = new Vec3[room];
        float[] laneDistances = new float[room];
        int stride = VisSeparatorColumns.StrideFor(room);
        float[] columns = new float[4 * stride];

        int scalar = VisClip.BuildSeparators(source, pass, scalarNormals, scalarDistances);
        int lanes = VisClipLanes.BuildSeparators(source, pass, laneNormals, laneDistances);
        int runs = VisClipLanes512.BuildSeparators(source, pass, columns, stride);

        Assert.Equal(scalar, lanes);
        Assert.Equal(scalar, runs);
        AssertSameBits(scalarNormals.AsSpan(0, scalar), Normals(columns, stride, runs));
        AssertSameBits(laneNormals.AsSpan(0, lanes), Normals(columns, stride, runs));
        AssertSameDistanceBits(scalarDistances.AsSpan(0, scalar), Distances(columns, stride, runs));
        AssertSameDistanceBits(laneDistances.AsSpan(0, lanes), Distances(columns, stride, runs));
        return scalar;
    }

    private static int AssertRunMatchesSingles(Vec3[] source, Vec3[] pass, int i, int edges, int alreadyFound)
    {
        int room = alreadyFound + (edges * Math.Max(1, pass.Length));
        Vec3[] singleNormals = new Vec3[room];
        float[] singleDistances = new float[room];
        int stride = room;
        float[] columns = new float[4 * stride];
        for (int k = 0; k < alreadyFound; k++)
        {
            // A prefix the derivation must leave alone.
            singleNormals[k] = new Vec3(k, -k, 0.5f);
            singleDistances[k] = k;
            columns[k] = k;
            columns[stride + k] = -k;
            columns[(2 * stride) + k] = 0.5f;
            columns[(3 * stride) + k] = k;
        }

        int single = alreadyFound;
        for (int e = 0; e < edges; e++)
        {
            single = VisClipLanes.DeriveEdge(source, pass, i + e, singleNormals, singleDistances, single);
        }

        int run = VisClipLanes512.DeriveEdgeRun(source, pass, i, edges, columns, stride, alreadyFound);

        Assert.Equal(single, run);
        AssertSameBits(singleNormals.AsSpan(0, single), Normals(columns, stride, run));
        AssertSameDistanceBits(singleDistances.AsSpan(0, single), Distances(columns, stride, run));
        return single - alreadyFound;
    }

    /// <summary>
    /// One target through the scalar list, the scalar fused clip, main's lazy
    /// clip and this path's whole-list clip: the same answer and the same
    /// bits from all four, and each path's list a (whole, for this path)
    /// prefix of the scalar one.
    /// </summary>
    private static bool AssertSameClips(
        ref VisSeparatorColumns columns,
        ref VisSeparatorMemo lazy,
        Vec3[] source,
        Vec3[] pass,
        Vec3[] target,
        bool flipClip,
        bool wideBatch,
        out Vec3[] survivor)
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

        Vec3[] lazyOut = new Vec3[VisClip.MaxPointsOnWinding];
        bool lazySurvived = VisClipLanes.ClipToSeparators(
            ref lazy, source, pass, target, flipClip, lazyOut, out int lazyCount);

        Vec3[] wideOut = new Vec3[VisClip.MaxPointsOnWinding];
        bool wideSurvived = VisClipLanes512.ClipToSeparators(
            ref columns, source, pass, target, flipClip, wideBatch, wideOut, out int wideCount);

        Assert.Equal(eagerSurvived, wideSurvived);
        Assert.Equal(fusedSurvived, wideSurvived);
        Assert.Equal(lazySurvived, wideSurvived);

        // This path's list is whole after any clip; main's is a prefix.
        Assert.Equal(planes, columns.Count);
        Assert.Equal(source.Length, columns.NextEdge);
        AssertSameBits(normals.AsSpan(0, planes), Normals(columns.Planes, columns.Stride, columns.Count));
        AssertSameDistanceBits(distances.AsSpan(0, planes), Distances(columns.Planes, columns.Stride, columns.Count));
        Assert.True(lazy.Count <= planes);
        AssertSameBits(lazy.Normals.AsSpan(0, lazy.Count), Normals(columns.Planes, columns.Stride, lazy.Count));

        survivor = [];
        if (wideSurvived)
        {
            Assert.Equal(eagerCount, wideCount);
            Assert.Equal(fusedCount, wideCount);
            Assert.Equal(lazyCount, wideCount);
            AssertSameBits(eager.AsSpan(0, eagerCount), wideOut.AsSpan(0, wideCount));
            AssertSameBits(fused.AsSpan(0, fusedCount), wideOut.AsSpan(0, wideCount));
            AssertSameBits(lazyOut.AsSpan(0, lazyCount), wideOut.AsSpan(0, wideCount));
            survivor = wideOut[..wideCount];
        }

        return wideSurvived;
    }
}
