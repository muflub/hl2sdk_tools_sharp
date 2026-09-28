//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.MapTools.Tracing;
using SourceSharp.Tests.MapTools.Rad.Ambient;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// <see cref="StockQuirk.SkyWindingNormalise"/>: the walk's sky windings and
/// its point-in-winding test, on each side of the quirk.
/// </summary>
/// <remarks>
/// <para>
/// The colinear-point facts use edges short enough that stock's
/// <c>1e-10</c> length bias, not the estimate's last bits, separates the two
/// sides, so they show the difference on every CPU: three colinear points
/// 1e-5 apart normalise to vectors about 0.71 long under stock, whose dot is
/// about 0.5 and keeps the middle point, and to unit vectors under Correct,
/// whose dot is 1 and removes it.
/// </para>
/// <para>
/// The sky-test facts pin which normalise each side takes, bit for bit, over
/// a sample of crosses, and that the geometry carries the decision to both
/// walks. A near-edge point whose answer flips with the estimate's last bits
/// cannot be written down once for every CPU, which is the defect itself.
/// </para>
/// </remarks>
public sealed class SkyWindingNormaliseTests : IClassFixture<AmbientFixture>
{
    private readonly AmbientFixture _fixture;

    /// <summary>Takes the shared fixture, whose map has sky faces.</summary>
    /// <param name="fixture">The leaf-ambient map.</param>
    public SkyWindingNormaliseTests(AmbientFixture fixture) => _fixture = fixture;

    /// <summary>A winding whose second point sits between two colinear edges 1e-5 long.</summary>
    private static List<Vec3> ShortColinearWinding() =>
    [
        new Vec3(0, 0, 0),
        new Vec3(1e-5f, 0, 0),
        new Vec3(2e-5f, 0, 0),
        new Vec3(2e-5f, 1, 0),
    ];

    /// <summary>
    /// Under stock the biased estimate shortens the two short edges, their dot
    /// falls under 0.999, and the colinear middle point is kept.
    /// </summary>
    [Fact]
    public void StockKeepsAColinearPointBetweenShortEdges()
    {
        List<Vec3> source = ShortColinearWinding();
        List<Vec3> sink = [];
        BspTraceGeometry.RemoveColinearPoints(source, sink, stockNormalise: true);
        Assert.Equal(source, sink);
    }

    /// <summary>
    /// Under Correct the two short edges normalise to the same unit vector,
    /// their dot is 1, and the colinear middle point is removed.
    /// </summary>
    [Fact]
    public void CorrectRemovesAColinearPointBetweenShortEdges()
    {
        List<Vec3> source = ShortColinearWinding();
        List<Vec3> sink = [];
        BspTraceGeometry.RemoveColinearPoints(source, sink, stockNormalise: false);
        Assert.Equal([source[0], source[2], source[3]], sink);
    }

    /// <summary>A deterministic sample of edge crosses, long and short.</summary>
    private static Vec3[] Crosses()
    {
        Random random = new(19);
        Vec3[] crosses = new Vec3[2048];
        for (int i = 0; i < crosses.Length; i++)
        {
            float scale = (float)Math.Pow(10, (random.NextDouble() * 12) - 6);
            crosses[i] = new Vec3(
                (float)((random.NextDouble() * 2) - 1) * scale,
                (float)((random.NextDouble() * 2) - 1) * scale,
                (float)((random.NextDouble() * 2) - 1) * scale);
        }

        return crosses;
    }

    /// <summary>
    /// A geometry built under stock tests the sky with stock's normalise, and
    /// that is the estimate, not the divide.
    /// </summary>
    [Fact]
    public void StockSkyTestNormalisesWithTheEstimate()
    {
        BspTraceGeometry geometry = BspTraceGeometry.Build(_fixture.Bsp, ComplianceOptions.Stock);
        Assert.True(geometry.StockSkyNormalise);
        Assert.True(new AmbientRayTracer(geometry, DispCollisionSet.Build(_fixture.Bsp)).Geometry.StockSkyNormalise);
        Assert.StartsWith("cpu-bsp-surface-1-stock-", new BspSurfaceTracer(geometry).TracerIdentity, StringComparison.Ordinal);

        int differs = 0;
        foreach (Vec3 cross in Crosses())
        {
            (Vec3 actual, _) = BspTraceGeometry.Normalise(cross, stock: true);
            Assert.Equal(cross.NormaliseLikeStock().Normalised, actual);
            if (actual != cross.Normalise().Normalised)
            {
                differs++;
            }
        }

        Assert.True(differs > 0, "the estimate matched the divide on every sample");
    }

    /// <summary>
    /// A geometry built under Correct, the default, tests the sky with the
    /// divide's normalise, bit for bit.
    /// </summary>
    [Fact]
    public void CorrectSkyTestNormalisesWithADivide()
    {
        BspTraceGeometry geometry = BspTraceGeometry.Build(_fixture.Bsp);
        Assert.False(geometry.StockSkyNormalise);
        Assert.False(_fixture.CorrectLdr.Tracer.Geometry.StockSkyNormalise);
        Assert.Equal("cpu-bsp-surface-2", new BspSurfaceTracer(geometry).TracerIdentity);

        foreach (Vec3 cross in Crosses())
        {
            (Vec3 actual, float length) = BspTraceGeometry.Normalise(cross, stock: false);
            (Vec3 exact, float exactLength) = cross.Normalise();
            Assert.Equal(BitConverter.SingleToInt32Bits(exact.X), BitConverter.SingleToInt32Bits(actual.X));
            Assert.Equal(BitConverter.SingleToInt32Bits(exact.Y), BitConverter.SingleToInt32Bits(actual.Y));
            Assert.Equal(BitConverter.SingleToInt32Bits(exact.Z), BitConverter.SingleToInt32Bits(actual.Z));
            Assert.Equal(exactLength, length);
        }
    }

    /// <summary>
    /// Both sides still find the fixture's sky: a ray straight up through the
    /// sky half reports a sky face either way, so the quirk moves only the
    /// margins.
    /// </summary>
    [Fact]
    public void BothSidesSeeTheSkyAboveTheSkyHalf()
    {
        foreach (AmbientScene scene in new[] { _fixture.Ldr, _fixture.CorrectLdr })
        {
            AmbientHit hit = scene.Tracer.Trace(
                new Vec3(300, 300, 200), new Vec3(0, 0, 400), scene.Tracer.Displacements.CreateScratch());
            Assert.True(hit.IsHit);
            Assert.NotEqual(0, scene.TexInfo[scene.Faces[hit.Surface].TexInfo].Flags & RayAmbientLighting.SurfSky);
        }
    }
}
