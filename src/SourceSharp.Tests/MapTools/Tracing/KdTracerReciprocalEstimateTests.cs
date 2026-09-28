//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.Intrinsics;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// <see cref="StockQuirk.KdTracerReciprocalEstimate"/>: the KD tracer's two
/// estimates, the ray direction's reciprocal in traversal and the triangle
/// planes' normalise, on each side of the quirk.
/// </summary>
/// <remarks>
/// <para>
/// The Stock facts pin the estimate's arithmetic (the estimate, then one
/// Newton step in stock's operand order) and show it is not a divide on this
/// CPU; the Correct facts pin the divide bit for bit. Neither asserts a
/// particular estimate value, which is the CPU's: the Correct facts hold on
/// every CPU because nothing they check came from an estimate, and the Stock
/// facts on every CPU because every estimate instruction is a few ulps from
/// the divide somewhere in a sample of thousands.
/// </para>
/// <para>
/// The Stock side here is the Correct policy with only this quirk flipped, so
/// that <see cref="StockQuirk.KdZeroDirectionReachCut"/> cannot be what moved
/// a hit.
/// </para>
/// </remarks>
public sealed class KdTracerReciprocalEstimateTests
{
    /// <summary>Correct in every respect but this quirk.</summary>
    private static readonly ComplianceOptions EstimateOnly =
        ComplianceOptions.Correct.Flipping(StockQuirk.KdTracerReciprocalEstimate);

    /// <summary>
    /// A deterministic sample of direction components: every sign, magnitudes
    /// from 1e-6 to 1e4, and the zeros the saturation replaces.
    /// </summary>
    private static float[] Components()
    {
        Random random = new(15);
        List<float> values = [0f, -0f, 1f, -1f, 0.5f, 3f, float.Epsilon * 1e30f];
        for (int i = 0; i < 4096; i++)
        {
            float magnitude = (float)Math.Pow(10, (random.NextDouble() * 10) - 6);
            values.Add(random.Next(2) == 0 ? magnitude : -magnitude);
        }

        return [.. values];
    }

    /// <summary>
    /// A deterministic scene of triangles in general position: none is
    /// axis-aligned, so every plane normal is a normalise the estimate reaches.
    /// </summary>
    private static TracedTriangle[] Scene()
    {
        Random random = new(33);
        TracedTriangle[] triangles = new TracedTriangle[512];
        for (int i = 0; i < triangles.Length; i++)
        {
            Vec3 centre = new(Next(random, 900), Next(random, 900), Next(random, 900));
            triangles[i] = new TracedTriangle(
                i,
                centre + new Vec3(Next(random, 60), Next(random, 60), Next(random, 60)),
                centre + new Vec3(Next(random, 60), Next(random, 60), Next(random, 60)),
                centre + new Vec3(Next(random, 60), Next(random, 60), Next(random, 60)),
                0);
        }

        return triangles;
    }

    private static float Next(Random random, float scale) => (float)((random.NextDouble() * 2) - 1) * scale;

    /// <summary>
    /// Under Stock the traversal reciprocal is the estimate plus one Newton
    /// step, zero components saturated first, and is not the divide.
    /// </summary>
    [Fact]
    public void StockTraversalTakesTheEstimatedReciprocal()
    {
        Vector128<float> substitute = Vector128.Create(KdRayTracer.CorrectZeroSubstitute);
        int differs = 0;
        foreach (float a in Components())
        {
            float safe = a == 0 ? MathF.CopySign(KdRayTracer.CorrectZeroSubstitute, a) : a;
            float estimate = FloatEstimate.Reciprocal(safe);
            float expected = (estimate + estimate) - (safe * (estimate * estimate));

            float actual = KdRayTracer.ReciprocalSaturate(Vector128.Create(a), substitute).ToScalar();

            Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(actual));
            if (BitConverter.SingleToInt32Bits(actual) != BitConverter.SingleToInt32Bits(1f / safe))
            {
                differs++;
            }
        }

        Assert.True(differs > 0, "the estimate matched the divide on every sample, so this CPU cannot show the quirk");
    }

    /// <summary>
    /// Under Correct the traversal reciprocal is the IEEE divide of the
    /// saturated component, bit for bit, on every lane; a zero becomes
    /// exactly <c>+-2^60</c>.
    /// </summary>
    [Fact]
    public void CorrectTraversalDivides()
    {
        Vector128<float> substitute = Vector128.Create(KdRayTracer.CorrectZeroSubstitute);
        float[] components = Components();
        for (int i = 0; i + 4 <= components.Length; i += 4)
        {
            Vector128<float> a = Vector128.Create(components, i);
            Vector128<float> actual = KdRayTracer.ReciprocalSaturateExact(a, substitute);
            for (int lane = 0; lane < 4; lane++)
            {
                float c = components[i + lane];
                float safe = c == 0 ? MathF.CopySign(KdRayTracer.CorrectZeroSubstitute, c) : c;
                Assert.Equal(BitConverter.SingleToInt32Bits(1f / safe), BitConverter.SingleToInt32Bits(actual.GetElement(lane)));
            }
        }

        Assert.Equal(0x5D800000, BitConverter.SingleToInt32Bits(
            KdRayTracer.ReciprocalSaturateExact(Vector128<float>.Zero, substitute).ToScalar()));
        Assert.Equal(unchecked((int)0xDD800000), BitConverter.SingleToInt32Bits(
            KdRayTracer.ReciprocalSaturateExact(Vector128.Create(-0f), substitute).ToScalar()));
    }

    /// <summary>
    /// Under Stock every triangle's plane normal is the estimate's normalise
    /// of its edge cross, and some are not the divide's.
    /// </summary>
    [Fact]
    public void StockTrianglePlanesTakeTheEstimatedNormal()
    {
        TracedTriangle[] scene = Scene();
        KdRayTracer tracer = KdRayTracer.Build(scene, EstimateOnly);
        Assert.True(tracer.StockReciprocalEstimate);

        int differs = 0;
        for (int i = 0; i < scene.Length; i++)
        {
            Vec3 cross = Vec3.Cross(scene[i].V1 - scene[i].V0, scene[i].V2 - scene[i].V0);
            Vec3 normal = tracer.Triangle(i).Normal;
            AssertSameBits(cross.NormaliseLikeStock().Normalised, normal);
            if (!SameBits(cross.Normalise().Normalised, normal))
            {
                differs++;
            }
        }

        Assert.True(differs > 0, "the estimate matched the divide on every triangle, so this CPU cannot show the quirk");
    }

    /// <summary>
    /// Under Correct every triangle's plane normal is the divide's normalise
    /// of its edge cross, and the plane distance is taken from it.
    /// </summary>
    [Fact]
    public void CorrectTrianglePlanesTakeTheExactNormal()
    {
        TracedTriangle[] scene = Scene();
        KdRayTracer tracer = KdRayTracer.Build(scene, ComplianceOptions.Correct);
        Assert.False(tracer.StockReciprocalEstimate);

        for (int i = 0; i < scene.Length; i++)
        {
            Vec3 cross = Vec3.Cross(scene[i].V1 - scene[i].V0, scene[i].V2 - scene[i].V0);
            (Vec3 exact, _) = cross.Normalise();
            (Vec3 normal, float d, _, _, _, _) = tracer.Triangle(i);
            AssertSameBits(exact, normal);
            Assert.Equal(BitConverter.SingleToInt32Bits(Vec3.Dot(exact, scene[i].V0)), BitConverter.SingleToInt32Bits(d));
        }
    }

    /// <summary>
    /// The same rays through the same scene reach different distances under
    /// the two sides: the quirk is observable in what the tracer returns, not
    /// only in its intermediate values. Every hit is still the same triangle
    /// or a near miss, since the two differ by a few ulps.
    /// </summary>
    [Fact]
    public void ThePoliciesTraceTheSameRaysToDifferentDistances()
    {
        TracedTriangle[] scene = Scene();
        KdRayTracer stock = KdRayTracer.Build(scene, EstimateOnly);
        KdRayTracer correct = KdRayTracer.Build(scene, ComplianceOptions.Correct);

        Random random = new(71);
        Ray[] rays = new Ray[4096];
        for (int i = 0; i < rays.Length; i++)
        {
            Vec3 origin = new(Next(random, 1200), Next(random, 1200), Next(random, 1200));
            TracedTriangle aim = scene[random.Next(scene.Length)];
            Vec3 target = (aim.V0 + aim.V1 + aim.V2) * (1f / 3f);
            Vec3 d = target - origin;
            rays[i] = new Ray(origin.X, origin.Y, origin.Z, d.X, d.Y, d.Z, 2f);
        }

        HitId[] stockHits = new HitId[rays.Length];
        HitId[] correctHits = new HitId[rays.Length];
        stock.TraceClosest(rays, stockHits, RayTraceOptions.StockExact);
        correct.TraceClosest(rays, correctHits, RayTraceOptions.StockExact);

        int hits = correctHits.Count(h => h.Surface != HitId.Miss);
        int moved = 0;
        for (int i = 0; i < rays.Length; i++)
        {
            if (stockHits[i].Surface == correctHits[i].Surface
                && BitConverter.SingleToInt32Bits(stockHits[i].Fraction) != BitConverter.SingleToInt32Bits(correctHits[i].Fraction))
            {
                moved++;
            }
        }

        Assert.True(hits > rays.Length / 2, $"only {hits} of {rays.Length} rays hit; the scene is not exercising the planes");
        Assert.True(moved > 0, "no hit distance moved between the two sides");
    }

    /// <summary>
    /// The Correct tracer takes no estimate, so its identity does not name
    /// an estimate family, and the two sides are two identities.
    /// </summary>
    [Fact]
    public void TheIdentityNamesTheSideAndTheEstimateFamily()
    {
        TracedTriangle[] scene = Scene();
        Assert.Equal("cpu-kd-exact-1", KdRayTracer.Build(scene, ComplianceOptions.Correct).TracerIdentity);
        Assert.Equal("cpu-kd-sse4-1-rcp-" + FloatEstimate.Family, KdRayTracer.Build(scene, EstimateOnly).TracerIdentity);
        Assert.Equal(
            "cpu-kd-sse4-1-rcp-" + FloatEstimate.Family + "-stock",
            KdRayTracer.Build(scene, ComplianceOptions.Stock).TracerIdentity);
    }

    /// <summary>The parallel build takes the same decision as the serial one.</summary>
    [Fact]
    public async Task TheParallelBuildMakesTheSamePlanesOnEachSide()
    {
        TracedTriangle[] scene = Scene();
        using SourceSharp.MapTools.Parallel.WorkQueue queue = new(new SourceSharp.MapTools.Parallel.CompileParallelism { MaxDegree = 4 });
        foreach (ComplianceOptions compliance in new[] { ComplianceOptions.Correct, EstimateOnly })
        {
            KdRayTracer serial = KdRayTracer.Build(scene, compliance);
            KdRayTracer parallel = await KdRayTracer.BuildAsync(scene, compliance, queue);
            Assert.Equal(serial.StockReciprocalEstimate, parallel.StockReciprocalEstimate);
            for (int i = 0; i < scene.Length; i++)
            {
                AssertSameBits(serial.Triangle(i).Normal, parallel.Triangle(i).Normal);
            }
        }
    }

    private static bool SameBits(Vec3 a, Vec3 b) =>
        BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X)
        && BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y)
        && BitConverter.SingleToInt32Bits(a.Z) == BitConverter.SingleToInt32Bits(b.Z);

    private static void AssertSameBits(Vec3 expected, Vec3 actual) =>
        Assert.True(SameBits(expected, actual), $"expected {expected}, got {actual}");
}
