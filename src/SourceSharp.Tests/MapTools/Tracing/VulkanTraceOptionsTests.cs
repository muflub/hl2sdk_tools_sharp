//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Gpu;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// Which <see cref="RayTraceOptions"/> the Vulkan tracer answers, and the
/// host-side fold that turns its closest hits into sky-passing visibility.
/// Device-free: these are the rules the hybrid's fallback relies on, and they
/// must hold on a machine with no capable GPU.
/// </summary>
public sealed class VulkanTraceOptionsTests
{
    [Fact]
    public void ASkippedIdIsNotSupported()
    {
        Assert.False(VulkanRayTracer.SupportsOptions(RayTraceOptions.TestLine(TraceId.StaticProp | 4)));
        Assert.False(VulkanRayTracer.SupportsOptions(RayTraceOptions.TestLine(0, skyDoesNotBlock: true)));
        Assert.Throws<NotSupportedException>(() => VulkanRayTracer.RequireSupported(RayTraceOptions.TestLine(7)));
    }

    [Fact]
    public void PlainIsolatedAndSkyPassingBatchesAreSupported()
    {
        Assert.True(VulkanRayTracer.SupportsOptions(RayTraceOptions.StockExact));
        Assert.True(VulkanRayTracer.SupportsOptions(RayTraceOptions.SelfIntersectionSafe));
        Assert.True(VulkanRayTracer.SupportsOptions(RayTraceOptions.TestLine()));
        Assert.True(VulkanRayTracer.SupportsOptions(RayTraceOptions.TestLine(skyDoesNotBlock: true)));
        VulkanRayTracer.RequireSupported(RayTraceOptions.TestLine(skyDoesNotBlock: true));
    }

    [Fact]
    public void TheSkyFoldBlocksOnlyOnANearNonSkyFirstHit()
    {
        HitId[] hits =
        [
            HitId.Missed,
            new HitId(TraceId.Opaque, 0.5f),
            new HitId(TraceId.Sky, 0.5f),
            new HitId(TraceId.StaticProp | 3, 0.99f),
            new HitId(TraceId.Opaque, 1.0f),
            new HitId(TraceId.Opaque, 1.5f),
        ];
        ulong[] bits = [ulong.MaxValue];

        VulkanRayTracer.FoldSkyPassing(hits, bits);

        Assert.Equal(0b001010UL, bits[0]);
    }

    [Fact]
    public void TheSkyFoldOverwritesEveryWordItSpans()
    {
        HitId[] hits = new HitId[130];
        Array.Fill(hits, HitId.Missed);
        hits[129] = new HitId(TraceId.Opaque, 0.25f);
        ulong[] bits = [ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue];

        VulkanRayTracer.FoldSkyPassing(hits, bits);

        Assert.Equal([0UL, 0UL, 0b10UL, ulong.MaxValue], bits);
    }

    // A floor, a sky ceiling, and rays well inside both faces, so no answer
    // grazes an edge and the device must agree with the KD tracer exactly.
    private static readonly TracedTriangle[] SkyScene =
    [
        new(TraceId.Opaque, new Vec3(-1000, -1000, 0), new Vec3(1000, -1000, 0), new Vec3(0, 1000, 0), 0),
        new(TraceId.Sky, new Vec3(-1000, -1000, 100), new Vec3(1000, -1000, 100), new Vec3(0, 1000, 100), 0),
    ];

    [DeviceFact]
    public void OnADeviceSkyPassingVisibilityMatchesTheKdTracer()
    {
        using VulkanRayTracer gpu = VulkanRayTracer.TryCreate(SkyScene).Tracer!;
        KdRayTracer cpu = KdRayTracer.Build(SkyScene);
        Ray[] rays =
        [
            Ray.Segment(new(0, 0, 50), new(10, 0, 200), false),   // reaches the sky: passes
            Ray.Segment(new(0, 0, 50), new(10, 0, -50), false),   // floor: blocked
            Ray.Segment(new(0, 0, 50), new(10, 0, 60), false),    // hits nothing
            Ray.Segment(new(0, 0, -50), new(10, 0, 200), false),  // floor first: blocked
        ];
        RayTraceOptions options = RayTraceOptions.TestLine(skyDoesNotBlock: true);
        ulong[] expected = new ulong[1];
        ulong[] actual = new ulong[1];

        cpu.TraceVisibility(rays, expected, options);
        gpu.TraceVisibilityAsync(rays, actual, options).AsTask().GetAwaiter().GetResult();

        Assert.Equal(0b1010UL, expected[0]);
        Assert.Equal(expected, actual);
    }

    [DeviceFact]
    public void OnADeviceASkippedIdOrAClosestSkyPassIsRefused()
    {
        using VulkanRayTracer gpu = VulkanRayTracer.TryCreate(SkyScene).Tracer!;
        Ray[] rays = [Ray.Segment(new(0, 0, 50), new(0, 0, -50), false)];

        Assert.False(gpu.Supports(RayTraceOptions.TestLine(TraceId.Opaque)));
        Assert.Throws<NotSupportedException>(() =>
            gpu.TraceVisibilityAsync(rays, new ulong[1], RayTraceOptions.TestLine(TraceId.Opaque)));
        Assert.Throws<ArgumentException>(() =>
            gpu.TraceClosestAsync(rays, new HitId[1], RayTraceOptions.TestLine(skyDoesNotBlock: true)));
    }

    /// <summary>
    /// Runs only where a device passes the tracer's own self-test; a machine
    /// whose only ray-query device the gate rejects (llvmpipe) skips.
    /// </summary>
    private sealed class DeviceFactAttribute : FactAttribute
    {
        private static readonly Lazy<string?> Reason = new(() =>
        {
            try
            {
                VulkanTracerAttempt attempt = VulkanRayTracer.TryCreate(SkyScene);
                attempt.Tracer?.Dispose();
                return attempt.Success
                    ? null
                    : "no Vulkan device passed the self-test: "
                        + (attempt.Report.Selected?.Reason ?? attempt.Report.Failure);
            }
            catch (Exception e)
            {
                return $"the Vulkan probe threw {e.GetType().Name}: {e.Message}";
            }
        });

        public DeviceFactAttribute()
        {
            if (Reason.Value is { } why)
            {
                Skip = "skipped: " + why;
            }
        }
    }
}
