//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapTools.Tracing;
using SourceSharp.Tests.MapFormats;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// The tracer under test, and stock's recorded answers, built once for the
/// class.
/// </summary>
/// <remarks>
/// Flattening <c>dm_lockdown</c>'s tree takes long enough to be worth doing
/// once, and every fact here is read-only over it.
/// </remarks>
public sealed class BspParityFixture
{
    /// <summary>Loads the map, flattens it, and reads stock's answers.</summary>
    public BspParityFixture()
    {
        using FileStream stream = File.OpenRead(GoldenBsp.Lockdown());
        Bsp = BspFile.LoadAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
        Geometry = BspTraceGeometry.Build(Bsp);
        Tracer = new BspSurfaceTracer(Geometry);
        Set = StockRaySet.Load();

        Ours = new HitId[Set.Count];
        Tracer.TraceClosest(Set.Rays, Ours, RayTraceOptions.StockExact);
    }

    /// <summary>The loaded map.</summary>
    public BspData Bsp { get; }

    /// <summary>The flattened tree.</summary>
    public BspTraceGeometry Geometry { get; }

    /// <summary>The tracer.</summary>
    public BspSurfaceTracer Tracer { get; }

    /// <summary>The rays and stock's answers.</summary>
    internal StockRaySet Set { get; }

    /// <summary>This port's answers for the same rays.</summary>
    public HitId[] Ours { get; }
}

/// <summary>
/// The 4a2 correctness gate: identical hit face and distance against stock, on
/// a recorded ray set.
/// </summary>
/// <remarks>
/// <para>
/// The rays are leaf ambient's own shape -- a random point inside a real leaf
/// of <c>dm_lockdown</c> and the fixed 162-direction <c>g_anorms</c> fan out to
/// <c>COORD_EXTENT * 1.74</c>, which is exactly what
/// <c>ComputeAmbientFromSphericalSamples</c> casts.
/// </para>
/// <para>
/// NOT A LATTICE, on purpose. A prior lane in this project measured 20,319
/// differing bits in 5 M rays on axis-aligned lattice geometry, falling to 4
/// once the geometry was jittered by 0.35 units: on a lattice a tracer
/// measures its own epsilons rather than its traversal, and a parity fact over
/// one would be green for the wrong reason.
/// </para>
/// </remarks>
[Collection("bsp-parity")]
public sealed class BspSurfaceStockParityTests : IClassFixture<BspParityFixture>
{
    private readonly BspParityFixture _fixture;

    /// <summary>Takes the shared tracer and answers.</summary>
    /// <param name="fixture">The shared fixture.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fixture"/> is null.</exception>
    public BspSurfaceStockParityTests(BspParityFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _fixture = fixture;
    }

    /// <summary>The recorded set is the size the fixture README says.</summary>
    [Fact]
    public void RecordedSetIsFiftySamplesOfOneHundredAndSixtyTwoRays()
    {
        Assert.Equal(50 * 162, _fixture.Set.Count);
    }

    /// <summary>
    /// The set is not all misses, which a parity fact over an empty answer
    /// would pass trivially.
    /// </summary>
    [Fact]
    public void StockHitsSomethingOnMostRays()
    {
        int hits = 0;
        for (int i = 0; i < _fixture.Set.Count; i++)
        {
            if (_fixture.Set.StockSurface[i] >= 0)
            {
                hits++;
            }
        }

        Assert.True(
            hits > _fixture.Set.Count / 2,
            $"stock hit on {hits} of {_fixture.Set.Count} rays, which is too few for this to be "
            + "a meaningful comparison");
    }

    /// <summary>
    /// The set reaches the sky, so the node path and its winding test are
    /// exercised and not merely compiled.
    /// </summary>
    [Fact]
    public void StockReachesSkyOnSomeRays()
    {
        int sky = 0;
        for (int i = 0; i < _fixture.Set.Count; i++)
        {
            if (_fixture.Set.StockSurface[i] >= 0 && !_fixture.Set.StockHasLuxel[i])
            {
                sky++;
            }
        }

        Assert.True(sky > 0, "no ray in the recorded set ended on a sky surface");
    }

    /// <summary>Every ray agrees with stock on WHICH face it hit.</summary>
    [Fact]
    public void EveryRayHitsTheSameFaceAsStock()
    {
        int differing = 0;
        int firstBad = -1;
        for (int i = 0; i < _fixture.Set.Count; i++)
        {
            if (_fixture.Ours[i].Surface != _fixture.Set.StockSurface[i])
            {
                differing++;
                if (firstBad < 0)
                {
                    firstBad = i;
                }
            }
        }

        Assert.True(
            differing == 0,
            differing == 0
                ? string.Empty
                : $"{differing} of {_fixture.Set.Count} rays hit a different face; the first is "
                  + $"ray {firstBad}, where stock says {_fixture.Set.StockSurface[firstBad]} and "
                  + $"this port says {_fixture.Ours[firstBad].Surface}");
    }

    /// <summary>Every ray agrees with stock on the hit distance, bit for bit.</summary>
    /// <remarks>
    /// BIT for bit, not within an epsilon, and that is a claim worth making
    /// rather than softening: every operation on the path is reproduced in
    /// stock's own order and operand order, so there is no rounding left to
    /// differ. An epsilon here would hide the day one of them stops matching.
    /// </remarks>
    [Fact]
    public void EveryRayReportsTheSameFractionAsStock()
    {
        int differing = 0;
        int firstBad = -1;
        for (int i = 0; i < _fixture.Set.Count; i++)
        {
            if (BitConverter.SingleToInt32Bits(_fixture.Ours[i].Fraction)
                != BitConverter.SingleToInt32Bits(_fixture.Set.StockFraction[i]))
            {
                differing++;
                if (firstBad < 0)
                {
                    firstBad = i;
                }
            }
        }

        Assert.True(
            differing == 0,
            differing == 0
                ? string.Empty
                : $"{differing} of {_fixture.Set.Count} fractions differ from stock's; the first "
                  + $"is ray {firstBad}, where stock says {_fixture.Set.StockFraction[firstBad]:R} "
                  + $"and this port says {_fixture.Ours[firstBad].Fraction:R}");
    }

    /// <summary>
    /// Stock's <c>m_bHasLuxel</c> is recoverable from what
    /// <see cref="HitId"/> carries, which is the claim that lets it stay eight
    /// bytes.
    /// </summary>
    /// <remarks>
    /// This is the fact that would fail if the argument in
    /// <see cref="HitId"/>'s remarks were wrong. Dropping a field because it is
    /// derivable is only safe if it IS derivable, on every ray, including the
    /// ones that end on nothing.
    /// </remarks>
    [Fact]
    public void HasLuxelIsRecoverableFromTheSurfaceAlone()
    {
        int differing = 0;
        for (int i = 0; i < _fixture.Set.Count; i++)
        {
            int surface = _fixture.Ours[i].Surface;
            bool derived = surface >= 0 && !BspTraceGeometry.IsSkyFace(_fixture.Bsp, surface);
            if (derived != _fixture.Set.StockHasLuxel[i])
            {
                differing++;
            }
        }

        Assert.Equal(0, differing);
    }

    /// <summary>
    /// The visibility bits agree with the closest-hit answers on the same
    /// rays.
    /// </summary>
    /// <remarks>
    /// Two operations on one seam that could disagree about whether a ray hit
    /// anything would be worse than one that was merely slow.
    /// </remarks>
    [Fact]
    public void VisibilityBitsAgreeWithStocksHits()
    {
        ulong[] bits = new ulong[(_fixture.Set.Count + 63) / 64];
        _fixture.Tracer.TraceVisibility(_fixture.Set.Rays, bits, RayTraceOptions.StockExact);

        int differing = 0;
        for (int i = 0; i < _fixture.Set.Count; i++)
        {
            bool bit = (bits[i >> 6] & (1UL << (i & 63))) != 0;
            if (bit != _fixture.Set.StockSurface[i] >= 0)
            {
                differing++;
            }
        }

        Assert.Equal(0, differing);
    }
}
