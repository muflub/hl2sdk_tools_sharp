//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Concurrent;

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Ambient;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Ambient;

/// <summary>
/// How many surface-light lines each leaf of the leaf-ambient stage should
/// trace, worked out in the test from the samples and the lights rather than
/// by asking the code under test.
/// </summary>
/// <remarks>
/// <para>
/// A (sample, baked light) pair needs its line unless the light's unoccluded
/// scale, <c>distanceScale * angleScale</c>, is zero. Spelled out from the
/// light's definition, that is zero exactly when the sample is out of the
/// light's radius (a nonzero radius and <c>|delta|^2 &gt; radius^2</c>) or the
/// emitter's cosine <c>-(dir . normal)</c> is not above <c>ON_EPSILON / 10</c>
/// (0.01, compared in double). The inverse-square term is never zero for a
/// finite delta (it is floored at one unit), and the receiving dot is a
/// self-dot of a unit vector, never negative. Every comparison is written so
/// that a NaN fails it, as the product's comparisons do: a NaN scale is not
/// zero, so its pair is traced.
/// </para>
/// <para>
/// The direction is normalised the way the compliance mode says, with the
/// vector primitive itself (<see cref="Vec3.NormaliseLikeStock"/> or
/// <see cref="Vec3.Normalise"/>), so the count agrees with the product to the
/// bit even for a pair sitting on the 0.01 threshold.
/// </para>
/// </remarks>
internal static class SurfaceLightPairs
{
    /// <summary>ON_EPSILON (a double 0.1) over ten.</summary>
    private const double BehindThreshold = 0.01;

    /// <summary>Whether a pair's line must be traced, from the light's definition.</summary>
    internal static bool NeedsLine(in DWorldLight light, Vec3 start, bool estimate)
    {
        Vec3 delta = light.Origin - start;
        float r2 = (delta.X * delta.X) + (delta.Y * delta.Y) + (delta.Z * delta.Z);
        if (light.Radius != 0 && r2 > light.Radius * light.Radius)
        {
            return false;
        }

        Vec3 dir = estimate ? delta.NormaliseLikeStock().Normalised : delta.Normalise().Normalised;
        float cosine = -((dir.X * light.Normal.X) + (dir.Y * light.Normal.Y) + (dir.Z * light.Normal.Z));
        return !((double)cosine <= BehindThreshold);
    }

    /// <summary>Whether the compliance mode normalises with stock's estimate.</summary>
    internal static bool Estimate(ComplianceOptions compliance) =>
        compliance.Emulates(StockQuirk.AmbientCubeReciprocalEstimate);

    /// <summary>
    /// The baked lights a leaf's segments end at, recovered from the segment
    /// ends (the flagged lights' origins, in lump order) by walking the lump.
    /// </summary>
    internal static DWorldLight[] LightsAt(ReadOnlySpan<DWorldLight> lump, Vec3[] ends)
    {
        DWorldLight[] lights = new DWorldLight[ends.Length];
        int i = 0;
        for (int e = 0; e < ends.Length; e++)
        {
            while (i < lump.Length && !lump[i].Origin.Equals(ends[e]))
            {
                i++;
            }

            Assert.True(i < lump.Length, $"no light at segment end {e}");
            lights[e] = lump[i++];
        }

        return lights;
    }

    /// <summary>How many of a leaf's pairs need a line.</summary>
    internal static int Traced(Vec3[] starts, DWorldLight[] lights, bool estimate)
    {
        int count = 0;
        foreach (Vec3 start in starts)
        {
            foreach (DWorldLight light in lights)
            {
                count += NeedsLine(in light, start, estimate) ? 1 : 0;
            }
        }

        return count;
    }

    /// <summary>
    /// A visibility that records each leaf's call -- its samples and the
    /// light origins -- and answers through another visibility, or "all
    /// visible" when it has none.
    /// </summary>
    /// <remarks>
    /// Not a <see cref="TracerLineVisibility"/>, so the stage takes its
    /// per-call path: one call per leaf, asking about every sample and every
    /// baked light.
    /// </remarks>
    internal sealed class LeafRecorder(IAmbientLightVisibility? inner = null) : IAmbientLightVisibility
    {
        public ConcurrentQueue<(Vec3[] Starts, Vec3[] Ends)> Leaves { get; } = new();

        public void FractionsVisible(Vec3 start, ReadOnlySpan<Vec3> ends, Span<float> fractions) =>
            FractionsVisible([start], ends, fractions);

        public void FractionsVisible(ReadOnlySpan<Vec3> starts, ReadOnlySpan<Vec3> ends, Span<float> fractions)
        {
            Leaves.Enqueue((starts.ToArray(), ends.ToArray()));
            if (inner is null)
            {
                fractions[..(starts.Length * ends.Length)].Fill(1.0f);
            }
            else
            {
                inner.FractionsVisible(starts, ends, fractions);
            }
        }

        /// <summary>Each recorded leaf's expected traced count, in call order.</summary>
        public int[] TracedPerLeaf(ReadOnlySpan<DWorldLight> lump, ComplianceOptions compliance)
        {
            bool estimate = Estimate(compliance);
            List<int> counts = [];
            foreach ((Vec3[] starts, Vec3[] ends) in Leaves)
            {
                counts.Add(Traced(starts, LightsAt(lump, ends), estimate));
            }

            return [.. counts];
        }
    }
}
