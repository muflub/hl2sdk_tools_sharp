using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Vis;

/// <summary>
/// <c>ChopWinding</c> and <c>ClipToSeperators</c>.
/// </summary>
public class VisClipTests
{
    private static readonly Vec3[] UnitSquareAtZeroZ =
    [
        new(0f, 0f, 0f),
        new(16f, 0f, 0f),
        new(16f, 16f, 0f),
        new(0f, 16f, 0f),
    ];

    [Fact]
    public void AWindingEntirelyInFrontIsUnchanged()
    {
        Span<Vec3> output = new Vec3[VisClip.MaxPointsOnFixedWinding];

        VisChopResult result = VisClip.ChopWinding(
            UnitSquareAtZeroZ, new Vec3(0f, 0f, 1f), -10f, output, out int count);

        Assert.Equal(VisChopResult.Unchanged, result);
        Assert.Equal(4, count);
    }

    [Fact]
    public void AWindingEntirelyBehindIsEmpty()
    {
        Span<Vec3> output = new Vec3[VisClip.MaxPointsOnFixedWinding];

        VisChopResult result = VisClip.ChopWinding(
            UnitSquareAtZeroZ, new Vec3(0f, 0f, 1f), 10f, output, out int count);

        Assert.Equal(VisChopResult.Empty, result);
        Assert.Equal(0, count);
    }

    [Fact]
    public void AWindingCrossingThePlaneIsClipped()
    {
        Span<Vec3> output = new Vec3[VisClip.MaxPointsOnFixedWinding];

        VisChopResult result = VisClip.ChopWinding(
            UnitSquareAtZeroZ, new Vec3(1f, 0f, 0f), 8f, output, out int count);

        Assert.Equal(VisChopResult.Clipped, result);
        Assert.Equal(4, count);
    }

    [Fact]
    public void TheClippedWindingKeepsOnlyTheFrontSide()
    {
        Span<Vec3> output = new Vec3[VisClip.MaxPointsOnFixedWinding];

        VisClip.ChopWinding(UnitSquareAtZeroZ, new Vec3(1f, 0f, 0f), 8f, output, out int count);

        for (int i = 0; i < count; i++)
        {
            Assert.True(output[i].X >= 8f, $"point {i} is at x={output[i].X}");
        }
    }

    [Fact]
    public void APointWithinTheEpsilonCountsAsOnThePlane()
    {
        // 0.005 is inside ON_VIS_EPSILON, so this winding is "entirely in
        // front" even though one corner is technically behind -- there is no
        // back point to make counts[SIDE_BACK] non-zero.
        Vec3[] winding =
        [
            new(0f, 0f, 0f),
            new(16f, 0f, 0f),
            new(16f, 16f, 0f),
            new(0f, 16f, 0f),
        ];

        Span<Vec3> output = new Vec3[VisClip.MaxPointsOnFixedWinding];
        VisChopResult result = VisClip.ChopWinding(
            winding, new Vec3(0f, 0f, 1f), 0.005f, output, out _);

        Assert.Equal(VisChopResult.Unchanged, result);
    }

    [Fact]
    public void JustOutsideTheEpsilonTheWindingIsGone()
    {
        Span<Vec3> output = new Vec3[VisClip.MaxPointsOnFixedWinding];

        VisChopResult result = VisClip.ChopWinding(
            UnitSquareAtZeroZ, new Vec3(0f, 0f, 1f), 0.02f, output, out _);

        Assert.Equal(VisChopResult.Empty, result);
    }

    [Fact]
    public void AnAxisAlignedPlaneGivesASplitPointExactlyOnIt()
    {
        //. The interpolation for this case lands at
        // 3.1000004; the special case assigns the plane distance itself.
        Vec3[] winding =
        [
            new(-10f, 0f, 0f),
            new(10f, 0f, 0f),
            new(10f, 4f, 0f),
            new(-10f, 4f, 0f),
        ];

        Span<Vec3> output = new Vec3[VisClip.MaxPointsOnFixedWinding];
        VisClip.ChopWinding(winding, new Vec3(1f, 0f, 0f), 3.1f, output, out int count);

        for (int i = 0; i < count; i++)
        {
            if (output[i].X != 10f)
            {
                Assert.Equal(3.1f, output[i].X);
            }
        }
    }

    [Fact]
    public void ThatAxisAlignedCaseIsOneInterpolationWouldGetWrong()
    {
        // The check that cannot fail is worth nothing: if plain interpolation
        // already landed on the plane, the fact above would pass with the
        // special case deleted. This is the arithmetic it is protecting
        // against, spelled out.
        const float a = -10f;
        const float b = 10f;
        const float dist = 3.1f;

        float da = a - dist;
        float db = b - dist;
        float dot = da / (da - db);
        float interpolated = a + (dot * (b - a));

        Assert.NotEqual(dist, interpolated);
    }

    [Fact]
    public void ANegativeAxisAlignedPlaneGivesTheNegatedDistance()
    {
        Vec3[] winding =
        [
            new(10f, 0f, 0f),
            new(-10f, 0f, 0f),
            new(-10f, 4f, 0f),
            new(10f, 4f, 0f),
        ];

        Span<Vec3> output = new Vec3[VisClip.MaxPointsOnFixedWinding];
        VisClip.ChopWinding(winding, new Vec3(-1f, 0f, 0f), -3.1f, output, out int count);

        for (int i = 0; i < count; i++)
        {
            if (output[i].X != -10f)
            {
                Assert.Equal(3.1f, output[i].X);
            }
        }
    }

    [Fact]
    public void AChopThatWouldNeedThirteenPointsKeepsTheOriginal()
    {
        //. A 24-gon cut in half needs 14 points, which does
        // not fit a MAX_POINTS_ON_FIXED_WINDING winding, so stock hands back
        // the UNCUT polygon -- a more conservative answer, never a smaller one.
        Vec3[] circle = new Vec3[24];
        for (int i = 0; i < circle.Length; i++)
        {
            double angle = 2 * Math.PI * i / circle.Length;
            circle[i] = new Vec3((float)(100 * Math.Cos(angle)), (float)(100 * Math.Sin(angle)), 0f);
        }

        Span<Vec3> output = new Vec3[VisClip.MaxPointsOnFixedWinding];
        VisChopResult result = VisClip.ChopWinding(
            circle, new Vec3(1f, 0f, 0f), 0f, output, out int count);

        Assert.Equal(VisChopResult.Unchanged, result);
        Assert.Equal(circle.Length, count);
    }

    [Fact]
    public void AWindingWithMoreThanSixtyFourPointsIsRefused()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            Span<Vec3> output = new Vec3[VisClip.MaxPointsOnFixedWinding];
            VisClip.ChopWinding(new Vec3[65], new Vec3(1f, 0f, 0f), 0f, output, out _);
        });
    }

    [Fact]
    public void AChopWithoutRoomForTwelvePointsIsRefused()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            Span<Vec3> output = new Vec3[VisClip.MaxPointsOnFixedWinding - 1];
            VisClip.ChopWinding(UnitSquareAtZeroZ, new Vec3(1f, 0f, 0f), 0f, output, out _);
        });
    }

    [Fact]
    public void TheEpsilonIsTheDoubleTheHeaderSpells()
    {
        // spells it 0.01 with no `f`. That is not a typographic
        // detail: the two values bracket a float, and compares a
        // float against it with `<`.
        Assert.Equal(0.01d, VisClip.OnVisEpsilon);
        Assert.NotEqual((double)0.01f, VisClip.OnVisEpsilon);
    }

    [Fact]
    public void TheEpsilonDecidesTheSquaredLengthTestDifferentlyInFloat()
    {
        // The case the double matters for: a squared cross-product length of
        // exactly 0.01f is BELOW the epsilon as C compares it, and not below it
        // if the constant were narrowed to a float first.
        const float length = 0.01f;

        Assert.True(length < VisClip.OnVisEpsilon);
        Assert.False(length < 0.01f);
    }

    [Fact]
    public void NegatingAZeroComponentGivesPositiveZero()
    {
        // VectorSubtract(vec3_origin, v, v) is 0 - x, which is +0 for x = 0.
        // C#'s unary minus gives -0. Nothing downstream can currently tell them
        // apart, which is exactly why the difference has to be written down
        // rather than discovered later.
        Vec3 negated = VisClip.Negate(new Vec3(0f, 1f, -2f));

        Assert.False(float.IsNegative(negated.X));
        Assert.Equal(-1f, negated.Y);
        Assert.Equal(2f, negated.Z);
    }

    [Fact]
    public void WithNoSeparatorTheTargetComesBackWhole()
    {
        // Two coincident portals cannot separate anything, so the target must
        // survive untouched -- including a target with more points than a
        // chopped winding may hold, which is why the result buffer is sized for
        // MaxPointsOnWinding.
        Vec3[] source = [.. UnitSquareAtZeroZ];
        Vec3[] pass = [.. UnitSquareAtZeroZ];
        Vec3[] target = [.. UnitSquareAtZeroZ];

        Span<Vec3> result = new Vec3[VisClip.MaxPointsOnWinding];
        bool survived = VisClip.ClipToSeparators(source, pass, target, false, result, out int count);

        Assert.True(survived);
        Assert.Equal(target.Length, count);
    }

    [Fact]
    public void ATargetBehindEveryCandidatePlaneIsClippedAway()
    {
        // Source and pass are two windows of a corridor; the target sits on the
        // far side of the wall the separators run along, so no sight line
        // reaches it.
        Vec3[] source =
        [
            new(0f, 0f, 0f),
            new(0f, 16f, 0f),
            new(0f, 16f, 16f),
            new(0f, 0f, 16f),
        ];
        Vec3[] pass =
        [
            new(64f, 0f, 0f),
            new(64f, 16f, 0f),
            new(64f, 16f, 16f),
            new(64f, 0f, 16f),
        ];
        Vec3[] target =
        [
            new(128f, 400f, 0f),
            new(128f, 416f, 0f),
            new(128f, 416f, 16f),
            new(128f, 400f, 16f),
        ];

        Span<Vec3> result = new Vec3[VisClip.MaxPointsOnWinding];
        bool survived = VisClip.ClipToSeparators(source, pass, target, false, result, out _);

        Assert.False(survived);
    }

    [Fact]
    public void ATargetStraightAheadSurvivesTheSeparators()
    {
        Vec3[] source =
        [
            new(0f, 0f, 0f),
            new(0f, 16f, 0f),
            new(0f, 16f, 16f),
            new(0f, 0f, 16f),
        ];
        Vec3[] pass =
        [
            new(64f, 0f, 0f),
            new(64f, 16f, 0f),
            new(64f, 16f, 16f),
            new(64f, 0f, 16f),
        ];
        Vec3[] target =
        [
            new(128f, 0f, 0f),
            new(128f, 16f, 0f),
            new(128f, 16f, 16f),
            new(128f, 0f, 16f),
        ];

        Span<Vec3> result = new Vec3[VisClip.MaxPointsOnWinding];
        bool survived = VisClip.ClipToSeparators(source, pass, target, false, result, out int count);

        Assert.True(survived);
        Assert.True(count >= 3);
    }

    /// <summary>
    /// The corridor of <see cref="ATargetBehindEveryCandidatePlaneIsClippedAway"/>,
    /// shrunk until every separating plane's cross product is degenerate by the
    /// rule.
    /// </summary>
    /// <param name="scale">How much to shrink by.</param>
    /// <returns>The three windings.</returns>
    /// <remarks>
    /// The cross products of that configuration are about 1024 long, and a
    /// cross product scales with the SQUARE of the geometry, so a scale of
    /// 0.007 puts them at roughly 0.05 -- inside the band where the squared
    /// length is below 0.01 and above 0.01 squared. The point-to-plane
    /// distances only scale linearly, so they stay well clear of the same
    /// epsilon and every other test in the function behaves as it did at full
    /// size.
    /// </remarks>
    /// <summary>
    /// The corridor of <see cref="ATargetStraightAheadSurvivesTheSeparators"/>:
    /// three windows in a line, so real separating planes are derived and the
    /// target survives them.
    /// </summary>
    /// <returns>The three windings.</returns>
    private static (Vec3[] Source, Vec3[] Pass, Vec3[] Target) StraightCorridor() =>
        (
            [new(0f, 0f, 0f), new(0f, 16f, 0f), new(0f, 16f, 16f), new(0f, 0f, 16f)],
            [new(64f, 0f, 0f), new(64f, 16f, 0f), new(64f, 16f, 16f), new(64f, 0f, 16f)],
            [new(128f, 0f, 0f), new(128f, 16f, 0f), new(128f, 16f, 16f), new(128f, 0f, 16f)]);

    private static (Vec3[] Source, Vec3[] Pass, Vec3[] Target) TinyCorridor(float scale)
    {
        Vec3[] Scaled(params Vec3[] points) =>
            [.. points.Select(p => new Vec3(p.X * scale, p.Y * scale, p.Z * scale))];

        return (
            Scaled(new(0f, 0f, 0f), new(0f, 16f, 0f), new(0f, 16f, 16f), new(0f, 0f, 16f)),
            Scaled(new(64f, 0f, 0f), new(64f, 16f, 0f), new(64f, 16f, 16f), new(64f, 0f, 16f)),
            Scaled(new(128f, 400f, 0f), new(128f, 416f, 0f), new(128f, 416f, 16f), new(128f, 400f, 16f)));
    }

    [Fact]
    public void ADegenerateSeparatorIsSkippedAndTheTargetSurvives()
    {
        //, the squared-length-versus-linear-epsilon quirk, as
        // behaviour rather than as a constant. At this scale every candidate
        // separating plane has a squared cross length below ON_VIS_EPSILON, so
        // stock skips all of them and the target is not clipped -- even though
        // the SAME configuration at full size is clipped away entirely.
        //
        // This is the only fact in the suite that reaches that comparison with a
        // value near the threshold. On real box geometry at map scale the
        // squared lengths are around a million, so the corpus cannot exercise
        // it: the threshold could be moved by eight orders of magnitude without
        // any catalogue map noticing. Measured, not assumed.
        (Vec3[] source, Vec3[] pass, Vec3[] target) = TinyCorridor(0.007f);

        Span<Vec3> result = new Vec3[VisClip.MaxPointsOnWinding];
        bool survived = VisClip.ClipToSeparators(source, pass, target, false, result, out _);

        Assert.True(survived);
    }

    [Fact]
    public void TheSameConfigurationAtFullSizeIsClippedAway()
    {
        // The control: without it, the fact above would pass just as happily
        // against a clip that never clips anything.
        (Vec3[] source, Vec3[] pass, Vec3[] target) = TinyCorridor(1f);

        Span<Vec3> result = new Vec3[VisClip.MaxPointsOnWinding];
        bool survived = VisClip.ClipToSeparators(source, pass, target, false, result, out _);

        Assert.False(survived);
    }

    [Fact]
    public void AClipWithoutRoomForAWholeWindingIsRefused()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            Span<Vec3> result = new Vec3[VisClip.MaxPointsOnFixedWinding];
            VisClip.ClipToSeparators(
                UnitSquareAtZeroZ, UnitSquareAtZeroZ, UnitSquareAtZeroZ, false, result, out _);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DerivingThePlanesFirstGivesTheSameWindingAsClippingAsItGoes(bool flipClip)
    {
        // The portal flow caches a frame's separating planes and replays them
        // for every candidate that reuses the frame's own source winding
        // (VisClip.BuildSeparators). That is only sound if the two-step route
        // is the SAME function as the fused one, on the same floats, in the
        // same order -- so this drives both and requires the windings to match
        // point for point, not merely to agree about survival.
        (Vec3[] source, Vec3[] pass, Vec3[] target) = StraightCorridor();

        Span<Vec3> fused = new Vec3[VisClip.MaxPointsOnWinding];
        bool fusedSurvived = VisClip.ClipToSeparators(
            source, pass, target, flipClip, fused, out int fusedCount);

        Span<Vec3> normals = new Vec3[source.Length * pass.Length];
        Span<float> distances = new float[source.Length * pass.Length];
        int planes = VisClip.BuildSeparators(source, pass, normals, distances);

        Span<Vec3> staged = new Vec3[VisClip.MaxPointsOnWinding];
        bool stagedSurvived = VisClip.ClipToSeparatorPlanes(
            normals[..planes], distances[..planes], target, flipClip, staged, out int stagedCount);

        Assert.Equal(fusedSurvived, stagedSurvived);
        Assert.Equal(fusedCount, stagedCount);
        Assert.Equal(fused[..fusedCount].ToArray(), staged[..stagedCount].ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheTwoRoutesAgreeWhenEverySightLineIsBlocked(bool flipClip)
    {
        // The other outcome: the fused route stops deriving planes the moment
        // the target is gone, the staged route derives them all first. Both
        // must still say the target did not survive.
        (Vec3[] source, Vec3[] pass, Vec3[] target) = TinyCorridor(1f);

        Span<Vec3> fused = new Vec3[VisClip.MaxPointsOnWinding];
        bool fusedSurvived = VisClip.ClipToSeparators(
            source, pass, target, flipClip, fused, out _);

        Span<Vec3> normals = new Vec3[source.Length * pass.Length];
        Span<float> distances = new float[source.Length * pass.Length];
        int planes = VisClip.BuildSeparators(source, pass, normals, distances);

        Span<Vec3> staged = new Vec3[VisClip.MaxPointsOnWinding];
        bool stagedSurvived = VisClip.ClipToSeparatorPlanes(
            normals[..planes], distances[..planes], target, flipClip, staged, out _);

        Assert.False(fusedSurvived);
        Assert.Equal(fusedSurvived, stagedSurvived);
    }

    [Fact]
    public void TwoCoincidentPortalsSeparateWithNoPlanesAtAll()
    {
        Span<Vec3> normals = new Vec3[16];
        Span<float> distances = new float[16];

        int planes = VisClip.BuildSeparators(
            UnitSquareAtZeroZ, UnitSquareAtZeroZ, normals, distances);

        Assert.Equal(0, planes);
    }

    [Fact]
    public void ADerivationWithNowhereToPutThePlanesSaysSoRatherThanTruncating()
    {
        // The frame cache is finite (VisFrameStack.MaxCachedSeparators) and the
        // flow falls back to the fused route when a pairing will not fit. A
        // silent truncation here would drop separating planes and let sight
        // lines through, so the overflow has to be reported.
        (Vec3[] source, Vec3[] pass, _) = StraightCorridor();

        Span<Vec3> normals = new Vec3[1];
        Span<float> distances = new float[1];

        int planes = VisClip.BuildSeparators(source, pass, normals, distances);

        Assert.Equal(-1, planes);
    }

    [Fact]
    public void ADerivationWithMismatchedPlaneArraysIsRefused()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            Span<Vec3> normals = new Vec3[4];
            Span<float> distances = new float[3];
            VisClip.BuildSeparators(UnitSquareAtZeroZ, UnitSquareAtZeroZ, normals, distances);
        });
    }

    [Fact]
    public void AReplayWithMismatchedPlaneArraysIsRefused()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            Span<Vec3> normals = new Vec3[4];
            Span<float> distances = new float[3];
            Span<Vec3> result = new Vec3[VisClip.MaxPointsOnWinding];
            VisClip.ClipToSeparatorPlanes(
                normals, distances, UnitSquareAtZeroZ, false, result, out _);
        });
    }

    [Fact]
    public void AReplayWithoutRoomForAWholeWindingIsRefused()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            Span<Vec3> result = new Vec3[VisClip.MaxPointsOnFixedWinding];
            VisClip.ClipToSeparatorPlanes(
                [], [], UnitSquareAtZeroZ, false, result, out _);
        });
    }
}
