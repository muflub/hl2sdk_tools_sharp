//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Bounce;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Bounce;

/// <summary><c>FormFactorDiffToDiff</c> and <c>FormFactorPolyToDiff</c>.</summary>
public sealed class FormFactorsTests
{
    /// <summary>
    /// Two patches facing each other straight on, 10 apart: both cosines are 1,
    /// so the result is 1/r^2.
    /// </summary>
    [Fact]
    public void FacingPatchesGiveTheInverseSquare()
    {
        float f = FormFactors.DiffToDiff(new(0, 0, 10), new(0, 0, -1), Vec3.Zero, new(0, 0, 1), stockNormalise: false);
        Assert.Equal(0.01f, f, 6);
    }

    /// <summary>A patch behind the other's plane gives a negative factor, which MakeTransfer drops.</summary>
    [Fact]
    public void OneFacingAwayIsNegative()
    {
        float f = FormFactors.DiffToDiff(new(0, 0, 10), new(0, 0, 1), Vec3.Zero, new(0, 0, 1), stockNormalise: false);
        Assert.True(f < 0);
    }

    /// <summary>
    /// Both facing away is POSITIVE: the product of two negative cosines. Stock
    /// relies on vismat's plane tests to keep such pairs out.
    /// </summary>
    [Fact]
    public void BothFacingAwayIsPositive()
    {
        float f = FormFactors.DiffToDiff(new(0, 0, 10), new(0, 0, 1), Vec3.Zero, new(0, 0, -1), stockNormalise: false);
        Assert.Equal(0.01f, f, 6);
    }

    /// <summary>
    /// Under stock's normalise the length in the denominator is
    /// <c>VectorNormalize</c>'s return value, which is
    /// close to, but not exactly, the length.
    /// </summary>
    [Fact]
    public void StockNormaliseKeepsTheResultWithinAnEstimateOfExact()
    {
        Vec3 a = new(3.3f, -7.1f, 10.9f);
        Vec3 n1 = new Vec3(-0.2f, 0.3f, -0.9f).Normalise().Normalised;
        Vec3 n2 = new Vec3(0.1f, -0.2f, 0.95f).Normalise().Normalised;
        float exact = FormFactors.DiffToDiff(a, n1, Vec3.Zero, n2, stockNormalise: false);
        float stock = FormFactors.DiffToDiff(a, n1, Vec3.Zero, n2, stockNormalise: true);
        Assert.Equal(exact, stock, exact * 1e-5f);
    }

    /// <summary>
    /// A 2x2 square 1 above a differential patch facing it, centred: the
    /// analytic point-to-rectangle form factor is 4 x 0.13853 = 0.55412, and
    /// the function returns it times pi / area.
    /// </summary>
    [Fact]
    public void ASquareOverheadMatchesTheAnalyticFormFactor()
    {
        Vec3[] square = [new(1, -1, 1), new(1, 1, 1), new(-1, 1, 1), new(-1, -1, 1)];
        float r = FormFactors.PolyToDiff(square, 4f, Vec3.Zero, new(0, 0, 1), false, ComplianceOptions.Correct);
        double f = r * 4.0 / Math.PI;
        Assert.Equal(0.55412, f, 4);
    }

    /// <summary>The winding's direction is the sign: reversed, the factor is negative.</summary>
    [Fact]
    public void AReversedWindingIsNegative()
    {
        Vec3[] square = [new(-1, -1, 1), new(-1, 1, 1), new(1, 1, 1), new(1, -1, 1)];
        float r = FormFactors.PolyToDiff(square, 4f, Vec3.Zero, new(0, 0, 1), false, ComplianceOptions.Correct);
        Assert.True(r < 0);
    }

    /// <summary>
    /// <see cref="StockQuirk.FormFactorSineAboveOne"/>, stock side: an edge
    /// whose sine rounds above 1 makes the WHOLE form factor 0.
    /// </summary>
    [Fact]
    public void StockReturnsZeroWhenAnEdgeSineRoundsAboveOne()
    {
        Vec3[] polygon = RightAngleWithSineAboveOne();
        float r = FormFactors.PolyToDiff(polygon, 1f, Vec3.Zero, new(0, 0, 1), true, ComplianceOptions.Stock);
        Assert.Equal(0f, r);
    }

    /// <summary>
    /// <see cref="StockQuirk.FormFactorSineAboveOne"/>, correct side: the sine
    /// is clamped to 1 and the other edges still count.
    /// </summary>
    [Fact]
    public void CorrectClampsTheSineAndKeepsSumming()
    {
        Vec3[] polygon = RightAngleWithSineAboveOne();
        ComplianceOptions correctHere = ComplianceOptions.Stock.Flipping(StockQuirk.FormFactorSineAboveOne);
        float r = FormFactors.PolyToDiff(polygon, 1f, Vec3.Zero, new(0, 0, 1), true, correctHere);
        Assert.True(r > 0);
    }

    /// <summary>
    /// A triangle whose first edge spans a right angle between two axis
    /// directions of a length for which stock's <c>rsqrtss</c> normalise
    /// returns a component above 1, so the cross product's "length" is above 1.
    /// Found by search, because <c>rsqrtss</c> is the CPU's.
    /// </summary>
    private static Vec3[] RightAngleWithSineAboveOne()
    {
        for (int i = 1; i < 100_000; i++)
        {
            float a = i * 0.37f;
            float x = new Vec3(a, 0, 0).NormaliseLikeStock().Normalised.X;
            if (x > 1f)
            {
                (_, float sine) = Vec3.Cross(new Vec3(x, 0, 0), new Vec3(0, x, 0)).NormaliseLikeStock();
                if (sine > 1f)
                {
                    return [new(a, 0, 0), new(0, a, 0), new(0, 0, a)];
                }
            }
        }

        throw new InvalidOperationException("no length found whose stock normalise rounds above 1");
    }
}
