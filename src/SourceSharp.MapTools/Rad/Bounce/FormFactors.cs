using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Light;

namespace SourceSharp.MapTools.Rad.Bounce;

/// <summary>
/// The two form factors <c>MakeTransfer</c> chooses between
/// (<c>vrad.cpp:1067-1111</c>).
/// </summary>
/// <remarks>
/// <para>
/// Every <c>VectorNormalize</c> here is vrad's own, so it takes the same fork
/// as the rest of the lighting (<see cref="StockQuirk.VradVectorNormalise"/>):
/// stock's <c>rsqrtss</c> estimate, or an exact divide. The fork matters
/// twice over in this file, because stock uses the normalise's RETURN value --
/// <c>sqrlen * invlen</c> on the estimate path, which is only approximately the
/// length -- as a distance in one formula and as a sine in the other.
/// </para>
/// <para>
/// Both compute in <c>float</c>, in stock's operand order; unlike their
/// caller, neither has a double literal in it.
/// </para>
/// </remarks>
public static class FormFactors
{
    /// <summary>
    /// <c>VectorNormalize</c> on the chosen path: the unit vector and the value
    /// stock's function returns.
    /// </summary>
    /// <param name="v">The vector.</param>
    /// <param name="stockNormalise">True for stock's estimate.</param>
    /// <returns>The normalised vector and the "length" the call returns.</returns>
    public static (Vec3 Normalised, float Length) Normalise(Vec3 v, bool stockNormalise) =>
        stockNormalise ? v.NormaliseLikeStock() : v.Normalise();

    /// <summary>
    /// <c>FormFactorDiffToDiff</c> (<c>vrad.cpp:1103</c>): two differential
    /// patches, good when they are at least five patch-widths apart.
    /// </summary>
    /// <param name="diff1Origin"><c>pDiff1-&gt;origin</c>.</param>
    /// <param name="diff1Normal"><c>pDiff1-&gt;normal</c>.</param>
    /// <param name="diff2Origin"><c>pDiff2-&gt;origin</c>.</param>
    /// <param name="diff2Normal"><c>pDiff2-&gt;normal</c>.</param>
    /// <param name="stockNormalise">The normalise fork.</param>
    /// <returns>
    /// <c>-(d . n1) * (d . n2) / len^2</c> with <c>d</c> the unit vector from 2
    /// to 1 -- positive when the two face each other, and ALSO when both face
    /// away, which the caller's plane tests are what rule out.
    /// </returns>
    public static float DiffToDiff(
        Vec3 diff1Origin, Vec3 diff1Normal, Vec3 diff2Origin, Vec3 diff2Normal, bool stockNormalise)
    {
        // :1106-1110. The length is the normalise's return value.
        (Vec3 delta, float length) = Normalise(diff1Origin - diff2Origin, stockNormalise);
        return -Vec3.Dot(delta, diff1Normal) * Vec3.Dot(delta, diff2Normal) / (length * length);
    }

    /// <summary>
    /// <c>FormFactorPolyToDiff</c> (<c>vrad.cpp:1067</c>): a polygon patch to a
    /// differential one, Dutre's formula 81, divided by the polygon's area.
    /// </summary>
    /// <param name="polygon">The polygon's winding points, in order.</param>
    /// <param name="polygonArea"><c>pPolygon-&gt;area</c>.</param>
    /// <param name="diffOrigin">The differential patch's origin.</param>
    /// <param name="diffNormal">Its normal.</param>
    /// <param name="stockNormalise">The normalise fork.</param>
    /// <param name="compliance">
    /// Decides <see cref="StockQuirk.FormFactorSineAboveOne"/>.
    /// </param>
    /// <returns>
    /// The form factor times <c>2 * pi / area</c>; the caller multiplies the
    /// area back in.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>The sine guard (<c>:1084</c>).</b> The edge's sine is the
    /// normalise's RETURN value on the cross product of two unit vectors, and
    /// rounding can take it past 1, where <c>asin</c> would be NaN. Stock
    /// answers by returning 0 for the WHOLE polygon, discarding every other
    /// edge's contribution and with it the transfer, because its caller drops a
    /// form factor at or below 0 (<c>vrad.cpp:1165</c>). Under
    /// <see cref="StockQuirk.FormFactorSineAboveOne"/> that is reproduced;
    /// correct clamps the sine to 1, which is the value it was an ulp away
    /// from.
    /// </para>
    /// <para>
    /// <c>asin</c> is the <c>float</c> overload (MSVC's C++ <c>math.h</c> maps
    /// <c>asin(float)</c> to <c>asinf</c>), so <see cref="MathF.Asin"/>.
    /// </para>
    /// </remarks>
    public static float PolyToDiff(
        ReadOnlySpan<Vec3> polygon,
        float polygonArea,
        Vec3 diffOrigin,
        Vec3 diffNormal,
        bool stockNormalise,
        ComplianceOptions compliance)
    {
        ArgumentNullException.ThrowIfNull(compliance);

        bool dropWhole = compliance.Emulates(StockQuirk.FormFactorSineAboveOne);
        float formFactor = 0.0f;

        for (int point = 0; point < polygon.Length; point++)
        {
            int next = point < polygon.Length - 1 ? point + 1 : 0;

            (Vec3 v1, _) = Normalise(polygon[point] - diffOrigin, stockNormalise);
            (Vec3 v2, _) = Normalise(polygon[next] - diffOrigin, stockNormalise);
            (Vec3 gamma, float sinAlpha) = Normalise(Vec3.Cross(v1, v2), stockNormalise);

            if (sinAlpha < -1.0f || sinAlpha > 1.0f)
            {
                if (dropWhole)
                {
                    return 0.0f;
                }

                sinAlpha = sinAlpha > 1.0f ? 1.0f : -1.0f;
            }

            gamma *= MathF.Asin(sinAlpha);
            formFactor += Vec3.Dot(gamma, diffNormal);
        }

        // :1090. "divide by pi later, multiply by area later".
        formFactor *= 0.5f / polygonArea;
        return formFactor;
    }
}
