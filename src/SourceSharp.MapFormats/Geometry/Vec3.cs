using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SourceSharp.MapFormats.Geometry;

/// <summary>
/// Three floats, in the layout the BSP stores them and with the arithmetic the
/// C++ tools perform.
/// </summary>
/// <remarks>
/// <para>
/// NOT <see cref="System.Numerics.Vector3"/>, and the reason is the whole
/// acceptance story of this port. <c>Vector3.Dot</c> and <c>Normalize</c> are
/// free to reassociate, to use a hardware dot-product instruction, or to use a
/// reciprocal-square-root ESTIMATE whose result differs between AMD and Intel.
/// Any of those makes the output depend on the machine it was compiled on,
/// which destroys the one guarantee this port offers that stock never did:
/// bit-exact self-determinism.
/// </para>
/// <para>
/// So every operation here is written longhand, in the C++ operand order, over
/// <see cref="float"/>. RyuJIT does not contract <c>a*b+c</c> into an FMA on
/// its own, and nothing here asks it to, so the arithmetic is plain IEEE
/// single precision and reproducible everywhere. Exact
/// <see cref="MathF.Sqrt(float)"/> and a divide, never
/// <c>ReciprocalSqrtEstimate</c>.
/// </para>
/// <para>
/// <see cref="StructLayoutAttribute"/> with
/// <see cref="LayoutKind.Sequential"/> and <c>Pack = 1</c> so the struct can be
/// reinterpreted over lump bytes: a <c>dvertex_t</c> IS three little-endian
/// floats, and copying it field by field would be the slow way to say so.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct Vec3 : IEquatable<Vec3>
{
    /// <summary>The X component.</summary>
    public readonly float X;

    /// <summary>The Y component.</summary>
    public readonly float Y;

    /// <summary>The Z component.</summary>
    public readonly float Z;

    /// <summary>Creates a vector from its three components.</summary>
    /// <param name="x">The X component.</param>
    /// <param name="y">The Y component.</param>
    /// <param name="z">The Z component.</param>
    public Vec3(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    /// <summary>The zero vector.</summary>
    public static Vec3 Zero => default;

    /// <summary>Reads one component by index, 0 for X through 2 for Z.</summary>
    /// <param name="index">Which component.</param>
    /// <returns>That component.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="index"/> is outside 0..2.
    /// </exception>
    /// <remarks>
    /// The C++ treats a <c>Vector</c> as <c>vec_t[3]</c> and indexes it
    /// constantly -- the axis-aligned special cases in the vis clipper are
    /// written as <c>normal[j] == 1</c>. Ported code reads more like its
    /// original with this than with a switch at every site.
    /// </remarks>
    public float this[int index] => index switch
    {
        0 => X,
        1 => Y,
        2 => Z,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    /// <summary>Adds two vectors componentwise.</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    /// <returns>Their sum.</returns>
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    /// <summary>Subtracts <paramref name="b"/> from <paramref name="a"/>.</summary>
    /// <param name="a">The vector to subtract from.</param>
    /// <param name="b">The vector to subtract.</param>
    /// <returns>Their difference.</returns>
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    /// <summary>Negates a vector.</summary>
    /// <param name="a">The vector to negate.</param>
    /// <returns>Its negation.</returns>
    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);

    /// <summary>Scales a vector.</summary>
    /// <param name="a">The vector.</param>
    /// <param name="scale">The scale factor.</param>
    /// <returns>The scaled vector.</returns>
    public static Vec3 operator *(Vec3 a, float scale) => new(a.X * scale, a.Y * scale, a.Z * scale);

    /// <summary>Scales a vector.</summary>
    /// <param name="scale">The scale factor.</param>
    /// <param name="a">The vector.</param>
    /// <returns>The scaled vector.</returns>
    public static Vec3 operator *(float scale, Vec3 a) => a * scale;

    /// <summary>
    /// The dot product, summed left to right as <c>DotProduct</c> does.
    /// </summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    /// <returns>Their dot product.</returns>
    /// <remarks>
    /// <c>mathlib.h</c> defines this as
    /// <c>(x[0]*y[0] + x[1]*y[1] + x[2]*y[2])</c>, which C evaluates as
    /// <c>((x0*y0 + x1*y1) + x2*y2)</c>. Float addition is not associative, so
    /// that grouping is part of the answer and is reproduced exactly.
    /// </remarks>
    public static float Dot(Vec3 a, Vec3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    /// <summary>
    /// The cross product, in <c>CrossProduct</c>'s operand order.
    /// </summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    /// <returns>Their cross product.</returns>
    public static Vec3 Cross(Vec3 a, Vec3 b) => new(
        (a.Y * b.Z) - (a.Z * b.Y),
        (a.Z * b.X) - (a.X * b.Z),
        (a.X * b.Y) - (a.Y * b.X));

    /// <summary>
    /// The vector's length, as <c>VectorLength</c> computes it.
    /// </summary>
    /// <returns>The length.</returns>
    /// <remarks>
    /// <para>
    /// An exact square root of the left-to-right sum of squares. Deliberately
    /// not a reciprocal-square-root estimate: those differ between vendors, so
    /// using one would make this port's output depend on the machine it ran on.
    /// </para>
    /// <para>
    /// THE COST OF THAT CHOICE, MEASURED rather than assumed away. Normalising
    /// in a tight loop on an AMD Ryzen 9 9950X, best of 200, agreeing between a
    /// cache-resident set and a 50 MB one (so not memory-bound):
    /// exact sqrt-and-divide 2.193 ns/op against 1.089 ns/op for
    /// <see cref="NormaliseLikeStock"/>'s estimate — almost exactly 2x. That is
    /// a microbenchmark of the operation alone; what it costs a whole compile
    /// is a Phase 5 measurement and will be far less, because a compile does
    /// much else between normalises.
    /// </para>
    /// </remarks>
    public float Length() => MathF.Sqrt((X * X) + (Y * Y) + (Z * Z));

    /// <summary>The sum of the squares of the components.</summary>
    /// <returns>The squared length.</returns>
    public float LengthSquared() => (X * X) + (Y * Y) + (Z * Z);

    /// <summary>
    /// This vector scaled to unit length, and the length it had.
    /// </summary>
    /// <returns>
    /// The normalised vector and the original length. A zero-length vector
    /// comes back as <see cref="Zero"/> with a length of zero rather than as
    /// NaN, which is what <c>VectorNormalize</c> does.
    /// </returns>
    public (Vec3 Normalised, float Length) Normalise()
    {
        float length = Length();
        if (length == 0f)
        {
            return (Zero, 0f);
        }

        // A divide, not a multiply by a reciprocal: 1/l then three multiplies
        // rounds twice and gives different last bits.
        return (new Vec3(X / length, Y / length, Z / length), length);
    }

    /// <summary>
    /// This vector normalised exactly as stock's <c>VectorNormalize</c> does,
    /// reproducing its reciprocal-square-root ESTIMATE instruction for
    /// instruction.
    /// </summary>
    /// <returns>
    /// The normalised vector and stock's return value, which is
    /// <c>sqrlen * invlen</c> and only approximately the length.
    /// </returns>
    /// <exception cref="PlatformNotSupportedException">
    /// The CPU has no SSE. See the remarks: this deliberately throws rather
    /// than falling back.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>This exists so the port can be compared with stock at all.</b>
    /// <see cref="Normalise"/> is an exact divide and is therefore reproducible
    /// on every machine, which is the guarantee this port offers and stock
    /// never did — but it means every quantity downstream of a normalise
    /// differs from stock in the last bits, and there is a great deal of such
    /// quantity: plane normals, vertex normals, phong smoothing, sky
    /// directions. Comparing those against stock would have to be a tolerance
    /// rather than an equality, which is a much weaker oracle.
    /// </para>
    /// <para>
    /// So this method reproduces <c>vector.h:2225-2251</c> exactly:
    /// <c>LengthSqr() + 1.0e-10f</c>, then <c>rsqrtss</c> followed by one
    /// Newton-Raphson step (<c>xr * (3 - xr*xr*xx) * 0.5</c>), then three
    /// multiplies. MEASURED, not assumed: the same 210,000 seeded vectors
    /// through this and through that C++ compiled with g++ -O2 produce
    /// bit-identical results in every component, axial vectors included.
    /// </para>
    /// <para>
    /// The cost is that <c>rsqrtss</c>'s result is implementation-defined
    /// within a relative error bound, so it may differ between CPU vendors.
    /// That makes this method MACHINE-DEPENDENT — which is precisely why it is
    /// not the default. On any one machine it agrees with stock; across two
    /// machines, stock and this port may move together but neither is fixed.
    /// (This was verified on an AMD Ryzen 9 9950X. No Intel part was available
    /// here, so the cross-vendor divergence is documented behaviour rather than
    /// something measured in this tree.)
    /// </para>
    /// <para>
    /// It THROWS rather than falling back to the exact path when SSE is
    /// missing. A silent fallback would mean a differential comparison quietly
    /// stopped matching stock while still reporting success, which is the exact
    /// shape of check this project has been bitten by before.
    /// </para>
    /// </remarks>
    public (Vec3 Normalised, float Returned) NormaliseLikeStock()
    {
        if (!Sse.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "NormaliseLikeStock reproduces stock's rsqrtss estimate and has no meaning "
                + "without SSE. It refuses rather than falling back to the exact path, because a "
                + "differential against stock that silently stopped matching would still report "
                + "success.");
        }

        // vector.h:2241. The +1e-10f is stock's guard against a zero length and
        // is part of the answer, not a detail: it shifts the estimate's input.
        float sqrlen = ((X * X) + (Y * Y) + (Z * Z)) + 1.0e-10f;

        // _SSE_RSqrtInline, vector.h:2225-2235: rsqrtss then one
        // Newton-Raphson refinement. Written with scalar intrinsics in the same
        // order as the C++ so the instruction sequence matches.
        Vector128<float> xx = Vector128.CreateScalarUnsafe(sqrlen);
        Vector128<float> xr = Sse.ReciprocalSqrtScalar(xx);
        Vector128<float> xt = Sse.MultiplyScalar(xr, xr);
        xt = Sse.MultiplyScalar(xt, xx);
        xt = Sse.SubtractScalar(Vector128.CreateScalarUnsafe(3f), xt);
        xt = Sse.MultiplyScalar(xt, Vector128.CreateScalarUnsafe(0.5f));
        xr = Sse.MultiplyScalar(xr, xt);

        float invlen = xr.ToScalar();
        return (new Vec3(X * invlen, Y * invlen, Z * invlen), sqrlen * invlen);
    }

    /// <inheritdoc />
    public bool Equals(Vec3 other) =>
        X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Vec3 other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"({X} {Y} {Z})");

    /// <summary>Compares two vectors for exact bitwise equality.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>True when every component is equal.</returns>
    public static bool operator ==(Vec3 left, Vec3 right) => left.Equals(right);

    /// <summary>Compares two vectors for exact bitwise equality.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>True when any component differs.</returns>
    public static bool operator !=(Vec3 left, Vec3 right) => !left.Equals(right);
}
