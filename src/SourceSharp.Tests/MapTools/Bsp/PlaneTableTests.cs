//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Diagnostics;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp;

/// <summary>
/// The plane table's ordering rules, which are the file format.
/// </summary>
/// <remarks>
/// A plane's INDEX is what the BSP stores, so every fact here is about an
/// index or about the bytes of a plane at one — not about the set of planes
/// the table holds.
/// </remarks>
public class PlaneTableTests
{
    [Fact]
    public void ANewTableIsEmpty()
    {
        PlaneTable table = new();

        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void CreateAppendsAPairSoTheOppositeIsTheIndexXorOne()
    {
        PlaneTable table = new();

        int index = table.Create(new Vec3(0f, 0f, 1f), 64f);

        Assert.Equal(2, table.Count);
        Assert.Equal(-table[index].Dist, table[index ^ 1].Dist);
        Assert.Equal(-table[index].Normal.Z, table[index ^ 1].Normal.Z);
    }

    /// <summary>
    /// is <c>VectorSubtract(vec3_origin, normal,...)</c>,
    /// so a zero component of the opposite normal is <c>+0.0f</c>. Unary
    /// negation would make it <c>-0.0f</c>, which is a different bit pattern
    /// and therefore different bytes in LUMP_PLANES.
    /// </summary>
    [Fact]
    public void TheOppositeOfAnAxialPlaneKeepsPositiveZeroComponents()
    {
        PlaneTable table = new();

        int index = table.Create(new Vec3(0f, 0f, 1f), 64f);
        Plane opposite = table[index ^ 1];

        Assert.Equal(0, BitConverter.SingleToInt32Bits(opposite.Normal.X));
        Assert.Equal(0, BitConverter.SingleToInt32Bits(opposite.Normal.Y));
    }

    [Fact]
    public void UnaryNegationWouldHaveGivenNegativeZeroSoTheSpellingMatters()
    {
        // The mutation this fact exists to catch, written out: if Create used
        // -x the two bit patterns below would be what LUMP_PLANES carried.
        Assert.NotEqual(
            BitConverter.SingleToInt32Bits(0f - 0f),
            BitConverter.SingleToInt32Bits(-0f));
    }

    /// <summary>
    ///: an axial plane facing negative is swapped with
    /// its opposite so the positive-facing one is first, and the index
    /// RETURNED is then the second slot, because that is where the plane the
    /// caller asked for ended up.
    /// </summary>
    [Fact]
    public void AnAxialPlaneFacingNegativeIsFlippedAndTheRequestedIndexIsTheSecondSlot()
    {
        PlaneTable table = new();

        int index = table.Create(new Vec3(0f, 0f, -1f), 64f);

        Assert.Equal(1, index);
        Assert.Equal(1f, table[0].Normal.Z);
        Assert.Equal(-1f, table[1].Normal.Z);
    }

    [Fact]
    public void AnAxialPlaneFacingPositiveIsNotFlipped()
    {
        PlaneTable table = new();

        int index = table.Create(new Vec3(0f, 1f, 0f), 64f);

        Assert.Equal(0, index);
        Assert.Equal(1f, table[0].Normal.Y);
    }

    /// <summary>
    /// The flip test is on the plane's TYPE being axial, so a non-axial normal
    /// with a negative component is left alone however negative it is.
    /// </summary>
    [Fact]
    public void ANonAxialPlaneIsNeverFlipped()
    {
        PlaneTable table = new();
        (Vec3 normal, _) = new Vec3(-1f, -1f, 0f).Normalise();

        int index = table.Create(normal, 10f);

        Assert.Equal(0, index);
        Assert.True(table[0].Normal.X < 0f);
    }

    [Fact]
    public void BothHalvesOfAPairStoreTheSameType()
    {
        PlaneTable table = new();

        int index = table.Create(new Vec3(0f, 0f, -1f), 64f);

        Assert.Equal(table.TypeOf(index), table.TypeOf(index ^ 1));
    }

    /// <summary>
    /// <c>dplane_t.type</c> is written to LUMP_PLANES, and
    /// <c>CreateNewFloatPlane</c> stores the FRONT half's classification on
    /// both halves. That is only safe because <c>PlaneTypeForNormal</c> is
    /// invariant under negation — which this fact asserts rather than assumes.
    /// </summary>
    [Theory]
    [InlineData(1f, 0f, 0f)]
    [InlineData(0f, 1f, 0f)]
    [InlineData(0f, 0f, 1f)]
    [InlineData(0.6f, 0.8f, 0f)]
    [InlineData(0.8f, 0f, 0.6f)]
    [InlineData(0f, 0.6f, 0.8f)]
    [InlineData(0.577f, 0.577f, 0.577f)]
    public void PlaneTypeIsInvariantUnderNegation(float x, float y, float z)
    {
        Plane forward = new(new Vec3(x, y, z), 0f);
        Plane back = forward.Flipped;

        Assert.Equal(forward.Type, back.Type);
    }

    [Fact]
    public void TheStoredTypeIsTheRecomputedTypeForBothHalves()
    {
        PlaneTable table = new();
        table.Create(new Vec3(0f, 0f, -1f), 64f);
        (Vec3 slanted, _) = new Vec3(-0.3f, 0.9f, 0.2f).Normalise();
        table.Create(slanted, 17f);

        for (int i = 0; i < table.Count; i++)
        {
            Assert.Equal(table[i].Type, table.TypeOf(i));
        }
    }

    [Fact]
    public void FindReturnsTheExistingIndexForAPlaneWithinTheEpsilon()
    {
        PlaneTable table = new();
        int first = table.Find(new Vec3(0f, 0f, 1f), 64f);

        // RENDER_DIST_EPSILON is 0.01f, exclusive.
        int again = table.Find(new Vec3(0f, 0f, 1f), 64.005f);

        Assert.Equal(first, again);
        Assert.Equal(2, table.Count);
    }

    [Fact]
    public void FindAppendsAPlaneOutsideTheEpsilon()
    {
        PlaneTable table = new();
        table.Find(new Vec3(0f, 0f, 1f), 64f);

        table.Find(new Vec3(0f, 0f, 1f), 64.02f);

        Assert.Equal(4, table.Count);
    }

    /// <summary>
    /// The neighbouring-bucket search exists for two planes that hash apart but
    /// still match — and after <c>SnapPlane</c> there are none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>FindFloatPlane</c> searches <c>bucket-1</c>, <c>bucket</c> and
    /// <c>bucket+1</c> because the bucket is
    /// <c>(int)|dist| / 8</c> and two distances a hair either side of a
    /// multiple of 8 hash apart while being within
    /// <c>RENDER_DIST_EPSILON</c> of each other.
    /// </para>
    /// <para>
    /// <b>But the snap upstream of it closes that gap.</b> Two distances can
    /// only straddle a bucket boundary by straddling a multiple of 8, which is
    /// an INTEGER — and a distance within 0.01 of an integer is rounded to it
    /// by <c>SnapPlane</c> before the hash is taken. So after snapping, any two
    /// distances close enough for <c>PlaneEqual</c> to accept are in the same
    /// bucket, and the search of the neighbours cannot change an answer. This
    /// fact is the sweep that establishes it; the neighbours are still searched,
    /// because matching stock is the point and a dead branch is cheap.
    /// </para>
    /// </remarks>
    [Fact]
    public void AfterSnappingNoTwoMatchingDistancesHashApart()
    {
        int checkedPairs = 0;

        for (int i = 0; i < 4000; i++)
        {
            float a = (i * 0.0625f) - 125f;

            for (int j = -3; j <= 3; j++)
            {
                float b = a + (j * 0.003f);

                Plane pa = new Plane(new Vec3(0f, 0f, 1f), a).Snapped();
                Plane pb = new Plane(new Vec3(0f, 0f, 1f), b).Snapped();

                if (!Plane.Equal(pa, pb, GeometryEpsilons.RenderNormalEpsilonFloat, GeometryEpsilons.RenderDistEpsilon))
                {
                    continue;
                }

                checkedPairs++;
                Assert.Equal(Plane.HashBucket(pa.Dist), Plane.HashBucket(pb.Dist));
            }
        }

        Assert.True(checkedPairs > 10000, $"the sweep only checked {checkedPairs} matching pairs");
    }

    /// <summary>
    /// The snap is what closes the gap, and this is the pair that shows it: two
    /// distances that hash apart BEFORE snapping and together after.
    /// </summary>
    [Fact]
    public void TwoDistancesThatHashApartAreSnappedTogether()
    {
        Assert.NotEqual(Plane.HashBucket(63.999f), Plane.HashBucket(64.001f));

        PlaneTable table = new();
        int first = table.Find(new Vec3(0f, 0f, 1f), 63.999f);

        Assert.Equal(first, table.Find(new Vec3(0f, 0f, 1f), 64.001f));
        Assert.Equal(2, table.Count);
        Assert.Equal(64f, table[first].Dist);
    }

    /// <summary>
    /// Insertion order IS the output, so an index handed out early must still
    /// name the same plane after a hundred more are added.
    /// </summary>
    [Fact]
    public void AnIndexStaysValidAsTheTableGrows()
    {
        PlaneTable table = new();
        int first = table.Find(new Vec3(0f, 0f, 1f), 64f);
        Plane expected = table[first];

        for (int i = 1; i < 100; i++)
        {
            table.Find(new Vec3(0f, 0f, 1f), 64f + (i * 32f));
        }

        Assert.Equal(expected, table[first]);
    }

    /// <summary>
    /// Two tables offered the same planes in different orders give different
    /// indices for the same plane. That is not a defect to fix — it is why the
    /// table may never be reordered behind the caller.
    /// </summary>
    [Fact]
    public void OfferingThePlanesInADifferentOrderGivesDifferentIndices()
    {
        PlaneTable a = new();
        a.Find(new Vec3(1f, 0f, 0f), 16f);
        int zInA = a.Find(new Vec3(0f, 0f, 1f), 32f);

        PlaneTable b = new();
        int zInB = b.Find(new Vec3(0f, 0f, 1f), 32f);
        b.Find(new Vec3(1f, 0f, 0f), 16f);

        Assert.Equal(a[zInA], b[zInB]);
        Assert.NotEqual(zInA, zInB);
    }

    [Fact]
    public void CreateRejectsANormalShorterThanAHalf()
    {
        PlaneTable table = new();

        Assert.Throws<MapCompileException>(() => table.Create(new Vec3(0.2f, 0f, 0f), 0f));
    }

    [Fact]
    public void FromPointsInsertsOnePairAndReturnsItsIndex()
    {
        PlaneTable table = new();

        int index = table.FromPoints(
            new Vec3(0f, 0f, 64f),
            new Vec3(64f, 0f, 64f),
            new Vec3(64f, 64f, 64f),
            snapAxialPlanes: false);

        // The winding is clockwise seen from +Z, so the normal faces -Z and the
        // pair is flipped: the plane asked for is the second slot.
        Assert.Equal(2, table.Count);
        Assert.Equal(1, index);
        Assert.Equal(-64f, table[index].Dist);
        Assert.Equal(-1f, table[index].Normal.Z);
    }

    /// <summary>
    /// <c>PlaneFromPoints</c>'s cross product is
    /// <c>(p0 - p1) x (p2 - p1)</c>. The opposite operand order flips every
    /// normal, which turns every brush inside out — so the direction is
    /// pinned, not left to the reader.
    /// </summary>
    [Fact]
    public void FromPointsFacesTheWayTheWindingOrderSays()
    {
        PlaneTable table = new();

        int up = table.FromPoints(
            new Vec3(0f, 0f, 0f),
            new Vec3(64f, 0f, 0f),
            new Vec3(64f, 64f, 0f),
            snapAxialPlanes: false);

        Assert.Equal(-1f, table[up].Normal.Z);
    }

    // ---- read-only while the world's blocks are built in parallel ----------

    [Fact]
    public void AFrozenTableStillFindsWhatItHoldsButAppendsNothing()
    {
        PlaneTable table = new();
        int up = table.Find(new Vec3(0f, 0f, 1f), 64f);

        table.Freeze();

        Assert.True(table.IsFrozen);
        Assert.Equal(up, table.Find(new Vec3(0f, 0f, 1f), 64f));
        Assert.Equal(up ^ 1, table.Find(new Vec3(0f, 0f, -1f), -64f));
        Assert.Throws<InvalidOperationException>(() => table.Find(new Vec3(1f, 0f, 0f), 64f));
        Assert.Throws<InvalidOperationException>(() => table.Create(new Vec3(1f, 0f, 0f), 64f));
        Assert.Equal(2, table.Count);
    }

    [Fact]
    public void AThawedTableAppendsAgain()
    {
        PlaneTable table = new();
        table.Freeze();
        table.Thaw();

        Assert.False(table.IsFrozen);
        Assert.Equal(0, table.Find(new Vec3(1f, 0f, 0f), 64f));
        Assert.Equal(2, table.Count);
    }
}
