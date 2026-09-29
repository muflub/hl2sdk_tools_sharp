//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using Silk.NET.Vulkan;

using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Gpu;
using SourceSharp.MapTools.Gpu.Interop;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// "Use the faster one by default": which device an attempt opens, and
/// when an unpinned attempt keeps the CPU tracer instead of a device that
/// works but would be slower (a CPU implementation of Vulkan, or a staged
/// ray upload below the floor). A pinned device is always used.
/// </summary>
/// <remarks>
/// The rules are pure functions and are tested with made-up devices and
/// measurements, so they run everywhere. The device facts need a ray-query
/// device; in a container that is llvmpipe, which is exactly the CPU case.
/// </remarks>
public sealed class VulkanDevicePolicyFacts(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly VulkanRayTracerOptions Auto = new() { DeclineSlowDevicesUnlessPinned = true };

    // ------------------------------------------------------------------
    // Device choice
    // ------------------------------------------------------------------

    /// <summary>The ranking: discrete, integrated, virtual, CPU, other.</summary>
    [Fact]
    public void DeviceTypesRankFromDiscreteToOther()
    {
        Assert.True(VulkanDevice.DeviceScore(PhysicalDeviceType.DiscreteGpu) > VulkanDevice.DeviceScore(PhysicalDeviceType.IntegratedGpu));
        Assert.True(VulkanDevice.DeviceScore(PhysicalDeviceType.IntegratedGpu) > VulkanDevice.DeviceScore(PhysicalDeviceType.VirtualGpu));
        Assert.True(VulkanDevice.DeviceScore(PhysicalDeviceType.VirtualGpu) > VulkanDevice.DeviceScore(PhysicalDeviceType.Cpu));
        Assert.True(VulkanDevice.DeviceScore(PhysicalDeviceType.Cpu) > VulkanDevice.DeviceScore(PhysicalDeviceType.Other));
    }

    /// <summary>With a real GPU present, an unpinned choice takes it over llvmpipe, whatever the order.</summary>
    [Fact]
    public void AGpuWinsOverACpuImplementationWhenUnpinned()
    {
        DeviceCandidate[] withDiscrete =
        [
            new("llvmpipe (LLVM 20.1.2, 256 bits)", PhysicalDeviceType.Cpu, true),
            new("AMD Radeon RX 9070 XT (RADV GFX1201)", PhysicalDeviceType.DiscreteGpu, true),
            new("NVIDIA GeForce RTX 2070 SUPER", PhysicalDeviceType.DiscreteGpu, true),
        ];
        DeviceCandidate[] withIntegrated =
        [
            new("AMD Radeon Graphics (RADV RAPHAEL_MENDOCINO)", PhysicalDeviceType.IntegratedGpu, true),
            new("llvmpipe (LLVM 20.1.2, 256 bits)", PhysicalDeviceType.Cpu, true),
        ];

        Assert.Equal(1, VulkanDevice.ChooseDevice(withDiscrete, null, -1));
        Assert.Equal(0, VulkanDevice.ChooseDevice(withIntegrated, null, -1));
    }

    /// <summary>A name pin reaches the device it names, a CPU one included, case-insensitively.</summary>
    [Fact]
    public void ANamePinReachesTheDeviceItNames()
    {
        DeviceCandidate[] devices =
        [
            new("llvmpipe (LLVM 20.1.2, 256 bits)", PhysicalDeviceType.Cpu, true),
            new("AMD Radeon RX 9070 XT (RADV GFX1201)", PhysicalDeviceType.DiscreteGpu, true),
            new("NVIDIA GeForce RTX 2070 SUPER", PhysicalDeviceType.DiscreteGpu, true),
        ];

        Assert.Equal(0, VulkanDevice.ChooseDevice(devices, "LLVMPIPE", -1));
        Assert.Equal(2, VulkanDevice.ChooseDevice(devices, "nvidia", -1));
        Assert.Equal(-1, VulkanDevice.ChooseDevice(devices, "intel", -1));
    }

    /// <summary>
    /// Devices without ray query or acceleration structures are never
    /// chosen, and an index pin skips that many traceable devices and ranks
    /// the rest.
    /// </summary>
    [Fact]
    public void OnlyTraceableDevicesAreChosenAndAnIndexSkipsThatMany()
    {
        DeviceCandidate[] devices =
        [
            new("old GPU", PhysicalDeviceType.DiscreteGpu, false),
            new("llvmpipe", PhysicalDeviceType.Cpu, true),
            new("new GPU", PhysicalDeviceType.DiscreteGpu, true),
        ];

        Assert.Equal(2, VulkanDevice.ChooseDevice(devices, null, -1));
        Assert.Equal(-1, VulkanDevice.ChooseDevice(devices, "old", -1));
        // An index skips that many traceable devices and ranks the rest, so
        // index 0 (a defaulted options value) still means the best device.
        Assert.Equal(2, VulkanDevice.ChooseDevice(devices, null, 0));
        Assert.Equal(2, VulkanDevice.ChooseDevice(devices, null, 1));
        Assert.Equal(-1, VulkanDevice.ChooseDevice(devices, null, 2));
        DeviceCandidate[] cpuLast = [devices[2], devices[1]];
        Assert.Equal(1, VulkanDevice.ChooseDevice(cpuLast, null, 1));
        // The index wins over a name.
        Assert.Equal(2, VulkanDevice.ChooseDevice(devices, "llvmpipe", 1));
    }

    // ------------------------------------------------------------------
    // The decline rule
    // ------------------------------------------------------------------

    /// <summary>Without the policy, every device that passes is used, however slow.</summary>
    [Fact]
    public void WithoutThePolicyNothingIsDeclined()
    {
        Assert.Null(VulkanRayTracer.SlowDeviceReason(default, "llvmpipe", true, 1e6));
    }

    /// <summary>Unpinned, a CPU implementation is declined with a reason that says how to use it anyway.</summary>
    [Fact]
    public void AnUnpinnedCpuDeviceIsDeclined()
    {
        Assert.Equal(
            "llvmpipe (LLVM 20.1.2, 256 bits) is a CPU implementation of Vulkan; the built-in CPU tracer is "
            + "faster, so it is used instead. Pin the device by name to trace on it anyway",
            VulkanRayTracer.SlowDeviceReason(Auto, "llvmpipe (LLVM 20.1.2, 256 bits)", true, null));
    }

    /// <summary>Unpinned, an upload below the floor is declined with the measured rate in the reason.</summary>
    [Fact]
    public void AnUnpinnedSlowUploadIsDeclined()
    {
        Assert.Equal(
            "rays upload to NVIDIA GeForce RTX 2070 SUPER at 0.67 GB/s, below the 2.50 GB/s at which tracing on "
            + "it could beat the CPU; the built-in CPU tracer is faster; pin the device by name to trace on it anyway",
            VulkanRayTracer.SlowDeviceReason(Auto, "NVIDIA GeForce RTX 2070 SUPER", false, 0.67e9));
    }

    /// <summary>At or above the floor, or with no upload to measure, the device is used.</summary>
    /// <param name="rate">The measured rate, or null for a device that reads rays in place.</param>
    [Theory]
    [InlineData(2.5e9)]
    [InlineData(12.1e9)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(null)]
    public void AFastOrDirectDeviceIsUsed(double? rate)
    {
        Assert.Null(VulkanRayTracer.SlowDeviceReason(Auto, "AMD Radeon RX 9070 XT", false, rate));
    }

    /// <summary>A pinned device is used, CPU or slow, whether pinned by name or by index.</summary>
    [Fact]
    public void APinnedDeviceIsAlwaysUsed()
    {
        VulkanRayTracerOptions byName = Auto with { DeviceMatch = "llvmpipe" };
        VulkanRayTracerOptions byIndex = Auto with { DeviceIndex = 1 };

        Assert.True(byName.IsPinned);
        Assert.True(byIndex.IsPinned);
        Assert.False(Auto.IsPinned);
        Assert.False((Auto with { DeviceIndex = -1 }).IsPinned);
        Assert.False((Auto with { DeviceMatch = "" }).IsPinned);
        Assert.Null(VulkanRayTracer.SlowDeviceReason(byName, "llvmpipe", true, 0.1e9));
        Assert.Null(VulkanRayTracer.SlowDeviceReason(byIndex, "NVIDIA", false, 0.1e9));
    }

    /// <summary>The floor defaults to 2.5 GB/s and can be moved.</summary>
    [Fact]
    public void TheFloorDefaultsAndCanBeMoved()
    {
        VulkanRayTracerOptions lower = Auto with { MinUploadBytesPerSecond = 0.5e9 };

        Assert.Equal(2.5e9, Auto.UploadFloor);
        Assert.Equal(0.5e9, lower.UploadFloor);
        Assert.Null(VulkanRayTracer.SlowDeviceReason(lower, "NVIDIA", false, 0.67e9));
        Assert.Contains("below the 0.50 GB/s", VulkanRayTracer.SlowDeviceReason(lower, "NVIDIA", false, 0.4e9));
    }

    /// <summary>The rate is bytes over seconds; a copy too fast for the clock counts as infinitely fast.</summary>
    [Fact]
    public void UploadRateIsBytesOverSeconds()
    {
        Assert.Equal(0.5e9, VulkanDevice.UploadRate(64UL << 20, (64 << 20) / 0.5e9), 1.0);
        Assert.Equal(double.PositiveInfinity, VulkanDevice.UploadRate(1, 0));
    }

    // ------------------------------------------------------------------
    // The host's -gpu value
    // ------------------------------------------------------------------

    /// <summary><c>-gpu auto</c> (any case) and an empty value pin nothing; anything else is a name pin.</summary>
    [Fact]
    public void GpuAutoPinsNothing()
    {
        Assert.Null(HostBackends.GpuDevicePin("auto"));
        Assert.Null(HostBackends.GpuDevicePin("AUTO"));
        Assert.Null(HostBackends.GpuDevicePin(""));
        Assert.Null(HostBackends.GpuDevicePin(null));
        Assert.Equal("nvidia", HostBackends.GpuDevicePin("nvidia"));
        Assert.Equal("llvmpipe", HostBackends.GpuDevicePin("llvmpipe"));
    }

    // ------------------------------------------------------------------
    // On a device
    // ------------------------------------------------------------------

    /// <summary>
    /// A staged device's probe measures a rate and allocates nothing; a
    /// device that reads rays in place is not probed.
    /// </summary>
    /// <param name="forceStaged">Keep the upload copy even where rays could be read in place.</param>
    [VulkanStageTheory(VulkanNeed.RayQueryDevice)]
    [InlineData(true)]
    [InlineData(false)]
    public void TheProbeMeasuresOnlyAStagedUploadAndAllocatesNothing(bool forceStaged)
    {
        // The default budget, so a staged probe moves what it would in a
        // real run (a third of 128 MB of rays, under the 64 MiB cap).
        VulkanDevice device = new();
        device.Construct(null, -1, 4_194_304, 3, forceStaged);
        long before = device.LiveBytes;

        double? rate = device.MeasureUploadRate();

        Assert.Equal(before, device.LiveBytes);
        if (device.SlabLayout.DirectRays)
        {
            Assert.Null(rate);
        }
        else
        {
            Assert.NotNull(rate);
            Assert.True(rate > 0, $"measured {rate} B/s");
            output.WriteLine($"{device.DeviceName}: staged ray upload {rate / 1e9:F2} GB/s over "
                + $"{Math.Min(VulkanDevice.UploadProbeBytes, (ulong)device.MaxSlabRays * 32UL)} bytes");
        }

        device.Dispose();
        Assert.Equal(0, device.LiveBytes);
    }

    /// <summary>
    /// A probe that fails part way propagates its failure and leaves the
    /// device usable and fully releasable: it borrowed slot 0 and owns
    /// nothing.
    /// </summary>
    [VulkanStageFact(VulkanNeed.PassingDevice)]
    public void AFailedProbeLeavesTheDeviceUsable()
    {
        VulkanDevice device = new();
        device.Construct(null, -1, 4096, 1, forceStaged: true);
        InvalidDataException planted = new("planted failure");
        device.Observe = step =>
        {
            if (step == VulkanStep.UploadProbeRecorded)
            {
                throw planted;
            }
        };

        Exception thrown = Assert.ThrowsAny<Exception>(() => device.MeasureUploadRate());
        device.Observe = null;
        (bool passed, SelfTestOutcome outcome) = device.RunSelfTest();
        device.Dispose();

        Assert.Same(planted, thrown);
        Assert.True(passed, VulkanDevice.SelfTestDetail(device.Identity, outcome));
        Assert.Equal(0, device.LiveBytes);
    }

    /// <summary>
    /// End to end on a machine whose only device is a CPU implementation:
    /// the unpinned policy declines it before the self-test and releases it;
    /// a name pin uses it.
    /// </summary>
    [VulkanStageFact(VulkanNeed.OnlyCpuDevice)]
    public void OnlyACpuDeviceIsDeclinedUnpinnedAndUsedPinned()
    {
        VulkanDeviceInfo[] rq = [.. VulkanRayTracer.ProbeDevices().Where(r => r.RayQuery)];

        List<TryCreateStage> seen = [];
        VulkanTracerAttempt unpinned = VulkanRayTracer.TryCreate(
            VulkanRayTracerReleaseFacts.TwoTriangles(), seen.Add, Auto, CancellationToken.None);
        Assert.False(unpinned.Success);
        Assert.Null(unpinned.Report.Selected);
        Assert.Equal(
            $"{rq[0].Name} is a CPU implementation of Vulkan; the built-in CPU tracer is faster, so it is used "
            + "instead. Pin the device by name to trace on it anyway",
            unpinned.Report.Failure);
        Assert.Equal([TryCreateStage.Opened, TryCreateStage.Constructed, TryCreateStage.Released], seen);

        VulkanTracerAttempt pinned = VulkanRayTracer.TryCreate(
            VulkanRayTracerReleaseFacts.TwoTriangles(), Auto with { DeviceMatch = rq[0].Name });
        using VulkanRayTracer? tracer = pinned.Tracer;
        Assert.True(pinned.Success, pinned.Report.Selected?.Reason ?? pinned.Report.Failure);
    }

    /// <summary>
    /// The host's factory applies the policy: <c>-gpu auto</c> on a
    /// CPU-only machine declines with the CPU reason, and naming the
    /// device traces on it.
    /// </summary>
    [VulkanStageFact(VulkanNeed.OnlyCpuDevice)]
    public async Task TheHostDeclinesAutoOnACpuOnlyMachine()
    {

        TracedTriangle[] tris = VulkanRayTracerReleaseFacts.TwoTriangles();
        ShadowCasterSet casters = new(tris, new float[tris.Length], new int[tris.Length], []);

        GpuTracerOffer auto = await HostBackends.TryOfferGpuAsync(casters, HostBackends.GpuAnyDevice, null);
        GpuTracerOffer named = await HostBackends.TryOfferGpuAsync(casters, "llvmpipe", null);
        (named.Tracer as IDisposable)?.Dispose();

        Assert.Null(auto.Tracer);
        Assert.Contains("is a CPU implementation of Vulkan; the built-in CPU tracer is faster", auto.DeclineReason);
        Assert.NotNull(named.Tracer);
    }
}
