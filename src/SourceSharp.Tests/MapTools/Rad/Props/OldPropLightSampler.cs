//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Props;
using SourceSharp.MapTools.Tracing;
using SourceSharp.Tests.MapTools.Tracing;

namespace SourceSharp.Tests.MapTools.Rad.Props;

/// <summary>
/// <c>GatherSampleLightSSE</c> for one light at one point, as
/// <see cref="PropLightSampler"/> computed it before it was split into Plan,
/// Trace and Resolve: one sample at a time, each segment tested on its own the
/// moment the sample needs it. A reference for
/// <see cref="PropLineBatchingTests"/>.
/// </summary>
/// <remarks>
/// <para>
/// WHY A COPY. After the split, <see cref="PropLightSampler.Gather"/> is itself
/// Plan, a one-sample batch, and Resolve, so a batch compared with
/// <c>Gather</c> compares the new code with the new code: a mistake in Plan or
/// Resolve (a segment planned from the wrong point, the sun's samples summed
/// in another order, the ambient sky's dot paired with the wrong segment)
/// shows on both sides. This class keeps the one-call arithmetic in the
/// one-call order and asks <see cref="OldTestLine"/> -- itself independent of
/// <see cref="Ray.Segment"/> and the seam -- about each segment in turn.
/// </para>
/// <para>
/// The only shared pieces are <see cref="DirectionalSampler"/> (the Halton
/// directions, which the split did not touch) and <see cref="PropLight"/>.
/// Everything else, including the estimate-based reciprocals and the fixed
/// point power, is written out again here.
/// </para>
/// </remarks>
internal sealed class OldPropLightSampler(KdRayTracer environment, bool stock, float sunAngularExtent = 0, bool fast = false)
{
    private const float MaxTraceLength = (float)(1.732050807569 * 32768.0);
    private const float DistEpsilon = 0.03125f;
    private const float ConstantDot = (float)(.7 / 2);

    private readonly bool _estimate = stock && FloatEstimate.IsSupported;

    /// <summary>One light at one point.</summary>
    public PropLightSample Gather(
        PropLight light, Vec3 pos, Vec3 normal, PropGatherFlags flags = PropGatherFlags.None, float epsilon = 0.0f, int skipId = -1)
    {
        PropLightSample s = light.Type switch
        {
            EmitType.SkyLight => Sky(light, pos, normal, flags, skipId),
            EmitType.Point or EmitType.Surface or EmitType.Spotlight => Standard(light, pos, normal, flags, skipId),
            EmitType.SkyAmbient => AmbientSky(pos, normal, flags, epsilon, skipId),
            _ => throw new InvalidOperationException($"Bad light type {light.Type}"),
        };

        return s with { Dot = Max(s.Dot, 0.0f) };
    }

    private bool Blocked(Vec3 start, Vec3 end, bool skyDoesNotBlock, int skipId) =>
        OldTestLine.Blocked(environment, start, end, _estimate, skyDoesNotBlock, skipId);

    private PropLightSample Standard(PropLight dl, Vec3 pos, Vec3 normal, PropGatherFlags flags, int skipId)
    {
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
        if (hardFalloff && !(dist <= dl.EndFadeDistance))
        {
            return default;
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

        if (Blocked(pos, src, skyDoesNotBlock: false, skipId))
        {
            dot = 0.0f * dot;
        }

        return new PropLightSample(falloff, dot);
    }

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
        if (sunAngularExtent > 0.0f)
        {
            nsamples = 30;
            if (fast || (flags & PropGatherFlags.ForceFast) != 0)
            {
                nsamples /= 4;
            }
        }

        float total = 0.0f;
        DirectionalSampler sampler = new();
        for (int d = 0; d < nsamples; d++)
        {
            Vec3 delta = new(
                dl.Normal.X * -MaxTraceLength, dl.Normal.Y * -MaxTraceLength, dl.Normal.Z * -MaxTraceLength);
            if (d != 0)
            {
                Vec3 ofs = sampler.NextValue();
                float scale = MaxTraceLength * sunAngularExtent;
                ofs = new Vec3(ofs.X * scale, ofs.Y * scale, ofs.Z * scale);
                delta += ofs;
            }

            total += Blocked(pos, delta + pos, skyDoesNotBlock: true, skipId) ? 0.0f : 1.0f;
        }

        float seeAmount = total * (1.0f / nsamples);
        return new PropLightSample(1.0f, dot * seeAmount);
    }

    private PropLightSample AmbientSky(Vec3 pos, Vec3 normal, PropGatherFlags flags, float epsilon, int skipId)
    {
        bool ignoreNormals = (flags & PropGatherFlags.IgnoreNormals) != 0;

        float sumdot = 0.0f;
        float ambient = 0.0f;
        float possibleHitCount = 0.0f;

        int nsky = 162;
        if (fast || (flags & PropGatherFlags.ForceFast) != 0)
        {
            nsky /= 4;
        }

        DirectionalSampler sampler = new();
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

            Vec3 delta = new(anorm.X * -MaxTraceLength, anorm.Y * -MaxTraceLength, anorm.Z * -MaxTraceLength);
            Vec3 end = delta + pos;
            Vec3 offset = new(anorm.X * -epsilon, anorm.Y * -epsilon, anorm.Z * -epsilon);
            float fractionVisible = Blocked(pos - offset, end, skyDoesNotBlock: true, skipId) ? 0.0f : 1.0f;
            ambient += fractionVisible * dot;
        }

        float factor = Reciprocal(possibleHitCount);
        factor *= possibleHitCount;
        float d2 = factor * sumdot;
        d2 = Reciprocal(d2);
        d2 = ambient * d2;
        return new PropLightSample(1.0f, d2);
    }

    private float ReciprocalSqrt(float a)
    {
        if (!_estimate)
        {
            return 1.0f / MathF.Sqrt(a);
        }

        float guess = FloatEstimate.ReciprocalSqrt(a);
        guess *= 3.0f - (a * (guess * guess));
        return 0.5f * guess;
    }

    private float Reciprocal(float a)
    {
        if (!_estimate)
        {
            return 1.0f / a;
        }

        float est = FloatEstimate.Reciprocal(a);
        return (est + est) - (a * (est * est));
    }

    private static float PowFixed(float x, int exponent)
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
            if (xp == 0)
            {
                break;
            }

            curpower *= curpower;
        }

        return exponent < 0 ? 1.0f / (rslt == 0 ? 1.1920929e-07f : rslt) : rslt;
    }

    private static float Max(float a, float b) => a > b ? a : b;

    private static float Min(float a, float b) => a < b ? a : b;
}
