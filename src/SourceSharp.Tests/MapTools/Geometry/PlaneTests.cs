using SourceSharp.MapFormats.Geometry;

using Xunit;

namespace SourceSharp.Tests.MapTools.Geometry;

/// <summary>
/// The plane predicates vbsp's plane table is built from: the classification,
/// the snapping,
/// the tolerant match, and the hash.
/// </summary>
public class PlaneTests
{
    [Fact]
    public void TypeIsXForAnExactlyPositiveXNormal() =>
        Assert.Equal(PlaneType.X, new Plane(new Vec3(1f, 0f, 0f), 0f).Type);

    [Fact]
    public void TypeIsXForAnExactlyNegativeXNormal() =>
        Assert.Equal(PlaneType.X, new Plane(new Vec3(-1f, 0f, 0f), 0f).Type);

    [Fact]
    public void TypeIsZForAnExactlyPositiveZNormal() =>
        Assert.Equal(PlaneType.Z, new Plane(new Vec3(0f, 0f, 1f), 0f).Type);

    [Fact]
    public void TypeIsAnyXWhenXDominates() =>
        Assert.Equal(PlaneType.AnyX, new Plane(new Vec3(0.9f, 0.3f, 0.1f), 0f).Type);

    [Fact]
    public void TypeIsAnyZOnlyWhenZDominatesBothOthers() =>
        Assert.Equal(PlaneType.AnyZ, new Plane(new Vec3(0.1f, 0.2f, 0.9f), 0f).Type);

    [Fact]
    public void TypeBreaksATieTowardsTheEarlierAxis()
    {
        // is `if (ax >= ay && ax >= az)`, so a normal exactly
        // between two axes is classified as the lower-numbered one. A `>` there
        // would give AnyY and renumber every 45-degree plane in the map.
        Assert.Equal(PlaneType.AnyX, new Plane(new Vec3(0.5f, 0.5f, 0f), 0f).Type);
    }

    [Fact]
    public void TypeIsNotAxialForANormalOnlyNearlyAxial()
    {
        // The exact-equality test has no epsilon, on purpose:
        // snapping is what is meant to have made it exact.
        Assert.Equal(PlaneType.AnyX, new Plane(new Vec3(0.9999f, 0.0001f, 0f), 0f).Type);
    }

    [Fact]
    public void IsAxialIsTrueForAnAxialPlane() =>
        Assert.True(new Plane(new Vec3(0f, -1f, 0f), 12f).IsAxial);

    [Fact]
    public void IsAxialIsFalseForASlopedPlane() =>
        Assert.False(new Plane(new Vec3(0.6f, 0.8f, 0f), 12f).IsAxial);

    [Fact]
    public void FlippingNegatesTheDistance() =>
        Assert.Equal(-64f, new Plane(new Vec3(1f, 0f, 0f), 64f).Flipped.Dist);

    [Fact]
    public void FlippingNegatesTheNormal() =>
        Assert.Equal(-1f, new Plane(new Vec3(1f, 0f, 0f), 64f).Flipped.Normal.X);

    [Fact]
    public void FlippingLeavesAZeroComponentPOSITIVELYSigned()
    {
        // flips with VectorSubtract(vec3_origin, normal), which is
        // `0 - x`. For x == +0.0f that gives +0.0f; the shorter `-x` gives
        // -0.0f. Same number, different bytes, and an axial plane's normal has
        // two of them going straight into the PLANES lump.
        Plane flipped = new Plane(new Vec3(1f, 0f, 0f), 64f).Flipped;
        Assert.False(float.IsNegative(flipped.Normal.Y));
        Assert.False(float.IsNegative(flipped.Normal.Z));
    }

    [Fact]
    public void SnappingANearAxialNormalMakesItExactlyAxial()
    {
        // |0.999995 - 1| = 5e-6, inside RENDER_NORMAL_EPSILON of 1e-5.
        Assert.True(Plane.TrySnapNormal(new Vec3(0.999995f, 2e-6f, 1e-6f), out Vec3 snapped));
        Assert.Equal(new Vec3(1f, 0f, 0f), snapped);
    }

    [Fact]
    public void SnappingClearsTheOtherComponentsRatherThanLeavingThem()
    {
        // SnapVector calls VectorClear before setting the axis,
        // so the small off-axis components become exactly zero and do not
        // survive as a normal that is axial in type but not in value.
        Plane.TrySnapNormal(new Vec3(0.999995f, 2e-6f, 1e-6f), out Vec3 snapped);
        Assert.Equal(0f, snapped.Y);
        Assert.Equal(0f, snapped.Z);
    }

    [Fact]
    public void SnappingLeavesANormalThatIsNotNearAxial()
    {
        // |0.9999 - 1| = 1e-4, ten times the epsilon.
        Assert.False(Plane.TrySnapNormal(new Vec3(0.9999f, 0.0001f, 0f), out Vec3 snapped));
        Assert.Equal(new Vec3(0.9999f, 0.0001f, 0f), snapped);
    }

    [Fact]
    public void SnappingTakesTheFirstAxisThatMatches()
    {
        // the reference implementation's loop RETURNS on the first hit rather than picking the
        // closest. Only reachable with a normal that is not unit length, which
        // is exactly the case a bad cross product produces.
        Plane.TrySnapNormal(new Vec3(1f, 1f, 0f), out Vec3 snapped);
        Assert.Equal(new Vec3(1f, 0f, 0f), snapped);
    }

    [Fact]
    public void SnappingMatchesANegativeAxisToo()
    {
        Plane.TrySnapNormal(new Vec3(3e-6f, -0.999996f, 0f), out Vec3 snapped);
        Assert.Equal(new Vec3(0f, -1f, 0f), snapped);
    }

    [Fact]
    public void SnappingRoundsANearIntegerDistance()
    {
        // |64.005 - 64| = 0.005, inside RENDER_DIST_EPSILON of 0.01f.
        Assert.Equal(64f, new Plane(new Vec3(0.6f, 0.8f, 0f), 64.005f).Snapped().Dist);
    }

    [Fact]
    public void SnappingLeavesADistanceThatIsNotNearAnInteger()
    {
        Assert.Equal(64.02f, new Plane(new Vec3(0.6f, 0.8f, 0f), 64.02f).Snapped().Dist);
    }

    [Fact]
    public void TheTwoArgumentSnapDoesNotRecomputeDistanceWhenTheNormalMoves()
    {
        // snaps the normal and then only ROUNDS the distance it was
        // given. The five-argument overload is the one that rotates the plane
        // about the centroid; this one can leave a distance that belonged to
        // the pre-snap normal, and that is what FindFloatPlane uses when it has
        // no points to hand.
        Plane snapped = new Plane(new Vec3(0.999995f, 2e-6f, 0f), 10.5f).Snapped();
        Assert.Equal(new Vec3(1f, 0f, 0f), snapped.Normal);
        Assert.Equal(10.5f, snapped.Dist);
    }

    [Fact]
    public void SnappingThroughPointsRecomputesTheDistanceFromTheCentroid()
    {
        // Three points on x == 10, with a normal a hair off +X. The snap makes
        // the normal exactly (1,0,0) and the distance is then Dot(normal,
        // centroid) = 10, not the 9.99999 it came in with.
        var plane = new Plane(new Vec3(0.999999f, 1e-6f, 0f), 9.99999f);
        Plane snapped = plane.SnappedThroughPoints(
            new Vec3(10f, 0f, 0f), new Vec3(10f, 0f, 10f), new Vec3(10f, 10f, 0f), false);

        Assert.Equal(new Vec3(1f, 0f, 0f), snapped.Normal);
        Assert.Equal(10f, snapped.Dist);
    }

    [Fact]
    public void SnappingThroughPointsRoundsTheRecomputedDistanceWhenAskedTo()
    {
        // g_snapAxialPlanes, the -snapaxial switch: the
        // recomputed 10.4 becomes 10 even though 0.4 is far outside
        // RENDER_DIST_EPSILON.
        var plane = new Plane(new Vec3(0.999999f, 1e-6f, 0f), 10.4f);
        Plane snapped = plane.SnappedThroughPoints(
            new Vec3(10.4f, 0f, 0f), new Vec3(10.4f, 0f, 10f), new Vec3(10.4f, 10f, 0f), true);

        Assert.Equal(10f, snapped.Dist);
    }

    [Fact]
    public void SnappingThroughPointsLeavesTheRecomputedDistanceWhenNotAskedTo()
    {
        var plane = new Plane(new Vec3(0.999999f, 1e-6f, 0f), 10.4f);
        Plane snapped = plane.SnappedThroughPoints(
            new Vec3(10.4f, 0f, 0f), new Vec3(10.4f, 0f, 10f), new Vec3(10.4f, 10f, 0f), false);

        Assert.NotEqual(10f, snapped.Dist);
    }

    [Fact]
    public void PlaneEqualAcceptsPlanesInsideBothTolerances()
    {
        var a = new Plane(new Vec3(1f, 0f, 0f), 64f);
        var b = new Plane(new Vec3(1f, 0f, 0f), 64.005f);
        Assert.True(Plane.Equal(a, b, GeometryEpsilons.RenderNormalEpsilonFloat,
            GeometryEpsilons.RenderDistEpsilon));
    }

    [Fact]
    public void PlaneEqualRejectsADistanceOutsideTheTolerance()
    {
        var a = new Plane(new Vec3(1f, 0f, 0f), 64f);
        var b = new Plane(new Vec3(1f, 0f, 0f), 64.5f);
        Assert.False(Plane.Equal(a, b, GeometryEpsilons.RenderNormalEpsilonFloat,
            GeometryEpsilons.RenderDistEpsilon));
    }

    [Fact]
    public void PlaneEqualIsNotTransitive()
    {
        // Three planes 0.6 apart with a tolerance of 1: the first matches the
        // second and the second the third, but not the first the third. That is
        // why the plane table's INSERTION ORDER is part of the output: which
        // planes collapse together depends on the order they are offered in.
        var a = new Plane(new Vec3(1f, 0f, 0f), 0f);
        var b = new Plane(new Vec3(1f, 0f, 0f), 0.6f);
        var c = new Plane(new Vec3(1f, 0f, 0f), 1.2f);

        Assert.True(Plane.Equal(a, b, 1f, 1f));
        Assert.True(Plane.Equal(b, c, 1f, 1f));
        Assert.False(Plane.Equal(a, c, 1f, 1f));
    }

    [Fact]
    public void PlaneEqualDoesNotMatchAPlaneWithItsOpposite()
    {
        // Which is why CreateNewFloatPlane has to store both halves of every
        // pair, and why plane indices come in twos.
        var a = new Plane(new Vec3(1f, 0f, 0f), 64f);
        Assert.False(Plane.Equal(a, a.Flipped, GeometryEpsilons.RenderNormalEpsilonFloat,
            GeometryEpsilons.RenderDistEpsilon));
    }

    [Fact]
    public void HashBucketTruncatesTheDistanceBeforeDividing()
    {
        // (int)fabs(15.9) is 15, and 15/8 is 1. Rounding first would give 16/8
        // = 2 and put the plane in a different chain from the one
        // AddPlaneToHash would have used.
        Assert.Equal(1, Plane.HashBucket(15.9f));
    }

    [Fact]
    public void HashBucketIsOneBucketPerEightUnits() => Assert.Equal(2, Plane.HashBucket(16f));

    [Fact]
    public void HashBucketIgnoresTheSignOfTheDistance()
    {
        // Deliberate: a plane and its opposite are inserted together and must
        // land in the same chain.
        Assert.Equal(Plane.HashBucket(20f), Plane.HashBucket(-20f));
    }

    [Fact]
    public void HashBucketWrapsAtEightThousandOneHundredAndNinetyTwoUnits()
    {
        // 8192/8 == 1024 == PLANE_HASHES, so the mask folds it back to zero.
        Assert.Equal(Plane.HashBucket(0f), Plane.HashBucket(8192f));
    }

    [Fact]
    public void RoundIntIsFloorOfAHalfOffsetAndNotBankersRounding()
    {
        // is floor(in + 0.5f). MathF.Round would give 2.
        Assert.Equal(3f, Plane.RoundInt(2.5f));
        Assert.Equal(2f, MathF.Round(2.5f));
    }

    [Fact]
    public void RoundIntRoundsNegativeHalvesTowardsZero()
    {
        // floor(-0.5 + 0.5) == floor(0) == 0, and floor(-1.5 + 0.5) == -1.
        Assert.Equal(0f, Plane.RoundInt(-0.5f));
        Assert.Equal(-1f, Plane.RoundInt(-1.5f));
    }

    [Fact]
    public void FromPointsTakesBothEdgesFromTheMIDDLEPoint()
    {
        // is `t1 = p0 - p1` and `t2 = p2 - p1`, then `t1 x t2`.
        // The obvious (p1-p0) x (p2-p0) gives +Z here; the stock order gives
        // -Z, and getting it wrong turns every brush inside out.
        Plane plane = Plane.FromPoints(
            new Vec3(0f, 0f, 0f), new Vec3(1f, 0f, 0f), new Vec3(1f, 1f, 0f));

        Assert.Equal(new Vec3(0f, 0f, -1f), plane.Normal);
    }

    [Fact]
    public void FromPointsMeasuresTheDistanceAlongTheNormal()
    {
        // The same triangle lifted to z == 5. The normal points at -Z, so the
        // distance is -5 and not +5.
        Plane plane = Plane.FromPoints(
            new Vec3(0f, 0f, 5f), new Vec3(1f, 0f, 5f), new Vec3(1f, 1f, 5f));

        Assert.Equal(-5f, plane.Dist);
    }

    [Fact]
    public void HasUsableNormalRejectsANormalShorterThanAHalf()
    {
        // the reference implementation's "FloatPlane: bad normal". The threshold is nowhere near
        // 1: what it catches is a cross product of two nearly parallel edges.
        Assert.False(new Plane(new Vec3(0.4f, 0f, 0f), 0f).HasUsableNormal);
        Assert.True(new Plane(new Vec3(0.5f, 0f, 0f), 0f).HasUsableNormal);
    }

    [Fact]
    public void DistanceToIsPositiveInFrontOfThePlane() =>
        Assert.Equal(4f, new Plane(new Vec3(1f, 0f, 0f), 10f).DistanceTo(new Vec3(14f, 0f, 0f)));

    [Fact]
    public void DistanceToIsNegativeBehindThePlane() =>
        Assert.Equal(-4f, new Plane(new Vec3(1f, 0f, 0f), 10f).DistanceTo(new Vec3(6f, 0f, 0f)));

    [Fact]
    public void EqualityIsExactAndIsNotTheTolerantMatch()
    {
        // Plane.Equal is vbsp's fuzzy comparison and is not transitive, so it
        // must never be what == or a hash set means.
        var a = new Plane(new Vec3(1f, 0f, 0f), 64f);
        var b = new Plane(new Vec3(1f, 0f, 0f), 64.005f);

        Assert.True(Plane.Equal(a, b, GeometryEpsilons.RenderNormalEpsilonFloat,
            GeometryEpsilons.RenderDistEpsilon));
        Assert.NotEqual(a, b);
    }
}
