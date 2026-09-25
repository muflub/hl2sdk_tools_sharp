using System.Collections.Immutable;

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// The three-vector tangent-space basis every bumped lightmap is sampled along
/// </summary>
/// <remarks>
/// <para>
/// A face whose texinfo carries <c>SURF_BUMPLIGHT</c> gets FOUR lightmaps, not
/// one: an unbumped one plus one per basis vector. Every array in the lighting
/// path is therefore <c>NUM_BUMP_VECTS + 1</c> long and indexed with 0 meaning
/// "the flat one", which is why <see cref="Count"/> is exported separately from
/// <see cref="LightmapCount"/> -- confusing the two is an off-by-one that
/// silently drops the third bump direction.
/// </para>
/// <para>
/// The basis itself is FIXED in tangent space and rotated into world space per
/// sample. That is the whole design: the engine's bumped lightmap shader knows
/// these three directions as constants, so vrad may not choose different ones.
/// </para>
/// </remarks>
public static class BumpBasis
{
    /// <summary><c>NUM_BUMP_VECTS</c>: three.</summary>
    public const int Count = 3;

    /// <summary>
    /// How many lightmaps a bumped face has: <see cref="Count"/> plus the
    /// unbumped one.
    /// </summary>
    public const int LightmapCount = Count + 1;

    // Spelled as the C++ spells them, to the digit: these
    // are float literals in the header and not computed from sqrt at runtime,
    // so recomputing them here would give different last bits.
    private const float OneOverSqrt2 = 0.70710676908493042f;
    private const float OneOverSqrt3 = 0.57735025882720947f;
    private const float OneOverSqrt6 = 0.40824821591377258f;
    private const float OneOverSqrt2Over3 = 0.81649661064147949f;

    // An ImmutableArray, not a Vec3[]: a static array's elements are writable
    // shared state however readonly the field is (LibraryRuleTests).
    private static readonly ImmutableArray<Vec3> LocalBasis =
    [
        new(OneOverSqrt2Over3, 0.0f, OneOverSqrt3),
        new(-OneOverSqrt6, OneOverSqrt2, OneOverSqrt3),
        new(-OneOverSqrt6, -OneOverSqrt2, OneOverSqrt3),
    ];

    /// <summary><c>g_localBumpBasis</c>: the basis in tangent space.</summary>
    public static ReadOnlySpan<Vec3> Local => LocalBasis.AsSpan();

    /// <summary>
    /// Rotates the tangent-space basis into world space around a normal.
    /// </summary>
    /// <param name="sVector">
    /// The texture s axis, <c>textureVecsTexelsPerWorldUnits[0]</c>. NOT the
    /// lightmap axis -- see the remarks.
    /// </param>
    /// <param name="tVector">The texture t axis.</param>
    /// <param name="flatNormal">
    /// The face's plane normal, used ONLY to decide handedness.
    /// </param>
    /// <param name="phongNormal">
    /// The normal the basis is built around: the face normal on a flat face,
    /// the interpolated one otherwise.
    /// </param>
    /// <param name="bumpNormals">
    /// Receives <see cref="Count"/> world-space vectors. Must be at least that
    /// long.
    /// </param>
    /// <param name="stockNormalise">
    /// True to reproduce stock's <c>VectorNormalize</c> reciprocal-square-root
    /// estimate; false to divide exactly.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="bumpNormals"/> is shorter than <see cref="Count"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <c>GetBumpNormals</c>. Two things about it
    /// surprise every reader:
    /// </para>
    /// <para>
    /// <b>The s and t vectors are the TEXTURE axes, not the lightmap axes.</b>
 /// Both pass
    /// <c>textureVecsTexelsPerWorldUnits</c>, while everything else in the
    /// sample path uses <c>lightmapVecsLuxelsPerWorldUnits</c>. They are
    /// usually parallel and are not required to be, so a face with rotated or
    /// non-uniformly scaled texture axes gets a basis that is rotated relative
    /// to its luxel grid. That is deliberate -- the engine's shader resolves
    /// the bump map in TEXTURE space -- and it is reproduced unconditionally.
    /// </para>
    /// <para>
    /// <b>Only the SIGN of the flat normal matters.</b> <paramref name="flatNormal"/>
    /// is used once, in a dot product against <c>s x t</c>, to decide whether
    /// the texture mapping is mirrored; a mirrored face negates the second
    /// basis row. Nothing else reads it, so passing the phong normal for both
    /// arguments (which <c>InitSampleInfo</c> does on flat faces,
    ///) is correct rather than sloppy.
    /// </para>
    /// <para>
    /// The final step is <c>VectorIRotate</c>,
    /// the TRANSPOSE of a rotation, so each output is the tangent vector's
    /// coordinates read down the basis matrix's columns rather than across its
    /// rows.
    /// </para>
    /// </remarks>
    public static void Build(
        Vec3 sVector,
        Vec3 tVector,
        Vec3 flatNormal,
        Vec3 phongNormal,
        Span<Vec3> bumpNormals,
        bool stockNormalise)
    {
        if (bumpNormals.Length < Count)
        {
            throw new ArgumentException(
                $"bumpNormals must hold at least {Count} vectors.", nameof(bumpNormals));
        }

        // Handedness from the texture axes against the
        // FLAT normal.
        bool leftHanded = Vec3.Dot(flatNormal, Vec3.Cross(sVector, tVector)) < 0.0f;

        // 47-53. row1 = normalise(phong x s); row0 = normalise(row1 x phong);
        // row2 = phong, NOT normalised -- stock copies it through untouched,
        // so a phong normal that is slightly off unit length stays that way.
        Vec3 row1 = Normalise(Vec3.Cross(phongNormal, sVector), stockNormalise);
        Vec3 row0 = Normalise(Vec3.Cross(row1, phongNormal), stockNormalise);
        Vec3 row2 = phongNormal;

        if (leftHanded)
        {
            row1 = -row1;
        }

        // VectorIRotate: out = (local . column0, local . column1,
        // local . column2).
        for (int i = 0; i < Count; i++)
        {
            Vec3 local = LocalBasis[i];
            bumpNormals[i] = new Vec3(
                (local.X * row0.X) + (local.Y * row1.X) + (local.Z * row2.X),
                (local.X * row0.Y) + (local.Y * row1.Y) + (local.Z * row2.Y),
                (local.X * row0.Z) + (local.Y * row1.Z) + (local.Z * row2.Z));
        }
    }

    /// <summary>
    /// <c>VectorNormalize</c>, either stock's estimate or an exact divide.
    /// </summary>
    /// <param name="v">The vector.</param>
    /// <param name="stockNormalise">True for stock's estimate.</param>
    /// <returns>The unit vector.</returns>
    /// <remarks>
    /// <para>
    /// The same fork <c>StockQuirk.BaseWindingNormalise</c> describes, at a
    /// different call site. On <c>PLATFORM_INTEL</c> -- which every shipped
    /// vrad.exe is -- adds <c>1e-10f</c> to the squared
    /// length and runs <c>rsqrtss</c> plus one Newton-Raphson step, so the
    /// result carries about 22 bits of mantissa rather than 24.
    /// </para>
    /// <para>
    /// It is exposed here rather than inlined because every normalise in the
    /// lighting path takes the same fork and they must all take it together: a
    /// mixture would be neither stock's answer nor the exact one.
    /// </para>
    /// </remarks>
    public static Vec3 Normalise(Vec3 v, bool stockNormalise) =>
        stockNormalise ? v.NormaliseLikeStock().Normalised : v.Normalise().Normalised;
}
