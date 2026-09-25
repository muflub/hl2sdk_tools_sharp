using System.Collections.Immutable;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Rad.Ambient;

/// <summary>
/// The six directions an ambient cube stores light along, and the maths that
/// fills them.
/// </summary>
/// <remarks>
/// <para>
/// An "ambient cube" is six colours, one per axis direction, and the engine
/// reconstructs the light arriving at a point from any direction by blending the
/// three that face it. <c>ComputeAmbientFromSphericalSamples</c>
/// Builds one by firing 162 rays, then
/// projecting what came back onto those six axes (<see cref="Project"/>) and
/// adding the baked surface lights (<see cref="AddEmitSurfaceLights"/>).
/// <see cref="AmbientSampler"/> strings the three together per sample.
/// </para>
/// </remarks>
public static class AmbientCube
{
    /// <summary>How many directions a cube stores.</summary>
    public const int Sides = 6;

    /// <summary>
    /// The six axis directions, in the order the lump stores them
    /// (<c>g_BoxDirections</c>).
    /// </summary>
    /// <remarks>
    /// +x, -x, +y, -y, +z, -z. The ORDER is the disk format, so it is not
    /// adjustable: <c>CompressedLightCube</c>'s six entries are indexed by it
    /// and the engine reads them back the same way.
    /// </remarks>
    private static readonly ImmutableArray<Vec3> BoxDirectionTable =
    [
        new(1, 0, 0),
        new(-1, 0, 0),
        new(0, 1, 0),
        new(0, -1, 0),
        new(0, 0, 1),
        new(0, 0, -1),
    ];

    /// <summary>The six axis directions.</summary>
    public static ReadOnlySpan<Vec3> BoxDirections => BoxDirectionTable.AsSpan();

    /// <summary>
    /// How far a ray is fired before it counts as having left the world
    /// (<c>COORD_EXTENT * 1.74</c>).
    /// </summary>
    /// <remarks>
    /// <c>COORD_EXTENT</c> is <c>2 * MAX_COORD_INTEGER</c> = 32768
    /// And 1.74 is a hand-rounded square root of three.
    /// Stock's product is a DOUBLE narrowed to float by <c>Vector * float</c>;
    /// both that and this float product are 57016.3203125.
    /// </remarks>
    public const float RayLength = 32768.0f * 1.74f;

    /// <summary>
    /// Projects 162 ray colours onto the six cube directions
    /// </summary>
    /// <param name="radColor">What each of the <see cref="VertexNormals.Count"/> rays brought back.</param>
    /// <param name="cube">Receives the six colours. Overwritten.</param>
    /// <remarks>
    /// THE ACCUMULATION RUNS BACKWARDS, <c>for (int j = 6; --j >= 0; )</c>, and
    /// the inner loop forwards; float addition is not associative, so the
    /// inner order is part of the output. The total is divided as
    /// <c>lightBoxColor[j] *= 1/t</c>: a reciprocal, then a multiply.
    /// </remarks>
    public static void Project(ReadOnlySpan<Vec3> radColor, Span<Vec3> cube)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(radColor.Length, VertexNormals.Count);
        ArgumentOutOfRangeException.ThrowIfLessThan(cube.Length, Sides);

        ReadOnlySpan<Vec3> anorms = VertexNormals.All;
        ReadOnlySpan<Vec3> box = BoxDirections;
        for (int j = Sides; --j >= 0;)
        {
            float t = 0;
            Vec3 sum = Vec3.Zero;

            for (int i = 0; i < VertexNormals.Count; i++)
            {
                float c = Vec3.Dot(anorms[i], box[j]);
                if (c > 0)
                {
                    t += c;
                    sum += radColor[i] * c;
                }
            }

            cube[j] = sum * (1 / t);
        }
    }

    /// <summary>
    /// <c>AddEmitSurfaceLights</c>: the
    /// direct contribution of the lights that were folded into the cubes.
    /// </summary>
    /// <param name="lights">
    /// The mode's world lights AS CLASSIFIED by this pass -- not the lump's
    /// flags, which are only right on a map a previous run already wrote.
    /// </param>
    /// <param name="flagged">
    /// The indices of the lights carrying <c>DWL_FLAGS_INAMBIENTCUBE</c>, in
    /// lump order.
    /// </param>
    /// <param name="fractionVisible">
    /// <c>TestLine</c>'s answer for each flagged light from
    /// <paramref name="start"/>, parallel to <paramref name="flagged"/>.
    /// </param>
    /// <param name="start">The sample position.</param>
    /// <param name="cube">The cube, added to in place.</param>
    /// <param name="compliance">Which defects to reproduce.</param>
    /// <exception cref="ArgumentNullException"><paramref name="compliance"/> is null.</exception>
    /// <remarks>
    /// There are a great many <c>emit_surface</c> lights and most are dim enough
    /// that the engine's <c>r_worldlightmin</c> would discard them, so the dim
    /// ones are baked into the ambient cube instead of being shipped as lights;
    /// <see cref="LeafAmbientSurfaceLights.IsAmbientCubeLight"/> decides which.
 /// A light the sample cannot see is skipped, and the rest
    /// are scaled by the fraction that got through.
    /// </remarks>
    public static void AddEmitSurfaceLights(
        ReadOnlySpan<DWorldLight> lights,
        ReadOnlySpan<int> flagged,
        ReadOnlySpan<float> fractionVisible,
        Vec3 start,
        Span<Vec3> cube,
        ComplianceOptions compliance)
    {
        ArgumentNullException.ThrowIfNull(compliance);

        ReadOnlySpan<Vec3> box = BoxDirections;
        bool estimate = compliance.Emulates(StockQuirk.AmbientCubeReciprocalEstimate);

        for (int f = 0; f < flagged.Length; f++)
        {
            ref readonly DWorldLight wl = ref lights[flagged[f]];

            // TestLine( vStart4, wlOrigin4, &fractionVisible ); skip unless > 0.
            float visible = fractionVisible[f];
            if (!(visible > 0.0f))
            {
                continue;
            }

            Vec3 delta = wl.Origin - start;
            float distanceScale = DistanceFalloff(in wl, delta, estimate);

            (Vec3 deltaNorm, _) = estimate ? delta.NormaliseLikeStock() : delta.Normalise();
            float angleScale = WorldLightAngle(wl.Normal, deltaNorm, deltaNorm);

            float ratio = distanceScale * angleScale * visible;
            if (ratio == 0)
            {
                continue;
            }

            for (int j = 0; j < Sides; j++)
            {
                float t = Vec3.Dot(box[j], deltaNorm);
                if (t > 0)
                {
                    cube[j] += wl.Intensity * (t * ratio);
                }
            }
        }
    }

    /// <summary>
    /// <c>Engine_WorldLightDistanceFalloff</c>
    /// The <c>emit_surface</c> case.
    /// </summary>
    /// <param name="wl">The light.</param>
    /// <param name="delta">From the sample to the light.</param>
    /// <param name="estimate">Whether to use stock's <c>rcpss</c> estimate.</param>
    /// <returns>The falloff, or 0 when the sample is out of range.</returns>
    /// <remarks>
    /// A radius of zero means UNLIMITED, not "no light": the cull is skipped
    /// entirely.
    /// </remarks>
    internal static float DistanceFalloff(ref readonly DWorldLight wl, Vec3 delta, bool estimate)
    {
        if (wl.Radius != 0 && Vec3.Dot(delta, delta) > wl.Radius * wl.Radius)
        {
            return 0.0f;
        }

        return estimate ? InvRSquaredStock(delta) : InvRSquared(delta);
    }

    /// <summary>
    /// <c>Engine_WorldLightAngle</c>, the
    /// <c>emit_surface</c> case.
    /// </summary>
    /// <param name="lightNormal">The emitting surface's normal.</param>
    /// <param name="surfaceNormal">
    /// The receiving normal. The caller passes the direction to the light for
    /// both, which is stock's own call and makes the first dot product a
    /// self-dot of a unit vector.
    /// </param>
    /// <param name="delta">The unit direction from the sample to the light.</param>
    /// <returns>The angular term, or 0 when the sample is behind the surface.</returns>
    public static float WorldLightAngle(Vec3 lightNormal, Vec3 surfaceNormal, Vec3 delta)
    {
        float dot = Vec3.Dot(surfaceNormal, delta);
        if (dot < 0)
        {
            return 0;
        }

        float dot2 = -Vec3.Dot(delta, lightNormal);

        // ON_EPSILON is a DOUBLE 0.1, so this compares in double.
        if ((double)dot2 <= OnEpsilon / 10)
        {
            return 0;
        }

        return dot * dot2;
    }

    /// <summary><c>ON_EPSILON</c>, a double.</summary>
    private const double OnEpsilon = 0.1;

    /// <summary>
    /// <c>InvRSquared</c>: <c>1 / max(1, |v|^2)</c>.
    /// </summary>
    /// <param name="v">The vector.</param>
    /// <returns>The inverse square, floored at 1.</returns>
    /// <remarks>
    /// The floor at one unit is what stops a light at the sample position from
    /// producing an infinity, and it is also why
    /// <c>IsLeafAmbientSurfaceLight</c>'s threshold can be expressed as this
    /// function of a 512-unit vector.
    /// </remarks>
    internal static float InvRSquared(Vec3 v)
    {
        float r2 = Vec3.Dot(v, v);
        return 1.0f / (r2 < 1.0f ? 1.0f : r2);
    }

    /// <summary>
    /// <c>InvRSquared</c> as x86 actually computes it: <c>rcpss</c> over a
    /// nudged length, with no refinement.
    /// </summary>
    /// <param name="v">The vector.</param>
    /// <returns>The estimate.</returns>
    /// <remarks>
    /// <para>
    /// The <c>PLATFORM_INTEL</c> branch:
    /// <c>_mm_rcp_ss(_mm_max_ss(_mm_set_ss(1.0f), _mm_load_ss(&amp;sqrlen)))</c>
    /// over <c>sqrlen = x*x + y*y + z*z + 1.0e-10f</c>.
    /// </para>
    /// <para>
    /// It THROWS rather than falling back when SSE is missing: a silent fall
    /// back to the exact path would leave a differential against stock
    /// reporting success while no longer comparing the same thing.
    /// </para>
    /// </remarks>
    internal static float InvRSquaredStock(Vec3 v)
    {
        if (!Sse.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "InvRSquaredStock reproduces stock's rcpss estimate and has no meaning without SSE.");
        }

        float sqrlen = ((v.X * v.X) + (v.Y * v.Y) + (v.Z * v.Z)) + 1.0e-10f;

        Vector128<float> clamped = Sse.MaxScalar(
            Vector128.CreateScalarUnsafe(1.0f), Vector128.CreateScalarUnsafe(sqrlen));
        return Sse.ReciprocalScalar(clamped).ToScalar();
    }
}
