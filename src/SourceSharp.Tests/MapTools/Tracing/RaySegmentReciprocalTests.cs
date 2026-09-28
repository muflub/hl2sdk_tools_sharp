//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.Intrinsics;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// <see cref="Ray.Segment"/>, the one place a <c>TestLine</c> segment becomes a
/// ray: its reciprocal is the caller's compliance decision, and each side is
/// what it says.
/// </summary>
/// <remarks>
/// The helper takes no estimate of its own accord: every caller passes the
/// flag its own quirk decided (<c>GatherReciprocalEstimate</c> for the prop
/// sampler, <c>AmbientCubeReciprocalEstimate</c> for leaf ambient), so under
/// the default policy the segment is always the divide. These facts pin both
/// sides so that stays true.
/// </remarks>
public sealed class RaySegmentReciprocalTests
{
    private static IEnumerable<(Vec3 Start, Vec3 End)> Segments()
    {
        Random random = new(23);
        for (int i = 0; i < 2048; i++)
        {
            float scale = (float)Math.Pow(10, (random.NextDouble() * 6) - 2);
            Vec3 start = new(Next(random, 4000), Next(random, 4000), Next(random, 4000));
            yield return (start, start + new Vec3(Next(random, scale), Next(random, scale), Next(random, scale)));
        }
    }

    private static float Next(Random random, float scale) => (float)((random.NextDouble() * 2) - 1) * scale;

    private static float Length(Vec3 d) => MathF.Sqrt((d.X * d.X) + (d.Y * d.Y) + (d.Z * d.Z));

    /// <summary>Without the estimate the direction is the segment times the divided reciprocal of its length.</summary>
    [Fact]
    public void TheExactSegmentDividesItsLength()
    {
        foreach ((Vec3 start, Vec3 end) in Segments())
        {
            Vec3 d = end - start;
            float len = Length(d);
            float inv = 1.0f / len;
            Ray ray = Ray.Segment(start, end, stockReciprocal: false);
            Assert.Equal(
                (d.X * inv, d.Y * inv, d.Z * inv, len),
                (ray.DirectionX, ray.DirectionY, ray.DirectionZ, ray.MaxDistance));
        }
    }

    /// <summary>
    /// With the estimate the reciprocal is the estimate plus one Newton step,
    /// and some segments' directions differ from the exact ones on this CPU.
    /// </summary>
    [Fact]
    public void TheStockSegmentTakesTheEstimatedReciprocal()
    {
        int differs = 0;
        foreach ((Vec3 start, Vec3 end) in Segments())
        {
            Vec3 d = end - start;
            float len = Length(d);
            Vector128<float> a = Vector128.Create(len);
            Vector128<float> est = FloatEstimate.Reciprocal(a);
            float inv = Vector128.Subtract(Vector128.Add(est, est), Vector128.Multiply(a, Vector128.Multiply(est, est))).ToScalar();

            Ray ray = Ray.Segment(start, end, stockReciprocal: true);
            Assert.Equal((d.X * inv, d.Y * inv, d.Z * inv), (ray.DirectionX, ray.DirectionY, ray.DirectionZ));
            if (ray.DirectionX != Ray.Segment(start, end, stockReciprocal: false).DirectionX)
            {
                differs++;
            }
        }

        Assert.True(differs > 0, "the estimate matched the divide on every segment");
    }
}
