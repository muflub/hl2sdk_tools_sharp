//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

namespace SourceSharp.MapFormats.Geometry;

/// <summary>
/// A plane as the compilers hold one: a normal and the distance along it from
/// the origin, with the classification and snapping rules <c>vbsp</c>'s plane
/// table is built on.
/// </summary>
/// <remarks>
/// <para>
/// A point <c>p</c> is on the plane when <c>Dot(p, Normal) == Dist</c>. That is
/// stock's convention for its <c>plane_t</c> and the loader's, and it is the
/// reason <see cref="DistanceTo"/> subtracts rather than adds.
/// </para>
/// <para>
/// <b>There is deliberately no plane list here, and no dedup.</b> In vbsp a
/// plane's INDEX is output: brush sides, nodes and the PLANES lump all store
/// it, and the reference's <c>CreateNewFloatPlane</c> appends planes in PAIRS —
/// the plane and its opposite — in the order the map file first mentions them,
/// flipping the pair so an axial plane faces positive. Anything that sorted,
/// deduplicated or reordered planes behind the caller's back would silently
/// renumber the map. So this type carries the <i>predicates</i> that table is
/// built from — <see cref="Type"/>, <see cref="Snapped"/>, <see cref="Equal"/>,
/// <see cref="HashBucket"/> — and the table itself belongs to whoever is doing
/// the appending.
/// </para>
/// <para>
/// <b>On normalisation.</b> Anything here that needs a unit normal uses
/// <see cref="Vec3.Normalise"/>, which is an exact square root and a divide.
/// Stock's <c>VectorNormalize</c> is neither: on the x86 build path it adds
/// <c>1.0e-10f</c> to the squared length and calls <c>_mm_rsqrt_ss</c> with one
/// Newton-Raphson step, and <c>_mm_rsqrt_ss</c> is an approximation whose
/// result is permitted to differ between CPU vendors. Off that path it is
/// <c>1.f / (sqrtf(len) + FLT_EPSILON)</c> and three multiplies. Neither is
/// <c>x / len</c>, and the first is not reproducible at all. Matching stock bit
/// for bit here is therefore impossible in principle, and this implementation
/// takes the exact divide instead: a documented, deterministic divergence
/// rather than an undocumented, machine-dependent one.
/// </para>
/// </remarks>
public readonly struct Plane : IEquatable<Plane>
{
    /// <summary>
    /// How many buckets <c>vbsp</c>'s plane hash has.
    /// </summary>
    /// <remarks>
    /// The reference layout declares <c>#define PLANE_HASHES 1024</c>.
    /// </remarks>
    public const int PlaneHashes = 1024;

    /// <summary>The plane's normal. Unit length by convention, not by check.</summary>
    public Vec3 Normal { get; }

    /// <summary>The distance from the origin along <see cref="Normal"/>.</summary>
    public float Dist { get; }

    /// <summary>Creates a plane from a normal and a distance.</summary>
    /// <param name="normal">The normal.</param>
    /// <param name="dist">The distance along the normal.</param>
    public Plane(Vec3 normal, float dist)
    {
        Normal = normal;
        Dist = dist;
    }

    /// <summary>
    /// How this plane's normal is oriented, as the reference's
    /// <c>PlaneTypeForNormal</c> classifies it.
    /// </summary>
    /// <remarks>
    /// The exact-equality tests come first and are exact on purpose — stock
    /// asks "should these have an epsilon around 1.0?" and the answer it
    /// shipped with is no, because <see cref="Snapped"/> has already forced a
    /// near-axial normal to exactly axial before anything classifies it. The
    /// tie-breaks that follow are <c>&gt;=</c>, so a normal at 45 degrees
    /// between two axes classifies as the earlier axis.
    /// </remarks>
    public PlaneType Type
    {
        get
        {
            if (Normal.X == 1.0f || Normal.X == -1.0f)
            {
                return PlaneType.X;
            }

            if (Normal.Y == 1.0f || Normal.Y == -1.0f)
            {
                return PlaneType.Y;
            }

            if (Normal.Z == 1.0f || Normal.Z == -1.0f)
            {
                return PlaneType.Z;
            }

            float ax = MathF.Abs(Normal.X);
            float ay = MathF.Abs(Normal.Y);
            float az = MathF.Abs(Normal.Z);

            if (ax >= ay && ax >= az)
            {
                return PlaneType.AnyX;
            }

            if (ay >= ax && ay >= az)
            {
                return PlaneType.AnyY;
            }

            return PlaneType.AnyZ;
        }
    }

    /// <summary>
    /// True when <see cref="Type"/> is one of the three exactly axial values.
    /// </summary>
    /// <remarks>
    /// The reference's <c>CreateNewFloatPlane</c> spells this
    /// <c>if (p-&gt;type &lt; 3)</c>, which is why the enum's numbering is
    /// fixed rather than incidental.
    /// </remarks>
    public bool IsAxial => Type < PlaneType.AnyX;

    /// <summary>
    /// How far <paramref name="point"/> is in front of the plane.
    /// </summary>
    /// <param name="point">The point to measure.</param>
    /// <returns>
    /// Positive in front, negative behind, and the result is signed distance
    /// only when <see cref="Normal"/> is unit length.
    /// </returns>
    /// <remarks>
    /// <c>DotProduct (w-&gt;p[i], normal) - dist</c>, the expression that opens
    /// every side test in the reference's polygon library. The dot is
    /// <see cref="Vec3.Dot"/>, which sums left to right for the reason that
    /// type documents.
    /// </remarks>
    public float DistanceTo(Vec3 point) => Vec3.Dot(point, Normal) - Dist;

    /// <summary>
    /// This plane facing the other way: the opposite of a plane pair.
    /// </summary>
    /// <remarks>
    /// Written as <c>0 - x</c> per component, not as <c>-x</c>, because the
    /// reference's <c>CreateNewFloatPlane</c> is
    /// <c>VectorSubtract (vec3_origin, normal, (p+1)-&gt;normal)</c>. For a
    /// component that is <c>+0.0f</c> the two differ: <c>0 - 0</c> is
    /// <c>+0.0f</c> and <c>-0.0f</c> is not, and an axial plane's normal has
    /// two zero components that go straight into the PLANES lump. Same number,
    /// different bytes on disk.
    /// </remarks>
    public Plane Flipped => new(
        new Vec3(0f - Normal.X, 0f - Normal.Y, 0f - Normal.Z),
        -Dist);

    /// <summary>
    /// Whether the normal is long enough for <c>vbsp</c> to accept the plane.
    /// </summary>
    /// <remarks>
    /// The reference's <c>CreateNewFloatPlane</c> rejects a plane with
    /// <c>VectorLength(normal) &lt; 0.5</c> as a "bad normal". The threshold is
    /// nowhere near 1, so what it really catches is a normal that came out of a
    /// cross product of two nearly parallel edges and normalised to roughly
    /// nothing.
    /// </remarks>
    public bool HasUsableNormal => Normal.Length() >= 0.5f;

    /// <summary>
    /// Rounds to the nearest integer the way the tools do.
    /// </summary>
    /// <param name="value">The value to round.</param>
    /// <returns>The rounded value.</returns>
    /// <remarks>
    /// The reference's <c>RoundInt</c> is <c>floor(in + 0.5f)</c>: floor of a
    /// half-offset, which is NOT <see cref="MathF.Round(float)"/>: this rounds
    /// -0.5 to -0.0 and 0.5 to 1, where <c>MathF.Round</c> rounds both to 0
    /// (banker's rounding). It also rounds 2.5 to 3 where <c>MathF.Round</c>
    /// gives 2.
    /// </remarks>
    public static float RoundInt(float value) => MathF.Floor(value + 0.5f);

    /// <summary>
    /// Snaps a near-axial normal to exactly axial.
    /// </summary>
    /// <param name="normal">The normal to consider.</param>
    /// <param name="snapped">
    /// The snapped normal when the return value is true, otherwise
    /// <paramref name="normal"/> unchanged.
    /// </param>
    /// <returns>True when the normal was snapped.</returns>
    /// <remarks>
    /// The reference's <c>SnapVector</c>. Note what it does on a hit: it CLEARS
    /// the whole vector and sets one component, so the other two become exactly
    /// zero rather than being left as the small values they were. It also
    /// returns on the FIRST axis that matches, so a normal within the epsilon
    /// of two axes at once — which cannot happen for a unit normal, but can for
    /// one that is not — takes the lowest-numbered one.
    /// </remarks>
    public static bool TrySnapNormal(Vec3 normal, out Vec3 snapped)
    {
        for (int i = 0; i < 3; i++)
        {
            // fabs() in the reference, so both comparisons happen in double:
            // the float difference is promoted and RENDER_NORMAL_EPSILON is a
            // double literal.
            if (Math.Abs((double)(normal[i] - 1f)) < GeometryEpsilons.RenderNormalEpsilon)
            {
                snapped = Axis(i, 1f);
                return true;
            }

            if (Math.Abs((double)(normal[i] - -1f)) < GeometryEpsilons.RenderNormalEpsilon)
            {
                snapped = Axis(i, -1f);
                return true;
            }
        }

        snapped = normal;
        return false;
    }

    /// <summary>
    /// This plane with a near-axial normal made axial and a near-integer
    /// distance made integral.
    /// </summary>
    /// <returns>The snapped plane.</returns>
    /// <remarks>
    /// The reference's two-argument <c>SnapPlane</c>. The distance is NOT
    /// recomputed when the normal moves — that is what the five-argument
    /// overload is for — so this one can leave a plane whose distance belongs
    /// to the pre-snap normal. Stock uses this form from
    /// <c>FindFloatPlane</c> when the caller had no points to hand.
    /// </remarks>
    public Plane Snapped()
    {
        _ = TrySnapNormal(Normal, out Vec3 normal);
        return new Plane(normal, SnapDist(Dist));
    }

    /// <summary>
    /// This plane snapped, with the distance recomputed through the centroid of
    /// the three points the plane came from.
    /// </summary>
    /// <param name="p0">The first point the plane was built from.</param>
    /// <param name="p1">The second point.</param>
    /// <param name="p2">The third point.</param>
    /// <param name="snapAxialPlanes">
    /// Stock's <c>g_snapAxialPlanes</c>, the <c>-snapaxial</c> switch: when set,
    /// a snapped normal's recomputed distance is also forced to an integer,
    /// whatever <see cref="GeometryEpsilons.RenderDistEpsilon"/> would have
    /// said.
    /// </param>
    /// <returns>The snapped plane.</returns>
    /// <remarks>
    /// The reference's five-argument <c>SnapPlane</c>. Rotating the plane about
    /// the centroid rather than about one of the three points is what keeps the
    /// error even across the face; the reference comment there says as much.
    /// The centroid is <c>(p0 + p1 + p2) / 3.0f</c> — a float divide by 3, in
    /// that grouping.
    /// </remarks>
    public Plane SnappedThroughPoints(Vec3 p0, Vec3 p1, Vec3 p2, bool snapAxialPlanes)
    {
        float dist = Dist;

        if (TrySnapNormal(Normal, out Vec3 normal))
        {
            Vec3 p3 = (p0 + p1 + p2) * (1f / 3f);
            // The reference is `(p0 + p1 + p2) / 3.0f`, which its
            // Vector::operator/ turns into a multiply by the reciprocal of
            // 3.0f -- see the reference's VectorDivide. 1f/3f is computed once
            // at compile time either way, so this spelling is the same
            // arithmetic and not a shortcut.
            dist = Vec3.Dot(normal, p3);

            if (snapAxialPlanes)
            {
                dist = RoundInt(dist);
            }
        }

        return new Plane(normal, SnapDist(dist));
    }

    /// <summary>
    /// Derives a plane from three points on it, before any snapping.
    /// </summary>
    /// <param name="p0">The first point.</param>
    /// <param name="p1">The second point, the corner the edges meet at.</param>
    /// <param name="p2">The third point.</param>
    /// <returns>The plane through the three points.</returns>
    /// <remarks>
    /// <para>
    /// The first half of the reference's <c>PlaneFromPoints</c>. The operand
    /// order is load-bearing and is not the obvious one: the edges are
    /// <c>p0 - p1</c> and <c>p2 - p1</c>, both FROM the middle point, and the
    /// cross is taken in that order. Writing it as <c>(p1-p0) x (p2-p0)</c>
    /// gives a normal of the opposite sign, which turns every brush inside out.
    /// </para>
    /// <para>
    /// The second half of the stock function is <c>SnapPlane</c> followed by
    /// <c>FindFloatPlane</c>, which inserts into vbsp's plane table and returns
    /// an INDEX. Only the derivation is here; see this type's remarks for why
    /// the table is not.
    /// </para>
    /// </remarks>
    public static Plane FromPoints(Vec3 p0, Vec3 p1, Vec3 p2)
    {
        Vec3 t1 = p0 - p1;
        Vec3 t2 = p2 - p1;
        (Vec3 normal, _) = Vec3.Cross(t1, t2).Normalise();
        return new Plane(normal, Vec3.Dot(p0, normal));
    }

    /// <summary>
    /// Whether two planes are the same to within the given tolerances.
    /// </summary>
    /// <param name="a">The first plane.</param>
    /// <param name="b">The second plane.</param>
    /// <param name="normalEpsilon">
    /// How far each normal component may differ, exclusive.
    /// </param>
    /// <param name="distEpsilon">
    /// How far the distances may differ, exclusive.
    /// </param>
    /// <returns>True when the planes match.</returns>
    /// <remarks>
    /// <para>
    /// The reference's <c>PlaneEqual</c>. Componentwise on the normal, with a
    /// strict <c>&lt;</c> and not <c>&lt;=</c>, and the comparisons happen in
    /// <c>double</c> because <c>fabs</c> returns one.
    /// </para>
    /// <para>
    /// This is not an equivalence relation: it is not transitive, so which
    /// planes collapse together depends on the order they are offered in. That
    /// is another reason the plane table's insertion order is part of the
    /// output and not an implementation detail.
    /// </para>
    /// <para>
    /// It also does not treat a plane and its opposite as equal, which is why
    /// the reference's <c>CreateNewFloatPlane</c> has to store both halves of
    /// every pair.
    /// </para>
    /// </remarks>
    public static bool Equal(Plane a, Plane b, float normalEpsilon, float distEpsilon) =>
        Math.Abs((double)(a.Normal.X - b.Normal.X)) < normalEpsilon
        && Math.Abs((double)(a.Normal.Y - b.Normal.Y)) < normalEpsilon
        && Math.Abs((double)(a.Normal.Z - b.Normal.Z)) < normalEpsilon
        && Math.Abs((double)(a.Dist - b.Dist)) < distEpsilon;

    /// <summary>
    /// Which bucket of <c>vbsp</c>'s plane hash a distance falls in.
    /// </summary>
    /// <param name="dist">The plane distance.</param>
    /// <returns>A bucket index in 0..<see cref="PlaneHashes"/>-1.</returns>
    /// <remarks>
    /// <para>
    /// The reference's <c>AddPlaneToHash</c>:
    /// <c>hash = (int)fabs(p-&gt;dist) / 8; hash &amp;= (PLANE_HASHES-1);</c>.
    /// Truncation to <c>int</c> first, THEN integer division by 8, so the bucket
    /// is one per 8 world units of |distance| and wraps every 8192 units.
    /// </para>
    /// <para>
    /// It keys on the ABSOLUTE distance, so a plane and its opposite land in the
    /// same bucket — deliberate, since they are inserted together. The
    /// reference's <c>FindFloatPlane</c> then searches this bucket and the two
    /// either side of it, because a plane a hair under a multiple of 8 and one
    /// a hair over are within <c>RENDER_DIST_EPSILON</c> of each other but hash
    /// apart.
    /// </para>
    /// <para>
    /// A distance whose magnitude exceeds <see cref="int"/> range would be
    /// undefined in the reference; it is saturated here rather than left to the
    /// JIT.
    /// </para>
    /// </remarks>
    public static int HashBucket(float dist)
    {
        double magnitude = Math.Abs((double)dist);
        int truncated = magnitude >= int.MaxValue ? int.MaxValue : (int)magnitude;
        return (truncated / 8) & (PlaneHashes - 1);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Exact, bitwise equality of all four floats. This is NOT
    /// <see cref="Equal(Plane, Plane, float, float)"/>: that one is vbsp's
    /// tolerant match and is not transitive, so it must never be what
    /// <c>==</c> or a hash set means.
    /// </remarks>
    public bool Equals(Plane other) => Normal.Equals(other.Normal) && Dist.Equals(other.Dist);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Plane other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Normal, Dist);

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Normal} @ {Dist}");

    /// <summary>Compares two planes for exact bitwise equality.</summary>
    /// <param name="left">The first plane.</param>
    /// <param name="right">The second plane.</param>
    /// <returns>True when they are identical.</returns>
    public static bool operator ==(Plane left, Plane right) => left.Equals(right);

    /// <summary>Compares two planes for exact bitwise equality.</summary>
    /// <param name="left">The first plane.</param>
    /// <param name="right">The second plane.</param>
    /// <returns>True when anything differs.</returns>
    public static bool operator !=(Plane left, Plane right) => !left.Equals(right);

    private static Vec3 Axis(int index, float value) => index switch
    {
        0 => new Vec3(value, 0f, 0f),
        1 => new Vec3(0f, value, 0f),
        _ => new Vec3(0f, 0f, value),
    };

    private static float SnapDist(float dist) =>
        Math.Abs((double)(dist - RoundInt(dist))) < GeometryEpsilons.RenderDistEpsilon
            ? RoundInt(dist)
            : dist;
}
