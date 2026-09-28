//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.Intrinsics;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// <c>TestLine</c> as the per-ray loop answered it before segments went
/// through the <see cref="IRayTracer"/> seam, written out independently of
/// <see cref="Ray.Segment"/>, the tracer's isolated-ray path and its sky-lane
/// mask: a reference for the facts that pin the batched code.
/// </summary>
/// <remarks>
/// <para>
/// WHY A COPY. The batched samplers, the seam and <c>KdRayTracer.TestLines</c>
/// were rewritten together onto <see cref="Ray.Segment"/> and one
/// isolated-trace helper, so comparing any two of them passes a mistake they
/// share. This keeps what the old loop did, step by step: the length as
/// <c>sqrt(x*x + y*y + z*z)</c>; its reciprocal as the estimate plus one Newton
/// step (<c>2e - len*e*e</c>) when stock's reciprocal is asked for and the CPU
/// has an estimate, the exact division otherwise; the unit direction times
/// that; the segment in all four lanes; and "blocked" as a first hit strictly
/// before the length that is not a sky triangle when the sky does not block.
/// </para>
/// <para>
/// Four copies of the ray go through the closest-hit trace, which loads its
/// packet as it always did and reports the first hit's surface id and its
/// distance over the ray's reach. "Strictly before the length" is then
/// <c>Fraction &lt; 1</c>: dividing by a positive length is monotone, and a
/// distance below the length divides to at most the float below 1, so the
/// division cannot move a hit across the end. The KD traversal is the one
/// piece both sides share; the rewrite did not change it.
/// </para>
/// </remarks>
internal static class OldTestLine
{
    /// <summary>Whether the segment from <paramref name="start"/> to <paramref name="end"/> is blocked.</summary>
    public static bool Blocked(
        KdRayTracer tracer, Vec3 start, Vec3 end, bool stockReciprocal, bool skyDoesNotBlock = false, int skipId = -1)
    {
        Vec3 d = end - start;
        float len = MathF.Sqrt((d.X * d.X) + (d.Y * d.Y) + (d.Z * d.Z));
        float inv;
        if (stockReciprocal && FloatEstimate.IsSupported)
        {
            Vector128<float> a = Vector128.Create(len);
            Vector128<float> est = FloatEstimate.Reciprocal(a);
            inv = Vector128.Subtract(Vector128.Add(est, est), Vector128.Multiply(a, Vector128.Multiply(est, est))).ToScalar();
        }
        else
        {
            inv = 1.0f / len;
        }

        Ray ray = new(start.X, start.Y, start.Z, d.X * inv, d.Y * inv, d.Z * inv, len);
        Ray[] four = [ray, ray, ray, ray];
        HitId[] hits = new HitId[4];
        tracer.TraceClosest(four, hits, RayTraceOptions.StockExact with { SkipId = skipId < 0 ? null : skipId });
        if (hits[1] != hits[0] || hits[2] != hits[0] || hits[3] != hits[0])
        {
            throw new InvalidOperationException("four copies of one ray answered differently");
        }

        HitId hit = hits[0];
        return hit.IsHit && hit.Fraction < 1.0f
            && !(skyDoesNotBlock && (hit.Surface & TraceId.Sky) != 0);
    }
}
