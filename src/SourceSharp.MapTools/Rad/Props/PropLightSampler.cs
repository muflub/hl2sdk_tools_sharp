using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rad.Props;

/// <summary><c>GATHERLFLAGS_*</c>.</summary>
[Flags]
public enum PropGatherFlags
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary><c>GATHERLFLAGS_FORCE_FAST</c>: a quarter of the sky samples.</summary>
    ForceFast = 1,

    /// <summary><c>GATHERLFLAGS_IGNORE_NORMALS</c>: <c>CONSTANT_DOT</c> for every dot.</summary>
    IgnoreNormals = 2,
}

/// <summary>What <c>GatherSampleLightSSE</c> returns for one sample.</summary>
/// <param name="Falloff"><c>m_flFalloff</c>.</param>
/// <param name="Dot"><c>m_flDot[0]</c>, after the final clamp.</param>
public readonly record struct PropLightSample(float Falloff, float Dot);

/// <summary>
/// <c>GatherSampleLightSSE</c> for ONE sample
/// position and ONE normal, the shape prop lighting calls it with
/// <c>facenum</c> -1, no flags, no epsilon.
/// </summary>
/// <remarks>
/// <para>
/// Stock evaluates four lanes of one duplicated vector, so a scalar port of
/// one lane is the same arithmetic. The SSE estimates are reproduced under
/// <see cref="StockQuirk.GatherReciprocalEstimate"/>:
/// <c>ReciprocalSqrtSIMD</c> is <c>rsqrtps</c> plus one Newton step,
/// <c>ReciprocalSIMD</c> is <c>rcpps</c> plus one, and <c>PowSIMD</c> is the
/// fixed-point exponent on <c>sqrtps</c>.
/// </para>
/// <para>
/// THE SEAM for the direct-lighting lane: this is the one place prop lighting
/// evaluates a light. When that lane's full <c>GatherSampleLightSSE</c> lands,
/// this class is what it replaces.
/// </para>
/// </remarks>
public sealed class PropLightSampler
{
    /// <summary><c>MAX_TRACE_LENGTH</c> as a float scale.</summary>
    public const float MaxTraceLength = (float)(1.732050807569 * 32768.0);

    /// <summary><c>DIST_EPSILON</c>(... the reference implementation's 0.03125).</summary>
    private const float DistEpsilon = 0.03125f;

    /// <summary><c>CONSTANT_DOT</c>.</summary>
    private const float ConstantDot = (float)(.7 / 2);

    private readonly KdRayTracer _environment;
    private readonly bool _estimate;
    private readonly float _sunAngularExtent;
    private readonly bool _fast;

    /// <summary>Makes a sampler.</summary>
    /// <param name="environment"><c>g_RtEnv</c>, for <c>TestLine</c>.</param>
    /// <param name="compliance">Whether to reproduce the SSE estimates.</param>
    /// <param name="sunAngularExtent"><c>g_SunAngularExtent</c>: 0 for a point sun.</param>
    /// <param name="fast"><c>do_fast</c>.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public PropLightSampler(KdRayTracer environment, ComplianceOptions compliance, float sunAngularExtent = 0, bool fast = false)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(compliance);
        _environment = environment;
        _estimate = compliance.Emulates(StockQuirk.GatherReciprocalEstimate) && Sse.IsSupported;
        _sunAngularExtent = sunAngularExtent;
        _fast = fast;
    }

    /// <summary>One light at one point (<c>GatherSampleLightSSE</c>).</summary>
    /// <param name="light">The light.</param>
    /// <param name="pos">The sample position.</param>
    /// <param name="normal">The sample normal.</param>
    /// <param name="flags"><c>GATHERLFLAGS_*</c>.</param>
    /// <param name="epsilon">
    /// <c>flEpsilon</c>: how far the sky-ambient traces start off the surface.
    /// </param>
    /// <param name="skipId">
    /// <c>static_prop_index_to_ignore</c> as the trace sees it
    /// (<c>TRACE_ID_STATICPROP | prop</c>), or -1 to shadow from everything.
    /// </param>
    /// <returns>Falloff and dot; both zero when the light cannot reach.</returns>
    public PropLightSample Gather(
        PropLight light, Vec3 pos, Vec3 normal, PropGatherFlags flags = PropGatherFlags.None, float epsilon = 0.0f, int skipId = -1)
    {
        ArgumentNullException.ThrowIfNull(light);

        PropLightSample s = light.Type switch
        {
            EmitType.SkyLight => Sky(light, pos, normal, flags, skipId),
            EmitType.Point or EmitType.Surface or EmitType.Spotlight => Standard(light, pos, normal, flags, skipId),
            EmitType.SkyAmbient => AmbientSky(pos, normal, flags, epsilon, skipId),
            _ => throw new InvalidOperationException($"Bad dl->light.type {light.Type}"),
        };

        // Out.m_flDot[0] = MaxSIMD(out.m_flDot[0], Four_Zeros).
        return s with { Dot = Max(s.Dot, 0.0f) };
    }

    /// <summary><c>GatherSampleStandardLightSSE</c>.</summary>
    private PropLightSample Standard(PropLight dl, Vec3 pos, Vec3 normal, PropGatherFlags flags, int skipId)
    {
 // facenum is always -1 (AllocDLight), so src is the origin.
        Vec3 src = dl.Origin;

        Vec3 delta = src - pos;
        float dist2 = (delta.X * delta.X) + (delta.Y * delta.Y) + (delta.Z * delta.Z);
        float rpcDist = ReciprocalSqrt(dist2);
        delta = new Vec3(delta.X * rpcDist, delta.Y * rpcDist, delta.Z * rpcDist);
        float dist = MathF.Sqrt(dist2);

        float dot = (flags & PropGatherFlags.IgnoreNormals) != 0
            ? ConstantDot
            : (delta.X * normal.X) + (delta.Y * normal.Y) + (delta.Z * normal.Z);
        dot = Max(0.0f, dot);

        bool hardFalloff = dl.EndFadeDistance > dl.StartFadeDistance;
        if (hardFalloff)
        {
            if (!(dist <= dl.EndFadeDistance))
            {
                return default;
            }
        }

        dist = Max(dist, 1.0f);
        float falloffEvalDist = Min(dist, dl.CapDist);

        float falloff = 0;
        switch (dl.Type)
        {
            case EmitType.Point:
            {
                falloff = falloffEvalDist * falloffEvalDist;
                falloff *= dl.QuadraticAttn;
                falloff += dl.LinearAttn * falloffEvalDist;
                falloff += dl.ConstantAttn;
                falloff = Reciprocal(falloff);
                break;
            }

            case EmitType.Surface:
            {
                float dot2 = (delta.X * dl.Normal.X) + (delta.Y * dl.Normal.Y) + (delta.Z * dl.Normal.Z);
                dot2 = -dot2;
                dot2 = Max(0.0f, dot2);
                if (0.0f == dot)
                {
                    return default;
                }

                falloff = Reciprocal(dist2);
                falloff *= dot2;

                // Move the endpoint off the surface so the trace does not hit it.
                src = new Vec3(
                    src.X + (dl.Normal.X * DistEpsilon),
                    src.Y + (dl.Normal.Y * DistEpsilon),
                    src.Z + (dl.Normal.Z * DistEpsilon));
                break;
            }

            case EmitType.Spotlight:
            {
                float dot2 = (delta.X * dl.Normal.X) + (delta.Y * dl.Normal.Y) + (delta.Z * dl.Normal.Z);
                dot2 = -dot2;
                if (!(dot2 > dl.StopDot2))
                {
                    return default;
                }

                falloff = falloffEvalDist * falloffEvalDist;
                falloff *= dl.QuadraticAttn;
                falloff += dl.LinearAttn * falloffEvalDist;
                falloff += dl.ConstantAttn;
                falloff = Reciprocal(falloff);
                falloff *= dot2;

                bool inFringe = dot2 <= dl.StopDot;
                float mult = Reciprocal(dl.StopDot - dl.StopDot2);
                mult *= dot2 - dl.StopDot2;
                mult = Min(mult, 1.0f);
                mult = Max(mult, 0.0f);

                if (dl.Exponent != 0.0f && dl.Exponent != 1.0f)
                {
                    mult = PowFixed(mult, (int)(4.0 * dl.Exponent));
                }

                if (!inFringe)
                {
                    mult = 1.0f;
                }

                falloff = mult * falloff;
                break;
            }
        }

        if (hardFalloff)
        {
            float t = Reciprocal(dl.EndFadeDistance - dl.StartFadeDistance);
            t *= dist - dl.StartFadeDistance;
            t = Min(t, 1.0f);
            t = Max(t, 0.0f);
            t = 1.0f - t;

            float mult = (6.0f * t) - 15.0f;
            mult = (mult * t) + 10.0f;
            mult = (t * t) * mult;
            mult = t * mult;
            falloff = mult * falloff;
        }

        // TestLine( pos, src ): fraction visible 0 or 1 (texture shadows off).
        Span<Vec3> starts = [pos];
        Span<Vec3> ends = [src];
        Span<bool> blocked = [false];
        _environment.TestLines(starts, ends, blocked, _estimate, skipId: skipId);
        if (blocked[0])
        {
            dot = 0.0f * dot;
        }

        return new PropLightSample(falloff, dot);
    }

    /// <summary><c>GatherSampleSkyLightSSE</c>.</summary>
    private PropLightSample Sky(PropLight dl, Vec3 pos, Vec3 normal, PropGatherFlags flags, int skipId)
    {
        float dot = (flags & PropGatherFlags.IgnoreNormals) != 0
            ? ConstantDot
            : -((normal.X * dl.Normal.X) + (normal.Y * dl.Normal.Y) + (normal.Z * dl.Normal.Z));
        dot = Max(dot, 0.0f);
        if (dot == 0.0f)
        {
            return default;
        }

        int nsamples = 1;
        if (_sunAngularExtent > 0.0f)
        {
            nsamples = 30;
            if (_fast || (flags & PropGatherFlags.ForceFast) != 0)
            {
                nsamples /= 4;
            }
        }

        float total = 0.0f;
        DirectionalSampler sampler = new();
        Span<Vec3> starts = [pos];
        Span<Vec3> ends = [default];
        Span<bool> blocked = [false];
        for (int d = 0; d < nsamples; d++)
        {
            Vec3 delta = new(
                dl.Normal.X * -MaxTraceLength, dl.Normal.Y * -MaxTraceLength, dl.Normal.Z * -MaxTraceLength);
            if (d != 0)
            {
                Vec3 ofs = sampler.NextValue();
                float scale = MaxTraceLength * _sunAngularExtent;
                ofs = new Vec3(ofs.X * scale, ofs.Y * scale, ofs.Z * scale);
                delta += ofs;
            }

            ends[0] = delta + pos;
            _environment.TestLines(starts, ends, blocked, _estimate, skyDoesNotBlock: true, skipId: skipId);
            total += blocked[0] ? 0.0f : 1.0f;
        }

        float seeAmount = total * (1.0f / nsamples);
        return new PropLightSample(1.0f, dot * seeAmount);
    }

    /// <summary><c>GatherSampleAmbientSkySSE</c>, one normal.</summary>
    private PropLightSample AmbientSky(Vec3 pos, Vec3 normal, PropGatherFlags flags, float epsilon, int skipId)
    {
        bool ignoreNormals = (flags & PropGatherFlags.IgnoreNormals) != 0;

        float sumdot = 0.0f;
        float ambient = 0.0f;
        float possibleHitCount = 0.0f;

        // NUMVERTEXNORMALS / 4 fast, else times g_flSkySampleScale (1).
        int nsky = 162;
        if (_fast || (flags & PropGatherFlags.ForceFast) != 0)
        {
            nsky /= 4;
        }

        DirectionalSampler sampler = new();
        Span<Vec3> starts = [default];
        Span<Vec3> ends = [default];
        Span<bool> blocked = [false];
        const float EqualEpsilon = (float)0.001;

        for (int j = 0; j < nsky; j++)
        {
            Vec3 anorm = sampler.NextValue();
            float dot = ignoreNormals
                ? ConstantDot
                : -((normal.X * anorm.X) + (normal.Y * anorm.Y) + (normal.Z * anorm.Z));

            if (!(dot > EqualEpsilon))
            {
                continue;
            }

            sumdot = dot + sumdot;
            possibleHitCount = 1.0f + possibleHitCount;

            // delta = anorm * -MAX_TRACE_LENGTH + pos; surfacePos = pos - anorm * -epsilon.
            Vec3 delta = new(anorm.X * -MaxTraceLength, anorm.Y * -MaxTraceLength, anorm.Z * -MaxTraceLength);
            ends[0] = delta + pos;
            Vec3 offset = new(anorm.X * -epsilon, anorm.Y * -epsilon, anorm.Z * -epsilon);
            starts[0] = pos - offset;

            _environment.TestLines(starts, ends, blocked, _estimate, skyDoesNotBlock: true, skipId: skipId);
            float fractionVisible = blocked[0] ? 0.0f : 1.0f;
            ambient += fractionVisible * dot;
        }

        // 1824-1832, normalCount 1: factor = 1/count * count; dot = ambient * 1/(factor*sumdot).
        float factor = Reciprocal(possibleHitCount);
        factor *= possibleHitCount;
        float d = factor * sumdot;
        d = Reciprocal(d);
        d = ambient * d;
        return new PropLightSample(1.0f, d);
    }

    /// <summary><c>ReciprocalSqrtSIMD</c> or the exact value.</summary>
    private float ReciprocalSqrt(float a)
    {
        if (!_estimate)
        {
            return 1.0f / MathF.Sqrt(a);
        }

        Vector128<float> va = Vector128.CreateScalarUnsafe(a);
        Vector128<float> g = Sse.ReciprocalSqrtScalar(va);

        // y(n+1) = 1/2 (y(n) * (3 - a * y(n)^2))
        float guess = g.ToScalar();
        guess *= 3.0f - (a * (guess * guess));
        return 0.5f * guess;
    }

    /// <summary><c>ReciprocalSIMD</c> or the exact value.</summary>
    private float Reciprocal(float a)
    {
        if (!_estimate)
        {
            return 1.0f / a;
        }

        float est = Sse.ReciprocalScalar(Vector128.CreateScalarUnsafe(a)).ToScalar();
        return (est + est) - (a * (est * est));
    }

    /// <summary><c>Pow_FixedPoint_Exponent_SIMD</c>.</summary>
    /// <param name="x">The base.</param>
    /// <param name="exponent">Four times the exponent, truncated.</param>
    /// <returns>The power.</returns>
    public static float PowFixed(float x, int exponent)
    {
        float rslt = 1.0f;
        int xp = Math.Abs(exponent);
        if ((xp & 3) != 0)
        {
            float sqRt = MathF.Sqrt(x);
            if ((xp & 1) != 0)
            {
                rslt = MathF.Sqrt(sqRt);
            }

            if ((xp & 2) != 0)
            {
                rslt *= sqRt;
            }
        }

        xp >>= 2;
        float curpower = x;
        while (true)
        {
            if ((xp & 1) != 0)
            {
                rslt *= curpower;
            }

            xp >>= 1;
            if (xp != 0)
            {
                curpower *= curpower;
            }
            else
            {
                break;
            }
        }

        // ReciprocalEstSaturateSIMD for a negative exponent: never reached from
        // the spot-light path, whose exponent is a light key.
        return exponent < 0 ? 1.0f / (rslt == 0 ? 1.1920929e-07f : rslt) : rslt;
    }

    /// <summary><c>maxps</c>: <c>a &gt; b ? a : b</c>.</summary>
    private static float Max(float a, float b) => a > b ? a : b;

    /// <summary><c>minps</c>: <c>a &lt; b ? a : b</c>.</summary>
    private static float Min(float a, float b) => a < b ? a : b;
}

/// <summary>
/// <c>DirectionalSampler_t</c>: Halton-sequence sphere
/// samples, bases 2 and 3.
/// </summary>
/// <remarks>
/// <c>HaltonSequenceGenerator_t::GetElement</c> ignores
/// its argument and reads the member <c>seed</c>, which <c>NextValue</c>'s
/// <c>seed++</c> has already advanced -- so the first value drawn is element
/// TWO, not one. Reproduced: it is the sequence stock samples.
/// </remarks>
public struct DirectionalSampler
{
    private int _zSeed;
    private int _rSeed;

    /// <summary>The next direction (<c>DirectionalSampler_t::NextValue</c>).</summary>
    /// <returns>A unit vector.</returns>
    public Vec3 NextValue()
    {
        float z = Element(2, ref _zSeed);
        z = (2 * z) - 1.0f;
        float phi = MathF.Acos(z);
        float theta = (float)(2.0 * Math.PI * Element(3, ref _rSeed));
        float sinP = MathF.Sin(phi);
        return new Vec3(MathF.Cos(theta) * sinP, MathF.Sin(theta) * sinP, z);
    }

    private static float Element(int b, ref int seed)
    {
        // seed starts at 1; NextValue passes seed++ and GetElement reads the
        // incremented member.
        if (seed == 0)
        {
            seed = 1;
        }

        seed++;
        int tmpseed = seed;
        float ret = 0.0f;
        float fbase = b;
        float baseInv = (float)(1.0 / fbase);
        while (tmpseed != 0)
        {
            int dig = tmpseed % b;
            ret += dig * baseInv;
            baseInv /= fbase;
            tmpseed /= b;
        }

        return ret;
    }
}
